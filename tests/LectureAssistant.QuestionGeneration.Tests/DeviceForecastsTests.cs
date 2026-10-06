using LectureAssistant.Core.Hardware;
using LectureAssistant.QuestionGeneration.Local;

namespace LectureAssistant.QuestionGeneration.Tests;

public class DeviceForecastsTests
{
    private const long GiB = 1L << 30;
    private const int Automatic = -1;

    private static readonly HardwareProfile Rtx3070 = new(16 * GiB, 8 * GiB, "NVIDIA GeForce RTX 3070");
    private static readonly HardwareProfile Gtx1650 = new(16 * GiB, 4 * GiB, "NVIDIA GeForce GTX 1650");
    private static readonly HardwareProfile IrisXe = new(16 * GiB, 128 << 20, "Intel(R) Iris(R) Xe Graphics");
    private static readonly HardwareProfile NoGpu = new(8 * GiB, 0);

    [Fact]
    public void Model_that_fits_runs_on_the_graphics_card()
    {
        var forecast = DeviceForecasts.ForQuestionModel(Rtx3070, LocalModelCatalog.Best, Automatic);
        Assert.Equal(ComputeDevice.GraphicsCard, forecast.Device);
        Assert.Equal("Runs on your graphics card (NVIDIA GeForce RTX 3070, 8 GB video memory). Fast.", forecast.Text);
    }

    [Fact]
    public void Model_too_big_for_the_card_runs_on_the_processor()
    {
        var forecast = DeviceForecasts.ForQuestionModel(Gtx1650, LocalModelCatalog.Best, Automatic);
        Assert.Equal(ComputeDevice.Processor, forecast.Device);
        Assert.StartsWith("Your graphics card (NVIDIA GeForce GTX 1650, 4 GB video memory) is too small for this model (about 8 GB needed)", forecast.Text);
        Assert.Contains("system memory (16 GB)", forecast.Text);

        Assert.Equal(ComputeDevice.GraphicsCard, DeviceForecasts.ForQuestionModel(Gtx1650, LocalModelCatalog.Standard, Automatic).Device);
    }

    [Fact]
    public void No_graphics_card_runs_on_the_processor()
    {
        var forecast = DeviceForecasts.ForQuestionModel(NoGpu, LocalModelCatalog.Standard, Automatic);
        Assert.Equal(ComputeDevice.Processor, forecast.Device);
        Assert.Equal("Runs on the processor using system memory (8 GB), since no graphics card was found. Slower: expect a few minutes per lecture.", forecast.Text);
    }

    [Fact]
    public void Gpu_layers_zero_forces_the_processor()
    {
        var forecast = DeviceForecasts.ForQuestionModel(Rtx3070, LocalModelCatalog.Standard, 0);
        Assert.Equal(ComputeDevice.Processor, forecast.Device);
        Assert.Contains("GPU layers is set to 0", forecast.Text);
    }

    [Fact]
    public void A_layer_count_splits_the_model()
    {
        var forecast = DeviceForecasts.ForQuestionModel(Rtx3070, LocalModelCatalog.Best, 20);
        Assert.Equal(ComputeDevice.Split, forecast.Device);
        Assert.Contains("up to 20 layers on the graphics card", forecast.Text);
    }

    [Fact]
    public void Integrated_graphics_promise_no_speed()
    {
        var forecast = DeviceForecasts.ForQuestionModel(IrisXe, LocalModelCatalog.Standard, Automatic);
        Assert.Equal("Uses your built-in graphics (Intel Iris Xe Graphics) if it can, otherwise the processor. " +
                     "Both share system memory (16 GB), so expect a few minutes or more per lecture.", forecast.Text);
        Assert.DoesNotContain("Fast", forecast.Text);
    }

    [Fact]
    public void Custom_model_needs_are_estimated_from_file_size()
    {
        // Within a few percent of the built-in models' published needs.
        Assert.InRange(DeviceForecasts.EstimateGpuMemoryBytes(LocalModelCatalog.Standard.SizeBytes), 3_900_000_000, 4_400_000_000);
        Assert.InRange(DeviceForecasts.EstimateGpuMemoryBytes(LocalModelCatalog.Best.SizeBytes), 7_600_000_000, 8_100_000_000);

        Assert.Equal(ComputeDevice.GraphicsCard, DeviceForecasts.ForCustomQuestionModel(Rtx3070, 4_000_000_000, Automatic).Device);
        Assert.Equal(ComputeDevice.Processor, DeviceForecasts.ForCustomQuestionModel(Rtx3070, 9_000_000_000, Automatic).Device);
    }

    [Fact]
    public void Speech_model_forecast()
    {
        Assert.Equal("Runs on your graphics card (NVIDIA GeForce RTX 3070). Fast.", DeviceForecasts.ForSpeechModel(Rtx3070).Text);
        Assert.StartsWith("Uses your built-in graphics (Intel Iris Xe Graphics) if it can", DeviceForecasts.ForSpeechModel(IrisXe).Text);
        Assert.Equal(ComputeDevice.Processor, DeviceForecasts.ForSpeechModel(NoGpu).Device);
    }

    [Fact]
    public void Hardware_summary()
    {
        Assert.Equal("This PC: 16 GB memory, NVIDIA GeForce RTX 3070 with 8 GB video memory.", DeviceForecasts.DescribeHardware(Rtx3070));
        Assert.Equal("This PC: 16 GB memory, Intel Iris Xe Graphics (built into the processor; shares system memory).", DeviceForecasts.DescribeHardware(IrisXe));
        Assert.Equal("This PC: 8 GB memory.", DeviceForecasts.DescribeHardware(NoGpu));
    }

    [Fact]
    public void Recommended_model_on_the_processor_isnt_flagged_but_a_bigger_one_is()
    {
        Assert.Null(LocalModelCatalog.HardwareWarning(LocalModelCatalog.Standard, NoGpu with { SystemMemoryBytes = 16 * GiB }));
        Assert.Contains("Standard model is quicker", LocalModelCatalog.HardwareWarning(LocalModelCatalog.Best, Gtx1650));
        Assert.Null(LocalModelCatalog.HardwareWarning(LocalModelCatalog.Best, Rtx3070));
    }
}
