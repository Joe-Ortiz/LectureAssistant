using CommunityToolkit.Mvvm.ComponentModel;
using LectureAssistant.Core.Downloads;
using LectureAssistant.QuestionGeneration.Local;

namespace LectureAssistant.App.Services;

/// <summary>
/// App-wide state of the (multi-GB) question-writing model download, so it keeps going when the
/// instructor moves between pages and every page shows the same progress. Use from the UI thread.
/// </summary>
public sealed partial class ModelDownloadService(LocalModelManager models) : ObservableObject
{
    private CancellationTokenSource? _cts;
    private Task? _task;

    [ObservableProperty] public partial LocalModelInfo? CurrentModel { get; private set; }
    [ObservableProperty] public partial bool IsDownloading { get; private set; }
    [ObservableProperty] public partial double Percent { get; private set; }
    [ObservableProperty] public partial string ProgressText { get; private set; } = "";
    [ObservableProperty] public partial string? Error { get; private set; }

    /// <summary>Raised on the UI thread after a model finishes downloading or is deleted.</summary>
    public event Action<LocalModelInfo>? ModelChanged;

    public LocalModelManager Models => models;

    /// <summary>Starts (or joins) the download of <paramref name="model"/>. Throws if it fails or is cancelled.</summary>
    public Task DownloadAsync(LocalModelInfo model)
    {
        if (models.IsDownloaded(model)) return Task.CompletedTask;
        if (_task is { IsCompleted: false } && CurrentModel == model) return _task;
        if (IsDownloading) Cancel();

        _task = RunAsync(model);
        return _task;
    }

    public void Cancel() => _cts?.Cancel();

    public void Delete(LocalModelInfo model)
    {
        if (CurrentModel == model) Cancel();
        models.Delete(model);
        ModelChanged?.Invoke(model);
    }

    private async Task RunAsync(LocalModelInfo model)
    {
        _cts = new CancellationTokenSource();
        CurrentModel = model;
        IsDownloading = true;
        Error = null;
        Percent = 0;
        ProgressText = "Starting download…";
        var progress = new Progress<DownloadProgress>(p =>
        {
            Percent = p.Fraction * 100;
            ProgressText = p.ToString();
        });

        try
        {
            await models.DownloadAsync(model, progress, _cts.Token);
            ProgressText = "Downloaded and verified.";
            ModelChanged?.Invoke(model);
        }
        catch (OperationCanceledException)
        {
            ProgressText = models.ResumableBytes(model) > 0 ? "Paused. The download will continue where it stopped." : "";
            throw;
        }
        catch (Exception ex)
        {
            Error = ex is DownloadException ? ex.Message : "The download failed: " + ex.Message;
            ProgressText = "";
            throw;
        }
        finally
        {
            IsDownloading = false;
            _cts.Dispose();
            _cts = null;
        }
    }
}
