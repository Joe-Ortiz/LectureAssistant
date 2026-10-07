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
