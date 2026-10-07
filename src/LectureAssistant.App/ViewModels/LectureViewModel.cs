using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureAssistant.App.Helpers;
using LectureAssistant.App.Services;
using LectureAssistant.Core;
using LectureAssistant.Core.Captions;
using LectureAssistant.Core.Hardware;
using LectureAssistant.Core.Models;
using LectureAssistant.Core.Persistence;
using LectureAssistant.Core.YouTube;
using LectureAssistant.Export.H5P;
using LectureAssistant.Transcription;

namespace LectureAssistant.App.ViewModels;

public sealed partial class LectureViewModel(
    ProjectStore store,
    IAudioExtractor audioExtractor,
    ITranscriber transcriber,
    WhisperModelManager whisperModels,
    SettingsService settings,
    DictionaryService dictionary,
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

    /// <summary>Words in the captions that may be misspelled, for quick fixing.</summary>
    public ObservableCollection<WordCheckViewModel> WordChecks { get; } = [];

    public static IReadOnlyList<string> SubjectNames { get; } = SubjectAreas.All.Select(s => s.DisplayName).ToList();

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
    [ObservableProperty] public partial int SubjectIndex { get; set; }

    // Quick corrections
    [ObservableProperty] public partial string FindText { get; set; } = "";
    [ObservableProperty] public partial string ReplaceText { get; set; } = "";
    [ObservableProperty] public partial bool RememberReplacement { get; set; } = true;

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
    /// <summary>Index into <see cref="AttemptChoices.AttemptNames"/>.</summary>
    [ObservableProperty] public partial int AttemptsIndex { get; set; } = AttemptChoices.AttemptsIndex(AttemptRules.Unlimited);
    /// <summary>Index into <see cref="AttemptChoices.ScoringNames"/>.</summary>
    [ObservableProperty] public partial int RetryScoringIndex { get; set; } = AttemptChoices.ScoringIndex(RetryScoring.FirstAttemptOnly);
    [ObservableProperty] public partial double RetryPenaltyPercent { get; set; } = AttemptRules.DefaultRetryPenaltyPercent;
    [ObservableProperty] public partial bool ShowCorrectAnswers { get; set; } = true;

    // Export
    [ObservableProperty] public partial string? H5PReferencePackagePath { get; set; }

    public bool IsNotBusy => !IsBusy;
    public bool HasCaptions => Captions.Count > 0;
    public bool HasNoCaptions => Captions.Count == 0;
    public bool HasNoWordChecks => WordChecks.Count == 0;
    public string WordChecksHeader => WordChecks.Count switch
    {
        0 => "Fix misheard words",
        1 => "Fix misheard words · 1 word to check",
        var n => $"Fix misheard words · {n} words to check",
    };
    private SubjectArea Subject => SubjectIndex >= 0 && SubjectIndex < SubjectAreas.All.Count ? SubjectAreas.All[SubjectIndex] : SubjectAreas.General;
    public bool HasQuestions => Questions.Count > 0;
    public bool HasNoQuestions => Questions.Count == 0;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasSuccess => !string.IsNullOrEmpty(SuccessMessage);
    public string QuestionProviderText => questionGenerators.DescribeCurrent();
    public IReadOnlyList<string> AttemptNames => AttemptChoices.AttemptNames;
    public IReadOnlyList<string> RetryScoringNames => AttemptChoices.ScoringNames;
    /// <summary>The penalty only matters when the quiz, or any question, uses reduced credit.</summary>
    public bool ShowRetryPenalty =>
        AttemptChoices.Scoring(RetryScoringIndex) == RetryScoring.ReducedCredit || Questions.Any(q => q.OverridesWithReducedCredit);
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
            SubjectIndex = IndexOfSubject(_project.SubjectArea ?? settings.Current.LastSubjectArea);
            PassingScore = _project.Quiz.PassingScorePercent;
            PreventSkippingAhead = _project.Quiz.PreventSkippingAhead;
            AttemptsIndex = AttemptChoices.AttemptsIndex(_project.Quiz.AttemptsAllowed);
            RetryScoringIndex = AttemptChoices.ScoringIndex(_project.Quiz.RetryScoring);
            RetryPenaltyPercent = _project.Quiz.RetryPenaltyPercent;
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
        _project.SubjectArea = Subject.Id;
        _project.Captions = Captions.Select(c => c.ToModel()).ToList();
        _project.Questions = Questions.Select(q => q.ToModel()).OrderBy(q => q.Timestamp).ToList();
        _project.Quiz = new QuizSettings
        {
            PassingScorePercent = Math.Clamp(double.IsNaN(PassingScore) ? 70 : PassingScore, 0, 100),
            PreventSkippingAhead = PreventSkippingAhead,
            AttemptsAllowed = AttemptChoices.Attempts(AttemptsIndex),
            RetryScoring = AttemptChoices.Scoring(RetryScoringIndex),
            RetryPenaltyPercent = (int)Math.Clamp(
                Math.Round(double.IsNaN(RetryPenaltyPercent) ? AttemptRules.DefaultRetryPenaltyPercent : RetryPenaltyPercent), 1, 100),
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
            case nameof(SubjectIndex) when !_loading && SubjectIndex >= 0:
                // Instructors tend to teach one subject, so the latest choice becomes the default for new lectures.
                settings.Current.LastSubjectArea = Subject.Id;
                settings.Save();
                MarkDirty();
                RefreshWordChecks();
                break;
            case nameof(RetryScoringIndex): OnPropertyChanged(nameof(ShowRetryPenalty)); MarkDirty(); break;
            case nameof(Title) or nameof(PassingScore) or nameof(PreventSkippingAhead) or nameof(AttemptsIndex) or nameof(RetryPenaltyPercent)
                or nameof(ShowCorrectAnswers):
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
                var language = settings.Current.TranscriptionLanguage;
                var options = new TranscriptionOptions
                {
                    Language = language,
                    Vocabulary = RecognitionPrompt.Build(Subject, dictionary.Current.TermsByRecency(), language),
                };
                // Created here, on the UI thread, so reports are marshalled back to it.
                var transcriptionProgress = new Progress<TranscriptionProgress>(p =>
                {
                    Progress = p.Fraction * 100;
                    if (p.LatestSegment is { } s) LiveText = $"[{TimeText.Format(s.Start)}] {s.Text}";
                });
                var segments = await Task.Run(() => transcriber.TranscribeAsync(wavPath, options, transcriptionProgress, ct), ct);
                var ranOn = RememberRun(transcriber, model.DisplayName.Split(" (")[0] + " speech model", run => settings.Current.LastTranscriptionRun = run);

                int fixedWords = dictionary.Current.ApplyTo(segments);
                dictionary.Save();

                ReplaceCaptions(segments);
                CaptionsReplaced?.Invoke();
                _dirty = true;
                await SaveIfDirtyAsync();
                SuccessMessage = $"Created {segments.Count} captions."
                    + (fixedWords switch { 0 => "", 1 => " Fixed 1 word using your dictionary.", _ => $" Fixed {fixedWords} words using your dictionary." })
                    + (WordChecks.Count > 0
                        ? " Open \"Fix misheard words\" to check the words listed there, and review names and technical terms before publishing."
                        : " Review names and technical terms before publishing.");
                if (ranOn.Length > 0) SuccessMessage += $" Transcribed {ranOn}.";
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

    // ---------- Quick corrections ----------

    [RelayCommand]
    private void ReplaceEverywhere()
    {
        if (string.IsNullOrWhiteSpace(FindText) || string.IsNullOrWhiteSpace(ReplaceText)) return;
        if (FixWord(FindText, ReplaceText, RememberReplacement))
        {
            FindText = "";
            ReplaceText = "";
        }
    }

    private void ApplyWordCheck(WordCheckViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.Replacement)) return;
        FixWord(row.Word, row.Replacement, row.Remember);
    }

    private void KeepWordCheck(WordCheckViewModel row)
    {
        if (row.Remember)
        {
            dictionary.Current.AddTerm(row.Word);
            dictionary.Save();
        }
        WordChecks.Remove(row);
        OnWordChecksChanged();
    }

    /// <summary>Replaces every whole-word occurrence across all captions. Returns false when nothing was replaced or remembered.</summary>
    private bool FixWord(string find, string replacement, bool remember)
    {
        find = CaptionCorrections.NormalizeSpaces(find);
        replacement = CaptionCorrections.NormalizeSpaces(replacement);

        var models = Captions.Select(c => c.ToModel()).ToList();
        int changed = CaptionCorrections.Apply(models, find, replacement);
        for (int i = 0; i < models.Count; i++)
        {
            if (models[i].Text != Captions[i].Text.Trim()) Captions[i].Text = models[i].Text;
        }

        if (remember)
        {
            dictionary.Current.AddCorrection(find, replacement);
            dictionary.Save();
        }

        ErrorMessage = null;
        SuccessMessage = changed switch
        {
            0 when CaptionCorrections.Count(models, find) > 0 => $"\"{find}\" is already spelled that way." + (remember ? " Saved in your dictionary." : ""),
            0 => $"\"{find}\" isn't in the captions." + (remember ? " Saved in your dictionary for future lectures." : ""),
            1 => $"Replaced \"{find}\" with \"{replacement}\" in 1 place.",
            _ => $"Replaced \"{find}\" with \"{replacement}\" in {changed} places.",
        };
        RefreshWordChecks();
        if (changed > 0) CaptionsReplaced?.Invoke();
        return changed > 0 || remember;
    }

    private void RefreshWordChecks()
    {
        WordChecks.Clear();
        if (Captions.Count > 0)
        {
            // Words from the dictionary or the subject's example sentence are already spelled the way the instructor wants.
            var captions = Captions.Select(c => c.ToModel()).ToList();
            var subjectPrompt = Subject.Prompt;
            bool IsKnown(string word) => dictionary.Current.IsKnownTerm(word) || CaptionCorrections.Count(subjectPrompt, word) > 0;
            foreach (var word in WordsToCheck.Find(captions, IsKnown))
                WordChecks.Add(new WordCheckViewModel(word, ApplyWordCheck, KeepWordCheck));
        }
        OnWordChecksChanged();
    }

    private void OnWordChecksChanged()
    {
        OnPropertyChanged(nameof(HasNoWordChecks));
        OnPropertyChanged(nameof(WordChecksHeader));
    }

    private static int IndexOfSubject(string? id)
    {
        var subject = SubjectAreas.Find(id);
        return SubjectAreas.All.ToList().IndexOf(subject);
    }

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
            // The live line shows the time left and, for the local model, where it's running.
            string timeLeft = "", runningOn = "";
            void ShowLiveText() => LiveText = string.Join(" ", new[] { timeLeft.Length > 0 ? timeLeft + "." : "", runningOn }.Where(s => s.Length > 0));
            if (generator is QuestionGeneration.Local.LocalLlmQuestionGenerator local)
                local.DeviceProgress = new Progress<ComputeDeviceReport>(device => { runningOn = device.StatusText; ShowLiveText(); });
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
                timeLeft = p.TimeLeft ?? "";
                ShowLiveText();
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
            var modelName = questionGenerators.SelectedLocalModel is { } builtIn
                ? builtIn.DisplayName + " model"
                : Path.GetFileName(settings.Current.LocalModelPath);
            var ranOn = RememberRun(generator, modelName, run => settings.Current.LastQuestionRun = run);

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
            if (ranOn.Length > 0) SuccessMessage += $" Written {ranOn}.";
        });
    }

    /// <summary>
    /// Remembers where a local model ran (graphics card or processor) for Settings, and returns
    /// "on your graphics card (…) in 34 seconds" for the success message, or "" when it isn't known.
    /// </summary>
    private string RememberRun(object model, string? modelName, Action<ModelRun> store)
    {
        if (model is not IReportsModelRun { LastRun: { } run }) return "";
        store(run with { ModelName = modelName });
        settings.Save();
        return run.Where.Device == ComputeDevice.Unknown ? "" : $"on {run.Where.Describe()} in {ModelRun.FormatDuration(run.Duration)}";
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
            RemoveQuestion, t => SeekRequested?.Invoke(t), OnQuestionChanged);
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
        OnQuestionChanged();
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

        if (kind == "h5p" && H5PExporter.Warnings(project) is { Count: > 0 } warnings && ConfirmAsync is not null &&
            !await ConfirmAsync(
                "H5P can't match your retry settings",
                string.Join("\n\n", warnings) + "\n\nThe SCORM package follows your settings exactly.",
                "Export anyway"))
            return;

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
        RefreshWordChecks();
    }

    private void ReplaceQuestions(IEnumerable<Question> questions)
    {
        Questions.Clear();
        foreach (var q in questions) Questions.Add(new QuestionViewModel(q, RemoveQuestion, t => SeekRequested?.Invoke(t), OnQuestionChanged));
        OnPropertyChanged(nameof(HasQuestions));
        OnPropertyChanged(nameof(HasNoQuestions));
        OnPropertyChanged(nameof(ShowRetryPenalty));
    }

    /// <summary>A question was edited or removed.</summary>
    private void OnQuestionChanged()
    {
        MarkDirty();
        OnPropertyChanged(nameof(ShowRetryPenalty));
    }
}
