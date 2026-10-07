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

    [Fact]
    public async Task Streamed_answer_is_assembled_and_questions_are_counted_as_they_arrive()
    {
        var json = QuestionProgressTrackerTests.QuestionsJson(3);
        var events = new List<string>
        {
            """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Plan: {three} questions"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
        };
        // Text in uneven chunks, like the API sends.
        for (int i = 0; i < json.Length; i += 37)
            events.Add(JsonSerializer.Serialize(new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = json.Substring(i, Math.Min(37, json.Length - i)) } }));
        events.Add("""{"type":"content_block_stop","index":1}""");
        events.Add("""{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":500}}""");
        events.Add("""{"type":"message_stop"}""");

        var reports = new List<Core.QuestionGenerationProgress>();
        int seen = 0;
        var message = await ClaudeQuestionGenerator.ReadStreamAsync(
            Events(events), 3, new SyncProgress(reports), () => seen++, CancellationToken.None, new FakeTime());

        Assert.Equal(events.Count, seen);
        Assert.Equal(json, ClaudeQuestionGenerator.ReadJson(message));
        Assert.Equal(3, QuestionDraftSchema.Parse(ClaudeQuestionGenerator.ReadJson(message)).Questions.Count);

        Assert.Null(reports[0].Fraction); // thinking: no way to know how long
        Assert.StartsWith("Claude is reading the lecture", reports[0].Message);
        Assert.Equal(["Writing question 1 of 3…", "Writing question 2 of 3…", "Writing question 3 of 3…"],
            reports.Skip(1).Select(r => r.Message).Distinct());
        var fractions = reports.Skip(1).Select(r => r.Fraction!.Value).ToList();
        Assert.Equal(fractions.Order(), fractions);
        Assert.Equal(1, fractions[^1]);
    }

    [Fact]
    public async Task Streamed_server_side_fallback_block_is_skipped()
    {
        string[] events =
        [
            """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"fallback","from":{"model":"claude-opus-5"},"to":{"model":"claude-opus-4-8"}}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"{\"questions\":[]}"}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}""",
            """{"type":"message_stop"}""",
        ];
        var message = await ClaudeQuestionGenerator.ReadStreamAsync(Events(events), 3, null, null, CancellationToken.None);
        Assert.Equal("{\"questions\":[]}", ClaudeQuestionGenerator.ReadJson(message));
    }

    [Fact]
    public async Task Streamed_refusal_keeps_its_stop_reason()
    {
        string[] events =
        [
            """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}""",
            """{"type":"message_delta","delta":{"stop_reason":"refusal","stop_sequence":null},"usage":{"output_tokens":1}}""",
            """{"type":"message_stop"}""",
        ];
        var message = await ClaudeQuestionGenerator.ReadStreamAsync(Events(events), 3, null, null, CancellationToken.None);
        var ex = Assert.Throws<QuestionGenerationException>(() => ClaudeQuestionGenerator.ReadJson(message));
        Assert.Equal(QuestionGenerationFailure.Refused, ex.Failure);
    }

    [Fact]
    public void Stream_errors_are_classified()
    {
        Assert.Equal(QuestionGenerationFailure.ServiceUnavailable,
            ClaudeQuestionGenerator.Translate(new Anthropic.Exceptions.AnthropicSseException("overloaded_error", null!), CancellationToken.None)!.Failure);
        Assert.Equal(QuestionGenerationFailure.Network,
            ClaudeQuestionGenerator.Translate(new IOException("connection reset"), CancellationToken.None)!.Failure);
    }

    private static async IAsyncEnumerable<BetaRawMessageStreamEvent> Events(IEnumerable<string> json)
    {
        foreach (var e in json)
        {
            await Task.Yield();
            yield return JsonSerializer.Deserialize<BetaRawMessageStreamEvent>(e)!;
        }
    }

    private sealed class SyncProgress(List<Core.QuestionGenerationProgress> reports) : IProgress<Core.QuestionGenerationProgress>
    {
        public void Report(Core.QuestionGenerationProgress value) => reports.Add(value);
    }

    private static BetaMessage Message(string stopReason, string contentJson) =>
        BetaMessage.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($$$"""
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5","content":{{{contentJson}}},
             "stop_reason":"{{{stopReason}}}","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
            """)!);

    private static JsonElement Json<T>(T value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;
}
