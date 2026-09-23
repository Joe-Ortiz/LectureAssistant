using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using LectureAssistant.Core;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Export.H5P;

/// <summary>
/// H5P Interactive Video (.h5p) for schools with an H5P host (H5P.com, Moodle, WordPress, Lumi).
/// The video plays from YouTube; each question becomes a MultiChoice, TrueFalse or Blanks interaction.
/// The package contains no library code of its own, only libraries copied from an optional reference package.
/// </summary>
public sealed class H5PExporter(H5PExportOptions? options = null) : ILectureExporter
{
    internal const string PackageJsonPath = "h5p.json";
    internal const string ContentJsonPath = "content/content.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public H5PExportOptions Options { get; } = options ?? new H5PExportOptions();

    public string DisplayName => "H5P Interactive Video (.h5p)";

    public string FileExtension => ".h5p";

    public IReadOnlyList<string> Validate(LectureProject project)
    {
        var problems = ExportValidation.Validate(project).ToList();
        if (Options.ReferencePackagePath is { } path && !File.Exists(path))
            problems.Add($"The reference H5P package \"{path}\" was not found.");
        return problems;
    }

    public async Task ExportAsync(LectureProject project, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);
        ExportValidation.ThrowIfInvalid(project);

        FileStream? referenceFile = null;
        ZipArchive? referenceZip = null;
        try
        {
            H5PReferencePackage? reference = null;
            if (Options.ReferencePackagePath is { } path)
            {
                referenceFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                referenceZip = await ZipArchive.CreateAsync(referenceFile, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, cancellationToken);
                reference = await H5PReferencePackage.ReadAsync(referenceZip, cancellationToken);
            }

            var versions = ResolveVersions(Options.LibraryVersions, reference?.Versions);
            H5PLibraryVersion VersionOf(string machineName) => versions[machineName];

            var content = H5PContentBuilder.Build(project, VersionOf);
            var packageJson = BuildPackageJson(project, VersionOf);

            await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken);
            await zip.AddTextAsync(PackageJsonPath, packageJson.ToJsonString(JsonOptions), cancellationToken);
            await zip.AddTextAsync(ContentJsonPath, content.ToJsonString(JsonOptions), cancellationToken);

            if (reference is not null)
            {
                foreach (var entry in reference.LibraryFiles)
                {
                    await using var source = await entry.OpenAsync(cancellationToken);
                    await zip.AddStreamAsync(H5PReferencePackage.NormalizePath(entry.FullName), source, cancellationToken);
                }
            }
        }
        finally
        {
            if (referenceZip is not null) await referenceZip.DisposeAsync();
            if (referenceFile is not null) await referenceFile.DisposeAsync();
        }
    }

    /// <summary>Defaults, then the configured table, then the reference package (what the host actually has).</summary>
    internal static IReadOnlyDictionary<string, H5PLibraryVersion> ResolveVersions(
        IReadOnlyDictionary<string, H5PLibraryVersion> configured,
        IReadOnlyDictionary<string, H5PLibraryVersion>? reference)
    {
        var versions = new Dictionary<string, H5PLibraryVersion>(H5PExportOptions.DefaultLibraryVersions, StringComparer.Ordinal);
        foreach (var (name, version) in configured) versions[name] = version;
        if (reference is not null)
            foreach (var (name, version) in reference)
                versions[name] = version;
        return versions;
    }

    private static JsonObject BuildPackageJson(LectureProject project, Func<string, H5PLibraryVersion> version)
    {
        var libraries = new[] { H5PLibraries.InteractiveVideo, H5PLibraries.Video }
            .Concat(new[] { H5PLibraries.MultiChoice, H5PLibraries.TrueFalse, H5PLibraries.Blanks }
                .Intersect(H5PContentBuilder.QuestionLibraries(project)));

        return new JsonObject
        {
            ["title"] = ExportValidation.TitleOrDefault(project),
            ["language"] = "und",
            ["mainLibrary"] = H5PLibraries.InteractiveVideo,
            ["embedTypes"] = new JsonArray("iframe"),
            ["license"] = "U",
            ["preloadedDependencies"] = new JsonArray(libraries.Select(name => (JsonNode)new JsonObject
            {
                ["machineName"] = name,
                ["majorVersion"] = version(name).Major,
                ["minorVersion"] = version(name).Minor,
            }).ToArray()),
        };
    }
}
