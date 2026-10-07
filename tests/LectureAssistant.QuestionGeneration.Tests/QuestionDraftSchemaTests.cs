using System.Text.Json;
using LectureAssistant.Core.Models;
using LectureAssistant.QuestionGeneration;

namespace LectureAssistant.QuestionGeneration.Tests;

public class QuestionDraftSchemaTests
{
    private const string SampleJson = """
        {"questions":[
          {"source_excerpt":"Entropy never decreases {in an isolated system}.","timestamp_seconds":95,"type":"true_false",
           "prompt":"Entropy of an isolated system can decrease.","options":[],"correct_answer":false,"accepted_answers":[],
           "explanation":"The second law says it never decreases."},
          {"source_excerpt":"quote","timestamp_seconds":"210.5","type":"fill_in_the_blank",
           "prompt":"Energy is measured in ___.","options":[],"correct_answer":false,"accepted_answers":["joules","J"],
           "explanation":"SI unit."}
        ]}
        """;

    [Fact]
    public void Parses_model_output_including_numbers_as_strings()
    {
        var set = QuestionDraftSchema.Parse(SampleJson);
        Assert.Equal(2, set.Questions.Count);
        Assert.Equal(95, set.Questions[0].TimestampSeconds);
        Assert.False(set.Questions[0].CorrectAnswer);
        Assert.Equal(210.5, set.Questions[1].TimestampSeconds);
        Assert.Equal(["joules", "J"], set.Questions[1].AcceptedAnswers);
    }

    [Fact]
    public void Parse_tolerates_code_fences_and_chatter()
    {
        var set = QuestionDraftSchema.Parse("Here you go:\n```json\n" + SampleJson + "\n```\nHope that helps!");
        Assert.Equal(2, set.Questions.Count);
    }

    [Fact]
    public void Parse_throws_on_non_json()
    {
        Assert.ThrowsAny<JsonException>(() => QuestionDraftSchema.Parse("I can't help with that."));
    }

    [Fact]
    public void ParseLenient_salvages_complete_questions_from_truncated_output()
    {
        var truncated = SampleJson[..(SampleJson.IndexOf("\"Energy is", StringComparison.Ordinal))];
        var drafts = QuestionDraftSchema.ParseLenient(truncated);
        var only = Assert.Single(drafts);
        Assert.Equal("Entropy of an isolated system can decrease.", only.Prompt);
        Assert.Contains("{in an isolated system}", only.SourceExcerpt); // braces inside strings don't confuse it
    }

    [Fact]
    public void ParseLenient_returns_empty_for_garbage()
    {
        Assert.Empty(QuestionDraftSchema.ParseLenient("nonsense"));
    }

    [Fact]
    public void Schema_lists_only_allowed_types_in_enum()
    {
        using var doc = JsonDocument.Parse(QuestionDraftSchema.BuildJsonSchema([QuestionType.TrueFalse, QuestionType.MultipleChoice]));
        var typeEnum = doc.RootElement.GetProperty("properties").GetProperty("questions").GetProperty("items")
            .GetProperty("properties").GetProperty("type").GetProperty("enum");
        Assert.Equal(["multiple_choice", "true_false"], typeEnum.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Schema_objects_are_closed_and_require_every_property()
    {
        using var doc = JsonDocument.Parse(QuestionDraftSchema.BuildJsonSchema(Enum.GetValues<QuestionType>()));
        var objects = 0;
        Visit(doc.RootElement);
        Assert.Equal(3, objects); // root, question, option

        void Visit(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "object")
            {
                objects++;
                Assert.False(e.GetProperty("additionalProperties").GetBoolean());
                var props = e.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order();
                var required = e.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).Order();
                Assert.Equal(props, required);
            }
            foreach (var p in e.EnumerateObject()) Visit(p.Value);
        }
    }

    [Fact]
    public void Schema_puts_the_explanation_before_the_answer_fields()
    {
        // Structured outputs emit required properties in schema order; the model should reason before answering.
        using var doc = JsonDocument.Parse(QuestionDraftSchema.BuildJsonSchema(Enum.GetValues<QuestionType>()));
        var item = doc.RootElement.GetProperty("properties").GetProperty("questions").GetProperty("items");
        string[] expected =
        [
            "source_excerpt", "timestamp_seconds", "type", "prompt", "explanation",
            "options", "correct_answer", "accepted_answers",
        ];
        Assert.Equal(expected, item.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(expected, item.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Correct_answer_is_not_described_as_defaulting_to_false()
    {
        using var doc = JsonDocument.Parse(QuestionDraftSchema.BuildJsonSchema(Enum.GetValues<QuestionType>()));
        var description = doc.RootElement.GetProperty("properties").GetProperty("questions").GetProperty("items")
            .GetProperty("properties").GetProperty("correct_answer").GetProperty("description").GetString();
        Assert.Contains("placeholder", description);
        Assert.DoesNotContain("false for other types", description);
    }

    [Fact]
    public void Schema_property_names_match_the_dto()
    {
        // A draft serialized with the DTO's names must round-trip through the schema's names.
        using var doc = JsonDocument.Parse(QuestionDraftSchema.BuildJsonSchema(Enum.GetValues<QuestionType>()));
        var schemaProps = doc.RootElement.GetProperty("properties").GetProperty("questions").GetProperty("items")
            .GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToList();
        var dtoProps = JsonDocument.Parse(JsonSerializer.Serialize(new QuestionDraft())).RootElement
            .EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.Equal(dtoProps, schemaProps);
    }
}
