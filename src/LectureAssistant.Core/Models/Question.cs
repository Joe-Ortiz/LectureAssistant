namespace LectureAssistant.Core.Models;

public enum QuestionType
{
    /// <summary>One or more <see cref="Question.Options"/> are correct.</summary>
    MultipleChoice,

    /// <summary>Answer is <see cref="Question.CorrectAnswer"/>; <see cref="Question.Options"/> is unused.</summary>
    TrueFalse,

    /// <summary>
    /// <see cref="Question.Prompt"/> contains exactly one <see cref="Question.BlankMarker"/>;
    /// any of <see cref="Question.AcceptedAnswers"/> is correct (case-insensitive).
    /// </summary>
    FillInTheBlank,
}

/// <summary>A question that pauses the video at <see cref="Timestamp"/>.</summary>
public sealed class Question
{
    public const string BlankMarker = "___";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TimeSpan Timestamp { get; set; }
    public QuestionType Type { get; set; }
    public string Prompt { get; set; } = "";

    /// <summary>MultipleChoice only.</summary>
    public List<AnswerOption> Options { get; set; } = [];

    /// <summary>TrueFalse only.</summary>
    public bool CorrectAnswer { get; set; }

    /// <summary>FillInTheBlank only.</summary>
    public List<string> AcceptedAnswers { get; set; } = [];

    /// <summary>Shown after answering; explains why the answer is right.</summary>
    public string? Explanation { get; set; }

    public int Points { get; set; } = 1;

    /// <summary>Transcript excerpt the question was generated from, for instructor review.</summary>
    public string? SourceExcerpt { get; set; }

    /// <summary>Returns problems that would make this question unusable in an export; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Prompt)) problems.Add("Question text is empty.");
        if (Timestamp < TimeSpan.Zero) problems.Add("Timestamp is negative.");
        if (Points <= 0) problems.Add("Points must be positive.");

        switch (Type)
        {
            case QuestionType.MultipleChoice:
                if (Options.Count < 2) problems.Add("Multiple choice needs at least two options.");
                if (!Options.Any(o => o.IsCorrect)) problems.Add("Multiple choice needs at least one correct option.");
                if (Options.Any(o => string.IsNullOrWhiteSpace(o.Text))) problems.Add("An option is empty.");
                break;
            case QuestionType.FillInTheBlank:
                var blanks = CountBlanks(Prompt);
                if (blanks != 1) problems.Add($"Fill in the blank needs exactly one '{BlankMarker}' (found {blanks}).");
                if (!AcceptedAnswers.Any(a => !string.IsNullOrWhiteSpace(a))) problems.Add("Fill in the blank needs an accepted answer.");
                break;
        }
        return problems;
    }

    private static int CountBlanks(string text)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(BlankMarker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            // Treat a run of underscores as one blank.
            while (index < text.Length && text[index] == '_') index++;
        }
        return count;
    }
}

public sealed class AnswerOption
{
    public string Text { get; set; } = "";
    public bool IsCorrect { get; set; }

    /// <summary>Optional feedback shown when this option is chosen.</summary>
    public string? Feedback { get; set; }

    public AnswerOption() { }

    public AnswerOption(string text, bool isCorrect, string? feedback = null)
    {
        Text = text;
        IsCorrect = isCorrect;
        Feedback = feedback;
    }
}
