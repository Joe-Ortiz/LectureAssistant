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

    /// <summary>Subject area picked most recently; the default for new lectures.</summary>
    public string? LastSubjectArea { get; set; }

    /// <summary>Local by default: works without an account, and nothing leaves the PC.</summary>
    public QuestionProvider QuestionProvider { get; set; } = QuestionProvider.LocalModel;
    public string ClaudeModel { get; set; } = "claude-opus-5";

    /// <summary>Built-in model to use; null means the one recommended for this PC.</summary>
    public string? LocalModelId { get; set; }

    /// <summary>Advanced: run a .gguf file the user picked instead of a built-in model.</summary>
    public bool UseCustomLocalModel { get; set; }
    public string? LocalModelPath { get; set; }

    /// <summary>Layers offloaded to the GPU; -1 = as many as fit.</summary>
    public int LocalModelGpuLayers { get; set; } = -1;

    /// <summary>Null uses the model's recommended context size.</summary>
    public int? LocalModelContextSize { get; set; }

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
