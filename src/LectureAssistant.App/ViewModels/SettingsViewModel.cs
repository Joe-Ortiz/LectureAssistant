using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureAssistant.App.Services;
using LectureAssistant.Core.Captions;
using LectureAssistant.Core.Downloads;
using LectureAssistant.Core.Hardware;
using LectureAssistant.QuestionGeneration.Local;
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

public sealed partial class LocalModelOption(LocalModelInfo info, bool recommended) : ObservableObject
{
    public LocalModelInfo Info { get; } = info;
    public string Title => $"{Info.DisplayName} · {Info.SizeText}";
    public string Description => Info.Description;
    public bool IsRecommended { get; } = recommended;

    [ObservableProperty] public partial string Status { get; set; } = "";
}

public sealed partial class DictionaryEntryViewModel(DictionaryEntry entry, Action<DictionaryEntryViewModel> remove)
{
    public DictionaryEntry Entry { get; } = entry;
    public string Term => Entry.Term;
    public string Detail => Entry.IsCorrection ? $"Replaces “{Entry.Heard}”" : "";
    public bool HasDetail => Entry.IsCorrection;

    [RelayCommand]
    private void Remove() => remove(this);
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private const int LocalIndex = 0, ClaudeIndex = 1;

    private readonly SettingsService _settings;
    private readonly SecretStore _secrets;
    private readonly WhisperModelManager _whisperModels;
    private readonly QuestionGeneratorFactory _generators;
    private readonly DictionaryService _dictionary;
    private readonly bool _initialized;
    private CancellationTokenSource? _whisperDownload;

    public ModelDownloadService Downloads { get; }

    public ObservableCollection<WhisperModelOption> WhisperModelOptions { get; } = [];
    public ObservableCollection<LocalModelOption> LocalModelOptions { get; } = [];
    public ObservableCollection<DictionaryEntryViewModel> DictionaryEntries { get; } = [];

    public static IReadOnlyList<string> Languages { get; } =
        ["auto", "en", "es", "fr", "de", "it", "pt", "nl", "pl", "zh", "ja", "ko", "ar", "hi", "ru", "uk", "tr", "vi"];

    // Captions
    [ObservableProperty] public partial int SelectedWhisperIndex { get; set; }
    [ObservableProperty] public partial string TranscriptionLanguage { get; set; }
    [ObservableProperty] public partial bool IsDownloadingWhisper { get; set; }
    [ObservableProperty] public partial double WhisperDownloadProgress { get; set; }
    [ObservableProperty] public partial string? WhisperDownloadError { get; set; }

    // Personal dictionary
    [ObservableProperty] public partial string NewTerm { get; set; } = "";

    // Question provider: 0 = on this PC, 1 = Claude
    [ObservableProperty] public partial int ProviderIndex { get; set; }

    // Local model
    [ObservableProperty] public partial int SelectedLocalModelIndex { get; set; }
    [ObservableProperty] public partial bool SelectedModelDownloaded { get; set; }
    [ObservableProperty] public partial string? SelectedModelWarning { get; set; }
    [ObservableProperty] public partial bool UseCustomLocalModel { get; set; }
    [ObservableProperty] public partial string? LocalModelPath { get; set; }
    [ObservableProperty] public partial double LocalGpuLayers { get; set; }
    [ObservableProperty] public partial double LocalContextSize { get; set; }

    // Claude
    [ObservableProperty] public partial string ApiKey { get; set; }
    [ObservableProperty] public partial string ClaudeModel { get; set; }
    [ObservableProperty] public partial string ApiKeyStatus { get; set; } = "";

    [ObservableProperty] public partial double DefaultQuestionCount { get; set; }

    public bool UseLocalModel => ProviderIndex == LocalIndex;
    public bool UseClaude => ProviderIndex == ClaudeIndex;
    public bool UseBuiltInModel => !UseCustomLocalModel;
    public bool CanDownloadSelected => !SelectedModelDownloaded && !Downloads.IsDownloading;
    public bool HasSelectedModelWarning => !string.IsNullOrEmpty(SelectedModelWarning);
    public string LocalModelText => LocalModelPath ?? "No file chosen";
    public bool HasNoDictionaryEntries => DictionaryEntries.Count == 0;
    public string HardwareText { get; }

    // Where the models run: forecast from the hardware, and what actually happened last time.
    [ObservableProperty] public partial string QuestionDeviceText { get; set; } = "";
    public string LastQuestionRunText { get; }
    public bool HasLastQuestionRun => LastQuestionRunText.Length > 0;
    public string SpeechDeviceText { get; }
    public string LastTranscriptionRunText { get; }
    public bool HasLastTranscriptionRun => LastTranscriptionRunText.Length > 0;

    private LocalModelInfo? SelectedLocalModel =>
        SelectedLocalModelIndex >= 0 && SelectedLocalModelIndex < LocalModelOptions.Count ? LocalModelOptions[SelectedLocalModelIndex].Info : null;

    public SettingsViewModel(
        SettingsService settings,
        SecretStore secrets,
        WhisperModelManager whisperModels,
        QuestionGeneratorFactory generators,
        ModelDownloadService downloads,
        DictionaryService dictionary)
    {
        _settings = settings;
        _secrets = secrets;
        _whisperModels = whisperModels;
        _generators = generators;
        _dictionary = dictionary;
        Downloads = downloads;
        RefreshDictionary();

        var current = settings.Current;
        foreach (var model in WhisperModels.All) WhisperModelOptions.Add(new WhisperModelOption(model, whisperModels.IsDownloaded(model)));
        SelectedWhisperIndex = WhisperModelOptions.ToList().FindIndex(o => o.Info == WhisperModels.Find(current.WhisperModelId));
        TranscriptionLanguage = current.TranscriptionLanguage;

        var hardware = HardwareProbe.Profile;
        var recommended = LocalModelCatalog.Recommend(hardware);
        foreach (var model in LocalModelCatalog.All) LocalModelOptions.Add(new LocalModelOption(model, model == recommended));
        var selected = LocalModelCatalog.Find(current.LocalModelId) ?? recommended;
        SelectedLocalModelIndex = LocalModelOptions.ToList().FindIndex(o => o.Info == selected);
        HardwareText = DeviceForecasts.DescribeHardware(hardware);
        SpeechDeviceText = DeviceForecasts.ForSpeechModel(hardware).Text;
        LastQuestionRunText = DescribeLastRun(current.LastQuestionRun);
        LastTranscriptionRunText = DescribeLastRun(current.LastTranscriptionRun);

        ProviderIndex = current.QuestionProvider == QuestionProvider.Claude ? ClaudeIndex : LocalIndex;
        UseCustomLocalModel = current.UseCustomLocalModel;
        LocalModelPath = current.LocalModelPath;
        LocalGpuLayers = current.LocalModelGpuLayers;
        LocalContextSize = current.LocalModelContextSize ?? 0;
        ApiKey = secrets.GetAnthropicApiKey() ?? "";
        ClaudeModel = current.ClaudeModel;
        DefaultQuestionCount = current.DefaultQuestionCount;

        RefreshLocalModelStatus();
        RefreshQuestionDevice();
        downloads.PropertyChanged += OnDownloadsChanged;
        downloads.ModelChanged += OnModelChanged;

        _initialized = true;
    }

    /// <summary>Stops listening to the app-wide download service (call when the page closes).</summary>
    public void Detach()
    {
        Downloads.PropertyChanged -= OnDownloadsChanged;
        Downloads.ModelChanged -= OnModelChanged;
    }

    private void OnDownloadsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModelDownloadService.IsDownloading) or nameof(ModelDownloadService.CurrentModel))
            RefreshLocalModelStatus();
    }

    private void OnModelChanged(LocalModelInfo model) => RefreshLocalModelStatus();

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(ProviderIndex):
                OnPropertyChanged(nameof(UseLocalModel));
                OnPropertyChanged(nameof(UseClaude));
                break;
            case nameof(UseCustomLocalModel): OnPropertyChanged(nameof(UseBuiltInModel)); break;
            case nameof(LocalModelPath): OnPropertyChanged(nameof(LocalModelText)); break;
            case nameof(SelectedLocalModelIndex): RefreshLocalModelStatus(); break;
            case nameof(SelectedModelDownloaded): OnPropertyChanged(nameof(CanDownloadSelected)); break;
            case nameof(SelectedModelWarning): OnPropertyChanged(nameof(HasSelectedModelWarning)); break;
            case nameof(ApiKey): ApiKeyStatus = ""; break;
        }

        if (e.PropertyName is nameof(SelectedLocalModelIndex) or nameof(UseCustomLocalModel) or nameof(LocalModelPath) or nameof(LocalGpuLayers))
            RefreshQuestionDevice();

        if (_initialized && e.PropertyName is nameof(SelectedWhisperIndex) or nameof(TranscriptionLanguage) or nameof(ProviderIndex)
            or nameof(SelectedLocalModelIndex) or nameof(UseCustomLocalModel) or nameof(LocalModelPath) or nameof(LocalGpuLayers)
            or nameof(LocalContextSize) or nameof(ApiKey) or nameof(ClaudeModel) or nameof(DefaultQuestionCount))
        {
            Save();
        }
    }

    private void Save()
    {
        var s = _settings.Current;
        if (SelectedWhisperIndex >= 0 && SelectedWhisperIndex < WhisperModelOptions.Count)
            s.WhisperModelId = WhisperModelOptions[SelectedWhisperIndex].Info.Id;
        s.TranscriptionLanguage = TranscriptionLanguage;
        s.QuestionProvider = ProviderIndex == ClaudeIndex ? QuestionProvider.Claude : QuestionProvider.LocalModel;
        s.LocalModelId = SelectedLocalModel?.Id;
        s.UseCustomLocalModel = UseCustomLocalModel;
        s.LocalModelPath = LocalModelPath;
        s.LocalModelGpuLayers = double.IsNaN(LocalGpuLayers) ? -1 : (int)LocalGpuLayers;
        s.LocalModelContextSize = double.IsNaN(LocalContextSize) || LocalContextSize < 2048 ? null : Math.Min((int)LocalContextSize, 131072);
        s.ClaudeModel = string.IsNullOrWhiteSpace(ClaudeModel) ? "claude-opus-5" : ClaudeModel.Trim();
        s.DefaultQuestionCount = double.IsNaN(DefaultQuestionCount) ? 8 : Math.Clamp((int)DefaultQuestionCount, 1, 50);
        _settings.Save();
        _secrets.SetAnthropicApiKey(ApiKey);
    }

    private void RefreshLocalModelStatus()
    {
        var manager = Downloads.Models;
        foreach (var option in LocalModelOptions)
        {
            var model = option.Info;
            option.Status =
                manager.IsDownloaded(model) ? "✓ Downloaded" :
                Downloads.IsDownloading && Downloads.CurrentModel == model ? "Downloading…" :
                manager.ResumableBytes(model) > 0 ? $"Paused at {DownloadProgress.FormatBytes(manager.ResumableBytes(model))}" :
                "Not downloaded";
        }

        SelectedModelDownloaded = SelectedLocalModel is { } selected && manager.IsDownloaded(selected);
        SelectedModelWarning = SelectedLocalModel is { } m ? LocalModelCatalog.HardwareWarning(m, HardwareProbe.Profile) : null;
        OnPropertyChanged(nameof(CanDownloadSelected));
    }

    /// <summary>Graphics card or processor for the selected question model, given this PC and the GPU layers setting.</summary>
    private void RefreshQuestionDevice()
    {
        var hardware = HardwareProbe.Profile;
        var gpuLayers = double.IsNaN(LocalGpuLayers) ? -1 : (int)LocalGpuLayers;
        if (UseCustomLocalModel)
        {
            QuestionDeviceText = LocalModelPath is { } path && File.Exists(path)
                ? DeviceForecasts.ForCustomQuestionModel(hardware, new FileInfo(path).Length, gpuLayers).Text
                : "";
        }
        else
        {
            QuestionDeviceText = SelectedLocalModel is { } model ? DeviceForecasts.ForQuestionModel(hardware, model, gpuLayers).Text : "";
        }
    }

    private static string DescribeLastRun(ModelRun? run) => run is null ? "" : $"Last run: {run.Describe()}.";

    [RelayCommand]
    private async Task DownloadLocalModelAsync()
    {
        if (SelectedLocalModel is not { } model) return;
        try
        {
            await Downloads.DownloadAsync(model);
        }
        catch (Exception)
        {
            // Downloads.Error / ProgressText already describe what happened.
        }
    }

    [RelayCommand]
    private void CancelLocalModelDownload() => Downloads.Cancel();

    [RelayCommand]
    private void DeleteLocalModel()
    {
        if (SelectedLocalModel is { } model) Downloads.Delete(model);
    }

    [RelayCommand]
    private async Task BrowseLocalModelAsync()
    {
        LocalModelPath = await Pickers.OpenFileAsync(".gguf") ?? LocalModelPath;
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
    private async Task DownloadWhisperModelAsync()
    {
        if (SelectedWhisperIndex < 0) return;
        var option = WhisperModelOptions[SelectedWhisperIndex];
        if (option.IsDownloaded) return;

        IsDownloadingWhisper = true;
        WhisperDownloadError = null;
        _whisperDownload = new CancellationTokenSource();
        try
        {
            await _whisperModels.DownloadAsync(option.Info, new Progress<double>(p => WhisperDownloadProgress = p * 100), _whisperDownload.Token);
            option.IsDownloaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            WhisperDownloadError = "Download failed: " + ex.Message;
        }
        finally
        {
            IsDownloadingWhisper = false;
            _whisperDownload.Dispose();
            _whisperDownload = null;
        }
    }

    [RelayCommand]
    private void CancelWhisperDownload() => _whisperDownload?.Cancel();

    [RelayCommand]
    private void AddTerm()
    {
        if (_dictionary.Current.AddTerm(NewTerm) is null) return;
        _dictionary.Save();
        NewTerm = "";
        RefreshDictionary();
    }

    private void RemoveEntry(DictionaryEntryViewModel entry)
    {
        _dictionary.Current.Remove(entry.Entry);
        _dictionary.Save();
        DictionaryEntries.Remove(entry);
        OnPropertyChanged(nameof(HasNoDictionaryEntries));
    }

    private void RefreshDictionary()
    {
        DictionaryEntries.Clear();
        var entries = _dictionary.Current.Entries
            .OrderBy(e => e.Term, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Heard, StringComparer.CurrentCultureIgnoreCase);
        foreach (var entry in entries) DictionaryEntries.Add(new DictionaryEntryViewModel(entry, RemoveEntry));
        OnPropertyChanged(nameof(HasNoDictionaryEntries));
    }
}
