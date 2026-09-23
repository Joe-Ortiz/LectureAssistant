using System.Text.RegularExpressions;
using LectureAssistant.Core;
using LectureAssistant.Core.Models;

namespace LectureAssistant.QuestionGeneration;

/// <summary>
/// Turns model drafts into valid Core <see cref="Question"/>s placed sensibly on the timeline.
/// Everything a model can get wrong is caught here, so both generators share it.
/// </summary>
public static partial class QuestionPostProcessor
{
    /// <summary>No question is placed before this point (unless the whole video is shorter).</summary>
    public static readonly TimeSpan LeadIn = TimeSpan.FromSeconds(30);

    private const int MaxExcerptLength = 400;

    public static IReadOnlyList<Question> Process(IEnumerable<QuestionDraft> drafts, QuestionGenerationRequest request)
    {
        var segments = Timeline.From(request.Transcript);
        if (segments.Count == 0 || request.QuestionCount <= 0) return [];

        var allowed = request.AllowedTypes.ToHashSet();
        var seenPrompts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Question>();

        foreach (var draft in drafts)
        {
            if (draft is null) continue;
            var type = QuestionDraftSchema.ParseType(draft.Type);
            if (type is not { } t || !allowed.Contains(t)) continue;
            if (!double.IsFinite(draft.TimestampSeconds)) continue;

            var question = ToQuestion(draft, t);
            question.Timestamp = Snap(segments, draft.TimestampSeconds);
            if (question.Validate().Count > 0) continue;
            if (!seenPrompts.Add(NormalizeWhitespace(question.Prompt))) continue;

            candidates.Add(question);
        }

        // Stable sort keeps the model's order for equal timestamps; the earlier one then wins spacing.
        var ordered = candidates.OrderBy(q => q.Timestamp).ToList();
        var spaced = new List<Question>();
        foreach (var q in ordered)
        {
            if (spaced.Count > 0 && q.Timestamp - spaced[^1].Timestamp < request.MinimumSpacing) continue;
            spaced.Add(q);
        }

        return SpreadTrim(spaced, request.QuestionCount);
    }

    internal static Question ToQuestion(QuestionDraft draft, QuestionType type)
    {
        var question = new Question
        {
            Type = type,
            Prompt = Clean(draft.Prompt),
            Explanation = NullIfEmpty(Clean(draft.Explanation)),
            SourceExcerpt = NullIfEmpty(Truncate(Clean(draft.SourceExcerpt), MaxExcerptLength)),
            Points = 1,
        };

        switch (type)
        {
            case QuestionType.MultipleChoice:
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in draft.Options ?? [])
                {
                    var text = Clean(o?.Text);
                    if (o is null || text.Length == 0 || !seen.Add(text)) continue;
                    question.Options.Add(new AnswerOption(text, o.IsCorrect, NullIfEmpty(Clean(o.Feedback))));
                }
                // All-correct questions test nothing.
                if (question.Options.Count > 0 && question.Options.All(o => o.IsCorrect)) question.Options.Clear();
                break;

            case QuestionType.TrueFalse:
                question.CorrectAnswer = draft.CorrectAnswer ?? false;
                if (draft.CorrectAnswer is null) question.Prompt = ""; // no answer given: invalid
                break;

            case QuestionType.FillInTheBlank:
                question.Prompt = BlankRun().Replace(question.Prompt, Question.BlankMarker);
                question.AcceptedAnswers = (draft.AcceptedAnswers ?? [])
                    .Select(Clean)
                    .Where(a => a.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                break;
        }
        return question;
    }

    /// <summary>
    /// Moves a model-supplied time to the end of the segment containing it (or the last one starting before it),
    /// so the video pauses after the sentence finishes; clamps into the transcript and past <see cref="LeadIn"/>.
    /// </summary>
    internal static TimeSpan Snap(IReadOnlyList<Timeline.Span> segments, double seconds)
    {
        var first = segments[0];
        var last = segments[^1];
        var t = seconds <= 0 ? TimeSpan.Zero : SafeFromSeconds(seconds);
        if (t < first.Start) t = first.Start;
        if (t > last.End) t = last.End;

        var index = LastStartingAtOrBefore(segments, t);
        var snapped = segments[index].End;

        if (snapped < LeadIn && last.End > LeadIn)
        {
            for (int i = index; i < segments.Count; i++)
            {
                if (segments[i].End >= LeadIn) { snapped = segments[i].End; break; }
            }
        }
        return snapped;
    }

    /// <summary>Keeps <paramref name="count"/> items evenly spread across the list (which is in time order).</summary>
    internal static IReadOnlyList<Question> SpreadTrim(IReadOnlyList<Question> questions, int count)
    {
        if (questions.Count <= count) return questions;
        if (count <= 0) return [];
        if (count == 1) return [questions[(questions.Count - 1) / 2]];

        var result = new List<Question>(count);
        for (int i = 0; i < count; i++)
        {
            var index = (int)Math.Round(i * (questions.Count - 1) / (double)(count - 1), MidpointRounding.AwayFromZero);
            result.Add(questions[index]);
        }
        return result;
    }

    private static int LastStartingAtOrBefore(IReadOnlyList<Timeline.Span> segments, TimeSpan t)
    {
        int lo = 0, hi = segments.Count - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (segments[mid].Start <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    private static TimeSpan SafeFromSeconds(double seconds) =>
        seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);

    private static string Clean(string? text) => text is null ? "" : NormalizeWhitespace(text);

    private static string NormalizeWhitespace(string text) => Whitespace().Replace(text, " ").Trim();

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>A run of 3+ underscores, optionally with spaces between ("_ _ _"), is one blank.</summary>
    [GeneratedRegex(@"_(?:\s?_){2,}")]
    private static partial Regex BlankRun();
}

/// <summary>Transcript segments cleaned up for placement: non-empty, ordered, End never before Start.</summary>
internal static class Timeline
{
    internal readonly record struct Span(TimeSpan Start, TimeSpan End);

    public static IReadOnlyList<Span> From(IEnumerable<CaptionSegment> transcript) =>
        transcript
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .OrderBy(s => s.Start)
            .Select(s => new Span(s.Start < TimeSpan.Zero ? TimeSpan.Zero : s.Start, s.End < s.Start ? s.Start : s.End))
            .ToList();
}
