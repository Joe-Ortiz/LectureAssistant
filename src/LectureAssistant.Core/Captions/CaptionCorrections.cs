using System.Text.RegularExpressions;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Core.Captions;

/// <summary>
/// Find-and-replace for caption text that matches whole words or phrases, ignores case,
/// and allows a line break wherever the phrase has a space (captions are wrapped onto two lines).
/// </summary>
public static class CaptionCorrections
{
    /// <summary>Number of whole-word, case-insensitive occurrences of <paramref name="phrase"/> in <paramref name="text"/>.</summary>
    public static int Count(string text, string phrase) =>
        Pattern(phrase) is { } pattern ? pattern.Count(text) : 0;

    /// <summary>Occurrences of <paramref name="phrase"/> across all captions.</summary>
    public static int Count(IEnumerable<CaptionSegment> captions, string phrase) =>
        Pattern(phrase) is { } pattern ? captions.Sum(c => pattern.Count(c.Text)) : 0;

    /// <summary>
    /// Replaces every whole-word occurrence of <paramref name="find"/> with <paramref name="replacement"/>.
    /// A lowercase replacement is capitalized when it starts a sentence.
    /// </summary>
    /// <param name="changed">How many occurrences actually changed.</param>
    public static string Replace(string text, string find, string replacement, out int changed)
    {
        changed = 0;
        replacement = NormalizeSpaces(replacement);
        if (Pattern(find) is not { } pattern || replacement.Length == 0) return text;
        return Replace(text, pattern, replacement, out changed);
    }

    /// <summary>Applies one correction to every caption; returns how many occurrences changed.</summary>
    public static int Apply(IEnumerable<CaptionSegment> captions, string find, string replacement)
    {
        replacement = NormalizeSpaces(replacement);
        if (Pattern(find) is not { } pattern || replacement.Length == 0) return 0;

        int total = 0;
        foreach (var caption in captions)
        {
            caption.Text = Replace(caption.Text, pattern, replacement, out int changed);
            total += changed;
        }
        return total;
    }

    private static string Replace(string text, Regex pattern, string replacement, out int changed)
    {
        int count = 0;
        bool spannedLines = false;
        var result = pattern.Replace(text, match =>
        {
            var value = MatchCase(match.Value, replacement, StartsSentence(text, match.Index));
            if (value == match.Value) return value;
            count++;
            spannedLines |= match.Value.Contains('\n');
            return value;
        });
        changed = count;

        // A phrase that was split across the two caption lines is replaced on one line, so re-balance them.
        return spannedLines ? CaptionCleanup.WrapLines(result) : result;
    }

    /// <summary>Collapses runs of whitespace to single spaces and trims.</summary>
    public static string NormalizeSpaces(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Whole-word, case-insensitive pattern for a word or phrase; null when it has no words.</summary>
    internal static Regex? Pattern(string phrase)
    {
        var words = phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        var body = string.Join(@"\s+", words.Select(Regex.Escape));

        // Not inside a longer word: "cell" doesn't match "cellular", and "t" doesn't match the end of "don't".
        // Plain lookarounds instead of \b so terms that start or end with punctuation ("C++", "Dr.") still match.
        return new Regex($@"(?<![\p{{L}}\p{{N}}_'’]){body}(?![\p{{L}}\p{{N}}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string MatchCase(string original, string replacement, bool sentenceStart)
    {
        // The instructor's (or dictionary's) capitalization wins, except that a lowercase
        // replacement still gets a capital letter at the start of a sentence.
        if (sentenceStart && replacement.Any(char.IsLetter) && !replacement.Any(char.IsUpper)
            && original.Length > 0 && char.IsUpper(original[0]))
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }
        return replacement;
    }

    private static bool StartsSentence(string text, int index)
    {
        int i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        return i < 0 || text[i] is '.' or '?' or '!';
    }
}
