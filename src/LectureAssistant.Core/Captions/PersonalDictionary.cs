using System.Text.Json;
using System.Text.Json.Serialization;
using LectureAssistant.Core.Models;
using LectureAssistant.Core.Persistence;

namespace LectureAssistant.Core.Captions;

/// <summary>A correctly spelled term, optionally with the wrong spelling speech recognition tends to produce for it.</summary>
public sealed class DictionaryEntry
{
    /// <summary>The correct spelling, e.g. "Kubernetes" or "Dr. Okonkwo".</summary>
    public string Term { get; set; } = "";

    /// <summary>What the captions said instead, e.g. "cooper netties"; null for a term on its own.</summary>
    public string? Heard { get; set; }

    /// <summary>When the entry was added, applied, or last appeared in a lecture. Recent entries win when space is short.</summary>
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public bool IsCorrection => !string.IsNullOrWhiteSpace(Heard);
}

/// <summary>
/// The instructor's own spellings, kept across every lecture. Terms prime speech recognition
/// (see <see cref="RecognitionPrompt"/>); corrections are applied to new captions automatically.
/// </summary>
public sealed class PersonalDictionary
{
    public List<DictionaryEntry> Entries { get; set; } = [];

    public IEnumerable<DictionaryEntry> Corrections => Entries.Where(e => e.IsCorrection);

    /// <summary>Adds a correct spelling (or refreshes an existing one). Returns null for blank input.</summary>
    public DictionaryEntry? AddTerm(string term, DateTimeOffset? now = null)
    {
        term = CaptionCorrections.NormalizeSpaces(term);
        if (term.Length == 0) return null;

        var entry = Entries.FirstOrDefault(e => !e.IsCorrection && Same(e.Term, term));
        if (entry is null) Entries.Add(entry = new DictionaryEntry());
        entry.Term = term;
        entry.LastUsed = now ?? DateTimeOffset.Now;
        return entry;
    }

    /// <summary>Remembers that <paramref name="heard"/> should read <paramref name="term"/>; replaces an earlier fix for the same words.</summary>
    public DictionaryEntry? AddCorrection(string heard, string term, DateTimeOffset? now = null)
    {
        heard = CaptionCorrections.NormalizeSpaces(heard);
        term = CaptionCorrections.NormalizeSpaces(term);
        if (heard.Length == 0 || term.Length == 0) return null;
        if (heard == term) return AddTerm(term, now);

        var entry = Entries.FirstOrDefault(e => e.IsCorrection && Same(e.Heard!, heard));
        if (entry is null) Entries.Add(entry = new DictionaryEntry { Heard = heard });
        entry.Term = term;
        entry.LastUsed = now ?? DateTimeOffset.Now;
        return entry;
    }

    public bool Remove(DictionaryEntry entry) => Entries.Remove(entry);

    /// <summary>Distinct correct spellings (including correction targets), most recently used first.</summary>
    public IReadOnlyList<string> TermsByRecency() =>
        Entries.OrderByDescending(e => e.LastUsed)
            .Select(e => e.Term)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .DistinctBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// True when <paramref name="word"/> is spelled like one of the dictionary's correct terms, or is a whole
    /// word within one ("Okonkwo" in "Dr. Okonkwo"), ignoring case.
    /// </summary>
    public bool IsKnownTerm(string word)
    {
        word = CaptionCorrections.NormalizeSpaces(word);
        if (word.Length == 0) return false;
        var pattern = CaptionCorrections.Pattern(word)!;
        return Entries.Any(e => Same(e.Term, word) || pattern.IsMatch(e.Term));
    }

    /// <summary>
    /// Applies every remembered correction to the captions, then marks entries that showed up as used
    /// so they stay near the front of the recognition prompt. Returns how many words were fixed.
    /// </summary>
    public int ApplyTo(IReadOnlyList<CaptionSegment> captions, DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.Now;
        int total = 0;

        // Longer phrases first, so "cooper netties" is fixed before a shorter "netties" could split it.
        foreach (var entry in Corrections.OrderByDescending(e => e.Heard!.Length).ToList())
        {
            int changed = CaptionCorrections.Apply(captions, entry.Heard!, entry.Term);
            if (changed > 0) entry.LastUsed = time;
            total += changed;
        }

        foreach (var entry in Entries.Where(e => !e.IsCorrection))
        {
            if (CaptionCorrections.Pattern(entry.Term) is { } pattern && captions.Any(c => pattern.IsMatch(c.Text)))
                entry.LastUsed = time;
        }
        return total;
    }

    public static PersonalDictionary Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<PersonalDictionary>(File.ReadAllText(path), ProjectStore.JsonOptions) ?? new();
        }
        catch (JsonException) { }
        return new PersonalDictionary();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, ProjectStore.JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
