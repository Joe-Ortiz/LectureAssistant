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
}
