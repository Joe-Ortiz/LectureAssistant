using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Core.Captions;

/// <summary>Reads and writes WebVTT (.vtt) and SubRip (.srt), the two formats YouTube accepts for caption upload.</summary>
public static partial class CaptionFormats
{
    public static string ToWebVtt(IEnumerable<CaptionSegment> segments)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var s in Ordered(segments))
        {
            sb.Append(Timestamp(s.Start, '.')).Append(" --> ").Append(Timestamp(s.End, '.')).Append('\n');
            sb.Append(s.Text.Trim()).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToSrt(IEnumerable<CaptionSegment> segments)
    {
        var sb = new StringBuilder();
        int index = 1;
        foreach (var s in Ordered(segments))
        {
            sb.Append(index++).Append('\n');
            sb.Append(Timestamp(s.Start, ',')).Append(" --> ").Append(Timestamp(s.End, ',')).Append('\n');
            sb.Append(s.Text.Trim()).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>Parses either WebVTT or SRT; cue settings, identifiers, NOTE and STYLE blocks are ignored.</summary>
    public static List<CaptionSegment> Parse(string text)
    {
        var result = new List<CaptionSegment>();
        var blocks = BlockSeparator().Split(text.Replace("\r\n", "\n").Trim());
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timingLine = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timingLine < 0) continue;

            var match = TimingLine().Match(lines[timingLine]);
            if (!match.Success) continue;

            var body = string.Join("\n", lines.Skip(timingLine + 1)).Trim();
            if (body.Length == 0) continue;

            result.Add(new CaptionSegment(ParseTimestamp(match.Groups[1].Value), ParseTimestamp(match.Groups[2].Value), body));
        }
        return result;
    }

    /// <summary>Plain transcript with [mm:ss] markers, the form sent to language models.</summary>
    public static string ToTimestampedTranscript(IEnumerable<CaptionSegment> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in Ordered(segments))
            sb.Append('[').Append(Clock(s.Start)).Append("] ").Append(s.Text.Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>"m:ss" or "h:mm:ss", for display.</summary>
    public static string Clock(TimeSpan t) =>
        t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static IEnumerable<CaptionSegment> Ordered(IEnumerable<CaptionSegment> segments) =>
        segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)).OrderBy(s => s.Start);

    private static string Timestamp(TimeSpan t, char fractionSeparator)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{fractionSeparator}{t.Milliseconds:000}");
    }

    private static TimeSpan ParseTimestamp(string value)
    {
        var parts = value.Replace(',', '.').Split(':');
        double seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        int minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        int hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlockSeparator();

    [GeneratedRegex(@"((?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})\s*-->\s*((?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})")]
    private static partial Regex TimingLine();
}
