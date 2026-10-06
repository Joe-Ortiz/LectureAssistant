using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;
using LectureAssistant.Core;
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
public sealed class LocalLlmQuestionGenerator : IQuestionGenerator
{
    /// <summary>Tokens reserved beyond the prompt and answer for template quirks and BOS/EOS.</summary>
    private const int SafetyMargin = 128;

    private static readonly object NativeConfigLock = new();
    private static bool _nativeConfigured;

    private readonly Func<LocalLlmOptions> _options;

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
    private static async Task<IReadOnlyList<Question>> GenerateWithCpuFallbackAsync(
        QuestionGenerationRequest request,
        LocalLlmOptions options,
        IProgress<QuestionGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GenerateCoreAsync(request, options, progress, "Loading the local model…", cancellationToken).ConfigureAwait(false);
        }
        catch (QuestionGenerationException ex) when (
            options.GpuLayerCount > 0 &&
            ex.Failure is QuestionGenerationFailure.ModelLoadFailed or QuestionGenerationFailure.Other &&
            File.Exists(options.ModelPath) &&
            !cancellationToken.IsCancellationRequested)
        {
            // Progress starts over (the bar resets), so say why.
            return await GenerateCoreAsync(request, options with { GpuLayerCount = 0 }, progress,
                "The graphics card couldn't run the model, so the processor is being used instead (slower). Starting over…",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<Question>> GenerateCoreAsync(
        QuestionGenerationRequest request,
        LocalLlmOptions options,
        IProgress<QuestionGenerationProgress>? progress,
        string loadingMessage,
        CancellationToken cancellationToken)
    {
        var modelParams = CreateModelParams(options);
        var speed = options.GpuLayerCount > 0 ? SpeedGuess.Gpu : SpeedGuess.Cpu;
        using var tracker = new QuestionProgressTracker(progress, SpeedGuess.TokensPerQuestion);
        tracker.BeginLoading(loadingMessage, speed.LoadShare(request));
        using var weights = await LoadAsync(modelParams, options, tracker, cancellationToken).ConfigureAwait(false);

        int CountTokens(string text) => weights.Tokenize(text, false, true, Encoding.UTF8).Length;

        // Budget: prompt (system + instructions + transcript section) + answer must fit the context.
        var contextSize = (int)(modelParams.ContextSize ?? (uint)options.ContextSize);
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
            var output = await InferAsync(weights, modelParams, prompt, request.AllowedTypes, count, perQuestion, options, piece =>
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

    private static async Task<string> InferAsync(
        LLamaWeights weights,
        ModelParams modelParams,
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
        var inferenceParams = new InferenceParams
        {
            MaxTokens = count * perQuestion + 200,
            SamplingPipeline = sampling,
        };

        var executor = new StatelessExecutor(weights, modelParams) { ApplyTemplate = false };
        var sb = new StringBuilder();
        try
        {
            await foreach (var piece in executor.InferAsync(prompt, inferenceParams, cancellationToken).ConfigureAwait(false))
            {
                sb.Append(piece);
                onPiece(piece);
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
        finally
        {
            executor.Context?.Dispose();
        }
        return sb.ToString();
    }

    private static int SpacingCapacity(double durationSeconds, TimeSpan spacing) =>
        spacing <= TimeSpan.Zero ? int.MaxValue : Math.Max(1, (int)(durationSeconds / spacing.TotalSeconds) + 1);

    private static ModelParams CreateModelParams(LocalLlmOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModelPath) || !File.Exists(options.ModelPath))
            throw new QuestionGenerationException(QuestionGenerationFailure.ModelLoadFailed,
                "The local model file wasn't found. Choose a .gguf model file in Settings.");

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
                    .WithLogCallback((level, message) => { /* llama.cpp is chatty; keep it off stderr */ });
            }
            catch (InvalidOperationException)
            {
                // Native library already loaded by an earlier call; its configuration stands.
            }
        }
    }
}
