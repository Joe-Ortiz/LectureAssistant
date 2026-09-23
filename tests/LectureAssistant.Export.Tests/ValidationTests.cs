using LectureAssistant.Core;
using LectureAssistant.Core.Models;
using LectureAssistant.Export.H5P;
using LectureAssistant.Export.Scorm;

namespace LectureAssistant.Export.Tests;

public class ValidationTests
{
    public static TheoryData<ILectureExporter> Exporters => [new ScormExporter(), new H5PExporter()];

    [Theory]
    [MemberData(nameof(Exporters))]
    public void Valid_project_has_no_problems(ILectureExporter exporter)
    {
        Assert.Empty(exporter.Validate(TestData.Project()));
    }

    [Theory]
    [MemberData(nameof(Exporters))]
    public void Missing_video_is_reported(ILectureExporter exporter)
    {
        var project = TestData.Project();
        project.YouTubeUrl = null;
        Assert.Contains(exporter.Validate(project), p => p.Contains("YouTube", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Exporters))]
    public void Unrecognized_video_url_is_reported(ILectureExporter exporter)
    {
        var project = TestData.Project();
        project.YouTubeUrl = "https://vimeo.com/12345";
        Assert.Contains(exporter.Validate(project), p => p.Contains("vimeo.com", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Exporters))]
    public void No_questions_is_reported(ILectureExporter exporter)
    {
        var project = TestData.Project();
        project.Questions.Clear();
        Assert.Contains(exporter.Validate(project), p => p.Contains("at least one question", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Exporters))]
    public void Invalid_question_is_reported_with_its_timestamp(ILectureExporter exporter)
    {
        var project = TestData.Project();
        project.Questions[3].Prompt = "No blank here.";         // 5:00
        project.Questions[0].Options.ForEach(o => o.IsCorrect = false); // 0:30

        var problems = exporter.Validate(project);
        Assert.Contains(problems, p => p.StartsWith("Question at 5:00:", StringComparison.Ordinal) && p.Contains("___"));
        Assert.Contains(problems, p => p.StartsWith("Question at 0:30:", StringComparison.Ordinal) && p.Contains("correct option"));
    }

    [Theory]
    [MemberData(nameof(Exporters))]
    public void Question_after_end_of_video_is_reported(ILectureExporter exporter)
    {
        var project = TestData.Project();
        project.Questions[2].Timestamp = TimeSpan.FromMinutes(11);
        Assert.Contains(exporter.Validate(project), p => p.Contains("11:00") && p.Contains("after the end"));

        project.Duration = null;
        Assert.Empty(exporter.Validate(project));
    }

    [Fact]
    public void Missing_reference_package_is_reported()
    {
        var exporter = new H5PExporter(new H5PExportOptions { ReferencePackagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".h5p") });
        Assert.Contains(exporter.Validate(TestData.Project()), p => p.Contains("reference H5P package", StringComparison.Ordinal));
    }
}
