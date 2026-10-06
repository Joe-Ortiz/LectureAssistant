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
        IProgress<string>? progress,
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
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GenerateCoreAsync(request, options, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (QuestionGenerationException ex) when (
            options.GpuLayerCount > 0 &&
            ex.Failure is QuestionGenerationFailure.ModelLoadFailed or QuestionGenerationFailure.Other &&
            File.Exists(options.ModelPath) &&
            !cancellationToken.IsCancellationRequested)
        {
            progress?.Report("The graphics card couldn't run the model, so the processor is being used instead (slower)…");
            return await GenerateCoreAsync(request, options with { GpuLayerCount = 0 }, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<Question>> GenerateCoreAsync(
        QuestionGenerationRequest request,
        LocalLlmOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var modelParams = CreateModelParams(options);
        progress?.Report("Loading the local model…");
        using var weights = await LoadAsync(modelParams, options, cancellationToken).ConfigureAwait(false);

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
        var allocation = PlanSections(chunks, request, maxQuestionsPerSection);

        var drafts = new List<QuestionDraft>();
        int failedSections = 0, attemptedSections = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = allocation[i];
            if (count == 0) continue;
            attemptedSections++;

            var sectionLabel = chunks.Count > 1 ? $"Section {i + 1} of {chunks.Count}: " : "";
            var writing = $"writing {count} question{(count == 1 ? "" : "s")}…";
            progress?.Report(sectionLabel.Length > 0 ? sectionLabel + writing : char.ToUpperInvariant(writing[0]) + writing[1..]);

            var section = new TranscriptSection(i + 1, chunks.Count, chunks[i]);
            var prompt = BuildPrompt(weights, request, count, section);
            var output = await InferAsync(weights, modelParams, prompt, request.AllowedTypes, count, perQuestion, options, cancellationToken)
                .ConfigureAwait(false);

            var sectionDrafts = QuestionDraftSchema.ParseLenient(output);
            if (sectionDrafts.Count == 0)
            {
                failedSections++;
                progress?.Report($"{sectionLabel}{(sectionLabel.Length > 0 ? "t" : "T")}he model's answer couldn't be read; skipping this section.");
                continue;
            }
            drafts.AddRange(sectionDrafts);
        }

        progress?.Report("Checking the questions…");
        var questions = QuestionPostProcessor.Process(drafts, request);
        if (questions.Count == 0)
        {
            throw new QuestionGenerationException(QuestionGenerationFailure.InvalidResponse,
                failedSections == attemptedSections
                    ? "The local model didn't produce readable questions. Try a larger or instruction-tuned model, or use Claude."
                    : "The local model's questions didn't pass validation. Try again, try a larger model, or use Claude.");
        }
        progress?.Report($"Generated {questions.Count} question{(questions.Count == 1 ? "" : "s")}.");
        return questions;
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
                sb.Append(piece);
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

    /// <summary>
    /// How many questions to ask each section for: shared out by duration, never more than fit in the section at the
    /// minimum spacing (away from questions being kept), and never more than fit in the whole lecture.
    /// </summary>
    internal static int[] PlanSections(
        IReadOnlyList<IReadOnlyList<CaptionSegment>> chunks, QuestionGenerationRequest request, int maxQuestionsPerSection)
    {
        var durations = chunks.Select(c => TranscriptChunker.Duration(c).TotalSeconds).ToList();
        var capacities = chunks
            .Select(c => Math.Min(maxQuestionsPerSection, QuestionPostProcessor.Capacity(c, request.MinimumSpacing, request.ReservedTimes)))
            .ToList();
        var total = Math.Min(request.QuestionCount, QuestionPostProcessor.Capacity(request));
        return TranscriptChunker.Allocate(durations, capacities, total);
    }

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

    private static async Task<LLamaWeights> LoadAsync(ModelParams modelParams, LocalLlmOptions options, CancellationToken cancellationToken)
    {
        ConfigureNativeLibrary();
        try
        {
            return await LLamaWeights.LoadFromFileAsync(modelParams, cancellationToken).ConfigureAwait(false);
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
