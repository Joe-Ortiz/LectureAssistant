using System.Text.Json;
using System.Xml.Linq;
using LectureAssistant.Core.Models;
using LectureAssistant.Export.Scorm;

namespace LectureAssistant.Export.Tests;

public class ScormExporterTests
{
    private static readonly XNamespace Cp = "http://www.imsproject.org/xsd/imscp_rootv1p1p2";
    private static readonly XNamespace Adl = "http://www.adlnet.org/xsd/adlcp_rootv1p2";

    [Fact]
    public async Task Zip_contains_manifest_player_and_data()
    {
        using var zip = TestData.OpenZip(await TestData.ExportAsync(new ScormExporter(), TestData.Project()));

        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Equal(
            new HashSet<string> { "imsmanifest.xml", "index.html", "player.css", "player.js", "lecture-data.js" },
            names);
        Assert.All(zip.Entries, e => Assert.True(e.Length > 0, $"{e.FullName} is empty"));
    }

    [Fact]
    public async Task Manifest_is_scorm_12_and_lists_every_file()
    {
        var project = TestData.Project();
        project.Title = "Heat & <Work> \"quoted\"";
        using var zip = TestData.OpenZip(await TestData.ExportAsync(new ScormExporter(), project));

        var manifest = XDocument.Parse(TestData.ReadText(zip, "imsmanifest.xml"));
        var root = manifest.Root!;
        Assert.Equal(Cp + "manifest", root.Name);
        Assert.Equal("1.2", root.Element(Cp + "metadata")!.Element(Cp + "schemaversion")!.Value);
        Assert.Equal("ADL SCORM", root.Element(Cp + "metadata")!.Element(Cp + "schema")!.Value);
        Assert.Contains("adlcp_rootv1p2.xsd", (string)root.Attribute(XName.Get("schemaLocation", "http://www.w3.org/2001/XMLSchema-instance"))!);
        Assert.Contains(project.Id, (string)root.Attribute("identifier")!);

        var organization = root.Element(Cp + "organizations")!.Element(Cp + "organization")!;
        Assert.Equal(project.Title, organization.Element(Cp + "title")!.Value);
        var item = Assert.Single(organization.Elements(Cp + "item"));

        var resource = Assert.Single(root.Element(Cp + "resources")!.Elements(Cp + "resource"));
        Assert.Equal((string)item.Attribute("identifierref")!, (string)resource.Attribute("identifier")!);
        Assert.Equal("sco", (string)resource.Attribute(Adl + "scormtype")!);
        Assert.Equal("index.html", (string)resource.Attribute("href")!);

        var listed = resource.Elements(Cp + "file").Select(f => (string)f.Attribute("href")!).ToHashSet();
        var packaged = zip.Entries.Select(e => e.FullName).Where(n => n != "imsmanifest.xml").ToHashSet();
        Assert.Equal(packaged, listed);
    }

    [Fact]
    public async Task Manifest_identifiers_are_stable_across_exports()
    {
        var project = TestData.Project();
        using var first = TestData.OpenZip(await TestData.ExportAsync(new ScormExporter(), project));
        using var second = TestData.OpenZip(await TestData.ExportAsync(new ScormExporter(), project));
        Assert.Equal(TestData.ReadText(first, "imsmanifest.xml"), TestData.ReadText(second, "imsmanifest.xml"));
    }

    [Fact]
    public async Task Index_html_loads_data_before_player()
    {
        using var zip = TestData.OpenZip(await TestData.ExportAsync(new ScormExporter(), TestData.Project()));
        var html = TestData.ReadText(zip, "index.html");
        var data = html.IndexOf("src=\"lecture-data.js\"", StringComparison.Ordinal);
        var player = html.IndexOf("src=\"player.js\"", StringComparison.Ordinal);
        Assert.True(data >= 0 && player > data);
        Assert.Contains("href=\"player.css\"", html);
    }

    [Fact]
    public async Task Lecture_data_round_trips()
    {
        var project = TestData.Project();
        using var zip = TestData.OpenZip(await TestData.ExportAsync(new ScormExporter(), project));
        using var json = ParseLectureData(TestData.ReadText(zip, "lecture-data.js"));
        var root = json.RootElement;

        Assert.Equal(project.Title, root.GetProperty("title").GetString());
        Assert.Equal(TestData.VideoId, root.GetProperty("videoId").GetString());

        var settings = root.GetProperty("settings");
        Assert.Equal(70, settings.GetProperty("passingScorePercent").GetDouble());
        Assert.True(settings.GetProperty("preventSkippingAhead").GetBoolean());
        Assert.False(settings.TryGetProperty("allowRetry", out _));
        Assert.True(settings.GetProperty("showCorrectAnswers").GetBoolean());

        var questions = root.GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal(project.Questions.Count, questions.Count);

        var mc = questions[0];
        Assert.Equal(project.Questions[0].Id, mc.GetProperty("id").GetString());
        Assert.Equal(30, mc.GetProperty("time").GetDouble());
        Assert.Equal("MultipleChoice", mc.GetProperty("type").GetString());
        Assert.Equal(2, mc.GetProperty("points").GetInt32());
        Assert.Equal("The first law of thermodynamics.", mc.GetProperty("explanation").GetString());
        var options = mc.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(3, options.Count);
        Assert.True(options[0].GetProperty("correct").GetBoolean());
        Assert.Equal("Right: the first law.", options[0].GetProperty("feedback").GetString());

        Assert.Equal(95.5, questions[1].GetProperty("time").GetDouble());
        Assert.False(questions[2].GetProperty("correctAnswer").GetBoolean());
        Assert.Equal(["joule", "J"], questions[3].GetProperty("acceptedAnswers").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task Lecture_data_has_each_questions_effective_attempt_rules()
    {
        var project = TestData.Project();
        project.Quiz.AttemptsAllowed = 3;
        project.Quiz.RetryScoring = RetryScoring.ReducedCredit;
        project.Quiz.RetryPenaltyPercent = 25;
        project.Questions[1].AttemptsAllowed = 1;
        project.Questions[2].RetryScoring = RetryScoring.FullCredit;
        project.Questions[3].AttemptsAllowed = AttemptRules.Unlimited;
        project.Questions[3].RetryScoring = RetryScoring.FirstAttemptOnly;

        using var zip = TestData.OpenZip(await TestData.ExportAsync(new ScormExporter(), project));
        using var json = ParseLectureData(TestData.ReadText(zip, "lecture-data.js"));
        var questions = json.RootElement.GetProperty("questions").EnumerateArray()
            .Select(q => (
                q.GetProperty("attemptsAllowed").GetInt32(),
                q.GetProperty("retryScoring").GetString(),
                q.GetProperty("retryPenaltyPercent").GetInt32()))
            .ToList();

        Assert.Equal(
            [(3, "ReducedCredit", 25), (1, "ReducedCredit", 25), (3, "FullCredit", 25), (0, "FirstAttemptOnly", 25)],
            questions);
    }

    [Fact]
    public async Task Player_credit_math_matches_core()
    {
        // player.js can't run in these tests, so check it still has the same rules as AttemptRules.CreditPercent.
        using var zip = TestData.OpenZip(await TestData.ExportAsync(new ScormExporter(), TestData.Project()));
        var player = TestData.ReadText(zip, "player.js");
        Assert.Contains("if (attempt < 1 || (q.attemptsAllowed > 0 && attempt > q.attemptsAllowed)) return 0;", player);
        Assert.Contains("if (q.retryScoring === 'FullCredit') return 100;", player);
        Assert.Contains("return Math.max(0, 100 - q.retryPenaltyPercent * (attempt - 1));", player);
    }

    [Fact]
    public async Task Lecture_data_is_sorted_by_timestamp()
    {
        var project = TestData.Project();
        project.Questions.Reverse();
        using var zip = TestData.OpenZip(await TestData.ExportAsync(new ScormExporter(), project));
        using var json = ParseLectureData(TestData.ReadText(zip, "lecture-data.js"));
        var times = json.RootElement.GetProperty("questions").EnumerateArray().Select(q => q.GetProperty("time").GetDouble()).ToList();
        Assert.Equal(times.Order(), times);
    }

    [Fact]
    public async Task Lecture_data_is_safe_inside_a_script_element()
    {
        var lineSeparator = ((char)0x2028).ToString();
        var paragraphSeparator = ((char)0x2029).ToString();
        var hostile = "</script><img src=x onerror=alert(1)> & 'quote' " + lineSeparator + "line" + paragraphSeparator + "para <!-- -->";
        var project = TestData.Project();
        project.Title = hostile;
        project.Questions[0].Prompt = hostile;
        project.Questions[0].Options[1].Text = hostile;
        project.Questions[3].AcceptedAnswers = [hostile];
        project.Questions[3].Explanation = hostile;

        using var zip = TestData.OpenZip(await TestData.ExportAsync(new ScormExporter(), project));
        var script = TestData.ReadText(zip, "lecture-data.js");

        foreach (var forbidden in new[] { "<", ">", "&", lineSeparator, paragraphSeparator })
            Assert.DoesNotContain(forbidden, script);

        using var json = ParseLectureData(script);
        var root = json.RootElement;
        Assert.Equal(hostile, root.GetProperty("title").GetString());
        Assert.Equal(hostile, root.GetProperty("questions")[0].GetProperty("prompt").GetString());
        Assert.Equal(hostile, root.GetProperty("questions")[0].GetProperty("options")[1].GetProperty("text").GetString());
        Assert.Equal(hostile, root.GetProperty("questions")[3].GetProperty("acceptedAnswers")[0].GetString());
    }

    [Fact]
    public async Task Player_script_never_uses_innerHTML()
    {
        using var zip = TestData.OpenZip(await TestData.ExportAsync(new ScormExporter(), TestData.Project()));
        var player = TestData.ReadText(zip, "player.js");
        Assert.DoesNotContain("innerHTML", player);
        Assert.DoesNotContain("insertAdjacentHTML", player);
        Assert.Contains("https://www.youtube.com/iframe_api", player);
        Assert.Contains("youtube-nocookie.com", player);
    }

    [Fact]
    public async Task Export_refuses_invalid_project()
    {
        var project = TestData.Project();
        project.YouTubeUrl = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestData.ExportAsync(new ScormExporter(), project));
    }

    [Fact]
    public async Task Output_stream_is_left_open()
    {
        using var output = new MemoryStream();
        await new ScormExporter().ExportAsync(TestData.Project(), output, CancellationToken.None);
        Assert.True(output.CanWrite);
        Assert.True(output.Length > 0);
    }

    [Fact]
    public void Exporter_metadata()
    {
        var exporter = new ScormExporter();
        Assert.Equal(".zip", exporter.FileExtension);
        Assert.Contains("SCORM", exporter.DisplayName);
    }

    internal static JsonDocument ParseLectureData(string script)
    {
        Assert.StartsWith(LectureDataScript.Prefix, script);
        var json = script[LectureDataScript.Prefix.Length..].TrimEnd().TrimEnd(';');
        return JsonDocument.Parse(json);
    }
}
