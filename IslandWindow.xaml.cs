using Microsoft.Win32;
using System.Globalization;
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
    private const double HoverFadeOpacity = 0.16;
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
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _trayIcon;

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
        EnsureTopmostOrder();
        _hoverTimer.Start();
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
        ApplyLoadingState(immediate: true);
        EnsureTopmostOrder();
        await _viewModel.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _monitorWarningTimer.Stop();
        _monitorWarningTimer.Tick -= MonitorWarningTimer_OnTick;
        _hoverTimer.Stop();
        _hoverTimer.Tick -= HoverTimer_OnTick;
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
        IslandLayer.BeginAnimation(OpacityProperty, null);
        var animation = new DoubleAnimation
        {
            To = isHovered ? HoverFadeOpacity : 1,
            Duration = TimeSpan.FromMilliseconds(isHovered ? 120 : 180),
            EasingFunction = new QuadraticEase
            {
                EasingMode = isHovered ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        IslandLayer.BeginAnimation(OpacityProperty, animation);
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

    private async void HandleCurrentLineChanged(string newCurrentLine)
    {
        if (string.Equals(_displayedLyricText, newCurrentLine, StringComparison.Ordinal))
        {
            return;
        }

        var transitionVersion = ++_lyricTransitionVersion;

        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        IslandBackground.BeginAnimation(WidthProperty, null);
        IslandTextClip.BeginAnimation(WidthProperty, null);
        IslandBackgroundScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IslandBackgroundTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);

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

        await AnimateIslandWidthAsync(newCurrentLine);

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

    private void UpdateIslandWidth(string text, bool immediate)
    {
        var width = MeasureIslandWidth(text);
        _islandWidth = width;

        IslandBackground.BeginAnimation(WidthProperty, null);
        IslandTextClip.BeginAnimation(WidthProperty, null);
        IslandBackground.Width = width;
        IslandTextClip.Width = width;

        if (immediate)
        {
            IslandBackgroundTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
            IslandBackgroundScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            IslandBackgroundScaleTransform.ScaleX = 1;
            IslandBackgroundTranslateTransform.X = 0;
        }
    }

    private async Task AnimateIslandWidthAsync(string text)
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
        IslandBackgroundScaleTransform.ScaleX = fromScale;
        IslandBackgroundTranslateTransform.X = 0;

        var scaleAnimation = new DoubleAnimation
        {
            From = fromScale,
            To = toScale,
            Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        await AnimateDoubleAsync(IslandBackgroundScaleTransform, ScaleTransform.ScaleXProperty, scaleAnimation);

        IslandBackground.BeginAnimation(WidthProperty, null);
        IslandTextClip.BeginAnimation(WidthProperty, null);
        IslandBackgroundScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IslandBackgroundTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        IslandBackground.Width = targetWidth;
        IslandTextClip.Width = targetWidth;
        IslandBackgroundScaleTransform.ScaleX = 1;
        IslandBackgroundTranslateTransform.X = 0;
        _islandWidth = targetWidth;
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

    private double MeasureIslandWidth(string text)
    {
        var availableWidth = Math.Max(
            IslandDisplayMode.MinimumBackgroundWidth,
            (ActualWidth / GetEffectiveIslandScale()) - (IslandDisplayMode.HorizontalMargin * 2));

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

        var desiredWidth = formattedText.WidthIncludingTrailingWhitespace + IslandDisplayMode.BackgroundHorizontalPadding;
        return Math.Clamp(desiredWidth, IslandDisplayMode.MinimumBackgroundWidth, availableWidth);
    }

    private void ApplyLoadingState(bool immediate = false)
    {
        var isLoading = _viewModel.IsLoadingLyrics;

        LoadingSpinnerImage.BeginAnimation(OpacityProperty, null);
        LoadingSpinnerRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);

        if (isLoading)
        {
            IncomingLyricTextBlock.Foreground = GetLoadingTextBrush();
            OutgoingLyricTextBlock.Foreground = GetLoadingTextBrush();
            LyricStage.Opacity = 0.58;
            LoadingOverlay.Visibility = Visibility.Visible;

            var spinnerFade = new DoubleAnimation
            {
                To = 1,
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

            LoadingSpinnerImage.BeginAnimation(OpacityProperty, spinnerFade);
            LoadingSpinnerRotateTransform.BeginAnimation(RotateTransform.AngleProperty, spinnerRotation);
            return;
        }

        LyricStage.Opacity = 1;
        ApplyAppearance();

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(immediate ? 1 : 180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            LoadingSpinnerRotateTransform.Angle = 0;
        };

        LoadingSpinnerImage.BeginAnimation(OpacityProperty, fadeOut);
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
        RefreshSettingsWindowOptions();
        ApplyAppearance();
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
