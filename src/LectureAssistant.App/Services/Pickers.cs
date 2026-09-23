using Microsoft.Windows.Storage.Pickers;

namespace LectureAssistant.App.Services;

/// <summary>File dialogs bound to the main window (Windows App SDK pickers work in packaged, unpackaged and elevated apps).</summary>
public static class Pickers
{
    public static readonly string[] VideoTypes = [".mp4", ".m4v", ".mov", ".mkv", ".wmv", ".avi", ".webm"];

    public static async Task<string?> OpenFileAsync(params string[] fileTypes)
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        foreach (var type in fileTypes) picker.FileTypeFilter.Add(type);
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public static async Task<string?> SaveFileAsync(string suggestedName, string description, string extension)
    {
        var picker = new FileSavePicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
        };
        picker.FileTypeChoices.Add(description, [extension]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    /// <summary>Makes a title safe to use as a file name.</summary>
    public static string SafeFileName(string title, string fallback = "Lecture")
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
