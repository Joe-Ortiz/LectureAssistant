namespace LectureAssistant.Export.H5P;

public sealed record H5PExportOptions
{
    /// <summary>Library versions the generated params were written against (their semantics.json on GitHub).</summary>
    public static IReadOnlyDictionary<string, H5PLibraryVersion> DefaultLibraryVersions { get; } =
        new Dictionary<string, H5PLibraryVersion>(StringComparer.Ordinal)
        {
            [H5PLibraries.InteractiveVideo] = new(1, 28),
            [H5PLibraries.Video] = new(1, 6),
            [H5PLibraries.MultiChoice] = new(1, 16),
            [H5PLibraries.TrueFalse] = new(1, 8),
            [H5PLibraries.Blanks] = new(1, 14),
        };

    /// <summary>Versions to declare, by machine name. Missing names fall back to <see cref="DefaultLibraryVersions"/>.</summary>
    public IReadOnlyDictionary<string, H5PLibraryVersion> LibraryVersions { get; init; } = DefaultLibraryVersions;

    /// <summary>
    /// Optional .h5p exported from the instructor's own H5P platform. Its library versions override
    /// <see cref="LibraryVersions"/> (so the package targets exactly what the host has installed), and its
    /// library folders are copied into the output so hosts without those libraries can install them.
    /// </summary>
    public string? ReferencePackagePath { get; init; }
}
