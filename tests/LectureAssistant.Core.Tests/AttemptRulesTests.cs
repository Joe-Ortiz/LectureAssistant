using System.Text.Json;
using LectureAssistant.Core.Models;
using LectureAssistant.Core.Persistence;

namespace LectureAssistant.Core.Tests;

public class AttemptRulesTests
{
    [Fact]
    public void New_quizzes_keep_unlimited_practice_retries()
    {
        var quiz = new QuizSettings();
        Assert.Equal(AttemptRules.Unlimited, quiz.AttemptsAllowed);
        Assert.Equal(RetryScoring.FirstAttemptOnly, quiz.RetryScoring);
        Assert.Equal(50, quiz.RetryPenaltyPercent);

        var question = new Question();
        Assert.Null(question.AttemptsAllowed);
        Assert.Null(question.RetryScoring);
    }

    [Fact]
    public void Questions_use_the_quiz_settings_unless_overridden()
    {
        var quiz = new QuizSettings { AttemptsAllowed = 3, RetryScoring = RetryScoring.FullCredit, RetryPenaltyPercent = 25 };

        Assert.Equal(new AttemptRules(3, RetryScoring.FullCredit, 25), AttemptRules.For(new Question(), quiz));
        Assert.Equal(
            new AttemptRules(1, RetryScoring.ReducedCredit, 25),
            AttemptRules.For(new Question { AttemptsAllowed = 1, RetryScoring = RetryScoring.ReducedCredit }, quiz));
        Assert.Equal(
            new AttemptRules(AttemptRules.Unlimited, RetryScoring.FullCredit, 25),
            AttemptRules.For(new Question { AttemptsAllowed = 0 }, quiz));
    }

    [Fact]
    public void Out_of_range_values_are_normalized()
    {
        var rules = AttemptRules.For(new Question { AttemptsAllowed = -4 }, new QuizSettings { RetryPenaltyPercent = 250 });
        Assert.True(rules.IsUnlimited);
        Assert.Equal(100, rules.RetryPenaltyPercent);
        Assert.Equal(0, AttemptRules.For(new Question(), new QuizSettings { RetryPenaltyPercent = -10 }).RetryPenaltyPercent);
    }

    [Theory]
    // First try is always full credit.
    [InlineData(3, RetryScoring.FirstAttemptOnly, 50, 1, 100)]
    [InlineData(3, RetryScoring.FullCredit, 50, 1, 100)]
    [InlineData(3, RetryScoring.ReducedCredit, 50, 1, 100)]
    // Only the first attempt counts.
    [InlineData(0, RetryScoring.FirstAttemptOnly, 50, 2, 0)]
    // Full credit within the limit, nothing beyond it.
    [InlineData(3, RetryScoring.FullCredit, 50, 3, 100)]
    [InlineData(3, RetryScoring.FullCredit, 50, 4, 0)]
    [InlineData(0, RetryScoring.FullCredit, 50, 40, 100)]
    // Reduced credit: 100, 50, 0, never negative.
    [InlineData(5, RetryScoring.ReducedCredit, 50, 2, 50)]
    [InlineData(5, RetryScoring.ReducedCredit, 50, 3, 0)]
    [InlineData(5, RetryScoring.ReducedCredit, 50, 5, 0)]
    [InlineData(0, RetryScoring.ReducedCredit, 25, 4, 25)]
    [InlineData(0, RetryScoring.ReducedCredit, 30, 4, 10)]
    [InlineData(0, RetryScoring.ReducedCredit, 30, 5, 0)]
    [InlineData(0, RetryScoring.ReducedCredit, 1, int.MaxValue, 0)]
    // A single attempt never gives credit for a second one.
    [InlineData(1, RetryScoring.FullCredit, 50, 2, 0)]
    // Not an attempt.
    [InlineData(3, RetryScoring.FullCredit, 50, 0, 0)]
    public void Credit_for_a_correct_answer(int attempts, RetryScoring scoring, int penalty, int attempt, int expected)
    {
        Assert.Equal(expected, new AttemptRules(attempts, scoring, penalty).CreditPercent(attempt));
    }

    [Fact]
    public void Points_earned_scale_with_credit()
    {
        var rules = new AttemptRules(3, RetryScoring.ReducedCredit, 50);
        Assert.Equal(3, rules.PointsEarned(3, 1));
        Assert.Equal(1.5, rules.PointsEarned(3, 2));
        Assert.Equal(0, rules.PointsEarned(3, 3));
    }

    [Theory]
    [InlineData(true, AttemptRules.Unlimited)]
    [InlineData(false, 1)]
    public void Projects_saved_with_allow_retry_still_load(bool allowRetry, int expectedAttempts)
    {
        var json = $$"""
            {
              "title": "Old lecture",
              "quiz": { "passingScorePercent": 80, "preventSkippingAhead": false, "allowRetry": {{(allowRetry ? "true" : "false")}}, "showCorrectAnswers": false },
              "questions": [ { "type": "TrueFalse", "prompt": "Old question", "correctAnswer": true, "points": 1 } ]
            }
            """;

        var project = JsonSerializer.Deserialize<LectureProject>(json, ProjectStore.JsonOptions)!;

        Assert.Equal(expectedAttempts, project.Quiz.AttemptsAllowed);
        Assert.Equal(RetryScoring.FirstAttemptOnly, project.Quiz.RetryScoring);
        Assert.Equal(80, project.Quiz.PassingScorePercent);
        Assert.False(project.Quiz.ShowCorrectAnswers);
        Assert.Null(project.Questions[0].AttemptsAllowed);
        Assert.Null(project.Questions[0].RetryScoring);
    }

    [Fact]
    public void Attempt_settings_round_trip_and_allow_retry_is_not_written()
    {
        var project = new LectureProject
        {
            Quiz = new QuizSettings { AttemptsAllowed = 3, RetryScoring = RetryScoring.ReducedCredit, RetryPenaltyPercent = 25 },
            Questions =
            [
                new() { Type = QuestionType.TrueFalse, Prompt = "A", AttemptsAllowed = 1 },
                new() { Type = QuestionType.TrueFalse, Prompt = "B", RetryScoring = RetryScoring.FullCredit },
                new() { Type = QuestionType.TrueFalse, Prompt = "C" },
            ],
        };

        var json = JsonSerializer.Serialize(project, ProjectStore.JsonOptions);
        Assert.DoesNotContain("allowRetry", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"retryScoring\": \"ReducedCredit\"", json);

        var loaded = JsonSerializer.Deserialize<LectureProject>(json, ProjectStore.JsonOptions)!;
        Assert.Equal(3, loaded.Quiz.AttemptsAllowed);
        Assert.Equal(RetryScoring.ReducedCredit, loaded.Quiz.RetryScoring);
        Assert.Equal(25, loaded.Quiz.RetryPenaltyPercent);
        Assert.Equal(1, loaded.Questions[0].AttemptsAllowed);
        Assert.Null(loaded.Questions[0].RetryScoring);
        Assert.Equal(RetryScoring.FullCredit, loaded.Questions[1].RetryScoring);
        Assert.Null(loaded.Questions[2].AttemptsAllowed);
    }
}
