using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using LectureAssistant.Core.Models;
using LectureAssistant.Export.H5P;

namespace LectureAssistant.Export.Tests;

public class H5PExporterTests
{
    [Fact]
    public async Task Package_has_h5p_json_and_content_json_only()
    {
        using var zip = TestData.OpenZip(await TestData.ExportAsync(new H5PExporter(), TestData.Project()));
        Assert.Equal(["h5p.json", "content/content.json"], zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task H5p_json_declares_interactive_video_and_used_libraries()
    {
        var project = TestData.Project();
        project.Title = "Heat & Work";
        using var zip = TestData.OpenZip(await TestData.ExportAsync(new H5PExporter(), project));
        var package = JsonNode.Parse(TestData.ReadText(zip, "h5p.json"))!;

        Assert.Equal("Heat & Work", (string?)package["title"]);
        Assert.Equal("und", (string?)package["language"]);
        Assert.Equal("H5P.InteractiveVideo", (string?)package["mainLibrary"]);
        Assert.Equal("U", (string?)package["license"]);
        Assert.Equal(["iframe"], package["embedTypes"]!.AsArray().Select(n => (string?)n));

        var dependencies = Dependencies(package);
        Assert.Equal("1.28", dependencies["H5P.InteractiveVideo"]);
        Assert.Equal("1.6", dependencies["H5P.Video"]);
        Assert.Equal("1.16", dependencies["H5P.MultiChoice"]);
        Assert.Equal("1.8", dependencies["H5P.TrueFalse"]);
        Assert.Equal("1.14", dependencies["H5P.Blanks"]);
    }

    [Fact]
    public async Task Unused_question_libraries_are_not_declared()
    {
        var project = TestData.Project();
        project.Questions.RemoveAll(q => q.Type != QuestionType.TrueFalse);
        using var zip = TestData.OpenZip(await TestData.ExportAsync(new H5PExporter(), project));
        var dependencies = Dependencies(JsonNode.Parse(TestData.ReadText(zip, "h5p.json"))!);
        Assert.Equal(["H5P.InteractiveVideo", "H5P.Video", "H5P.TrueFalse"], dependencies.Keys);
    }

    [Fact]
    public async Task Content_uses_youtube_and_one_interaction_per_question()
    {
        var project = TestData.Project();
        var content = await ExportContentAsync(new H5PExporter(), project);
        var iv = content["interactiveVideo"]!;

        var file = Assert.Single(iv["video"]!["files"]!.AsArray())!;
        Assert.Equal($"https://www.youtube.com/watch?v={TestData.VideoId}", (string?)file["path"]);
        Assert.Equal("video/YouTube", (string?)file["mime"]);
        Assert.Empty(iv["video"]!["textTracks"]!["videoTrack"]!.AsArray());

        var interactions = iv["assets"]!["interactions"]!.AsArray();
        Assert.Equal(project.Questions.Count, interactions.Count);
        Assert.Equal(
            ["H5P.MultiChoice 1.16", "H5P.MultiChoice 1.16", "H5P.TrueFalse 1.8", "H5P.Blanks 1.14"],
            interactions.Select(i => (string?)i!["action"]!["library"]));

        var first = interactions[0]!;
        Assert.Equal(30, (double)first["duration"]!["from"]!);
        Assert.Equal(30, (double)first["duration"]!["to"]!);
        Assert.True((bool)first["pause"]!);
        Assert.Equal("poster", (string?)first["displayType"]);
        Assert.Equal(project.Questions[0].Id, ((string?)first["action"]!["subContentId"])!.Replace("-", ""));
        Assert.NotNull(first["adaptivity"]);
        Assert.NotNull(first["x"]);
        Assert.NotNull(first["width"]);

        Assert.Empty(iv["assets"]!["bookmarks"]!.AsArray());
        Assert.Empty(iv["assets"]!["endscreens"]!.AsArray());
        Assert.Null(iv["summary"]!["task"]);

        Assert.Equal("forward", (string?)content["override"]!["preventSkippingMode"]);
        Assert.Equal("Continue", (string?)content["l10n"]!["defaultAdaptivitySeekLabel"]);
    }

    [Fact]
    public async Task Question_params_match_library_semantics()
    {
        var content = await ExportContentAsync(new H5PExporter(), TestData.Project());
        var interactions = content["interactiveVideo"]!["assets"]!["interactions"]!.AsArray();

        var single = interactions[0]!["action"]!["params"]!;
        Assert.Equal("<p>Which quantity is conserved in an isolated system?</p>", (string?)single["question"]);
        Assert.Equal("single", (string?)single["behaviour"]!["type"]);
        Assert.Equal("<div>Energy</div>", (string?)single["answers"]![0]!["text"]);
        Assert.True((bool)single["answers"]![0]!["correct"]!);
        Assert.Equal("<div>Right: the first law.</div>", (string?)single["answers"]![0]!["tipsAndFeedback"]!["chosenFeedback"]);
        Assert.Equal("The first law of thermodynamics.", (string?)single["overallFeedback"]![0]!["feedback"]);
        Assert.Equal("Check", (string?)single["UI"]!["checkAnswerButton"]);
        Assert.Equal("Finish ?", (string?)single["confirmCheck"]!["header"]);

        var multi = interactions[1]!["action"]!["params"]!;
        Assert.Equal("multi", (string?)multi["behaviour"]!["type"]);
        Assert.True((bool)multi["behaviour"]!["singlePoint"]!);

        var trueFalse = interactions[2]!["action"]!["params"]!;
        Assert.Equal("false", (string?)trueFalse["correct"]);
        Assert.Equal("True", (string?)trueFalse["l10n"]!["trueText"]);
        Assert.Equal("Second law.", (string?)trueFalse["behaviour"]!["feedbackOnCorrect"]);

        var blanks = interactions[3]!["action"]!["params"]!;
        Assert.Equal("<p>The SI unit of energy is the *joule/J*.</p>", (string?)Assert.Single(blanks["questions"]!.AsArray()));
        Assert.False((bool)blanks["behaviour"]!["caseSensitive"]!);
        Assert.Equal("Blank input @num of @total", (string?)blanks["inputLabel"]);
    }

    [Fact]
    public async Task Question_text_is_html_encoded()
    {
        var project = TestData.Project();
        project.Questions[0].Prompt = "Is <b>x</b> & y \"safe\"?";
        project.Questions[0].Options[0].Text = "<script>alert(1)</script>";
        var content = await ExportContentAsync(new H5PExporter(), project);
        var mc = content["interactiveVideo"]!["assets"]!["interactions"]![0]!["action"]!["params"]!;

        Assert.Equal("<p>Is &lt;b&gt;x&lt;/b&gt; &amp; y &quot;safe&quot;?</p>", (string?)mc["question"]);
        Assert.Equal("<div>&lt;script&gt;alert(1)&lt;/script&gt;</div>", (string?)mc["answers"]![0]!["text"]);
    }

    [Fact]
    public async Task Hidden_correct_answers_keep_explanation_for_full_marks_only()
    {
        var project = TestData.Project();
        project.Quiz.ShowCorrectAnswers = false;
        project.Quiz.AttemptsAllowed = 1;
        var content = await ExportContentAsync(new H5PExporter(), project);
        var mc = content["interactiveVideo"]!["assets"]!["interactions"]![0]!["action"]!["params"]!;

        var ranges = mc["overallFeedback"]!.AsArray();
        Assert.Equal(2, ranges.Count);
        Assert.Null(ranges[0]!["feedback"]);
        Assert.Equal(100, (int)ranges[1]!["from"]!);
        Assert.False((bool)mc["behaviour"]!["enableSolutionsButton"]!);
        Assert.False((bool)mc["behaviour"]!["enableRetry"]!);
        Assert.Equal("off", (string?)content["override"]!["showSolutionButton"]);
        Assert.Equal("off", (string?)content["override"]!["retryButton"]);
    }

    [Fact]
    public async Task Retry_follows_each_questions_attempts()
    {
        var project = TestData.Project();
        project.Quiz.AttemptsAllowed = 3;
        project.Questions[1].AttemptsAllowed = 1;
        project.Questions[2].AttemptsAllowed = AttemptRules.Unlimited;
        var content = await ExportContentAsync(new H5PExporter(), project);
        var retries = content["interactiveVideo"]!["assets"]!["interactions"]!.AsArray()
            .Select(i => (bool)i!["action"]!["params"]!["behaviour"]!["enableRetry"]!).ToList();

        Assert.Equal([true, false, true, true], retries);
        // Mixed settings: no IV-wide override, so each question's own setting applies.
        Assert.False(content["override"]!.AsObject().ContainsKey("retryButton"));
    }

    [Fact]
    public async Task Retry_override_is_on_when_every_question_allows_retries()
    {
        var content = await ExportContentAsync(new H5PExporter(), TestData.Project());
        Assert.Equal("on", (string?)content["override"]!["retryButton"]);
    }

    [Fact]
    public void Warns_about_attempt_limits_and_reduced_credit_h5p_cannot_honor()
    {
        var project = TestData.Project();
        Assert.Empty(H5PExporter.Warnings(project));

        project.Quiz.AttemptsAllowed = 1;
        project.Quiz.RetryScoring = RetryScoring.ReducedCredit;
        Assert.Empty(H5PExporter.Warnings(project));

        project.Quiz.AttemptsAllowed = 3;
        project.Quiz.RetryScoring = RetryScoring.FullCredit;
        var warning = Assert.Single(H5PExporter.Warnings(project));
        Assert.Contains("can't limit the number of attempts", warning);
        Assert.Contains("every question", warning);

        project.Quiz.AttemptsAllowed = AttemptRules.Unlimited;
        project.Questions[0].AttemptsAllowed = 2;
        project.Questions[3].RetryScoring = RetryScoring.ReducedCredit;
        var warnings = H5PExporter.Warnings(project);
        Assert.Equal(2, warnings.Count);
        Assert.Contains("the question at 0:30 ", warnings[0]);
        Assert.Contains("can't reduce credit", warnings[1]);
        Assert.Contains("the question at 5:00 ", warnings[1]);
    }

    [Fact]
    public async Task Questions_at_the_same_time_are_spread_apart()
    {
        var project = TestData.Project();
        project.Questions[1].Timestamp = project.Questions[0].Timestamp;
        var content = await ExportContentAsync(new H5PExporter(), project);
        var froms = content["interactiveVideo"]!["assets"]!["interactions"]!.AsArray()
            .Select(i => (double)i!["duration"]!["from"]!).ToList();
        Assert.Equal(30, froms[0]);
        Assert.Equal(31, froms[1]);
    }

    [Theory]
    [InlineData("The capital of France is ___.", new[] { "Paris" }, "<p>The capital of France is *Paris*.</p>")]
    [InlineData("_____ is the capital.", new[] { "Paris", "paris" }, "<p>*Paris* is the capital.</p>")]
    [InlineData("Half is ___", new[] { "1/2", "0.5" }, "<p>Half is *1&#47;2/0.5*</p>")]
    [InlineData("Time: ___ (a*b)", new[] { "3:00", "a*b" }, "<p>Time: *3&#58;00/a&#42;b* (a&#42;b)</p>")]
    [InlineData("Tag ___", new[] { "<b>", "  two   words " }, "<p>Tag *&lt;b&gt;/two words*</p>")]
    [InlineData("A ___ B", new[] { "x", " ", "" }, "<p>A *x* B</p>")]
    public void Blanks_conversion(string prompt, string[] answers, string expected)
    {
        Assert.Equal(expected, H5PContentBuilder.ToClozeText(prompt, answers));
    }

    [Fact]
    public async Task Reference_package_overrides_versions_and_supplies_libraries()
    {
        var referencePath = Path.Combine(Path.GetTempPath(), $"reference-{Guid.NewGuid():N}.h5p");
        try
        {
            using (var file = File.Create(referencePath))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Add(zip, "h5p.json", """
                    {"title":"Theirs","mainLibrary":"H5P.InteractiveVideo","language":"und","embedTypes":["iframe"],
                     "preloadedDependencies":[
                       {"machineName":"H5P.InteractiveVideo","majorVersion":"1","minorVersion":"27"},
                       {"machineName":"H5P.MultiChoice","majorVersion":1,"minorVersion":17}]}
                    """);
                Add(zip, "content/content.json", """{"secret":"their content"}""");
                Add(zip, "content/images/photo.jpg", "jpg");
                Add(zip, "H5P.InteractiveVideo-1.27/library.json", """{"machineName":"H5P.InteractiveVideo","majorVersion":1,"minorVersion":27}""");
                Add(zip, "H5P.InteractiveVideo-1.27/dist/h5p-interactive-video.js", "/* iv */");
                Add(zip, "H5P.TrueFalse-1.9/library.json", """{"machineName":"H5P.TrueFalse","majorVersion":1,"minorVersion":9}""");
                Add(zip, "H5P.TrueFalse-1.9/scripts/h5p-true-false.js", "/* tf */");
                Add(zip, "readme.txt", "loose top-level file");
            }

            var exporter = new H5PExporter(new H5PExportOptions { ReferencePackagePath = referencePath });
            Assert.Empty(exporter.Validate(TestData.Project()));
            using var output = TestData.OpenZip(await TestData.ExportAsync(exporter, TestData.Project()));

            var names = output.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Contains("H5P.InteractiveVideo-1.27/library.json", names);
            Assert.Contains("H5P.InteractiveVideo-1.27/dist/h5p-interactive-video.js", names);
            Assert.Contains("H5P.TrueFalse-1.9/scripts/h5p-true-false.js", names);
            Assert.DoesNotContain("content/images/photo.jpg", names);
            Assert.DoesNotContain("readme.txt", names);
            Assert.Equal("/* tf */", TestData.ReadText(output, "H5P.TrueFalse-1.9/scripts/h5p-true-false.js"));

            // Our content and h5p.json, not theirs.
            Assert.DoesNotContain("their content", TestData.ReadText(output, "content/content.json"));
            var dependencies = Dependencies(JsonNode.Parse(TestData.ReadText(output, "h5p.json"))!);
            Assert.Equal("1.27", dependencies["H5P.InteractiveVideo"]); // from h5p.json (string versions)
            Assert.Equal("1.17", dependencies["H5P.MultiChoice"]);      // from h5p.json (numeric versions)
            Assert.Equal("1.9", dependencies["H5P.TrueFalse"]);         // from a bundled library.json
            Assert.Equal("1.14", dependencies["H5P.Blanks"]);           // not in the reference: default

            var content = JsonNode.Parse(TestData.ReadText(output, "content/content.json"))!;
            var libraries = content["interactiveVideo"]!["assets"]!["interactions"]!.AsArray()
                .Select(i => (string?)i!["action"]!["library"]).ToList();
            Assert.Equal(["H5P.MultiChoice 1.17", "H5P.MultiChoice 1.17", "H5P.TrueFalse 1.9", "H5P.Blanks 1.14"], libraries);
        }
        finally
        {
            File.Delete(referencePath);
        }
    }

    [Fact]
    public async Task Configured_versions_are_used_without_reference()
    {
        var versions = new Dictionary<string, H5PLibraryVersion> { ["H5P.Blanks"] = new(1, 12) };
        var exporter = new H5PExporter(new H5PExportOptions { LibraryVersions = versions });
        using var zip = TestData.OpenZip(await TestData.ExportAsync(exporter, TestData.Project()));
        var dependencies = Dependencies(JsonNode.Parse(TestData.ReadText(zip, "h5p.json"))!);
        Assert.Equal("1.12", dependencies["H5P.Blanks"]);
        Assert.Equal("1.28", dependencies["H5P.InteractiveVideo"]);
    }

    [Fact]
    public async Task Reference_without_h5p_json_is_rejected()
    {
        var referencePath = Path.Combine(Path.GetTempPath(), $"reference-{Guid.NewGuid():N}.h5p");
        try
        {
            using (var file = File.Create(referencePath))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                Add(zip, "something.txt", "not h5p");

            var exporter = new H5PExporter(new H5PExportOptions { ReferencePackagePath = referencePath });
            await Assert.ThrowsAsync<InvalidDataException>(() => TestData.ExportAsync(exporter, TestData.Project()));
        }
        finally
        {
            File.Delete(referencePath);
        }
    }

    private static async Task<JsonNode> ExportContentAsync(H5PExporter exporter, LectureProject project)
    {
        using var zip = TestData.OpenZip(await TestData.ExportAsync(exporter, project));
        return JsonNode.Parse(TestData.ReadText(zip, "content/content.json"))!;
    }

    private static Dictionary<string, string> Dependencies(JsonNode package) =>
        package["preloadedDependencies"]!.AsArray().ToDictionary(
            d => (string)d!["machineName"]!,
            d => $"{d!["majorVersion"]}.{d["minorVersion"]}");

    private static void Add(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
