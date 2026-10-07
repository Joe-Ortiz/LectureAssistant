using System.Globalization;
using System.Text.RegularExpressions;

namespace LectureAssistant.Core.Hardware;

/// <summary>A Vulkan device as ggml lists it when its runtime starts.</summary>
/// <param name="SharedMemory">ggml's "uma" flag: built-in graphics that use system memory.</param>
public sealed record VulkanDevice(int Index, string Name, bool SharedMemory);

/// <summary>A device llama.cpp chose for a model ("Vulkan0", "NVIDIA GeForce RTX 3070").</summary>
public sealed record RuntimeDevice(string Id, string Name);

/// <summary>What llama.cpp said about where a model's layers went while loading it.</summary>
public sealed record LlamaLoadLog(int? LayersOnGpu, int? TotalLayers, IReadOnlyList<RuntimeDevice> Devices);

/// <summary>
/// Reads the log lines llama.cpp and whisper.cpp (both built on ggml) print while loading a model, to tell whether it
/// ended up on a graphics card. The lines are informational and may change between versions, so every pattern is
/// optional and callers fall back to what the runtime API says.
/// </summary>
public static partial class GgmlLog
{
    /// <summary>
    /// "ggml_vulkan: 0 = NVIDIA GeForce RTX 3070 (NVIDIA) | uma: 0 | fp16: 1 | ...". Printed once per process,
    /// when the Vulkan backend starts, so callers should keep the result for later loads.
    /// </summary>
    public static IReadOnlyList<VulkanDevice> ParseVulkanDevices(IEnumerable<string> lines) =>
        lines.Select(l => VulkanDeviceLine().Match(l))
            .Where(m => m.Success)
            .Select(m => new VulkanDevice(
                int.Parse(m.Groups["index"].Value, CultureInfo.InvariantCulture),
                GraphicsAdapters.FriendlyName(m.Groups["name"].Value),
                m.Groups["uma"].Value == "1"))
            .ToList();

    /// <summary>
    /// "llama_model_load_from_file_impl: using device Vulkan0 (NVIDIA GeForce RTX 3070) - 7187 MiB free" and
    /// "load_tensors: offloaded 33/33 layers to GPU".
    /// </summary>
    public static LlamaLoadLog ParseLlamaLoad(IEnumerable<string> lines)
    {
        int? onGpu = null, total = null;
        var devices = new List<RuntimeDevice>();
        foreach (var line in lines)
        {
            if (LlamaDeviceLine().Match(line) is { Success: true } d)
            {
                var device = new RuntimeDevice(d.Groups["id"].Value, GraphicsAdapters.FriendlyName(d.Groups["name"].Value));
                if (!devices.Contains(device)) devices.Add(device);
            }
            else if (LlamaOffloadLine().Match(line) is { Success: true } o)
            {
                onGpu = int.Parse(o.Groups["on"].Value, CultureInfo.InvariantCulture);
                total = int.Parse(o.Groups["total"].Value, CultureInfo.InvariantCulture);
            }
        }
        return new LlamaLoadLog(onGpu, total, devices);
    }

    /// <summary>Where a llama.cpp model ran.</summary>
    /// <param name="requestedGpuLayers">The layer count the app asked for (0 = processor only).</param>
    /// <param name="gpuOffloadSupported">
    /// The loaded runtime has a usable GPU backend (llama_supports_gpu_offload); only consulted when the log didn't say.
    /// </param>
    /// <param name="vulkanDevices">Devices seen at runtime start, to tell built-in graphics from a graphics card.</param>
    public static ComputeDeviceReport InterpretLlamaLoad(
        LlamaLoadLog log, int requestedGpuLayers, bool gpuOffloadSupported, IReadOnlyList<VulkanDevice> vulkanDevices)
    {
        if (requestedGpuLayers <= 0) return new(ComputeDevice.Processor, Reason: ComputeDeviceReport.GpuLayersZero);

        var (name, integrated) = Identify(log.Devices, vulkanDevices);
        if (log.LayersOnGpu is { } onGpu && log.TotalLayers is { } total and > 0)
        {
            if (onGpu <= 0) return new(ComputeDevice.Processor, Reason: ComputeDeviceReport.NoUsableGpu);
            return new(onGpu >= total ? ComputeDevice.GraphicsCard : ComputeDevice.Split, name, integrated, onGpu, total);
        }

        // No offload line (log format changed?): llama.cpp puts the requested layers on the GPU whenever it has one.
        return gpuOffloadSupported
            ? new(ComputeDevice.GraphicsCard, name, integrated)
            : new(ComputeDevice.Processor, Reason: ComputeDeviceReport.NoUsableGpu);
    }

    /// <summary>Where a whisper.cpp model ran.</summary>
    /// <param name="gpuRuntimeLoaded">Whisper.net loaded its Vulkan build (true), a CPU build (false), or unknown (null).</param>
    public static ComputeDeviceReport InterpretWhisper(
        IEnumerable<string> lines, bool? gpuRuntimeLoaded, IReadOnlyList<VulkanDevice> vulkanDevices)
    {
        if (gpuRuntimeLoaded == false) return new(ComputeDevice.Processor, Reason: ComputeDeviceReport.NoUsableGpu);

        string? gpuId = null;
        var integratedIds = new HashSet<string>();
        foreach (var line in lines)
        {
            if (line.Contains("whisper_backend_init_gpu: no GPU found", StringComparison.Ordinal))
                return new(ComputeDevice.Processor, Reason: ComputeDeviceReport.NoUsableGpu);
            if (WhisperDeviceTypeLine().Match(line) is { Success: true } t && t.Groups["type"].Value == IntegratedGpuType)
                integratedIds.Add(t.Groups["id"].Value);
            var m = WhisperGpuLine().Match(line);
            if (!m.Success) m = WhisperBufferLine().Match(line);
            if (m.Success && !m.Groups["id"].Value.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))
                gpuId ??= m.Groups["id"].Value;
        }
        if (gpuId is null) return ComputeDeviceReport.Unknown;

        var vulkan = IndexOf(gpuId) is { } index ? vulkanDevices.FirstOrDefault(v => v.Index == index) : null;
        return new(ComputeDevice.GraphicsCard, vulkan?.Name, vulkan?.SharedMemory ?? integratedIds.Contains(gpuId));
    }

    /// <summary>ggml_backend_dev_type: CPU = 0, GPU = 1, IGPU (built-in graphics) = 2, ACCEL = 3.</summary>
    private const string IntegratedGpuType = "2";

    private static (string? Name, bool Integrated) Identify(IReadOnlyList<RuntimeDevice> devices, IReadOnlyList<VulkanDevice> vulkanDevices)
    {
        if (devices.Count == 0) return (null, false);
        bool integrated = devices.All(d =>
        {
            var vulkan = vulkanDevices.FirstOrDefault(v => v.Index == IndexOf(d.Id) || v.Name == d.Name);
            return vulkan?.SharedMemory ?? GraphicsAdapters.ClassifyByName(d.Name) == GraphicsKind.Integrated;
        });
        return (string.Join(" + ", devices.Select(d => d.Name).Distinct()), integrated);
    }

    /// <summary>"Vulkan1" → 1.</summary>
    private static int? IndexOf(string deviceId) =>
        TrailingNumber().Match(deviceId) is { Success: true } m ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;

    // The name may itself contain parentheses ("Intel(R) Iris(R) Xe Graphics"), hence the lazy match anchored on what follows.
    [GeneratedRegex(@"ggml_vulkan: (?<index>\d+) = (?<name>.+?) \([^()]*\) \| uma: (?<uma>\d)")]
    private static partial Regex VulkanDeviceLine();

    // Newer llama.cpp adds the PCI bus id in a second pair of parentheses.
    [GeneratedRegex(@"using device (?<id>\S+) \((?<name>.+?)\)(?: \([^()]*\))? - \d+ MiB free")]
    private static partial Regex LlamaDeviceLine();

    [GeneratedRegex(@"offloaded (?<on>\d+)/(?<total>\d+) layers to GPU")]
    private static partial Regex LlamaOffloadLine();

    [GeneratedRegex(@"whisper_backend_init_gpu: using (?<id>\S+) backend")]
    private static partial Regex WhisperGpuLine();

    [GeneratedRegex(@"whisper_backend_init_gpu: device \d+: (?<id>\S+) \(type: (?<type>\d+)\)")]
    private static partial Regex WhisperDeviceTypeLine();

    [GeneratedRegex(@"whisper_model_load:\s+(?<id>\S+) total size")]
    private static partial Regex WhisperBufferLine();

    [GeneratedRegex(@"\d+$")]
    private static partial Regex TrailingNumber();
}
