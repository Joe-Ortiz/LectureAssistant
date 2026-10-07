using System.Diagnostics;
using LectureAssistant.Core;
using LectureAssistant.Core.Captions;
using LectureAssistant.Core.Hardware;
using LectureAssistant.Core.Models;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace LectureAssistant.Transcription;

/// <summary>Local, offline transcription with whisper.cpp (via Whisper.net). Uses the GPU through Vulkan when available.</summary>
public sealed class WhisperTranscriber(Func<string> modelPathProvider) : ITranscriber, IReportsModelRun
{
    /// <summary>whisper.cpp's log, recorded only during a transcription to see whether it found a graphics card.</summary>
    private static readonly NativeLogRecorder NativeLog = new();
    private static readonly Lazy<IDisposable> LogHook = new(() =>
        LogProvider.AddLogger((level, message) => NativeLog.Write(message, level == WhisperLogLevel.Cont)));

    /// <summary>Vulkan devices ggml listed when it started; it only lists them once per process.</summary>
    private static IReadOnlyList<VulkanDevice> _vulkanDevices = [];

    /// <summary>Where the last successful transcription ran and how long it took (including loading the model).</summary>
    public ModelRun? LastRun { get; private set; }

    public async Task<IReadOnlyList<CaptionSegment>> TranscribeAsync(
        string wavPath,
        TranscriptionOptions options,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var modelPath = modelPathProvider();
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The speech recognition model hasn't been downloaded yet.", modelPath);

        var segments = new List<CaptionSegment>();
        double lastFraction = 0;
        var stopwatch = Stopwatch.StartNew();
        _ = LogHook.Value;
        using var runLog = NativeLog.Start();

        using var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true });
        var builder = factory.CreateBuilder()
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            // Per-token probabilities, so the instructor can be pointed at the words recognition was unsure of.
            .WithProbabilities()
            .WithProgressHandler(percent =>
            {
                lastFraction = percent / 100.0;
                progress?.Report(new TranscriptionProgress(lastFraction, null));
            });

        builder = string.IsNullOrWhiteSpace(options.Language) || options.Language == "auto"
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(options.Language);

        if (!string.IsNullOrWhiteSpace(options.Vocabulary))
            builder = builder.WithPrompt(options.Vocabulary).WithCarryInitialPrompt(true);

        await using var processor = builder.Build();
        await using var audio = File.OpenRead(wavPath);

        await foreach (var result in processor.ProcessAsync(audio, cancellationToken))
        {
            var text = result.Text.Trim();
            var uncertain = WordsToCheck.FindUnsure((result.Tokens ?? []).Select(t => (t.Text ?? "", t.Probability)), text);
            var segment = new CaptionSegment(result.Start, result.End, text) { UncertainWords = uncertain.Count > 0 ? uncertain : null };
            segments.Add(segment);
            progress?.Report(new TranscriptionProgress(lastFraction, segment));
        }

        progress?.Report(new TranscriptionProgress(1, null));
        // whisper.cpp picks its backend while loading the model and creating its state, which may wait for the first audio.
        LastRun = new ModelRun(DetectDevice(runLog.Stop()), stopwatch.Elapsed);
        return CaptionCleanup.Normalize(segments);
    }

    /// <summary>Which native backend Whisper.net loaded (e.g. Vulkan or CPU), for diagnostics.</summary>
    public static string? GetRuntimeInfo() => WhisperFactory.GetRuntimeInfo();

    private static ComputeDeviceReport DetectDevice(IReadOnlyList<string> loadLog)
    {
        var vulkanDevices = GgmlLog.ParseVulkanDevices(loadLog);
        if (vulkanDevices.Count > 0) _vulkanDevices = vulkanDevices;

        bool? gpuRuntime = RuntimeOptions.LoadedLibrary switch
        {
            RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 => true,
            RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => false,
            _ => null,
        };
        return GgmlLog.InterpretWhisper(loadLog, gpuRuntime, _vulkanDevices);
    }
}
