using System.Text;
using System.Text.RegularExpressions;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Core.Captions;

/// <param name="Word">Word or short phrase as it appears in the captions.</param>
/// <param name="Count">Occurrences across all captions.</param>
/// <param name="Unsure">True when speech recognition reported low confidence; false when it was picked by spelling alone.</param>
public sealed record WordToCheck(string Word, int Count, bool Unsure);

/// <summary>Picks out words in a transcript that are worth a second look: the ones most likely to be misheard names and terms.</summary>
public static partial class WordsToCheck
{
    /// <summary>A word whose least-confident piece is below this probability counts as unsure.</summary>
    public const float UnsureBelow = 0.5f;

    private const int MaxPhraseWords = 3;

    /// <summary>
    /// Groups Whisper's tokens into words and returns the words (or runs of up to three adjacent words)
    /// that recognition wasn't sure about, as they appear in <paramref name="segmentText"/>.
    /// Common words and anything not found in the text are left out.
    /// </summary>
    public static List<string> FindUnsure(IEnumerable<(string Text, float Probability)> tokens, string segmentText, float threshold = UnsureBelow)
    {
        var words = new List<(string Text, float Probability)>();
        var current = new StringBuilder();
        float lowest = 1;

        void Flush()
        {
            if (current.Length > 0) words.Add((current.ToString().Trim(), lowest));
            current.Clear();
            lowest = 1;
        }

        foreach (var (text, probability) in tokens)
        {
            // Timestamps and control tokens ([_BEG_], [_TT_150], <|endoftext|>) aren't speech.
            if (text.Length == 0 || IsSpecialToken(text)) { Flush(); continue; }
            if (char.IsWhiteSpace(text[0])) Flush();
            current.Append(text);
            if (text.Any(char.IsLetterOrDigit)) lowest = Math.Min(lowest, probability);
        }
        Flush();

        var result = new List<string>();
        var run = new List<string>();

        void EndRun()
        {
            // Trim common words from the ends: "the cooper netties" → "cooper netties".
            int start = 0, end = run.Count;
            while (start < end && IsCommon(run[start])) start++;
            while (end > start && IsCommon(run[end - 1])) end--;
            if (end > start)
            {
                var phrase = string.Join(' ', run.Skip(start).Take(end - start));
                if (!result.Contains(phrase, StringComparer.OrdinalIgnoreCase) && CaptionCorrections.Count(segmentText, phrase) > 0)
                    result.Add(phrase);
            }
            run.Clear();
        }

        foreach (var (text, probability) in words)
        {
            var word = TrimPunctuation(text);
            if (word.Length == 0 || word.Contains('�') || probability >= threshold)
            {
                EndRun();
                continue;
            }
            if (run.Count == MaxPhraseWords) EndRun();
            run.Add(word);

            // Punctuation after a word ends the phrase: "netties, and" isn't one term.
            if (text.Length > 0 && !char.IsLetterOrDigit(text[^1])) EndRun();
        }
        EndRun();
        return result;
    }

    /// <summary>
    /// Words worth checking, most likely mistakes first: words recognition was unsure about, then names and
    /// terms (capitalized mid-sentence, not everyday words). Words <paramref name="isKnown"/> accepts are skipped.
    /// </summary>
    public static IReadOnlyList<WordToCheck> Find(IReadOnlyList<CaptionSegment> captions, Func<string, bool> isKnown, int max = 25)
    {
        var unsure = new List<string>();
        foreach (var caption in captions)
        {
            foreach (var word in caption.UncertainWords ?? [])
                if (!unsure.Contains(word, StringComparer.OrdinalIgnoreCase)) unsure.Add(word);
        }

        var results = new List<WordToCheck>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(IEnumerable<string> words, bool isUnsure)
        {
            var found = new List<WordToCheck>();
            foreach (var word in words)
            {
                // Counting is the slow part on long lectures; once there are enough, stop looking.
                if (results.Count + found.Count >= max * 4) break;
                if (!seen.Add(word) || isKnown(word)) continue;
                if (word.Split(' ').All(IsCommon)) continue;
                int count = CaptionCorrections.Count(captions, word);
                if (count > 0) found.Add(new WordToCheck(word, count, isUnsure));
            }
            results.AddRange(found.OrderByDescending(w => w.Count).ThenBy(w => w.Word, StringComparer.OrdinalIgnoreCase));
        }

        Consider(unsure, isUnsure: true);
        if (results.Count < max) Consider(CapitalizedMidSentence(captions), isUnsure: false);
        return results.Take(max).ToList();
    }

    /// <summary>
    /// Capitalized words that don't start a sentence, and all-caps words anywhere, that aren't everyday words:
    /// usually names, terms and acronyms, which is where recognition makes most of its mistakes
    /// ("etcd" comes out as "ECT", "Grafana" as "Grafina").
    /// </summary>
    private static IEnumerable<string> CapitalizedMidSentence(IEnumerable<CaptionSegment> captions)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var firstSeen = new List<string>();
        foreach (var caption in captions)
        {
            // Recognition often capitalizes the first word of a segment even mid-sentence, so don't trust it.
            bool sentenceStart = true;
            int position = 0;
            foreach (Match match in Word().Matches(caption.Text))
            {
                var between = caption.Text[position..match.Index];
                if (between.IndexOfAny(['.', '?', '!', ':', '"', '“']) >= 0) sentenceStart = true;
                position = match.Index + match.Length;

                var word = match.Value;
                bool candidate = word.Length >= 3 && char.IsUpper(word[0]) && !IsCommon(word)
                    && (!sentenceStart || word.All(c => !char.IsLetter(c) || char.IsUpper(c)));
                sentenceStart = false;
                if (!candidate) continue;

                if (counts.TryGetValue(word, out int n)) counts[word] = n + 1;
                else { counts[word] = 1; firstSeen.Add(word); }
            }
        }
        return firstSeen.OrderByDescending(w => counts[w]);
    }

    private static bool IsSpecialToken(string text) =>
        (text.StartsWith("[_", StringComparison.Ordinal) && text.EndsWith(']')) ||
        (text.StartsWith("<|", StringComparison.Ordinal) && text.EndsWith("|>", StringComparison.Ordinal));

    private static string TrimPunctuation(string word)
    {
        int start = 0, end = word.Length;
        while (start < end && !char.IsLetterOrDigit(word[start])) start++;
        while (end > start && !char.IsLetterOrDigit(word[end - 1])) end--;
        return word[start..end];
    }

    internal static bool IsCommon(string word)
    {
        var lower = word.ToLowerInvariant().Replace('’', '\'');
        if (CommonWords.Contains(lower)) return true;
        // Possessives and simple contractions of common words: "teacher's", "it's".
        int apostrophe = lower.IndexOf('\'');
        return apostrophe > 0 && CommonWords.Contains(lower[..apostrophe]);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*")]
    private static partial Regex Word();

    /// <summary>
    /// Everyday English words (plus fillers and calendar words) that are almost never misheard terms.
    /// Deliberately small: it only filters candidates, it isn't a spelling dictionary.
    /// </summary>
    private static readonly HashSet<string> CommonWords = new(
        """
        a about above across after again against ago all almost along already also although always am among an and another any anybody
        anyone anything anyway are aren't around as ask asked at away back bad be became because become been before began begin being
        below best better between big bit both bring but by call called came can can't cannot case change class come comes coming could
        couldn't course day days did didn't different do does doesn't doing don't done down during each early easy either else end enough
        even ever every everybody everyone everything example except fact far few find first five follow following for found four free
        from full gave get gets getting give given go goes going gone good got great group had hand happen happens hard has hasn't have
        haven't having he he's head hear help her here here's high him his hold home how however i i'd i'll i'm i've idea if important
        in inside instead into is isn't it it's its just keep kind know known last later least left less let let's life like line little
        long look looking lot lots made make makes making many may maybe me mean means might mind minute minutes more most move much must
        my myself name need never new next nice no none nor not nothing now number of off often oh ok okay old on once one only open or
        order other others our out over own page part people per perhaps place point problem put question questions quite rather really
        reason right said same saw say saying says second see seem seems seen set several she should show side simple since slide small
        so some someone something sometimes soon sorry start started still stuff such sure take talk talking tell than thank thanks that
        that's the their them then there there's these they they're thing things think this those though thought three through time
        times to today together told too took top toward try trying turn two type under understand until up us use used using usually
        very want wanted was wasn't way we we'll we're we've week well went were weren't what what's when where whether which while who
        whole why will with within without won't word words work works would wouldn't yeah year years yes yet you you'll you're you've
        your yourself
        um uh uhm hmm mm mhm ah er erm huh wow alright
        monday tuesday wednesday thursday friday saturday sunday january february march april june july august september october
        november december
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);
}
