using LectureAssistant.Core;
using LectureAssistant.Core.Models;
using LectureAssistant.QuestionGeneration.Claude;

namespace LectureAssistant.QuestionGeneration.Tests;

/// <summary>Runs only when ANTHROPIC_API_KEY is set; otherwise reported as skipped.</summary>
public sealed class RequiresApiKeyFactAttribute : FactAttribute
{
    public RequiresApiKeyFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            Skip = "ANTHROPIC_API_KEY is not set.";
    }
}

public class ClaudeIntegrationTests
{
    private static readonly string[] Lines =
    [
        "Good morning everyone, before we start, a reminder that the problem set is due Friday.",
        "Today we're talking about the second law of thermodynamics.",
        "The second law says that the total entropy of an isolated system never decreases over time.",
        "Entropy, roughly, measures the number of microscopic arrangements consistent with what we observe.",
        "So a system naturally evolves toward macrostates with more microstates, because they're overwhelmingly more likely.",
        "That's why heat flows from a hot object to a cold one and never the reverse on its own.",
        "Now, a refrigerator does move heat from cold to hot, but only by doing work, and it increases entropy elsewhere.",
        "The total entropy, fridge plus room plus power plant, still goes up.",
        "Next, let's define the Carnot efficiency: one minus the cold temperature over the hot temperature, in kelvin.",
        "No heat engine operating between those two temperatures can be more efficient than that.",
        "So an engine between 300 kelvin and 600 kelvin can be at most fifty percent efficient.",
        "Real engines fall well short because of friction and irreversible heat transfer.",
    ];

    [RequiresApiKeyFact]
    public async Task Generates_valid_questions_from_a_short_lecture()
    {
        var transcript = Lines.Select((text, i) => new CaptionSegment(TimeSpan.FromSeconds(i * 25), TimeSpan.FromSeconds(i * 25 + 25), text)).ToList();
        var generator = new ClaudeQuestionGenerator(new ClaudeQuestionGeneratorOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
            Effort = ClaudeEffort.Low,
        });

        var questions = await generator.GenerateAsync(new QuestionGenerationRequest
        {
            Transcript = transcript,
            LectureTitle = "Second law of thermodynamics",
            QuestionCount = 3,
            MinimumSpacing = TimeSpan.FromSeconds(30),
        }, null, CancellationToken.None);

        Assert.InRange(questions.Count, 1, 3);
        Assert.All(questions, q => Assert.Empty(q.Validate()));
        Assert.All(questions, q => Assert.InRange(q.Timestamp, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(300)));
        Assert.Null(await ClaudeQuestionGenerator.ValidateApiKeyAsync(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")));
    }
}
