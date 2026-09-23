using LectureAssistant.Core.Captions;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Core.Tests;

public class CaptionTests
{
    private static readonly List<CaptionSegment> Sample =
    [
        new(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4), "Welcome to the lecture."),
        new(TimeSpan.FromMinutes(61) + TimeSpan.FromMilliseconds(250), TimeSpan.FromMinutes(61) + TimeSpan.FromSeconds(3), "Over an hour in."),
    ];

    [Fact]
    public void WebVtt_uses_dot_milliseconds_and_header()
    {
        var vtt = CaptionFormats.ToWebVtt(Sample);
        Assert.StartsWith("WEBVTT\n\n", vtt);
        Assert.Contains("00:00:01.500 --> 00:00:04.000\nWelcome to the lecture.", vtt);
        Assert.Contains("01:01:00.250 --> 01:01:03.000", vtt);
    }

    [Fact]
    public void Srt_numbers_cues_and_uses_comma_milliseconds()
    {
        var srt = CaptionFormats.ToSrt(Sample);
        Assert.StartsWith("1\n00:00:01,500 --> 00:00:04,000\nWelcome to the lecture.\n\n2\n", srt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parse_round_trips(bool vtt)
    {
        var text = vtt ? CaptionFormats.ToWebVtt(Sample) : CaptionFormats.ToSrt(Sample);
        var parsed = CaptionFormats.Parse(text);
        Assert.Equal(Sample.Count, parsed.Count);
        for (int i = 0; i < Sample.Count; i++)
        {
            Assert.Equal(Sample[i].Start, parsed[i].Start);
            Assert.Equal(Sample[i].End, parsed[i].End);
            Assert.Equal(Sample[i].Text, parsed[i].Text);
        }
    }

    [Fact]
    public void Parse_handles_vtt_without_hours_cue_ids_and_settings()
    {
        var parsed = CaptionFormats.Parse("WEBVTT\r\n\r\nNOTE ignore me\r\n\r\nintro\r\n00:05.000 --> 00:07.250 align:start\r\nHello\r\nthere\r\n");
        var cue = Assert.Single(parsed);
        Assert.Equal(TimeSpan.FromSeconds(5), cue.Start);
        Assert.Equal(TimeSpan.FromSeconds(7.25), cue.End);
        Assert.Equal("Hello\nthere", cue.Text);
    }

    [Fact]
    public void Timestamped_transcript_uses_clock_markers()
    {
        var text = CaptionFormats.ToTimestampedTranscript(Sample);
        Assert.Equal("[0:01] Welcome to the lecture.\n[1:01:00] Over an hour in.\n", text);
    }

    [Fact]
    public void Cleanup_drops_non_speech_and_blank_segments()
    {
        var result = CaptionCleanup.Normalize(
        [
            new(TimeSpan.Zero, TimeSpan.FromSeconds(2), " [BLANK_AUDIO] "),
            new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), "  Hello   world "),
        ]);
        var cue = Assert.Single(result);
        Assert.Equal("Hello world", cue.Text);
    }

    [Fact]
    public void Cleanup_splits_long_segments_and_wraps_to_two_lines()
    {
        var text = "The first law of thermodynamics says energy is conserved. " +
                   "The second law says entropy of an isolated system never decreases, " +
                   "which is why heat flows from hot to cold objects.";
        var result = CaptionCleanup.Normalize([new(TimeSpan.Zero, TimeSpan.FromSeconds(12), text)]);

        Assert.True(result.Count >= 2);
        Assert.Equal(TimeSpan.Zero, result[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(12), result[^1].End);
        for (int i = 1; i < result.Count; i++) Assert.Equal(result[i - 1].End, result[i].Start);

        foreach (var cue in result)
        {
            var lines = cue.Text.Split('\n');
            Assert.True(lines.Length <= CaptionCleanup.MaxLines, cue.Text);
            Assert.All(lines, l => Assert.True(l.Length <= CaptionCleanup.MaxLineLength, $"'{l}' too long"));
        }
        Assert.Equal(text, string.Join(" ", result.Select(c => c.Text.Replace('\n', ' '))));
    }
}
