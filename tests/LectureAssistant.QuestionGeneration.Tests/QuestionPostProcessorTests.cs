using LectureAssistant.Core.Models;
using LectureAssistant.QuestionGeneration;
using static LectureAssistant.QuestionGeneration.Tests.TestData;

namespace LectureAssistant.QuestionGeneration.Tests;

public class QuestionPostProcessorTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Converts_all_three_types_to_valid_core_questions()
    {
        var result = QuestionPostProcessor.Process([Mc(100), Tf(200, answer: false), Fib(300)], Request());

        Assert.Equal(3, result.Count);
        Assert.All(result, q => Assert.Empty(q.Validate()));

        var mc = result[0];
        Assert.Equal(QuestionType.MultipleChoice, mc.Type);
        Assert.Equal(["Right", "Wrong", "Also wrong"], mc.Options.Select(o => o.Text));
        Assert.True(mc.Options[0].IsCorrect);
        Assert.Equal("Yes.", mc.Options[0].Feedback);
        Assert.Null(mc.Options[2].Feedback); // empty feedback becomes null
        Assert.Equal("Because the lecturer said so.", mc.Explanation);
        Assert.Equal("Right is right.", mc.SourceExcerpt);
        Assert.Equal(1, mc.Points);

        Assert.Equal(QuestionType.TrueFalse, result[1].Type);
        Assert.False(result[1].CorrectAnswer);
        Assert.Null(result[1].SourceExcerpt);

        Assert.Equal(QuestionType.FillInTheBlank, result[2].Type);
        Assert.Equal(["mitochondrion", "mitochondria"], result[2].AcceptedAnswers);
    }

    [Fact]
    public void Allows_multiple_correct_options()
    {
        var draft = Mc(100);
        draft.Options![1].IsCorrect = true;
        var q = Assert.Single(QuestionPostProcessor.Process([draft], Request()));
        Assert.Equal(2, q.Options.Count(o => o.IsCorrect));
    }

    [Theory]
    [InlineData(100, 110)]    // start of segment [100,110) -> its end
    [InlineData(104.5, 110)]  // inside a segment
    [InlineData(109.99, 110)]
    [InlineData(110, 120)]    // exactly on a boundary belongs to the next segment
    [InlineData(5000, 600)]   // past the end -> clamped to the last segment's end
    public void Snaps_timestamp_to_end_of_containing_segment(double seconds, double expected)
    {
        var q = Assert.Single(QuestionPostProcessor.Process([Tf(seconds)], Request()));
        Assert.Equal(S(expected), q.Timestamp);
    }

    [Fact]
    public void Timestamp_in_a_gap_snaps_to_the_preceding_segment()
    {
        var transcript = new List<CaptionSegment>
        {
            new(S(0), S(40), "Intro."),
            new(S(50), S(60), "Idea explained."),
            new(S(90), S(100), "After a pause."),
        };
        var q = Assert.Single(QuestionPostProcessor.Process([Tf(75)], Request(transcript)));
        Assert.Equal(S(60), q.Timestamp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-20)]
    [InlineData(12)]
    public void Nothing_is_placed_in_the_first_thirty_seconds(double seconds)
    {
        var q = Assert.Single(QuestionPostProcessor.Process([Tf(seconds)], Request()));
        Assert.Equal(S(30), q.Timestamp);
    }

    [Fact]
    public void Lead_in_moves_to_first_segment_ending_after_thirty_seconds()
    {
        var transcript = new List<CaptionSegment> { new(S(0), S(25), "a"), new(S(25), S(47), "b"), new(S(47), S(80), "c") };
        var q = Assert.Single(QuestionPostProcessor.Process([Tf(3)], Request(transcript)));
        Assert.Equal(S(47), q.Timestamp);
    }

    [Fact]
    public void Very_short_video_keeps_questions_inside_it()
    {
        var transcript = new List<CaptionSegment> { new(S(0), S(8), "a"), new(S(8), S(20), "b") };
        var q = Assert.Single(QuestionPostProcessor.Process([Tf(2)], Request(transcript)));
        Assert.Equal(S(8), q.Timestamp);
    }

    [Fact]
    public void Transcript_starting_late_clamps_to_first_segment()
    {
        var transcript = new List<CaptionSegment> { new(S(120), S(130), "a"), new(S(130), S(140), "b") };
        var q = Assert.Single(QuestionPostProcessor.Process([Tf(10)], Request(transcript)));
        Assert.Equal(S(130), q.Timestamp);
    }

    [Fact]
    public void Unordered_and_blank_segments_are_handled()
    {
        var transcript = new List<CaptionSegment>
        {
            new(S(60), S(70), "later"),
            new(S(40), S(50), "earlier"),
            new(S(50), S(60), "   "),
        };
        // 55 falls in the blank segment, which is ignored -> preceding real segment [40,50).
        var q = Assert.Single(QuestionPostProcessor.Process([Tf(55)], Request(transcript)));
        Assert.Equal(S(50), q.Timestamp);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_finite_timestamps_are_dropped(double seconds)
    {
        Assert.Empty(QuestionPostProcessor.Process([Tf(seconds)], Request()));
    }

    [Fact]
    public void Drops_types_that_are_not_allowed()
    {
        var result = QuestionPostProcessor.Process([Mc(100), Tf(200), Fib(300)], Request(types: QuestionType.TrueFalse));
        Assert.Equal(QuestionType.TrueFalse, Assert.Single(result).Type);
    }

    [Theory]
    [InlineData("essay")]
    [InlineData("")]
    [InlineData(null)]
    public void Drops_unknown_types(string? type)
    {
        var draft = Tf(100);
        draft.Type = type;
        Assert.Empty(QuestionPostProcessor.Process([draft], Request()));
    }

    [Theory]
    [InlineData("MultipleChoice", QuestionType.MultipleChoice)]
    [InlineData("multiple-choice", QuestionType.MultipleChoice)]
    [InlineData("True/False", QuestionType.TrueFalse)]
    [InlineData("TRUE_FALSE", QuestionType.TrueFalse)]
    [InlineData("fill in the blank", QuestionType.FillInTheBlank)]
    [InlineData("FillInTheBlank", QuestionType.FillInTheBlank)]
    public void Accepts_type_name_variants(string type, QuestionType expected)
    {
        Assert.Equal(expected, QuestionDraftSchema.ParseType(type));
    }

    [Fact]
    public void Drops_invalid_multiple_choice()
    {
        var oneOption = Mc(100);
        oneOption.Options = [new() { Text = "Only", IsCorrect = true }];

        var noCorrect = Mc(200);
        noCorrect.Options!.ForEach(o => o.IsCorrect = false);

        var allCorrect = Mc(300);
        allCorrect.Options!.ForEach(o => o.IsCorrect = true);

        var nullOptions = Mc(400);
        nullOptions.Options = null;

        var emptyPrompt = Mc(500, prompt: "   ");

        Assert.Empty(QuestionPostProcessor.Process([oneOption, noCorrect, allCorrect, nullOptions, emptyPrompt], Request()));
    }

    [Fact]
    public void Removes_blank_and_duplicate_options()
    {
        var draft = Mc(100);
        draft.Options!.Add(new() { Text = "  ", IsCorrect = false });
        draft.Options!.Add(new() { Text = "right", IsCorrect = false });
        draft.Options!.Add(null!);
        var q = Assert.Single(QuestionPostProcessor.Process([draft], Request()));
        Assert.Equal(["Right", "Wrong", "Also wrong"], q.Options.Select(o => o.Text));
    }

    [Fact]
    public void Drops_true_false_without_an_answer()
    {
        Assert.Empty(QuestionPostProcessor.Process([Tf(100, answer: null)], Request()));
    }

    [Theory]
    [InlineData("No blank here.")]
    [InlineData("Two ___ blanks ___ here.")]
    public void Drops_fill_in_the_blank_without_exactly_one_blank(string prompt)
    {
        Assert.Empty(QuestionPostProcessor.Process([Fib(100, prompt)], Request()));
    }

    [Fact]
    public void Drops_fill_in_the_blank_without_accepted_answers()
    {
        var draft = Fib(100);
        draft.AcceptedAnswers = ["  ", ""];
        var nullAnswers = Fib(200);
        nullAnswers.AcceptedAnswers = null;
        Assert.Empty(QuestionPostProcessor.Process([draft, nullAnswers], Request()));
    }

    [Theory]
    [InlineData("The answer is ______.")]
    [InlineData("The answer is _ _ _.")]
    [InlineData("The answer is ___.")]
    public void Normalizes_blank_runs_to_the_marker(string prompt)
    {
        var q = Assert.Single(QuestionPostProcessor.Process([Fib(100, prompt)], Request()));
        Assert.Equal("The answer is ___.", q.Prompt);
    }

    [Fact]
    public void Dedupes_accepted_answers_case_insensitively_and_trims()
    {
        var q = Assert.Single(QuestionPostProcessor.Process([Fib(100, "X is ___.", " ATP ", "atp", "adenosine triphosphate")], Request()));
        Assert.Equal(["ATP", "adenosine triphosphate"], q.AcceptedAnswers);
    }

    [Fact]
    public void Collapses_whitespace_and_truncates_long_excerpts()
    {
        var draft = Tf(100, prompt: "  The   sky\n is  blue. ");
        draft.SourceExcerpt = new string('a', 1000);
        var q = Assert.Single(QuestionPostProcessor.Process([draft], Request()));
        Assert.Equal("The sky is blue.", q.Prompt);
        Assert.True(q.SourceExcerpt!.Length <= 400);
    }

    [Fact]
    public void Enforces_minimum_spacing_by_moving_crowded_questions_later()
    {
        var result = QuestionPostProcessor.Process(
            [Tf(100, prompt: "A"), Tf(125, prompt: "B"), Tf(150, prompt: "C"), Tf(300, prompt: "D")],
            Request(spacing: TimeSpan.FromSeconds(45)));
        // A@110; B snaps to 130, too close, moves to the first sentence end 45s after A (160);
        // C snaps to 160, moves to 210 (50s later, within the allowed shift); D@310 is clear.
        Assert.Equal(["A", "B", "C", "D"], result.Select(q => q.Prompt));
        Assert.Equal([S(110), S(160), S(210), S(310)], result.Select(q => q.Timestamp));
    }

    [Fact]
    public void Drops_a_crowded_question_that_would_move_too_far_from_its_material()
    {
        var result = QuestionPostProcessor.Process(
            [Tf(100, prompt: "A"), Tf(105, prompt: "B"), Tf(115, prompt: "C")],
            Request(spacing: TimeSpan.FromSeconds(45)));
        // A@110, B moves 110 -> 160 (50s), C would move 120 -> 210 (90s > MaxShift): dropped.
        Assert.Equal(["A", "B"], result.Select(q => q.Prompt));
    }

    [Fact]
    public void Spacing_relaxes_for_short_videos_so_the_requested_count_fits()
    {
        // 3 minutes, 5 questions requested, 45s preferred: 150 usable seconds / 6 = 25s spacing.
        var drafts = Enumerable.Range(0, 5).Select(i => Tf(40 + i * 28, prompt: $"Q{i}")).ToList();
        var result = QuestionPostProcessor.Process(drafts, Request(transcript: Transcript(180), count: 5, spacing: TimeSpan.FromSeconds(45)));
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void Spacing_is_measured_after_snapping_and_sorting()
    {
        // Out of order in the model output; both snap into the same segment.
        var result = QuestionPostProcessor.Process(
            [Tf(305, prompt: "Later in list"), Tf(301, prompt: "Earlier in list"), Tf(100, prompt: "First")],
            Request(spacing: TimeSpan.FromSeconds(30)));
        // Stable order keeps "Later in list" at 310; the other is pushed 30s to the next free sentence end.
        Assert.Equal(["First", "Later in list", "Earlier in list"], result.Select(q => q.Prompt));
        Assert.Equal([S(110), S(310), S(340)], result.Select(q => q.Timestamp));
    }

    [Fact]
    public void Output_is_in_timeline_order()
    {
        var result = QuestionPostProcessor.Process([Tf(400, prompt: "c"), Tf(100, prompt: "a"), Tf(250, prompt: "b")], Request());
        Assert.Equal(["a", "b", "c"], result.Select(q => q.Prompt));
    }

    [Fact]
    public void Drops_duplicate_prompts()
    {
        var result = QuestionPostProcessor.Process([Tf(100, prompt: "Same thing."), Tf(400, prompt: "same  THING.")], Request());
        Assert.Single(result);
    }

    [Fact]
    public void Trims_to_question_count_keeping_an_even_spread()
    {
        var drafts = Enumerable.Range(0, 10).Select(i => Tf(50 + i * 50, prompt: $"Q{i}")).ToList();
        var result = QuestionPostProcessor.Process(drafts, Request(count: 4));
        Assert.Equal(["Q0", "Q3", "Q6", "Q9"], result.Select(q => q.Prompt));
    }

    [Fact]
    public void Trim_to_one_takes_the_middle()
    {
        var drafts = Enumerable.Range(0, 5).Select(i => Tf(50 + i * 50, prompt: $"Q{i}")).ToList();
        Assert.Equal("Q2", Assert.Single(QuestionPostProcessor.Process(drafts, Request(count: 1))).Prompt);
    }

    [Fact]
    public void Returns_fewer_when_model_returns_fewer()
    {
        Assert.Equal(2, QuestionPostProcessor.Process([Tf(100, prompt: "a"), Tf(200, prompt: "b")], Request(count: 8)).Count);
    }

    [Fact]
    public void Empty_transcript_or_zero_count_returns_nothing()
    {
        Assert.Empty(QuestionPostProcessor.Process([Tf(100)], Request(transcript: [])));
        Assert.Empty(QuestionPostProcessor.Process([Tf(100)], Request(count: 0)));
    }

    [Fact]
    public void Null_drafts_are_ignored()
    {
        Assert.Single(QuestionPostProcessor.Process([null!, Tf(100)], Request()));
    }

    [Fact]
    public void Every_question_gets_a_unique_id()
    {
        var result = QuestionPostProcessor.Process([Tf(100, prompt: "a"), Tf(200, prompt: "b")], Request());
        Assert.Equal(2, result.Select(q => q.Id).Distinct().Count());
    }
}
