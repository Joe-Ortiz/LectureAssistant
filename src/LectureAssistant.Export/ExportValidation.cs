using LectureAssistant.Core.Captions;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Export;

/// <summary>Checks shared by every exporter: students watch on YouTube, and every question must be usable.</summary>
internal static class ExportValidation
{
    /// <param name="requireYouTube">False for a preview that can play the local video file instead.</param>
    public static IReadOnlyList<string> Validate(LectureProject project, bool requireYouTube = true)
    {
        var problems = new List<string>();

        if (requireYouTube && project.YouTubeVideoId is null)
        {
            problems.Add(string.IsNullOrWhiteSpace(project.YouTubeUrl)
                ? "Add the YouTube link for this lecture; students watch the video on YouTube."
                : $"The YouTube link \"{project.YouTubeUrl.Trim()}\" isn't a recognizable YouTube video URL.");
        }

        if (project.Questions.Count == 0)
            problems.Add("Add at least one question.");

        foreach (var question in project.Questions.OrderBy(q => q.Timestamp))
        {
            var at = CaptionFormats.Clock(question.Timestamp < TimeSpan.Zero ? TimeSpan.Zero : question.Timestamp);
            foreach (var problem in question.Validate())
                problems.Add($"Question at {at}: {problem}");

            if (project.Duration is { } duration && duration > TimeSpan.Zero && question.Timestamp > duration)
                problems.Add($"Question at {at} is after the end of the video ({CaptionFormats.Clock(duration)}).");
        }

        return problems;
    }

    public static void ThrowIfInvalid(LectureProject project)
    {
        var problems = Validate(project);
        if (problems.Count > 0)
            throw new InvalidOperationException("The lecture can't be exported yet:\n" + string.Join("\n", problems));
    }

    /// <summary>Questions in timeline order; ties keep the instructor's order.</summary>
    public static IReadOnlyList<Question> OrderedQuestions(LectureProject project) =>
        project.Questions.OrderBy(q => q.Timestamp).ToList();

    public static string TitleOrDefault(LectureProject project) =>
        string.IsNullOrWhiteSpace(project.Title) ? "Untitled lecture" : project.Title.Trim();

    public static double Seconds(TimeSpan t) => Math.Round(Math.Max(0, t.TotalSeconds), 3);
}
