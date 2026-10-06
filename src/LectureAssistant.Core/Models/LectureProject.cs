using System.Text.Json.Serialization;

namespace LectureAssistant.Core.Models;

/// <summary>
/// Everything the app knows about one lecture: the source video, its captions,
/// the YouTube video students will watch, and the questions placed on its timeline.
/// </summary>
public sealed class LectureProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";

    /// <summary>Local video file the instructor imported (used for transcription and preview).</summary>
    public string? SourceVideoPath { get; set; }

    /// <summary>YouTube URL as pasted by the instructor. Use <see cref="YouTubeVideoId"/> for playback.</summary>
    public string? YouTubeUrl { get; set; }

    public TimeSpan? Duration { get; set; }

    /// <summary>Whisper language code ("en", "es", ...) or "auto".</summary>
    public string Language { get; set; } = "auto";

    /// <summary><see cref="Core.Captions.SubjectAreas"/> id used to prime speech recognition; null in projects saved before subjects existed.</summary>
    public string? SubjectArea { get; set; }

    public List<CaptionSegment> Captions { get; set; } = [];
    public List<Question> Questions { get; set; } = [];
    public QuizSettings Quiz { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;

    public string? YouTubeVideoId =>
        YouTubeUrl is not null && YouTube.YouTubeUrl.TryGetVideoId(YouTubeUrl, out var id) ? id : null;
}

public sealed class CaptionSegment
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; } = "";

    /// <summary>Words in <see cref="Text"/> the speech recognizer was unsure about; null when unknown (imported or older captions).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? UncertainWords { get; set; }

    public CaptionSegment() { }

    public CaptionSegment(TimeSpan start, TimeSpan end, string text)
    {
        Start = start;
        End = end;
        Text = text;
    }
}

public sealed class QuizSettings
{
    /// <summary>Score (0-100) at which the SCORM lesson status becomes "passed".</summary>
    public double PassingScorePercent { get; set; } = 70;

    /// <summary>Stop students from seeking past a question they haven't answered.</summary>
    public bool PreventSkippingAhead { get; set; } = true;

    /// <summary>Let students retry a question they got wrong (only the first attempt is scored).</summary>
    public bool AllowRetry { get; set; } = true;

    /// <summary>Reveal the correct answer and explanation after a student answers.</summary>
    public bool ShowCorrectAnswers { get; set; } = true;
}
