using System.Text.Json;
using System.Text.Json.Serialization;
using LectureAssistant.Core.Models;

namespace LectureAssistant.QuestionGeneration;

/// <summary>
/// The JSON shape language models are asked to produce. Loosely typed on purpose (type is a string,
/// every field present for every type) so that bad output can be parsed and then rejected by
/// <see cref="QuestionPostProcessor"/> rather than failing deserialization.
/// </summary>
public sealed class QuestionDraftSet
{
    [JsonPropertyName("questions")]
    public List<QuestionDraft> Questions { get; set; } = [];
}

public sealed class QuestionDraft
{
    /// <summary>Short verbatim quote from the transcript the question is based on.</summary>
    [JsonPropertyName("source_excerpt")]
    public string? SourceExcerpt { get; set; }

    /// <summary>Seconds from the start of the video; the post-processor snaps it to a segment end.</summary>
    [JsonPropertyName("timestamp_seconds")]
    public double TimestampSeconds { get; set; }

    /// <summary>One of <see cref="QuestionDraftSchema.TypeNames"/>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("options")]
    public List<QuestionDraftOption>? Options { get; set; }

    [JsonPropertyName("correct_answer")]
    public bool? CorrectAnswer { get; set; }

    [JsonPropertyName("accepted_answers")]
    public List<string>? AcceptedAnswers { get; set; }

    [JsonPropertyName("explanation")]
    public string? Explanation { get; set; }
}

public sealed class QuestionDraftOption
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("is_correct")]
    public bool IsCorrect { get; set; }

    [JsonPropertyName("feedback")]
    public string? Feedback { get; set; }
}

public static class QuestionDraftSchema
{
    public const string MultipleChoice = "multiple_choice";
    public const string TrueFalse = "true_false";
    public const string FillInTheBlank = "fill_in_the_blank";

    public static readonly IReadOnlyDictionary<QuestionType, string> TypeNames = new Dictionary<QuestionType, string>
    {
        [QuestionType.MultipleChoice] = MultipleChoice,
        [QuestionType.TrueFalse] = TrueFalse,
        [QuestionType.FillInTheBlank] = FillInTheBlank,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Accepts the schema names plus common variants ("MultipleChoice", "true-false", "fill in the blank").</summary>
    public static QuestionType? ParseType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return key switch
        {
            "multiplechoice" or "mc" or "mcq" => QuestionType.MultipleChoice,
            "truefalse" or "tf" or "trueorfalse" => QuestionType.TrueFalse,
            "fillintheblank" or "fillintheblanks" or "fillblank" or "cloze" => QuestionType.FillInTheBlank,
            _ => null,
        };
    }

    /// <summary>Parses model output; tolerates code fences or chatter around the JSON object.</summary>
    /// <exception cref="JsonException">No JSON object could be read.</exception>
    public static QuestionDraftSet Parse(string json)
    {
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) throw new JsonException("No JSON object found in model output.");
        return JsonSerializer.Deserialize<QuestionDraftSet>(json.AsSpan(start, end - start + 1), ReadOptions)
            ?? throw new JsonException("Model output was null.");
    }

    /// <summary>
    /// Like <see cref="Parse"/>, but if the JSON is cut off (output limit reached) returns the questions that
    /// were completed before the cut instead of failing.
    /// </summary>
    public static List<QuestionDraft> ParseLenient(string json)
    {
        try
        {
            return Parse(json).Questions;
        }
        catch (JsonException)
        {
            return SalvageQuestions(json);
        }
    }

    /// <summary>Deserializes each complete object found at the depth of the "questions" array items.</summary>
    private static List<QuestionDraft> SalvageQuestions(string json)
    {
        var result = new List<QuestionDraft>();
        int depth = 0, itemStart = -1;
        bool inString = false, escaped = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{' or '[':
                    depth++;
                    if (c == '{' && depth == 3) itemStart = i; // root { -> questions [ -> item {
                    break;
                case '}' or ']':
                    if (c == '}' && depth == 3 && itemStart >= 0)
                    {
                        try
                        {
                            var draft = JsonSerializer.Deserialize<QuestionDraft>(json.AsSpan(itemStart, i - itemStart + 1), ReadOptions);
                            if (draft is not null) result.Add(draft);
                        }
                        catch (JsonException) { }
                        itemStart = -1;
                    }
                    depth--;
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// JSON Schema for structured outputs. Every property is required (structured outputs and the local
    /// grammar both work best with a fixed shape); unused fields are empty for the other types.
    /// </summary>
    public static string BuildJsonSchema(IEnumerable<QuestionType> allowedTypes)
    {
        var types = TypeNamesFor(allowedTypes);
        var schema = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "questions" },
            properties = new
            {
                questions = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[]
                        {
                            "source_excerpt", "timestamp_seconds", "type", "prompt", "options",
                            "correct_answer", "accepted_answers", "explanation",
                        },
                        properties = new Dictionary<string, object>
                        {
                            ["source_excerpt"] = new { type = "string", description = "Short verbatim quote (one or two sentences) from the transcript the question is based on." },
                            ["timestamp_seconds"] = new { type = "number", description = "Seconds value of the [Ns] marker on the transcript line where the relevant explanation finishes." },
                            ["type"] = new { type = "string", @enum = types },
                            ["prompt"] = new { type = "string", description = "The question. For fill_in_the_blank, a statement containing exactly one ___." },
                            ["options"] = new
                            {
                                type = "array",
                                description = "multiple_choice only (3-5 options); empty array for other types.",
                                items = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    required = new[] { "text", "is_correct", "feedback" },
                                    properties = new
                                    {
                                        text = new { type = "string" },
                                        is_correct = new { type = "boolean" },
                                        feedback = new { type = "string", description = "One sentence shown if the student picks this option." },
                                    },
                                },
                            },
                            ["correct_answer"] = new { type = "boolean", description = "true_false only; false for other types." },
                            ["accepted_answers"] = new
                            {
                                type = "array",
                                description = "fill_in_the_blank only: every acceptable answer (synonyms, abbreviations, spellings); empty for other types.",
                                items = new { type = "string" },
                            },
                            ["explanation"] = new { type = "string", description = "Why the answer is right, grounded in what the lecturer said." },
                        },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(schema);
    }

    internal static string[] TypeNamesFor(IEnumerable<QuestionType> allowedTypes)
    {
        var names = allowedTypes.Distinct().Order().Select(t => TypeNames[t]).ToArray();
        return names.Length > 0 ? names : [.. TypeNames.Values];
    }
}
