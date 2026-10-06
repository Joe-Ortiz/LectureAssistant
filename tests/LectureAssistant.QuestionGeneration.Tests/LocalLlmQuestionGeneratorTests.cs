using LectureAssistant.Core.Models;
using LectureAssistant.QuestionGeneration;
using LectureAssistant.QuestionGeneration.Local;
using static LectureAssistant.QuestionGeneration.Tests.TestData;

namespace LectureAssistant.QuestionGeneration.Tests;

public class LocalLlmQuestionGeneratorTests
{
    [Fact]
    public async Task Missing_model_file_gives_friendly_error_without_loading_natives()
    {
        var generator = new LocalLlmQuestionGenerator(new LocalLlmOptions { ModelPath = @"C:\does\not\exist.gguf" });
        var ex = await Assert.ThrowsAsync<QuestionGenerationException>(() => generator.GenerateAsync(Request(), null, CancellationToken.None));
        Assert.Equal(QuestionGenerationFailure.ModelLoadFailed, ex.Failure);
    }

    [Fact]
    public async Task Empty_transcript_is_rejected_before_anything_else()
    {
        var generator = new LocalLlmQuestionGenerator(new LocalLlmOptions { ModelPath = @"C:\does\not\exist.gguf" });
        var ex = await Assert.ThrowsAsync<QuestionGenerationException>(() => generator.GenerateAsync(Request(transcript: []), null, CancellationToken.None));
        Assert.Equal(QuestionGenerationFailure.BadRequest, ex.Failure);
    }

    private static List<IReadOnlyList<CaptionSegment>> ThreeSections() =>
        Transcript(600).Chunk(20).Select(c => (IReadOnlyList<CaptionSegment>)c).ToList(); // 0-200, 200-400, 400-600

    [Fact]
    public void Sections_are_never_asked_for_more_than_fit_at_the_minimum_spacing()
    {
        // With 1 minute apart, sections fit 3 (30, 90, 150), 4 and 4, but the whole lecture only 10.
        var plan = LocalLlmQuestionGenerator.PlanSections(ThreeSections(), Request(count: 20, spacing: TimeSpan.FromSeconds(60)), 10);
        Assert.Equal(10, plan.Sum());
        Assert.True(plan[0] <= 3 && plan[1] <= 4 && plan[2] <= 4);
    }

    [Fact]
    public void Sections_full_of_kept_questions_get_none()
    {
        var request = Request(count: 20, spacing: TimeSpan.FromSeconds(60)) with
        {
            ReservedTimes = [TimeSpan.FromSeconds(260), TimeSpan.FromSeconds(350)],
        };
        Assert.Equal([3, 0, 4], LocalLlmQuestionGenerator.PlanSections(ThreeSections(), request, 10));
    }

    [Fact]
    public void Section_plan_follows_the_requested_count_when_there_is_room()
    {
        var plan = LocalLlmQuestionGenerator.PlanSections(ThreeSections(), Request(count: 6, spacing: TimeSpan.FromSeconds(30)), 10);
        Assert.Equal([2, 2, 2], plan);
        Assert.Equal(5, LocalLlmQuestionGenerator.PlanSections(ThreeSections(), Request(count: 5, spacing: TimeSpan.Zero), 10).Sum());
    }
}
