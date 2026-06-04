using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lyrictified.Models;

namespace Lyrictified.Services;

internal static class TtmlLyricsParser
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static IReadOnlyList<LyricLine> Parse(string ttml)
    {
        if (string.IsNullOrWhiteSpace(ttml))
        {
            return Array.Empty<LyricLine>();
        }

        try
        {
            var document = XDocument.Parse(ttml, LoadOptions.PreserveWhitespace);
            var lines = new List<LyricLine>();

            foreach (var paragraph in document.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                var timestamp = GetTiming(paragraph, "begin");
                if (timestamp is null
                    && paragraph.Descendants().FirstOrDefault(e => e.Name.LocalName == "span") is { } firstSpan)
                {
                    timestamp = GetTiming(firstSpan, "begin");
                }

                if (timestamp is null)
                {
                    continue;
                }

                var text = NormalizeText(paragraph.Value);
                var words = ParseWords(paragraph);
                lines.Add(new LyricLine(timestamp.Value, text, words.Count > 0 ? words : null));
            }

            return lines
                .OrderBy(line => line.Timestamp)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"TtmlLyricsParser.Parse failed: {ex.Message}");
            return Array.Empty<LyricLine>();
        }
    }

    private static IReadOnlyList<WordInfo> ParseWords(XElement paragraph)
    {
        var words = new List<WordInfo>();

        foreach (var span in paragraph.Descendants().Where(e => e.Name.LocalName == "span"))
        {
            var timestamp = GetTiming(span, "begin");
            var text = NormalizeText(span.Value);
            if (timestamp is not null && !string.IsNullOrWhiteSpace(text))
            {
                words.Add(new WordInfo(timestamp.Value, text));
            }
        }

        return words;
    }

    private static TimeSpan? GetTiming(XElement element, string attributeName)
    {
        var value = element.Attributes()
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, attributeName, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return TryParseTiming(value, out var timestamp) ? timestamp : null;
    }

    private static bool TryParseTiming(string? value, out TimeSpan timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            timestamp = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            timestamp = TimeSpan.FromSeconds(seconds);
            return true;
        }

        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var hours = 0;
        var minutesPartIndex = 0;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
            {
                return false;
            }

            minutesPartIndex = 1;
        }

        if (!int.TryParse(parts[minutesPartIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[minutesPartIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secondsPart))
        {
            return false;
        }

        timestamp = TimeSpan.FromHours(hours)
            + TimeSpan.FromMinutes(minutes)
            + TimeSpan.FromSeconds(secondsPart);
        return true;
    }

    private static string NormalizeText(string value)
    {
        return WhitespaceRegex.Replace(value, " ").Trim();
    }
}
