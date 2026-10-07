namespace LectureAssistant.Core.Models;

/// <summary>How a correct answer on a retry is scored.</summary>
public enum RetryScoring
{
    /// <summary>Only the first attempt is scored; retries are practice.</summary>
    FirstAttemptOnly,

    /// <summary>A correct answer on any allowed attempt earns full points.</summary>
    FullCredit,

    /// <summary>Each retry earns <see cref="QuizSettings.RetryPenaltyPercent"/> less of the points, never below zero.</summary>
    ReducedCredit,
}

/// <summary>
/// The attempt limit and retry scoring that apply to one question (its own overrides, otherwise the quiz settings),
/// and the credit a correct answer earns. The SCORM player's <c>creditPercent</c> in player.js mirrors
/// <see cref="CreditPercent"/> exactly, so keep the two in step.
/// </summary>
/// <param name="AttemptsAllowed">At least 1, or <see cref="Unlimited"/>.</param>
/// <param name="RetryPenaltyPercent">0-100; only used with <see cref="RetryScoring.ReducedCredit"/>.</param>
public sealed record AttemptRules(int AttemptsAllowed, RetryScoring Scoring, int RetryPenaltyPercent)
{
    /// <summary>An <see cref="AttemptsAllowed"/> of 0 means students can answer as many times as they like.</summary>
    public const int Unlimited = 0;

    public const int DefaultRetryPenaltyPercent = 50;

    public bool IsUnlimited => AttemptsAllowed == Unlimited;

    /// <summary>True when students get more than one attempt.</summary>
    public bool AllowsRetries => AttemptsAllowed != 1;

    /// <summary>The rules for <paramref name="question"/>: its overrides where set, otherwise <paramref name="quiz"/>'s.</summary>
    public static AttemptRules For(Question question, QuizSettings quiz) => new(
        NormalizeAttempts(question.AttemptsAllowed ?? quiz.AttemptsAllowed),
        question.RetryScoring ?? quiz.RetryScoring,
        Math.Clamp(quiz.RetryPenaltyPercent, 0, 100));

    /// <summary>Zero or less means unlimited.</summary>
    public static int NormalizeAttempts(int attempts) => attempts <= 0 ? Unlimited : attempts;

    /// <summary>
    /// Percent (0-100) of the question's points earned by a correct answer on <paramref name="attempt"/>
    /// (1 = first try). Attempts beyond the limit earn nothing.
    /// </summary>
    public int CreditPercent(int attempt)
    {
        if (attempt < 1 || (!IsUnlimited && attempt > AttemptsAllowed)) return 0;
        if (attempt == 1) return 100;
        return Scoring switch
        {
            RetryScoring.FullCredit => 100,
            RetryScoring.ReducedCredit => (int)Math.Max(0, 100 - (long)RetryPenaltyPercent * (attempt - 1)),
            _ => 0,
        };
    }

    /// <summary>Points earned by a correct answer worth <paramref name="points"/> on <paramref name="attempt"/>.</summary>
    public double PointsEarned(int points, int attempt) => points * CreditPercent(attempt) / 100.0;
}
