using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Export.Scorm;

/// <summary>
/// Builds <c>lecture-data.js</c>, which assigns the lecture to <c>window.LECTURE_DATA</c>.
/// A script (rather than a JSON file loaded with fetch) works from any LMS content host and from disk.
/// </summary>
internal static class LectureDataScript
{
    public const string FileName = "lecture-data.js";
    public const string Prefix = "window.LECTURE_DATA = ";

    /// <summary>
    /// <see cref="JavaScriptEncoder.Default"/> escapes &lt; &gt; &amp; ' and all non-ASCII (including U+2028/U+2029),
    /// so no instructor text can close the script element or break the JavaScript literal. It is set explicitly
    /// so a change to shared serializer options can't relax it.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Default,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Source-generated metadata: works when the app is trimmed or reflection-based serialization is off.
        TypeInfoResolver = LectureDataJsonContext.Default,
    };

    /// <param name="videoUrl">Preview only: play this video file instead of YouTube.</param>
    /// <param name="captionsUrl">Preview only: WebVTT captions for <paramref name="videoUrl"/>.</param>
    public static string Build(LectureProject project, string? videoUrl = null, string? captionsUrl = null)
    {
        var data = new LectureData(
            ExportValidation.TitleOrDefault(project),
            project.YouTubeVideoId ?? "",
            string.Equals(project.Language, "auto", StringComparison.OrdinalIgnoreCase) ? null : project.Language,
            new LectureSettings(
                project.Quiz.PassingScorePercent,
                project.Quiz.PreventSkippingAhead,
                project.Quiz.ShowCorrectAnswers),
            ExportValidation.OrderedQuestions(project).Select(q => ToData(q, AttemptRules.For(q, project.Quiz))).ToList(),
            videoUrl,
            captionsUrl);

        return Prefix + JsonSerializer.Serialize(data, JsonOptions) + ";\n";
    }

    /// <param name="rules">Resolved here, so the player never has to combine quiz settings and question overrides.</param>
    private static QuestionData ToData(Question q, AttemptRules rules) => new(
        q.Id,
        ExportValidation.Seconds(q.Timestamp),
        q.Type.ToString(),
        q.Prompt.Trim(),
        q.Type == QuestionType.MultipleChoice
            ? q.Options.Select(o => new OptionData(o.Text.Trim(), o.IsCorrect, NullIfBlank(o.Feedback))).ToList()
            : null,
        q.Type == QuestionType.TrueFalse ? q.CorrectAnswer : null,
        q.Type == QuestionType.FillInTheBlank
            ? q.AcceptedAnswers.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList()
            : null,
        NullIfBlank(q.Explanation),
        q.Points,
        rules.AttemptsAllowed,
        rules.Scoring.ToString(),
        rules.RetryPenaltyPercent);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal sealed record LectureData(
        string Title,
        string VideoId,
        string? Language,
        LectureSettings Settings,
        IReadOnlyList<QuestionData> Questions,
        string? VideoUrl = null,
        string? CaptionsUrl = null);

    internal sealed record LectureSettings(
        double PassingScorePercent,
        bool PreventSkippingAhead,
        bool ShowCorrectAnswers);

    /// <param name="Time">Seconds from the start of the video.</param>
    /// <param name="Type">A <see cref="QuestionType"/> name.</param>
    /// <param name="AttemptsAllowed">The question's effective limit; 0 = unlimited (<see cref="AttemptRules.Unlimited"/>).</param>
    /// <param name="RetryScoring">The question's effective <see cref="Core.Models.RetryScoring"/> name.</param>
    internal sealed record QuestionData(
        string Id,
        double Time,
        string Type,
        string Prompt,
        IReadOnlyList<OptionData>? Options,
        bool? CorrectAnswer,
        IReadOnlyList<string>? AcceptedAnswers,
        string? Explanation,
        int Points,
        int AttemptsAllowed,
        string RetryScoring,
        int RetryPenaltyPercent);

    internal sealed record OptionData(string Text, bool Correct, string? Feedback);
}

[JsonSerializable(typeof(LectureDataScript.LectureData))]
internal sealed partial class LectureDataJsonContext : JsonSerializerContext;
