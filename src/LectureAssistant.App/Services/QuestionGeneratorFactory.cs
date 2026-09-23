using LectureAssistant.Core;
using LectureAssistant.QuestionGeneration.Claude;
using LectureAssistant.QuestionGeneration.Local;

namespace LectureAssistant.App.Services;

/// <summary>Builds the question generator the instructor picked in Settings (their Claude key, or a local model).</summary>
public sealed class QuestionGeneratorFactory(SettingsService settings, SecretStore secrets)
{
    public IQuestionGenerator Create()
    {
        var s = settings.Current;
        switch (s.QuestionProvider)
        {
            case QuestionProvider.LocalModel:
                if (string.IsNullOrWhiteSpace(s.LocalModelPath) || !File.Exists(s.LocalModelPath))
                    throw new InvalidOperationException("Choose a local model file (.gguf) in Settings first.");
                return new LocalLlmQuestionGenerator(new LocalLlmOptions
                {
                    ModelPath = s.LocalModelPath,
                    ContextSize = s.LocalModelContextSize,
                    GpuLayerCount = s.LocalModelGpuLayers < 0 ? 999 : s.LocalModelGpuLayers,
                });

            default:
                var key = secrets.GetAnthropicApiKey();
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidOperationException("Add your Anthropic API key in Settings first, or switch to a local model.");
                return new ClaudeQuestionGenerator(new ClaudeQuestionGeneratorOptions { ApiKey = key, Model = s.ClaudeModel });
        }
    }

    public string DescribeCurrent()
    {
        var s = settings.Current;
        return s.QuestionProvider == QuestionProvider.LocalModel
            ? $"Using the local model {Path.GetFileName(s.LocalModelPath) ?? "(none chosen)"}. The transcript stays on this PC. Change this in Settings."
            : $"Using Claude ({s.ClaudeModel}) with your API key. The transcript is sent to Anthropic; the video is not. Change this in Settings.";
    }

    /// <summary>Returns a message for the settings page.</summary>
    public async Task<string> TestApiKeyAsync(string apiKey) =>
        await ClaudeQuestionGenerator.ValidateApiKeyAsync(apiKey) ?? "✓ Key works.";
}
