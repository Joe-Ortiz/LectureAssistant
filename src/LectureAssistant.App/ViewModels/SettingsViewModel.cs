using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureAssistant.App.Services;
using LectureAssistant.Transcription;

namespace LectureAssistant.App.ViewModels;

public sealed partial class WhisperModelOption(WhisperModelInfo info, bool downloaded) : ObservableObject
{
    public WhisperModelInfo Info { get; } = info;
    public string Title => $"{Info.DisplayName} (about {Info.ApproximateBytes / 1_000_000} MB)";
    public string Description => Info.Description;

    [ObservableProperty] public partial bool IsDownloaded { get; set; } = downloaded;
    public string Status => IsDownloaded ? "Downloaded" : "Downloads when first used";

    partial void OnIsDownloadedChanged(bool value) => OnPropertyChanged(nameof(Status));
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SecretStore _secrets;
    private readonly WhisperModelManager _whisperModels;
    private readonly QuestionGeneratorFactory _generators;
    private CancellationTokenSource? _download;

    public ObservableCollection<WhisperModelOption> WhisperModelOptions { get; } = [];

    public static IReadOnlyList<string> Languages { get; } =
        ["auto", "en", "es", "fr", "de", "it", "pt", "nl", "pl", "zh", "ja", "ko", "ar", "hi", "ru", "uk", "tr", "vi"];

    [ObservableProperty] public partial int SelectedWhisperIndex { get; set; }
    [ObservableProperty] public partial string TranscriptionLanguage { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial double DownloadProgress { get; set; }

    [ObservableProperty] public partial int ProviderIndex { get; set; }
    [ObservableProperty] public partial string ApiKey { get; set; }
    [ObservableProperty] public partial string ClaudeModel { get; set; }
    [ObservableProperty] public partial string ApiKeyStatus { get; set; } = "";

    [ObservableProperty] public partial string? LocalModelPath { get; set; }
    [ObservableProperty] public partial double LocalGpuLayers { get; set; }
    [ObservableProperty] public partial double LocalContextSize { get; set; }
    [ObservableProperty] public partial double DefaultQuestionCount { get; set; }

    public bool UseClaude => ProviderIndex == (int)QuestionProvider.Claude;
    public bool UseLocalModel => ProviderIndex == (int)QuestionProvider.LocalModel;
    public string LocalModelText => LocalModelPath ?? "No model selected";

    public SettingsViewModel(SettingsService settings, SecretStore secrets, WhisperModelManager whisperModels, QuestionGeneratorFactory generators)
    {
        _settings = settings;
        _secrets = secrets;
        _whisperModels = whisperModels;
        _generators = generators;

        var current = settings.Current;
        foreach (var model in WhisperModels.All) WhisperModelOptions.Add(new WhisperModelOption(model, whisperModels.IsDownloaded(model)));
        SelectedWhisperIndex = WhisperModelOptions.ToList().FindIndex(o => o.Info == WhisperModels.Find(current.WhisperModelId));
        TranscriptionLanguage = current.TranscriptionLanguage;
        ProviderIndex = (int)current.QuestionProvider;
        ApiKey = secrets.GetAnthropicApiKey() ?? "";
        ClaudeModel = current.ClaudeModel;
        LocalModelPath = current.LocalModelPath;
        LocalGpuLayers = current.LocalModelGpuLayers;
        LocalContextSize = current.LocalModelContextSize;
        DefaultQuestionCount = current.DefaultQuestionCount;

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ApiKeyStatus) or nameof(IsDownloading) or nameof(DownloadProgress)) return;
            Save();
        };
    }

    partial void OnProviderIndexChanged(int value)
    {
        OnPropertyChanged(nameof(UseClaude));
        OnPropertyChanged(nameof(UseLocalModel));
    }

    partial void OnLocalModelPathChanged(string? value) => OnPropertyChanged(nameof(LocalModelText));

    partial void OnApiKeyChanged(string value) => ApiKeyStatus = "";

    private void Save()
    {
        var s = _settings.Current;
        if (SelectedWhisperIndex >= 0 && SelectedWhisperIndex < WhisperModelOptions.Count)
            s.WhisperModelId = WhisperModelOptions[SelectedWhisperIndex].Info.Id;
        s.TranscriptionLanguage = TranscriptionLanguage;
        s.QuestionProvider = (QuestionProvider)Math.Max(0, ProviderIndex);
        s.ClaudeModel = string.IsNullOrWhiteSpace(ClaudeModel) ? "claude-opus-5" : ClaudeModel.Trim();
        s.LocalModelPath = LocalModelPath;
        s.LocalModelGpuLayers = double.IsNaN(LocalGpuLayers) ? -1 : (int)LocalGpuLayers;
        s.LocalModelContextSize = double.IsNaN(LocalContextSize) ? 8192 : Math.Clamp((int)LocalContextSize, 2048, 131072);
        s.DefaultQuestionCount = double.IsNaN(DefaultQuestionCount) ? 8 : Math.Clamp((int)DefaultQuestionCount, 1, 50);
        _settings.Save();
        _secrets.SetAnthropicApiKey(ApiKey);
    }

    [RelayCommand]
    private async Task TestApiKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            ApiKeyStatus = "Enter a key first. Create one at console.anthropic.com → API keys.";
            return;
        }
        ApiKeyStatus = "Checking…";
        ApiKeyStatus = await _generators.TestApiKeyAsync(ApiKey.Trim());
    }

    [RelayCommand]
    private async Task BrowseLocalModelAsync()
    {
        LocalModelPath = await Pickers.OpenFileAsync(".gguf") ?? LocalModelPath;
    }

    [RelayCommand]
    private async Task DownloadWhisperModelAsync()
    {
        if (SelectedWhisperIndex < 0) return;
        var option = WhisperModelOptions[SelectedWhisperIndex];
        if (option.IsDownloaded) return;

        IsDownloading = true;
        _download = new CancellationTokenSource();
        try
        {
            await _whisperModels.DownloadAsync(option.Info, new Progress<double>(p => DownloadProgress = p * 100), _download.Token);
            option.IsDownloaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ApiKeyStatus = "";
            DownloadError = "Download failed: " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
            _download.Dispose();
            _download = null;
        }
    }

    [ObservableProperty] public partial string? DownloadError { get; set; }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    [RelayCommand]
    private void DeleteWhisperModel(WhisperModelOption option)
    {
        _whisperModels.Delete(option.Info);
        option.IsDownloaded = false;
    }
}
