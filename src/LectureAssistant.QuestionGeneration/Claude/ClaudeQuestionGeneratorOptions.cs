namespace LectureAssistant.QuestionGeneration.Claude;

/// <summary>How much the model thinks before answering; higher is slower and costs more.</summary>
public enum ClaudeEffort
{
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

public sealed record ClaudeQuestionGeneratorOptions
{
    public const string DefaultModel = "claude-opus-5";

    /// <summary>The user's own Anthropic API key (BYOK). Empty/null produces a friendly "add your key" error.</summary>
    public string? ApiKey { get; init; }

    public string Model { get; init; } = DefaultModel;

    public ClaudeEffort Effort { get; init; } = ClaudeEffort.High;

    /// <summary>Output budget, including adaptive thinking. ~16k fits a dozen questions with room to think.</summary>
    public int MaxTokens { get; init; } = 16000;

    /// <summary>
    /// Re-run the request on another model server-side if the requested model's safety classifiers
    /// decline it (rare for lecture content, but possible for e.g. security or biology courses).
    /// </summary>
    public bool EnableRefusalFallback { get; init; } = true;

    /// <summary>
    /// Null (default) lets Anthropic pick the recommended fallback per refusal category
    /// (<c>fallbacks: "default"</c>). Set a model id, e.g. "claude-opus-4-8", to pin one instead.
    /// </summary>
    public string? FallbackModel { get; init; }

    /// <summary>Per-request HTTP timeout. Long lectures at high effort can take several minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>SDK retries for 429/5xx/connection errors, with backoff.</summary>
    public int MaxRetries { get; init; } = 2;
}
