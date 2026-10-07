using System.Text.Json;
using LectureAssistant.Core.Hardware;
using LectureAssistant.Core.Persistence;

namespace LectureAssistant.Core.Tests;

public class GgmlLogTests
{
    // Captured from LLamaSharp 0.27.0 loading Qwen 3.5 4B with 999 GPU layers on a Quadro P2000 (excerpt).
    private static readonly string[] LlamaGpuLog =
    [
        "ggml_vulkan: Found 1 Vulkan devices:",
        "ggml_vulkan: 0 = Quadro P2000 (NVIDIA) | uma: 0 | fp16: 0 | bf16: 0 | warp size: 32 | shared memory: 49152 | int dot: 1 | matrix cores: none",
        "llama_model_load_from_file_impl: using device Vulkan0 (Quadro P2000) (0000:65:00.0) - 4450 MiB free",
        "load_tensors: layer   0 assigned to device Vulkan0, is_swa = 0",
        "load_tensors: offloading output layer to GPU",
        "load_tensors: offloading 31 repeating layers to GPU",
        "load_tensors: offloaded 33/33 layers to GPU",
        "llama_kv_cache:    Vulkan0 KV buffer size =   128.00 MiB",
        "sched_reserve:    Vulkan0 compute buffer size =   490.00 MiB",
    ];

    [Fact]
    public void Vulkan_devices_are_parsed_with_their_shared_memory_flag()
    {
        var devices = GgmlLog.ParseVulkanDevices(
        [
            ..LlamaGpuLog,
            "ggml_vulkan: 1 = Intel(R) Iris(R) Xe Graphics (Intel Corporation) | uma: 1 | fp16: 1 | warp size: 32",
        ]);

        Assert.Equal(
        [
            new VulkanDevice(0, "Quadro P2000", false),
            new VulkanDevice(1, "Intel Iris Xe Graphics", true),
        ], devices);
    }

    [Fact]
    public void Llama_load_reports_offloaded_layers_and_device()
    {
        var log = GgmlLog.ParseLlamaLoad(LlamaGpuLog);
        Assert.Equal(33, log.LayersOnGpu);
        Assert.Equal(33, log.TotalLayers);
        Assert.Equal([new RuntimeDevice("Vulkan0", "Quadro P2000")], log.Devices);
    }

    [Fact]
    public void Older_device_line_without_bus_id_and_names_with_parentheses_parse()
    {
        var log = GgmlLog.ParseLlamaLoad(["llama_model_load_from_file_impl: using device Vulkan0 (Intel(R) Arc(TM) Graphics) - 15000 MiB free"]);
        Assert.Equal([new RuntimeDevice("Vulkan0", "Intel Arc Graphics")], log.Devices);
    }

    [Fact]
    public void All_layers_on_gpu_is_graphics_card()
    {
        var report = GgmlLog.InterpretLlamaLoad(GgmlLog.ParseLlamaLoad(LlamaGpuLog), 999, true, []);
        Assert.Equal(new ComputeDeviceReport(ComputeDevice.GraphicsCard, "Quadro P2000", false, 33, 33), report);
        Assert.Equal("your graphics card (Quadro P2000)", report.Describe());
    }

    [Fact]
    public void Some_layers_on_gpu_is_split()
    {
        var report = GgmlLog.InterpretLlamaLoad(GgmlLog.ParseLlamaLoad(["load_tensors: offloaded 10/33 layers to GPU"]), 10, true, []);
        Assert.Equal(ComputeDevice.Split, report.Device);
        Assert.Equal("your graphics card and the processor (10 of 33 layers on the graphics card)", report.Describe());
    }

    [Fact]
    public void No_layers_offloaded_means_no_usable_gpu()
    {
        var report = GgmlLog.InterpretLlamaLoad(GgmlLog.ParseLlamaLoad(["load_tensors: offloaded 0/33 layers to GPU"]), 999, false, []);
        Assert.Equal(ComputeDevice.Processor, report.Device);
        Assert.Equal("the processor (no graphics card was usable)", report.Describe());
    }

    [Fact]
    public void Zero_requested_layers_is_the_processor_whatever_the_log_says()
    {
        var report = GgmlLog.InterpretLlamaLoad(GgmlLog.ParseLlamaLoad(LlamaGpuLog), 0, true, []);
        Assert.Equal(ComputeDevice.Processor, report.Device);
        Assert.Equal(ComputeDeviceReport.GpuLayersZero, report.Reason);
    }

    [Theory]
    [InlineData(true, ComputeDevice.GraphicsCard)]
    [InlineData(false, ComputeDevice.Processor)]
    public void Without_log_lines_the_runtime_decides(bool gpuOffloadSupported, ComputeDevice expected)
    {
        var report = GgmlLog.InterpretLlamaLoad(GgmlLog.ParseLlamaLoad([]), 999, gpuOffloadSupported, []);
        Assert.Equal(expected, report.Device);
    }

    [Fact]
    public void Integrated_graphics_are_recognized_from_the_vulkan_list_seen_on_an_earlier_load()
    {
        VulkanDevice[] known = [new(0, "AMD Radeon 780M Graphics", true)];
        var log = GgmlLog.ParseLlamaLoad(
        [
            "llama_model_load_from_file_impl: using device Vulkan0 (AMD Radeon(TM) 780M Graphics) - 8000 MiB free",
            "load_tensors: offloaded 37/37 layers to GPU",
        ]);
        var report = GgmlLog.InterpretLlamaLoad(log, 999, true, known);
        Assert.True(report.IsIntegrated);
        Assert.Equal("your built-in graphics (AMD Radeon 780M Graphics)", report.Describe());
    }

    [Fact]
    public void Integrated_graphics_fall_back_to_the_name_when_vulkan_list_is_unknown()
    {
        var log = GgmlLog.ParseLlamaLoad(
        [
            "llama_model_load_from_file_impl: using device Vulkan0 (Intel(R) UHD Graphics 620) - 4000 MiB free",
            "load_tensors: offloaded 37/37 layers to GPU",
        ]);
        Assert.True(GgmlLog.InterpretLlamaLoad(log, 999, true, []).IsIntegrated);
    }

    [Fact]
    public void Whisper_gpu_backend_is_graphics_card_named_from_vulkan_list()
    {
        // Captured from Whisper.net 1.9.1 (Vulkan runtime) loading large-v3-turbo on a Quadro P2000 (excerpt).
        string[] log =
        [
            "whisper_init_with_params_no_state: use gpu    = 1",
            "whisper_init_with_params_no_state: gpu_device = 0",
            "ggml_vulkan: 0 = Quadro P2000 (NVIDIA) | uma: 0 | fp16: 0 | bf16: 0 | warp size: 32 | shared memory: 49152 | int dot: 1 | matrix cores: none",
            "whisper_model_load:      Vulkan0 total size =   573.40 MB",
            "whisper_backend_init_gpu: device 0: Vulkan0 (type: 1)",
            "whisper_backend_init_gpu: found GPU device 0: Vulkan0 (type: 1, cnt: 0)",
            "whisper_backend_init_gpu: using Vulkan0 backend",
        ];
        var report = GgmlLog.InterpretWhisper(log, true, GgmlLog.ParseVulkanDevices(log));
        Assert.Equal(new ComputeDeviceReport(ComputeDevice.GraphicsCard, "Quadro P2000"), report);
    }

    [Fact]
    public void Whisper_integrated_device_type_marks_built_in_graphics()
    {
        string[] log = ["whisper_backend_init_gpu: device 0: Vulkan0 (type: 2)", "whisper_backend_init_gpu: using Vulkan0 backend"];
        Assert.True(GgmlLog.InterpretWhisper(log, true, []).IsIntegrated);
    }

    [Fact]
    public void Whisper_without_gpu_is_processor()
    {
        var report = GgmlLog.InterpretWhisper(["whisper_backend_init_gpu: no GPU found", "whisper_model_load:      CPU total size =   573.40 MB"], true, []);
        Assert.Equal(ComputeDevice.Processor, report.Device);
        Assert.Equal(ComputeDeviceReport.NoUsableGpu, report.Reason);
    }

    [Fact]
    public void Whisper_cpu_runtime_is_processor_and_silence_is_unknown()
    {
        Assert.Equal(ComputeDevice.Processor, GgmlLog.InterpretWhisper([], false, []).Device);
        Assert.Equal(ComputeDevice.Unknown, GgmlLog.InterpretWhisper([], true, []).Device);
    }
}

public class GraphicsAdaptersTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070", 8L << 30, GraphicsKind.Dedicated)]
    [InlineData("NVIDIA GeForce GTX 1650", 4L << 30, GraphicsKind.Dedicated)]
    [InlineData("AMD Radeon RX 7800 XT", 16L << 30, GraphicsKind.Dedicated)]
    [InlineData("AMD Radeon RX 6800M", 12L << 30, GraphicsKind.Dedicated)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", 16L << 30, GraphicsKind.Dedicated)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", 12L << 30, GraphicsKind.Dedicated)]
    [InlineData("Intel(R) UHD Graphics 620", 128L << 20, GraphicsKind.Integrated)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", 128L << 20, GraphicsKind.Integrated)]
    [InlineData("Intel(R) Arc(TM) Graphics", 128L << 20, GraphicsKind.Integrated)]
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)", 128L << 20, GraphicsKind.Integrated)]
    [InlineData("AMD Radeon(TM) Graphics", 512L << 20, GraphicsKind.Integrated)]
    [InlineData("AMD Radeon(TM) 780M Graphics", 4L << 30, GraphicsKind.Integrated)]
    [InlineData("AMD Radeon RX Vega 11 Graphics", 2L << 30, GraphicsKind.Integrated)]
    [InlineData("AMD Radeon 890M", 4L << 30, GraphicsKind.Integrated)]
    [InlineData("Qualcomm(R) Adreno(TM) X1-85 GPU", 0, GraphicsKind.Integrated)]
    [InlineData("Microsoft Basic Display Adapter", 0, GraphicsKind.None)]
    [InlineData("Parsec Virtual Display Adapter", 0, GraphicsKind.None)]
    [InlineData(null, 0, GraphicsKind.None)]
    [InlineData("Some Future GPU", 12L << 30, GraphicsKind.Dedicated)]
    [InlineData("Some Future GPU", 256L << 20, GraphicsKind.Integrated)]
    public void Classifies_common_adapters(string? name, long memory, GraphicsKind expected) =>
        Assert.Equal(expected, GraphicsAdapters.Classify(name, memory));

    [Fact]
    public void Friendly_name_drops_trademark_marks() =>
        Assert.Equal("Intel Iris Xe Graphics", GraphicsAdapters.FriendlyName("Intel(R) Iris(R)  Xe Graphics"));
}

public class ModelRunTests
{
    [Theory]
    [InlineData(0.4, "0 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(34.4, "34 seconds")]
    [InlineData(120, "2 min")]
    [InlineData(130, "2 min 10 s")]
    [InlineData(3900, "1 h 5 min")]
    public void Durations_read_naturally(double seconds, string expected) =>
        Assert.Equal(expected, ModelRun.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Describes_last_run_for_settings()
    {
        var run = new ModelRun(new ComputeDeviceReport(ComputeDevice.Processor, Reason: ComputeDeviceReport.GpuFailed), TimeSpan.FromSeconds(372), "Best quality model");
        Assert.Equal("Best quality model on the processor (the graphics card couldn't run it), 6 min 12 s", run.Describe());
    }

    [Fact]
    public void Status_text_says_where_the_model_runs()
    {
        Assert.Equal("Running on your built-in graphics (Intel Iris Xe Graphics).",
            new ComputeDeviceReport(ComputeDevice.GraphicsCard, "Intel Iris Xe Graphics", IsIntegrated: true).StatusText);
        Assert.Equal("Running on the processor (no graphics card was usable), which is slower than a graphics card.",
            new ComputeDeviceReport(ComputeDevice.Processor, Reason: ComputeDeviceReport.NoUsableGpu).StatusText);
        Assert.Equal("", ComputeDeviceReport.Unknown.StatusText);
    }

    [Fact]
    public void Survives_a_settings_round_trip()
    {
        var run = new ModelRun(new ComputeDeviceReport(ComputeDevice.GraphicsCard, "NVIDIA GeForce RTX 3070", false, 37, 37), TimeSpan.FromSeconds(34.5), "Standard model");
        var json = JsonSerializer.Serialize(run, ProjectStore.JsonOptions);
        Assert.Contains("\"GraphicsCard\"", json);
        Assert.Equal(run, JsonSerializer.Deserialize<ModelRun>(json, ProjectStore.JsonOptions));
    }
}

public class NativeLogRecorderTests
{
    [Fact]
    public void Records_only_while_started_and_joins_continued_messages()
    {
        var recorder = new NativeLogRecorder();
        recorder.Write("before\n");

        var recording = recorder.Start();
        recorder.Write("load_tensors: offloaded ");
        recorder.Write("33/33 layers to GPU\n", continuation: true);
        recorder.Write("no newline at the end");
        recorder.Write("next message\n");
        var lines = recording.Stop();
        recorder.Write("after\n");

        Assert.Equal(["load_tensors: offloaded 33/33 layers to GPU", "no newline at the end", "next message"], lines);
    }
}
