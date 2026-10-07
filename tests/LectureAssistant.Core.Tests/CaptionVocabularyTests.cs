using System.Text.Json;
using LectureAssistant.Core.Captions;
using LectureAssistant.Core.Models;
using LectureAssistant.Core.Persistence;

namespace LectureAssistant.Core.Tests;

public class CaptionVocabularyTests
{
    private static CaptionSegment C(string text) => new(TimeSpan.Zero, TimeSpan.FromSeconds(1), text);

    // ---------- Subject areas and prompts ----------

    [Fact]
    public void Subject_prompts_fit_the_budget_and_ids_are_unique()
    {
        Assert.Equal(SubjectAreas.All.Count, SubjectAreas.All.Select(s => s.Id).Distinct().Count());
        Assert.All(SubjectAreas.All, s =>
        {
            Assert.InRange(s.Prompt.Length, 60, 260);
            Assert.False(string.IsNullOrWhiteSpace(s.DisplayName));
        });
    }

    [Fact]
    public void Unknown_subject_falls_back_to_general()
    {
        Assert.Same(SubjectAreas.General, SubjectAreas.Find(null));
        Assert.Same(SubjectAreas.General, SubjectAreas.Find("underwater-basket-weaving"));
        Assert.Equal("Biology", SubjectAreas.Find("biology").DisplayName);
    }

    [Fact]
    public void Prompt_appends_terms_after_the_subject_sentence()
    {
        var subject = SubjectAreas.Find("software");
        var prompt = RecognitionPrompt.Build(subject, ["Dr. Okonkwo", "kubectl"], "en");
        Assert.Equal(subject.Prompt + " Dr. Okonkwo, kubectl.", prompt);
    }

    [Fact]
    public void Prompt_skips_duplicates_and_terms_already_in_the_sentence()
    {
        var subject = SubjectAreas.Find("software");
        var prompt = RecognitionPrompt.Build(subject, ["Kubernetes", "Helm", "helm", "  "], "auto");
        Assert.Equal(subject.Prompt + " Helm.", prompt);
    }

    [Fact]
    public void Prompt_keeps_earlier_terms_when_over_budget()
    {
        var terms = Enumerable.Range(1, 200).Select(i => $"Term{i:000}").ToList();
        var prompt = RecognitionPrompt.Build(SubjectAreas.General, terms, "en")!;
        Assert.True(prompt.Length <= RecognitionPrompt.MaxLength);
        Assert.Contains("Term001, Term002", prompt);
        Assert.DoesNotContain("Term200", prompt);
        Assert.EndsWith(".", prompt);
    }

    [Fact]
    public void Prompt_fills_leftover_room_with_shorter_terms()
    {
        var prompt = RecognitionPrompt.Build(SubjectAreas.General, [new string('x', 40), "Okonkwo"], "en", maxLength: SubjectAreas.General.Prompt.Length + 20)!;
        Assert.EndsWith(" Okonkwo.", prompt);
    }

    [Fact]
    public void Prompt_uses_only_terms_for_other_languages()
    {
        Assert.Equal("Dr. Okonkwo.", RecognitionPrompt.Build(SubjectAreas.Find("medicine"), ["Dr. Okonkwo"], "es"));
        Assert.Null(RecognitionPrompt.Build(SubjectAreas.Find("medicine"), [], "es"));
    }

    // ---------- Corrections ----------

    [Fact]
    public void Replace_matches_whole_words_ignoring_case()
    {
        var text = CaptionCorrections.Replace("The cell and the cellular wall. CELL.", "cell", "nucleus", out int changed);
        Assert.Equal("The nucleus and the cellular wall. Nucleus.", text);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Replace_matches_phrases_across_line_breaks_and_rebalances()
    {
        var text = CaptionCorrections.Replace("we deploy everything with cooper\nnetties on a small cluster", "cooper netties", "Kubernetes", out int changed);
        Assert.Equal(1, changed);
        Assert.Equal("we deploy everything with Kubernetes on a small cluster", text.Replace('\n', ' '));
        Assert.All(text.Split('\n'), line => Assert.True(line.Length <= CaptionCleanup.MaxLineLength));
    }

    [Fact]
    public void Replace_keeps_the_given_casing_but_capitalizes_sentence_starts()
    {
        Assert.Equal("Use kubectl now. Kubectl is the CLI.",
            CaptionCorrections.Replace("Use cube control now. Cube control is the CLI.", "cube control", "kubectl", out _));
        Assert.Equal("ask Dr. Okonkwo", CaptionCorrections.Replace("ask doctor oconco", "doctor oconco", "Dr. Okonkwo", out _));
    }

    [Fact]
    public void Replace_handles_terms_with_punctuation_and_skips_inside_words()
    {
        Assert.Equal("We use C++ here", CaptionCorrections.Replace("We use c++ here", "c++", "C++", out int changed));
        Assert.Equal(1, changed);
        Assert.Equal("don't", CaptionCorrections.Replace("don't", "t", "x", out changed));
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Replace_reports_nothing_when_already_correct()
    {
        CaptionCorrections.Replace("Kubernetes rocks", "Kubernetes", "Kubernetes", out int changed);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Count_spans_all_captions()
    {
        List<CaptionSegment> captions = [C("Python is fun."), C("I like python\nand Pythonic code.")];
        Assert.Equal(2, CaptionCorrections.Count(captions, "python"));
    }

    // ---------- Personal dictionary ----------

    [Fact]
    public void Dictionary_dedupes_terms_and_corrections()
    {
        var dictionary = new PersonalDictionary();
        dictionary.AddTerm("kubernetes");
        dictionary.AddTerm("Kubernetes");
        dictionary.AddCorrection("cooper netties", "Kubernetes");
        dictionary.AddCorrection("Cooper  Netties", "Kubernetes!");
        Assert.Null(dictionary.AddTerm("   "));

        Assert.Equal(2, dictionary.Entries.Count);
        Assert.Equal("Kubernetes", dictionary.Entries[0].Term);
        Assert.Equal("Kubernetes!", Assert.Single(dictionary.Corrections).Term);
    }

    [Fact]
    public void Terms_are_ordered_by_most_recent_use()
    {
        var dictionary = new PersonalDictionary();
        var start = DateTimeOffset.Now;
        dictionary.AddTerm("Old", start.AddDays(-10));
        dictionary.AddCorrection("new ish", "Newish", start.AddDays(-1));
        dictionary.AddTerm("Newest", start);
        Assert.Equal(["Newest", "Newish", "Old"], dictionary.TermsByRecency());
    }

    [Fact]
    public void Known_terms_include_words_within_multiword_terms()
    {
        var dictionary = new PersonalDictionary();
        dictionary.AddTerm("Dr. Okonkwo");
        dictionary.AddCorrection("cooper netties", "Kubernetes");
        Assert.True(dictionary.IsKnownTerm("okonkwo"));
        Assert.True(dictionary.IsKnownTerm("Kubernetes"));
        Assert.False(dictionary.IsKnownTerm("cooper"));
        Assert.False(dictionary.IsKnownTerm("Okon"));
    }

    [Fact]
    public void Dictionary_applies_corrections_and_marks_entries_used()
    {
        var old = DateTimeOffset.Now.AddDays(-30);
        var now = DateTimeOffset.Now;
        var dictionary = new PersonalDictionary();
        var fix = dictionary.AddCorrection("cooper netties", "Kubernetes", old)!;
        var term = dictionary.AddTerm("Helm", old)!;
        var unused = dictionary.AddTerm("Terraform", old)!;

        List<CaptionSegment> captions = [C("Cooper netties runs containers."), C("Install it with Helm on cooper\nnetties.")];
        Assert.Equal(2, dictionary.ApplyTo(captions, now));
        Assert.Equal("Kubernetes runs containers.", captions[0].Text);
        Assert.Equal("Install it with Helm on Kubernetes.", captions[1].Text);
        Assert.Equal(now, fix.LastUsed);
        Assert.Equal(now, term.LastUsed);
        Assert.Equal(old, unused.LastUsed);
    }

    [Fact]
    public void Dictionary_round_trips_and_tolerates_bad_files()
    {
        var path = Path.Combine(Path.GetTempPath(), "la-dict-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var dictionary = new PersonalDictionary();
            dictionary.AddTerm("Dr. Okonkwo");
            dictionary.AddCorrection("cooper netties", "Kubernetes");
            dictionary.Save(path);

            var loaded = PersonalDictionary.Load(path);
            Assert.Equal(2, loaded.Entries.Count);
            Assert.Equal("cooper netties", loaded.Corrections.Single().Heard);

            File.WriteAllText(path, "{ not json");
            Assert.Empty(PersonalDictionary.Load(path).Entries);
            Assert.Empty(PersonalDictionary.Load(path + ".missing").Entries);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------- Words to check ----------

    [Fact]
    public void Unsure_words_come_from_low_probability_tokens()
    {
        (string, float)[] tokens =
        [
            ("[_BEG_]", 1f), (" We", 0.99f), (" deploy", 0.95f), (" with", 0.97f), (" the", 0.4f), (" co", 0.3f), ("oper", 0.8f),
            (" net", 0.2f), ("ties", 0.6f), (",", 0.9f), (" and", 0.3f), (" Hel", 0.4f), ("m", 0.9f), (".", 0.99f), ("[_TT_150]", 0.1f),
        ];
        var words = WordsToCheck.FindUnsure(tokens, "We deploy with the cooper netties, and Helm.");
        Assert.Equal(["cooper netties", "Helm"], words);
    }

    [Fact]
    public void Unsure_words_missing_from_the_text_are_dropped()
    {
        Assert.Empty(WordsToCheck.FindUnsure([(" caf�", 0.1f), (" zzz", 0.1f)], "Coffee time."));
    }

    [Fact]
    public void Normalize_keeps_unsure_words_with_the_piece_they_landed_in()
    {
        var long1 = "We will set up cooper netties first and then talk about the control plane. After that, Helm charts make installs repeatable.";
        var segments = CaptionCleanup.Normalize([new CaptionSegment(TimeSpan.Zero, TimeSpan.FromSeconds(10), long1) { UncertainWords = ["cooper netties", "Helm"] }]);
        Assert.True(segments.Count > 1);
        Assert.Equal(["cooper netties"], segments[0].UncertainWords);
        Assert.Equal(["Helm"], segments[^1].UncertainWords);
    }

    [Fact]
    public void Candidates_put_unsure_words_first_and_skip_known_and_common_words()
    {
        List<CaptionSegment> captions =
        [
            new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Today we meet Professor Okonkwo and cooper netties.") { UncertainWords = ["cooper netties", "the"] },
            C("Then we install Helm, and Okonkwo explains Helm again."),
            C("Python is great, says Okonkwo. And Monday we use Python."),
            C("The NASA team visits."),
        ];

        var candidates = WordsToCheck.Find(captions, w => w.Equals("helm", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(new WordToCheck("cooper netties", 1, true), candidates[0]);
        Assert.Equal(new WordToCheck("Okonkwo", 3, false), candidates[1]);
        Assert.Contains(candidates, c => c.Word == "Professor");
        Assert.Contains(candidates, c => c.Word == "Python" && c.Count == 2);
        Assert.Contains(candidates, c => c.Word == "NASA");
        Assert.DoesNotContain(candidates, c => c.Word is "Helm" or "Monday" or "Today" or "Then" or "the" or "The");
    }

    [Fact]
    public void Candidates_respect_the_limit()
    {
        var captions = Enumerable.Range(0, 40).Select(i => C($"we met Name{(char)('a' + i % 26)}{i} today")).ToList();
        Assert.Equal(10, WordsToCheck.Find(captions, _ => false, max: 10).Count);
    }

    // ---------- Project JSON ----------

    [Fact]
    public void Old_projects_load_and_new_fields_round_trip()
    {
        var old = JsonSerializer.Deserialize<LectureProject>(
            """{"title":"Old","captions":[{"start":"00:00:01","end":"00:00:02","text":"Hi"}]}""", ProjectStore.JsonOptions)!;
        Assert.Null(old.SubjectArea);
        Assert.Null(old.Captions[0].UncertainWords);

        var json = JsonSerializer.Serialize(old, ProjectStore.JsonOptions);
        Assert.DoesNotContain("uncertainWords", json);

        old.SubjectArea = "biology";
        old.Captions[0].UncertainWords = ["Hi"];
        var loaded = JsonSerializer.Deserialize<LectureProject>(JsonSerializer.Serialize(old, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        Assert.Equal("biology", loaded.SubjectArea);
        Assert.Equal(["Hi"], loaded.Captions[0].UncertainWords);
    }
}
