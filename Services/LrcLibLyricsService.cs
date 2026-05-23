using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lyrictified.Models;

namespace Lyrictified.Services;

public sealed class LrcLibLyricsService : IDisposable
{
    private static readonly Regex TimestampRegex = new(@"^\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\](.*)$", RegexOptions.Compiled);
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LrcLibLyricsService()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://lrclib.net/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Lyrictified/0.1");
    }

    public async Task<IReadOnlyList<LyricLine>> GetLyricsAsync(SongInfo song, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist))
        {
            return Array.Empty<LyricLine>();
        }

        try
        {
            var direct = await TryGetAsync(BuildGetEndpoint(song), cancellationToken);
            if (direct is not null)
            {
                return direct;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LrcLibLyricsService direct lookup failed: {ex}");
        }

        try
        {
            var search = await TrySearchAsync(song, cancellationToken);
            return search ?? Array.Empty<LyricLine>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LrcLibLyricsService search lookup failed: {ex}");
            return Array.Empty<LyricLine>();
        }
    }

    private async Task<IReadOnlyList<LyricLine>?> TryGetAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<LyricsPayload>(content, _jsonOptions, cancellationToken);
        return ParsePayload(payload);
    }

    private async Task<IReadOnlyList<LyricLine>?> TrySearchAsync(SongInfo song, CancellationToken cancellationToken)
    {
        var query = $"api/search?track_name={Uri.EscapeDataString(song.Title)}&artist_name={Uri.EscapeDataString(song.Artist)}";
        using var response = await _httpClient.GetAsync(query, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payloads = await JsonSerializer.DeserializeAsync<List<LyricsPayload>>(content, _jsonOptions, cancellationToken);
        return payloads?
            .Select(ParsePayload)
            .FirstOrDefault(lines => lines.Count > 0);
    }

    private static IReadOnlyList<LyricLine> ParsePayload(LyricsPayload? payload)
    {
        if (payload is null || payload.Instrumental == true || string.IsNullOrWhiteSpace(payload.SyncedLyrics))
        {
            return Array.Empty<LyricLine>();
        }

        return ParseSyncedLyrics(payload.SyncedLyrics);
    }

    internal static IReadOnlyList<LyricLine> ParseSyncedLyrics(string syncedLyrics)
    {
        var lines = new List<LyricLine>();
        var wordRegex = new Regex(@"<(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?>", RegexOptions.Compiled);

        foreach (var rawLine in syncedLyrics.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var match = TimestampRegex.Match(rawLine);
                if (!match.Success)
                {
                    continue;
                }

                var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var fractionValue = match.Groups[3].Value;
                var milliseconds = fractionValue.Length switch
                {
                    1 => int.Parse(fractionValue, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fractionValue, CultureInfo.InvariantCulture) * 10,
                    3 => int.Parse(fractionValue, CultureInfo.InvariantCulture),
                    _ => 0
                };

                var text = match.Groups[4].Value.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var baseTimestamp = new TimeSpan(0, 0, minutes, seconds, milliseconds);

                IReadOnlyList<WordInfo>? words = null;
                var wordMatches = wordRegex.Matches(text);
                if (wordMatches.Count > 0)
                {
                    Logger.Log($"Parse: {wordMatches.Count} word timestamps in '{text.Substring(0, Math.Min(80, text.Length))}'");
                    var wordList = new List<WordInfo>();
                    var lastEnd = 0;
                    foreach (System.Text.RegularExpressions.Match wordMatch in wordMatches)
                    {
                        var wMinutes = int.Parse(wordMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                        var wSeconds = int.Parse(wordMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                        var wFractionValue = wordMatch.Groups[3].Value;
                        var wMilliseconds = wFractionValue.Length switch
                        {
                            1 => int.Parse(wFractionValue, CultureInfo.InvariantCulture) * 100,
                            2 => int.Parse(wFractionValue, CultureInfo.InvariantCulture) * 10,
                            3 => int.Parse(wFractionValue, CultureInfo.InvariantCulture),
                            _ => 0
                        };

                        var wordStart = wordMatch.Index + wordMatch.Length;
                        var wordEnd = text.Length;
                        if (wordMatches.Count > wordList.Count + 1)
                        {
                            wordEnd = wordMatches[wordList.Count + 1].Index;
                        }

                        var wordText = text.AsSpan(wordStart, wordEnd - wordStart).Trim().ToString();
                        if (!string.IsNullOrWhiteSpace(wordText))
                        {
                            wordList.Add(new WordInfo(new TimeSpan(0, 0, wMinutes, wSeconds, wMilliseconds), wordText));
                        }

                        lastEnd = wordEnd;
                    }

                    if (wordList.Count > 0)
                    {
                        words = wordList;
                        text = string.Join(" ", wordList.Select(w => w.Word));
                        Logger.Log($"Parse: {wordList.Count} words extracted");
                    }
                }

                lines.Add(new LyricLine(baseTimestamp, text, words));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LrcLibLyricsService.ParseSyncedLyrics skipped line '{rawLine}': {ex.Message}");
            }
        }

        return lines;
    }

    private static string BuildGetEndpoint(SongInfo song)
    {
        var parameters = new List<string>
        {
            $"track_name={Uri.EscapeDataString(song.Title)}",
            $"artist_name={Uri.EscapeDataString(song.Artist)}"
        };

        if (!string.IsNullOrWhiteSpace(song.Album))
        {
            parameters.Add($"album_name={Uri.EscapeDataString(song.Album)}");
        }

        if (song.Duration > TimeSpan.Zero)
        {
            parameters.Add($"duration={(int)Math.Round(song.Duration.TotalSeconds)}");
        }

        return $"api/get?{string.Join("&", parameters)}";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class LyricsPayload
    {
        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; init; }

        [JsonPropertyName("instrumental")]
        public bool? Instrumental { get; init; }
    }
}
