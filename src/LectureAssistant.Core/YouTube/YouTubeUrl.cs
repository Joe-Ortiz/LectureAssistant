using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace LectureAssistant.Core.YouTube;

public static partial class YouTubeUrl
{
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();

    /// <summary>
    /// Extracts the 11-character video id from the URL shapes instructors are likely to paste:
    /// watch?v=, youtu.be/, embed/, shorts/, live/, youtube-nocookie.com, or a bare id.
    /// </summary>
    public static bool TryGetVideoId(string? input, [NotNullWhen(true)] out string? videoId)
    {
        videoId = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();

        if (VideoIdPattern().IsMatch(text))
        {
            videoId = text;
            return true;
        }

        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.StartsWith("m.", StringComparison.Ordinal)) host = host[2..];

        string? candidate = null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host == "youtu.be")
        {
            candidate = segments.FirstOrDefault();
        }
        else if (host is "youtube.com" or "youtube-nocookie.com" or "music.youtube.com")
        {
            if (segments.Length >= 2 && segments[0] is "embed" or "shorts" or "live" or "v")
                candidate = segments[1];
            else
                candidate = GetQueryValue(uri.Query, "v");
        }

        if (candidate is not null && VideoIdPattern().IsMatch(candidate))
        {
            videoId = candidate;
            return true;
        }
        return false;
    }

    public static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    private static string? GetQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == key) return Uri.UnescapeDataString(parts[1]);
        }
        return null;
    }
}
