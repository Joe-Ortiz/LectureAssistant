using System.Text.RegularExpressions;

namespace LectureAssistant.Core.Hardware;

public enum GraphicsKind
{
    /// <summary>No graphics hardware an AI model could use (or only a basic/virtual display adapter).</summary>
    None,

    /// <summary>Built into the processor; uses (a slice of) system memory rather than its own.</summary>
    Integrated,

    /// <summary>A separate graphics card with its own video memory.</summary>
    Dedicated,
}

/// <summary>Tells built-in graphics from separate graphics cards by name, since Windows doesn't say directly.</summary>
public static partial class GraphicsAdapters
{
    private const long GiB = 1L << 30;

    /// <summary>
    /// Classifies the adapter Windows reports. Integrated graphics report only a small "dedicated" carve-out
    /// (often 128–512 MB), so names decide where they're recognizable and memory size decides otherwise.
    /// </summary>
    public static GraphicsKind Classify(string? name, long dedicatedMemoryBytes) =>
        ClassifyByName(name) ?? dedicatedMemoryBytes switch
        {
            >= 2 * GiB => GraphicsKind.Dedicated,
            > 0 => GraphicsKind.Integrated,
            _ => GraphicsKind.None,
        };

    /// <summary>The kind implied by a known vendor naming scheme, or null when the name doesn't say.</summary>
    public static GraphicsKind? ClassifyByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = FriendlyName(name);

        if (VirtualAdapter().IsMatch(n)) return GraphicsKind.None;
        if (Nvidia().IsMatch(n)) return GraphicsKind.Dedicated;
        if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return IntelArcCard().IsMatch(n) ? GraphicsKind.Dedicated : GraphicsKind.Integrated; // Arc A/B-series cards vs. UHD/Iris/Arc iGPUs
        if (Amd().IsMatch(n))
        {
            // Ryzen iGPUs: "AMD Radeon(TM) Graphics", "Radeon 780M Graphics", "Radeon RX Vega 11 Graphics", "Radeon 890M".
            if (n.EndsWith("Graphics", StringComparison.OrdinalIgnoreCase)) return GraphicsKind.Integrated;
            if (AmdCard().IsMatch(n)) return GraphicsKind.Dedicated;
            if (AmdMobileIgpu().IsMatch(n)) return GraphicsKind.Integrated;
            return null;
        }
        if (ArmIgpu().IsMatch(n)) return GraphicsKind.Integrated;
        return null;
    }

    /// <summary>Drops trademark marks: "Intel(R) Iris(R) Xe Graphics" becomes "Intel Iris Xe Graphics".</summary>
    public static string FriendlyName(string name) =>
        Spaces().Replace(TrademarkMarks().Replace(name, ""), " ").Trim();

    [GeneratedRegex(@"\((R|TM|C)\)|®|™", RegexOptions.IgnoreCase)]
    private static partial Regex TrademarkMarks();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"Basic (Display|Render)|Remote Display|Virtual|Hyper-V|VMware|VirtualBox|Citrix|Parsec|Indirect Display", RegexOptions.IgnoreCase)]
    private static partial Regex VirtualAdapter();

    [GeneratedRegex(@"NVIDIA|GeForce|Quadro|Tesla|\bRTX\b", RegexOptions.IgnoreCase)]
    private static partial Regex Nvidia();

    [GeneratedRegex(@"\bArc\b.*\b[AB]\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex IntelArcCard();

    [GeneratedRegex(@"\bAMD\b|Radeon|\bATI\b", RegexOptions.IgnoreCase)]
    private static partial Regex Amd();

    [GeneratedRegex(@"\bRX\b|Radeon Pro|FirePro|Radeon VII|Instinct|\bR[579] \d{3}|\bHD \d{4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmdCard();

    [GeneratedRegex(@"\b\d{3,4}M\b|Vega \d{1,2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmdMobileIgpu();

    [GeneratedRegex(@"Qualcomm|Adreno|Snapdragon|Mali|PowerVR", RegexOptions.IgnoreCase)]
    private static partial Regex ArmIgpu();
}
