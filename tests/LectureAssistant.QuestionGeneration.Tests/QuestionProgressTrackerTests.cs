using System.Text.Json;
using LectureAssistant.Core;
using LectureAssistant.QuestionGeneration;
using static LectureAssistant.QuestionGeneration.Tests.TestData;

namespace LectureAssistant.QuestionGeneration.Tests;

public class QuestionProgressTrackerTests
{
    private const double Guess = 100;

    [Fact]
    public void Multi_part_run_is_monotonic_counts_across_parts_and_ends_full()
    {
        var (time, reports, tracker) = Create();
        tracker.BeginLoading("Loading the local model…", 0.1);
        tracker.ReportLoad(0.5);
        time.Advance(TimeSpan.FromSeconds(1));
        tracker.ReportLoad(1);
        tracker.Plan([new PlannedPart(3, ReadWeight: 1), new PlannedPart(2, ReadWeight: 0.5)]);

        tracker.BeginPart(0, TimeSpan.FromSeconds(4));
        Assert.Equal("Reading the transcript (part 1 of 2)…", reports[^1].Message);
        time.Advance(TimeSpan.FromSeconds(2));
        tracker.Tick();
        Stream(tracker, time, QuestionsJson(3));
        tracker.EndPart();
        Assert.Equal(0.1 + 0.9 * 4 / 6.5, reports[^1].Fraction!.Value, 9);

        tracker.BeginPart(1, TimeSpan.FromSeconds(2));
        Assert.Equal("Reading the transcript (part 2 of 2)…", reports[^1].Message);
        Stream(tracker, time, QuestionsJson(2));
        tracker.EndPart();
        tracker.Complete("Checking the questions…");

        var messages = reports.Select(r => r.Message).Distinct().ToList();
        Assert.Contains("Writing question 1 of 5…", messages);
        Assert.Contains("Writing question 3 of 5…", messages);
        Assert.Contains("Writing question 4 of 5…", messages);
        Assert.Contains("Writing question 5 of 5…", messages);
        Assert.DoesNotContain("Writing question 6 of 5…", messages);
        AssertMonotonic(reports);
        Assert.Equal(1, reports[^1].Fraction);
        Assert.Null(reports[^1].TimeLeft);
    }

    [Fact]
    public void Loading_fills_only_its_share_and_reading_creeps_without_finishing()
    {
        var (time, reports, tracker) = Create();
        tracker.BeginLoading("Loading…", 0.2);
        time.Advance(TimeSpan.FromSeconds(1));
        tracker.ReportLoad(0.5);
        Assert.Equal(0.1, reports[^1].Fraction!.Value, 9);

        tracker.Plan([new PlannedPart(1, ReadWeight: 1)]); // reading = half the remaining bar
        tracker.BeginPart(0, TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(5));
        tracker.Tick();
        var midRead = reports[^1].Fraction!.Value;
        Assert.InRange(midRead, 0.2 + 0.01, 0.2 + 0.8 * 0.5 * QuestionProgressTracker.MaxPartial);

        time.Advance(TimeSpan.FromHours(1));
        tracker.Tick();
        Assert.Equal(0.2 + 0.8 * 0.5 * QuestionProgressTracker.MaxPartial, reports[^1].Fraction!.Value, 9);
        AssertMonotonic(reports);
    }

    [Fact]
    public void Partial_question_never_reaches_the_next_question()
    {
        var (time, reports, tracker) = Create();
        tracker.Plan([new PlannedPart(4)]);
        tracker.BeginPart(0, TimeSpan.Zero);
        Assert.Equal("Writing question 1 of 4…", reports[^1].Message);

        // Far more output than the guess, without the question closing.
        tracker.OnOutput("{\"questions\":[{\"prompt\":\"", 1);
        for (int i = 0; i < 1000; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            tracker.OnOutput("blah ", 1);
        }
        Assert.Equal(QuestionProgressTracker.MaxPartial / 4, reports[^1].Fraction!.Value, 9);
        Assert.Equal("Writing question 1 of 4…", reports[^1].Message);
    }

    [Fact]
    public void Part_that_writes_fewer_questions_jumps_forward_and_numbering_continues()
    {
        var (time, reports, tracker) = Create();
        tracker.Plan([new PlannedPart(3), new PlannedPart(2)]);
        tracker.BeginPart(0, TimeSpan.Zero);
        var oneQuestion = QuestionsJson(1);
        Stream(tracker, time, oneQuestion[..^2]); // cut off: one question, array never closed
        tracker.EndPart();
        Assert.Equal(3.0 / 5, reports[^1].Fraction!.Value, 9);

        tracker.BeginPart(1, TimeSpan.Zero);
        Assert.Equal("Writing question 4 of 5…", reports[^1].Message);
        AssertMonotonic(reports);
    }

    [Fact]
    public void Time_left_appears_after_the_first_question_from_the_measured_pace()
    {
        var (time, reports, tracker) = Create();
        tracker.Plan([new PlannedPart(5)]);
        tracker.BeginPart(0, TimeSpan.Zero);
        var json = QuestionsJson(5);
        var counter = new StreamingQuestionCounter();
        var firstEnd = 1;
        while (counter.Append(json.AsSpan(firstEnd - 1, 1)) == 0) firstEnd++;

        // First question takes a minute; nothing is promised until it's done.
        var step = TimeSpan.FromSeconds(60.0 / firstEnd);
        for (int i = 0; i < firstEnd - 1; i++)
        {
            time.Advance(step);
            tracker.OnOutput(json.AsSpan(i, 1), 1);
        }
        Assert.All(reports, r => Assert.Null(r.TimeLeft));

        time.Advance(step);
        tracker.OnOutput(json.AsSpan(firstEnd - 1, 1), 1);
        Assert.Equal("Writing question 2 of 5…", reports[^1].Message);
        Assert.Equal("About 4 minutes left", reports[^1].TimeLeft);
    }

    [Fact]
    public void Fraction_only_updates_are_throttled_but_milestones_are_not()
    {
        var (time, reports, tracker) = Create();
        tracker.Plan([new PlannedPart(2)]);
        tracker.BeginPart(0, TimeSpan.Zero);
        var json = QuestionsJson(2);
        int before = reports.Count;
        foreach (var c in json)
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            tracker.OnOutput([c], 1);
        }
        int seconds = (int)Math.Ceiling(json.Length * 0.010);
        // At most ~5 a second, plus the two question milestones.
        Assert.InRange(reports.Count - before, 2, seconds * 5 + 3);
        Assert.Contains(reports, r => r.Message == "Writing question 2 of 2…");
    }

    [Fact]
    public void Indeterminate_steps_report_no_fraction_until_a_plan_exists()
    {
        var (_, reports, tracker) = Create();
        tracker.ReportIndeterminate("Claude is planning…");
        Assert.Null(reports[^1].Fraction);
        tracker.Plan([new PlannedPart(1)]);
        tracker.BeginPart(0, TimeSpan.Zero);
        Assert.Equal(new QuestionGenerationProgress("Writing the question…", 0), reports[^1]);
    }

    [Theory]
    [InlineData(10, "Less than a minute left")]
    [InlineData(49, "Less than a minute left")]
    [InlineData(70, "About a minute left")]
    [InlineData(100, "About 2 minutes left")]
    [InlineData(150, "About 3 minutes left")]
    [InlineData(29 * 60, "About 29 minutes left")]
    [InlineData(3 * 3600, "About 3 hours left")]
    public void Time_left_is_rounded_and_plain(double seconds, string expected) =>
        Assert.Equal(expected, QuestionProgressTracker.FormatTimeLeft(seconds));

    internal static string QuestionsJson(int count) => JsonSerializer.Serialize(new QuestionDraftSet
    {
        Questions = Enumerable.Range(0, count).Select(i => Mc(30 * (i + 1), $"Question {i} with {{braces}}?")).ToList(),
    });

    private static void Stream(QuestionProgressTracker tracker, FakeTime time, string json)
    {
        foreach (var c in json)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            tracker.OnOutput([c], 1);
        }
    }

    private static void AssertMonotonic(List<QuestionGenerationProgress> reports)
    {
        var fractions = reports.Where(r => r.Fraction is not null).Select(r => r.Fraction!.Value).ToList();
        for (int i = 1; i < fractions.Count; i++)
            Assert.True(fractions[i] >= fractions[i - 1], $"Went backwards at report {i}: {fractions[i - 1]} → {fractions[i]}");
        Assert.All(fractions, f => Assert.InRange(f, 0, 1));
    }

    private static (FakeTime, List<QuestionGenerationProgress>, QuestionProgressTracker) Create()
    {
        var time = new FakeTime();
        var reports = new List<QuestionGenerationProgress>();
        return (time, reports, new QuestionProgressTracker(new ListProgress(reports), Guess, time));
    }

    private sealed class ListProgress(List<QuestionGenerationProgress> reports) : IProgress<QuestionGenerationProgress>
    {
        public void Report(QuestionGenerationProgress value) => reports.Add(value);
    }
}

/// <summary>A clock that only moves when told to, with timers that never fire (tests call Tick themselves).</summary>
internal sealed class FakeTime : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public void Advance(TimeSpan by) => _ticks += by.Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NeverTimer();

    private sealed class NeverTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => default;
    }
}
