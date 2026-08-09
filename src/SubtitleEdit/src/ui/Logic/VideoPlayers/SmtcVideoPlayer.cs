using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.Logic.VideoPlayers.LibMpvDynamic;
using Windows.Media.Control;

namespace Nikse.SubtitleEdit.Logic.VideoPlayers;

/// <summary>
/// An IVideoPlayer implementation that wraps the Windows System Media Transport Controls (SMTC).
/// Position is interpolated between SMTC reports using a Stopwatch for smooth 100ms updates.
/// </summary>
public sealed class SmtcVideoPlayer : IVideoPlayer
{
    private GlobalSystemMediaTransportControlsSession? _smtcSession;
    private GlobalSystemMediaTransportControlsSessionManager? _smtcManager;
    private readonly SynchronizationContext _syncContext;
    private double _position;
    private double _duration;
    private bool _isPlaying;
    private bool _isPaused = true;
    private bool _isLoaded;
    private Timer? _pollTimer;
    private readonly Stopwatch _positionClock = new();
    private double _anchoredPosition;
    private bool _wasPlaying;

    public SmtcVideoPlayer()
    {
        _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _smtcManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _smtcManager.CurrentSessionChanged += OnCurrentSessionChanged;

            AttachSession(_smtcManager.GetCurrentSession());

            _isLoaded = true;

            _pollTimer = new Timer(
                callback: _ => PollPosition(),
                state: null,
                dueTime: TimeSpan.Zero,
                period: TimeSpan.FromMilliseconds(100));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SmtcVideoPlayer.InitializeAsync failed: {ex}");
        }
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null)
        {
            var allSessions = _smtcManager?.GetSessions();
            if (allSessions != null)
            {
                session = allSessions
                    .OrderByDescending(s =>
                    {
                        try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
                        catch { return false; }
                    })
                    .FirstOrDefault();
            }
        }

        _smtcSession = session;
        if (_smtcSession != null)
        {
            ReanchorPlayback();
        }
    }

    private void ReanchorPlayback()
    {
        if (_smtcSession == null) return;

        try
        {
            var timeline = _smtcSession.GetTimelineProperties();
            var playbackInfo = _smtcSession.GetPlaybackInfo();

            _anchoredPosition = timeline.Position.TotalSeconds;
            _duration = timeline.EndTime > timeline.StartTime
                ? (timeline.EndTime - timeline.StartTime).TotalSeconds
                : 0;

            var isNowPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _positionClock.Restart();
            _wasPlaying = isNowPlaying;
            _isPlaying = isNowPlaying;
            _isPaused = !isNowPlaying;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SmtcVideoPlayer.ReanchorPlayback failed: {ex}");
        }
    }

    public string Name => "SMTC";
    public string FileName => string.Empty;
    public bool CanLoad() => true;

    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _isPaused;

    public double Position
    {
        get => _position;
        set { }
    }

    public double Duration => _duration;

    public int VolumeMaximum => 100;
    public double Volume { get; set; } = 100;

    public double Speed { get; set; } = 1.0;

    public event EventHandler<EventArgs>? MediaEnded;

    public Task LoadFile(string fileName, double startPositionSeconds = 0)
    {
        return Task.CompletedTask;
    }

    public void CloseFile()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;

        if (_smtcManager != null)
        {
            _smtcManager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }

        _smtcSession = null;
        _smtcManager = null;
        _isLoaded = false;
        _isPlaying = false;
        _isPaused = true;
        _position = 0;
        _duration = 0;
    }

    public void Play()
    {
        if (_smtcSession == null) return;
        try
        {
            _smtcSession.TryPlayAsync().AsTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SmtcVideoPlayer.Play failed: {ex}");
        }
    }

    public void Pause()
    {
        if (_smtcSession == null) return;
        try
        {
            _smtcSession.TryPauseAsync().AsTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SmtcVideoPlayer.Pause failed: {ex}");
        }
    }

    public void PlayOrPause()
    {
        if (_isPlaying) Pause(); else Play();
    }

    public void Stop()
    {
        Pause();
    }

    public AudioTrackInfo? ToggleAudioTrack()
    {
        return null;
    }

    private void PollPosition()
    {
        try
        {
            if (_smtcSession == null)
            {
                AttachSession(null);
                return;
            }

            var timeline = _smtcSession.GetTimelineProperties();
            var playbackInfo = _smtcSession.GetPlaybackInfo();

            var smtcPosition = timeline.Position.TotalSeconds;
            var newDuration = timeline.EndTime > timeline.StartTime
                ? (timeline.EndTime - timeline.StartTime).TotalSeconds
                : 0;
            var isNowPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            if (Math.Abs(smtcPosition - _anchoredPosition) > 0.5 || isNowPlaying != _wasPlaying)
            {
                _anchoredPosition = smtcPosition;
                _positionClock.Restart();
                _wasPlaying = isNowPlaying;
            }

            if (isNowPlaying)
            {
                _position = _anchoredPosition + _positionClock.Elapsed.TotalSeconds;
            }
            else
            {
                _position = _anchoredPosition;
            }

            _duration = newDuration;
            _isPlaying = isNowPlaying;
            _isPaused = !isNowPlaying;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SmtcVideoPlayer.PollPosition failed: {ex}");
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        try
        {
            _syncContext.Post(_ =>
            {
                AttachSession(sender.GetCurrentSession());
            }, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SmtcVideoPlayer.OnCurrentSessionChanged failed: {ex}");
        }
    }

    public void Dispose()
    {
        CloseFile();
    }
}
