using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Lyrictified.Models;
using Lyrictified.Services;
using Lyrictified.Settings;

namespace Lyrictified.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan IdleRefreshInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan MaxRefreshInterval = TimeSpan.FromMilliseconds(750);

    private readonly Dispatcher _dispatcher;
    private readonly MediaSessionWatcher _mediaSessionWatcher;
    private readonly CompositeLyricsService _lyricsService;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _playbackClock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly AppSettingsService _appSettingsService = new();

    private IReadOnlyList<LyricLine> _lyrics = Array.Empty<LyricLine>();
    private CancellationTokenSource? _lyricsLoadCts;
    private SongInfo? _currentSong;
    private TimeSpan? _anchoredPlaybackPosition;
    private bool _wasPlaying;
    private bool _isLoadingLyrics;
    private bool _noTimedLyricsFound;
    private bool _isPlaybackPaused;
    private byte[]? _albumArt;
    private string _songTitle = string.Empty;
    private string _songArtist = string.Empty;
    private double _progress;
    private string _windowTitle = "Lyrictified";
    private string _statusText = "Waiting for a song...";
    private string _helperStatusHint = string.Empty;
    private string _currentLine = "Play something to show lyrics here.";
    private string _nextLine = string.Empty;
    private LyricLine? _currentLyricLine;
    private IReadOnlyList<LyricLine> _activeLyricLines = Array.Empty<LyricLine>();
    private int _currentWordIndex;
    private int _currentLineIndex = -1;
    private DateTime? _noLyricsShownAt;
    private bool _wordByWordMode;
    private bool _forceLyricsRefresh;
    private bool _hasTtmlLyrics;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _mediaSessionWatcher = new MediaSessionWatcher();
        _lyricsService = new CompositeLyricsService();
        _helperStatusHint = _lyricsService.HelperStatusHint;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = IdleRefreshInterval
        };
        _timer.Tick += OnTimerTick;
        _mediaSessionWatcher.SongChanged += OnSongChanged;
        _mediaSessionWatcher.DetectedApp += OnDetectedApp;

        var settings = _appSettingsService.Load();
        _wordByWordMode = settings.WordByWordMode;
        _mediaSessionWatcher.UpdateIgnoredAppIds(settings.IgnoredMediaAppIds);
        _lyricsService.SetMaxCacheSize(settings.MaxCacheSize);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string WindowTitle
    {
        get => _windowTitle;
        private set => SetField(ref _windowTitle, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string HelperStatusHint
    {
        get => _helperStatusHint;
        private set => SetField(ref _helperStatusHint, value);
    }

    public string LastSearchInfo => _lyricsService.LastSearchInfo;

    public string CurrentLine
    {
        get => _currentLine;
        private set => SetField(ref _currentLine, value);
    }

    public string NextLine
    {
        get => _nextLine;
        private set => SetField(ref _nextLine, value);
    }

    public bool IsLoadingLyrics
    {
        get => _isLoadingLyrics;
        private set => SetField(ref _isLoadingLyrics, value);
    }

    public bool NoTimedLyricsFound
    {
        get => _noTimedLyricsFound;
        private set => SetField(ref _noTimedLyricsFound, value);
    }

    public bool IsPlaybackPaused
    {
        get => _isPlaybackPaused;
        private set => SetField(ref _isPlaybackPaused, value);
    }

    public byte[]? AlbumArt
    {
        get => _albumArt;
        private set => SetField(ref _albumArt, value);
    }

    public string SongTitle
    {
        get => _songTitle;
        private set => SetField(ref _songTitle, value);
    }

    public string SongArtist
    {
        get => _songArtist;
        private set => SetField(ref _songArtist, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetField(ref _progress, value);
    }

    public TimeSpan? EstimatedPosition => GetEstimatedPlaybackPosition();

    public TimeSpan SongDuration => _currentSong?.Duration ?? TimeSpan.Zero;

    public LyricLine? CurrentLyricLine
    {
        get => _currentLyricLine;
        private set => SetField(ref _currentLyricLine, value);
    }

    public IReadOnlyList<LyricLine> ActiveLyricLines
    {
        get => _activeLyricLines;
        private set => SetField(ref _activeLyricLines, value);
    }

    public int CurrentWordIndex
    {
        get => _currentWordIndex;
        private set => SetField(ref _currentWordIndex, value);
    }

    public bool WordByWordMode => _wordByWordMode;

    public bool HasTtmlLyrics
    {
        get => _hasTtmlLyrics;
        private set => SetField(ref _hasTtmlLyrics, value);
    }

    public IReadOnlyList<LyricLine> Lyrics => _lyrics;

    public int CurrentLineIndex
    {
        get => _currentLineIndex;
        private set => SetField(ref _currentLineIndex, value);
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _mediaSessionWatcher.InitializeAsync();
            _timer.Start();

            var song = await _mediaSessionWatcher.GetCurrentSongAsync();
            await HandleSongAsync(song);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MainViewModel.InitializeAsync failed: {ex}");
            ApplyFatalFallbackState("Unable to initialize media session.");
        }
    }

    public void UpdateSettings(AppSettings settings)
    {
        _wordByWordMode = settings.WordByWordMode;
        _mediaSessionWatcher.UpdateIgnoredAppIds(settings.IgnoredMediaAppIds);
        _lyricsService.SetMaxCacheSize(settings.MaxCacheSize);
        _lyricsService.ForcedSource = settings.DebugForceLyricsSource;
    }

    public async Task ForceLyricsRefreshAsync()
    {
        if (_currentSong is null)
            return;

        _forceLyricsRefresh = true;
        await HandleSongAsync(_currentSong);
    }

    public void ForceNoLyrics()
    {
        if (_currentSong is null)
            return;

        CancelLyricsLoad();
        ResetPlaybackClock();
        _lyrics = Array.Empty<LyricLine>();
        HasTtmlLyrics = false;
        ActiveLyricLines = Array.Empty<LyricLine>();
        CurrentLineIndex = -1;
        OnPropertyChanged(nameof(Lyrics));
        IsLoadingLyrics = false;
        NoTimedLyricsFound = true;
        _noLyricsShownAt = DateTime.UtcNow;
        CurrentLine = "Lyrics not found";
        NextLine = string.Empty;
        StatusText = $"No synced lyrics found for {_currentSong.Artist}";
        SetNextRefreshInterval(IdleRefreshInterval);
    }

    public void ForceSimulateLyrics()
    {
        if (_currentSong is null)
            return;

        CancelLyricsLoad();
        ResetPlaybackClock();
        var testLyrics = new List<LyricLine>();
        for (var i = 1; i <= 100; i++)
        {
            testLyrics.Add(new LyricLine(TimeSpan.FromSeconds(i * 3), $"{i} - Test lyrics"));
        }

        _lyrics = testLyrics;
        HasTtmlLyrics = false;
        ActiveLyricLines = Array.Empty<LyricLine>();
        CurrentLineIndex = -1;
        OnPropertyChanged(nameof(Lyrics));
        IsLoadingLyrics = false;
        NoTimedLyricsFound = false;
        _noLyricsShownAt = null;
        _ = ReanchorPlaybackAsync(_currentSong.IsPlaying);
        _ = UpdateCurrentLineAsync();
    }

    private async void OnSongChanged(object? sender, SongInfo? song)
    {
        try
        {
            if (_dispatcher.CheckAccess())
            {
                await HandleSongAsync(song);
                return;
            }

            await _dispatcher.InvokeAsync(() => HandleSongAsync(song), DispatcherPriority.Normal).Task.Unwrap();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MainViewModel.OnSongChanged failed: {ex}");
            ApplySongFallbackState(song, "Unable to refresh current song.");
        }
    }

    private async Task HandleSongAsync(SongInfo? song)
    {
        try
        {
            var sameSong = IsSameSong(_currentSong, song) && !_forceLyricsRefresh;
            _forceLyricsRefresh = false;
            _currentSong = song;

            if (song is null)
            {
                CancelLyricsLoad();
                ResetPlaybackClock();
                IsLoadingLyrics = false;
                NoTimedLyricsFound = false;
                IsPlaybackPaused = false;
                WindowTitle = "Lyrictified";
                StatusText = "Waiting for a song...";
                CurrentLine = "Play something to show lyrics here.";
                NextLine = string.Empty;
                SongTitle = string.Empty;
                SongArtist = string.Empty;
                AlbumArt = null;
                _lyrics = Array.Empty<LyricLine>();
                HasTtmlLyrics = false;
                ActiveLyricLines = Array.Empty<LyricLine>();
                CurrentLineIndex = -1;
                OnPropertyChanged(nameof(Lyrics));
                SetNextRefreshInterval(IdleRefreshInterval);
                return;
            }

            if (sameSong)
            {
                WindowTitle = song.DisplayTitle;
                SongTitle = song.Title;
                SongArtist = song.Artist;
                if (song.AlbumArt is not null)
                    AlbumArt = song.AlbumArt;
                IsPlaybackPaused = !song.IsPlaying;
                await ReanchorPlaybackAsync(song.IsPlaying);
                await UpdateCurrentLineAsync();
                return;
            }

            CancelLyricsLoad();
            ResetPlaybackClock();
            _lyricsLoadCts = new CancellationTokenSource();
            var cancellationToken = _lyricsLoadCts.Token;

            WindowTitle = song.DisplayTitle;
            StatusText = song.IsPlaying ? $"{song.Artist} is playing" : $"{song.Artist} is paused";
            IsPlaybackPaused = !song.IsPlaying;
            CurrentLine = "Loading synced lyrics...";
            NextLine = string.Empty;
            IsLoadingLyrics = true;
            NoTimedLyricsFound = false;
            _noLyricsShownAt = null;
            _lyrics = Array.Empty<LyricLine>();
            HasTtmlLyrics = false;
            ActiveLyricLines = Array.Empty<LyricLine>();
            CurrentLineIndex = -1;
            OnPropertyChanged(nameof(Lyrics));
            SongTitle = song.Title;
            SongArtist = song.Artist;
            AlbumArt = song.AlbumArt;

            try
            {
                var lyrics = await _lyricsService.GetTimedLyricsAsync(song, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _lyrics = lyrics;
                HasTtmlLyrics = lyrics.Any(line => line.IsTtml);
                OnPropertyChanged(nameof(Lyrics));
                var wordsTotal = lyrics.Count(l => l.Words?.Count > 0);
                Logger.Log($"HandleSongAsync: {lyrics.Count} lines, {wordsTotal} with word data");
                if (_lyrics.Count == 0)
                {
                    IsLoadingLyrics = false;
                    NoTimedLyricsFound = true;
                    CurrentLineIndex = -1;
                    ActiveLyricLines = Array.Empty<LyricLine>();
                    _noLyricsShownAt = DateTime.UtcNow;
                    CurrentLine = "Lyrics not found";
                    NextLine = string.Empty;
                    StatusText = $"No synced lyrics found for {song.Artist}";
                    SetNextRefreshInterval(IdleRefreshInterval);
                    return;
                }

                NoTimedLyricsFound = false;
                IsLoadingLyrics = false;
                await ReanchorPlaybackAsync(song.IsPlaying);
                await UpdateCurrentLineAsync();
            }
            catch (OperationCanceledException)
            {
                IsLoadingLyrics = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainViewModel.HandleSongAsync lyrics fetch failed: {ex}");
                ApplySongFallbackState(song, "Unable to load synced lyrics.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MainViewModel.HandleSongAsync failed: {ex}");
            ApplySongFallbackState(song, "Unable to switch songs.");
        }
    }

    private void OnDetectedApp(object? sender, DetectedMediaAppInfo appInfo)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(appInfo.AppId))
            {
                return;
            }

            var settings = _appSettingsService.Load();
            if (settings.DetectedMediaApps.Any(app => string.Equals(app.AppId, appInfo.AppId, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            settings.DetectedMediaApps.Add(new DetectedMediaApp(appInfo.AppId, appInfo.DisplayName));
            settings.DetectedMediaApps = settings.DetectedMediaApps
                .OrderBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(app => app.AppId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _appSettingsService.Save(settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MainViewModel.OnDetectedApp failed: {ex}");
        }
    }

    private async Task ReanchorPlaybackAsync(bool shouldPlay)
    {
        try
        {
            var position = await _mediaSessionWatcher.GetPlaybackPositionAsync();
            if (position is not null)
            {
                _anchoredPlaybackPosition = position.Value;
            }

            _playbackClock.Reset();
            if (shouldPlay)
            {
                _playbackClock.Start();
            }

            _wasPlaying = shouldPlay;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MainViewModel.ReanchorPlaybackAsync failed: {ex}");
            _playbackClock.Reset();
            _wasPlaying = shouldPlay;
        }
    }

    private void ResetPlaybackClock()
    {
        _anchoredPlaybackPosition = null;
        _playbackClock.Reset();
        _wasPlaying = false;
    }

    private void CancelLyricsLoad()
    {
        try
        {
            _lyricsLoadCts?.Cancel();
            _lyricsLoadCts?.Dispose();
            _lyricsLoadCts = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MainViewModel.CancelLyricsLoad failed: {ex}");
        }
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            await UpdateCurrentLineAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MainViewModel.OnTimerTick failed: {ex}");
            SetNextRefreshInterval(IdleRefreshInterval);
        }
    }

    private async Task UpdateCurrentLineAsync()
    {
        try
        {
            if (_currentSong is null)
            {
                SetNextRefreshInterval(IdleRefreshInterval);
                return;
            }

            if (_lyrics.Count == 0)
            {
                _noLyricsShownAt ??= DateTime.UtcNow;
                var elapsed = DateTime.UtcNow - _noLyricsShownAt.Value;
                CurrentLine = elapsed.TotalSeconds < 5 ? "Lyrics not found" : _currentSong.Title;
                NextLine = string.Empty;
                StatusText = _currentSong.IsPlaying
                    ? $"No synced lyrics found for {_currentSong.Artist}"
                    : $"No synced lyrics found for {_currentSong.Artist} (paused)";
                SetNextRefreshInterval(IdleRefreshInterval);
                return;
            }

            if (!await _refreshGate.WaitAsync(0))
            {
                return;
            }

            try
            {
                if (_currentSong.IsPlaying != _wasPlaying)
                {
                    await ReanchorPlaybackAsync(_currentSong.IsPlaying);
                }

                var position = GetEstimatedPlaybackPosition();
                if (position is null)
                {
                    SetNextRefreshInterval(IdleRefreshInterval);
                    return;
                }

                var currentIndex = FindCurrentLyricIndex(position.Value);
                CurrentLineIndex = currentIndex;
                var activeLyrics = GetActiveLyricLines(position.Value, currentIndex);
                ActiveLyricLines = activeLyrics;
                var currentLyric = activeLyrics.Count > 0
                    ? activeLyrics[^1]
                    : currentIndex >= 0
                        ? _lyrics[currentIndex]
                        : null;

                if (currentLyric is not null)
                {
                    var words = currentLyric.Words;
                    if (WordByWordMode && words is null)
                    {
                        words = EstimateWordInfos(currentLyric, currentIndex, _lyrics);
                    }

                    CurrentLyricLine = words is not null ? currentLyric with { Words = words } : currentLyric;

                    if (WordByWordMode && words is not null)
                    {
                        var wordIdx = FindCurrentWordIndex(words, position.Value);
                        Logger.Log($"wordIdx={wordIdx}/{words.Count}");
                        CurrentWordIndex = wordIdx;
                    }
                    else
                    {
                        CurrentWordIndex = -1;
                    }

                    CurrentLine = activeLyrics.Count > 1
                        ? string.Join(Environment.NewLine, activeLyrics.Select(line => line.Text))
                        : currentLyric.Text;
                }
                else
                {
                    if (_lyrics.Count > 0 && position.Value < _lyrics[0].Timestamp)
                    {
                        var firstLine = _lyrics[0];
                        if (WordByWordMode && firstLine.Words is null)
                        {
                            var estimated = EstimateWordInfos(firstLine, 0, _lyrics);
                            CurrentLyricLine = firstLine with { Words = estimated };
                        }
                        else
                        {
                            CurrentLyricLine = firstLine;
                        }
                        CurrentWordIndex = -1;
                        CurrentLine = _lyrics[0].Text;
                    }
                    else
                    {
                        CurrentLyricLine = null;
                        ActiveLyricLines = Array.Empty<LyricLine>();
                        CurrentWordIndex = -1;
                    }
                }

                var nextIndex = currentIndex >= 0 ? currentIndex + 1 : 1;
                if (nextIndex >= 0 && nextIndex < _lyrics.Count)
                {
                    NextLine = _lyrics[nextIndex].Text;
                }
                else
                {
                    NextLine = string.Empty;
                }

                StatusText = _currentSong.IsPlaying
                    ? $"{_currentSong.Artist} - {position.Value:mm\\:ss}"
                    : $"{_currentSong.Artist} - {position.Value:mm\\:ss} paused";

                if (_currentSong.Duration > TimeSpan.Zero)
                {
                    Progress = Math.Clamp(position.Value.TotalMilliseconds / _currentSong.Duration.TotalMilliseconds, 0, 1);
                }
                else
                {
                    Progress = 0;
                }

                SetNextRefreshInterval(_currentSong.IsPlaying
                    ? GetNextRefreshInterval(position.Value, currentIndex)
                    : IdleRefreshInterval);
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MainViewModel.UpdateCurrentLineAsync failed: {ex}");
            SetNextRefreshInterval(IdleRefreshInterval);
        }
    }

    private TimeSpan? GetEstimatedPlaybackPosition()
    {
        try
        {
            if (_anchoredPlaybackPosition is null)
            {
                return null;
            }

            if (_currentSong is null || !_currentSong.IsPlaying)
            {
                return _anchoredPlaybackPosition.Value;
            }

            return _anchoredPlaybackPosition.Value + _playbackClock.Elapsed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MainViewModel.GetEstimatedPlaybackPosition failed: {ex}");
            return _anchoredPlaybackPosition;
        }
    }

    private int FindCurrentLyricIndex(TimeSpan position)
    {
        var low = 0;
        var high = _lyrics.Count - 1;
        var result = -1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (_lyrics[mid].Timestamp <= position)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private IReadOnlyList<LyricLine> GetActiveLyricLines(TimeSpan position, int currentIndex)
    {
        if (currentIndex < 0)
        {
            return Array.Empty<LyricLine>();
        }

        var active = new List<LyricLine>();
        for (var i = 0; i <= currentIndex; i++)
        {
            var line = _lyrics[i];
            if (line.Timestamp > position)
            {
                continue;
            }

            if (i == currentIndex || (line.EndTime is { } endTime && endTime > position))
            {
                active.Add(line);
            }
        }

        return active;
    }

    internal static int FindCurrentWordIndex(IReadOnlyList<WordInfo> words, TimeSpan position)
    {
        if (words.Count == 0)
            return -1;

        var result = -1;
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].Timestamp <= position)
            {
                result = i;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private static IReadOnlyList<WordInfo> EstimateWordInfos(LyricLine line, int lineIndex, IReadOnlyList<LyricLine> allLines)
    {
        var split = line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (split.Length == 0) return Array.Empty<WordInfo>();

        var nextTimestamp = lineIndex + 1 < allLines.Count
            ? allLines[lineIndex + 1].Timestamp
            : line.Timestamp + TimeSpan.FromSeconds(4);

        var totalGap = nextTimestamp - line.Timestamp;
        if (totalGap <= TimeSpan.Zero)
            totalGap = TimeSpan.FromSeconds(4);

        var maxHighlightDuration = TimeSpan.FromSeconds(Math.Min(4.0, totalGap.TotalSeconds));
        var visualHighlightDuration = totalGap > maxHighlightDuration ? maxHighlightDuration : totalGap;
        var wordDuration = visualHighlightDuration / split.Length;
        var infos = new WordInfo[split.Length];
        for (var i = 0; i < split.Length; i++)
        {
            infos[i] = new WordInfo(line.Timestamp + wordDuration * i, split[i]);
        }

        return infos;
    }

    private TimeSpan GetNextRefreshInterval(TimeSpan position, int currentIndex)
    {
        var nextIndex = currentIndex + 1;
        if (nextIndex < 0 || nextIndex >= _lyrics.Count)
        {
            return IdleRefreshInterval;
        }

        var remaining = _lyrics[nextIndex].Timestamp - position;
        if (remaining <= TimeSpan.Zero)
        {
            return MinRefreshInterval;
        }

        if (remaining < MinRefreshInterval)
        {
            return MinRefreshInterval;
        }

        if (remaining > MaxRefreshInterval)
        {
            return MaxRefreshInterval;
        }

        return remaining;
    }

    private void SetNextRefreshInterval(TimeSpan interval)
    {
        _timer.Interval = interval;
    }

    private void ApplyFatalFallbackState(string statusText)
    {
        CancelLyricsLoad();
        ResetPlaybackClock();
        _lyrics = Array.Empty<LyricLine>();
        HasTtmlLyrics = false;
        ActiveLyricLines = Array.Empty<LyricLine>();
        CurrentLineIndex = -1;
        OnPropertyChanged(nameof(Lyrics));
        _currentSong = null;
        IsLoadingLyrics = false;
        NoTimedLyricsFound = false;
        _noLyricsShownAt = null;
        IsPlaybackPaused = false;
        WindowTitle = "Lyrictified";
        StatusText = statusText;
        CurrentLine = "Play something to show lyrics here.";
        NextLine = string.Empty;
        SetNextRefreshInterval(IdleRefreshInterval);
    }

    private void ApplySongFallbackState(SongInfo? song, string statusText)
    {
        ResetPlaybackClock();
        _lyrics = Array.Empty<LyricLine>();
        HasTtmlLyrics = false;
        ActiveLyricLines = Array.Empty<LyricLine>();
        CurrentLineIndex = -1;
        OnPropertyChanged(nameof(Lyrics));
        IsLoadingLyrics = false;
        NoTimedLyricsFound = song is not null;
        _noLyricsShownAt = song is not null ? DateTime.UtcNow : null;
        IsPlaybackPaused = song is not null && !song.IsPlaying;
        WindowTitle = song?.DisplayTitle ?? "Lyrictified";
        StatusText = statusText;
        CurrentLine = song is not null ? "Lyrics not found" : "Play something to show lyrics here.";
        NextLine = string.Empty;
        SetNextRefreshInterval(IdleRefreshInterval);
    }

    private static bool IsSameSong(SongInfo? left, SongInfo? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(left.Title, right.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Artist, right.Artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Album, right.Album, StringComparison.OrdinalIgnoreCase);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public async Task TogglePlayPauseAsync()
    {
        await _mediaSessionWatcher.TogglePlayPauseAsync();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _mediaSessionWatcher.SongChanged -= OnSongChanged;
        _mediaSessionWatcher.DetectedApp -= OnDetectedApp;
        CancelLyricsLoad();
        _refreshGate.Dispose();
        _mediaSessionWatcher.Dispose();
        _lyricsService.Dispose();
    }
}
