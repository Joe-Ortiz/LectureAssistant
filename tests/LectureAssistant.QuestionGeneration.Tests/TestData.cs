using LectureAssistant.Core;
using LectureAssistant.Core.Models;
using LectureAssistant.QuestionGeneration;

namespace LectureAssistant.QuestionGeneration.Tests;

internal static class TestData
{
    /// <summary>Back-to-back segments of <paramref name="segmentSeconds"/> covering <paramref name="totalSeconds"/>.</summary>
    public static List<CaptionSegment> Transcript(int totalSeconds = 600, int segmentSeconds = 10)
    {
        var list = new List<CaptionSegment>();
        for (int s = 0; s < totalSeconds; s += segmentSeconds)
            list.Add(new CaptionSegment(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(s + segmentSeconds), $"Sentence starting at {s} seconds."));
        return list;
    }

    public static QuestionGenerationRequest Request(
        List<CaptionSegment>? transcript = null,
        int count = 10,
        TimeSpan? spacing = null,
        params QuestionType[] types) => new()
        {
            Transcript = transcript ?? Transcript(),
            QuestionCount = count,
            MinimumSpacing = spacing ?? TimeSpan.Zero,
            AllowedTypes = types.Length > 0 ? types : [QuestionType.MultipleChoice, QuestionType.TrueFalse, QuestionType.FillInTheBlank],
        };

    public static QuestionDraft Mc(double seconds, string prompt = "Which is right?") => new()
    {
        TimestampSeconds = seconds,
        Type = QuestionDraftSchema.MultipleChoice,
        Prompt = prompt,
        Options =
        [
            new() { Text = "Right", IsCorrect = true, Feedback = "Yes." },
            new() { Text = "Wrong", IsCorrect = false, Feedback = "No." },
            new() { Text = "Also wrong", IsCorrect = false, Feedback = "" },
        ],
        CorrectAnswer = false,
        AcceptedAnswers = [],
        Explanation = "Because the lecturer said so.",
        SourceExcerpt = "Right is right.",
    };

    public static QuestionDraft Tf(double seconds, bool? answer = true, string prompt = "The sky is blue.") => new()
    {
        TimestampSeconds = seconds,
        Type = QuestionDraftSchema.TrueFalse,
        Prompt = prompt,
        Options = [],
        CorrectAnswer = answer,
        AcceptedAnswers = [],
        Explanation = "Rayleigh scattering.",
    };

    public static QuestionDraft Fib(double seconds, string prompt = "The powerhouse of the cell is the ___.", params string[] answers) => new()
    {
        TimestampSeconds = seconds,
        Type = QuestionDraftSchema.FillInTheBlank,
        Prompt = prompt,
        Options = [],
        CorrectAnswer = false,
        AcceptedAnswers = answers.Length > 0 ? [.. answers] : ["mitochondrion", "mitochondria"],
        Explanation = "It makes ATP.",
    };
}
