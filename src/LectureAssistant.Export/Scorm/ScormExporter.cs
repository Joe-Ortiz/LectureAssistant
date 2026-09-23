using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml;
using LectureAssistant.Core;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Export.Scorm;

/// <summary>
/// SCORM 1.2 package: a single SCO that plays the lecture's YouTube video, pauses for each question,
/// and reports score and pass/fail to the LMS. Upload the zip to Canvas (SCORM assignment), Moodle or Blackboard.
/// </summary>
public sealed class ScormExporter : ILectureExporter
{
    internal const string LaunchFile = "index.html";
    private const string ResourcePrefix = "Scorm/";

    public string DisplayName => "SCORM 1.2 package (Canvas, Moodle, Blackboard)";

    public string FileExtension => ".zip";

    public IReadOnlyList<string> Validate(LectureProject project) => ExportValidation.Validate(project);

    public async Task ExportAsync(LectureProject project, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);
        ExportValidation.ThrowIfInvalid(project);

        var assembly = typeof(ScormExporter).Assembly;
        var assets = PlayerAssetNames(assembly);
        var files = assets.Append(LectureDataScript.FileName).ToList();

        await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken);

        var manifest = ScormManifest.Build(project.Id, ExportValidation.TitleOrDefault(project), LaunchFile, files);
        await zip.AddTextAsync(ScormManifest.FileName, ToXmlString(manifest), cancellationToken);

        foreach (var name in assets)
        {
            await using var source = assembly.GetManifestResourceStream(ResourcePrefix + name)
                ?? throw new InvalidOperationException($"Missing embedded SCORM asset '{name}'.");
            await zip.AddStreamAsync(name, source, cancellationToken);
        }

        await zip.AddTextAsync(LectureDataScript.FileName, LectureDataScript.Build(project), cancellationToken);
    }

    /// <summary>Static player files (index.html, player.js, ...) embedded from Assets/Scorm, as paths inside the zip.</summary>
    internal static IReadOnlyList<string> PlayerAssetNames(Assembly assembly) =>
        assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..])
            .OrderBy(n => n == LaunchFile ? 0 : 1)
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static string ToXmlString(System.Xml.Linq.XDocument document)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
            document.Save(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
