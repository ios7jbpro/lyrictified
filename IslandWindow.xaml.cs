using Microsoft.Win32;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using Lyrictified.DisplayModes;
using Lyrictified.Interop;
using Lyrictified.Settings;
using Lyrictified.ViewModels;
using WpfBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfApplication = System.Windows.Application;

namespace Lyrictified;

public partial class IslandWindow : Window, ITrayIconHost
{
    private static readonly TimeSpan MonitorWarningDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HoverPollInterval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan EmptyLineHideDelay = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan EmptyLineUpcomingLyricGrace = TimeSpan.FromMilliseconds(1600);
    private static WpfBrush GetLoadingTextBrush()
    {
        var color = IsWindowsLightTheme()
            ? MediaColor.FromRgb(100, 106, 114)
            : MediaColor.FromRgb(150, 156, 164);
        return new SolidColorBrush(color);
    }
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _monitorWarningTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly AppSettingsService _appSettingsService;
    private AppBarManager? _appBarManager;
    private HwndSource? _hwndSource;
    private IntPtr _foregroundHook;
    private WinEventDelegate? _foregroundHookDelegate;
    private AppSettings _settings;
    private string _displayedLyricText = string.Empty;
    private string? _lastMonitorWarningKey;
    private double _islandWidth = 160;
    private int _lyricTransitionVersion;
    private bool _isPointerOverIsland;
    private bool _isIslandContentVisible = true;
    private bool _isPauseVisualActive;
    private bool _isNoLyricsAnimationActive;
    private bool _isFullscreenHidden;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _trayIcon;
    private readonly DispatcherTimer _fullscreenTimer;
    private readonly DispatcherTimer _timeoutTimer;
    private bool _isTimeoutHidden;
    private const double PauseSpacerTargetWidth = 24;
    private const double LoadingSpacerTargetWidth = 24;
    private const double SlideWordOffset = 48;
    private const int SlideWordDurationMs = 130;
    private const int SlideWordInDurationMs = 100;
    private const int SlideWordStaggerMs = 75;
    private static readonly MediaColor RedFlashColor = MediaColor.FromArgb(230, 200, 40, 40);

    public IslandWindow()
    {
        InitializeComponent();
        _appSettingsService = new AppSettingsService();
        _settings = _appSettingsService.Load();

        _viewModel = new MainViewModel();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = _viewModel;

        LoadingSpinnerImage.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "loading.png"), UriKind.Absolute));

        _monitorWarningTimer = new DispatcherTimer { Interval = MonitorWarningDuration };
        _monitorWarningTimer.Tick += MonitorWarningTimer_OnTick;
        _hoverTimer = new DispatcherTimer { Interval = HoverPollInterval };
        _hoverTimer.Tick += HoverTimer_OnTick;
        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fullscreenTimer.Tick += FullscreenTimer_OnTick;
        _timeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.IslandTimeout) };
        _timeoutTimer.Tick += TimeoutTimer_OnTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _foregroundHookDelegate = OnForegroundWindowChanged;
        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _foregroundHookDelegate,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);
        _appBarManager = new AppBarManager(this, IslandDisplayMode.WindowHeight);
        ApplyClickThroughWindowStyle();
        ApplyMonitorSetting();
        PositionWindow();
        ApplyAppearance();
        ApplyPlaybackStateVisual(immediate: true);
        EnsureTopmostOrder();
        _hoverTimer.Start();
        _fullscreenTimer.Start();
        WorkspaceVisibilityManager.PinToAllWorkspaces(this);
        _trayIcon = new TrayIcon(this);

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        IncomingLyricTextBlock.Text = _viewModel.TaskbarCurrentLine;
        _displayedLyricText = _viewModel.TaskbarCurrentLine;
        UpdateIslandWidth(_displayedLyricText, immediate: true);
        UpdateIslandContentVisibility(_displayedLyricText);
        ApplyLoadingState(immediate: true);
        ApplyPlaybackStateVisual(immediate: true);
        EnsureTopmostOrder();
        await _viewModel.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _monitorWarningTimer.Stop();
        _monitorWarningTimer.Tick -= MonitorWarningTimer_OnTick;
        _hoverTimer.Stop();
        _hoverTimer.Tick -= HoverTimer_OnTick;
        _fullscreenTimer.Stop();
        _fullscreenTimer.Tick -= FullscreenTimer_OnTick;
        _timeoutTimer.Stop();
        _timeoutTimer.Tick -= TimeoutTimer_OnTick;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        _foregroundHookDelegate = null;
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
        EnsureTopmostOrder();
        RefreshSettingsWindowOptions();
    }

    private void HoverTimer_OnTick(object? sender, EventArgs e)
    {
        if (_isNoLyricsAnimationActive)
        {
            return;
        }

        if (!GetCursorPos(out var point))
        {
            return;
        }

        var scale = GetEffectiveIslandScale();
        var scaledWidth = _islandWidth * scale;
        var islandLeft = Left + ((ActualWidth - scaledWidth) / 2);
        var scaledHeight = IslandLayer.ActualHeight * scale;
        var islandTop = Top + ((ActualHeight - scaledHeight) / 2);
        var islandRight = islandLeft + scaledWidth;
        var islandBottom = islandTop + scaledHeight;
        var isPointerOverIsland =
            point.X >= islandLeft
            && point.X <= islandRight
            && point.Y >= islandTop
            && point.Y <= islandBottom;

        if (isPointerOverIsland == _isPointerOverIsland)
        {
            return;
        }

        _isPointerOverIsland = isPointerOverIsland;
        AnimateIslandHoverOpacity(isPointerOverIsland);
    }

    private void AnimateIslandHoverOpacity(bool isHovered)
    {
        if (_isNoLyricsAnimationActive || _isFullscreenHidden || _isTimeoutHidden)
        {
            return;
        }

        var currentOpacity = IslandLayer.Opacity;
        IslandLayer.BeginAnimation(OpacityProperty, null);
        var animation = new DoubleAnimation
        {
            From = currentOpacity,
            To = GetTargetIslandOpacity(),
            Duration = TimeSpan.FromMilliseconds(isHovered ? 120 : 180),
            EasingFunction = new QuadraticEase
            {
                EasingMode = isHovered ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        IslandLayer.BeginAnimation(OpacityProperty, animation);
    }

    private void FullscreenTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_settings.IslandHideInFullscreen)
        {
            if (_isFullscreenHidden)
            {
                ShowFromFullscreenHide();
            }

            return;
        }

        var isFullscreen = IsForegroundWindowFullscreenOnIslandMonitor();
        if (isFullscreen && !_isFullscreenHidden)
        {
            HideForFullscreen();
        }
        else if (!isFullscreen && _isFullscreenHidden)
        {
            ShowFromFullscreenHide();
        }
    }

    private void HideForFullscreen()
    {
        _isFullscreenHidden = true;
        IslandLayer.BeginAnimation(OpacityProperty, null);
        var animation = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        IslandLayer.BeginAnimation(OpacityProperty, animation);
    }

    private void ShowFromFullscreenHide()
    {
        _isFullscreenHidden = false;
        IslandLayer.BeginAnimation(OpacityProperty, null);
        var animation = new DoubleAnimation
        {
            To = GetTargetIslandOpacity(),
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        IslandLayer.BeginAnimation(OpacityProperty, animation);
    }

    private bool IsForegroundWindowFullscreenOnIslandMonitor()
    {
        if (_appBarManager is null || _appBarManager.Monitors.Count == 0)
        {
            return false;
        }

        var foregroundHwnd = GetForegroundWindow();
        if (foregroundHwnd == IntPtr.Zero)
        {
            return false;
        }

        if (foregroundHwnd == _hwndSource?.Handle)
        {
            return false;
        }

        if (!GetWindowRect(foregroundHwnd, out var windowRect))
        {
            return false;
        }

        var monitor = _appBarManager.Monitors[Math.Clamp(_appBarManager.CurrentMonitorIndex, 0, _appBarManager.Monitors.Count - 1)];
        var bounds = monitor.Bounds;
        var monitorWidth = bounds.right - bounds.left;
        var monitorHeight = bounds.bottom - bounds.top;
        var windowWidth = windowRect.right - windowRect.left;
        var windowHeight = windowRect.bottom - windowRect.top;

        // Check if the window covers the entire monitor
        if (windowWidth < monitorWidth - 8 || windowHeight < monitorHeight - 8)
        {
            return false;
        }

        if (Math.Abs(windowRect.left - bounds.left) > 8 || Math.Abs(windowRect.top - bounds.top) > 8)
        {
            return false;
        }

        // Also verify it's not a desktop window
        var desktopHwnd = GetShellWindow();
        if (foregroundHwnd == desktopHwnd)
        {
            return false;
        }

        return true;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        ApplyAppearance();
        EnsureTopmostOrder();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        ScheduleTopmostRefreshBurst();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        ScheduleTopmostRefreshBurst();
    }

    private void OnForegroundWindowChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType != EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (hwnd == (_hwndSource?.Handle ?? IntPtr.Zero))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(ScheduleTopmostRefreshBurst, DispatcherPriority.Background);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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

        if (e.PropertyName == nameof(MainViewModel.IsLoadingLyrics))
        {
            if (Dispatcher.CheckAccess())
            {
                ApplyLoadingState();
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() => ApplyLoadingState());
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
                ApplyTargetIslandOpacity();
            }
            _timeoutTimer.Stop();
        }

        if (e.PropertyName == nameof(MainViewModel.NoTimedLyricsFound)
            || e.PropertyName == nameof(MainViewModel.IsLoadingLyrics))
        {
            if (Dispatcher.CheckAccess())
            {
                HandleNoLyricsState();
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() => HandleNoLyricsState());
            }
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
        ApplyIslandCornerRadius();
        if (!_viewModel.IsLoadingLyrics)
        {
            var lyricBrush = new SolidColorBrush(MediaColor.FromRgb(245, 247, 250));
            IncomingLyricTextBlock.Foreground = lyricBrush;
            OutgoingLyricTextBlock.Foreground = lyricBrush;
        }
    }

    private void ApplyMonitorSetting()
    {
        if (_appBarManager is null)
        {
            return;
        }

        var preferredMonitorDeviceName = _settings.IslandPreferredMonitorDeviceName ?? _settings.PreferredMonitorDeviceName;

        if (string.IsNullOrWhiteSpace(preferredMonitorDeviceName))
        {
            if (_appBarManager.CurrentMonitorDeviceName is not null)
            {
                _settings.IslandPreferredMonitorDeviceName = _appBarManager.CurrentMonitorDeviceName;
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
        var bounds = IslandDisplayMode.GetWindowBounds(
            monitor,
            _settings.IslandMaximumWidth,
            GetEffectiveIslandScale(),
            _settings.IslandContainerHeight);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        ApplyIslandScale();
        UpdateIslandWidth(_displayedLyricText, immediate: true);
        _ = Dispatcher.InvokeAsync(
            () => UpdateIslandWidth(_displayedLyricText, immediate: true),
            DispatcherPriority.Loaded);
        EnsureTopmostOrder();
    }

    private void ApplyIslandScale()
    {
        var scale = GetEffectiveIslandScale();
        IslandLayerScaleTransform.ScaleX = scale;
        IslandLayerScaleTransform.ScaleY = scale;
    }

    private double GetEffectiveIslandScale()
    {
        return IslandDisplayMode.GetEffectiveScale(_settings.IslandScale);
    }

    private void ApplyIslandCornerRadius()
    {
        var radius = IslandDisplayMode.GetEffectiveCornerRadius(_settings.IslandCornerRadius);
        IslandBackground.CornerRadius = new CornerRadius(radius);
    }

    private async void HandleCurrentLineChanged(string newCurrentLine)
    {
        if (string.Equals(_displayedLyricText, newCurrentLine, StringComparison.Ordinal))
        {
            return;
        }

        if (_isNoLyricsAnimationActive)
        {
            return;
        }

        var transitionVersion = ++_lyricTransitionVersion;
        CancelLyricTransitionAnimations();

        if (_settings.IslandAnimationMode == IslandAnimationMode.SlideIn || _settings.IslandAnimationMode == IslandAnimationMode.SlideInManual)
        {
            await HandleCurrentLineChangedSlideIn(newCurrentLine, transitionVersion);
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

            await SetIslandContentVisibleAsync(false);
            return;
        }

        await AnimateIslandWidthAsync(newCurrentLine);

        if (transitionVersion != _lyricTransitionVersion)
        {
            return;
        }

        if (!_isIslandContentVisible)
        {
            await SetIslandContentVisibleAsync(true);

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

    private double CalculateLyricTempoMultiplier()
    {
        var lyrics = _viewModel.Lyrics;
        if (lyrics.Count < 2)
        {
            return 1.0;
        }

        var gaps = new List<double>();
        for (var i = 0; i < lyrics.Count - 1; i++)
        {
            var gap = (lyrics[i + 1].Timestamp - lyrics[i].Timestamp).TotalSeconds;
            if (gap > 0.2)
            {
                gaps.Add(gap);
            }
        }

        if (gaps.Count == 0)
        {
            return 1.0;
        }

        gaps.Sort();
        var medianGap = gaps[gaps.Count / 2];
        if (medianGap <= 0)
        {
            return 1.0;
        }

        var multiplier = 3.0 / medianGap;
        return Math.Clamp(multiplier, 0.5, 2.0);
    }

    private async Task HandleCurrentLineChangedSlideIn(string newCurrentLine, int transitionVersion)
    {
        var outgoingText = _displayedLyricText;
        var hasOutgoing = !string.IsNullOrWhiteSpace(outgoingText);
        var hasIncoming = !string.IsNullOrWhiteSpace(newCurrentLine);
        var tempo = _settings.IslandAnimationMode == IslandAnimationMode.SlideInManual
            ? _settings.IslandAnimationManualSpeed
            : CalculateLyricTempoMultiplier();
        var outDuration = (int)(SlideWordDurationMs / tempo);
        var inDuration = (int)(SlideWordInDurationMs / tempo);
        var stagger = (int)(SlideWordStaggerMs / tempo);
        var widthDuration = (int)(190 / tempo);

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
            await SetIslandContentVisibleAsync(false);
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

        if (!_isIslandContentVisible)
        {
            await SetIslandContentVisibleAsync(true);

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

        // 2. Resize island
        await AnimateIslandWidthAsync(newCurrentLine, widthDuration);

        if (transitionVersion != _lyricTransitionVersion)
        {
            return;
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

    private void CancelLyricTransitionAnimations()
    {
        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        IslandBackground.BeginAnimation(WidthProperty, null);
        IslandTextClip.BeginAnimation(WidthProperty, null);
        IslandBackgroundScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IslandBackgroundTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
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
        for (var i = 0; i < words.Count; i++)
        {
            var transform = new TranslateTransform(0, initialYOffset);
            var wordText = i < words.Count - 1 ? words[i] + " " : words[i];
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
                RenderTransform = transform
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

        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
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

    private void UpdateIslandContentVisibility(string text)
    {
        _isIslandContentVisible = !string.IsNullOrWhiteSpace(text);
        if (_isFullscreenHidden)
        {
            return;
        }

        IslandLayer.BeginAnimation(OpacityProperty, null);
        IslandLayer.Opacity = GetTargetIslandOpacity();
    }

    private Task SetIslandContentVisibleAsync(bool isVisible)
    {
        if (_isFullscreenHidden && isVisible)
        {
            _isIslandContentVisible = isVisible;
            return Task.CompletedTask;
        }

        var targetOpacity = GetTargetIslandOpacity();

        if (_isIslandContentVisible == isVisible && Math.Abs(IslandLayer.Opacity - targetOpacity) < 0.001)
        {
            IslandLayer.Opacity = targetOpacity;
            return Task.CompletedTask;
        }

        _isIslandContentVisible = isVisible;
        targetOpacity = GetTargetIslandOpacity();
        IslandLayer.BeginAnimation(OpacityProperty, null);
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
            IslandLayer.BeginAnimation(OpacityProperty, null);
            IslandLayer.Opacity = targetOpacity;
            completion.TrySetResult();
        };
        IslandLayer.BeginAnimation(OpacityProperty, animation);
        return completion.Task;
    }

    private void UpdateIslandWidth(string text, bool immediate)
    {
        var width = MeasureIslandWidth(text);
        _islandWidth = width;

        IslandBackground.BeginAnimation(WidthProperty, null);
        IslandTextClip.BeginAnimation(WidthProperty, null);
        IslandBackground.Width = width;
        IslandTextClip.Width = width;
        ApplyLyricTextWidth(width);

        if (immediate)
        {
            IslandBackgroundTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
            IslandBackgroundScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            IslandBackgroundScaleTransform.ScaleX = 1;
            IslandBackgroundTranslateTransform.X = 0;
        }
    }

    private async Task AnimateIslandWidthAsync(string text, int durationMs = 190)
    {
        var targetWidth = MeasureIslandWidth(text);
        var animationBaseWidth = Math.Max(_islandWidth, targetWidth);
        var fromScale = _islandWidth / animationBaseWidth;
        var toScale = targetWidth / animationBaseWidth;

        IslandBackground.BeginAnimation(WidthProperty, null);
        IslandTextClip.BeginAnimation(WidthProperty, null);
        IslandBackgroundTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        IslandBackground.Width = animationBaseWidth;
        IslandTextClip.Width = animationBaseWidth;
        ApplyLyricTextWidth(animationBaseWidth);
        IslandBackgroundScaleTransform.ScaleX = fromScale;
        IslandBackgroundTranslateTransform.X = 0;

        var scaleAnimation = new DoubleAnimation
        {
            From = fromScale,
            To = toScale,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        await AnimateDoubleAsync(IslandBackgroundScaleTransform, ScaleTransform.ScaleXProperty, scaleAnimation);

        IslandBackground.BeginAnimation(WidthProperty, null);
        IslandTextClip.BeginAnimation(WidthProperty, null);
        IslandBackgroundScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IslandBackgroundTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        IslandBackground.Width = targetWidth;
        IslandTextClip.Width = targetWidth;
        ApplyLyricTextWidth(targetWidth);
        IslandBackgroundScaleTransform.ScaleX = 1;
        IslandBackgroundTranslateTransform.X = 0;
        _islandWidth = targetWidth;
    }

    private void ApplyLyricTextWidth(double islandWidth)
    {
        var textWidth = Math.Max(0, islandWidth - IslandDisplayMode.BackgroundHorizontalPadding);
        LyricStage.Width = textWidth;
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

    private double GetExtraIconSpace()
    {
        var extra = 0.0;
        if (LoadingIcon.Visibility == Visibility.Visible)
            extra += LoadingSpacerTargetWidth;
        if (PauseIcon.Visibility == Visibility.Visible)
            extra += PauseSpacerTargetWidth;
        return extra;
    }

    private double MeasureIslandWidth(string text)
    {
        var availableWidth = Math.Max(
            IslandDisplayMode.MinimumBackgroundWidth,
            RootGrid.ActualWidth > 0
                ? RootGrid.ActualWidth / GetEffectiveIslandScale()
                : ActualWidth / GetEffectiveIslandScale());

        if (string.IsNullOrWhiteSpace(text))
        {
            return Math.Min(IslandDisplayMode.MinimumBackgroundWidth, availableWidth);
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(
                IncomingLyricTextBlock.FontFamily,
                IncomingLyricTextBlock.FontStyle,
                IncomingLyricTextBlock.FontWeight,
                IncomingLyricTextBlock.FontStretch),
            IncomingLyricTextBlock.FontSize,
            WpfBrushes.White,
            pixelsPerDip);

        var desiredWidth = formattedText.WidthIncludingTrailingWhitespace + IslandDisplayMode.BackgroundHorizontalPadding + GetExtraIconSpace();
        return Math.Clamp(desiredWidth, IslandDisplayMode.MinimumBackgroundWidth, availableWidth);
    }

    private void ApplyLoadingState(bool immediate = false)
    {
        var isLoading = _viewModel.IsLoadingLyrics;

        LoadingIcon.BeginAnimation(OpacityProperty, null);
        LoadingSpacer.BeginAnimation(WidthProperty, null);
        LoadingSpinnerRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);

        if (isLoading)
        {
            IncomingLyricTextBlock.Foreground = GetLoadingTextBrush();
            OutgoingLyricTextBlock.Foreground = GetLoadingTextBrush();
            LyricStage.Opacity = 0.58;
            LoadingIcon.Visibility = Visibility.Visible;

            var iconFade = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(immediate ? 1 : 150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var spacerAnimation = new DoubleAnimation
            {
                To = LoadingSpacerTargetWidth,
                Duration = TimeSpan.FromMilliseconds(immediate ? 1 : 150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var spinnerRotation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromMilliseconds(850),
                RepeatBehavior = RepeatBehavior.Forever
            };

            LoadingIcon.BeginAnimation(OpacityProperty, iconFade);
            LoadingSpacer.BeginAnimation(WidthProperty, spacerAnimation);
            LoadingSpinnerRotateTransform.BeginAnimation(RotateTransform.AngleProperty, spinnerRotation);
            UpdateIslandWidth(_displayedLyricText, false);
            return;
        }

        LyricStage.Opacity = 1;
        ApplyAppearance();
        UpdateIslandWidth(_displayedLyricText, false);

        var iconFadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(immediate ? 1 : 180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        var spacerFadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(immediate ? 1 : 180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        iconFadeOut.Completed += (_, _) =>
        {
            LoadingIcon.Visibility = Visibility.Collapsed;
            LoadingSpinnerRotateTransform.Angle = 0;
        };

        LoadingIcon.BeginAnimation(OpacityProperty, iconFadeOut);
        LoadingSpacer.BeginAnimation(WidthProperty, spacerFadeOut);
    }

    private void HandlePlaybackPausedChanged(bool isPaused)
    {
        AnimatePlaybackStateChange(isPaused);

        if (isPaused && _settings.IslandTimeout > 0)
        {
            _timeoutTimer.Interval = TimeSpan.FromSeconds(_settings.IslandTimeout);
            _timeoutTimer.Start();
        }
        else
        {
            _timeoutTimer.Stop();
            if (_isTimeoutHidden)
            {
                _isTimeoutHidden = false;
                ApplyTargetIslandOpacity();
            }
        }
    }

    private void TimeoutTimer_OnTick(object? sender, EventArgs e)
    {
        _timeoutTimer.Stop();
        if (_viewModel.IsPlaybackPaused && _settings.IslandTimeout > 0)
        {
            _isTimeoutHidden = true;
            ApplyTargetIslandOpacity(TimeSpan.FromMilliseconds(400));
        }
    }

    private double GetTargetIslandOpacity()
    {
        if (_isNoLyricsAnimationActive)
            return 1;
        if (_isFullscreenHidden)
            return 0;
        if (!_isIslandContentVisible)
            return 0;
        if (_isTimeoutHidden)
            return 0;
        return _isPointerOverIsland ? IslandDisplayMode.GetEffectiveHoverOpacity(_settings.IslandHoverOpacity) : 1;
    }

    private void ApplyTargetIslandOpacity(TimeSpan? duration = null)
    {
        if (_isNoLyricsAnimationActive)
            return;

        var target = GetTargetIslandOpacity();
        if (Math.Abs(IslandLayer.Opacity - target) < 0.001)
        {
            IslandLayer.Opacity = target;
            return;
        }

        IslandLayer.BeginAnimation(OpacityProperty, null);
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = duration ?? TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = target > 0 ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        IslandLayer.BeginAnimation(OpacityProperty, animation);
    }

    private void AnimatePlaybackStateChange(bool isPaused)
    {
        if (_isPauseVisualActive == isPaused)
        {
            return;
        }

        PauseIcon.BeginAnimation(OpacityProperty, null);
        PauseSpacer.BeginAnimation(WidthProperty, null);

        var spacerAnimation = new DoubleAnimation
        {
            From = PauseSpacer.ActualWidth,
            To = isPaused ? PauseSpacerTargetWidth : 0,
            Duration = TimeSpan.FromMilliseconds(isPaused ? 260 : 150),
            EasingFunction = new CubicEase
            {
                EasingMode = isPaused ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        var iconAnimation = new DoubleAnimation
        {
            From = PauseIcon.Opacity,
            To = isPaused ? 1 : 0,
            BeginTime = isPaused ? TimeSpan.FromMilliseconds(110) : TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(isPaused ? 190 : 90),
            EasingFunction = new QuadraticEase
            {
                EasingMode = isPaused ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        spacerAnimation.Completed += (_, _) =>
        {
            PauseSpacer.Width = isPaused ? PauseSpacerTargetWidth : 0;
        };

        iconAnimation.Completed += (_, _) =>
        {
            PauseIcon.Opacity = isPaused ? 1 : 0;
            if (!isPaused)
            {
                PauseIcon.Visibility = Visibility.Collapsed;
            }
        };

        if (isPaused)
        {
            PauseIcon.Visibility = Visibility.Visible;
        }

        PauseSpacer.BeginAnimation(WidthProperty, spacerAnimation);
        PauseIcon.BeginAnimation(OpacityProperty, iconAnimation);
        UpdateIslandWidth(_displayedLyricText, false);
        _isPauseVisualActive = isPaused;
    }

    private void ApplyPlaybackStateVisual(bool immediate)
    {
        _isPauseVisualActive = _viewModel.IsPlaybackPaused;
        PauseSpacer.BeginAnimation(WidthProperty, null);
        PauseIcon.BeginAnimation(OpacityProperty, null);

        if (immediate)
        {
            PauseSpacer.Width = _isPauseVisualActive ? PauseSpacerTargetWidth : 0;
            PauseIcon.Opacity = _isPauseVisualActive ? 1 : 0;
            PauseIcon.Visibility = _isPauseVisualActive ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        AnimatePlaybackStateChange(_viewModel.IsPlaybackPaused);
    }

    private void HandleNoLyricsState()
    {
        if (_viewModel.IsLoadingLyrics)
        {
            CancelNoLyricsAnimation();
            return;
        }

        if (_viewModel.NoTimedLyricsFound && !_isNoLyricsAnimationActive)
        {
            StartNoLyricsAnimation();
        }
        else if (!_viewModel.NoTimedLyricsFound && _isNoLyricsAnimationActive)
        {
            CancelNoLyricsAnimation();
        }
    }

    private void StartNoLyricsAnimation()
    {
        if (_isNoLyricsAnimationActive)
        {
            return;
        }

        _isNoLyricsAnimationActive = true;

        // 1. Flash background red
        IslandRedOverlay.BeginAnimation(OpacityProperty, null);
        var redFlash = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(250),
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        IslandRedOverlay.BeginAnimation(OpacityProperty, redFlash);

        // 2. Vibrate the pill
        IslandLayerVibrateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        var vibrate = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(500)
        };
        vibrate.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        vibrate.KeyFrames.Add(new LinearDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        vibrate.KeyFrames.Add(new LinearDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        vibrate.KeyFrames.Add(new LinearDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        vibrate.KeyFrames.Add(new LinearDoubleKeyFrame(3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))));
        vibrate.KeyFrames.Add(new LinearDoubleKeyFrame(-2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
        vibrate.KeyFrames.Add(new LinearDoubleKeyFrame(2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360))));
        vibrate.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));
        IslandLayerVibrateTransform.BeginAnimation(TranslateTransform.XProperty, vibrate);

        // 3. Fade out the entire pill after 2 seconds
        IslandLayer.BeginAnimation(OpacityProperty, null);
        IslandLayer.Opacity = 1;
        var fadeOut = new DoubleAnimation
        {
            To = 0,
            BeginTime = TimeSpan.FromSeconds(2),
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        IslandLayer.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void CancelNoLyricsAnimation()
    {
        if (!_isNoLyricsAnimationActive)
        {
            return;
        }

        _isNoLyricsAnimationActive = false;

        // Stop all animations
        IslandRedOverlay.BeginAnimation(OpacityProperty, null);
        IslandLayerVibrateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        IslandLayer.BeginAnimation(OpacityProperty, null);

        // Restore visual state
        IslandRedOverlay.Opacity = 0;
        IslandLayerVibrateTransform.X = 0;
        var targetOpacity = GetTargetIslandOpacity();
        IslandLayer.Opacity = targetOpacity;

        // Fade the pill back in
        var fadeIn = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        IslandLayer.BeginAnimation(OpacityProperty, fadeIn);
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

        if (_settings.DisplayMode != DisplayMode.Island)
        {
            ((App)WpfApplication.Current).RestartDisplayWindow();
            return;
        }

        if (_appBarManager is not null && !string.IsNullOrWhiteSpace(_settings.IslandPreferredMonitorDeviceName))
        {
            if (_appBarManager.SetCurrentMonitor(_settings.IslandPreferredMonitorDeviceName))
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
        FullscreenTimer_OnTick(null, EventArgs.Empty);

        if (_settings.IslandTimeout <= 0)
        {
            _timeoutTimer.Stop();
            if (_isTimeoutHidden)
            {
                _isTimeoutHidden = false;
                ApplyTargetIslandOpacity();
            }
        }

        ApplyTargetIslandOpacity();
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
        persistedSettings.CustomBarHeight = incomingSettings.CustomBarHeight;
        persistedSettings.WindowedWidth = incomingSettings.WindowedWidth;
        persistedSettings.WindowedHeight = incomingSettings.WindowedHeight;
        persistedSettings.TaskbarMaximumWidth = incomingSettings.TaskbarMaximumWidth;
        persistedSettings.IslandMaximumWidth = incomingSettings.IslandMaximumWidth;
        persistedSettings.IslandScale = incomingSettings.IslandScale;
        persistedSettings.IslandContainerHeight = incomingSettings.IslandContainerHeight;
        persistedSettings.IslandCornerRadius = incomingSettings.IslandCornerRadius;
        persistedSettings.IslandHideInFullscreen = incomingSettings.IslandHideInFullscreen;
        persistedSettings.IslandTimeout = incomingSettings.IslandTimeout;
        persistedSettings.IslandHoverOpacity = incomingSettings.IslandHoverOpacity;
        persistedSettings.IslandAnimationMode = incomingSettings.IslandAnimationMode;
        persistedSettings.IslandAnimationManualSpeed = incomingSettings.IslandAnimationManualSpeed;
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

    private void EnsureTopmostOrder()
    {
        var hwnd = _hwndSource?.Handle ?? new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
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

    private void ScheduleTopmostRefreshBurst()
    {
        EnsureTopmostOrder();

        var retryShort = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        retryShort.Tick += (_, _) =>
        {
            retryShort.Stop();
            EnsureTopmostOrder();
        };
        retryShort.Start();

        var retryLong = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(240)
        };
        retryLong.Tick += (_, _) =>
        {
            retryLong.Stop();
            EnsureTopmostOrder();
        };
        retryLong.Start();
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out CursorPoint lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectNative lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetShellWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    public void ShowFromTray()
    {
        Show();
        WorkspaceVisibilityManager.PinToAllWorkspaces(this);
        Activate();
        EnsureTopmostOrder();
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

    public DisplayMode CurrentDisplayMode => DisplayMode.Island;

    public void SwitchDisplayMode(DisplayMode mode)
    {
        if (_settings.DisplayMode == mode) return;
        _settings.DisplayMode = mode;
        _appSettingsService.Save(_settings);
        ((App)WpfApplication.Current).RestartDisplayWindow();
    }
}
