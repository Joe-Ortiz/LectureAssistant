using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureAssistant.App.Helpers;
using LectureAssistant.Core.Models;

namespace LectureAssistant.App.ViewModels;

public sealed partial class QuestionViewModel : ObservableObject
{
    private readonly Action<QuestionViewModel> _remove;
    private readonly Action<TimeSpan> _seek;
    private readonly Action _changed;
    private bool _initializing;

    public string Id { get; }

    [ObservableProperty] public partial TimeSpan Timestamp { get; set; }
    [ObservableProperty] public partial string TimestampText { get; set; }
    [ObservableProperty] public partial bool TimestampInvalid { get; set; }
    [ObservableProperty] public partial int TypeIndex { get; set; }
    [ObservableProperty] public partial string Prompt { get; set; }
    [ObservableProperty] public partial bool CorrectAnswer { get; set; }
    [ObservableProperty] public partial string AcceptedAnswersText { get; set; }
    [ObservableProperty] public partial string Explanation { get; set; }
    [ObservableProperty] public partial double Points { get; set; }
    /// <summary>Index into <see cref="AttemptChoices.QuestionAttemptNames"/>; 0 = quiz default.</summary>
    [ObservableProperty] public partial int AttemptsIndex { get; set; }
    /// <summary>Index into <see cref="AttemptChoices.QuestionScoringNames"/>; 0 = quiz default.</summary>
    [ObservableProperty] public partial int RetryScoringIndex { get; set; }
    [ObservableProperty] public partial string? SourceExcerpt { get; set; }
    [ObservableProperty] public partial string ProblemsText { get; set; } = "";

    /// <summary>Non-blocking: the question comes sooner after the previous one than the lecture's minimum.</summary>
    [ObservableProperty] public partial string SpacingWarning { get; set; } = "";

    public ObservableCollection<OptionViewModel> Options { get; } = [];

    public static IReadOnlyList<string> TypeNames { get; } = ["Multiple choice", "True / false", "Fill in the blank"];
    public static IReadOnlyList<string> AttemptNames => AttemptChoices.QuestionAttemptNames;
    public static IReadOnlyList<string> RetryScoringNames => AttemptChoices.QuestionScoringNames;

    public QuestionType Type => (QuestionType)TypeIndex;
    public bool IsMultipleChoice => Type == QuestionType.MultipleChoice;
    public bool IsTrueFalse => Type == QuestionType.TrueFalse;
    public bool IsFillInTheBlank => Type == QuestionType.FillInTheBlank;
    public bool HasProblems => ProblemsText.Length > 0;
    public bool HasSpacingWarning => SpacingWarning.Length > 0;
    public bool HasSourceExcerpt => !string.IsNullOrWhiteSpace(SourceExcerpt);
    public bool OverridesWithReducedCredit => AttemptChoices.OverrideScoring(RetryScoringIndex) == RetryScoring.ReducedCredit;

    public QuestionViewModel(Question model, Action<QuestionViewModel> remove, Action<TimeSpan> seek, Action changed)
    {
        _remove = remove;
        _seek = seek;
        _changed = changed;

        _initializing = true;
        Id = model.Id;
        Timestamp = model.Timestamp;
        TimestampText = TimeText.Format(model.Timestamp);
        Prompt = model.Prompt;
        CorrectAnswer = model.CorrectAnswer;
        AcceptedAnswersText = string.Join("; ", model.AcceptedAnswers);
        Explanation = model.Explanation ?? "";
        Points = model.Points;
        AttemptsIndex = AttemptChoices.OverrideAttemptsIndex(model.AttemptsAllowed);
        RetryScoringIndex = AttemptChoices.OverrideScoringIndex(model.RetryScoring);
        SourceExcerpt = model.SourceExcerpt;
        foreach (var option in model.Options) Options.Add(new OptionViewModel(option, RemoveOption, OnChildChanged));
        TypeIndex = (int)model.Type;
        if (IsMultipleChoice) EnsureStarterOptions();
        _initializing = false;

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProblemsText) or nameof(SpacingWarning) or nameof(HasSpacingWarning)) return;
            _changed();
            Revalidate();
        };
        Revalidate();
    }

    partial void OnTimestampTextChanged(string value)
    {
        TimestampInvalid = !TimeText.TryParse(value, out var parsed);
        if (!TimestampInvalid) Timestamp = parsed;
    }

    partial void OnTypeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsMultipleChoice));
        OnPropertyChanged(nameof(IsTrueFalse));
        OnPropertyChanged(nameof(IsFillInTheBlank));
        if (_initializing) return;
        if (IsMultipleChoice) EnsureStarterOptions();
        if (IsFillInTheBlank && !Prompt.Contains(Question.BlankMarker, StringComparison.Ordinal))
            Prompt = Prompt.TrimEnd() + " " + Question.BlankMarker;
    }

    partial void OnProblemsTextChanged(string value) => OnPropertyChanged(nameof(HasProblems));
    partial void OnSpacingWarningChanged(string value) => OnPropertyChanged(nameof(HasSpacingWarning));
    partial void OnSourceExcerptChanged(string? value) => OnPropertyChanged(nameof(HasSourceExcerpt));

    [RelayCommand]
    private void AddOption() => Options.Add(new OptionViewModel(new AnswerOption(), RemoveOption, OnChildChanged));

    [RelayCommand]
    private void Remove() => _remove(this);

    [RelayCommand]
    private void Jump() => _seek(Timestamp);

    public void SetTimestamp(TimeSpan value) => TimestampText = TimeText.Format(value);

    /// <param name="sincePrevious">Time since the question before this one on the timeline; null for the first.</param>
    public void UpdateSpacingWarning(TimeSpan? sincePrevious, TimeSpan minimum)
    {
        SpacingWarning = sincePrevious is not { } gap || gap >= minimum ? ""
            : gap < TimeSpan.FromSeconds(1) ? $"At the same time as the previous question (minimum is {TimeText.Describe(minimum)})."
            : $"Only {TimeText.Describe(gap)} after the previous question (minimum is {TimeText.Describe(minimum)}).";
    }

    public Question ToModel() => new()
    {
        Id = Id,
        Timestamp = Timestamp,
        Type = Type,
        Prompt = Prompt.Trim(),
        Options = IsMultipleChoice ? Options.Select(o => o.ToModel()).ToList() : [],
        CorrectAnswer = CorrectAnswer,
        AcceptedAnswers = IsFillInTheBlank
            ? AcceptedAnswersText.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [],
        Explanation = string.IsNullOrWhiteSpace(Explanation) ? null : Explanation.Trim(),
        Points = Math.Max(1, (int)Math.Round(double.IsNaN(Points) ? 1 : Points)),
        AttemptsAllowed = AttemptChoices.OverrideAttempts(AttemptsIndex),
        RetryScoring = AttemptChoices.OverrideScoring(RetryScoringIndex),
        SourceExcerpt = SourceExcerpt,
    };

    private void EnsureStarterOptions()
    {
        while (Options.Count < 2) AddOption();
    }

    private void RemoveOption(OptionViewModel option)
    {
        Options.Remove(option);
        OnChildChanged();
    }

    private void OnChildChanged()
    {
        _changed();
        Revalidate();
    }

    private void Revalidate()
    {
        var problems = ToModel().Validate().ToList();
        if (TimestampInvalid) problems.Insert(0, "Time isn't valid; use m:ss.");
        ProblemsText = string.Join(" ", problems);
    }
}

public sealed partial class OptionViewModel : ObservableObject
{
    private readonly Action<OptionViewModel> _remove;

    [ObservableProperty] public partial string Text { get; set; }
    [ObservableProperty] public partial bool IsCorrect { get; set; }
    [ObservableProperty] public partial string Feedback { get; set; }

    public OptionViewModel(AnswerOption model, Action<OptionViewModel> remove, Action changed)
    {
        _remove = remove;
        Text = model.Text;
        IsCorrect = model.IsCorrect;
        Feedback = model.Feedback ?? "";
        PropertyChanged += (_, _) => changed();
    }

    [RelayCommand]
    private void Remove() => _remove(this);

    public AnswerOption ToModel() => new(Text.Trim(), IsCorrect, string.IsNullOrWhiteSpace(Feedback) ? null : Feedback.Trim());
}
