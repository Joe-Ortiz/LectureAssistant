using LectureAssistant.Core.Captions;

namespace LectureAssistant.App.Services;

/// <summary>The instructor's personal dictionary, shared by every lecture and stored as JSON next to the settings.</summary>
public sealed class DictionaryService
{
    public PersonalDictionary Current { get; } = PersonalDictionary.Load(AppPaths.DictionaryFile);

    public void Save()
    {
        try
        {
            Current.Save(AppPaths.DictionaryFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting the instructor; the entries stay in memory for this session.
        }
    }
}
