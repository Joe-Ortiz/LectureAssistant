using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using LectureAssistant.Core;
using LectureAssistant.Core.Models;

namespace LectureAssistant.QuestionGeneration.Claude;

/// <summary>
/// Generates questions with Claude using the instructor's own API key. The whole transcript is sent in one
/// request (a 75-minute lecture is ~12-20k tokens) and the answer is constrained to
/// <see cref="QuestionDraftSchema"/> with structured outputs.
/// </summary>
public sealed class ClaudeQuestionGenerator : IQuestionGenerator
{
    // Array form of server-side fallbacks vs. the "default" scalar form use different beta headers;
    // pairing either header with the other form is a 400.
    private const string PinnedFallbackBeta = "server-side-fallback-2026-06-01";
    private const string DefaultFallbackBeta = "server-side-fallback-2026-07-01";

    private readonly Func<ClaudeQuestionGeneratorOptions> _options;

    public ClaudeQuestionGenerator(ClaudeQuestionGeneratorOptions options) : this(() => options) { }

    /// <summary>Reads options on every call, so a key changed on the settings page takes effect immediately.</summary>
    public ClaudeQuestionGenerator(Func<ClaudeQuestionGeneratorOptions> optionsProvider)
    {
        _options = optionsProvider;
    }

    public string DisplayName => "Claude (your API key)";

    public async Task<IReadOnlyList<Question>> GenerateAsync(
        QuestionGenerationRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        QuestionPrompt.EnsureUsable(request);
        var options = _options();
        using var client = CreateClient(options);

        progress?.Report("Sending the transcript to Claude…");
        var parameters = BuildParameters(request, options);

        progress?.Report("Claude is reading the lecture and writing questions. This can take a few minutes…");
        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Translate(ex, cancellationToken) is { } friendly)
        {
            throw friendly;
        }

        var json = ReadJson(response);
        progress?.Report("Checking the questions…");

        QuestionDraftSet drafts;
        try
        {
            drafts = QuestionDraftSchema.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new QuestionGenerationException(QuestionGenerationFailure.InvalidResponse,
                "Claude's answer couldn't be read. Please try again.", ex);
        }

        var questions = QuestionPostProcessor.Process(drafts.Questions, request);
        if (questions.Count == 0)
            throw new QuestionGenerationException(QuestionGenerationFailure.InvalidResponse,
                "Claude didn't return any usable questions for this lecture. Try again, or adjust the question types or guidance.");

        progress?.Report($"Generated {questions.Count} question{(questions.Count == 1 ? "" : "s")}.");
        return questions;
    }

    /// <summary>
    /// Cheap check for the settings page: lists models with the key. Returns null when the key works,
    /// otherwise a message suitable for display.
    /// </summary>
    public static async Task<string?> ValidateApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return MissingKeyMessage;
        using var client = CreateClient(new ClaudeQuestionGeneratorOptions { ApiKey = apiKey, MaxRetries = 1, Timeout = TimeSpan.FromSeconds(30) });
        try
        {
            await client.Models.List(cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (Translate(ex, cancellationToken) is { } friendly)
        {
            return friendly.Message;
        }
    }

    internal static MessageCreateParams BuildParameters(QuestionGenerationRequest request, ClaudeQuestionGeneratorOptions options)
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            QuestionDraftSchema.BuildJsonSchema(request.AllowedTypes))!;

        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            System = QuestionPrompt.SystemPrompt,
            Messages =
            [
                new() { Role = Role.User, Content = QuestionPrompt.BuildUserMessage(request, request.QuestionCount) },
            ],
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig
            {
                Effort = ToSdk(options.Effort),
                Format = new BetaJsonOutputFormat { Schema = schema },
            },
        };

        if (options.EnableRefusalFallback)
        {
            if (string.IsNullOrWhiteSpace(options.FallbackModel))
            {
                parameters = parameters with { Betas = [DefaultFallbackBeta], Fallbacks = new Default() };
            }
            else
            {
                parameters = parameters with
                {
                    Betas = [PinnedFallbackBeta],
                    Fallbacks = new List<BetaFallbackParam> { new() { Model = options.FallbackModel } },
                };
            }
        }
        return parameters;
    }

    internal static string ReadJson(BetaMessage response)
    {
        // Check the stop reason before touching content: a refusal can arrive with empty or partial content.
        var stopReason = response.StopReason?.Raw();
        if (stopReason == "refusal")
            throw new QuestionGenerationException(QuestionGenerationFailure.Refused,
                "Claude declined to write questions for this transcript. You can try again with different guidance, or use a local model.");
        if (stopReason == "max_tokens")
            throw new QuestionGenerationException(QuestionGenerationFailure.InvalidResponse,
                "Claude's answer was cut off before it finished. Try asking for fewer questions, or increase the output limit in settings.");

        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        if (string.IsNullOrWhiteSpace(text))
            throw new QuestionGenerationException(QuestionGenerationFailure.InvalidResponse,
                "Claude returned an empty answer. Please try again.");
        return text;
    }

    internal static QuestionGenerationException? Translate(Exception ex, CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) return null;
        if (ex is QuestionGenerationException) return null;

        return ex switch
        {
            AnthropicUnauthorizedException => new(QuestionGenerationFailure.InvalidApiKey,
                "Your Anthropic API key was rejected. Check that it was copied correctly in Settings, or create a new key in the Anthropic Console.", ex),
            AnthropicForbiddenException => new(QuestionGenerationFailure.PermissionDenied,
                "Your Anthropic API key isn't allowed to use this model. Check your organization's permissions in the Anthropic Console.", ex),
            AnthropicRateLimitException => new(QuestionGenerationFailure.RateLimited,
                "Your Anthropic account has hit its rate limit. Wait a minute and try again, or check your usage limits in the Anthropic Console.", ex),
            AnthropicNotFoundException => new(QuestionGenerationFailure.BadRequest,
                "The selected Claude model isn't available to your API key. Choose a different model in Settings.", ex),
            Anthropic5xxException => new(QuestionGenerationFailure.ServiceUnavailable,
                "Claude is temporarily overloaded or unavailable. Please try again in a few minutes.", ex),
            AnthropicBadRequestException when IsBilling(ex) => new(QuestionGenerationFailure.PermissionDenied,
                "Your Anthropic account is out of credits. Add credits in the Anthropic Console (Plans & Billing) and try again.", ex),
            AnthropicBadRequestException => new(QuestionGenerationFailure.BadRequest,
                "Claude rejected the request: " + ex.Message, ex),
            AnthropicApiException api when IsStatus(api, 402) || IsBilling(ex) => new(QuestionGenerationFailure.PermissionDenied,
                "Your Anthropic account is out of credits. Add credits in the Anthropic Console (Plans & Billing) and try again.", ex),
            AnthropicApiException api when IsStatus(api, 529) => new(QuestionGenerationFailure.ServiceUnavailable,
                "Claude is temporarily overloaded. Please try again in a few minutes.", ex),
            AnthropicIOException or HttpRequestException => new(QuestionGenerationFailure.Network,
                "Couldn't reach Anthropic. Check your internet connection (and any proxy or firewall) and try again.", ex),
            TaskCanceledException or TimeoutException => new(QuestionGenerationFailure.Network,
                "The request to Claude timed out. Check your connection and try again; long lectures can take several minutes.", ex),
            AnthropicApiException => new(QuestionGenerationFailure.Other,
                "Claude returned an error: " + ex.Message, ex),
            _ => null,
        };
    }

    internal const string MissingKeyMessage =
        "Add your Anthropic API key in Settings to generate questions with Claude.";

    private static AnthropicClient CreateClient(ClaudeQuestionGeneratorOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new QuestionGenerationException(QuestionGenerationFailure.InvalidApiKey, MissingKeyMessage);

        return new AnthropicClient
        {
            ApiKey = options.ApiKey.Trim(),
            MaxRetries = options.MaxRetries,
            Timeout = options.Timeout,
        };
    }

    private static Effort ToSdk(ClaudeEffort effort) => effort switch
    {
        ClaudeEffort.Low => Effort.Low,
        ClaudeEffort.Medium => Effort.Medium,
        ClaudeEffort.XHigh => Effort.Xhigh,
        ClaudeEffort.Max => Effort.Max,
        _ => Effort.High,
    };

    private static bool IsStatus(AnthropicApiException ex, int status) => (int)ex.StatusCode == status;

    private static bool IsBilling(Exception ex) =>
        ex.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("billing", StringComparison.OrdinalIgnoreCase);
}
