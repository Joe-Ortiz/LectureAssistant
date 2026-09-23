using System.Globalization;
using System.Text;
using LectureAssistant.Core;
using LectureAssistant.Core.Captions;
using LectureAssistant.Core.Models;

namespace LectureAssistant.QuestionGeneration;

/// <summary>A slice of the lecture sent on its own when the model's context is too small for the whole transcript.</summary>
internal sealed record TranscriptSection(int Number, int Of, IReadOnlyList<CaptionSegment> Segments);

/// <summary>Builds the system prompt and user message shared by the Claude and local generators.</summary>
internal static class QuestionPrompt
{
    public const string SystemPrompt = """
        You write in-video quiz questions for recorded university lectures. The video pauses at each question's
        timestamp, the student answers, and then the lecture continues. An instructor reviews and edits every
        question before students see it, so accuracy and good placement matter more than cleverness.

        The transcript comes from automatic speech recognition. Each line starts with a marker like [754s]: the
        number of seconds from the start of the video at which that line begins. Expect occasional
        mis-transcribed words; infer the intended term from context and spell it correctly in your questions.

        What to ask about
        - Check understanding of the key ideas the lecturer has just explained: concepts, definitions,
          relationships, reasoning steps, how to apply a method, why something is true.
        - Skip trivia, jokes, anecdotes, asides, course logistics (deadlines, office hours, exams), and anything
          the lecturer only mentions in passing or says will be covered later.
        - Ask about the idea, not the wording: prefer "which of these follows from..." or "what happens when..."
          over recalling an exact phrase or number the lecturer happened to use.
        - Ground everything in this transcript. Do not introduce facts, examples or terminology the lecturer did
          not use, even if they are true. If the lecturer makes an error, do not build a question on it.
        - Each question should stand on its own for a student who just watched that part, and must not give
          away the answer to another question.

        Where to place each question
        - timestamp_seconds is the number from the [Ns] marker on the transcript line where the relevant
          explanation finishes. The app moves the pause to the end of that line. Never place a question before
          the idea has been taught, and never in the first 30 seconds of the video.
        - Spread questions across the whole lecture (or the whole section you are given) rather than
          clustering them, and keep them at least the requested minimum spacing apart.

        Question types
        - multiple_choice: 3 to 5 options. Usually one is correct; mark several correct only when the prompt
          clearly asks the student to select all that apply. Distractors must be plausible to a student who
          half-understood the explanation (common misconceptions, a related but different concept, a
          reversed relationship), similar in length, grammar and specificity to the correct option, and
          clearly wrong to someone who understood. Avoid "all of the above", "none of the above", and joke
          options. Give each option one sentence of feedback explaining why it is right or wrong.
        - true_false: a single clear statement that is unambiguously true or false according to the lecture.
          Avoid double negatives and "always/never" giveaways. Vary which answer is correct.
        - fill_in_the_blank: a statement with exactly one blank written as ___ (three underscores) where a key
          term belongs. The blank must be answerable with a short term (one to three words). List every answer
          that should be accepted: synonyms, abbreviations, singular/plural and common spelling variants.
        - Use a mix of the allowed types unless the instructor asks otherwise.

        Every question also needs
        - explanation: two or three sentences on why the answer is correct, based on what the lecturer said.
        - source_excerpt: a short verbatim quote (one or two sentences) from the transcript that the question
          is based on.
        - Fields that do not apply to a question's type are left empty: options = [], accepted_answers = [],
          correct_answer = false.

        Follow the instructor's guidance (topic focus, level of the students, style) when it is given.
        Write in the language of the lecture. Return only the JSON object.
        """;

    public static string BuildUserMessage(QuestionGenerationRequest request, int questionCount, TranscriptSection? section = null)
    {
        var segments = section?.Segments ?? request.Transcript;
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(request.LectureTitle))
            sb.Append("Lecture title: ").Append(request.LectureTitle.Trim()).Append('\n');

        if (section is not null && section.Of > 1)
        {
            var ordered = Ordered(segments).ToList();
            sb.Append(CultureInfo.InvariantCulture,
                $"This is section {section.Number} of {section.Of} of the lecture, from {CaptionFormats.Clock(ordered[0].Start)} to {CaptionFormats.Clock(ordered[^1].End)}. ");
            sb.Append("Other sections are handled separately; ask only about this section.\n");
        }

        var typeNames = QuestionDraftSchema.TypeNamesFor(request.AllowedTypes);
        sb.Append(CultureInfo.InvariantCulture, $"Write exactly {questionCount} question{(questionCount == 1 ? "" : "s")}.\n");
        sb.Append("Allowed types: ").AppendJoin(", ", typeNames).Append('\n');
        sb.Append(CultureInfo.InvariantCulture, $"Minimum spacing between questions: {Math.Round(request.MinimumSpacing.TotalSeconds)} seconds.\n");

        if (!string.IsNullOrWhiteSpace(request.InstructorGuidance))
        {
            sb.Append("\nInstructor guidance:\n<guidance>\n").Append(request.InstructorGuidance.Trim()).Append("\n</guidance>\n");
        }

        sb.Append("\n<transcript>\n").Append(FormatTranscript(segments)).Append("</transcript>");
        return sb.ToString();
    }

    public static string FormatTranscript(IEnumerable<CaptionSegment> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in Ordered(segments)) sb.Append(FormatLine(s)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// "[754s] text". Whole seconds, rounded up, so the marker lies inside the segment and snapping lands on
    /// its end rather than the previous line's. Seconds instead of m:ss so small models copy rather than compute.
    /// </summary>
    public static string FormatLine(CaptionSegment segment) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[{(long)Math.Ceiling(Math.Max(0, segment.Start.TotalSeconds))}s] {Whitespace(segment.Text)}");

    public static IEnumerable<CaptionSegment> Ordered(IEnumerable<CaptionSegment> segments) =>
        segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)).OrderBy(s => s.Start);

    /// <summary>Throws a friendly exception for requests no model can satisfy.</summary>
    public static void EnsureUsable(QuestionGenerationRequest request)
    {
        if (!Ordered(request.Transcript).Any())
            throw new QuestionGenerationException(QuestionGenerationFailure.BadRequest,
                "This lecture has no transcript yet. Transcribe the video before generating questions.");
        if (request.AllowedTypes.Count == 0)
            throw new QuestionGenerationException(QuestionGenerationFailure.BadRequest,
                "Choose at least one question type.");
        if (request.QuestionCount <= 0)
            throw new QuestionGenerationException(QuestionGenerationFailure.BadRequest,
                "Choose how many questions to generate.");
    }

    private static string Whitespace(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
