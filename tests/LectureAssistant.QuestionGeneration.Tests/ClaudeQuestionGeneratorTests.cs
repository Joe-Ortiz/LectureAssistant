using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using LectureAssistant.Core.Models;
using LectureAssistant.QuestionGeneration;
using LectureAssistant.QuestionGeneration.Claude;
using static LectureAssistant.QuestionGeneration.Tests.TestData;

namespace LectureAssistant.QuestionGeneration.Tests;

public class ClaudeQuestionGeneratorTests
{
    [Fact]
    public void Parameters_use_model_effort_thinking_and_structured_output()
    {
        var p = ClaudeQuestionGenerator.BuildParameters(Request(), new ClaudeQuestionGeneratorOptions { ApiKey = "k", Effort = ClaudeEffort.Medium });
        Assert.Equal("claude-opus-5", p.Model.Raw());
        Assert.Equal(16000, p.MaxTokens);
        Assert.Equal("adaptive", Json(p.Thinking).GetProperty("type").GetString());
        var output = Json(p.OutputConfig);
        Assert.Equal("medium", output.GetProperty("effort").GetString());
        Assert.Equal("json_schema", output.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("object", output.GetProperty("format").GetProperty("schema").GetProperty("type").GetString());
        Assert.Contains("[590s]", Json(p.Messages)[0].GetProperty("content").GetString());
        Assert.Equal(QuestionPrompt.SystemPrompt, Json(p.System).GetString());
    }

    [Fact]
    public void Request_schema_keeps_the_explanation_before_the_answer_fields()
    {
        // Claude writes properties in schema order, so the order must survive serialization into the request.
        var p = ClaudeQuestionGenerator.BuildParameters(Request(), new ClaudeQuestionGeneratorOptions { ApiKey = "k" });
        var names = Json(p.OutputConfig).GetProperty("format").GetProperty("schema").GetProperty("properties")
            .GetProperty("questions").GetProperty("items").GetProperty("properties").EnumerateObject().Select(e => e.Name).ToList();
        Assert.True(names.IndexOf("explanation") > names.IndexOf("prompt"));
        Assert.True(names.IndexOf("explanation") < names.IndexOf("options"));
        Assert.True(names.IndexOf("explanation") < names.IndexOf("correct_answer"));
    }

    [Fact]
    public void Default_fallback_uses_the_scalar_form_and_its_beta()
    {
        var p = ClaudeQuestionGenerator.BuildParameters(Request(), new ClaudeQuestionGeneratorOptions { ApiKey = "k" });
        Assert.Equal("\"default\"", JsonSerializer.Serialize(p.Fallbacks));
        Assert.Equal(["server-side-fallback-2026-07-01"], p.Betas!.Select(b => b.Raw()));
    }

    [Fact]
    public void Pinned_fallback_uses_the_array_form_and_its_beta()
    {
        var p = ClaudeQuestionGenerator.BuildParameters(Request(), new ClaudeQuestionGeneratorOptions { ApiKey = "k", FallbackModel = "claude-opus-4-8" });
        Assert.Equal("[{\"model\":\"claude-opus-4-8\"}]", JsonSerializer.Serialize(p.Fallbacks));
        Assert.Equal(["server-side-fallback-2026-06-01"], p.Betas!.Select(b => b.Raw()));
    }

    [Fact]
    public void Fallback_can_be_disabled()
    {
        var p = ClaudeQuestionGenerator.BuildParameters(Request(), new ClaudeQuestionGeneratorOptions { ApiKey = "k", EnableRefusalFallback = false });
        Assert.Null(p.Fallbacks);
        Assert.Null(p.Betas);
    }

    [Theory]
    [InlineData("refusal", QuestionGenerationFailure.Refused)]
    [InlineData("max_tokens", QuestionGenerationFailure.InvalidResponse)]
    public void Stop_reason_is_checked_before_content(string stopReason, QuestionGenerationFailure expected)
    {
        var message = Message(stopReason, """[{"type":"text","text":"{\"questions\":[]}"}]""");
        var ex = Assert.Throws<QuestionGenerationException>(() => ClaudeQuestionGenerator.ReadJson(message));
        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public void Refusal_with_empty_content_is_reported_as_refusal()
    {
        var ex = Assert.Throws<QuestionGenerationException>(() => ClaudeQuestionGenerator.ReadJson(Message("refusal", "[]")));
        Assert.Equal(QuestionGenerationFailure.Refused, ex.Failure);
    }

    [Fact]
    public void Text_blocks_are_read_and_other_blocks_skipped()
    {
        var message = Message("end_turn", """
            [{"type":"fallback","from":{"model":"claude-opus-5"},"to":{"model":"claude-opus-4-8"}},
             {"type":"thinking","thinking":"","signature":"sig"},
             {"type":"text","text":"{\"questions\":[]}"}]
            """);
        Assert.Equal("{\"questions\":[]}", ClaudeQuestionGenerator.ReadJson(message));
    }

    [Fact]
    public async Task Missing_api_key_fails_fast_without_network()
    {
        var generator = new ClaudeQuestionGenerator(new ClaudeQuestionGeneratorOptions { ApiKey = " " });
        var ex = await Assert.ThrowsAsync<QuestionGenerationException>(() => generator.GenerateAsync(Request(), null, CancellationToken.None));
        Assert.Equal(QuestionGenerationFailure.InvalidApiKey, ex.Failure);
        Assert.Equal(ClaudeQuestionGenerator.MissingKeyMessage, await ClaudeQuestionGenerator.ValidateApiKeyAsync(null));
    }

    [Fact]
    public void Network_and_cancellation_are_classified()
    {
        using var cts = new CancellationTokenSource();
        Assert.Equal(QuestionGenerationFailure.Network,
            ClaudeQuestionGenerator.Translate(new HttpRequestException("dns"), cts.Token)!.Failure);
        cts.Cancel();
        Assert.Null(ClaudeQuestionGenerator.Translate(new OperationCanceledException(cts.Token), cts.Token));
        Assert.Null(ClaudeQuestionGenerator.Translate(new InvalidOperationException(), CancellationToken.None));
    }

    private static BetaMessage Message(string stopReason, string contentJson) =>
        BetaMessage.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($$$"""
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5","content":{{{contentJson}}},
             "stop_reason":"{{{stopReason}}}","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
            """)!);

    private static JsonElement Json<T>(T value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;
}
