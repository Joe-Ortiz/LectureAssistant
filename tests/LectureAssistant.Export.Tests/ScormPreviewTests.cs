using System.IO.Compression;
using System.Text.Json;
using LectureAssistant.Core.Models;
using LectureAssistant.Export.Scorm;

namespace LectureAssistant.Export.Tests;

public sealed class ScormPreviewTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "la-preview-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private JsonElement ReadData()
    {
        var script = File.ReadAllText(Path.Combine(_folder, "lecture-data.js"));
        var json = script[LectureDataScript.Prefix.Length..].TrimEnd().TrimEnd(';');
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task Writes_the_student_player_and_the_host_page()
    {
        await ScormPreview.WriteAsync(TestData.Project(), _folder, null, default);

        foreach (var file in new[] { "index.html", "player.js", "player.css", "lecture-data.js", ScormPreview.HostPage })
            Assert.True(File.Exists(Path.Combine(_folder, file)), file);

        var host = File.ReadAllText(Path.Combine(_folder, ScormPreview.HostPage));
        Assert.Contains("window.API", host);
        Assert.Contains("index.html", host);
    }

    [Fact]
    public async Task Uses_youtube_when_no_local_video_is_given()
    {
        await ScormPreview.WriteAsync(TestData.Project(), _folder, null, default);
        var data = ReadData();
        Assert.Equal(TestData.VideoId, data.GetProperty("videoId").GetString());
        Assert.False(data.TryGetProperty("videoUrl", out _));
        Assert.False(File.Exists(Path.Combine(_folder, "captions.vtt")));
    }

    [Fact]
    public async Task Local_video_plays_with_captions_and_needs_no_youtube_link()
    {
        var project = TestData.Project();
        project.YouTubeUrl = null;
        project.Captions = [new CaptionSegment(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "Hello, class.")];
        Assert.Empty(ScormPreview.Validate(project, hasLocalVideo: true));
        Assert.NotEmpty(ScormPreview.Validate(project, hasLocalVideo: false));

        await ScormPreview.WriteAsync(project, _folder, new PreviewVideo("https://video.example/lecture%201.mp4"), default);

        var data = ReadData();
        Assert.Equal("https://video.example/lecture%201.mp4", data.GetProperty("videoUrl").GetString());
        Assert.Equal("captions.vtt", data.GetProperty("captionsUrl").GetString());
        Assert.Contains("Hello, class.", File.ReadAllText(Path.Combine(_folder, "captions.vtt")));
    }

    [Fact]
    public async Task Preview_scores_retries_like_the_exported_package()
    {
        // The simulated gradebook shows whatever the real player reports, so it only needs the same rules.
        var project = TestData.Project();
        project.Quiz.AttemptsAllowed = 2;
        project.Quiz.RetryScoring = RetryScoring.ReducedCredit;
        project.Questions[0].RetryScoring = RetryScoring.FullCredit;
        await ScormPreview.WriteAsync(project, _folder, null, default);

        var first = ReadData().GetProperty("questions")[0];
        Assert.Equal(2, first.GetProperty("attemptsAllowed").GetInt32());
        Assert.Equal("FullCredit", first.GetProperty("retryScoring").GetString());
        Assert.Equal(
            File.ReadAllText(Path.Combine(_folder, "lecture-data.js")),
            LectureDataScript.Build(project));
    }

    [Fact]
    public async Task Replaces_a_previous_preview()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "stale.txt"), "old");
        await ScormPreview.WriteAsync(TestData.Project(), _folder, null, default);
        Assert.False(File.Exists(Path.Combine(_folder, "stale.txt")));
    }

    [Fact]
    public async Task Refuses_invalid_questions()
    {
        var project = TestData.Project();
        project.Questions[0].Prompt = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScormPreview.WriteAsync(project, _folder, null, default));
    }

    [Fact]
    public async Task Exported_packages_never_contain_preview_parts()
    {
        using var buffer = new MemoryStream();
        await new ScormExporter().ExportAsync(TestData.Project(), buffer, default);
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()));

        Assert.DoesNotContain(zip.Entries, e => e.FullName == ScormPreview.HostPage);
        using var reader = new StreamReader(zip.GetEntry("lecture-data.js")!.Open());
        var script = reader.ReadToEnd();
        Assert.DoesNotContain("videoUrl", script);
        Assert.DoesNotContain("captionsUrl", script);
    }
}
