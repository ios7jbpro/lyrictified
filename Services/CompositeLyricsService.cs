using System.Diagnostics;
using Lyrictified.Models;

namespace Lyrictified.Services;

public sealed class CompositeLyricsService : IDisposable
{
    private readonly LocalLyricsService _localLyricsService;
    private readonly LrcLibLyricsService _lrcLibLyricsService;
    private readonly SyncedLyricsCliService _syncedLyricsCliService;
    private readonly LyricsCacheService _cacheService;
    private int _maxCacheSize = 25;
    private string _lastSearchInfo = string.Empty;

    public string HelperStatusHint => _syncedLyricsCliService.StatusHint;

    public string LastSearchInfo => _lastSearchInfo;

    public string? ForcedSource { get; set; }

    public CompositeLyricsService()
    {
        _localLyricsService = new LocalLyricsService();
        _lrcLibLyricsService = new LrcLibLyricsService();
        _syncedLyricsCliService = new SyncedLyricsCliService();
        _cacheService = new LyricsCacheService();
    }

    public void SetMaxCacheSize(int maxSize)
    {
        _maxCacheSize = maxSize;
        _cacheService.Prune(maxSize);
    }

    public async Task<LyricsResult> GetTimedLyricsAsync(SongInfo song, CancellationToken cancellationToken)
    {
        Logger.Log($"GetTimedLyricsAsync: {song.Title} - {song.Artist}");

        var searchTitle = song.Title ?? "";
        var searchArtist = song.Artist ?? "";
        var searchAlbum = song.Album ?? "";
        var searchDuration = song.Duration > TimeSpan.Zero ? $"{(int)Math.Round(song.Duration.TotalSeconds)}s" : "unknown";
        var searchKey = $"Title=\"{searchTitle}\" Artist=\"{searchArtist}\" Album=\"{searchAlbum}\" Duration={searchDuration}";

        var forcedSource = ForcedSource;

        if (_maxCacheSize > 0 && !App.IgnoreLocalCache && string.IsNullOrEmpty(forcedSource))
        {
            var cached = _cacheService.TryGet(searchTitle, searchArtist);
            if (cached is not null)
            {
                Logger.Log($"Cache hit: {cached.Count} lines");
                _lastSearchInfo = $"{searchKey}\nResult: Cache hit — {cached.Count} lines";
                return WrapResult(cached);
            }
        }

        if (string.IsNullOrEmpty(forcedSource) || forcedSource == "Local")
        {
            try
            {
                Logger.Log("Trying Lyrictified Server API");
                var sw = Stopwatch.StartNew();
                var localLyrics = await _localLyricsService.GetLyricsAsync(song, cancellationToken);
                sw.Stop();
                if (localLyrics.Count > 0)
                {
                    Logger.Log($"Lyrictified Server API: {localLyrics.Count} lines in {sw.ElapsedMilliseconds}ms");
                    _lastSearchInfo = $"{searchKey}\nSource: Lyrictified Server API — {localLyrics.Count} lines ({sw.ElapsedMilliseconds}ms) — Accepted";
                    _cacheService.Store(searchTitle, searchArtist, localLyrics, _maxCacheSize);
                    return WrapResult(localLyrics);
                }
                Logger.Log($"Lyrictified Server API: 0 lines in {sw.ElapsedMilliseconds}ms");
                _lastSearchInfo = $"{searchKey}\nSource: Lyrictified Server API — No synced lyrics found ({sw.ElapsedMilliseconds}ms) — Denied";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"Lyrictified Server API failed: {ex.Message}");
                _lastSearchInfo = $"{searchKey}\nSource: Lyrictified Server API — Error: {ex.Message} — Denied";
            }

            if (!string.IsNullOrEmpty(forcedSource))
            {
                return EmptyResult();
            }
        }

        if (string.IsNullOrEmpty(forcedSource) || forcedSource == "LrcLib")
        {
            try
            {
                Logger.Log("Trying lrclib.net");
                var sw = Stopwatch.StartNew();
                var lrclibLyrics = await _lrcLibLyricsService.GetLyricsAsync(song, cancellationToken);
                sw.Stop();
                if (lrclibLyrics.Count > 0)
                {
                    Logger.Log($"lrclib: {lrclibLyrics.Count} lines in {sw.ElapsedMilliseconds}ms");
                    _lastSearchInfo = $"{searchKey}\nSource: lrclib.net — {lrclibLyrics.Count} lines ({sw.ElapsedMilliseconds}ms) — Accepted";
                    _cacheService.Store(searchTitle, searchArtist, lrclibLyrics, _maxCacheSize);
                    return WrapResult(lrclibLyrics);
                }
                Logger.Log($"lrclib: 0 lines in {sw.ElapsedMilliseconds}ms");
                _lastSearchInfo = $"{searchKey}\nSource: lrclib.net — No synced lyrics found ({sw.ElapsedMilliseconds}ms) — Denied";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"lrclib failed: {ex.Message}");
                _lastSearchInfo = $"{searchKey}\nSource: lrclib.net — Error: {ex.Message} — Denied";
            }

            if (!string.IsNullOrEmpty(forcedSource))
            {
                return EmptyResult();
            }
        }

        if (string.IsNullOrEmpty(forcedSource) || forcedSource == "Synced")
        {
            try
            {
                Logger.Log("Trying syncedlyrics fallback");
                var sw = Stopwatch.StartNew();
                var sidecarLyrics = await _syncedLyricsCliService.GetTimedLyricsAsync(song, cancellationToken);
                sw.Stop();
                if (sidecarLyrics.Count > 0)
                {
                    Logger.Log($"syncedlyrics: {sidecarLyrics.Count} lines in {sw.ElapsedMilliseconds}ms");
                    _lastSearchInfo = $"{searchKey}\nSource: syncedlyrics — {sidecarLyrics.Count} lines ({sw.ElapsedMilliseconds}ms) — Accepted";
                    _cacheService.Store(searchTitle, searchArtist, sidecarLyrics, _maxCacheSize);
                    return WrapResult(sidecarLyrics);
                }
                Logger.Log($"syncedlyrics: 0 lines in {sw.ElapsedMilliseconds}ms");
                _lastSearchInfo = $"{searchKey}\nSource: syncedlyrics — No lyrics found ({sw.ElapsedMilliseconds}ms) — Denied";
                return EmptyResult();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"syncedlyrics failed: {ex.Message}");
                _lastSearchInfo = $"{searchKey}\nSource: syncedlyrics — Error: {ex.Message} — Denied";
                return EmptyResult();
            }
        }

        return EmptyResult();
    }

    private static LyricsResult WrapResult(IReadOnlyList<LyricLine> lyrics)
    {
        var isTtml = lyrics.Any(line => line.IsTtml);
        var cleaned = isTtml ? TtmlLyricsParser.CleanToLrc(lyrics) : null;
        return new LyricsResult(lyrics, isTtml, cleaned);
    }

    private static LyricsResult EmptyResult()
    {
        return new LyricsResult(Array.Empty<LyricLine>(), false);
    }

    public void Dispose()
    {
        _localLyricsService.Dispose();
        _lrcLibLyricsService.Dispose();
    }
}
