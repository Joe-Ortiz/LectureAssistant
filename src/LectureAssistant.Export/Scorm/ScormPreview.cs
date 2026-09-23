using LectureAssistant.Core.Captions;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Export.Scorm;

/// <summary>A video file the preview plays instead of YouTube, addressed by a URL the preview browser can load.</summary>
/// <param name="VideoUrl">URL of the lecture video file.</param>
public sealed record PreviewVideo(string VideoUrl);

/// <summary>
/// Lays out the exact SCORM player students get, plus <see cref="HostPage"/>: a page that stands in for the LMS
/// (a SCORM 1.2 API kept in memory) and shows what the gradebook would record. Nothing is saved or sent anywhere.
/// </summary>
public static class ScormPreview
{
    public const string HostPage = "preview.html";
    private const string CaptionsFile = "captions.vtt";
    private const string HostResource = "Preview/preview.html";

    /// <param name="hasLocalVideo">True when the preview can play the instructor's video file if there's no YouTube link yet.</param>
    public static IReadOnlyList<string> Validate(LectureProject project, bool hasLocalVideo) =>
        ExportValidation.Validate(project, requireYouTube: !hasLocalVideo);

    /// <summary>
    /// Replaces the contents of <paramref name="folder"/> with the preview. When <paramref name="localVideo"/> is given
    /// the player uses it (with the project's captions) instead of YouTube.
    /// </summary>
    public static async Task WriteAsync(LectureProject project, string folder, PreviewVideo? localVideo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var problems = Validate(project, localVideo is not null);
        if (problems.Count > 0)
            throw new InvalidOperationException("The lecture can't be previewed yet:\n" + string.Join("\n", problems));

        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        var assembly = typeof(ScormPreview).Assembly;
        foreach (var name in ScormExporter.PlayerAssetNames(assembly))
            await CopyResourceAsync("Scorm/" + name, Path.Combine(folder, name), cancellationToken);
        await CopyResourceAsync(HostResource, Path.Combine(folder, HostPage), cancellationToken);

        string? captionsUrl = null;
        if (localVideo is not null && project.Captions.Count > 0)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, CaptionsFile), CaptionFormats.ToWebVtt(project.Captions), cancellationToken);
            captionsUrl = CaptionsFile;
        }

        var script = LectureDataScript.Build(project, localVideo?.VideoUrl, captionsUrl);
        await File.WriteAllTextAsync(Path.Combine(folder, LectureDataScript.FileName), script, cancellationToken);
    }

    private static async Task CopyResourceAsync(string resource, string destination, CancellationToken cancellationToken)
    {
        await using var source = typeof(ScormPreview).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded preview asset '{resource}'.");
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken);
    }
}
