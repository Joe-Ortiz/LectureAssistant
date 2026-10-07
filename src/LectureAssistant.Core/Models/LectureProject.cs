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

    /// <summary>
    /// How many times a student may answer each question: 1 means no retries, <see cref="AttemptRules.Unlimited"/> (0)
    /// means as many as they like. A question can override it with <see cref="Question.AttemptsAllowed"/>.
    /// </summary>
    public int AttemptsAllowed { get; set; } = AttemptRules.Unlimited;

    /// <summary>How a correct answer on a retry is scored. A question can override it with <see cref="Question.RetryScoring"/>.</summary>
    public RetryScoring RetryScoring { get; set; } = RetryScoring.FirstAttemptOnly;

    /// <summary>
    /// With <see cref="RetryScoring.ReducedCredit"/>: percent of a question's points lost for each retry
    /// (50 gives 100%, 50%, 0%, ...). Credit never goes below zero.
    /// </summary>
    public int RetryPenaltyPercent { get; set; } = AttemptRules.DefaultRetryPenaltyPercent;

    /// <summary>Reveal the correct answer and explanation after a student answers.</summary>
    public bool ShowCorrectAnswers { get; set; } = true;

    public static readonly TimeSpan DefaultMinimumQuestionSpacing = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Shortest time allowed between questions, so students aren't interrupted constantly. Suggested questions
    /// always keep it; questions placed closer by hand are flagged. Null (lectures saved before this setting
    /// existed) means the app's default.
    /// </summary>
    public TimeSpan? MinimumQuestionSpacing { get; set; }

    /// <summary>
    /// Projects saved before attempts were configurable have an on/off "allowRetry" switch (retries were unlimited
    /// and only the first attempt was scored). Reading it maps on to unlimited attempts with only the first attempt
    /// counting, and off to a single attempt. It is never written.
    /// </summary>
    [JsonInclude, JsonPropertyName("allowRetry"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private bool? LegacyAllowRetry
    {
        get => null;
        set
        {
            if (value is null) return;
            AttemptsAllowed = value.Value ? AttemptRules.Unlimited : 1;
            RetryScoring = RetryScoring.FirstAttemptOnly;
        }
    }
}
