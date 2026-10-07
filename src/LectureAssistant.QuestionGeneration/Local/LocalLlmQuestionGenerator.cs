using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;
using LectureAssistant.Core;
using LectureAssistant.Core.Hardware;
using LectureAssistant.Core.Models;

namespace LectureAssistant.QuestionGeneration.Local;

/// <summary>
/// Generates questions with a GGUF model on the user's PC via LLamaSharp (Vulkan GPU when available, else CPU).
/// </summary>
/// <remarks>
/// The model is loaded at the start of each <see cref="GenerateAsync"/> call and released at the end. Generation is
/// occasional and the model can occupy several GB of (video) memory that transcription also wants, so
/// holding it between runs isn't worth it; loading typically takes seconds.
/// The transcript is split into sections that fit the context window; each is prompted with the model's own
/// chat template and sampled under a GBNF grammar (<see cref="QuestionGrammar"/>) so the output is always valid JSON.
/// </remarks>
public sealed class LocalLlmQuestionGenerator : IQuestionGenerator, IReportsModelRun
{
    /// <summary>Tokens reserved beyond the prompt and answer for template quirks and BOS/EOS.</summary>
    private const int SafetyMargin = 128;

    private static readonly object NativeConfigLock = new();
    private static bool _nativeConfigured;

    /// <summary>llama.cpp's log, recorded only while a model loads to see where its layers went.</summary>
    private static readonly NativeLogRecorder NativeLog = new();

    /// <summary>Vulkan devices ggml listed when it started; it only lists them once per process.</summary>
    private static IReadOnlyList<VulkanDevice> _vulkanDevices = [];

    private readonly Func<LocalLlmOptions> _options;

    /// <summary>
    /// Told where the model is running (graphics card or processor) as soon as it has loaded, and again if it
    /// has to move to the processor. Use a <see cref="Progress{T}"/> created on the UI thread.
    /// </summary>
    public IProgress<ComputeDeviceReport>? DeviceProgress { get; set; }

    /// <summary>Where the last successful <see cref="GenerateAsync"/> ran and how long it took (including loading).</summary>
    public ModelRun? LastRun { get; private set; }

    public LocalLlmQuestionGenerator(LocalLlmOptions options) : this(() => options) { }

    /// <summary>Reads options on every call, so settings changes take effect on the next run.</summary>
    public LocalLlmQuestionGenerator(Func<LocalLlmOptions> optionsProvider)
    {
        _options = optionsProvider;
    }

    public string DisplayName => "Local model (runs on this PC)";

    public Task<IReadOnlyList<Question>> GenerateAsync(
        QuestionGenerationRequest request,
        IProgress<QuestionGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        QuestionPrompt.EnsureUsable(request);
        var options = _options();
        // Inference is CPU/GPU-bound native work; keep it off the caller's (UI) thread.
        return Task.Run(() => GenerateWithCpuFallbackAsync(request, options, progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// If the graphics card can't hold the model (or its driver misbehaves), run on the processor instead of
    /// failing: slower, but instructors shouldn't have to know what "GPU layers" means.
    /// </summary>
    private async Task<IReadOnlyList<Question>> GenerateWithCpuFallbackAsync(
        QuestionGenerationRequest request,
        LocalLlmOptions options,
        IProgress<QuestionGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var device = ComputeDeviceReport.Unknown;
        // The progress bar's message is the tracker's (reading, writing question n of m), so the device is
        // reported separately.
        void OnLoaded(ComputeDeviceReport report)
        {
            device = report;
            DeviceProgress?.Report(report);
        }

        IReadOnlyList<Question> questions;
        try
        {
            questions = await GenerateCoreAsync(request, options, progress, "Loading the local model…", OnLoaded, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (QuestionGenerationException ex) when (
            options.GpuLayerCount > 0 &&
            ex.Failure is QuestionGenerationFailure.ModelLoadFailed or QuestionGenerationFailure.Other &&
            File.Exists(options.ModelPath) &&
            !cancellationToken.IsCancellationRequested)
        {
            // Progress starts over (the bar resets), so say why.
            questions = await GenerateCoreAsync(request, options with { GpuLayerCount = 0 }, progress,
                "The graphics card couldn't run the model, so the processor is being used instead (slower). Starting over…",
                report => OnLoaded(report with { Reason = ComputeDeviceReport.GpuFailed }), cancellationToken).ConfigureAwait(false);
        }
        LastRun = new ModelRun(device, stopwatch.Elapsed);
        return questions;
    }

    private static async Task<IReadOnlyList<Question>> GenerateCoreAsync(
        QuestionGenerationRequest request,
        LocalLlmOptions options,
        IProgress<QuestionGenerationProgress>? progress,
        string loadingMessage,
        Action<ComputeDeviceReport> onLoaded,
        CancellationToken cancellationToken)
    {
        var modelParams = CreateModelParams(options);
        var speed = options.GpuLayerCount > 0 ? SpeedGuess.Gpu : SpeedGuess.Cpu;
        using var tracker = new QuestionProgressTracker(progress, SpeedGuess.TokensPerQuestion);
        tracker.BeginLoading(loadingMessage, speed.LoadShare(request));
        using var loadLog = NativeLog.Start();
        using var weights = await LoadAsync(modelParams, options, tracker, cancellationToken).ConfigureAwait(false);
        var device = DetectDevice(loadLog.Stop(), modelParams.GpuLayerCount);
        // Reported once the context exists too, since creating it can fail on the GPU and fall back to the CPU.
        using var context = CreateContext(weights, modelParams, options);
        onLoaded(device);

        int CountTokens(string text) => weights.Tokenize(text, false, true, Encoding.UTF8).Length;

        // Budget: prompt (system + instructions + transcript section) + answer must fit the context.
        var contextSize = (int)context.ContextSize;
        var perQuestion = Math.Max(150, options.MaxTokensPerQuestion);
        var answerBudget = Math.Clamp(contextSize / 4, Math.Min(perQuestion + 200, contextSize / 2), 4096);
        var maxQuestionsPerSection = Math.Max(1, (answerBudget - 200) / perQuestion);
        var overhead = CountTokens(BuildPrompt(weights, request, maxQuestionsPerSection, EmptySection)) + SafetyMargin;
        var transcriptBudget = contextSize - overhead - answerBudget;
        if (transcriptBudget < 512)
            throw new QuestionGenerationException(QuestionGenerationFailure.BadRequest,
                $"The context size ({contextSize} tokens) is too small for question generation. Increase it to at least 8192 in Settings.");

        var chunks = TranscriptChunker.Chunk(request.Transcript, transcriptBudget, CountTokens);
        var durations = chunks.Select(c => TranscriptChunker.Duration(c).TotalSeconds).ToList();
        var capacities = durations.Select(d => Math.Min(maxQuestionsPerSection, SpacingCapacity(d, request.MinimumSpacing))).ToList();
        var allocation = TranscriptChunker.Allocate(durations, capacities, request.QuestionCount);

        // The grammar makes each section write exactly its allocation and sections run one after another, so the
        // whole job is known up front: read each section's prompt, then write its questions.
        var parts = new List<(int Section, string Prompt, int PromptTokens, int Count)>();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (allocation[i] == 0) continue;
            var prompt = BuildPrompt(weights, request, allocation[i], new TranscriptSection(i + 1, chunks.Count, chunks[i]));
            parts.Add((i, prompt, CountTokens(prompt), allocation[i]));
        }
        tracker.Plan(parts.Select(p => new PlannedPart(p.Count, speed.ReadWeight(p.PromptTokens))).ToList());

        var drafts = new List<QuestionDraft>();
        int failedSections = 0;
        double readTokensPerSecond = speed.ReadTokensPerSecond;
        for (int n = 0; n < parts.Count; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (section, prompt, promptTokens, count) = parts[n];

            tracker.BeginPart(n, TimeSpan.FromSeconds(promptTokens / readTokensPerSecond));
            var readStarted = Stopwatch.GetTimestamp();
            bool firstPiece = true;
            var output = await InferAsync(context, prompt, request.AllowedTypes, count, perQuestion, options, piece =>
            {
                if (firstPiece)
                {
                    // The reading speed measured on this PC paces the next section's reading step.
                    firstPiece = false;
                    var seconds = Stopwatch.GetElapsedTime(readStarted).TotalSeconds;
                    if (seconds > 0.05) readTokensPerSecond = promptTokens / seconds;
                }
                tracker.OnOutput(piece, 1);
            }, cancellationToken).ConfigureAwait(false);
            tracker.EndPart();

            var sectionDrafts = QuestionDraftSchema.ParseLenient(output);
            if (sectionDrafts.Count == 0)
            {
                failedSections++;
                tracker.Report(chunks.Count > 1
                    ? $"Section {section + 1} of {chunks.Count}: the model's answer couldn't be read; skipping this section."
                    : "The model's answer couldn't be read.");
                continue;
            }
            drafts.AddRange(sectionDrafts);
        }

        tracker.Complete("Checking the questions…");
        var questions = QuestionPostProcessor.Process(drafts, request);
        if (questions.Count == 0)
        {
            throw new QuestionGenerationException(QuestionGenerationFailure.InvalidResponse,
                failedSections == parts.Count
                    ? "The local model didn't produce readable questions. Try a larger or instruction-tuned model, or use Claude."
                    : "The local model's questions didn't pass validation. Try again, try a larger model, or use Claude.");
        }
        tracker.Complete($"Generated {questions.Count} question{(questions.Count == 1 ? "" : "s")}.");
        return questions;
    }

    /// <summary>
    /// First guesses at this PC's speed. They only pace the progress bar until real speeds are measured
    /// (Qwen 3.5 4B on an RTX 3070 vs. a typical laptop processor; bigger models are slower at every step alike).
    /// </summary>
    private sealed record SpeedGuess(double LoadSeconds, double ReadTokensPerSecond, double WriteTokensPerSecond)
    {
        /// <summary>Typical size of one question in the grammar's JSON, in tokens.</summary>
        public const int TokensPerQuestion = 260;

        public static readonly SpeedGuess Gpu = new(6, 1500, 60);
        public static readonly SpeedGuess Cpu = new(4, 120, 10);

        private double SecondsPerQuestion => TokensPerQuestion / WriteTokensPerSecond;

        /// <summary>Time to read (process) a prompt, in "time to write one question".</summary>
        public double ReadWeight(int promptTokens) => promptTokens / ReadTokensPerSecond / SecondsPerQuestion;

        /// <summary>The part of the bar for loading the model, estimated before the transcript is tokenized.</summary>
        public double LoadShare(QuestionGenerationRequest request)
        {
            // ~4 characters per token, plus each line's timestamp and the instructions in the prompt.
            var promptTokens = request.Transcript.Sum(s => s.Text.Length + 10) / 4.0 + 1000;
            var work = request.QuestionCount * SecondsPerQuestion + promptTokens / ReadTokensPerSecond;
            return LoadSeconds / (LoadSeconds + work);
        }
    }

    private static readonly TranscriptSection EmptySection =
        new(1, 2, [new CaptionSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), ".")]);

    /// <summary>Formats the conversation with the chat template embedded in the GGUF (falls back to ChatML).</summary>
    private static string BuildPrompt(LLamaWeights weights, QuestionGenerationRequest request, int count, TranscriptSection section)
    {
        var template = new LLamaTemplate(weights) { AddAssistant = true };
        template.Add("system", QuestionPrompt.SystemPrompt);
        template.Add("user", QuestionPrompt.BuildUserMessage(request, count, section));
        return PromptTemplateTransformer.ToModelPrompt(template);
    }

    /// <summary>
    /// Runs one prompt from an empty context. Equivalent to LLamaSharp's StatelessExecutor, which can't be used
    /// because it creates its own contexts without checking that creation succeeded (see <see cref="CreateContext"/>).
    /// </summary>
    private static async Task<string> InferAsync(
        LLamaContext context,
        string prompt,
        IReadOnlyCollection<QuestionType> allowedTypes,
        int count,
        int perQuestion,
        LocalLlmOptions options,
        Action<string> onPiece,
        CancellationToken cancellationToken)
    {
        using var sampling = new DefaultSamplingPipeline
        {
            Temperature = options.Temperature,
            Grammar = new Grammar(QuestionGrammar.Build(allowedTypes, count), QuestionGrammar.Root),
            // Grammar applied to the top candidates only, falling back to the full vocabulary when needed.
            GrammarOptimization = DefaultSamplingPipeline.GrammarOptimizationMode.Extended,
            Seed = options.Seed ?? (uint)Random.Shared.Next(),
        };
        var maxTokens = count * perQuestion + 200;
        var contextSize = (int)context.ContextSize;
        var decoder = new StreamingTokenDecoder(context);
        var batch = new LLamaBatch();
        var sb = new StringBuilder();
        try
        {
            context.NativeHandle.MemoryClear();
            var tokens = context.Tokenize(prompt, addBos: true, special: true).ToList();
            var (result, _, past) = await context.DecodeAsync(tokens, LLamaSeqId.Zero, batch, 0, cancellationToken).ConfigureAwait(false);
            if (result != DecodeResult.Ok) throw new LLamaDecodeError(result);

            for (int i = 0; i < maxTokens; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = sampling.Sample(context.NativeHandle, batch.TokenCount - 1);
                if (token.IsEndOfGeneration(context.Vocab)) break;
                decoder.Add(token);
                var piece = decoder.Read();
                sb.Append(piece);
                onPiece(piece);

                if (past + 1 >= contextSize) throw new ContextOverflowException();
                batch.Clear();
                batch.Add(token, past++, LLamaSeqId.Zero, true);
                result = await context.DecodeAsync(batch, cancellationToken).ConfigureAwait(false);
                if (result != DecodeResult.Ok) throw new LLamaDecodeError(result);
            }
        }
        catch (ContextOverflowException ex)
        {
            throw new QuestionGenerationException(QuestionGenerationFailure.BadRequest,
                "The transcript section didn't fit the local model's context. Increase the context size in Settings.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new QuestionGenerationException(QuestionGenerationFailure.Other,
                "The local model stopped with an error: " + ex.Message, ex);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Creates the context (KV cache and compute buffers) for the loaded model, using the largest context size
    /// from <see cref="ContextSizesToTry"/> that fits in the device's free memory.
    /// </summary>
    /// <remarks>
    /// llama.cpp returns a null context when it can't allocate the KV cache (e.g. out of video memory), and
    /// LLamaSharp 0.27 doesn't check for that: <c>SafeLLamaContextHandle.Create</c> tests the P/Invoke result for
    /// null, but the marshaller always returns a handle object (wrapping a null pointer), so no exception is
    /// thrown and the next native call on it crashes the process with an access violation. Hence the explicit
    /// <see cref="System.Runtime.InteropServices.SafeHandle.IsInvalid"/> check here.
    /// A smaller context on the GPU is much faster than the CPU fallback; it only means more transcript sections.
    /// </remarks>
    private static LLamaContext CreateContext(LLamaWeights weights, ModelParams modelParams, LocalLlmOptions options)
    {
        var requested = (int)(modelParams.ContextSize ?? (uint)options.ContextSize);
        Exception? lastError = null;
        foreach (var size in ContextSizesToTry(requested, weights.ContextSize))
        {
            modelParams.ContextSize = (uint)size;
            LLamaContext context;
            try
            {
                context = weights.CreateContext(modelParams);
            }
            catch (RuntimeError ex)
            {
                // A later LLamaSharp that checks the handle itself would throw here instead.
                lastError = ex;
                continue;
            }
            if (!context.NativeHandle.IsInvalid) return context;

            // Create() took a reference on the model before the handle turned out to be invalid, and SafeHandle
            // never runs ReleaseHandle for an invalid handle, so Dispose won't give it back. Without this the
            // weights would stay in (video) memory until the app closes, even after LLamaWeights.Dispose.
            context.Dispose();
            weights.NativeHandle.DangerousRelease();
        }

        throw new QuestionGenerationException(QuestionGenerationFailure.ModelLoadFailed,
            options.GpuLayerCount > 0
                ? "The graphics card doesn't have enough free memory for the local model."
                : "This PC doesn't have enough free memory to run the local model. Close other programs and try again, lower the context size in Settings, or use Claude.",
            lastError);
    }

    /// <summary>Smallest context size worth retrying with when the requested one doesn't fit in memory.</summary>
    internal const int MinimumFallbackContextSize = 8192;

    /// <summary>
    /// Context sizes to attempt, largest first: the requested size (capped at what the model was trained for,
    /// since more can't help), then halving down to <see cref="MinimumFallbackContextSize"/>. A request already
    /// at or below the minimum is tried as-is only.
    /// </summary>
    /// <param name="trainedContextSize">The model's training context, or 0 or less if unknown.</param>
    internal static IReadOnlyList<int> ContextSizesToTry(int requested, int trainedContextSize)
    {
        var size = trainedContextSize > 0 ? Math.Min(requested, Math.Max(trainedContextSize, MinimumFallbackContextSize)) : requested;
        var sizes = new List<int> { size };
        while (size > MinimumFallbackContextSize)
        {
            size = Math.Max(MinimumFallbackContextSize, size / 2);
            sizes.Add(size);
        }
        return sizes;
    }

    private static int SpacingCapacity(double durationSeconds, TimeSpan spacing) =>
        spacing <= TimeSpan.Zero ? int.MaxValue : Math.Max(1, (int)(durationSeconds / spacing.TotalSeconds) + 1);

    private static ModelParams CreateModelParams(LocalLlmOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModelPath) || !File.Exists(options.ModelPath))
            throw new QuestionGenerationException(QuestionGenerationFailure.ModelLoadFailed,
                "The local model file wasn't found. Choose a .gguf model file in Settings.");

        // Constructing ModelParams loads the native library, after which its configuration can't change.
        ConfigureNativeLibrary();
        var p = new ModelParams(options.ModelPath)
        {
            ContextSize = (uint)Math.Max(2048, options.ContextSize),
            GpuLayerCount = Math.Max(0, options.GpuLayerCount),
        };
        if (options.Threads is { } threads and > 0) p.Threads = threads;
        return p;
    }

    private static async Task<LLamaWeights> LoadAsync(
        ModelParams modelParams, LocalLlmOptions options, QuestionProgressTracker tracker, CancellationToken cancellationToken)
    {
        ConfigureNativeLibrary();
        try
        {
            // llama.cpp reports loading progress from its own thread; the tracker is thread-safe.
            return await LLamaWeights.LoadFromFileAsync(modelParams, cancellationToken, new LoadProgress(tracker)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is not LoadWeightsFailedException and (RuntimeError or DllNotFoundException or TypeInitializationException)
                ? "The local AI runtime couldn't start on this PC."
                : $"The model \"{Path.GetFileName(options.ModelPath)}\" couldn't be loaded. It may be damaged or too large for this PC's memory. Try the Standard model in Settings, or delete and download it again.";
            throw new QuestionGenerationException(QuestionGenerationFailure.ModelLoadFailed, message, ex);
        }
    }

    /// <summary>Forwards llama.cpp's loading progress straight to the tracker (no context switch).</summary>
    private sealed class LoadProgress(QuestionProgressTracker tracker) : IProgress<float>
    {
        public void Report(float value) => tracker.ReportLoad(value);
    }

    /// <summary>
    /// Where the model's layers went, from llama.cpp's load log ("offloaded 33/33 layers to GPU"), or from the
    /// runtime itself if the log didn't say.
    /// </summary>
    private static ComputeDeviceReport DetectDevice(IReadOnlyList<string> loadLog, int requestedGpuLayers)
    {
        var vulkanDevices = GgmlLog.ParseVulkanDevices(loadLog);
        if (vulkanDevices.Count > 0) _vulkanDevices = vulkanDevices;

        bool gpuOffloadSupported;
        try
        {
            // False when the CPU build was loaded (Vulkan runtime missing or broken) or Vulkan found no device.
            gpuOffloadSupported = NativeApi.llama_supports_gpu_offload() &&
                NativeApi.GetLoadedNativeLibrary(NativeLibraryName.LLama)?.Metadata?.UseVulkan != false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            gpuOffloadSupported = false;
        }
        return GgmlLog.InterpretLlamaLoad(GgmlLog.ParseLlamaLoad(loadLog), requestedGpuLayers, gpuOffloadSupported, _vulkanDevices);
    }

    /// <summary>
    /// Prefer the Vulkan backend (NVIDIA/AMD/Intel GPUs without CUDA) and fall back to the best CPU build
    /// (AVX512/AVX2/AVX/none) if Vulkan can't load. Must run before the first native call; later calls are ignored.
    /// </summary>
    private static void ConfigureNativeLibrary()
    {
        lock (NativeConfigLock)
        {
            if (_nativeConfigured) return;
            _nativeConfigured = true;
            try
            {
                NativeLibraryConfig.All
                    .WithVulkan(true)
                    .WithAutoFallback(true)
                    // llama.cpp is chatty; keep it off stderr. Only recorded while a model loads (see DetectDevice).
                    .WithLogCallback((level, message) => NativeLog.Write(message, level == LLamaLogLevel.Continue));
            }
            catch (InvalidOperationException)
            {
                // Native library already loaded by an earlier call; its configuration stands.
            }
        }
    }
}
