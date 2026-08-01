using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

public partial class TaskbarWindow : Window, ITrayIconHost
{
    private static readonly TimeSpan MonitorWarningDuration = TimeSpan.FromSeconds(4);
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
    private readonly AppSettingsService _appSettingsService;
    private AppBarManager? _appBarManager;
    private HwndSource? _hwndSource;
    private IntPtr _foregroundHook;
    private WinEventDelegate? _foregroundHookDelegate;
    private AppSettings _settings;
    private string _displayedLyricText = string.Empty;
    private int _lyricTransitionVersion;
    private string? _lastMonitorWarningKey;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _trayIcon;

    private const double SlideWordOffset = 48;
    private const int SlideWordDurationMs = 130;
    private const int SlideWordInDurationMs = 100;
    private const int SlideWordStaggerMs = 75;

    public TaskbarWindow()
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
        _appBarManager = new AppBarManager(this, TaskbarDisplayMode.WindowHeight);
        ApplyMonitorSetting();
        PositionWindow();
        ApplyAppearance();
        EnsureTopmostOrder();
        WorkspaceVisibilityManager.PinToAllWorkspaces(this);
        _trayIcon = new TrayIcon(this);

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        IncomingLyricTextBlock.Text = _viewModel.TaskbarCurrentLine;
        _displayedLyricText = _viewModel.TaskbarCurrentLine;
        ApplyLoadingState(immediate: true);
        EnsureTopmostOrder();
        await _viewModel.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _monitorWarningTimer.Stop();
        _monitorWarningTimer.Tick -= MonitorWarningTimer_OnTick;
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
            var lyricColor = IsWindowsLightTheme()
                ? MediaColor.FromRgb(26, 26, 26)
                : MediaColor.FromRgb(245, 247, 250);
            var lyricBrush = new SolidColorBrush(lyricColor);
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

        var preferredMonitorDeviceName = _settings.TaskbarPreferredMonitorDeviceName ?? _settings.PreferredMonitorDeviceName;

        if (string.IsNullOrWhiteSpace(preferredMonitorDeviceName))
        {
            if (_appBarManager.CurrentMonitorDeviceName is not null)
            {
                _settings.TaskbarPreferredMonitorDeviceName = _appBarManager.CurrentMonitorDeviceName;
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
        var bounds = TaskbarDisplayMode.GetWindowBounds(monitor, _settings.TaskbarMaximumWidth, _settings.TaskbarHeight);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        EnsureTopmostOrder();
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

    private void HandleCurrentLineChanged(string newCurrentLine)
    {
        if (string.Equals(_displayedLyricText, newCurrentLine, StringComparison.Ordinal))
        {
            return;
        }

        var transitionVersion = ++_lyricTransitionVersion;

        if (_settings.TaskbarAnimationMode == IslandAnimationMode.SlideIn || _settings.TaskbarAnimationMode == IslandAnimationMode.SlideInManual)
        {
            _ = HandleCurrentLineChangedSlideIn(newCurrentLine, transitionVersion);
            return;
        }

        CancelLyricTransitionAnimations();

        OutgoingLyricTextBlock.Text = _displayedLyricText;
        OutgoingLyricTextBlock.Opacity = string.IsNullOrWhiteSpace(_displayedLyricText) ? 0 : 1;
        OutgoingLyricTranslateTransform.Y = 0;

        IncomingLyricTextBlock.Text = newCurrentLine;
        IncomingLyricTextBlock.Opacity = 0;
        IncomingLyricTranslateTransform.Y = TaskbarDisplayMode.GetSingleLineStartY();

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        var slideOut = new DoubleAnimation
        {
            To = -6,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(40),
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        var slideIn = new DoubleAnimation
        {
            From = TaskbarDisplayMode.GetSingleLineStartY(),
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(40),
            Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, fadeOut);
        OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);
        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, fadeIn);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);

        _displayedLyricText = newCurrentLine;
    }

    private async Task HandleCurrentLineChangedSlideIn(string newCurrentLine, int transitionVersion)
    {
        var outgoingText = _displayedLyricText;
        var hasOutgoing = !string.IsNullOrWhiteSpace(outgoingText);
        var hasIncoming = !string.IsNullOrWhiteSpace(newCurrentLine);
        var tempo = _settings.TaskbarAnimationMode == IslandAnimationMode.SlideInManual
            ? _settings.TaskbarAnimationManualSpeed
            : CalculateLyricTempoMultiplier();
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
            RestoreLyricTextBlocks(string.Empty);
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

        // 2. Slide in new words (slightly faster)
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

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            OpenSettingsWindow();
            e.Handled = true;
        }
    }

    private void Window_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        WpfApplication.Current.Shutdown();
        e.Handled = true;
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

        if (_settings.DisplayMode != DisplayMode.Taskbar)
        {
            ((App)WpfApplication.Current).RestartDisplayWindow();
            return;
        }

        if (_appBarManager is not null && !string.IsNullOrWhiteSpace(_settings.TaskbarPreferredMonitorDeviceName))
        {
            if (_appBarManager.SetCurrentMonitor(_settings.TaskbarPreferredMonitorDeviceName))
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
        persistedSettings.TaskbarHeight = incomingSettings.TaskbarHeight;
        persistedSettings.IslandMaximumWidth = incomingSettings.IslandMaximumWidth;
        persistedSettings.IslandScale = incomingSettings.IslandScale;
        persistedSettings.IslandContainerHeight = incomingSettings.IslandContainerHeight;
        persistedSettings.IslandCornerRadius = incomingSettings.IslandCornerRadius;
        persistedSettings.IslandHoverOpacity = incomingSettings.IslandHoverOpacity;
        persistedSettings.IslandAnimationMode = incomingSettings.IslandAnimationMode;
        persistedSettings.IslandAnimationManualSpeed = incomingSettings.IslandAnimationManualSpeed;
        persistedSettings.WallpaperPreferredMonitorDeviceName = incomingSettings.WallpaperPreferredMonitorDeviceName;
        persistedSettings.WallpaperMaximumWidth = incomingSettings.WallpaperMaximumWidth;
        persistedSettings.WallpaperScale = incomingSettings.WallpaperScale;
        persistedSettings.WallpaperContainerHeight = incomingSettings.WallpaperContainerHeight;
        persistedSettings.WallpaperAnimationMode = incomingSettings.WallpaperAnimationMode;
        persistedSettings.WallpaperAnimationManualSpeed = incomingSettings.WallpaperAnimationManualSpeed;
        persistedSettings.WallpaperTimeout = incomingSettings.WallpaperTimeout;
        persistedSettings.WallpaperHorizontalAlignment = incomingSettings.WallpaperHorizontalAlignment;
        persistedSettings.WallpaperVerticalAlignment = incomingSettings.WallpaperVerticalAlignment;
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
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOOWNERZORDER = 0x0200;

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

    public DisplayMode CurrentDisplayMode => DisplayMode.Taskbar;

    public void SwitchDisplayMode(DisplayMode mode)
    {
        if (_settings.DisplayMode == mode) return;
        _settings.DisplayMode = mode;
        _appSettingsService.Save(_settings);
        ((App)WpfApplication.Current).RestartDisplayWindow();
    }
}
