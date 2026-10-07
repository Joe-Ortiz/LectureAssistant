using LectureAssistant.Core.Hardware;
using LectureAssistant.QuestionGeneration.Local;
using Microsoft.Win32;

namespace LectureAssistant.App.Services;

/// <summary>
/// Finds installed RAM and the GPU with the most dedicated memory, to recommend a local model and predict where it runs.
/// Built-in graphics report only a small carve-out here (often 128-512 MB) and are otherwise picked out by name
/// (see <see cref="GraphicsAdapters"/>).
/// </summary>
public static class HardwareProbe
{
    // Display adapters device class.
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly Lazy<(HardwareProfile Profile, string? GpuName)> Cached = new(Probe);

    public static HardwareProfile Profile => Cached.Value.Profile;

    /// <summary>Name of the graphics card with the most memory, if one was found.</summary>
    public static string? GpuName => Cached.Value.GpuName;

    private static (HardwareProfile, string?) Probe()
    {
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long bestVram = 0;
        string? bestName = null;

        try
        {
            using var displayClass = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            foreach (var name in displayClass?.GetSubKeyNames() ?? [])
            {
                if (!int.TryParse(name, out _)) continue; // "0000", "0001", ... (skips "Properties")
                using var adapter = displayClass!.OpenSubKey(name);
                if (adapter is null) continue;

                // qwMemorySize (QWORD) is accurate above 4 GB; older drivers only write the 32-bit MemorySize.
                long vram = adapter.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long q => q,
                    byte[] { Length: >= 8 } b => BitConverter.ToInt64(b),
                    _ => adapter.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int d => (uint)d,
                        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b),
                        _ => 0,
                    },
                };
                if (vram > bestVram)
                {
                    bestVram = vram;
                    bestName = adapter.GetValue("DriverDesc") as string;
                }
                else if (bestVram == 0 && GraphicsAdapters.Classify(bestName, 0) == GraphicsKind.None)
                {
                    // Some built-in graphics report no dedicated memory at all; keep the name so Settings can still say what's there.
                    bestName = adapter.GetValue("DriverDesc") as string;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unknown GPU: recommendations fall back to the model that runs on any PC.
        }

        return (new HardwareProfile(ram, bestVram, bestName), bestName);
    }
}
