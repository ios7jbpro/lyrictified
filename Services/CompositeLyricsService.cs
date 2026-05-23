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

    public async Task<IReadOnlyList<LyricLine>> GetTimedLyricsAsync(SongInfo song, CancellationToken cancellationToken)
    {
        try
        {
            var lrclibLyrics = await _lrcLibLyricsService.GetLyricsAsync(song, cancellationToken);
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
            Debug.WriteLine($"CompositeLyricsService lrclib lookup failed: {ex}");
        }

        try
        {
            var sidecarLyrics = await _syncedLyricsCliService.GetTimedLyricsAsync(song, cancellationToken);
            return sidecarLyrics.Count > 0 ? sidecarLyrics : Array.Empty<LyricLine>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CompositeLyricsService syncedlyrics lookup failed: {ex}");
            return Array.Empty<LyricLine>();
        }
    }

    public void Dispose()
    {
        _lrcLibLyricsService.Dispose();
    }
}
