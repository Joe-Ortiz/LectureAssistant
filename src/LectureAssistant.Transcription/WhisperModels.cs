using Whisper.net.Ggml;

namespace LectureAssistant.Transcription;

public sealed record WhisperModelInfo(
    string Id,
    string DisplayName,
    string Description,
    GgmlType Type,
    QuantizationType Quantization,
    long ApproximateBytes)
{
    public string FileName => $"ggml-{Id}.bin";
}

public static class WhisperModels
{
    public static readonly WhisperModelInfo Accurate = new(
        "large-v3-turbo-q5_0", "Accurate (recommended)",
        "Best accuracy for lectures and technical vocabulary. Fast with a GPU; slower on CPU only.",
        GgmlType.LargeV3Turbo, QuantizationType.Q5_0, 574_000_000);

    public static readonly WhisperModelInfo Balanced = new(
        "small-q5_1", "Balanced",
        "Good accuracy, runs comfortably on most laptops without a GPU.",
        GgmlType.Small, QuantizationType.Q5_1, 190_000_000);

    public static readonly WhisperModelInfo Fast = new(
        "base-q5_1", "Fast",
        "Quick drafts; expect more corrections on names and jargon.",
        GgmlType.Base, QuantizationType.Q5_1, 60_000_000);

    public static IReadOnlyList<WhisperModelInfo> All { get; } = [Accurate, Balanced, Fast];

    public static WhisperModelInfo Find(string? id) => All.FirstOrDefault(m => m.Id == id) ?? Accurate;
}

/// <summary>Downloads Whisper models on demand (they're too large to ship in the Store package).</summary>
public sealed class WhisperModelManager(string modelsDirectory)
{
    public string ModelsDirectory { get; } = modelsDirectory;

    public string GetPath(WhisperModelInfo model) => Path.Combine(ModelsDirectory, model.FileName);

    public bool IsDownloaded(WhisperModelInfo model) => File.Exists(GetPath(model));

    /// <summary>Downloads to a temp file and renames on success, so a cancelled download never leaves a corrupt model.</summary>
    public async Task<string> DownloadAsync(WhisperModelInfo model, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var path = GetPath(model);
        if (File.Exists(path)) return path;

        var temp = path + ".download";
        try
        {
            await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model.Type, model.Quantization, cancellationToken))
            await using (var target = File.Create(temp))
            {
                var buffer = new byte[1 << 20];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    total += read;
                    progress?.Report(Math.Min(0.99, (double)total / model.ApproximateBytes));
                }
            }
            File.Move(temp, path, overwrite: true);
            progress?.Report(1);
            return path;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public void Delete(WhisperModelInfo model)
    {
        var path = GetPath(model);
        if (File.Exists(path)) File.Delete(path);
    }
}
