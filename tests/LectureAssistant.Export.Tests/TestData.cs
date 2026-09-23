using System.IO.Compression;
using System.Text;
using LectureAssistant.Core;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Export.Tests;

internal static class TestData
{
    public const string VideoId = "dQw4w9WgXcQ";

    public static LectureProject Project() => new()
    {
        Id = "0f8fad5bd9cb469fa16570867728950e",
        Title = "Intro to Thermodynamics",
        YouTubeUrl = $"https://www.youtube.com/watch?v={VideoId}",
        Duration = TimeSpan.FromMinutes(10),
        Questions =
        [
            new Question
            {
                Timestamp = TimeSpan.FromSeconds(30),
                Type = QuestionType.MultipleChoice,
                Prompt = "Which quantity is conserved in an isolated system?",
                Options =
                [
                    new AnswerOption("Energy", true, "Right: the first law."),
                    new AnswerOption("Entropy", false, "Entropy can increase."),
                    new AnswerOption("Temperature", false),
                ],
                Explanation = "The first law of thermodynamics.",
                Points = 2,
            },
            new Question
            {
                Timestamp = TimeSpan.FromSeconds(95.5),
                Type = QuestionType.MultipleChoice,
                Prompt = "Which are state functions?",
                Options =
                [
                    new AnswerOption("Internal energy", true),
                    new AnswerOption("Enthalpy", true),
                    new AnswerOption("Work", false),
                ],
            },
            new Question
            {
                Timestamp = TimeSpan.FromMinutes(3),
                Type = QuestionType.TrueFalse,
                Prompt = "Heat flows spontaneously from cold to hot.",
                CorrectAnswer = false,
                Explanation = "Second law.",
            },
            new Question
            {
                Timestamp = TimeSpan.FromMinutes(5),
                Type = QuestionType.FillInTheBlank,
                Prompt = "The SI unit of energy is the _____.",
                AcceptedAnswers = ["joule", "J"],
            },
        ],
    };

    public static async Task<byte[]> ExportAsync(ILectureExporter exporter, LectureProject project)
    {
        using var output = new MemoryStream();
        await exporter.ExportAsync(project, output, CancellationToken.None);
        return output.ToArray();
    }

    public static ZipArchive OpenZip(byte[] bytes) => new(new MemoryStream(bytes), ZipArchiveMode.Read);

    public static string ReadText(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? throw new Xunit.Sdk.XunitException($"Missing zip entry '{path}'.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
