using LectureAssistant.Core.Models;

namespace LectureAssistant.Core;

/// <summary>Produces a 16 kHz, mono, 16-bit PCM WAV file (what Whisper expects) from a video.</summary>
public interface IAudioExtractor
{
    Task ExtractAsync(string videoPath, string outputWavPath, IProgress<double>? progress, CancellationToken cancellationToken);
}

public sealed record TranscriptionOptions
{
    /// <summary>Whisper language code, or "auto" to detect.</summary>
    public string Language { get; init; } = "auto";

    /// <summary>
    /// Text in the style of the lecture, full of course vocabulary (names, jargon), that biases recognition
    /// toward correct spellings. Built by <see cref="Captions.RecognitionPrompt"/>.
    /// </summary>
    public string? Vocabulary { get; init; }
}

/// <param name="Fraction">0..1 through the audio.</param>
/// <param name="LatestSegment">Most recently recognized segment, for live display.</param>
public sealed record TranscriptionProgress(double Fraction, CaptionSegment? LatestSegment);

public interface ITranscriber
{
    Task<IReadOnlyList<CaptionSegment>> TranscribeAsync(
        string wavPath,
        TranscriptionOptions options,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record QuestionGenerationRequest
{
    public required IReadOnlyList<CaptionSegment> Transcript { get; init; }
    public string? LectureTitle { get; init; }
    public int QuestionCount { get; init; } = 8;
    public IReadOnlyCollection<QuestionType> AllowedTypes { get; init; } =
        [QuestionType.MultipleChoice, QuestionType.TrueFalse, QuestionType.FillInTheBlank];

    /// <summary>Free-form instructor direction, e.g. "focus on the derivation, intro-level students".</summary>
    public string? InstructorGuidance { get; init; }

    /// <summary>Questions closer together than this are spread out or dropped.</summary>
    public TimeSpan MinimumSpacing { get; init; } = TimeSpan.FromSeconds(45);
}

/// <param name="Message">What's happening, e.g. "Writing question 3 of 8…".</param>
/// <param name="Fraction">0..1 through the whole job, never decreasing within a run; null while the length of the
/// current step can't be known (shown as an indeterminate bar).</param>
/// <param name="TimeLeft">Rough time remaining, e.g. "About 2 minutes left"; null until there's a stable estimate.</param>
public sealed record QuestionGenerationProgress(string Message, double? Fraction, string? TimeLeft = null);

public interface IQuestionGenerator
{
    /// <summary>Shown in the UI, e.g. "Claude (API key)" or "Local model".</summary>
    string DisplayName { get; }

    Task<IReadOnlyList<Question>> GenerateAsync(
        QuestionGenerationRequest request,
        IProgress<QuestionGenerationProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Writes a <see cref="LectureProject"/> as a package students can use (SCORM zip, .h5p, ...).</summary>
public interface ILectureExporter
{
    /// <summary>Shown in the UI, e.g. "SCORM 1.2 package (Canvas, Moodle, Blackboard)".</summary>
    string DisplayName { get; }

    /// <summary>Including the dot, e.g. ".zip".</summary>
    string FileExtension { get; }

    /// <summary>Returns problems that prevent export (missing YouTube link, invalid questions); empty when ready.</summary>
    IReadOnlyList<string> Validate(LectureProject project);

    Task ExportAsync(LectureProject project, Stream output, CancellationToken cancellationToken);
}
