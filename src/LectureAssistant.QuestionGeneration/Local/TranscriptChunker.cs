using LectureAssistant.Core.Models;

namespace LectureAssistant.QuestionGeneration.Local;

/// <summary>Splits a transcript into sections that fit a small context window and shares out questions between them.</summary>
internal static class TranscriptChunker
{
    /// <summary>
    /// Greedy split on segment boundaries so each section's formatted lines total at most
    /// <paramref name="maxTokens"/> (a single over-long segment still gets its own section).
    /// When more than one section is needed they are balanced, so the last one isn't a short leftover.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<CaptionSegment>> Chunk(
        IEnumerable<CaptionSegment> transcript, int maxTokens, Func<string, int> countTokens)
    {
        var segments = QuestionPrompt.Ordered(transcript).ToList();
        if (segments.Count == 0) return [];
        var costs = segments.Select(s => countTokens(QuestionPrompt.FormatLine(s) + "\n")).ToArray();
        var total = costs.Sum();

        int sections = Math.Max(1, (int)Math.Ceiling(total / (double)Math.Max(1, maxTokens)));
        // Aim for equal-sized sections; fall back to more sections if boundaries make that impossible.
        while (true)
        {
            var target = Math.Min(maxTokens, (int)Math.Ceiling(total / (double)sections));
            var result = Split(segments, costs, target, maxTokens);
            if (result.Count <= sections || target >= maxTokens) return result;
            sections++;
        }
    }

    private static List<IReadOnlyList<CaptionSegment>> Split(List<CaptionSegment> segments, int[] costs, int target, int max)
    {
        var result = new List<IReadOnlyList<CaptionSegment>>();
        var current = new List<CaptionSegment>();
        int used = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            // Close the section at the target size, or earlier if this segment would overflow the hard maximum.
            if (current.Count > 0 && (used >= target || used + costs[i] > max))
            {
                result.Add(current);
                current = [];
                used = 0;
            }
            current.Add(segments[i]);
            used += costs[i];
        }
        if (current.Count > 0) result.Add(current);
        return result;
    }

    /// <summary>
    /// Shares <paramref name="total"/> questions in proportion to <paramref name="weights"/> (section durations),
    /// never exceeding a section's capacity. Each question goes to the section furthest below its fair share.
    /// </summary>
    public static int[] Allocate(IReadOnlyList<double> weights, IReadOnlyList<int> capacities, int total)
    {
        var counts = new int[weights.Count];
        var weightSum = weights.Sum(w => Math.Max(0, w));
        if (weights.Count == 0 || total <= 0) return counts;

        for (int n = 0; n < total; n++)
        {
            int best = -1;
            double bestDeficit = double.NegativeInfinity;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] >= capacities[i]) continue;
                var share = weightSum > 0 ? Math.Max(0, weights[i]) / weightSum * total : total / (double)counts.Length;
                var deficit = share - counts[i];
                if (deficit > bestDeficit) { bestDeficit = deficit; best = i; }
            }
            if (best < 0) break; // everything is full
            counts[best]++;
        }
        return counts;
    }

    public static TimeSpan Duration(IReadOnlyList<CaptionSegment> section) =>
        section.Count == 0 ? TimeSpan.Zero : section.Max(s => s.End) - section[0].Start;
}
