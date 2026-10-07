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
        // The instructor's minimum is strict: a question that can't keep it is dropped, never squeezed in.
        var ordered = candidates.OrderBy(q => q.Timestamp).ToList();
        var spacing = request.MinimumSpacing > TimeSpan.Zero ? request.MinimumSpacing : TimeSpan.Zero;
        var reserved = request.ReservedTimes.ToList();
        var slots = Slots(segments);
        var spaced = new List<Question>();
        foreach (var q in ordered)
        {
            // Too close to the previous question or a kept one: move it to a later sentence end if one is near
            // enough that the question still follows the material it's about; otherwise drop it.
            var earliest = q.Timestamp;
            if (spaced.Count > 0 && spaced[^1].Timestamp + spacing > earliest) earliest = spaced[^1].Timestamp + spacing;
            var slot = slots.FirstOrDefault(s => s >= earliest && IsClear(s, reserved, spacing), TimeSpan.MinValue);
            if (slot == TimeSpan.MinValue || slot - q.Timestamp > MaxShift) continue;
            q.Timestamp = slot;
            spaced.Add(q);
        }

        var result = SpreadTrim(spaced, request.QuestionCount);
        ShuffleOptions(result, random ?? Random.Shared);
        return result;
    }

    /// <summary>How far a question may be pushed later to keep questions apart.</summary>
    public static readonly TimeSpan MaxShift = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The most new questions that can fit on the lecture's timeline with the request's minimum spacing, away from
    /// its reserved times (<see cref="int.MaxValue"/> when there's no minimum).
    /// </summary>
    public static int Capacity(QuestionGenerationRequest request) =>
        Capacity(request.Transcript, request.MinimumSpacing, request.ReservedTimes);

    /// <inheritdoc cref="Capacity(QuestionGenerationRequest)"/>
    internal static int Capacity(IEnumerable<CaptionSegment> transcript, TimeSpan spacing, IEnumerable<TimeSpan> reservedTimes)
    {
        var segments = Timeline.From(transcript);
        if (segments.Count == 0) return 0;
        if (spacing <= TimeSpan.Zero) return int.MaxValue;

        // Taking the earliest usable sentence end each time fits the most questions.
        var reserved = reservedTimes.ToList();
        int count = 0;
        TimeSpan? previous = null;
        foreach (var slot in Slots(segments))
        {
            if (previous is { } p && slot - p < spacing) continue;
            if (!IsClear(slot, reserved, spacing)) continue;
            count++;
            previous = slot;
        }
        return count;
    }

    /// <summary>Where a question can go: sentence ends in time order, skipping the lead-in the way <see cref="Snap"/> does.</summary>
    private static List<TimeSpan> Slots(IReadOnlyList<Timeline.Span> segments)
    {
        bool skipLeadIn = segments[^1].End > LeadIn;
        return segments.Select(s => s.End).Where(end => !skipLeadIn || end >= LeadIn).Distinct().Order().ToList();
    }

    /// <summary>True when <paramref name="time"/> is at least <paramref name="spacing"/> from every reserved time.</summary>
    private static bool IsClear(TimeSpan time, IReadOnlyList<TimeSpan> reserved, TimeSpan spacing) =>
        reserved.All(r => (time - r).Duration() >= spacing);

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

    /// <summary>
    /// Keeps the <paramref name="count"/> questions (of a list in time order) closest to evenly spaced times between
    /// the first and last, so the kept ones spread across the lecture even when the candidates are bunched up.
    /// Only removes questions, so it never brings two closer together.
    /// </summary>
    internal static IReadOnlyList<Question> SpreadTrim(IReadOnlyList<Question> questions, int count)
    {
        if (questions.Count <= count) return questions;
        if (count <= 0) return [];

        var first = questions[0].Timestamp;
        var span = questions[^1].Timestamp - first;
        var result = new List<Question>(count);
        int next = 0;
        for (int i = 0; i < count; i++)
        {
            var target = first + (count == 1 ? span / 2 : span * i / (count - 1));
            // Leave enough later questions for the remaining targets; ties go to the earlier question.
            int best = next, last = questions.Count - (count - i);
            for (int j = next + 1; j <= last; j++)
            {
                if ((questions[j].Timestamp - target).Duration() < (questions[best].Timestamp - target).Duration()) best = j;
            }
            result.Add(questions[best]);
            next = best + 1;
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
