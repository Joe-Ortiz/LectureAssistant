using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LectureAssistant.Core.Models;
using LectureAssistant.Core.YouTube;

namespace LectureAssistant.Export.H5P;

/// <summary>Builds <c>content/content.json</c> (H5P.InteractiveVideo params) from a lecture.</summary>
internal static partial class H5PContentBuilder
{
    // Poster geometry. x/y are percentages of the video; width/height are in IV's "em" units, where the
    // video is 40 wide and 22.5 high (16:9), so this is a centered box covering 80% x ~89% of the video.
    private const double PosterX = 10, PosterY = 5, PosterWidth = 32, PosterHeight = 20;

    /// <summary>Minimum gap between interactions so two questions never share the screen.</summary>
    private const double MinimumGapSeconds = 1;

    public static JsonObject Build(LectureProject project, Func<string, H5PLibraryVersion> version)
    {
        var content = H5PDefaults.InteractiveVideo();
        var iv = content["interactiveVideo"]!.AsObject();
        var video = iv["video"]!.AsObject();

        video["files"] = new JsonArray(new JsonObject
        {
            ["path"] = YouTubeUrl.WatchUrl(project.YouTubeVideoId!),
            ["mime"] = "video/YouTube",
            ["copyright"] = new JsonObject { ["license"] = "U" },
        });
        video["startScreenOptions"]!["title"] = HtmlText.Encode(ExportValidation.TitleOrDefault(project));
        // Captions come from YouTube itself; IV can't overlay its own text tracks on a YouTube source.
        // The group must still exist: the player reads textTracks.videoTrack/defaultTrackLabel unguarded.
        video["textTracks"] = new JsonObject { ["videoTrack"] = new JsonArray() };

        var interactions = iv["assets"]!["interactions"]!.AsArray();
        double previousFrom = double.NegativeInfinity;
        foreach (var question in ExportValidation.OrderedQuestions(project))
        {
            var from = Math.Max(ExportValidation.Seconds(question.Timestamp), previousFrom + MinimumGapSeconds);
            previousFrom = from;
            interactions.Add(Interaction(question, from, project.Quiz, version));
        }

        var overrides = content["override"]!.AsObject();
        overrides["preventSkippingMode"] = project.Quiz.PreventSkippingAhead ? "forward" : "none";
        overrides["retryButton"] = project.Quiz.AllowRetry ? "on" : "off";
        overrides["showSolutionButton"] = project.Quiz.ShowCorrectAnswers ? "on" : "off";

        return content;
    }

    /// <summary>Machine names of the question libraries the project uses.</summary>
    public static IEnumerable<string> QuestionLibraries(LectureProject project) =>
        project.Questions.Select(q => LibraryFor(q.Type).MachineName).Distinct();

    private static (string MachineName, string Title) LibraryFor(QuestionType type) => type switch
    {
        QuestionType.MultipleChoice => (H5PLibraries.MultiChoice, "Multiple Choice"),
        QuestionType.TrueFalse => (H5PLibraries.TrueFalse, "True/False Question"),
        QuestionType.FillInTheBlank => (H5PLibraries.Blanks, "Fill in the Blanks"),
        _ => throw new NotSupportedException($"Question type {type} has no H5P equivalent."),
    };

    private static JsonObject Interaction(Question question, double from, QuizSettings quiz, Func<string, H5PLibraryVersion> version)
    {
        var (machineName, libraryTitle) = LibraryFor(question.Type);
        var parameters = question.Type switch
        {
            QuestionType.MultipleChoice => MultiChoiceParams(question, quiz),
            QuestionType.TrueFalse => TrueFalseParams(question, quiz),
            _ => BlanksParams(question, quiz),
        };

        var interaction = H5PDefaults.Interaction();
        interaction["x"] = PosterX;
        interaction["y"] = PosterY;
        interaction["width"] = PosterWidth;
        interaction["height"] = PosterHeight;
        interaction["duration"] = new JsonObject { ["from"] = from, ["to"] = from };
        interaction["libraryTitle"] = libraryTitle;
        interaction["action"] = new JsonObject
        {
            ["library"] = version(machineName).ToLibraryString(machineName),
            ["params"] = parameters,
            ["subContentId"] = SubContentId(question.Id),
            ["metadata"] = new JsonObject
            {
                ["contentType"] = libraryTitle,
                ["license"] = "U",
                ["title"] = HtmlText.Encode(MetadataTitle(question.Prompt)),
            },
        };
        return interaction;
    }

    private static JsonObject MultiChoiceParams(Question q, QuizSettings quiz)
    {
        var p = H5PDefaults.MultiChoice();
        var multi = q.Options.Count(o => o.IsCorrect) > 1;

        p["question"] = HtmlText.Paragraph(q.Prompt);
        p["answers"] = new JsonArray(q.Options.Select(o => (JsonNode)new JsonObject
        {
            ["text"] = HtmlText.Div(o.Text),
            ["correct"] = o.IsCorrect,
            ["tipsAndFeedback"] = new JsonObject
            {
                ["tip"] = "",
                ["chosenFeedback"] = string.IsNullOrWhiteSpace(o.Feedback) ? "" : HtmlText.Div(o.Feedback),
                ["notChosenFeedback"] = "",
            },
        }).ToArray());
        p["overallFeedback"] = OverallFeedback(q.Explanation, quiz.ShowCorrectAnswers);

        var behaviour = p["behaviour"]!.AsObject();
        behaviour["type"] = multi ? "multi" : "single";
        // Matches the SCORM player: several correct options are scored all-or-nothing.
        behaviour["singlePoint"] = multi;
        // The instructor's option order is intentional (e.g. "All of the above").
        behaviour["randomAnswers"] = false;
        behaviour["enableRetry"] = quiz.AllowRetry;
        behaviour["enableSolutionsButton"] = quiz.ShowCorrectAnswers;
        return p;
    }

    private static JsonObject TrueFalseParams(Question q, QuizSettings quiz)
    {
        var p = H5PDefaults.TrueFalse();
        p["question"] = HtmlText.Paragraph(q.Prompt);
        p["correct"] = q.CorrectAnswer ? "true" : "false";

        var behaviour = p["behaviour"]!.AsObject();
        behaviour["enableRetry"] = quiz.AllowRetry;
        behaviour["enableSolutionsButton"] = quiz.ShowCorrectAnswers;
        if (!string.IsNullOrWhiteSpace(q.Explanation))
        {
            // Plain-text fields: hosts HTML-escape these on save; pre-escaping keeps hosts that don't safe.
            behaviour["feedbackOnCorrect"] = HtmlText.Encode(q.Explanation.Trim());
            if (quiz.ShowCorrectAnswers) behaviour["feedbackOnWrong"] = HtmlText.Encode(q.Explanation.Trim());
        }
        return p;
    }

    private static JsonObject BlanksParams(Question q, QuizSettings quiz)
    {
        var p = H5PDefaults.Blanks();
        p["text"] = "<p>Fill in the missing word.</p>";
        p["questions"] = new JsonArray(ToClozeText(q.Prompt, q.AcceptedAnswers));
        p["overallFeedback"] = OverallFeedback(q.Explanation, quiz.ShowCorrectAnswers);

        var behaviour = p["behaviour"]!.AsObject();
        behaviour["caseSensitive"] = false;
        behaviour["enableRetry"] = quiz.AllowRetry;
        behaviour["enableSolutionsButton"] = quiz.ShowCorrectAnswers;
        return p;
    }

    /// <summary>Explanation for everyone, or only for full marks when correct answers are hidden.</summary>
    private static JsonArray OverallFeedback(string? explanation, bool showOnWrong)
    {
        if (string.IsNullOrWhiteSpace(explanation))
            return [new JsonObject { ["from"] = 0, ["to"] = 100 }];

        var text = HtmlText.Encode(explanation.Trim());
        return showOnWrong
            ? [new JsonObject { ["from"] = 0, ["to"] = 100, ["feedback"] = text }]
            :
            [
                new JsonObject { ["from"] = 0, ["to"] = 99 },
                new JsonObject { ["from"] = 100, ["to"] = 100, ["feedback"] = text },
            ];
    }

    /// <summary>
    /// Converts a fill-in prompt to H5P.Blanks cloze syntax: the blank (a run of 3+ underscores) becomes
    /// <c>*answer1/answer2*</c>, wrapped in &lt;p&gt;.
    /// H5P.Blanks splits the text between asterisks on '/' (alternatives) and ':' (tip), and then decodes HTML
    /// entities in each alternative. So '*', '/' and ':' inside answers are written as the numeric entities
    /// &amp;#42; &amp;#47; &amp;#58; — they can't break the syntax, yet students still type them normally
    /// ("1/2" and "3:00" remain correct answers). '*' in the surrounding prompt is escaped the same way so it
    /// can't open a second blank. Whitespace inside answers is collapsed, matching how students' input compares.
    /// </summary>
    internal static string ToClozeText(string prompt, IEnumerable<string> acceptedAnswers)
    {
        var match = BlankRun().Match(prompt);
        var before = match.Success ? prompt[..match.Index] : prompt.TrimEnd() + " ";
        var after = match.Success ? prompt[(match.Index + match.Length)..] : "";

        var answers = acceptedAnswers
            .Select(a => Whitespace().Replace(a, " ").Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ClozeAnswer);

        return "<p>" + ClozeProse(before.TrimStart()) + "*" + string.Join("/", answers) + "*" + ClozeProse(after.TrimEnd()) + "</p>";
    }

    private static string ClozeProse(string text) => HtmlText.EncodeMultiline(text).Replace("*", "&#42;");

    private static string ClozeAnswer(string answer) =>
        HtmlText.Encode(answer).Replace("*", "&#42;").Replace("/", "&#47;").Replace(":", "&#58;");

    /// <summary>Stable per question, so re-exports keep learners' saved state attached to the right interaction.</summary>
    internal static string SubContentId(string questionId)
    {
        if (Guid.TryParse(questionId, out var guid)) return guid.ToString("D");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(questionId));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static string MetadataTitle(string prompt)
    {
        var text = Whitespace().Replace(BlankRun().Replace(prompt, "___"), " ").Trim();
        return text.Length <= 100 ? text : text[..99].TrimEnd() + "…";
    }

    [GeneratedRegex("_{3,}")]
    private static partial Regex BlankRun();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
