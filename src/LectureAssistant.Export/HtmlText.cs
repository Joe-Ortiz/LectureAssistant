using System.Text;

namespace LectureAssistant.Export;

/// <summary>Minimal HTML encoding for text placed in HTML-bearing fields (H5P params).</summary>
internal static class HtmlText
{
    /// <summary>
    /// Escapes &amp; &lt; &gt; " and ' only, leaving other characters as-is (unlike WebUtility.HtmlEncode,
    /// which also turns Latin-1 letters into numeric entities that some H5P plain-text fields would show literally).
    /// </summary>
    public static string Encode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>Encodes and turns line breaks into &lt;br&gt;. Does not trim.</summary>
    public static string EncodeMultiline(string? text) =>
        Encode(text).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br>");

    public static string Paragraph(string? text) => "<p>" + EncodeMultiline(text?.Trim()) + "</p>";

    public static string Div(string? text) => "<div>" + EncodeMultiline(text?.Trim()) + "</div>";
}
