using System.Text.Json;
using LectureAssistant.Core.Persistence;

namespace LectureAssistant.App.Services;

public enum QuestionProvider
{
    Claude,
    LocalModel,
}

public sealed class AppSettings
{
    public string? WhisperModelId { get; set; }
    public string TranscriptionLanguage { get; set; } = "auto";

    public QuestionProvider QuestionProvider { get; set; } = QuestionProvider.Claude;
    public string ClaudeModel { get; set; } = "claude-opus-5";

    public string? LocalModelPath { get; set; }

    /// <summary>Layers offloaded to the GPU; -1 = as many as fit.</summary>
    public int LocalModelGpuLayers { get; set; } = -1;
    public int LocalModelContextSize { get; set; } = 8192;

    public int DefaultQuestionCount { get; set; } = 8;
}

/// <summary>Non-secret preferences, stored as JSON. The API key lives in <see cref="SecretStore"/>.</summary>
public sealed class SettingsService
{
    public AppSettings Current { get; }

    public SettingsService()
    {
        Current = Load();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, ProjectStore.JsonOptions));
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), ProjectStore.JsonOptions) ?? new();
        }
        catch (JsonException) { }
        return new AppSettings();
    }
}
