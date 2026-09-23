using LectureAssistant.Core.Downloads;

namespace LectureAssistant.QuestionGeneration.Local;

/// <summary>A model the app can download and run itself. Files are pinned to a commit and verified by SHA-256.</summary>
/// <param name="MinGpuMemoryBytes">Video memory needed to run entirely on the GPU (weights plus working memory).</param>
/// <param name="MinSystemMemoryBytes">System memory needed to run on the CPU.</param>
public sealed record LocalModelInfo(
    string Id,
    string DisplayName,
    string Description,
    Uri DownloadUrl,
    string FileName,
    long SizeBytes,
    string Sha256,
    int ContextSize,
    long MinGpuMemoryBytes,
    long MinSystemMemoryBytes,
    string License)
{
    public string SizeText => DownloadProgress.FormatBytes(SizeBytes);
}

/// <param name="SystemMemoryBytes">Installed RAM.</param>
/// <param name="GpuMemoryBytes">Dedicated video memory of the best GPU, or 0 if none was found.</param>
public sealed record HardwareProfile(long SystemMemoryBytes, long GpuMemoryBytes);

public static class LocalModelCatalog
{
    private const long GB = 1_000_000_000;

    public static readonly LocalModelInfo Standard = new(
        "qwen3.5-4b-q4km",
        "Standard",
        "Qwen 3.5 4B. Runs on most PCs, including laptops without a graphics card.",
        new Uri("https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/e87f176479d0855a907a41277aca2f8ee7a09523/Qwen3.5-4B-Q4_K_M.gguf"),
        "Qwen3.5-4B-Q4_K_M.gguf",
        2_740_937_888,
        "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
        ContextSize: 16384,
        MinGpuMemoryBytes: 4 * GB,
        MinSystemMemoryBytes: 8 * GB,
        License: "Apache 2.0 (Qwen team, Alibaba Cloud)");

    public static readonly LocalModelInfo Best = new(
        "qwen3.5-9b-q4km",
        "Best quality",
        "Qwen 3.5 9B. Writes better questions; needs a graphics card with 8 GB or more to be quick.",
        new Uri("https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/3885219b6810b007914f3a7950a8d1b469d598a5/Qwen3.5-9B-Q4_K_M.gguf"),
        "Qwen3.5-9B-Q4_K_M.gguf",
        5_680_522_464,
        "03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8",
        ContextSize: 16384,
        MinGpuMemoryBytes: 8 * GB,
        MinSystemMemoryBytes: 16 * GB,
        License: "Apache 2.0 (Qwen team, Alibaba Cloud)");

    public static IReadOnlyList<LocalModelInfo> All { get; } = [Standard, Best];

    public static LocalModelInfo? Find(string? id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// The best model this PC runs at a comfortable speed. The larger model only on a GPU that can hold it:
    /// on the CPU it technically works but takes long enough that instructors would think the app hung.
    /// </summary>
    public static LocalModelInfo Recommend(HardwareProfile hardware) =>
        hardware.GpuMemoryBytes >= Best.MinGpuMemoryBytes && hardware.SystemMemoryBytes >= Standard.MinSystemMemoryBytes
            ? Best
            : Standard;

    /// <summary>A warning when the PC is below what the model needs, or null when it should run fine.</summary>
    public static string? HardwareWarning(LocalModelInfo model, HardwareProfile hardware)
    {
        if (hardware.SystemMemoryBytes > 0 && hardware.SystemMemoryBytes < model.MinSystemMemoryBytes && hardware.GpuMemoryBytes < model.MinGpuMemoryBytes)
            return $"This PC may not have enough memory for this model ({DownloadProgress.FormatBytes(model.MinSystemMemoryBytes)} recommended).";
        if (hardware.GpuMemoryBytes < model.MinGpuMemoryBytes)
            return "No suitable graphics card was found, so this model will run on the processor and can take several minutes per lecture.";
        return null;
    }
}

/// <summary>Where downloaded models live, and downloading/removing them.</summary>
public sealed class LocalModelManager(string modelsDirectory, FileDownloader downloader)
{
    public string ModelsDirectory { get; } = modelsDirectory;

    public string GetPath(LocalModelInfo model) => Path.Combine(ModelsDirectory, model.FileName);

    public bool IsDownloaded(LocalModelInfo model) => File.Exists(GetPath(model));

    /// <summary>Bytes from an interrupted download that the next download will resume from.</summary>
    public long ResumableBytes(LocalModelInfo model) => FileDownloader.PartialBytes(GetPath(model));

    public async Task<string> DownloadAsync(LocalModelInfo model, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var path = GetPath(model);
        await downloader.DownloadAsync(model.DownloadUrl, path, model.SizeBytes, model.Sha256, progress, cancellationToken);
        return path;
    }

    /// <summary>Removes the model and any partial download.</summary>
    public void Delete(LocalModelInfo model)
    {
        var path = GetPath(model);
        if (File.Exists(path)) File.Delete(path);
        var partial = FileDownloader.PartialPath(path);
        if (File.Exists(partial)) File.Delete(partial);
    }
}
