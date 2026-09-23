using LectureAssistant.QuestionGeneration.Local;
using static LectureAssistant.QuestionGeneration.Tests.TestData;

namespace LectureAssistant.QuestionGeneration.Tests;

public class TranscriptChunkerTests
{
    // Deliberately crude "tokenizer": one token per 4 characters, rounded up.
    private static int Count(string s) => (s.Length + 3) / 4;

    [Fact]
    public void Everything_fits_in_one_section_when_budget_allows()
    {
        var transcript = Transcript(600);
        var chunks = TranscriptChunker.Chunk(transcript, 100_000, Count);
        Assert.Equal(transcript.Count, Assert.Single(chunks).Count);
    }

    [Theory]
    [InlineData(4500, 200)]
    [InlineData(4500, 1000)]
    [InlineData(600, 37)]
    public void Sections_respect_budget_keep_order_and_cover_everything(int seconds, int budget)
    {
        var transcript = Transcript(seconds);
        var chunks = TranscriptChunker.Chunk(transcript, budget, Count);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Sum(s => Count(QuestionPrompt.FormatLine(s) + "\n")) <= budget));
        Assert.Equal(transcript, chunks.SelectMany(c => c));
    }

    [Fact]
    public void Sections_are_balanced_rather_than_leaving_a_tiny_tail()
    {
        var transcript = Transcript(4500); // 450 identical-ish lines
        var chunks = TranscriptChunker.Chunk(transcript, 1000, Count);
        var sizes = chunks.Select(c => c.Count).ToList();
        Assert.True(sizes.Min() >= sizes.Max() / 2, string.Join(",", sizes));
    }

    [Fact]
    public void Oversized_segment_gets_its_own_section()
    {
        var transcript = Transcript(30);
        transcript[1].Text = new string('x', 2000);
        var chunks = TranscriptChunker.Chunk(transcript, 100, Count);
        Assert.Equal(3, chunks.Count);
        Assert.Same(transcript[1], Assert.Single(chunks[1]));
    }

    [Fact]
    public void Allocation_is_proportional_to_duration_and_sums_to_total()
    {
        var counts = TranscriptChunker.Allocate([600, 600, 300], [10, 10, 10], 10);
        Assert.Equal(10, counts.Sum());
        Assert.Equal([4, 4, 2], counts);
    }

    [Fact]
    public void Allocation_respects_capacity_and_redistributes()
    {
        var counts = TranscriptChunker.Allocate([1000, 100, 100], [2, 5, 5], 6);
        Assert.Equal([2, 2, 2], counts);
    }

    [Fact]
    public void Allocation_stops_when_everything_is_full()
    {
        Assert.Equal([1, 2], TranscriptChunker.Allocate([100, 100], [1, 2], 10));
    }

    [Fact]
    public void Fewer_questions_than_sections_go_to_the_longest()
    {
        Assert.Equal([0, 1, 0], TranscriptChunker.Allocate([100, 900, 200], [5, 5, 5], 1));
    }

    [Fact]
    public void Empty_inputs()
    {
        Assert.Empty(TranscriptChunker.Chunk([], 100, Count));
        Assert.Empty(TranscriptChunker.Allocate([], [], 5));
        Assert.Equal([0, 0], TranscriptChunker.Allocate([1, 1], [5, 5], 0));
    }
}
