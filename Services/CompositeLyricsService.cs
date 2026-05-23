using System.Diagnostics;
using Lyrictified.Models;

namespace Lyrictified.Services;

public sealed class CompositeLyricsService : IDisposable
{
    private readonly LrcLibLyricsService _lrcLibLyricsService;
    private readonly SyncedLyricsCliService _syncedLyricsCliService;

    public CompositeLyricsService()
    {
        _lrcLibLyricsService = new LrcLibLyricsService();
        _syncedLyricsCliService = new SyncedLyricsCliService();
    }

    public string HelperStatusHint => _syncedLyricsCliService.StatusHint;

    public async Task<IReadOnlyList<LyricLine>> GetTimedLyricsAsync(SongInfo song, bool enhanced, CancellationToken cancellationToken)
    {
        Logger.Log($"GetTimedLyricsAsync: {song.Title} - {song.Artist} enhanced={enhanced}");

        if (enhanced)
        {
            try
            {
                Logger.Log("Trying syncedlyrics --enhanced first");
                var sw = Stopwatch.StartNew();
                var enhancedLyrics = await _syncedLyricsCliService.GetTimedLyricsAsync(song, true, cancellationToken);
                sw.Stop();
                var hasWords = enhancedLyrics.Any(l => l.Words?.Count > 0);
                Logger.Log($"Enhanced: {enhancedLyrics.Count} lines, hasWords={hasWords} in {sw.ElapsedMilliseconds}ms");
                if (enhancedLyrics.Count > 0 && hasWords)
                {
                    Logger.Log("Using enhanced word-level lyrics");
                    return enhancedLyrics;
                }
                if (enhancedLyrics.Count > 0)
                {
                    Logger.Log("Enhanced no word data, fall through to lrclib");
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Log("Enhanced lookup cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"Enhanced lookup failed: {ex.Message}");
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
            Logger.Log("Trying syncedlyrics without --enhanced");
            var sw = Stopwatch.StartNew();
            var sidecarLyrics = await _syncedLyricsCliService.GetTimedLyricsAsync(song, false, cancellationToken);
            sw.Stop();
            Logger.Log($"syncedlyrics: {sidecarLyrics.Count} lines in {sw.ElapsedMilliseconds}ms");
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
