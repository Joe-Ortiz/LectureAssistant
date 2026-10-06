using LectureAssistant.Core.Hardware;

namespace LectureAssistant.QuestionGeneration.Local;

/// <summary>A plain-language forecast of where a model will run on this PC, shown in Settings before it runs.</summary>
public sealed record DeviceForecast(ComputeDevice Device, string Text);

/// <summary>
/// Predicts from the hardware Windows reports whether a model runs on the graphics card (video memory) or the
/// processor (system memory). The runtime has the final say; see <see cref="GgmlLog"/> for what actually happened.
/// </summary>
public static class DeviceForecasts
{
    private const long GB = 1_000_000_000;

    /// <summary>
    /// For a built-in model. <paramref name="gpuLayersSetting"/> is the Settings value: negative = automatic
    /// (everything on the graphics card when there is one), 0 = processor only, otherwise a layer count.
    /// </summary>
    public static DeviceForecast ForQuestionModel(HardwareProfile hardware, LocalModelInfo model, int gpuLayersSetting) =>
        ForQuestionModel(hardware, model.MinGpuMemoryBytes, gpuLayersSetting);

    /// <summary>For the instructor's own .gguf file, whose memory needs are estimated from its size.</summary>
    public static DeviceForecast ForCustomQuestionModel(HardwareProfile hardware, long fileBytes, int gpuLayersSetting) =>
        ForQuestionModel(hardware, EstimateGpuMemoryBytes(fileBytes), gpuLayersSetting);

    /// <summary>
    /// Video memory to hold a .gguf entirely on the GPU: the weights plus room for the context cache and working
    /// buffers. Matches the built-in models' figures (2.7 GB file → 4 GB, 5.7 GB → 8 GB) to within a few percent.
    /// </summary>
    public static long EstimateGpuMemoryBytes(long fileBytes) => fileBytes + fileBytes / 5 + GB;

    public static DeviceForecast ForQuestionModel(HardwareProfile hardware, long minGpuMemoryBytes, int gpuLayersSetting)
    {
        var ram = MemoryText(hardware.SystemMemoryBytes);
        var slow = minGpuMemoryBytes > 5 * GB ? "Slow: expect several minutes or more per lecture." : "Slower: expect a few minutes per lecture.";
        var kind = GraphicsAdapters.Classify(hardware.GpuName, hardware.GpuMemoryBytes);

        if (gpuLayersSetting == 0)
            return new(ComputeDevice.Processor, $"Runs on the processor using system memory ({ram}), because GPU layers is set to 0 under Advanced. {slow}");
        if (kind == GraphicsKind.None)
            return new(ComputeDevice.Processor, $"Runs on the processor using system memory ({ram}), since no graphics card was found. {slow}");

        if (gpuLayersSetting > 0 && gpuLayersSetting < 999)
            return new(ComputeDevice.Split,
                $"Split between {Gpu(hardware, kind)} and the processor: up to {gpuLayersSetting} layers on the graphics card, as set under Advanced.");

        if (kind == GraphicsKind.Integrated)
        {
            // llama.cpp uses built-in graphics through Vulkan when there's no separate card, but they share memory
            // bandwidth with the processor, so it isn't reliably faster. Don't promise speed.
            return new(ComputeDevice.GraphicsCard,
                $"Uses {Gpu(hardware, kind)} if it can, otherwise the processor. Both share system memory ({ram}), so expect a few minutes or more per lecture.");
        }

        if (hardware.GpuMemoryBytes >= minGpuMemoryBytes)
            return new(ComputeDevice.GraphicsCard, $"Runs on {Gpu(hardware, kind)}. Fast.");

        // All layers go to the GPU or none do: if the model doesn't fit, loading fails and the app retries on the processor.
        return new(ComputeDevice.Processor,
            $"{Capitalize(Gpu(hardware, kind))} is too small for this model (about {minGpuMemoryBytes / (double)GB:0} GB needed), " +
            $"so it will most likely run on the processor using system memory ({ram}). {slow}");
    }

    /// <summary>Whisper (captions). Its models need 1–2 GB, which any graphics card has, but built-in graphics may not be used.</summary>
    public static DeviceForecast ForSpeechModel(HardwareProfile hardware) =>
        GraphicsAdapters.Classify(hardware.GpuName, hardware.GpuMemoryBytes) switch
        {
            GraphicsKind.Dedicated => new(ComputeDevice.GraphicsCard, $"Runs on {Gpu(hardware, GraphicsKind.Dedicated, withMemory: false)}. Fast."),
            GraphicsKind.Integrated => new(ComputeDevice.GraphicsCard,
                $"Uses {Gpu(hardware, GraphicsKind.Integrated)} if it can, otherwise the processor. A long lecture can take a while."),
            _ => new(ComputeDevice.Processor, "Runs on the processor, since no graphics card was found. Slower: a long lecture can take a while."),
        };

    /// <summary>"This PC: 16 GB memory, NVIDIA GeForce RTX 3070 with 8 GB video memory."</summary>
    public static string DescribeHardware(HardwareProfile hardware)
    {
        var text = $"This PC: {MemoryText(hardware.SystemMemoryBytes)} memory";
        var name = hardware.GpuName is { Length: > 0 } n ? GraphicsAdapters.FriendlyName(n) : null;
        return GraphicsAdapters.Classify(hardware.GpuName, hardware.GpuMemoryBytes) switch
        {
            GraphicsKind.Dedicated => text + $", {name ?? "a graphics card"} with {MemoryText(hardware.GpuMemoryBytes)} video memory.",
            GraphicsKind.Integrated => text + $", {name ?? "built-in graphics"} (built into the processor; shares system memory).",
            _ => text + ".",
        };
    }

    /// <summary>"your graphics card (NVIDIA GeForce RTX 3070, 8 GB video memory)" or "your built-in graphics (Intel Iris Xe Graphics)".</summary>
    private static string Gpu(HardwareProfile hardware, GraphicsKind kind, bool withMemory = true)
    {
        var details = new List<string>();
        if (hardware.GpuName is { Length: > 0 } name) details.Add(GraphicsAdapters.FriendlyName(name));
        if (kind == GraphicsKind.Dedicated && withMemory && hardware.GpuMemoryBytes > 0) details.Add($"{MemoryText(hardware.GpuMemoryBytes)} video memory");
        var what = kind == GraphicsKind.Integrated ? "your built-in graphics" : "your graphics card";
        return details.Count > 0 ? $"{what} ({string.Join(", ", details)})" : what;
    }

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Installed memory as Windows shows it: binary gigabytes, rounded (16 GB, 8 GB).</summary>
    private static string MemoryText(long bytes) =>
        bytes >= 1L << 30 ? $"{Math.Round(bytes / 1073741824.0):0} GB" : $"{bytes / (1L << 20)} MB";
}
