using LectureAssistant.Core.Models;

namespace LectureAssistant.App.ViewModels;

/// <summary>
/// Picker choices for attempts and retry scoring, shared by the quiz settings and each question.
/// Question pickers start with "Quiz default" (index 0), so their indexes are shifted by one.
/// </summary>
public static class AttemptChoices
{
    private const string QuizDefault = "Quiz default";

    /// <summary>Attempt limits in picker order; <see cref="AttemptRules.Unlimited"/> last.</summary>
    private static readonly int[] AttemptValues = [1, 2, 3, 4, 5, AttemptRules.Unlimited];

    private static readonly RetryScoring[] ScoringValues = [RetryScoring.FullCredit, RetryScoring.ReducedCredit, RetryScoring.FirstAttemptOnly];

    public static IReadOnlyList<string> AttemptNames { get; } = ["1 (no retries)", "2", "3", "4", "5", "Unlimited"];

    public static IReadOnlyList<string> ScoringNames { get; } = ["Full credit", "Reduced credit for each retry", "Only the first attempt counts"];

    public static IReadOnlyList<string> QuestionAttemptNames { get; } = [QuizDefault, .. AttemptNames];

    public static IReadOnlyList<string> QuestionScoringNames { get; } = [QuizDefault, .. ScoringNames];

    /// <summary>Picker index for a limit. Limits the picker doesn't offer (only possible in hand-edited files) show as the nearest one.</summary>
    public static int AttemptsIndex(int attempts)
    {
        attempts = AttemptRules.NormalizeAttempts(attempts);
        var index = Array.IndexOf(AttemptValues, attempts);
        return index >= 0 ? index : Array.IndexOf(AttemptValues, 5);
    }

    public static int Attempts(int index) => AttemptValues[Math.Clamp(index, 0, AttemptValues.Length - 1)];

    public static int ScoringIndex(RetryScoring scoring) => Math.Max(0, Array.IndexOf(ScoringValues, scoring));

    public static RetryScoring Scoring(int index) => ScoringValues[Math.Clamp(index, 0, ScoringValues.Length - 1)];

    public static int OverrideAttemptsIndex(int? attempts) => attempts is { } a ? AttemptsIndex(a) + 1 : 0;

    public static int? OverrideAttempts(int index) => index <= 0 ? null : Attempts(index - 1);

    public static int OverrideScoringIndex(RetryScoring? scoring) => scoring is { } s ? ScoringIndex(s) + 1 : 0;

    public static RetryScoring? OverrideScoring(int index) => index <= 0 ? null : Scoring(index - 1);
}
