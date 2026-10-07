using System.Text;
using System.Text.RegularExpressions;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Core.Captions;

/// <summary>
/// Turns raw speech-recognition segments into readable captions: drops non-speech markers,
/// splits long segments, and wraps text to at most two lines (common caption guidelines:
/// ~42 characters per line, two lines per caption).
/// </summary>
public static partial class CaptionCleanup
{
    public const int MaxLineLength = 42;
    public const int MaxLines = 2;
    private const int MaxCaptionLength = MaxLineLength * MaxLines;

    public static List<CaptionSegment> Normalize(IEnumerable<CaptionSegment> segments)
    {
        var result = new List<CaptionSegment>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            var text = NonSpeechMarker().Replace(segment.Text, " ");
            text = Whitespace().Replace(text, " ").Trim();
            if (text.Length == 0) continue;

            foreach (var piece in Split(segment.Start, segment.End, text))
            {
                result.Add(new CaptionSegment(piece.Start, piece.End, WrapLines(piece.Text))
                {
                    UncertainWords = WordsIn(segment.UncertainWords, piece.Text),
                });
            }
        }
        return result;
    }

    /// <summary>The uncertain words that ended up in this piece of a split segment; null when there are none.</summary>
    private static List<string>? WordsIn(List<string>? words, string text)
    {
        var kept = words?.Where(w => CaptionCorrections.Count(text, w) > 0).ToList();
        return kept is { Count: > 0 } ? kept : null;
    }

    /// <summary>Splits text longer than two lines into consecutive captions, dividing the time by character count.</summary>
    private static IEnumerable<CaptionSegment> Split(TimeSpan start, TimeSpan end, string text)
    {
        var chunks = new List<string>();
        var remaining = text;
        while (remaining.Length > MaxCaptionLength)
        {
            int cut = FindBreak(remaining, MaxCaptionLength);
            chunks.Add(remaining[..cut].Trim());
            remaining = remaining[cut..].Trim();
        }
        if (remaining.Length > 0) chunks.Add(remaining);

        double totalChars = chunks.Sum(c => c.Length);
        var duration = end > start ? end - start : TimeSpan.Zero;
        var cursor = start;
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunkEnd = i == chunks.Count - 1 ? end : cursor + duration * (chunks[i].Length / totalChars);
            yield return new CaptionSegment(cursor, chunkEnd, chunks[i]);
            cursor = chunkEnd;
        }
    }

    /// <summary>Wraps into at most <see cref="MaxLines"/> lines, balancing line lengths.</summary>
    public static string WrapLines(string text)
    {
        text = text.Replace('\n', ' ').Trim();
        if (text.Length <= MaxLineLength) return text;

        // Break nearest the middle so the two lines are similar in length.
        int best = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != ' ') continue;
            if (text.Length - i - 1 > MaxLineLength * 2) continue;
            if (best < 0 || Math.Abs(i - text.Length / 2) < Math.Abs(best - text.Length / 2)) best = i;
        }
        return best < 0 ? text : new StringBuilder(text).Replace(" ", "\n", best, 1).ToString();
    }

    /// <summary>Best place to cut at or before <paramref name="limit"/>: sentence end, then clause, then word.</summary>
    private static int FindBreak(string text, int limit)
    {
        int minimum = limit / 3;
        foreach (var marks in new[] { ".?!", ",;:" })
        {
            for (int i = limit - 1; i >= minimum; i--)
                if (marks.Contains(text[i]) && i + 1 < text.Length && text[i + 1] == ' ') return i + 1;
        }
        int space = text.LastIndexOf(' ', limit);
        return space > 0 ? space : limit;
    }

    // Whisper emits markers like [BLANK_AUDIO], [MUSIC], (inaudible) for non-speech.
    [GeneratedRegex(@"\[(?:BLANK_AUDIO|MUSIC|NOISE|SILENCE|INAUDIBLE)\]|\((?:inaudible|silence)\)", RegexOptions.IgnoreCase)]
    private static partial Regex NonSpeechMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
