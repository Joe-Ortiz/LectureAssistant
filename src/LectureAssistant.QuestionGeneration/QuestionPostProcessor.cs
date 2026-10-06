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

    /// <param name="random">Shuffles multiple-choice options; pass a seeded one for repeatable output.</param>
    public static IReadOnlyList<Question> Process(IEnumerable<QuestionDraft> drafts, QuestionGenerationRequest request, Random? random = null)
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
        var spacing = EffectiveSpacing(segments, request);
        var spaced = new List<Question>();
        foreach (var q in ordered)
        {
            if (spaced.Count > 0 && q.Timestamp - spaced[^1].Timestamp < spacing)
            {
                // Too close to the previous question: move it to a later sentence end if one is near enough
                // that the question still follows the material it's about; otherwise drop it.
                var earliest = spaced[^1].Timestamp + spacing;
                var slot = segments.Select(s => s.End).FirstOrDefault(end => end >= earliest);
                if (slot == default || slot - q.Timestamp > MaxShift) continue;
                q.Timestamp = slot;
            }
            spaced.Add(q);
        }

        var result = SpreadTrim(spaced, request.QuestionCount);
        ShuffleOptions(result, random ?? Random.Shared);
        return result;
    }

    /// <summary>How far a question may be pushed later to keep questions apart.</summary>
    public static readonly TimeSpan MaxShift = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The requested spacing, relaxed for short videos so the requested number of questions can fit
    /// (never below 15 seconds, so students aren't interrupted constantly).
    /// </summary>
    internal static TimeSpan EffectiveSpacing(IReadOnlyList<Timeline.Span> segments, QuestionGenerationRequest request)
    {
        var usable = segments[^1].End - (segments[^1].End > LeadIn ? LeadIn : TimeSpan.Zero);
        var fit = usable / Math.Max(1, request.QuestionCount + 1);
        var floor = TimeSpan.FromSeconds(15);
        return request.MinimumSpacing <= fit ? request.MinimumSpacing : fit < floor ? floor : fit;
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
                if (draft.CorrectAnswer is not { } answer)
                {
                    question.Prompt = ""; // no answer given: invalid
                    break;
                }
                // The explanation is written first and holds the reasoning; if it clearly concludes the
                // opposite of the answer that followed, the answer is the slip.
                question.CorrectAnswer = StatedVerdict(question.Explanation) ?? answer;
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
    /// The true/false verdict a model's explanation clearly states ("False. …", "This statement is true because …",
    /// "… So the statement is false."), or null when it states none or contradicts itself. Deliberately narrow:
    /// anything hedged or ambiguous is left to the model's answer.
    /// </summary>
    internal static bool? StatedVerdict(string? explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation)) return null;
        var opening = Verdict(OpeningVerdict().Match(explanation));
        var closing = Verdict(ClosingVerdict().Match(explanation));
        if (opening is not null && closing is not null && opening != closing) return null;
        return opening ?? closing;

        static bool? Verdict(Match m) =>
            m.Success ? m.Groups["v"].Value.ToLowerInvariant() is "true" or "correct" : null;
    }

    /// <summary>
    /// Models tend to write the correct option first. Shuffles each multiple-choice question's options (feedback
    /// stays with its option) and moves a single correct option to the slot used least so far among questions
    /// with as many options, ties broken at random: a shuffled round-robin, so the answer isn't in the same
    /// place for most questions. Options that refer to others by position ("all of the above") keep their slot.
    /// Only newly generated questions come through here, so the instructor reviews the final order and every
    /// export uses it.
    /// </summary>
    internal static void ShuffleOptions(IEnumerable<Question> questions, Random random)
    {
        var uses = new Dictionary<(int Count, int Slot), int>();
        foreach (var q in questions)
        {
            if (q.Type != QuestionType.MultipleChoice) continue;
            var slots = Enumerable.Range(0, q.Options.Count).Where(i => !RefersToOtherOptions().IsMatch(q.Options[i].Text)).ToArray();
            if (slots.Length < 2) continue;

            var options = slots.Select(i => q.Options[i]).ToArray();
            random.Shuffle(options);

            var from = Array.FindIndex(options, o => o.IsCorrect);
            if (from >= 0 && q.Options.Count(o => o.IsCorrect) == 1)
            {
                var count = q.Options.Count;
                var fewest = slots.Min(s => uses.GetValueOrDefault((count, s)));
                var candidates = Array.FindAll(slots, s => uses.GetValueOrDefault((count, s)) == fewest);
                var target = candidates[random.Next(candidates.Length)];
                var to = Array.IndexOf(slots, target);
                (options[from], options[to]) = (options[to], options[from]);
                uses[(count, target)] = fewest + 1;
            }

            for (int k = 0; k < slots.Length; k++) q.Options[slots[k]] = options[k];
        }
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

    /// <summary>"True." / "Incorrect:" / "This statement is false because" / "This is correct." at the start.</summary>
    [GeneratedRegex(@"^[\s*""'“]*(?:(?<v>true|false|correct|incorrect)\s*[.!:]|(?:the|this) statement is (?<v>true|false|correct|incorrect)\s*(?:[.!:;]|$|because\b|since\b)|this is (?<v>true|false|correct|incorrect)\s*[.!:])", RegexOptions.IgnoreCase)]
    private static partial Regex OpeningVerdict();

    /// <summary>"… so the statement is false." / "… This statement is therefore true." / "… so it is true." at the end.</summary>
    [GeneratedRegex(@"(?<!\b(?:whether|if)\s)\b(?:(?:the|this) statement|so,? (?:it|this)) is (?:therefore |thus |hence )?(?<v>true|false|correct|incorrect)[\s*""'”]*[.!]?[\s*""'”]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingVerdict();

    /// <summary>Options whose meaning depends on where they sit: "All of the above", "Both A and B", "Option C".</summary>
    [GeneratedRegex(@"\b(?:all|none|both|neither|any) of the above\b|\b(?:all|none|both|neither) of these\b|\b(?:above|previous|preceding) (?:options?|answers?|choices?)\b|\b(?:options?|answers?|choices?) (?-i:[A-E])\b|^(?:both |only )?(?-i:[A-E]) and (?-i:[A-E])\b", RegexOptions.IgnoreCase)]
    private static partial Regex RefersToOtherOptions();
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
