using Microsoft.Win32;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using Lyrictified.DisplayModes;
using Lyrictified.Interop;
using Lyrictified.Models;
using Lyrictified.Settings;
using Lyrictified.ViewModels;
using WpfBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfApplication = System.Windows.Application;

namespace Lyrictified;

public partial class WallpaperWindow : Window, ITrayIconHost
{
    private static readonly TimeSpan MonitorWarningDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan EmptyLineHideDelay = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan EmptyLineUpcomingLyricGrace = TimeSpan.FromMilliseconds(1600);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _monitorWarningTimer;
    private readonly DispatcherTimer _timeoutTimer;
    private readonly AppSettingsService _appSettingsService;
    private AppBarManager? _appBarManager;
    private HwndSource? _hwndSource;
    private AppSettings _settings;
    private string _displayedLyricText = string.Empty;
    private string? _lastMonitorWarningKey;
    private int _lyricTransitionVersion;
    private bool _isWallpaperContentVisible = true;
    private bool _isTimeoutHidden;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _trayIcon;
    private static BitmapImage? _baseFlashImage;
    private static BitmapImage? _flashImage;
    private static MediaColor? _appliedFlashColor;
    private static readonly Random FlashRandom = new();
    private const double SlideWordOffset = 48;
    private const int SlideWordDurationMs = 130;
    private const int SlideWordInDurationMs = 100;
    private const int SlideWordStaggerMs = 75;
    private const int FadeLineOutDurationMs = 160;
    private const int FlashIconSize = 12;
    private const int FlashIconSizeSingle = 18;
    private const int FlashIconSizeTriple = 9;
    private const int FlashInMs = 90;
    private const int FlashOutMs = 150;
    private const int FlashStaggerMs = 90;
    private const int FlashPopLeadMs = 45;
    private const double FlashLayoutBox = 2;
    private const double FlashGlowScale = 24;
    private const double FlashGlowMaxOpacity = 0.45;

    public WallpaperWindow()
    {
        InitializeComponent();
        _appSettingsService = new AppSettingsService();
        _settings = _appSettingsService.Load();

        _viewModel = new MainViewModel();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = _viewModel;

        _baseFlashImage = LoadFlashImage();
        _flashImage = _baseFlashImage;

        _monitorWarningTimer = new DispatcherTimer { Interval = MonitorWarningDuration };
        _monitorWarningTimer.Tick += MonitorWarningTimer_OnTick;
        _timeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.WallpaperTimeout) };
        _timeoutTimer.Tick += TimeoutTimer_OnTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _appBarManager = new AppBarManager(this, WallpaperDisplayMode.WindowHeight);
        ApplyClickThroughWindowStyle();
        AttachToWallpaperLayer();
        ApplyMonitorSetting();
        PositionWindow();
        ApplyAppearance();
        ApplyWallpaperTextAlignment();
        WorkspaceVisibilityManager.PinToAllWorkspaces(this);
        _trayIcon = new TrayIcon(this);

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        IncomingLyricTextBlock.Text = _viewModel.TaskbarCurrentLine;
        _displayedLyricText = _viewModel.TaskbarCurrentLine;
        ApplyWallpaperTextAlignment();
        UpdateWallpaperContentVisibility(_displayedLyricText);
        await _viewModel.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _monitorWarningTimer.Stop();
        _monitorWarningTimer.Tick -= MonitorWarningTimer_OnTick;
        _timeoutTimer.Stop();
        _timeoutTimer.Tick -= TimeoutTimer_OnTick;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settingsWindow?.Close();
        _appBarManager?.Dispose();
        _trayIcon?.Dispose();
        _viewModel.Dispose();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _appBarManager?.RefreshMonitors();
        ApplyMonitorSetting();
        PositionWindow();
        RefreshSettingsWindowOptions();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        ApplyAppearance();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LastSearchInfo))
        {
            _settingsWindow?.UpdateLastSearchInfo(_viewModel.LastSearchInfo);
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.TaskbarCurrentLine))
        {
            if (Dispatcher.CheckAccess())
            {
                HandleCurrentLineChanged(_viewModel.TaskbarCurrentLine);
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() => HandleCurrentLineChanged(_viewModel.TaskbarCurrentLine));
            }
        }

        if (e.PropertyName == nameof(MainViewModel.IsPlaybackPaused))
        {
            if (Dispatcher.CheckAccess())
            {
                HandlePlaybackPausedChanged(_viewModel.IsPlaybackPaused);
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() => HandlePlaybackPausedChanged(_viewModel.IsPlaybackPaused));
            }
        }

        if (e.PropertyName == nameof(MainViewModel.TaskbarCurrentLine))
        {
            if (_isTimeoutHidden)
            {
                _isTimeoutHidden = false;
                ApplyTargetWallpaperOpacity();
            }
            _timeoutTimer.Stop();
        }
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 1;
            }
        }
        catch
        {
            // Ignore registry read failures and default to dark theme behaviour.
        }
        return false;
    }

    private void ApplyAppearance()
    {
        Background = WpfBrushes.Transparent;
        RootGrid.Background = IsSettingsWindowVisible()
            ? new SolidColorBrush(MediaColor.FromArgb(180, 10, 14, 20))
            : new SolidColorBrush(MediaColor.FromArgb(0, 0, 0, 0));
        var lyricColor = GetCustomLyricColor();
        ApplyFlashColor(lyricColor);
        var lyricBrush = new SolidColorBrush(lyricColor);
        IncomingLyricTextBlock.Foreground = lyricBrush;
        OutgoingLyricTextBlock.Foreground = lyricBrush;
    }

    private MediaColor GetCustomLyricColor()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_settings.WallpaperTextColor)
                && System.Windows.Media.ColorConverter.ConvertFromString(_settings.WallpaperTextColor) is MediaColor color)
            {
                return color;
            }
        }
        catch
        {
            // Fall back to the default lyric color on invalid hex values.
        }

        return MediaColor.FromRgb(245, 247, 250);
    }

    private void ApplyFlashColor(MediaColor color)
    {
        if (_appliedFlashColor is MediaColor appliedColor && appliedColor == color)
        {
            return;
        }

        _flashImage = _baseFlashImage is null ? null : CreateTintedFlashImage(_baseFlashImage, color);
        _appliedFlashColor = color;
    }

    private void ApplyMonitorSetting()
    {
        if (_appBarManager is null)
        {
            return;
        }

        var preferredMonitorDeviceName = _settings.WallpaperPreferredMonitorDeviceName ?? _settings.PreferredMonitorDeviceName;

        if (string.IsNullOrWhiteSpace(preferredMonitorDeviceName))
        {
            if (_appBarManager.CurrentMonitorDeviceName is not null)
            {
                _settings.WallpaperPreferredMonitorDeviceName = _appBarManager.CurrentMonitorDeviceName;
                _appSettingsService.Save(_settings);
            }

            _lastMonitorWarningKey = null;
            return;
        }

        if (_appBarManager.SetCurrentMonitor(preferredMonitorDeviceName))
        {
            _lastMonitorWarningKey = null;
            return;
        }

        if (_appBarManager.SetCurrentMonitorToPrimary())
        {
            var currentDeviceName = _appBarManager.CurrentMonitorDeviceName ?? "primary display";
            var warningKey = $"{preferredMonitorDeviceName}|{currentDeviceName}";
            if (!string.Equals(_lastMonitorWarningKey, warningKey, StringComparison.Ordinal))
            {
                ShowMonitorWarning($"Saved display not detected. Falling back to primary display ({currentDeviceName}).");
                _lastMonitorWarningKey = warningKey;
            }
        }
    }

    private void PositionWindow()
    {
        if (_appBarManager is null || _appBarManager.Monitors.Count == 0)
        {
            return;
        }

        var monitor = _appBarManager.Monitors[Math.Clamp(_appBarManager.CurrentMonitorIndex, 0, _appBarManager.Monitors.Count - 1)];
        var bounds = WallpaperDisplayMode.GetWindowBounds(
            monitor,
            _settings.WallpaperMaximumWidth,
            GetEffectiveWallpaperScale(),
            _settings.WallpaperContainerHeight,
            _settings.WallpaperHorizontalAlignment,
            _settings.WallpaperVerticalAlignment,
            _settings.WallpaperCustomX,
            _settings.WallpaperCustomY);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        ApplyWallpaperScale();
    }

    private void ApplyWallpaperScale()
    {
        var scale = GetEffectiveWallpaperScale();
        WallpaperLayerScaleTransform.ScaleX = scale;
        WallpaperLayerScaleTransform.ScaleY = scale;
    }

    private void ApplyWallpaperTextAlignment()
    {
        var horizontal = _settings.WallpaperTextHorizontalAlignment switch
        {
            WallpaperTextHorizontalAlignment.Left => System.Windows.HorizontalAlignment.Left,
            WallpaperTextHorizontalAlignment.Right => System.Windows.HorizontalAlignment.Right,
            _ => System.Windows.HorizontalAlignment.Center
        };
        var vertical = _settings.WallpaperTextVerticalAlignment switch
        {
            WallpaperTextVerticalAlignment.Top => System.Windows.VerticalAlignment.Top,
            WallpaperTextVerticalAlignment.Bottom => System.Windows.VerticalAlignment.Bottom,
            _ => System.Windows.VerticalAlignment.Center
        };
        var textAlignment = horizontal switch
        {
            System.Windows.HorizontalAlignment.Left => TextAlignment.Left,
            System.Windows.HorizontalAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Center
        };
        var verticalMargin = vertical == System.Windows.VerticalAlignment.Center ? 0.0 : 8.0;
        LyricStage.Margin = new Thickness(18, verticalMargin, 18, verticalMargin);

        IncomingWordsPanel.HorizontalAlignment = horizontal;
        OutgoingWordsPanel.HorizontalAlignment = horizontal;
        IncomingWordsPanel.VerticalAlignment = vertical;
        OutgoingWordsPanel.VerticalAlignment = vertical;

        // Keep the lyric elements sized to the stage.  Applying Left/Right to
        // the element itself makes WPF measure it at its content width, so a
        // long line is positioned by its centre/edge and then clipped instead
        // of being aligned inside the configured container.
        IncomingLyricTextBlock.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        OutgoingLyricTextBlock.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        LyricStage.ClipToBounds = true;
        IncomingLyricTextBlock.TextAlignment = textAlignment;
        OutgoingLyricTextBlock.TextAlignment = textAlignment;
        IncomingLyricTextBlock.VerticalAlignment = vertical;
        OutgoingLyricTextBlock.VerticalAlignment = vertical;
    }

    private double GetEffectiveWallpaperScale()
    {
        return WallpaperDisplayMode.GetEffectiveScale(_settings.WallpaperScale);
    }

    private void AttachToWallpaperLayer()
    {
        var hwnd = _hwndSource?.Handle ?? new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var workerW = FindWallpaperWorkerW();
        if (workerW != IntPtr.Zero)
        {
            _ = SetParent(hwnd, workerW);
        }
    }

    private static IntPtr FindWallpaperWorkerW()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        _ = SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), IntPtr.Zero, 0x0002, 1000, out _);

        IntPtr workerW = IntPtr.Zero;
        while (true)
        {
            workerW = FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null);
            if (workerW == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            if (FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                return FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null);
            }
        }
    }

    private async void HandleCurrentLineChanged(string newCurrentLine)
    {
        if (string.Equals(_displayedLyricText, newCurrentLine, StringComparison.Ordinal))
        {
            return;
        }

        if (_viewModel.Lyrics.Count == 0)
        {
            ++_lyricTransitionVersion;
            CancelLyricTransitionAnimations();
            _displayedLyricText = string.Empty;
            RestoreLyricTextBlocks(string.Empty);
            await SetWallpaperContentVisibleAsync(false);
            return;
        }

        var transitionVersion = ++_lyricTransitionVersion;
        CancelLyricTransitionAnimations();

        if (_settings.WallpaperAnimationMode == IslandAnimationMode.SlideIn || _settings.WallpaperAnimationMode == IslandAnimationMode.SlideInManual)
        {
            await HandleCurrentLineChangedSlideIn(newCurrentLine, transitionVersion);
            return;
        }

        if (_settings.WallpaperAnimationMode == IslandAnimationMode.WordFade)
        {
            await HandleCurrentLineChangedWordFade(newCurrentLine, transitionVersion);
            return;
        }

        if (_settings.WallpaperAnimationMode == IslandAnimationMode.FlashIn
            || _settings.WallpaperAnimationMode == IslandAnimationMode.FlashInRandom)
        {
            await HandleCurrentLineChangedFlashIn(newCurrentLine, transitionVersion);
            return;
        }

        OutgoingLyricTextBlock.Text = _displayedLyricText;
        OutgoingLyricTextBlock.Opacity = string.IsNullOrWhiteSpace(_displayedLyricText) ? 0 : 1;
        OutgoingLyricTranslateTransform.Y = 0;

        IncomingLyricTextBlock.Text = string.Empty;
        IncomingLyricTextBlock.Opacity = 0;
        IncomingLyricTranslateTransform.Y = 0;

        if (!string.IsNullOrWhiteSpace(_displayedLyricText))
        {
            await AnimateDoubleAsync(
                OutgoingLyricTextBlock,
                OpacityProperty,
                new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(160),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                });

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }
        }

        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        OutgoingLyricTextBlock.Opacity = 0;
        OutgoingLyricTextBlock.Text = string.Empty;

        if (string.IsNullOrWhiteSpace(newCurrentLine))
        {
            _displayedLyricText = string.Empty;

            if (!ShouldHideForEmptyLine())
            {
                return;
            }

            await Task.Delay(EmptyLineHideDelay);

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }

            await SetWallpaperContentVisibleAsync(false);
            return;
        }

        if (!_isWallpaperContentVisible)
        {
            await SetWallpaperContentVisibleAsync(true);

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }
        }

        if (transitionVersion != _lyricTransitionVersion)
        {
            return;
        }

        IncomingLyricTextBlock.Text = newCurrentLine;
        IncomingLyricTextBlock.Opacity = 0;

        await AnimateDoubleAsync(
            IncomingLyricTextBlock,
            OpacityProperty,
            new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(190),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });

        if (transitionVersion != _lyricTransitionVersion)
        {
            return;
        }

        _displayedLyricText = newCurrentLine;
    }

    private double CalculateLocalLyricTempoMultiplier(string incomingLine, string outgoingLine)
    {
        var lyrics = _viewModel.Lyrics;
        if (lyrics.Count < 2)
        {
            return 1.0;
        }

        var anchorText = string.IsNullOrWhiteSpace(incomingLine) ? outgoingLine : incomingLine;
        var anchorIndex = FindLineIndexClosestToPosition(lyrics, anchorText);
        if (anchorIndex < 0)
        {
            return 1.0;
        }

        var afterIndex = FindAdjacentNonEmptyLineIndex(lyrics, anchorIndex, 1);
        if (afterIndex < 0)
        {
            return 1.0;
        }

        var gap = (lyrics[afterIndex].Timestamp - lyrics[anchorIndex].Timestamp).TotalSeconds;
        if (gap <= 0.2)
        {
            return 1.0;
        }

        return Math.Clamp(3.0 / gap, 0.5, 2.0);
    }

    private int FindLineIndexClosestToPosition(IReadOnlyList<LyricLine> lyrics, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return -1;
        }

        var position = _viewModel.EstimatedPosition;
        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < lyrics.Count; i++)
        {
            if (lyrics[i].IsBackground || !string.Equals(lyrics[i].Text, text, StringComparison.Ordinal))
            {
                continue;
            }

            var distance = position is null
                ? 0
                : Math.Abs((lyrics[i].Timestamp - position.Value).TotalSeconds);
            if (distance < bestDistance)
            {
                bestIndex = i;
                bestDistance = distance;
            }
        }

        return bestIndex;
    }

    private static int FindAdjacentNonEmptyLineIndex(IReadOnlyList<LyricLine> lyrics, int anchorIndex, int direction)
    {
        for (var i = anchorIndex + direction; i >= 0 && i < lyrics.Count; i += direction)
        {
            if (!lyrics[i].IsBackground && !string.IsNullOrWhiteSpace(lyrics[i].Text))
            {
                return i;
            }
        }

        return -1;
    }

    private async Task HandleCurrentLineChangedSlideIn(string newCurrentLine, int transitionVersion)
    {
        var outgoingText = _displayedLyricText;
        var hasOutgoing = !string.IsNullOrWhiteSpace(outgoingText);
        var hasIncoming = !string.IsNullOrWhiteSpace(newCurrentLine);
        var tempo = _settings.WallpaperAnimationMode == IslandAnimationMode.SlideInManual
            ? _settings.WallpaperAnimationManualSpeed
            : CalculateLocalLyricTempoMultiplier(newCurrentLine, outgoingText);
        var outDuration = (int)(SlideWordDurationMs / tempo);
        var inDuration = (int)(SlideWordInDurationMs / tempo);
        var stagger = (int)(SlideWordStaggerMs / tempo);

        if (!hasIncoming)
        {
            if (!hasOutgoing)
            {
                _displayedLyricText = string.Empty;
                RestoreLyricTextBlocks(string.Empty);
                return;
            }

            SetLyricWordPanelsActive(true);
            PopulateWordPanel(OutgoingWordsPanel, outgoingText);
            OutgoingWordsPanel.Visibility = Visibility.Visible;
            IncomingWordsPanel.Visibility = Visibility.Collapsed;
            ClearWordPanel(IncomingWordsPanel);

            await AnimateSlideWordsAsync(OutgoingWordsPanel, slideOut: true, transitionVersion, outDuration, stagger);

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }

            _displayedLyricText = string.Empty;

            if (!ShouldHideForEmptyLine())
            {
                RestoreLyricTextBlocks(string.Empty);
                return;
            }

            await Task.Delay(EmptyLineHideDelay);

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }

            RestoreLyricTextBlocks(string.Empty);
            await SetWallpaperContentVisibleAsync(false);
            return;
        }

        SetLyricWordPanelsActive(true);

        if (hasOutgoing)
        {
            PopulateWordPanel(OutgoingWordsPanel, outgoingText);
            OutgoingWordsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ClearWordPanel(OutgoingWordsPanel);
            OutgoingWordsPanel.Visibility = Visibility.Collapsed;
        }

        IncomingWordsPanel.Visibility = Visibility.Collapsed;
        ClearWordPanel(IncomingWordsPanel);

        if (!_isWallpaperContentVisible)
        {
            await SetWallpaperContentVisibleAsync(true);

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }
        }

        // 1. Slide out outgoing words
        if (hasOutgoing)
        {
            await AnimateSlideWordsAsync(OutgoingWordsPanel, slideOut: true, transitionVersion, outDuration, stagger);

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }

            ClearWordPanel(OutgoingWordsPanel);
            OutgoingWordsPanel.Visibility = Visibility.Collapsed;
        }

        // 3. Slide in new words (slightly faster)
        PopulateWordPanel(IncomingWordsPanel, newCurrentLine, initialYOffset: SlideWordOffset);
        IncomingWordsPanel.Visibility = Visibility.Visible;

        await AnimateSlideWordsAsync(IncomingWordsPanel, slideOut: false, transitionVersion, inDuration, stagger);

        if (transitionVersion != _lyricTransitionVersion)
        {
            return;
        }

        RestoreLyricTextBlocks(newCurrentLine);
        _displayedLyricText = newCurrentLine;
    }

    private async Task HandleCurrentLineChangedWordFade(string newCurrentLine, int transitionVersion)
    {
        var outgoingText = _displayedLyricText;
        var hasOutgoing = !string.IsNullOrWhiteSpace(outgoingText);
        var hasIncoming = !string.IsNullOrWhiteSpace(newCurrentLine);
        var tempo = CalculateLocalLyricTempoMultiplier(newCurrentLine, outgoingText);
        var outDuration = (int)(FadeLineOutDurationMs / tempo);
        var inDuration = (int)(SlideWordInDurationMs / tempo);
        var stagger = (int)(SlideWordStaggerMs / tempo);

        // The outgoing line always fades out as a whole using the plain text block.
        SetLyricWordPanelsActive(false);
        OutgoingLyricTextBlock.Text = outgoingText;
        OutgoingLyricTextBlock.Opacity = hasOutgoing ? 1 : 0;
        OutgoingLyricTranslateTransform.Y = 0;
        IncomingLyricTextBlock.Text = string.Empty;
        IncomingLyricTextBlock.Opacity = 0;

        // 1. Fade out the whole outgoing line
        if (hasOutgoing)
        {
            await AnimateDoubleAsync(
                OutgoingLyricTextBlock,
                OpacityProperty,
                new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(outDuration),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                });

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }

            OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
            OutgoingLyricTextBlock.Opacity = 0;
            OutgoingLyricTextBlock.Text = string.Empty;
        }

        if (!hasIncoming)
        {
            _displayedLyricText = string.Empty;

            if (!ShouldHideForEmptyLine())
            {
                RestoreLyricTextBlocks(string.Empty);
                return;
            }

            await Task.Delay(EmptyLineHideDelay);

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }

            RestoreLyricTextBlocks(string.Empty);
            await SetWallpaperContentVisibleAsync(false);
            return;
        }

        if (!_isWallpaperContentVisible)
        {
            await SetWallpaperContentVisibleAsync(true);

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }
        }

        // 3. Fade in new words one by one (no sliding)
        SetLyricWordPanelsActive(true);
        PopulateWordPanel(IncomingWordsPanel, newCurrentLine);
        foreach (var child in IncomingWordsPanel.Children.OfType<TextBlock>())
        {
            child.Opacity = 0;
        }
        OutgoingWordsPanel.Visibility = Visibility.Collapsed;
        IncomingWordsPanel.Visibility = Visibility.Visible;

        await AnimateFadeWordsInAsync(IncomingWordsPanel, transitionVersion, inDuration, stagger);

        if (transitionVersion != _lyricTransitionVersion)
        {
            return;
        }

        RestoreLyricTextBlocks(newCurrentLine);
        _displayedLyricText = newCurrentLine;
    }

    private async Task HandleCurrentLineChangedFlashIn(string newCurrentLine, int transitionVersion)
    {
        var outgoingText = _displayedLyricText;
        var hasOutgoing = !string.IsNullOrWhiteSpace(outgoingText);
        var hasIncoming = !string.IsNullOrWhiteSpace(newCurrentLine);
        var tempo = CalculateLocalLyricTempoMultiplier(newCurrentLine, outgoingText);
        var outDuration = (int)(FadeLineOutDurationMs / tempo);
        var stagger = (int)(FlashStaggerMs / tempo);

        // The outgoing line always fades out as a whole using the plain text block.
        SetLyricWordPanelsActive(false);
        OutgoingLyricTextBlock.Text = outgoingText;
        OutgoingLyricTextBlock.Opacity = hasOutgoing ? 1 : 0;
        OutgoingLyricTranslateTransform.Y = 0;
        IncomingLyricTextBlock.Text = string.Empty;
        IncomingLyricTextBlock.Opacity = 0;

        // 1. Fade out the whole outgoing line
        if (hasOutgoing)
        {
            await AnimateDoubleAsync(
                OutgoingLyricTextBlock,
                OpacityProperty,
                new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(outDuration),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                });

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }

            OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
            OutgoingLyricTextBlock.Opacity = 0;
            OutgoingLyricTextBlock.Text = string.Empty;
        }

        if (!hasIncoming)
        {
            _displayedLyricText = string.Empty;

            if (!ShouldHideForEmptyLine())
            {
                RestoreLyricTextBlocks(string.Empty);
                return;
            }

            await Task.Delay(EmptyLineHideDelay);

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }

            RestoreLyricTextBlocks(string.Empty);
            await SetWallpaperContentVisibleAsync(false);
            return;
        }

        if (!_isWallpaperContentVisible)
        {
            await SetWallpaperContentVisibleAsync(true);

            if (transitionVersion != _lyricTransitionVersion)
            {
                return;
            }
        }

        // 3. Pop in new words one by one, flashing as each appears.
        SetLyricWordPanelsActive(true);
        if (_settings.WallpaperAnimationMode == IslandAnimationMode.FlashInRandom)
        {
            PopulateRandomFlashWordPanel(IncomingWordsPanel, newCurrentLine);
        }
        else
        {
            PopulateFlashWordPanel(IncomingWordsPanel, newCurrentLine);
        }
        OutgoingWordsPanel.Visibility = Visibility.Collapsed;
        IncomingWordsPanel.Visibility = Visibility.Visible;
        _displayedLyricText = newCurrentLine;

        await AnimateFlashWordsInAsync(IncomingWordsPanel, transitionVersion, stagger);

        if (transitionVersion != _lyricTransitionVersion)
        {
            return;
        }

        RestoreLyricTextBlocks(newCurrentLine);
        _displayedLyricText = newCurrentLine;
    }

    private void CancelLyricTransitionAnimations()
    {
        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        CancelSlideWordAnimations(OutgoingWordsPanel);
        CancelSlideWordAnimations(IncomingWordsPanel);
    }

    private void SetLyricWordPanelsActive(bool useWordPanels)
    {
        if (useWordPanels)
        {
            OutgoingLyricTextBlock.Visibility = Visibility.Collapsed;
            IncomingLyricTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        OutgoingLyricTextBlock.Visibility = Visibility.Visible;
        IncomingLyricTextBlock.Visibility = Visibility.Visible;
        OutgoingWordsPanel.Visibility = Visibility.Collapsed;
        IncomingWordsPanel.Visibility = Visibility.Collapsed;
        ClearWordPanel(OutgoingWordsPanel);
        ClearWordPanel(IncomingWordsPanel);
    }

    private void RestoreLyricTextBlocks(string displayedText)
    {
        ClearWordPanel(OutgoingWordsPanel);
        ClearWordPanel(IncomingWordsPanel);
        OutgoingWordsPanel.Visibility = Visibility.Collapsed;
        IncomingWordsPanel.Visibility = Visibility.Collapsed;

        OutgoingLyricTextBlock.Visibility = Visibility.Visible;
        IncomingLyricTextBlock.Visibility = Visibility.Visible;
        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);

        OutgoingLyricTextBlock.Text = string.Empty;
        OutgoingLyricTextBlock.Opacity = 0;
        OutgoingLyricTranslateTransform.Y = 0;

        IncomingLyricTextBlock.Text = displayedText;
        IncomingLyricTextBlock.Opacity = string.IsNullOrWhiteSpace(displayedText) ? 0 : 1;
        IncomingLyricTranslateTransform.Y = 0;
    }

    private void PopulateWordPanel(StackPanel panel, string text, double initialYOffset = 0)
    {
        ClearWordPanel(panel);
        var words = SplitLyricWords(text);
        var characterBased = ContainsCharacterBasedScript(text);
        for (var i = 0; i < words.Count; i++)
        {
            var transform = new TranslateTransform(0, initialYOffset);
            var wordText = characterBased || i == words.Count - 1 ? words[i] : words[i] + " ";
            var textBlock = new TextBlock
            {
                Text = wordText,
                Foreground = IncomingLyricTextBlock.Foreground,
                FontFamily = IncomingLyricTextBlock.FontFamily,
                FontSize = IncomingLyricTextBlock.FontSize,
                FontWeight = IncomingLyricTextBlock.FontWeight,
                FontStyle = IncomingLyricTextBlock.FontStyle,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = initialYOffset == 0 ? 1 : 0,
                RenderTransform = transform,
                Padding = new Thickness(0, 2, 0, 2)
            };
            panel.Children.Add(textBlock);
        }
    }

    private static void ClearWordPanel(StackPanel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is TextBlock textBlock && textBlock.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                textBlock.BeginAnimation(OpacityProperty, null);
            }
        }

        panel.Children.Clear();
    }

    private static void CancelSlideWordAnimations(StackPanel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is TextBlock textBlock && textBlock.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                textBlock.BeginAnimation(OpacityProperty, null);
            }
        }
    }

    private static List<string> SplitLyricWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        if (ContainsCharacterBasedScript(text))
        {
            var characters = new List<string>();
            foreach (var rune in text.EnumerateRunes())
            {
                var value = rune.ToString();
                if (Rune.IsWhiteSpace(rune) && characters.Count > 0)
                {
                    characters[^1] += value;
                }
                else if (!Rune.IsWhiteSpace(rune))
                {
                    characters.Add(value);
                }
            }

            return characters;
        }

        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static bool ContainsCharacterBasedScript(string text)
    {
        return text.EnumerateRunes().Any(rune =>
            (rune.Value >= 0x3040 && rune.Value <= 0x30FF) || // Hiragana and Katakana
            (rune.Value >= 0x3400 && rune.Value <= 0x9FFF) || // CJK ideographs
            (rune.Value >= 0xAC00 && rune.Value <= 0xD7AF) || // Hangul syllables
            (rune.Value >= 0x1100 && rune.Value <= 0x11FF)); // Hangul jamo
    }

    private async Task AnimateSlideWordsAsync(StackPanel panel, bool slideOut, int transitionVersion, int durationMs = SlideWordDurationMs, int staggerMs = SlideWordStaggerMs)
    {
        var children = panel.Children.OfType<TextBlock>().ToList();
        if (children.Count == 0)
        {
            return;
        }

        var animations = new List<Task>();
        for (var i = 0; i < children.Count; i++)
        {
            var textBlock = children[i];
            var transform = textBlock.RenderTransform as TranslateTransform;
            if (transform != null)
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
            }
            textBlock.BeginAnimation(OpacityProperty, null);

            var beginTime = TimeSpan.FromMilliseconds(i * staggerMs);
            var yAnimation = slideOut
                ? new DoubleAnimation
                {
                    From = 0,
                    To = -SlideWordOffset,
                    BeginTime = beginTime,
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                }
                : new DoubleAnimation
                {
                    From = SlideWordOffset,
                    To = 0,
                    BeginTime = beginTime,
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

            var opacityAnimation = slideOut
                ? new DoubleAnimation
                {
                    From = 1,
                    To = 0,
                    BeginTime = beginTime,
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                }
                : new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    BeginTime = beginTime,
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

            if (transform != null)
            {
                animations.Add(AnimateDoubleAsync(transform, TranslateTransform.YProperty, yAnimation));
            }
            animations.Add(AnimateDoubleAsync(textBlock, OpacityProperty, opacityAnimation));
        }

        await Task.WhenAll(animations);

        if (transitionVersion != _lyricTransitionVersion)
        {
            return;
        }
    }

    private async Task AnimateFadeWordsInAsync(StackPanel panel, int transitionVersion, int durationMs = SlideWordInDurationMs, int staggerMs = SlideWordStaggerMs)
    {
        var children = panel.Children.OfType<TextBlock>().ToList();
        if (children.Count == 0)
        {
            return;
        }

        var animations = new List<Task>();
        for (var i = 0; i < children.Count; i++)
        {
            var textBlock = children[i];
            textBlock.BeginAnimation(OpacityProperty, null);

            var beginTime = TimeSpan.FromMilliseconds(i * staggerMs);
            var opacityAnimation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                BeginTime = beginTime,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            animations.Add(AnimateDoubleAsync(textBlock, OpacityProperty, opacityAnimation));
        }

        await Task.WhenAll(animations);

        if (transitionVersion != _lyricTransitionVersion)
        {
            return;
        }
    }

    private static BitmapImage LoadFlashImage()
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "island-flash.png"), UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static Border CreateFlashGlow()
    {
        var glowColor = GetFlashGlowColor();
        var brush = new RadialGradientBrush
        {
            Center = new System.Windows.Point(0.5, 0.5),
            GradientOrigin = new System.Windows.Point(0.5, 0.5)
        };
        brush.GradientStops.Add(new GradientStop(glowColor, 0.0));
        brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(90, glowColor.R, glowColor.G, glowColor.B), 0.5));
        brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0, glowColor.R, glowColor.G, glowColor.B), 1.0));
        return new Border
        {
            Width = FlashLayoutBox,
            Height = FlashLayoutBox,
            Background = brush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(FlashGlowScale, FlashGlowScale)
        };
    }

    private static MediaColor GetFlashGlowColor()
    {
        var defaultColor = MediaColor.FromRgb(245, 247, 250);
        if (_appliedFlashColor is MediaColor appliedColor)
        {
            return appliedColor;
        }

        return defaultColor;
    }

    private static BitmapImage CreateTintedFlashImage(BitmapImage source, MediaColor color)
    {
        using var sourceStream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(sourceStream);
        sourceStream.Position = 0;

        using var original = new System.Drawing.Bitmap(sourceStream);
        using var tinted = new System.Drawing.Bitmap(original.Width, original.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(tinted))
        using (var attributes = new System.Drawing.Imaging.ImageAttributes())
        {
            var matrix = new float[5][]
            {
                new float[] { color.R / 255f, 0, 0, 0, 0 },
                new float[] { color.G / 255f, 0, 0, 0, 0 },
                new float[] { color.B / 255f, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { 0, 0, 0, 0, 1 }
            };
            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(matrix));
            graphics.DrawImage(
                original,
                new System.Drawing.Rectangle(0, 0, original.Width, original.Height),
                0,
                0,
                original.Width,
                original.Height,
                System.Drawing.GraphicsUnit.Pixel,
                attributes);
        }

        using var outStream = new MemoryStream();
        tinted.Save(outStream, System.Drawing.Imaging.ImageFormat.Png);
        outStream.Position = 0;

        var result = new BitmapImage();
        result.BeginInit();
        result.CacheOption = BitmapCacheOption.OnLoad;
        result.StreamSource = outStream;
        result.EndInit();
        result.Freeze();
        return result;
    }

    private void PopulateFlashWordPanel(StackPanel panel, string text)
    {
        ClearWordPanel(panel);
        var words = SplitLyricWords(text);
        var characterBased = ContainsCharacterBasedScript(text);
        for (var i = 0; i < words.Count; i++)
        {
            var wordText = characterBased || i == words.Count - 1 ? words[i] : words[i] + " ";
            var textBlock = new TextBlock
            {
                Text = wordText,
                Foreground = IncomingLyricTextBlock.Foreground,
                FontFamily = IncomingLyricTextBlock.FontFamily,
                FontSize = IncomingLyricTextBlock.FontSize,
                FontWeight = IncomingLyricTextBlock.FontWeight,
                FontStyle = IncomingLyricTextBlock.FontStyle,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
                Padding = new Thickness(0, 2, 0, 2)
            };

            var wordGrid = new Grid();
            wordGrid.Children.Add(CreateFlashGlow());
            wordGrid.Children.Add(textBlock);
            wordGrid.Children.Add(CreateFlashImage(System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Top));
            wordGrid.Children.Add(CreateFlashImage(System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Bottom));
            panel.Children.Add(wordGrid);
        }
    }

    private void PopulateRandomFlashWordPanel(StackPanel panel, string text)
    {
        ClearWordPanel(panel);
        var words = SplitLyricWords(text);
        var characterBased = ContainsCharacterBasedScript(text);
        for (var i = 0; i < words.Count; i++)
        {
            var wordText = characterBased || i == words.Count - 1 ? words[i] : words[i] + " ";
            var textBlock = new TextBlock
            {
                Text = wordText,
                Foreground = IncomingLyricTextBlock.Foreground,
                FontFamily = IncomingLyricTextBlock.FontFamily,
                FontSize = IncomingLyricTextBlock.FontSize,
                FontWeight = IncomingLyricTextBlock.FontWeight,
                FontStyle = IncomingLyricTextBlock.FontStyle,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
                Padding = new Thickness(0, 2, 0, 2)
            };

            var wordGrid = new Grid();
            wordGrid.Children.Add(CreateFlashGlow());
            wordGrid.Children.Add(textBlock);

            var starCount = FlashRandom.Next(1, 4);
            for (var s = 0; s < starCount; s++)
            {
                wordGrid.Children.Add(CreateRandomFlashImage(starCount));
            }

            panel.Children.Add(wordGrid);
        }
    }

    private System.Windows.Controls.Image CreateRandomFlashImage(int starCount)
    {
        var size = starCount switch
        {
            1 => FlashIconSizeSingle,
            3 => FlashIconSizeTriple,
            _ => FlashIconSize
        };
        var scale = size / FlashLayoutBox;
        var image = new System.Windows.Controls.Image
        {
            Source = _flashImage,
            Width = FlashLayoutBox,
            Height = FlashLayoutBox,
            Opacity = 0,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5)
        };
        var offsetX = FlashRandom.NextDouble() * 28 - 14;
        var offsetY = FlashRandom.NextDouble() * 18 - 9;
        var angle = FlashRandom.NextDouble() * 120 - 60;
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(new ScaleTransform(scale, scale));
        transformGroup.Children.Add(new RotateTransform(angle));
        transformGroup.Children.Add(new TranslateTransform(offsetX, offsetY));
        image.RenderTransform = transformGroup;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    private System.Windows.Controls.Image CreateFlashImage(System.Windows.HorizontalAlignment horizontalAlignment, System.Windows.VerticalAlignment verticalAlignment)
    {
        var scale = FlashIconSize / FlashLayoutBox;
        var image = new System.Windows.Controls.Image
        {
            Source = _flashImage,
            Width = FlashLayoutBox,
            Height = FlashLayoutBox,
            Opacity = 0,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = verticalAlignment,
            IsHitTestVisible = false,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5)
        };
        var offsetX = FlashRandom.NextDouble() * 8 - 4;
        var offsetY = FlashRandom.NextDouble() * 8 - 4;
        var angle = FlashRandom.NextDouble() * 60 - 30;
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(new ScaleTransform(scale, scale));
        transformGroup.Children.Add(new RotateTransform(angle));
        transformGroup.Children.Add(new TranslateTransform(offsetX, offsetY));
        image.RenderTransform = transformGroup;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    private async Task AnimateFlashWordsInAsync(StackPanel panel, int transitionVersion, int staggerMs = FlashStaggerMs)
    {
        var children = panel.Children.OfType<Grid>().ToList();
        if (children.Count == 0)
        {
            return;
        }

        var animations = new List<Task>();
        for (var i = 0; i < children.Count; i++)
        {
            var grid = children[i];
            var textBlock = grid.Children.OfType<TextBlock>().FirstOrDefault();
            var flashImages = grid.Children.OfType<System.Windows.Controls.Image>().ToList();

            textBlock?.BeginAnimation(OpacityProperty, null);
            foreach (var flashImage in flashImages)
            {
                flashImage.BeginAnimation(OpacityProperty, null);
            }
            var glow = grid.Children.OfType<Border>().FirstOrDefault();
            if (glow is not null)
            {
                glow.BeginAnimation(OpacityProperty, null);
                glow.Opacity = 0;
            }

            var beginTime = TimeSpan.FromMilliseconds(i * staggerMs);
            var wordTime = beginTime + TimeSpan.FromMilliseconds(FlashPopLeadMs);

            if (textBlock is not null)
            {
                animations.Add(AnimateDoubleAsync(textBlock, OpacityProperty, new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    BeginTime = wordTime,
                    Duration = TimeSpan.Zero
                }));
            }

            if (glow is not null)
            {
                animations.Add(AnimateFlashGlowAsync(glow, beginTime));
            }

            foreach (var flashImage in flashImages)
            {
                animations.Add(AnimateFlashEffectAsync(flashImage, beginTime));
            }
        }

        await Task.WhenAll(animations);

        if (transitionVersion != _lyricTransitionVersion)
        {
            return;
        }
    }

    private static async Task AnimateFlashEffectAsync(System.Windows.Controls.Image image, TimeSpan beginTime)
    {
        await AnimateDoubleAsync(image, OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = beginTime,
            Duration = TimeSpan.FromMilliseconds(FlashInMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });

        await AnimateDoubleAsync(image, OpacityProperty, new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(FlashOutMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        });
    }

    private static async Task AnimateFlashGlowAsync(Border glow, TimeSpan beginTime)
    {
        await AnimateDoubleAsync(glow, OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = FlashGlowMaxOpacity,
            BeginTime = beginTime,
            Duration = TimeSpan.FromMilliseconds(FlashInMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });

        await AnimateDoubleAsync(glow, OpacityProperty, new DoubleAnimation
        {
            From = FlashGlowMaxOpacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(FlashOutMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        });
    }

    private bool ShouldHideForEmptyLine()
    {
        if (_viewModel.IsLoadingLyrics)
        {
            return false;
        }

        if (_viewModel.Lyrics.Count == 0)
        {
            return true;
        }

        var position = _viewModel.EstimatedPosition;
        if (position is null)
        {
            return true;
        }

        var nextNonEmptyLine = _viewModel.Lyrics
            .Where(line => line.Timestamp >= position.Value && !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.Timestamp)
            .FirstOrDefault();

        if (nextNonEmptyLine is null)
        {
            return true;
        }

        return nextNonEmptyLine.Timestamp - position.Value > EmptyLineUpcomingLyricGrace;
    }

    private void UpdateWallpaperContentVisibility(string text)
    {
        _isWallpaperContentVisible = !string.IsNullOrWhiteSpace(text) && _viewModel.Lyrics.Count > 0;
        WallpaperLayer.BeginAnimation(OpacityProperty, null);
        WallpaperLayer.Opacity = GetTargetWallpaperOpacity();
    }

    private Task SetWallpaperContentVisibleAsync(bool isVisible)
    {
        var targetOpacity = GetTargetWallpaperOpacity();

        if (_isWallpaperContentVisible == isVisible && Math.Abs(WallpaperLayer.Opacity - targetOpacity) < 0.001)
        {
            WallpaperLayer.Opacity = targetOpacity;
            return Task.CompletedTask;
        }

        _isWallpaperContentVisible = isVisible;
        targetOpacity = GetTargetWallpaperOpacity();
        WallpaperLayer.BeginAnimation(OpacityProperty, null);
        var completion = new TaskCompletionSource();
        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(isVisible ? 180 : 140),
            EasingFunction = new QuadraticEase
            {
                EasingMode = isVisible ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };
        animation.Completed += (_, _) =>
        {
            WallpaperLayer.BeginAnimation(OpacityProperty, null);
            WallpaperLayer.Opacity = targetOpacity;
            completion.TrySetResult();
        };
        WallpaperLayer.BeginAnimation(OpacityProperty, animation);
        return completion.Task;
    }

    private static Task AnimateDoubleAsync(UIElement target, DependencyProperty property, DoubleAnimation animation)
    {
        var completion = new TaskCompletionSource();
        animation.Completed += (_, _) => completion.TrySetResult();
        target.BeginAnimation(property, animation);
        return completion.Task;
    }

    private static Task AnimateDoubleAsync(Animatable target, DependencyProperty property, DoubleAnimation animation)
    {
        var completion = new TaskCompletionSource();
        animation.Completed += (_, _) => completion.TrySetResult();
        target.BeginAnimation(property, animation);
        return completion.Task;
    }

    private void HandlePlaybackPausedChanged(bool isPaused)
    {
        if (isPaused && _settings.WallpaperTimeout > 0)
        {
            _timeoutTimer.Interval = TimeSpan.FromSeconds(_settings.WallpaperTimeout);
            _timeoutTimer.Start();
        }
        else
        {
            _timeoutTimer.Stop();
            if (_isTimeoutHidden)
            {
                _isTimeoutHidden = false;
                ApplyTargetWallpaperOpacity();
            }
        }
    }

    private void TimeoutTimer_OnTick(object? sender, EventArgs e)
    {
        _timeoutTimer.Stop();
        if (_viewModel.IsPlaybackPaused && _settings.WallpaperTimeout > 0)
        {
            _isTimeoutHidden = true;
            ApplyTargetWallpaperOpacity(TimeSpan.FromMilliseconds(400));
        }
    }

    private double GetTargetWallpaperOpacity()
    {
        if (!_isWallpaperContentVisible)
            return 0;
        if (_isTimeoutHidden)
            return 0;
        return 1;
    }

    private void ApplyTargetWallpaperOpacity(TimeSpan? duration = null)
    {
        var target = GetTargetWallpaperOpacity();
        if (Math.Abs(WallpaperLayer.Opacity - target) < 0.001)
        {
            WallpaperLayer.Opacity = target;
            return;
        }

        WallpaperLayer.BeginAnimation(OpacityProperty, null);
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = duration ?? TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = target > 0 ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        WallpaperLayer.BeginAnimation(OpacityProperty, animation);
    }

    private void MonitorWarningTimer_OnTick(object? sender, EventArgs e)
    {
        _monitorWarningTimer.Stop();
        HideMonitorWarning();
    }

    private void ShowMonitorWarning(string message)
    {
        MonitorWarningTextBlock.Text = message;
        MonitorWarningBanner.Visibility = Visibility.Visible;
        MonitorWarningBanner.BeginAnimation(OpacityProperty, null);

        var fadeIn = new DoubleAnimation
        {
            From = MonitorWarningBanner.Opacity,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        MonitorWarningBanner.BeginAnimation(OpacityProperty, fadeIn);
        _monitorWarningTimer.Stop();
        _monitorWarningTimer.Start();
    }

    private void HideMonitorWarning()
    {
        MonitorWarningBanner.BeginAnimation(OpacityProperty, null);
        var fadeOut = new DoubleAnimation
        {
            From = MonitorWarningBanner.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) => MonitorWarningBanner.Visibility = Visibility.Collapsed;
        MonitorWarningBanner.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow { Owner = this };
            _settingsWindow.SettingsChanged += SettingsWindow_OnSettingsChanged;
            _settingsWindow.ForceLyricsRefreshRequested += (_, _) => _ = _viewModel.ForceLyricsRefreshAsync();
            _settingsWindow.DebugForceNoLyricsRequested += (_, _) => _viewModel.ForceNoLyrics();
            _settingsWindow.DebugForceSimulateLyricsRequested += (_, _) => _viewModel.ForceSimulateLyrics();
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                ApplyAppearance();
            };
            RefreshSettingsWindowOptions();
            _settingsWindow.Show();
            ApplyAppearance();
            return;
        }

        RefreshSettingsWindowOptions();
        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
        ApplyAppearance();
    }

    private void RefreshSettingsWindowOptions()
    {
        if (_settingsWindow is null || _appBarManager is null)
        {
            return;
        }

        _settings = _appSettingsService.Load();

        var monitors = _appBarManager.Monitors
            .Select((monitor, index) => new MonitorOption(
                monitor.DeviceName,
                monitor.IsPrimary
                    ? $"Monitor {index + 1} ({monitor.DeviceName}, Primary)"
                    : $"Monitor {index + 1} ({monitor.DeviceName})"))
            .ToList();

        _settingsWindow.LoadSettings(_settings, monitors);
    }

    private void SettingsWindow_OnSettingsChanged(object? sender, AppSettings settings)
    {
        _settings = MergeSettings(settings);
        _appSettingsService.Save(_settings);
        _viewModel.UpdateSettings(_settings);

        if (_settings.DisplayMode != DisplayMode.Wallpaper)
        {
            ((App)WpfApplication.Current).RestartDisplayWindow();
            return;
        }

        if (_appBarManager is not null && !string.IsNullOrWhiteSpace(_settings.WallpaperPreferredMonitorDeviceName))
        {
            if (_appBarManager.SetCurrentMonitor(_settings.WallpaperPreferredMonitorDeviceName))
            {
                _lastMonitorWarningKey = null;
            }
            else
            {
                ApplyMonitorSetting();
            }
        }

        PositionWindow();
        ApplyAppearance();
        ApplyWallpaperTextAlignment();

        if (_settings.WallpaperTimeout <= 0)
        {
            _timeoutTimer.Stop();
            if (_isTimeoutHidden)
            {
                _isTimeoutHidden = false;
                ApplyTargetWallpaperOpacity();
            }
        }

        ApplyTargetWallpaperOpacity();
    }

    private AppSettings MergeSettings(AppSettings incomingSettings)
    {
        var persistedSettings = _appSettingsService.Load();
        persistedSettings.DisplayMode = incomingSettings.DisplayMode;
        persistedSettings.HideMode = incomingSettings.HideMode;
        persistedSettings.ShowNextLine = incomingSettings.ShowNextLine;
        persistedSettings.AppBarPreferredMonitorDeviceName = incomingSettings.AppBarPreferredMonitorDeviceName;
        persistedSettings.TaskbarPreferredMonitorDeviceName = incomingSettings.TaskbarPreferredMonitorDeviceName;
        persistedSettings.IslandPreferredMonitorDeviceName = incomingSettings.IslandPreferredMonitorDeviceName;
        persistedSettings.WallpaperPreferredMonitorDeviceName = incomingSettings.WallpaperPreferredMonitorDeviceName;
        persistedSettings.CustomBarHeight = incomingSettings.CustomBarHeight;
        persistedSettings.WindowedWidth = incomingSettings.WindowedWidth;
        persistedSettings.WindowedHeight = incomingSettings.WindowedHeight;
        persistedSettings.TaskbarMaximumWidth = incomingSettings.TaskbarMaximumWidth;
        persistedSettings.TaskbarHeight = incomingSettings.TaskbarHeight;
        persistedSettings.IslandMaximumWidth = incomingSettings.IslandMaximumWidth;
        persistedSettings.IslandScale = incomingSettings.IslandScale;
        persistedSettings.IslandContainerHeight = incomingSettings.IslandContainerHeight;
        persistedSettings.IslandCornerRadius = incomingSettings.IslandCornerRadius;
        persistedSettings.IslandHideInFullscreen = incomingSettings.IslandHideInFullscreen;
        persistedSettings.IslandTimeout = incomingSettings.IslandTimeout;
        persistedSettings.IslandHoverOpacity = incomingSettings.IslandHoverOpacity;
        persistedSettings.IslandAnimationMode = incomingSettings.IslandAnimationMode;
        persistedSettings.IslandAnimationManualSpeed = incomingSettings.IslandAnimationManualSpeed;
        persistedSettings.WallpaperMaximumWidth = incomingSettings.WallpaperMaximumWidth;
        persistedSettings.WallpaperScale = incomingSettings.WallpaperScale;
        persistedSettings.WallpaperContainerHeight = incomingSettings.WallpaperContainerHeight;
        persistedSettings.WallpaperAnimationMode = incomingSettings.WallpaperAnimationMode;
        persistedSettings.WallpaperAnimationManualSpeed = incomingSettings.WallpaperAnimationManualSpeed;
        persistedSettings.WallpaperTimeout = incomingSettings.WallpaperTimeout;
        persistedSettings.WallpaperHorizontalAlignment = incomingSettings.WallpaperHorizontalAlignment;
        persistedSettings.WallpaperVerticalAlignment = incomingSettings.WallpaperVerticalAlignment;
        persistedSettings.WallpaperCustomX = incomingSettings.WallpaperCustomX;
        persistedSettings.WallpaperCustomY = incomingSettings.WallpaperCustomY;
        persistedSettings.WallpaperTextHorizontalAlignment = incomingSettings.WallpaperTextHorizontalAlignment;
        persistedSettings.WallpaperTextVerticalAlignment = incomingSettings.WallpaperTextVerticalAlignment;
        persistedSettings.WallpaperTextColor = incomingSettings.WallpaperTextColor;
        persistedSettings.TaskbarAnimationMode = incomingSettings.TaskbarAnimationMode;
        persistedSettings.TaskbarAnimationManualSpeed = incomingSettings.TaskbarAnimationManualSpeed;
        persistedSettings.LyricAlignment = incomingSettings.LyricAlignment;
        persistedSettings.ShowAlbumArt = incomingSettings.ShowAlbumArt;
        persistedSettings.WordByWordMode = incomingSettings.WordByWordMode;
        persistedSettings.AutostartWithWindows = incomingSettings.AutostartWithWindows;
        persistedSettings.MaxCacheSize = incomingSettings.MaxCacheSize;
        persistedSettings.WindowedShowNextLine = incomingSettings.WindowedShowNextLine;
        persistedSettings.WindowedLyricAlignment = incomingSettings.WindowedLyricAlignment;
        persistedSettings.WindowedShowAlbumArt = incomingSettings.WindowedShowAlbumArt;
        persistedSettings.WindowedWordByWordMode = incomingSettings.WindowedWordByWordMode;
        persistedSettings.DebugForceLyricsSource = incomingSettings.DebugForceLyricsSource;
        persistedSettings.PreferredMonitorDeviceName = null;
        persistedSettings.DetectedMediaApps = MergeDetectedApps(
            incomingSettings.DetectedMediaApps,
            persistedSettings.DetectedMediaApps);
        persistedSettings.IgnoredMediaAppIds = MergeIgnoredMediaAppIds(
            incomingSettings.IgnoredMediaAppIds,
            incomingSettings.DetectedMediaApps,
            persistedSettings.IgnoredMediaAppIds,
            persistedSettings.DetectedMediaApps);
        return persistedSettings;
    }

    private static List<DetectedMediaApp> MergeDetectedApps(
        IEnumerable<DetectedMediaApp> primaryApps,
        IEnumerable<DetectedMediaApp> secondaryApps)
    {
        var mergedApps = new Dictionary<string, DetectedMediaApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in primaryApps.Concat(secondaryApps))
        {
            if (string.IsNullOrWhiteSpace(app.AppId))
            {
                continue;
            }

            if (!mergedApps.TryGetValue(app.AppId, out var existingApp)
                || string.IsNullOrWhiteSpace(existingApp.DisplayName))
            {
                mergedApps[app.AppId] = new DetectedMediaApp(
                    app.AppId,
                    string.IsNullOrWhiteSpace(app.DisplayName) ? app.AppId : app.DisplayName);
            }
        }

        return mergedApps.Values
            .OrderBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.AppId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> MergeIgnoredMediaAppIds(
        IEnumerable<string> primaryIgnoredIds,
        IEnumerable<DetectedMediaApp> primaryDetectedApps,
        IEnumerable<string> secondaryIgnoredIds,
        IEnumerable<DetectedMediaApp> secondaryDetectedApps)
    {
        var primaryDetectedIds = new HashSet<string>(
            primaryDetectedApps
                .Where(app => !string.IsNullOrWhiteSpace(app.AppId))
                .Select(app => app.AppId),
            StringComparer.OrdinalIgnoreCase);
        var mergedIgnoredIds = new HashSet<string>(
            primaryIgnoredIds.Where(appId => !string.IsNullOrWhiteSpace(appId)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var appId in secondaryIgnoredIds.Where(appId => !string.IsNullOrWhiteSpace(appId)))
        {
            if (!primaryDetectedIds.Contains(appId))
            {
                mergedIgnoredIds.Add(appId);
            }
        }

        var knownDetectedIds = new HashSet<string>(
            primaryDetectedIds.Concat(secondaryDetectedApps
                .Where(app => !string.IsNullOrWhiteSpace(app.AppId))
                .Select(app => app.AppId)),
            StringComparer.OrdinalIgnoreCase);

        return mergedIgnoredIds
            .Where(knownDetectedIds.Contains)
            .OrderBy(appId => appId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsSettingsWindowVisible()
    {
        return _settingsWindow is not null && _settingsWindow.IsLoaded && _settingsWindow.IsVisible;
    }

    private void ApplyClickThroughWindowStyle()
    {
        var hwnd = _hwndSource?.Handle ?? new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(
            hwnd,
            GWL_EXSTYLE,
            extendedStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    public void ShowFromTray()
    {
        Show();
        WorkspaceVisibilityManager.PinToAllWorkspaces(this);
    }

    public void OpenSettingsFromTray()
    {
        ShowFromTray();
        OpenSettingsWindow();
    }

    public void ExitApp()
    {
        Close();
    }

    public DisplayMode CurrentDisplayMode => DisplayMode.Wallpaper;

    public void SwitchDisplayMode(DisplayMode mode)
    {
        if (_settings.DisplayMode == mode) return;
        _settings.DisplayMode = mode;
        _appSettingsService.Save(_settings);
        ((App)WpfApplication.Current).RestartDisplayWindow();
    }
}
