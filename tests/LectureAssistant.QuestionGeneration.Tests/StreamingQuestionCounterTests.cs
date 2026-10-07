using System.Text.Encodings.Web;
using System.Text.Json;
using LectureAssistant.QuestionGeneration;
using static LectureAssistant.QuestionGeneration.Tests.TestData;

namespace LectureAssistant.QuestionGeneration.Tests;

public class StreamingQuestionCounterTests
{
    /// <summary>Strings full of JSON punctuation, so only real structure can be counted.</summary>
    internal static string ThreeQuestionsJson() => JsonSerializer.Serialize(new QuestionDraftSet
    {
        Questions =
        [
            Mc(30, "Which {set} is \"closed\" under [addition]?"),
            Tf(90, prompt: "A backslash \\ before a quote \\\" is fine }]"),
            Fib(150, "Braces { and } go in ___.", "C:\\temp\\", "{}"),
        ],
    }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); // \" rather than \u0022

    [Fact]
    public void Counts_questions_wherever_the_output_is_split()
    {
        var json = ThreeQuestionsJson();
        for (int split = 0; split <= json.Length; split++)
        {
            var counter = new StreamingQuestionCounter();
            counter.Append(json.AsSpan(0, split));
            counter.Append(json.AsSpan(split));
            Assert.Equal(3, counter.Completed);
        }
    }

    [Fact]
    public void Counts_one_character_at_a_time_and_reports_each_question_as_it_closes()
    {
        var json = ThreeQuestionsJson();
        var counter = new StreamingQuestionCounter();
        var finishedAt = new List<int>();
        for (int i = 0; i < json.Length; i++)
            if (counter.Append(json.AsSpan(i, 1)) == 1) finishedAt.Add(i);

        Assert.Equal(3, finishedAt.Count);
        // Each question finishes exactly where its object closes: the next character starts the next item or ends the array.
        Assert.All(finishedAt, i => Assert.Equal('}', json[i]));
        Assert.All(finishedAt, i => Assert.Contains(json[i + 1], ",]"));
    }

    [Fact]
    public void Braces_and_escaped_quotes_inside_strings_are_ignored()
    {
        const string json = """{"questions":[{"prompt":"a \"}\" b {[","explanation":"ends in a slash \\"},{"prompt":"]}}","options":[{"text":"}"}]}]}""";
        var counter = new StreamingQuestionCounter();
        foreach (var c in json) counter.Append([c]);
        Assert.Equal(2, counter.Completed);
    }

    [Fact]
    public void Unfinished_question_is_not_counted()
    {
        var json = ThreeQuestionsJson();
        var counter = new StreamingQuestionCounter();
        counter.Append(json.AsSpan(0, json.Length - 3)); // cut inside the last object: "...}]}" minus "}]}"
        Assert.Equal(2, counter.Completed);
    }
}
