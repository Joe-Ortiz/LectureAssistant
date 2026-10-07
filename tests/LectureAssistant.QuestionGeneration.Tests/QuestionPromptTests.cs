using LectureAssistant.Core;
using LectureAssistant.Core.Models;
using LectureAssistant.QuestionGeneration;
using static LectureAssistant.QuestionGeneration.Tests.TestData;

namespace LectureAssistant.QuestionGeneration.Tests;

public class QuestionPromptTests
{
    [Fact]
    public void Transcript_lines_use_whole_seconds_rounded_up()
    {
        var line = QuestionPrompt.FormatLine(new CaptionSegment(TimeSpan.FromSeconds(754.2), TimeSpan.FromSeconds(760), "  Hello\n world "));
        Assert.Equal("[755s] Hello world", line);
    }

    [Fact]
    public void Rounded_marker_snaps_back_to_the_same_segment()
    {
        var transcript = new List<CaptionSegment>
        {
            new(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(104.3), "a"),
            new(TimeSpan.FromSeconds(104.3), TimeSpan.FromSeconds(109), "b"),
            new(TimeSpan.FromSeconds(109), TimeSpan.FromSeconds(115), "c"),
        };
        // The model copies "[105s]" from line b; the pause must land at the end of b.
        Assert.Equal("[105s] b", QuestionPrompt.FormatLine(transcript[1]));
        var q = Assert.Single(QuestionPostProcessor.Process([Tf(105)], Request(transcript)));
        Assert.Equal(TimeSpan.FromSeconds(109), q.Timestamp);
    }

    [Fact]
    public void User_message_contains_request_details()
    {
        var request = Request(count: 7, spacing: TimeSpan.FromSeconds(45), types: [QuestionType.TrueFalse, QuestionType.MultipleChoice]) with
        {
            LectureTitle = "Thermodynamics 101",
            InstructorGuidance = "Focus on the second law.",
        };
        var message = QuestionPrompt.BuildUserMessage(request, 7);

        Assert.Contains("Lecture title: Thermodynamics 101", message);
        Assert.Contains("exactly 7 questions", message);
        Assert.Contains("Allowed types: multiple_choice, true_false", message);
        Assert.Contains("45 seconds", message);
        Assert.Contains("<guidance>\nFocus on the second law.\n</guidance>", message);
        Assert.Contains("[590s] Sentence starting at 590 seconds.", message);
        Assert.DoesNotContain("section", message);
    }

    [Fact]
    public void Section_message_names_the_section_and_only_its_lines()
    {
        var transcript = Transcript(600);
        var section = new TranscriptSection(2, 3, transcript.Skip(20).Take(20).ToList());
        var message = QuestionPrompt.BuildUserMessage(Request(transcript), 1, section);

        Assert.Contains("section 2 of 3", message);
        Assert.Contains("from 3:20 to 6:40", message);
        Assert.Contains("exactly 1 question.", message);
        Assert.Contains("[200s]", message);
        Assert.DoesNotContain("[190s]", message);
        Assert.DoesNotContain("[400s]", message);
    }

    [Fact]
    public void System_prompt_covers_the_rules_the_post_processor_relies_on()
    {
        Assert.Contains("___", QuestionPrompt.SystemPrompt);
        Assert.Contains("first 30 seconds", QuestionPrompt.SystemPrompt);
        Assert.Contains("[754s]", QuestionPrompt.SystemPrompt);
        Assert.Contains("source_excerpt", QuestionPrompt.SystemPrompt);
    }

    [Fact]
    public void System_prompt_asks_for_reasoning_before_the_answer()
    {
        Assert.Contains("before the answer fields", QuestionPrompt.SystemPrompt);
        Assert.Contains("Mix true and false", QuestionPrompt.SystemPrompt);
        Assert.Contains("ignored placeholders", QuestionPrompt.SystemPrompt);
        Assert.Contains("shuffles the options", QuestionPrompt.SystemPrompt);
    }

    [Theory]
    [InlineData("So the statement is true.", true)]
    [InlineData("So the statement is false.", false)]
    public void Verdict_sentence_the_prompt_asks_for_is_recognized(string sentence, bool expected)
    {
        Assert.Contains($"\"{sentence}\"", QuestionPrompt.SystemPrompt);
        Assert.Equal(expected, QuestionPostProcessor.StatedVerdict("The lecturer said so. " + sentence));
    }

    [Fact]
    public void Unusable_requests_get_friendly_errors()
    {
        var noTranscript = Assert.Throws<QuestionGenerationException>(() => QuestionPrompt.EnsureUsable(Request(transcript: [])));
        Assert.Equal(QuestionGenerationFailure.BadRequest, noTranscript.Failure);

        Assert.Throws<QuestionGenerationException>(() => QuestionPrompt.EnsureUsable(Request() with { AllowedTypes = [] }));
        Assert.Throws<QuestionGenerationException>(() => QuestionPrompt.EnsureUsable(Request(count: 0)));
    }
}
