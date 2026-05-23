using System.Diagnostics;
using Lyrictified.Models;

namespace Lyrictified.Services;

public sealed class CompositeLyricsService : IDisposable
{
    private readonly LrcLibLyricsService _lrcLibLyricsService;
    private readonly SyncedLyricsCliService _syncedLyricsCliService;
    private readonly LyricsCacheService _cacheService;
    private int _maxCacheSize = 25;

    public CompositeLyricsService()
    {
        _lrcLibLyricsService = new LrcLibLyricsService();
        _syncedLyricsCliService = new SyncedLyricsCliService();
        _cacheService = new LyricsCacheService();
    }

    public string HelperStatusHint => _syncedLyricsCliService.StatusHint;

    public void SetMaxCacheSize(int maxSize)
    {
        _maxCacheSize = maxSize;
        if (maxSize <= 0)
        {
            _cacheService.Clear();
        }
    }

    public async Task<IReadOnlyList<LyricLine>> GetTimedLyricsAsync(SongInfo song, CancellationToken cancellationToken)
    {
        Logger.Log($"GetTimedLyricsAsync: {song.Title} - {song.Artist}");

        if (_maxCacheSize > 0)
        {
            var cached = _cacheService.TryGet(song.Title, song.Artist);
            if (cached is not null)
            {
                Logger.Log($"Cache hit: {cached.Count} lines");
                return cached;
            }
        }

        try
        {
            Logger.Log("Trying lrclib.net");
            var sw = Stopwatch.StartNew();
            var lrclibLyrics = await _lrcLibLyricsService.GetLyricsAsync(song, cancellationToken);
            sw.Stop();
            Logger.Log($"lrclib: {lrclibLyrics.Count} lines in {sw.ElapsedMilliseconds}ms");
            if (lrclibLyrics.Count > 0)
            {
                _cacheService.Store(song.Title, song.Artist, lrclibLyrics, _maxCacheSize);
                return lrclibLyrics;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"lrclib failed: {ex.Message}");
        }

        try
        {
            Logger.Log("Trying syncedlyrics fallback");
            var sw = Stopwatch.StartNew();
            var sidecarLyrics = await _syncedLyricsCliService.GetTimedLyricsAsync(song, cancellationToken);
            sw.Stop();
            Logger.Log($"syncedlyrics: {sidecarLyrics.Count} lines in {sw.ElapsedMilliseconds}ms");
            if (sidecarLyrics.Count > 0)
            {
                _cacheService.Store(song.Title, song.Artist, sidecarLyrics, _maxCacheSize);
            }
            return sidecarLyrics.Count > 0 ? sidecarLyrics : Array.Empty<LyricLine>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"syncedlyrics failed: {ex.Message}");
            return Array.Empty<LyricLine>();
        }
    }

    public void Dispose()
    {
        _lrcLibLyricsService.Dispose();
    }
}
