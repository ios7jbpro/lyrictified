using System.IO;
using Windows.Media.Control;
using Lyrictified.Models;

namespace Lyrictified.Services;

public sealed class MediaSessionWatcher : IDisposable
{
    private readonly HashSet<string> _ignoredAppIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedAppIds = new(StringComparer.OrdinalIgnoreCase);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    public event EventHandler<SongInfo?>? SongChanged;
    public event EventHandler<DetectedMediaAppInfo>? DetectedApp;

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            await RefreshSessionsAsync(_manager.GetCurrentSession());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void UpdateIgnoredAppIds(IEnumerable<string>? ignoredAppIds)
    {
        _ignoredAppIds.Clear();
        if (ignoredAppIds is not null)
        {
            foreach (var appId in ignoredAppIds)
            {
                if (!string.IsNullOrWhiteSpace(appId))
                {
                    _ignoredAppIds.Add(appId);
                }
            }
        }

        _ = RefreshSessionsAsync(_manager?.GetCurrentSession());
    }

    public Task<TimeSpan?> GetPlaybackPositionAsync()
    {
        try
        {
            if (_currentSession is null)
            {
                return Task.FromResult<TimeSpan?>(null);
            }

            var timeline = _currentSession.GetTimelineProperties();
            return Task.FromResult<TimeSpan?>(timeline.Position);
        }
        catch
        {
            return Task.FromResult<TimeSpan?>(null);
        }
    }

    public async Task<SongInfo?> GetCurrentSongAsync()
    {
        if (_currentSession is null)
        {
            return null;
        }

        return await ReadSongSafeAsync(_currentSession);
    }

    private async void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        try
        {
            await RefreshSessionsAsync(sender.GetCurrentSession());
        }
        catch
        {
            RaiseSongChanged(null);
        }
    }

    private async void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        try
        {
            await RefreshSessionsAsync(sender.GetCurrentSession());
        }
        catch
        {
            RaiseSongChanged(null);
        }
    }

    private async Task RefreshSessionsAsync(GlobalSystemMediaTransportControlsSession? preferredSession)
    {
        var sessions = _manager?.GetSessions()?.ToList() ?? new List<GlobalSystemMediaTransportControlsSession>();
        foreach (var session in sessions)
        {
            ReportDetectedApp(session);
        }

        var nextSession = SelectSession(preferredSession, sessions);
        await SwitchToSessionAsync(nextSession);
    }

    private async Task SwitchToSessionAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_currentSession, session))
        {
            return;
        }

        if (_currentSession is not null)
        {
            _currentSession.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged -= OnSessionTimelinePropertiesChanged;
        }

        _currentSession = session;

        if (_currentSession is not null)
        {
            _currentSession.MediaPropertiesChanged += OnSessionMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += OnSessionPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged += OnSessionTimelinePropertiesChanged;
        }

        RaiseSongChanged(await GetCurrentSongAsync());
    }

    private async void OnSessionMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        RaiseSongChanged(await ReadSongSafeAsync(sender));
    }

    private async void OnSessionPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        RaiseSongChanged(await ReadSongSafeAsync(sender));
    }

    private async void OnSessionTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        RaiseSongChanged(await ReadSongSafeAsync(sender));
    }

    private void RaiseSongChanged(SongInfo? song)
    {
        try
        {
            SongChanged?.Invoke(this, song);
        }
        catch
        {
        }
    }

    private void ReportDetectedApp(GlobalSystemMediaTransportControlsSession session)
    {
        var appId = GetAppId(session);
        if (string.IsNullOrWhiteSpace(appId) || !_reportedAppIds.Add(appId))
        {
            return;
        }

        try
        {
            DetectedApp?.Invoke(this, new DetectedMediaAppInfo(appId, CreateDisplayName(appId)));
        }
        catch
        {
        }
    }

    private GlobalSystemMediaTransportControlsSession? SelectSession(
        GlobalSystemMediaTransportControlsSession? preferredSession,
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        if (preferredSession is not null && !IsIgnored(preferredSession))
        {
            return preferredSession;
        }

        return sessions
            .Where(session => !IsIgnored(session))
            .OrderByDescending(session => session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            .FirstOrDefault();
    }

    private bool IsIgnored(GlobalSystemMediaTransportControlsSession session)
    {
        var appId = GetAppId(session);
        return !string.IsNullOrWhiteSpace(appId) && _ignoredAppIds.Contains(appId);
    }

    private static string GetAppId(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.SourceAppUserModelId ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CreateDisplayName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return "Unknown app";
        }

        var trimmed = appId.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(trimmed);
        }

        if (trimmed.Contains('!'))
        {
            var segments = trimmed.Split('!', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 0)
            {
                return segments[^1];
            }
        }

        if (trimmed.Contains('\\') || trimmed.Contains('/'))
        {
            var fileName = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(fileName)
                    : fileName;
            }
        }

        if (trimmed.Contains('.'))
        {
            var tokens = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length > 0)
            {
                return tokens[^1];
            }
        }

        return trimmed;
    }

    private static async Task<SongInfo?> ReadSongSafeAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return await ReadSongAsync(session);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<SongInfo?> ReadSongAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var properties = await session.TryGetMediaPropertiesAsync();
        if (properties is null)
        {
            return null;
        }

        var playbackInfo = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();

        return new SongInfo(
            string.IsNullOrWhiteSpace(properties.Title) ? "Unknown title" : properties.Title,
            string.IsNullOrWhiteSpace(properties.Artist) ? "Unknown artist" : properties.Artist,
            properties.AlbumTitle,
            timeline.EndTime,
            playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
    }

    public void Dispose()
    {
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }

        if (_currentSession is not null)
        {
            _currentSession.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged -= OnSessionTimelinePropertiesChanged;
        }
    }
}
