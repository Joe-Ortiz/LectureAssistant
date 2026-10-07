namespace LectureAssistant.Core.Captions;

/// <summary>
/// Builds the text Whisper reads as "what was said just before" the recording: the subject's
/// example sentence followed by the instructor's dictionary terms.
/// </summary>
public static class RecognitionPrompt
{
    /// <summary>
    /// Whisper reads at most 224 tokens of prompt and silently drops the oldest beyond that. Technical
    /// terms average roughly 3 characters per token, so 450 characters stays well inside the limit
    /// and leaves room for the context Whisper carries over from the previous 30 seconds.
    /// </summary>
    public const int MaxLength = 450;

    private const int MaxTermLength = 60;

    /// <param name="subject">Subject area; its sentence is used only for English (or auto-detected) lectures, since it's written in English.</param>
    /// <param name="terms">Dictionary terms, most important first; whatever doesn't fit in <paramref name="maxLength"/> is left out.</param>
    /// <param name="language">Whisper language code, or "auto".</param>
    /// <returns>The prompt, or null when there's nothing to say.</returns>
    public static string? Build(SubjectArea subject, IEnumerable<string> terms, string? language, int maxLength = MaxLength)
    {
        bool english = string.IsNullOrWhiteSpace(language) || language is "auto" or "en";
        var sentence = english ? subject.Prompt : "";
        if (sentence.Length > maxLength) sentence = "";

        var picked = new List<string>();
        int length = sentence.Length;
        foreach (var raw in terms)
        {
            var term = CaptionCorrections.NormalizeSpaces(raw);
            if (term.Length == 0 || term.Length > MaxTermLength) continue;
            if (picked.Contains(term, StringComparer.OrdinalIgnoreCase)) continue;
            if (CaptionCorrections.Count(sentence, term) > 0) continue;

            // Separator before it (" " after the sentence, ", " between terms) plus a closing period.
            int added = term.Length + (picked.Count > 0 ? 2 : length > 0 ? 1 : 0) + (picked.Count == 0 ? 1 : 0);
            if (length + added > maxLength) continue; // a shorter term later on may still fit
            picked.Add(term);
            length += added;
        }

        var prompt = sentence;
        if (picked.Count > 0)
        {
            var list = string.Join(", ", picked);
            if (!list.EndsWith('.')) list += ".";
            prompt = prompt.Length > 0 ? prompt + " " + list : list;
        }
        return prompt.Length > 0 ? prompt : null;
    }
}
