namespace LectureAssistant.App.Services;

/// <summary>Where the app keeps its data: the package's LocalFolder when installed from MSIX, %LOCALAPPDATA% otherwise.</summary>
public static class AppPaths
{
    public static string DataRoot { get; } = ResolveDataRoot();

    public static string Projects => Path.Combine(DataRoot, "Projects");
    public static string WhisperModels => Path.Combine(DataRoot, "Models", "whisper");
    public static string LocalModels => Path.Combine(DataRoot, "Models", "llm");
    public static string Previews => Path.Combine(DataRoot, "Previews");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string DictionaryFile => Path.Combine(DataRoot, "dictionary.json");

    private static string ResolveDataRoot()
    {
        try
        {
            return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch (Exception)
        {
            // Unpackaged (e.g. running straight from bin during development) has no package identity.
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LectureAssistant");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
