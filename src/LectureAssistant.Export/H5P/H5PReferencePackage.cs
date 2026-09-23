using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace LectureAssistant.Export.H5P;

/// <summary>
/// A .h5p exported from the instructor's H5P host. We read which library versions it uses and copy its
/// library folders, but never its content.
/// </summary>
internal sealed class H5PReferencePackage
{
    private const string PackageJson = "h5p.json";
    private const string ContentFolder = "content/";

    private H5PReferencePackage(Dictionary<string, H5PLibraryVersion> versions, List<ZipArchiveEntry> libraryFiles)
    {
        Versions = versions;
        LibraryFiles = libraryFiles;
    }

    /// <summary>Library versions by machine name: h5p.json's preloadedDependencies, else the highest bundled library.json.</summary>
    public IReadOnlyDictionary<string, H5PLibraryVersion> Versions { get; }

    /// <summary>Every file inside a top-level library folder (anything other than content/ and h5p.json).</summary>
    public IReadOnlyList<ZipArchiveEntry> LibraryFiles { get; }

    public static async Task<H5PReferencePackage> ReadAsync(ZipArchive zip, CancellationToken cancellationToken)
    {
        // First occurrence wins if a (malformed) zip repeats a path.
        var entries = zip.Entries
            .GroupBy(e => NormalizePath(e.FullName), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (!entries.TryGetValue(PackageJson, out var packageJson))
            throw new InvalidDataException("The reference file is not an H5P package (it has no h5p.json).");

        var libraryFiles = new List<ZipArchiveEntry>();
        var versions = new Dictionary<string, H5PLibraryVersion>(StringComparer.Ordinal);

        foreach (var (path, entry) in entries)
        {
            var slash = path.IndexOf('/');
            if (slash <= 0 || path.EndsWith('/') || path.StartsWith(ContentFolder, StringComparison.Ordinal)) continue;
            if (path.Split('/').Any(segment => segment is "" or "." or "..")) continue;
            libraryFiles.Add(entry);

            if (path.Length == slash + "/library.json".Length && path.EndsWith("/library.json", StringComparison.Ordinal)
                && await TryReadDependencyAsync(entry, cancellationToken) is { } library
                && (!versions.TryGetValue(library.Name, out var existing) || IsNewer(library.Version, existing)))
            {
                versions[library.Name] = library.Version;
            }
        }

        using (var document = await ReadJsonAsync(packageJson, cancellationToken)
            ?? throw new InvalidDataException("The reference package's h5p.json is not valid JSON."))
        {
            if (document.RootElement.TryGetProperty("preloadedDependencies", out var dependencies)
                && dependencies.ValueKind == JsonValueKind.Array)
            {
                foreach (var dependency in dependencies.EnumerateArray())
                    if (ParseDependency(dependency) is { } d) versions[d.Name] = d.Version;
            }
        }

        return new H5PReferencePackage(versions, libraryFiles);
    }

    internal static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static bool IsNewer(H5PLibraryVersion a, H5PLibraryVersion b) =>
        a.Major > b.Major || (a.Major == b.Major && a.Minor > b.Minor);

    private static async Task<(string Name, H5PLibraryVersion Version)?> TryReadDependencyAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var document = await ReadJsonAsync(entry, cancellationToken);
        return document is null ? null : ParseDependency(document.RootElement);
    }

    private static async Task<JsonDocument?> ReadJsonAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await entry.OpenAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads { machineName, majorVersion, minorVersion }; versions may be numbers or strings.</summary>
    private static (string Name, H5PLibraryVersion Version)? ParseDependency(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("machineName", out var name) || name.ValueKind != JsonValueKind.String) return null;
        if (!TryGetInt(element, "majorVersion", out var major) || !TryGetInt(element, "minorVersion", out var minor)) return null;
        var machineName = name.GetString();
        return string.IsNullOrWhiteSpace(machineName) ? null : (machineName, new H5PLibraryVersion(major, minor));
    }

    private static bool TryGetInt(JsonElement element, string property, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var p)) return false;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(p.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }
}
