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
            var lines = new List<ParsedLine>();
            var order = 0;

            foreach (var paragraph in document.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                var timestamp = GetTiming(paragraph, "begin");
                var endTime = GetTiming(paragraph, "end");
                var mainWords = ParseWords(paragraph, includeBackground: false);
                if (timestamp is null && mainWords.FirstOrDefault() is { } firstWord)
                {
                    timestamp = firstWord.Timestamp;
                }

                if (timestamp is not null)
                {
                    var text = NormalizeText(GetText(paragraph, includeBackground: false));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lines.Add(new ParsedLine(
                            new LyricLine(timestamp.Value, text, mainWords.Count > 0 ? mainWords : null, endTime, IsTtml: true),
                            order++));
                    }
                }

                foreach (var backgroundSpan in paragraph
                    .Descendants()
                    .Where(e => e.Name.LocalName == "span" && IsBackgroundRole(e)))
                {
                    var backgroundTimestamp = GetTiming(backgroundSpan, "begin")
                        ?? ParseWords(backgroundSpan, includeBackground: true).FirstOrDefault()?.Timestamp;
                    if (backgroundTimestamp is null)
                    {
                        continue;
                    }

                    var text = NormalizeText(GetText(backgroundSpan, includeBackground: true));
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var words = ParseWords(backgroundSpan, includeBackground: true);
                    lines.Add(new ParsedLine(
                        new LyricLine(
                            backgroundTimestamp.Value,
                            text,
                            words.Count > 0 ? words : null,
                            GetTiming(backgroundSpan, "end") ?? GetLastWordEnd(words) ?? endTime,
                            IsTtml: true,
                            IsBackground: true),
                        order++));
                }
            }

            return lines
                .OrderBy(line => line.Line.Timestamp)
                .ThenBy(line => line.Order)
                .Select(line => line.Line)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"TtmlLyricsParser.Parse failed: {ex.Message}");
            return Array.Empty<LyricLine>();
        }
    }

    private static IReadOnlyList<WordInfo> ParseWords(XElement paragraph, bool includeBackground)
    {
        var words = new List<WordInfo>();

        foreach (var span in paragraph.Descendants().Where(e => e.Name.LocalName == "span"))
        {
            if (span.Elements().Any(e => e.Name.LocalName == "span"))
            {
                continue;
            }

            if (!includeBackground && IsInBackgroundRole(span))
            {
                continue;
            }

            var timestamp = GetTiming(span, "begin");
            var text = NormalizeText(span.Value);
            if (timestamp is not null && !string.IsNullOrWhiteSpace(text))
            {
                words.Add(new WordInfo(timestamp.Value, text, GetTiming(span, "end")));
            }
        }

        return words;
    }

    private static string GetText(XElement element, bool includeBackground)
    {
        return string.Concat(element.Nodes().Select(node => GetText(node, includeBackground)));
    }

    private static string GetText(XNode node, bool includeBackground)
    {
        return node switch
        {
            XText text => text.Value,
            XElement element when element.Name.LocalName == "span" && !includeBackground && IsBackgroundRole(element) => " ",
            XElement element => GetText(element, includeBackground),
            _ => string.Empty
        };
    }

    private static bool IsInBackgroundRole(XElement element)
    {
        return element.AncestorsAndSelf().Any(IsBackgroundRole);
    }

    private static bool IsBackgroundRole(XElement element)
    {
        return element.Attributes()
            .Any(attribute => string.Equals(attribute.Name.LocalName, "role", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attribute.Value, "x-bg", StringComparison.OrdinalIgnoreCase));
    }

    private static TimeSpan? GetLastWordEnd(IReadOnlyList<WordInfo> words)
    {
        return words
            .Select(word => word.EndTime)
            .LastOrDefault(endTime => endTime is not null);
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

    public static IReadOnlyList<LyricLine> CleanToLrc(IReadOnlyList<LyricLine> ttmlLines)
    {
        if (ttmlLines.Count == 0)
        {
            return Array.Empty<LyricLine>();
        }

        return ttmlLines
            .Where(line => !line.IsBackground)
            .Select(line => new LyricLine(line.Timestamp, line.Text, null, null, false, false))
            .ToArray();
    }

    private static string NormalizeText(string value)
    {
        return WhitespaceRegex.Replace(value, " ").Trim();
    }

    private sealed record ParsedLine(LyricLine Line, int Order);
}
