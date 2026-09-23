using LectureAssistant.Core;
using LectureAssistant.QuestionGeneration.Claude;
using LectureAssistant.QuestionGeneration.Local;

namespace LectureAssistant.App.Services;

/// <summary>Builds the question generator the instructor picked in Settings (a local model, or their Claude key).</summary>
public sealed class QuestionGeneratorFactory(SettingsService settings, SecretStore secrets, LocalModelManager localModels)
{
    /// <summary>The built-in local model in use, or null when Claude or a custom .gguf file is selected.</summary>
    public LocalModelInfo? SelectedLocalModel
    {
        get
        {
            var s = settings.Current;
            if (s.QuestionProvider != QuestionProvider.LocalModel || s.UseCustomLocalModel) return null;
            return LocalModelCatalog.Find(s.LocalModelId) ?? LocalModelCatalog.Recommend(HardwareProbe.Profile);
        }
    }

    /// <summary>The built-in model that has to be downloaded before questions can be generated, if any.</summary>
    public LocalModelInfo? ModelNeedingDownload() =>
        SelectedLocalModel is { } model && !localModels.IsDownloaded(model) ? model : null;

    public IQuestionGenerator Create()
    {
        var s = settings.Current;
        if (s.QuestionProvider == QuestionProvider.LocalModel)
        {
            var model = SelectedLocalModel;
            var path = model is not null ? localModels.GetPath(model) : s.LocalModelPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException(model is not null
                    ? "The question-writing model hasn't been downloaded yet. Download it in Settings."
                    : "Choose your model file (.gguf) in Settings → Advanced, or switch back to a built-in model.");

            return new LocalLlmQuestionGenerator(new LocalLlmOptions
            {
                ModelPath = path,
                ContextSize = s.LocalModelContextSize ?? model?.ContextSize ?? 8192,
                GpuLayerCount = s.LocalModelGpuLayers < 0 ? 999 : s.LocalModelGpuLayers,
            });
        }

        var key = secrets.GetAnthropicApiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Add your Anthropic API key in Settings first, or switch to \"On this PC\".");
        return new ClaudeQuestionGenerator(new ClaudeQuestionGeneratorOptions { ApiKey = key, Model = s.ClaudeModel });
    }

    public string DescribeCurrent()
    {
        var s = settings.Current;
        if (s.QuestionProvider == QuestionProvider.Claude)
            return $"Using Claude ({s.ClaudeModel}) with your API key. The transcript is sent to Anthropic; the video is not. Change this in Settings.";

        if (SelectedLocalModel is { } model)
            return localModels.IsDownloaded(model)
                ? $"Runs on this PC with the {model.DisplayName} model. Nothing is sent over the internet."
                : $"Runs on this PC. The first time, Lecture Assistant downloads its question-writing model ({model.SizeText}, one time only).";

        return $"Runs on this PC with your model file {Path.GetFileName(s.LocalModelPath) ?? "(none chosen)"}.";
    }

    /// <summary>Returns a message for the settings page.</summary>
    public async Task<string> TestApiKeyAsync(string apiKey) =>
        await ClaudeQuestionGenerator.ValidateApiKeyAsync(apiKey) ?? "✓ Key works.";
}
