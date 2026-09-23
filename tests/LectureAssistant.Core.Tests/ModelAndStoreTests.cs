using LectureAssistant.Core.Models;
using LectureAssistant.Core.Persistence;

namespace LectureAssistant.Core.Tests;

public class ModelAndStoreTests
{
    [Fact]
    public void Valid_questions_have_no_problems()
    {
        Assert.Empty(new Question
        {
            Type = QuestionType.MultipleChoice,
            Prompt = "Which is a noble gas?",
            Options = [new("Neon", true), new("Nitrogen", false)],
        }.Validate());

        Assert.Empty(new Question { Type = QuestionType.TrueFalse, Prompt = "Water boils at 100 °C at sea level.", CorrectAnswer = true }.Validate());

        Assert.Empty(new Question
        {
            Type = QuestionType.FillInTheBlank,
            Prompt = "The powerhouse of the cell is the _____.",
            AcceptedAnswers = ["mitochondrion", "mitochondria"],
        }.Validate());
    }

    [Fact]
    public void Invalid_questions_report_problems()
    {
        Assert.NotEmpty(new Question { Type = QuestionType.MultipleChoice, Prompt = "Q", Options = [new("A", false), new("B", false)] }.Validate());
        Assert.NotEmpty(new Question { Type = QuestionType.MultipleChoice, Prompt = "Q", Options = [new("A", true)] }.Validate());
        Assert.NotEmpty(new Question { Type = QuestionType.FillInTheBlank, Prompt = "No blank here", AcceptedAnswers = ["x"] }.Validate());
        Assert.NotEmpty(new Question { Type = QuestionType.FillInTheBlank, Prompt = "___ and ___", AcceptedAnswers = ["x"] }.Validate());
        Assert.NotEmpty(new Question { Type = QuestionType.FillInTheBlank, Prompt = "The ___", AcceptedAnswers = [] }.Validate());
        Assert.NotEmpty(new Question { Type = QuestionType.TrueFalse, Prompt = " " }.Validate());
    }

    [Fact]
    public void Project_exposes_youtube_video_id()
    {
        Assert.Equal("dQw4w9WgXcQ", new LectureProject { YouTubeUrl = "https://youtu.be/dQw4w9WgXcQ" }.YouTubeVideoId);
        Assert.Null(new LectureProject { YouTubeUrl = "not a url" }.YouTubeVideoId);
    }

    [Fact]
    public async Task Store_round_trips_and_lists_projects()
    {
        var root = Path.Combine(Path.GetTempPath(), "la-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProjectStore(root);
            var project = new LectureProject
            {
                Title = "Thermodynamics 101",
                YouTubeUrl = "https://youtu.be/dQw4w9WgXcQ",
                Captions = [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "Hi")],
                Questions = [new() { Type = QuestionType.TrueFalse, Prompt = "Energy is conserved.", CorrectAnswer = true, Timestamp = TimeSpan.FromSeconds(95) }],
            };
            await store.SaveAsync(project);

            var loaded = await store.LoadAsync(project.Id);
            Assert.NotNull(loaded);
            Assert.Equal("Thermodynamics 101", loaded.Title);
            Assert.Equal(TimeSpan.FromSeconds(95), loaded.Questions[0].Timestamp);
            Assert.Equal(QuestionType.TrueFalse, loaded.Questions[0].Type);
            Assert.Single(loaded.Captions);

            Directory.CreateDirectory(Path.Combine(root, "corrupt"));
            await File.WriteAllTextAsync(Path.Combine(root, "corrupt", "project.json"), "{ nope");
            var all = await store.ListAsync();
            Assert.Equal(project.Id, Assert.Single(all).Id);

            store.Delete(project.Id);
            Assert.Null(await store.LoadAsync(project.Id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
