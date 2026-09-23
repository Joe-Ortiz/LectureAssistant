using LectureAssistant.Core.Models;
using LectureAssistant.Export.Scorm;

namespace LectureAssistant.App.Services;

/// <summary>What the preview window should show.</summary>
/// <param name="Folder">Folder holding the student player and the simulated-LMS host page.</param>
/// <param name="VideoFolder">Folder of the local video file, when the preview plays it instead of YouTube.</param>
public sealed record PreviewRequest(string Title, string Folder, string? VideoFolder);

/// <summary>Prepares the student preview: the real SCORM player, with the local video when there's no YouTube link yet.</summary>
public static class StudentPreview
{
    /// <summary>Private host names mapped to local folders. .example is reserved, so they never reach the internet.</summary>
    public const string PreviewHost = "preview.lectureassistant.example";
    public const string VideoHost = "video.lectureassistant.example";

    /// <summary>Formats the built-in Edge browser can play (H.264/AAC in MP4 or MOV, VP9 in WebM).</summary>
    private static readonly string[] PlayableExtensions = [".mp4", ".m4v", ".mov", ".webm"];

    public static bool CanPlayLocally(string? videoPath) =>
        videoPath is not null && File.Exists(videoPath) &&
        PlayableExtensions.Contains(Path.GetExtension(videoPath), StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns problems that prevent a preview; empty when it can open.</summary>
    public static IReadOnlyList<string> Validate(LectureProject project)
    {
        bool local = project.YouTubeVideoId is null && CanPlayLocally(project.SourceVideoPath);
        var problems = ScormPreview.Validate(project, local).ToList();
        if (project.YouTubeVideoId is null && !local && project.SourceVideoPath is not null)
        {
            problems.Insert(0, File.Exists(project.SourceVideoPath)
                ? $"This video's format ({Path.GetExtension(project.SourceVideoPath)}) can't play in the preview. Add the YouTube link in step 3 to preview with YouTube."
                : "The original video file can't be found. Add the YouTube link in step 3 to preview with YouTube.");
        }
        return problems;
    }

    public static async Task<PreviewRequest> PrepareAsync(LectureProject project, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(AppPaths.Previews, project.Id);
        string? videoFolder = null;
        PreviewVideo? video = null;

        if (project.YouTubeVideoId is null && CanPlayLocally(project.SourceVideoPath))
        {
            videoFolder = Path.GetDirectoryName(project.SourceVideoPath)!;
            video = new PreviewVideo($"https://{VideoHost}/{Uri.EscapeDataString(Path.GetFileName(project.SourceVideoPath!))}");
        }

        await ScormPreview.WriteAsync(project, folder, video, cancellationToken);
        return new PreviewRequest(string.IsNullOrWhiteSpace(project.Title) ? "Untitled lecture" : project.Title, folder, videoFolder);
    }
}
