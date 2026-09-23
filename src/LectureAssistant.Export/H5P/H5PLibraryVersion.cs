using System.Globalization;

namespace LectureAssistant.Export.H5P;

/// <summary>H5P libraries are addressed by major.minor; patch versions are interchangeable.</summary>
public readonly record struct H5PLibraryVersion(int Major, int Minor)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");

    /// <summary>"H5P.MultiChoice 1.16", the form used in content params.</summary>
    public string ToLibraryString(string machineName) => $"{machineName} {this}";
}

/// <summary>Machine names of the H5P libraries this exporter targets.</summary>
public static class H5PLibraries
{
    public const string InteractiveVideo = "H5P.InteractiveVideo";
    public const string Video = "H5P.Video";
    public const string MultiChoice = "H5P.MultiChoice";
    public const string TrueFalse = "H5P.TrueFalse";
    public const string Blanks = "H5P.Blanks";
}
