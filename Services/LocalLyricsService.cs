using System.Net;
using System.Net.Http;
using System.Text.Json;
using Lyrictified.Models;

namespace Lyrictified.Services;

public sealed class LocalLyricsService : IDisposable
{
    private readonly HttpClient _httpClient;

    public LocalLyricsService()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(App.LocalLyricsBaseAddress),
            Timeout = TimeSpan.FromSeconds(3)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Lyrictified/0.1");
    }

    public async Task<IReadOnlyList<LyricLine>> GetLyricsAsync(SongInfo song, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(song.Title) && string.IsNullOrWhiteSpace(song.Artist))
        {
            return Array.Empty<LyricLine>();
        }

        foreach (var endpoint in BuildSearchEndpoints(song))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var lyrics = await TrySearchAsync(endpoint, cancellationToken);
                if (lyrics.Count > 0)
                {
                    return lyrics;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"LocalLyricsService '{endpoint}' failed: {ex.Message}");
            }
        }

        return Array.Empty<LyricLine>();
    }

    private async Task<IReadOnlyList<LyricLine>> TrySearchAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<LyricLine>();
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = ParseResponse(payload);
        if (parsed.Count > 0)
        {
            return parsed;
        }

        foreach (var lyricsEndpoint in BuildLyricsEndpoints(payload))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Logger.Log($"LocalLyricsService: trying follow-up '{lyricsEndpoint}'");
                using var lyricsResponse = await _httpClient.GetAsync(lyricsEndpoint, cancellationToken);
                if (lyricsResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                lyricsResponse.EnsureSuccessStatusCode();
                var lyricsPayload = await lyricsResponse.Content.ReadAsStringAsync(cancellationToken);
                var lyrics = ParseResponse(lyricsPayload);
                if (lyrics.Count > 0)
                {
                    Logger.Log($"LocalLyricsService: follow-up '{lyricsEndpoint}' returned {lyrics.Count} lines");
                    return lyrics;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"LocalLyricsService follow-up '{lyricsEndpoint}' failed: {ex.Message}");
            }
        }

        return Array.Empty<LyricLine>();
    }

    private static IEnumerable<string> BuildSearchEndpoints(SongInfo song)
    {
        var title = song.Title?.Trim();
        var artist = song.Artist?.Trim();

        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
        {
            yield return $"search?song={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist)}";
            yield return $"search?q={Uri.EscapeDataString($"{artist} {title}")}";
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            yield return $"search?song={Uri.EscapeDataString(title)}";
            yield return $"search?q={Uri.EscapeDataString(title)}";
        }

        if (!string.IsNullOrWhiteSpace(artist))
        {
            yield return $"search?artist={Uri.EscapeDataString(artist)}";
            yield return $"search?q={Uri.EscapeDataString(artist)}";
        }
    }

    private static IReadOnlyList<LyricLine> ParseResponse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<LyricLine>();
        }

        var trimmed = payload.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var parsed = ParseJsonElement(document.RootElement);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
            }
        }

        return ParseTimedLyrics(trimmed);
    }

    private static IEnumerable<string> BuildLyricsEndpoints(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectLyricsEndpoints(document.RootElement, endpoints);

            foreach (var endpoint in endpoints)
            {
                yield return endpoint;
            }
        }
    }

    private static void CollectLyricsEndpoints(JsonElement element, ISet<string> endpoints)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectLyricsEndpoints(item, endpoints);
                }
                break;
            case JsonValueKind.Object:
                AddObjectLyricsEndpoints(element, endpoints);

                foreach (var propertyName in new[] { "data", "result", "results", "track", "tracks", "items" })
                {
                    if (element.TryGetProperty(propertyName, out var nestedElement))
                    {
                        CollectLyricsEndpoints(nestedElement, endpoints);
                    }
                }
                break;
        }
    }

    private static void AddObjectLyricsEndpoints(JsonElement element, ISet<string> endpoints)
    {
        var id = GetStringProperty(element, "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            var escapedId = Uri.EscapeDataString(id);
            endpoints.Add($"lyrics/{escapedId}/raw");
            endpoints.Add($"lyrics/{escapedId}");
            endpoints.Add($"lyrics?id={escapedId}");
            endpoints.Add($"lyric/{escapedId}");
            endpoints.Add($"lyric?id={escapedId}");
            endpoints.Add($"lrc/{escapedId}");
            endpoints.Add($"lrc?id={escapedId}");
            endpoints.Add($"ttml/{escapedId}");
            endpoints.Add($"ttml?id={escapedId}");
            endpoints.Add($"raw/{escapedId}");
            endpoints.Add($"raw?id={escapedId}");
        }

        foreach (var propertyName in new[] { "url", "href", "path", "relativePath", "relative_path", "file", "filename" })
        {
            var value = GetStringProperty(element, propertyName);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            AddPathLyricsEndpoints(value, endpoints);
        }
    }

    private static void AddPathLyricsEndpoints(string path, ISet<string> endpoints)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var absoluteUri))
        {
            endpoints.Add(absoluteUri.ToString());
            return;
        }

        var trimmed = path.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        var escapedPath = EscapeRelativePath(trimmed);
        var escapedQueryValue = Uri.EscapeDataString(trimmed);
        endpoints.Add(escapedPath);
        endpoints.Add($"lyrics/{escapedPath}");
        endpoints.Add($"lyrics?path={escapedQueryValue}");
        endpoints.Add($"lyrics?relativePath={escapedQueryValue}");
        endpoints.Add($"lyric?path={escapedQueryValue}");
        endpoints.Add($"lrc?path={escapedQueryValue}");
        endpoints.Add($"ttml?path={escapedQueryValue}");
        endpoints.Add($"raw?path={escapedQueryValue}");
        endpoints.Add($"file?path={escapedQueryValue}");
    }

    private static string EscapeRelativePath(string path)
    {
        return string.Join(
            "/",
            path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static IReadOnlyList<LyricLine> ParseJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return ParseTimedLyrics(element.GetString() ?? string.Empty);
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var parsed = ParseJsonElement(item);
                    if (parsed.Count > 0)
                    {
                        return parsed;
                    }
                }
                break;
            case JsonValueKind.Object:
                if (IsInstrumental(element))
                {
                    return Array.Empty<LyricLine>();
                }

                foreach (var propertyName in new[] { "syncedLyrics", "synced_lyrics", "ttml", "lyrics", "lrc", "text", "body" })
                {
                    if (element.TryGetProperty(propertyName, out var lyricElement))
                    {
                        var parsed = ParseJsonElement(lyricElement);
                        if (parsed.Count > 0)
                        {
                            return parsed;
                        }
                    }
                }

                foreach (var propertyName in new[] { "data", "result", "results", "track", "tracks", "items" })
                {
                    if (element.TryGetProperty(propertyName, out var nestedElement))
                    {
                        var parsed = ParseJsonElement(nestedElement);
                        if (parsed.Count > 0)
                        {
                            return parsed;
                        }
                    }
                }
                break;
        }

        return Array.Empty<LyricLine>();
    }

    internal static IReadOnlyList<LyricLine> ParseTimedLyrics(string lyrics)
    {
        var ttml = TtmlLyricsParser.Parse(lyrics);
        return ttml.Count > 0 ? ttml : LrcLibLyricsService.ParseSyncedLyrics(lyrics);
    }

    private static bool IsInstrumental(JsonElement element)
    {
        return element.TryGetProperty("instrumental", out var instrumental)
            && instrumental.ValueKind == JsonValueKind.True;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
