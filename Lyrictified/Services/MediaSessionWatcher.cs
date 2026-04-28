using Windows.Media.Control;
using Lyrictified.Models;

namespace Lyrictified.Services;

public sealed class MediaSessionWatcher : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    public event EventHandler<SongInfo?>? SongChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            await SwitchToSessionAsync(_manager.GetCurrentSession());
        }
        catch
        {
            Dispose();
            throw;
        }
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
            await SwitchToSessionAsync(sender.GetCurrentSession());
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
            await SwitchToSessionAsync(sender.GetCurrentSession());
        }
        catch
        {
            RaiseSongChanged(null);
        }
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
