using System.Globalization;
using LectureAssistant.Core.Captions;

namespace LectureAssistant.App.Helpers;

/// <summary>Formats and parses the timestamps instructors type: "90", "1:30", "1:30.5", "1:02:03".</summary>
public static class TimeText
{
    public static string Format(TimeSpan t) => CaptionFormats.Clock(t);

    /// <summary>Like <see cref="Format"/> but with tenths, for caption boundaries.</summary>
    public static string FormatPrecise(TimeSpan t) =>
        Format(TimeSpan.FromSeconds(Math.Floor(t.TotalSeconds))) + "." + (t.Milliseconds / 100).ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split(':');
        if (parts.Length > 3) return false;

        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            if (last)
            {
                if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)) return false;
                if (parts.Length > 1 && seconds >= 60) return false;
                total = total * 60 + seconds;
            }
            else
            {
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var whole)) return false;
                if (i > 0 && whole >= 60) return false;
                total = total * 60 + whole;
            }
        }
        value = TimeSpan.FromSeconds(total);
        return true;
    }
}
