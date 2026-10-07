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

    [Theory]
    [InlineData(262144, 262144, new[] { 262144, 131072, 65536, 32768, 16384, 8192 })]
    [InlineData(16384, 262144, new[] { 16384, 8192 })]
    [InlineData(10000, 262144, new[] { 10000, 8192 })]
    [InlineData(8192, 262144, new[] { 8192 })]
    public void Context_sizes_halve_down_to_the_minimum(int requested, int trained, int[] expected) =>
        Assert.Equal(expected, LocalLlmQuestionGenerator.ContextSizesToTry(requested, trained));

    [Fact]
    public void Context_size_below_the_minimum_is_tried_as_is() =>
        Assert.Equal([4096], LocalLlmQuestionGenerator.ContextSizesToTry(4096, 262144));

    [Fact]
    public void Context_size_is_capped_at_the_models_training_context()
    {
        Assert.Equal([32768, 16384, 8192], LocalLlmQuestionGenerator.ContextSizesToTry(131072, 32768));
        // Never below what question generation needs, even for a model trained on less.
        Assert.Equal([8192], LocalLlmQuestionGenerator.ContextSizesToTry(16384, 4096));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Unknown_training_context_leaves_the_request_alone(int trained) =>
        Assert.Equal([65536, 32768, 16384, 8192], LocalLlmQuestionGenerator.ContextSizesToTry(65536, trained));
}
