using LectureAssistant.Core;
using LectureAssistant.Core.Captions;
using LectureAssistant.Core.Models;
using Whisper.net;

namespace LectureAssistant.Transcription;

/// <summary>Local, offline transcription with whisper.cpp (via Whisper.net). Uses the GPU through Vulkan when available.</summary>
public sealed class WhisperTranscriber(Func<string> modelPathProvider) : ITranscriber
{
    public async Task<IReadOnlyList<CaptionSegment>> TranscribeAsync(
        string wavPath,
        TranscriptionOptions options,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var modelPath = modelPathProvider();
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The speech recognition model hasn't been downloaded yet.", modelPath);

        var segments = new List<CaptionSegment>();
        double lastFraction = 0;

        using var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true });
        var builder = factory.CreateBuilder()
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            // Per-token probabilities, so the instructor can be pointed at the words recognition was unsure of.
            .WithProbabilities()
            .WithProgressHandler(percent =>
            {
                lastFraction = percent / 100.0;
                progress?.Report(new TranscriptionProgress(lastFraction, null));
            });

        builder = string.IsNullOrWhiteSpace(options.Language) || options.Language == "auto"
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(options.Language);

        if (!string.IsNullOrWhiteSpace(options.Vocabulary))
            builder = builder.WithPrompt(options.Vocabulary).WithCarryInitialPrompt(true);

        await using var processor = builder.Build();
        await using var audio = File.OpenRead(wavPath);

        await foreach (var result in processor.ProcessAsync(audio, cancellationToken))
        {
            var text = result.Text.Trim();
            var uncertain = WordsToCheck.FindUnsure((result.Tokens ?? []).Select(t => (t.Text ?? "", t.Probability)), text);
            var segment = new CaptionSegment(result.Start, result.End, text) { UncertainWords = uncertain.Count > 0 ? uncertain : null };
            segments.Add(segment);
            progress?.Report(new TranscriptionProgress(lastFraction, segment));
        }

        progress?.Report(new TranscriptionProgress(1, null));
        return CaptionCleanup.Normalize(segments);
    }

    /// <summary>Which native backend Whisper.net loaded (e.g. Vulkan or CPU), for diagnostics.</summary>
    public static string? GetRuntimeInfo() => WhisperFactory.GetRuntimeInfo();
}
