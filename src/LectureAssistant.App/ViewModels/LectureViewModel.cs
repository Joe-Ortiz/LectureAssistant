using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureAssistant.App.Helpers;
using LectureAssistant.App.Services;
using LectureAssistant.Core;
using LectureAssistant.Core.Captions;
using LectureAssistant.Core.Models;
using LectureAssistant.Core.Persistence;
using LectureAssistant.Core.YouTube;
using LectureAssistant.Transcription;

namespace LectureAssistant.App.ViewModels;

public sealed partial class LectureViewModel(
    ProjectStore store,
    IAudioExtractor audioExtractor,
    ITranscriber transcriber,
    WhisperModelManager whisperModels,
    SettingsService settings,
    QuestionGeneratorFactory questionGenerators,
    ModelDownloadService modelDownloads,
    ExporterCatalog exporters) : ObservableObject
{
    /// <summary>Asks the instructor a yes/no question (title, message, confirm button text); set by the page.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    private LectureProject _project = new();
    private CancellationTokenSource? _operation;
    private bool _dirty;
    private bool _loading;

    public ObservableCollection<CaptionViewModel> Captions { get; } = [];
    public ObservableCollection<QuestionViewModel> Questions { get; } = [];

    /// <summary>Raised when the view should move the video preview to a time.</summary>
    public event Action<TimeSpan>? SeekRequested;

    /// <summary>Raised when captions were replaced, so the preview can reload its caption track.</summary>
    public event Action? CaptionsReplaced;

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string YouTubeUrl { get; set; } = "";
    [ObservableProperty] public partial string YouTubeStatus { get; set; } = "";
    [ObservableProperty] public partial bool YouTubeValid { get; set; }
    [ObservableProperty] public partial string? VideoPath { get; set; }
    [ObservableProperty] public partial string DurationText { get; set; } = "";

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string BusyText { get; set; } = "";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial bool ProgressIndeterminate { get; set; }
    [ObservableProperty] public partial string LiveText { get; set; } = "";
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial string? SuccessMessage { get; set; }

    // Transcription
    [ObservableProperty] public partial string Vocabulary { get; set; } = "";

    // Question generation
    [ObservableProperty] public partial double QuestionCount { get; set; } = settings.Current.DefaultQuestionCount;
    [ObservableProperty] public partial bool IncludeMultipleChoice { get; set; } = true;
    [ObservableProperty] public partial bool IncludeTrueFalse { get; set; } = true;
    [ObservableProperty] public partial bool IncludeFillInTheBlank { get; set; } = true;
    [ObservableProperty] public partial string Guidance { get; set; } = "";
    [ObservableProperty] public partial bool ReplaceExistingQuestions { get; set; }

    // Quiz settings
    [ObservableProperty] public partial double PassingScore { get; set; } = 70;
    [ObservableProperty] public partial bool PreventSkippingAhead { get; set; } = true;
    [ObservableProperty] public partial bool AllowRetry { get; set; } = true;
    [ObservableProperty] public partial bool ShowCorrectAnswers { get; set; } = true;

    // Export
    [ObservableProperty] public partial string? H5PReferencePackagePath { get; set; }

    public bool IsNotBusy => !IsBusy;
    public bool HasCaptions => Captions.Count > 0;
    public bool HasNoCaptions => Captions.Count == 0;
    public bool HasQuestions => Questions.Count > 0;
    public bool HasNoQuestions => Questions.Count == 0;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasSuccess => !string.IsNullOrEmpty(SuccessMessage);
    public string QuestionProviderText => questionGenerators.DescribeCurrent();
    public string H5PReferenceText => H5PReferencePackagePath is null
        ? "None: uses default library versions"
        : Path.GetFileName(H5PReferencePackagePath);

    public string ProjectDirectory => store.GetProjectDirectory(_project.Id);

    public async Task LoadAsync(string projectId)
    {
        _loading = true;
        try
        {
            _project = await store.LoadAsync(projectId) ?? throw new InvalidOperationException("This lecture couldn't be found.");
            Title = _project.Title;
            YouTubeUrl = _project.YouTubeUrl ?? "";
            VideoPath = _project.SourceVideoPath;
            DurationText = _project.Duration is { } d ? TimeText.Format(d) : "";
            PassingScore = _project.Quiz.PassingScorePercent;
            PreventSkippingAhead = _project.Quiz.PreventSkippingAhead;
            AllowRetry = _project.Quiz.AllowRetry;
            ShowCorrectAnswers = _project.Quiz.ShowCorrectAnswers;
            ReplaceCaptions(_project.Captions);
            ReplaceQuestions(_project.Questions);
        }
        finally
        {
            _loading = false;
            _dirty = false;
        }
    }

    public LectureProject ToProject()
    {
        _project.Title = Title.Trim();
        _project.YouTubeUrl = string.IsNullOrWhiteSpace(YouTubeUrl) ? null : YouTubeUrl.Trim();
        _project.Captions = Captions.Select(c => c.ToModel()).ToList();
        _project.Questions = Questions.Select(q => q.ToModel()).OrderBy(q => q.Timestamp).ToList();
        _project.Quiz = new QuizSettings
        {
            PassingScorePercent = Math.Clamp(double.IsNaN(PassingScore) ? 70 : PassingScore, 0, 100),
            PreventSkippingAhead = PreventSkippingAhead,
            AllowRetry = AllowRetry,
            ShowCorrectAnswers = ShowCorrectAnswers,
        };
        return _project;
    }

    public async Task SaveIfDirtyAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        await store.SaveAsync(ToProject());
    }

    [RelayCommand]
    private Task SaveAsync()
    {
        _dirty = true;
        return SaveIfDirtyAsync();
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(IsBusy): OnPropertyChanged(nameof(IsNotBusy)); break;
            case nameof(ErrorMessage): OnPropertyChanged(nameof(HasError)); break;
            case nameof(SuccessMessage): OnPropertyChanged(nameof(HasSuccess)); break;
            case nameof(H5PReferencePackagePath): OnPropertyChanged(nameof(H5PReferenceText)); break;
            case nameof(YouTubeUrl): UpdateYouTubeStatus(); MarkDirty(); break;
            case nameof(Title) or nameof(PassingScore) or nameof(PreventSkippingAhead) or nameof(AllowRetry) or nameof(ShowCorrectAnswers):
                MarkDirty(); break;
        }
    }

    private void MarkDirty()
    {
        if (!_loading) _dirty = true;
    }

    private void UpdateYouTubeStatus()
    {
        if (string.IsNullOrWhiteSpace(YouTubeUrl))
        {
            YouTubeValid = false;
            YouTubeStatus = "";
        }
        else if (Core.YouTube.YouTubeUrl.TryGetVideoId(YouTubeUrl, out var id))
        {
            YouTubeValid = true;
            YouTubeStatus = $"Video ID: {id}";
        }
        else
        {
            YouTubeValid = false;
            YouTubeStatus = "That doesn't look like a YouTube video link.";
        }
    }

    // ---------- Captions ----------

    [RelayCommand]
    private async Task TranscribeAsync()
    {
        if (VideoPath is null || !File.Exists(VideoPath))
        {
            ErrorMessage = "The original video file can't be found. It may have been moved or deleted.";
            return;
        }

        await RunAsync("Preparing…", async ct =>
        {
            var model = WhisperModels.Find(settings.Current.WhisperModelId);
            if (!whisperModels.IsDownloaded(model))
            {
                BusyText = $"Downloading speech model ({model.DisplayName}, about {model.ApproximateBytes / 1_000_000} MB, one time only)…";
                await whisperModels.DownloadAsync(model, new Progress<double>(p => Progress = p * 100), ct);
            }

            BusyText = "Extracting audio…";
            Progress = 0;
            var wavPath = Path.Combine(ProjectDirectory, "audio.wav");
            try
            {
                await audioExtractor.ExtractAsync(VideoPath, wavPath, new Progress<double>(p => Progress = p * 100), ct);

                BusyText = "Transcribing (this runs on your PC and can take a while for long lectures)…";
                Progress = 0;
                var options = new TranscriptionOptions
                {
                    Language = settings.Current.TranscriptionLanguage,
                    Vocabulary = string.IsNullOrWhiteSpace(Vocabulary) ? null : Vocabulary,
                };
                // Created here, on the UI thread, so reports are marshalled back to it.
                var transcriptionProgress = new Progress<TranscriptionProgress>(p =>
                {
                    Progress = p.Fraction * 100;
                    if (p.LatestSegment is { } s) LiveText = $"[{TimeText.Format(s.Start)}] {s.Text}";
                });
                var segments = await Task.Run(() => transcriber.TranscribeAsync(wavPath, options, transcriptionProgress, ct), ct);

                ReplaceCaptions(segments);
                CaptionsReplaced?.Invoke();
                _dirty = true;
                await SaveIfDirtyAsync();
                SuccessMessage = $"Created {segments.Count} captions. Review names and technical terms before publishing.";
            }
            finally
            {
                if (File.Exists(wavPath)) File.Delete(wavPath);
            }
        });
    }

    [RelayCommand]
    private async Task ImportCaptionsAsync()
    {
        var path = await Pickers.OpenFileAsync(".vtt", ".srt");
        if (path is null) return;
        try
        {
            var segments = CaptionFormats.Parse(await File.ReadAllTextAsync(path));
            if (segments.Count == 0)
            {
                ErrorMessage = "No captions were found in that file.";
                return;
            }
            ReplaceCaptions(segments);
            CaptionsReplaced?.Invoke();
            _dirty = true;
            await SaveIfDirtyAsync();
            SuccessMessage = $"Imported {segments.Count} captions.";
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            ErrorMessage = "Couldn't read that caption file: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ExportCaptionsAsync(string format)
    {
        if (Captions.Count == 0) return;
        bool srt = format == "srt";
        var path = await Pickers.SaveFileAsync(Pickers.SafeFileName(Title), srt ? "SubRip captions" : "WebVTT captions", srt ? ".srt" : ".vtt");
        if (path is null) return;

        var segments = Captions.Select(c => c.ToModel()).ToList();
        await File.WriteAllTextAsync(path, srt ? CaptionFormats.ToSrt(segments) : CaptionFormats.ToWebVtt(segments));
        SuccessMessage = $"Saved {Path.GetFileName(path)}. In YouTube Studio: Subtitles → Add language → Upload file → With timing.";
    }

    /// <summary>Current captions as WebVTT, for the preview player.</summary>
    public string CaptionsAsWebVtt() => CaptionFormats.ToWebVtt(Captions.Select(c => c.ToModel()));

    // ---------- Questions ----------

    [RelayCommand]
    private async Task GenerateQuestionsAsync()
    {
        if (Captions.Count == 0)
        {
            ErrorMessage = "Create or import captions first; questions are written from the transcript.";
            return;
        }

        var types = new List<QuestionType>();
        if (IncludeMultipleChoice) types.Add(QuestionType.MultipleChoice);
        if (IncludeTrueFalse) types.Add(QuestionType.TrueFalse);
        if (IncludeFillInTheBlank) types.Add(QuestionType.FillInTheBlank);
        if (types.Count == 0)
        {
            ErrorMessage = "Pick at least one question type.";
            return;
        }

        var modelToDownload = questionGenerators.ModelNeedingDownload();
        if (modelToDownload is not null)
        {
            bool confirmed = ConfirmAsync is not null && await ConfirmAsync(
                $"Download the question-writing model ({modelToDownload.SizeText})?",
                "Lecture Assistant needs to download its AI model once. After that, questions are written on this PC: " +
                "it works offline, needs no account, and your lectures stay private.\n\n" +
                "On a typical home connection this takes a few minutes. If it's interrupted, it continues where it stopped next time.",
                "Download");
            if (!confirmed) return;
        }

        await RunAsync("Writing questions…", async ct =>
        {
            if (modelToDownload is not null)
            {
                await DownloadModelAsync(modelToDownload, ct);
                OnPropertyChanged(nameof(QuestionProviderText));
                BusyText = "Writing questions…";
            }

            ProgressIndeterminate = true;
            var generator = questionGenerators.Create();
            var request = new QuestionGenerationRequest
            {
                Transcript = Captions.Select(c => c.ToModel()).ToList(),
                LectureTitle = Title,
                QuestionCount = (int)Math.Clamp(double.IsNaN(QuestionCount) ? 8 : QuestionCount, 1, 50),
                AllowedTypes = types,
                InstructorGuidance = string.IsNullOrWhiteSpace(Guidance) ? null : Guidance.Trim(),
            };

            // Created here, on the UI thread, so reports are marshalled back to it. The generators throttle them.
            bool finished = false;
            var generationProgress = new Progress<QuestionGenerationProgress>(p =>
            {
                if (finished) return; // a report still queued when generation ended
                BusyText = p.Message;
                LiveText = p.TimeLeft ?? "";
                if (p.Fraction is { } fraction) Progress = fraction * 100;
                ProgressIndeterminate = p.Fraction is null;
            });
            IReadOnlyList<Question> generated;
            try
            {
                generated = await Task.Run(() => generator.GenerateAsync(request, generationProgress, ct), ct);
            }
            finally
            {
                finished = true;
            }
            if (generator is IDisposable disposable) disposable.Dispose();

            var keep = ReplaceExistingQuestions ? [] : Questions.Select(q => q.ToModel()).ToList();
            ReplaceQuestions(keep.Concat(generated).OrderBy(q => q.Timestamp));
            _dirty = true;
            await SaveIfDirtyAsync();
            SuccessMessage = generated.Count switch
            {
                0 => "No usable questions came back. Try again, or add guidance about what to focus on.",
                _ when generated.Count < request.QuestionCount =>
                    $"Added {generated.Count} suggested question{(generated.Count == 1 ? "" : "s")} (you asked for {request.QuestionCount}; " +
                    "there wasn't room for more without interrupting students too often). Review each one; you're the expert.",
                _ => $"Added {generated.Count} suggested questions. Review each one; you're the expert.",
            };
        });
    }

    /// <summary>Downloads through the app-wide service (so Settings shows the same progress), mirrored in this page's status panel.</summary>
    private async Task DownloadModelAsync(QuestionGeneration.Local.LocalModelInfo model, CancellationToken ct)
    {
        BusyText = $"Downloading the question-writing model ({model.SizeText}, one time only)…";
        void Mirror(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Progress = modelDownloads.Percent;
            LiveText = modelDownloads.ProgressText;
        }

        modelDownloads.PropertyChanged += Mirror;
        using var registration = ct.Register(modelDownloads.Cancel);
        try
        {
            await modelDownloads.DownloadAsync(model);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception) when (modelDownloads.Error is { } message)
        {
            throw new InvalidOperationException(message);
        }
        finally
        {
            modelDownloads.PropertyChanged -= Mirror;
            LiveText = "";
            Progress = 0;
        }
    }

    public void AddQuestionAt(TimeSpan timestamp)
    {
        var question = new QuestionViewModel(
            new Question { Timestamp = timestamp, Type = QuestionType.MultipleChoice },
            RemoveQuestion, t => SeekRequested?.Invoke(t), MarkDirty);
        int index = 0;
        while (index < Questions.Count && Questions[index].Timestamp <= timestamp) index++;
        Questions.Insert(index, question);
        OnPropertyChanged(nameof(HasQuestions));
        OnPropertyChanged(nameof(HasNoQuestions));
        MarkDirty();
    }

    [RelayCommand]
    private void SortQuestions()
    {
        ReplaceQuestions(Questions.Select(q => q.ToModel()).OrderBy(q => q.Timestamp));
    }

    private void RemoveQuestion(QuestionViewModel question)
    {
        Questions.Remove(question);
        OnPropertyChanged(nameof(HasQuestions));
        OnPropertyChanged(nameof(HasNoQuestions));
        MarkDirty();
    }

    // ---------- Publish ----------

    [RelayCommand]
    private async Task BrowseH5PReferenceAsync()
    {
        H5PReferencePackagePath = await Pickers.OpenFileAsync(".h5p") ?? H5PReferencePackagePath;
    }

    [RelayCommand]
    private void ClearH5PReference() => H5PReferencePackagePath = null;

    [RelayCommand]
    private async Task ExportAsync(string kind)
    {
        var exporter = kind == "h5p" ? exporters.CreateH5P(H5PReferencePackagePath) : exporters.Scorm;
        var project = ToProject();

        var problems = exporter.Validate(project);
        if (problems.Count > 0)
        {
            ErrorMessage = "Fix these before exporting:\n• " + string.Join("\n• ", problems);
            return;
        }

        var path = await Pickers.SaveFileAsync(Pickers.SafeFileName(Title), exporter.DisplayName, exporter.FileExtension);
        if (path is null) return;

        await RunAsync("Exporting…", async ct =>
        {
            ProgressIndeterminate = true;
            await using (var output = File.Create(path))
                await exporter.ExportAsync(project, output, ct);
            await store.SaveAsync(project);
            SuccessMessage = kind == "h5p"
                ? $"Saved {Path.GetFileName(path)}. Upload it to your H5P platform (H5P.com, Moodle, WordPress, Lumi)."
                : $"Saved {Path.GetFileName(path)}. In Canvas: create an assignment, choose SCORM (or open the SCORM page in your course), and upload this file.";
        });
    }

    /// <summary>Raised when a student preview is ready for the view to open.</summary>
    public event Func<PreviewRequest, Task>? PreviewReady;

    [RelayCommand]
    private async Task PreviewAsync()
    {
        var project = ToProject();
        var problems = StudentPreview.Validate(project);
        if (problems.Count > 0)
        {
            SuccessMessage = null;
            ErrorMessage = "Fix these before previewing:\n• " + string.Join("\n• ", problems);
            return;
        }

        ErrorMessage = null;
        try
        {
            var request = await StudentPreview.PrepareAsync(project, CancellationToken.None);
            if (PreviewReady is not null) await PreviewReady(request);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorMessage = "The preview couldn't be prepared: " + ex.Message;
        }
    }

    [RelayCommand]
    private void CancelOperation() => _operation?.Cancel();

    // ---------- helpers ----------

    private async Task RunAsync(string busyText, Func<CancellationToken, Task> work)
    {
        ErrorMessage = null;
        SuccessMessage = null;
        LiveText = "";
        BusyText = busyText;
        Progress = 0;
        ProgressIndeterminate = false;
        IsBusy = true;
        _operation = new CancellationTokenSource();
        try
        {
            await work(_operation.Token);
        }
        catch (OperationCanceledException)
        {
            SuccessMessage = null;
            ErrorMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = false;
            LiveText = "";
            _operation.Dispose();
            _operation = null;
        }
    }

    private void ReplaceCaptions(IEnumerable<CaptionSegment> segments)
    {
        Captions.Clear();
        foreach (var s in segments) Captions.Add(new CaptionViewModel(s, t => SeekRequested?.Invoke(t), MarkDirty));
        OnPropertyChanged(nameof(HasCaptions));
        OnPropertyChanged(nameof(HasNoCaptions));
    }

    private void ReplaceQuestions(IEnumerable<Question> questions)
    {
        Questions.Clear();
        foreach (var q in questions) Questions.Add(new QuestionViewModel(q, RemoveQuestion, t => SeekRequested?.Invoke(t), MarkDirty));
        OnPropertyChanged(nameof(HasQuestions));
        OnPropertyChanged(nameof(HasNoQuestions));
    }
}
