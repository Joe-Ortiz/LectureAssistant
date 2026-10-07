namespace LectureAssistant.Core.Hardware;

/// <summary>Where a local AI model did its work.</summary>
public enum ComputeDevice
{
    /// <summary>The runtime didn't say.</summary>
    Unknown,

    /// <summary>Entirely on a graphics card (its video memory, or shared memory for built-in graphics).</summary>
    GraphicsCard,

    /// <summary>Some layers on the graphics card, the rest on the processor.</summary>
    Split,

    /// <summary>On the processor, in system memory.</summary>
    Processor,
}

/// <summary>Where a local model actually ran, as worked out from its runtime after loading.</summary>
/// <param name="GpuName">The graphics card the runtime used, when it said (already cleaned up for display).</param>
/// <param name="IsIntegrated">The graphics is built into the processor and shares system memory.</param>
/// <param name="LayersOnGpu">Model layers placed on the graphics card, when llama.cpp reported it.</param>
/// <param name="TotalLayers">The model's layer count, when llama.cpp reported it.</param>
/// <param name="Reason">Why the processor was used, in plain words (e.g. "no graphics card was usable").</param>
public sealed record ComputeDeviceReport(
    ComputeDevice Device,
    string? GpuName = null,
    bool IsIntegrated = false,
    int? LayersOnGpu = null,
    int? TotalLayers = null,
    string? Reason = null)
{
    public const string NoUsableGpu = "no graphics card was usable";
    public const string GpuFailed = "the graphics card couldn't run it";
    public const string GpuLayersZero = "GPU layers is set to 0";

    public static ComputeDeviceReport Unknown { get; } = new(ComputeDevice.Unknown);

    /// <summary>Completes "on …": "your graphics card (NVIDIA GeForce RTX 3070)", "the processor (no graphics card was usable)".</summary>
    public string Describe()
    {
        var gpu = (IsIntegrated ? "your built-in graphics" : "your graphics card") + (GpuName is { Length: > 0 } ? $" ({GpuName})" : "");
        return Device switch
        {
            ComputeDevice.GraphicsCard => gpu,
            ComputeDevice.Split => gpu + " and the processor" +
                (LayersOnGpu is { } on && TotalLayers is { } total ? $" ({on} of {total} layers on the graphics card)" : ""),
            ComputeDevice.Processor => "the processor" + (Reason is { Length: > 0 } ? $" ({Reason})" : ""),
            _ => "this PC",
        };
    }

    /// <summary>Shown under the progress bar while the model works.</summary>
    public string StatusText => Device switch
    {
        ComputeDevice.Processor => $"Running on {Describe()}, which is slower than a graphics card.",
        ComputeDevice.Unknown => "",
        _ => $"Running on {Describe()}.",
    };
}

/// <summary>Where and how quickly a local model last ran, remembered in settings for the instructor.</summary>
/// <param name="ModelName">Filled in by the app, e.g. "Standard model".</param>
public sealed record ModelRun(ComputeDeviceReport Where, TimeSpan Duration, string? ModelName = null)
{
    /// <summary>"Standard model on your graphics card (NVIDIA GeForce RTX 3070), 34 seconds".</summary>
    public string Describe() =>
        (ModelName is { Length: > 0 } ? $"{ModelName} on " : "On ") + Where.Describe() + ", " + FormatDuration(Duration);

    public static string FormatDuration(TimeSpan t)
    {
        var seconds = (int)Math.Round(t.TotalSeconds);
        if (seconds < 60) return seconds == 1 ? "1 second" : $"{seconds} seconds";
        if (seconds < 3600) return seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds / 60} min {seconds % 60} s";
        var minutes = (int)Math.Round(t.TotalMinutes);
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }
}

/// <summary>A local model that can say where its last successful run happened.</summary>
public interface IReportsModelRun
{
    ModelRun? LastRun { get; }
}
