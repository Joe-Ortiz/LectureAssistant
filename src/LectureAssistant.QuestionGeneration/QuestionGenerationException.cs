namespace LectureAssistant.QuestionGeneration;

public enum QuestionGenerationFailure
{
    /// <summary>API key missing, malformed or revoked (HTTP 401).</summary>
    InvalidApiKey,

    /// <summary>Key is valid but not allowed to use the model, or billing problem (HTTP 402/403).</summary>
    PermissionDenied,

    /// <summary>Too many requests or tokens for the account's tier (HTTP 429).</summary>
    RateLimited,

    /// <summary>Anthropic is overloaded or had a server error (HTTP 5xx / 529).</summary>
    ServiceUnavailable,

    /// <summary>Could not reach the server (offline, proxy, DNS, timeout).</summary>
    Network,

    /// <summary>The model declined to answer (stop reason "refusal").</summary>
    Refused,

    /// <summary>The answer was cut off or could not be parsed.</summary>
    InvalidResponse,

    /// <summary>The model file could not be found or loaded.</summary>
    ModelLoadFailed,

    /// <summary>The request itself was rejected (bad model name, transcript too long, ...).</summary>
    BadRequest,

    Other,
}

/// <summary>
/// A failure whose <see cref="Exception.Message"/> is written for instructors and can be shown as-is.
/// </summary>
public sealed class QuestionGenerationException : Exception
{
    public QuestionGenerationFailure Failure { get; }

    public QuestionGenerationException(QuestionGenerationFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }
}
