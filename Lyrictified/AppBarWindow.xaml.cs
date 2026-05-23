using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lyrictified.DisplayModes;
using Lyrictified.Interop;
using Lyrictified.Settings;
using Lyrictified.Styling;
using Lyrictified.ViewModels;

namespace Lyrictified;

public partial class AppBarWindow : Window
{
    private static readonly TimeSpan ControlFadeDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MonitorWarningDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NoLyricsHideDeferralDuration = TimeSpan.FromMilliseconds(2600);
    private static readonly Brush BlackoutBrush = new SolidColorBrush(Colors.Black);
    private static readonly Brush PreviewLyricBrush = new SolidColorBrush(Color.FromRgb(245, 247, 250));
    private static readonly Brush LoadingTextBrush = new SolidColorBrush(Color.FromRgb(150, 156, 164));
    private const double PauseShiftX = 42;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _controlsFadeTimer;
    private readonly DispatcherTimer _monitorWarningTimer;
    private readonly DispatcherTimer _deferredHideTimer;
    private readonly AppSettingsService _appSettingsService;
    private AppBarManager? _appBarManager;
    private WindowAppearanceManager? _appearanceManager;
    private AppSettings _settings;
    private string _displayedLyricText = string.Empty;
    private string _displayedNextLineText = string.Empty;
    private string? _lastMonitorWarningKey;
    private bool _isDeferringNoLyricsHideMode;
    private bool _wasLoadingLyrics;
    private bool _isNotFoundAnimationRunning;
    private bool _lastKnownLoadingLyrics;
    private bool _isBorderHiddenForBlackout;
    private bool _isPauseVisualActive;
    private bool _controlsVisible = true;
    private bool _isPointerOverWindow;
    private SettingsWindow? _settingsWindow;
    private bool IsPreviewModeEnabled => _settings.ShowNextLine;

    public AppBarWindow()
    {
        InitializeComponent();
        _appSettingsService = new AppSettingsService();
        _settings = _appSettingsService.Load();

        _viewModel = new MainViewModel();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = _viewModel;

        PauseIcon.Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "pause.png"), UriKind.Absolute));
        LoadingSpinnerImage.Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "loading.png"), UriKind.Absolute));

        _controlsFadeTimer = new DispatcherTimer
        {
            Interval = ControlFadeDelay
        };
        _controlsFadeTimer.Tick += ControlsFadeTimer_OnTick;

        _monitorWarningTimer = new DispatcherTimer
        {
            Interval = MonitorWarningDuration
        };
        _monitorWarningTimer.Tick += MonitorWarningTimer_OnTick;

        _deferredHideTimer = new DispatcherTimer
        {
            Interval = NoLyricsHideDeferralDuration
        };
        _deferredHideTimer.Tick += DeferredHideTimer_OnTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _appearanceManager = new WindowAppearanceManager(this);

        _appBarManager = new AppBarManager(this, AppBarDisplayMode.DefaultHeight);
        ApplyMonitorSetting();
        ApplyNextLineLayout();
        ApplyDisplayModeState();
        ApplyAppearance();
        UpdateMonitorControls();
        ApplyHideModeState();
        ApplyPlaybackStateVisual(immediate: true);

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        IncomingLyricTextBlock.Text = _viewModel.CurrentLine;
        PreviewLyricTextBlock.Text = _viewModel.NextLine;
        _displayedLyricText = _viewModel.CurrentLine;
        _displayedNextLineText = _viewModel.NextLine;
        _lastKnownLoadingLyrics = _viewModel.IsLoadingLyrics;
        ApplyNextLineLayout();
        UpdateAlbumArtAndCredit();
        ApplyLyricAlignment();
        ApplyPlaybackStateVisual(immediate: true);
        ApplyLoadingState(immediate: true);
        ShowControls(immediate: true);
        ScheduleControlsFade();
        await _viewModel.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _controlsFadeTimer.Stop();
        _controlsFadeTimer.Tick -= ControlsFadeTimer_OnTick;
        _monitorWarningTimer.Stop();
        _monitorWarningTimer.Tick -= MonitorWarningTimer_OnTick;
        _deferredHideTimer.Stop();
        _deferredHideTimer.Tick -= DeferredHideTimer_OnTick;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _appBarManager?.Dispose();
        _settingsWindow?.Close();
        _viewModel.Dispose();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _appBarManager?.RefreshMonitors();
        ApplyMonitorSetting();
        ApplyDisplayModeState();
        UpdateMonitorControls();
        RefreshSettingsWindowOptions();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        ApplyAppearance();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentLine))
        {
            if (Dispatcher.CheckAccess())
            {
                HandleCurrentLineChanged(_viewModel.CurrentLine);
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() => HandleCurrentLineChanged(_viewModel.CurrentLine));
            }
        }

        if (e.PropertyName == nameof(MainViewModel.NextLine))
        {
            if (Dispatcher.CheckAccess())
            {
                HandleNextLineChanged(_viewModel.NextLine);
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() => HandleNextLineChanged(_viewModel.NextLine));
            }
        }

        if (e.PropertyName == nameof(MainViewModel.NoTimedLyricsFound)
            || e.PropertyName == nameof(MainViewModel.IsLoadingLyrics))
        {
            var loadingJustFinishedWithoutLyrics =
                (_lastKnownLoadingLyrics && !_viewModel.IsLoadingLyrics && _viewModel.NoTimedLyricsFound)
                || (e.PropertyName == nameof(MainViewModel.NoTimedLyricsFound)
                    && _viewModel.NoTimedLyricsFound
                    && !_viewModel.IsLoadingLyrics
                    && _wasLoadingLyrics);

            if (loadingJustFinishedWithoutLyrics)
            {
                BeginNoLyricsHideDeferral("view-model transition");
            }

            _lastKnownLoadingLyrics = _viewModel.IsLoadingLyrics;

            if (Dispatcher.CheckAccess())
            {
                ApplyHideModeState();
                ApplyLoadingState();
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    ApplyHideModeState();
                    ApplyLoadingState();
                });
            }
        }

        if (e.PropertyName == nameof(MainViewModel.IsPlaybackPaused))
        {
            if (Dispatcher.CheckAccess())
            {
                AnimatePlaybackStateChange(_viewModel.IsPlaybackPaused);
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() => AnimatePlaybackStateChange(_viewModel.IsPlaybackPaused));
            }
        }

        if (e.PropertyName == nameof(MainViewModel.AlbumArt)
            || e.PropertyName == nameof(MainViewModel.SongTitle)
            || e.PropertyName == nameof(MainViewModel.SongArtist)
            || e.PropertyName == nameof(MainViewModel.StatusText))
        {
            if (Dispatcher.CheckAccess())
            {
                UpdateAlbumArtAndCredit();
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() => UpdateAlbumArtAndCredit());
            }
        }

        if (e.PropertyName == nameof(MainViewModel.Progress))
        {
            if (Dispatcher.CheckAccess())
            {
                UpdateProgressBar();
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() => UpdateProgressBar());
            }
        }
    }

    private void ApplyAppearance()
    {
        if (_appearanceManager is null)
        {
            return;
        }

        var isBlackout = ShouldUseBlackoutMode();
        if (isBlackout)
        {
            Background = BlackoutBrush;
            SurfaceBorder.Background = BlackoutBrush;
            SurfaceBorder.BorderBrush = BlackoutBrush;
        }
        else
        {
            var palette = _appearanceManager.Apply();
            Background = palette.WindowBackground;
            SurfaceBorder.Background = palette.SurfaceBackground;
            SurfaceBorder.BorderBrush = palette.SurfaceBorder;
            SettingsButton.Background = palette.ButtonBackground;
            SettingsButton.BorderBrush = palette.ButtonBorder;
            CloseButton.Background = palette.ButtonBackground;
            CloseButton.BorderBrush = palette.ButtonBorder;
            SwitchMonitorButton.Background = palette.ButtonBackground;
            SwitchMonitorButton.BorderBrush = palette.ButtonBorder;
        }

        if (isBlackout)
        {
            SettingsButton.Background = BlackoutBrush;
            SettingsButton.BorderBrush = BlackoutBrush;
            CloseButton.Background = BlackoutBrush;
            CloseButton.BorderBrush = BlackoutBrush;
            SwitchMonitorButton.Background = BlackoutBrush;
            SwitchMonitorButton.BorderBrush = BlackoutBrush;
        }

        LyricsContentPanel.Visibility = isBlackout ? Visibility.Collapsed : Visibility.Visible;
        AnimateBorderVisibility(isBlackout);
        if (!_viewModel.IsLoadingLyrics)
        {
            var isActiveBrush = new SolidColorBrush(Color.FromRgb(245, 247, 250));
            var dimBrush = _settings.KaraokeMode
                ? new SolidColorBrush(Color.FromArgb(90, 245, 247, 250))
                : new SolidColorBrush(Color.FromRgb(245, 247, 250));
            IncomingLyricTextBlock.Foreground = isActiveBrush;
            OutgoingLyricTextBlock.Foreground = dimBrush;
            PreviewLyricTextBlock.Foreground = _settings.KaraokeMode
                ? new SolidColorBrush(Color.FromArgb(60, 245, 247, 250))
                : PreviewLyricBrush;
        }
    }

    private void ApplyNextLineLayout()
    {
        var enabled = IsPreviewModeEnabled;
        var effectiveHeight = GetEffectiveBarHeight(enabled);
        LyricStage.Height = GetLyricStageHeight(enabled, effectiveHeight);
        PreviewLyricTextBlock.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        PreviewLyricTextBlock.Text = _displayedNextLineText;
        PreviewLyricTextBlock.FontSize = AppBarDisplayMode.PreviewLyricFontSize;
        PreviewLyricTranslateTransform.Y = AppBarDisplayMode.PreviewRestY;
        PreviewLyricTextBlock.Opacity = enabled && !string.IsNullOrWhiteSpace(_displayedNextLineText) ? AppBarDisplayMode.PreviewLyricOpacity : 0;
        PreviewLyricTextBlock.Foreground = PreviewLyricBrush;
        IncomingLyricTextBlock.FontSize = GetCurrentLyricFontSize();
        OutgoingLyricTextBlock.FontSize = GetCurrentLyricFontSize();
        _appBarManager?.SetHeight(effectiveHeight);
        ApplyDynamicScale(effectiveHeight);
        ApplyLyricAlignment();
    }

    private void ApplyMonitorSetting()
    {
        if (_appBarManager is null)
        {
            return;
        }

        var preferredMonitorDeviceName = _settings.AppBarPreferredMonitorDeviceName ?? _settings.PreferredMonitorDeviceName;

        if (string.IsNullOrWhiteSpace(preferredMonitorDeviceName))
        {
            if (_appBarManager.CurrentMonitorDeviceName is not null)
            {
                _settings.AppBarPreferredMonitorDeviceName = _appBarManager.CurrentMonitorDeviceName;
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

    private void UpdateAlbumArtAndCredit()
    {
        SongTitleTextBlock.Text = _viewModel.SongTitle;
        SongArtistTextBlock.Text = _viewModel.SongArtist;
        SongTimestampTextBlock.Text = FormatTimestamp(_viewModel.StatusText);
        AnimateAlbumArtTransition(_viewModel.AlbumArt);
        AlbumArtPanel.Visibility = _settings.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatTimestamp(string statusText)
    {
        if (string.IsNullOrWhiteSpace(statusText))
        {
            return string.Empty;
        }

        var dashIndex = statusText.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            return statusText[(dashIndex + 3)..];
        }

        return statusText;
    }

    private void ApplyLyricAlignment()
    {
        var alignment = _settings.LyricAlignment;

        var horizontalAlignment = alignment switch
        {
            LyricAlignment.Left => HorizontalAlignment.Left,
            LyricAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Stretch
        };

        var textAlignment = alignment switch
        {
            LyricAlignment.Left => TextAlignment.Left,
            LyricAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Center
        };

        IncomingLyricTextBlock.HorizontalAlignment = horizontalAlignment;
        IncomingLyricTextBlock.TextAlignment = textAlignment;
        OutgoingLyricTextBlock.HorizontalAlignment = horizontalAlignment;
        OutgoingLyricTextBlock.TextAlignment = textAlignment;
        PreviewLyricTextBlock.HorizontalAlignment = horizontalAlignment;
        PreviewLyricTextBlock.TextAlignment = textAlignment;
    }

    private void ApplyDynamicScale(int effectiveHeight)
    {
        var autoHeight = AppBarDisplayMode.GetAutomaticHeight(IsPreviewModeEnabled);

        if (effectiveHeight < autoHeight)
        {
            var scaleFactor = (double)effectiveHeight / autoHeight;
            scaleFactor = Math.Max(scaleFactor, 0.4);

            var artSize = Math.Max(32, 76 * scaleFactor);
            AlbumArtBorder.Width = artSize;
            AlbumArtBorder.Height = artSize;
            SongCreditPanel.Visibility = effectiveHeight < 50 ? Visibility.Collapsed : Visibility.Visible;

            var isCompact = effectiveHeight < 70;
            SongTitleTextBlock.FontSize = isCompact ? 11 : 13;
            SongArtistTextBlock.FontSize = isCompact ? 9 : 11;
            SongTimestampTextBlock.FontSize = isCompact ? 8 : 10;

            LyricsContentPanel.LayoutTransform = new ScaleTransform(scaleFactor, scaleFactor);
        }
        else
        {
            AlbumArtBorder.Width = 76;
            AlbumArtBorder.Height = 76;
            SongCreditPanel.Visibility = Visibility.Visible;
            SongTitleTextBlock.FontSize = 13;
            SongArtistTextBlock.FontSize = 11;
            SongTimestampTextBlock.FontSize = 10;
            LyricsContentPanel.LayoutTransform = null;
        }

        AlbumArtPanel.Visibility = _settings.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMonitorControls()
    {
        if (_appBarManager is null)
        {
            SwitchMonitorButton.Visibility = Visibility.Collapsed;
            return;
        }

        SwitchMonitorButton.Visibility = _appBarManager.MonitorCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        SwitchMonitorButton.ToolTip = _appBarManager.MonitorCount > 1
            ? $"Move to next monitor ({_appBarManager.CurrentMonitorIndex + 1}/{_appBarManager.MonitorCount})"
            : "Single monitor detected";
    }

    private void ApplyHideModeState()
    {
        if (_appBarManager is null)
        {
            return;
        }

        if (_isDeferringNoLyricsHideMode)
        {
            LogDebug("ApplyHideModeState deferred");
            if (!IsVisible)
            {
                Show();
            }

            ApplyAppearance();
            ApplyNextLineLayout();
            ApplyDisplayModeState();
            return;
        }

        var shouldHide = _settings.HideMode == HideMode.Hide && _viewModel.NoTimedLyricsFound && !_viewModel.IsLoadingLyrics;
        if (shouldHide)
        {
            LogDebug("ApplyHideModeState hiding window");
            _controlsFadeTimer.Stop();
            if (_appBarManager.IsAttached)
            {
                _appBarManager.Detach();
            }
            Hide();
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        ApplyAppearance();
        ApplyNextLineLayout();
        ApplyDisplayModeState();
    }

    private bool ShouldUseBlackoutMode()
    {
        if (_isDeferringNoLyricsHideMode)
        {
            return false;
        }

        return _settings.HideMode == HideMode.Blackout && _viewModel.NoTimedLyricsFound && !_viewModel.IsLoadingLyrics;
    }

    private void HandleCurrentLineChanged(string newCurrentLine)
    {
        if (_isNotFoundAnimationRunning)
        {
            return;
        }

        if (string.Equals(_displayedLyricText, newCurrentLine, StringComparison.Ordinal))
        {
            return;
        }

        if (_settings.ShowNextLine)
        {
            AnimateAppleMusicStyle(newCurrentLine);
            return;
        }

        AnimateSingleLine(newCurrentLine);
    }

    private void ApplyDisplayModeState()
    {
        if (_appBarManager is null)
        {
            return;
        }

        if (!_appBarManager.IsAttached)
        {
            _appBarManager.Attach();
        }

        _appBarManager.SetHeight(GetEffectiveBarHeight(IsPreviewModeEnabled));
        _appBarManager.Reposition();
    }

    private void HandleNextLineChanged(string newNextLine)
    {
        if (string.Equals(_displayedNextLineText, newNextLine, StringComparison.Ordinal))
        {
            return;
        }

        _displayedNextLineText = newNextLine;

        if (!_settings.ShowNextLine)
        {
            PreviewLyricTextBlock.Text = newNextLine;
            PreviewLyricTextBlock.Opacity = 0;
            return;
        }

        AnimatePreviewLine(newNextLine);
    }

    private void AnimateSingleLine(string newCurrentLine)
    {
        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);

        OutgoingLyricTextBlock.Text = _displayedLyricText;
        OutgoingLyricTextBlock.Opacity = string.IsNullOrEmpty(_displayedLyricText) ? 0 : 1;
        OutgoingLyricTranslateTransform.Y = 0;

        IncomingLyricTextBlock.Text = newCurrentLine;
        IncomingLyricTextBlock.FontSize = GetCurrentLyricFontSize();
        IncomingLyricTextBlock.Opacity = 0;
        IncomingLyricTranslateTransform.Y = GetSingleLineStartY();

        var fadeOut = new DoubleAnimation
        {
            From = OutgoingLyricTextBlock.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var slideOut = new DoubleAnimation
        {
            From = 0,
            To = -14,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(80),
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var slideIn = new DoubleAnimation
        {
            From = GetSingleLineStartY(),
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(80),
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };

        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, fadeOut);
        OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);
        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, fadeIn);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);

        _displayedLyricText = newCurrentLine;
    }

    private void AnimateAppleMusicStyle(string newCurrentLine)
    {
        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        OutgoingLyricTextBlock.BeginAnimation(TextBlock.FontSizeProperty, null);
        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        IncomingLyricTextBlock.BeginAnimation(TextBlock.FontSizeProperty, null);
        PreviewLyricTextBlock.BeginAnimation(OpacityProperty, null);
        PreviewLyricTextBlock.BeginAnimation(TextBlock.FontSizeProperty, null);
        OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        PreviewLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);

        var canPromotePreview = string.Equals(_displayedNextLineText, newCurrentLine, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(_displayedNextLineText);

        OutgoingLyricTextBlock.Text = _displayedLyricText;
        OutgoingLyricTextBlock.FontSize = AppBarDisplayMode.CurrentLyricFontSize;
        OutgoingLyricTextBlock.Opacity = string.IsNullOrEmpty(_displayedLyricText) ? 0 : 1;
        OutgoingLyricTranslateTransform.Y = 0;

        IncomingLyricTextBlock.Text = newCurrentLine;
        IncomingLyricTextBlock.FontSize = canPromotePreview ? AppBarDisplayMode.PreviewLyricFontSize : 24;
        IncomingLyricTextBlock.Opacity = canPromotePreview ? AppBarDisplayMode.PreviewLyricOpacity : 0;
        IncomingLyricTranslateTransform.Y = canPromotePreview ? AppBarDisplayMode.PreviewPromoteStartY : AppBarDisplayMode.IncomingPromoteStartY;

        PreviewLyricTextBlock.Text = _displayedNextLineText;
        PreviewLyricTextBlock.FontSize = AppBarDisplayMode.PreviewLyricFontSize;
        PreviewLyricTextBlock.Opacity = canPromotePreview ? AppBarDisplayMode.PreviewLyricOpacity : PreviewLyricTextBlock.Opacity;
        PreviewLyricTranslateTransform.Y = AppBarDisplayMode.PreviewRestY;

        var fadeOut = new DoubleAnimation
        {
            From = OutgoingLyricTextBlock.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var slideOut = new DoubleAnimation
        {
            From = 0,
            To = -16,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var promoteOpacity = new DoubleAnimation
        {
            From = canPromotePreview ? AppBarDisplayMode.PreviewLyricOpacity : 0,
            To = string.IsNullOrWhiteSpace(newCurrentLine) ? 0 : 1,
            BeginTime = TimeSpan.FromMilliseconds(60),
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };

        var promoteSlide = new DoubleAnimation
        {
            From = canPromotePreview ? AppBarDisplayMode.PreviewPromoteStartY : AppBarDisplayMode.IncomingPromoteStartY,
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(60),
            Duration = TimeSpan.FromMilliseconds(380),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };

        var promoteSize = new DoubleAnimation
        {
            From = canPromotePreview ? AppBarDisplayMode.PreviewLyricFontSize : 24,
            To = AppBarDisplayMode.CurrentLyricFontSize,
            BeginTime = TimeSpan.FromMilliseconds(60),
            Duration = TimeSpan.FromMilliseconds(380),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };

        var previewFadeOut = new DoubleAnimation
        {
            From = canPromotePreview ? AppBarDisplayMode.PreviewLyricOpacity : PreviewLyricTextBlock.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var completeAnimation = newCurrentLine;
        promoteSize.Completed += (_, _) =>
        {
            if (!string.Equals(_displayedLyricText, completeAnimation, StringComparison.Ordinal))
            {
                return;
            }

            ApplyCurrentLineVisualState();
            ApplyPreviewLineVisualState();
        };

        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, fadeOut);
        OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);
        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, promoteOpacity);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, promoteSlide);
        IncomingLyricTextBlock.BeginAnimation(TextBlock.FontSizeProperty, promoteSize);
        PreviewLyricTextBlock.BeginAnimation(OpacityProperty, previewFadeOut);

        _displayedLyricText = newCurrentLine;
    }

    private void AnimatePreviewLine(string newNextLine)
    {
        PreviewLyricTextBlock.BeginAnimation(OpacityProperty, null);
        PreviewLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);

        PreviewLyricTextBlock.Text = newNextLine;
        PreviewLyricTextBlock.FontSize = AppBarDisplayMode.PreviewLyricFontSize;
        PreviewLyricTextBlock.Opacity = 0;
        PreviewLyricTranslateTransform.Y = AppBarDisplayMode.PreviewEnterY;

        var opacityAnimation = new DoubleAnimation
        {
            From = 0,
            To = string.IsNullOrWhiteSpace(newNextLine) ? 0 : AppBarDisplayMode.PreviewLyricOpacity,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var slideAnimation = new DoubleAnimation
        {
            From = AppBarDisplayMode.PreviewEnterY,
            To = AppBarDisplayMode.PreviewRestY,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        PreviewLyricTextBlock.BeginAnimation(OpacityProperty, opacityAnimation);
        PreviewLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideAnimation);
    }

    private void ApplyCurrentLineVisualState()
    {
        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        OutgoingLyricTextBlock.BeginAnimation(TextBlock.FontSizeProperty, null);
        OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        OutgoingLyricTextBlock.Opacity = 0;
        OutgoingLyricTextBlock.Text = string.Empty;
        OutgoingLyricTranslateTransform.Y = 0;

        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        IncomingLyricTextBlock.BeginAnimation(TextBlock.FontSizeProperty, null);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        IncomingLyricTextBlock.Text = _displayedLyricText;
        IncomingLyricTextBlock.FontSize = GetCurrentLyricFontSize();
        IncomingLyricTextBlock.Opacity = string.IsNullOrWhiteSpace(_displayedLyricText) ? 0 : 1;
        IncomingLyricTranslateTransform.Y = 0;
    }

    private void ApplyPreviewLineVisualState()
    {
        PreviewLyricTextBlock.BeginAnimation(OpacityProperty, null);
        PreviewLyricTextBlock.BeginAnimation(TextBlock.FontSizeProperty, null);
        PreviewLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        PreviewLyricTextBlock.Text = _displayedNextLineText;
        PreviewLyricTextBlock.FontSize = AppBarDisplayMode.PreviewLyricFontSize;
        PreviewLyricTextBlock.Opacity = _settings.ShowNextLine && !string.IsNullOrWhiteSpace(_displayedNextLineText)
            ? AppBarDisplayMode.PreviewLyricOpacity
            : 0;
        PreviewLyricTranslateTransform.Y = AppBarDisplayMode.PreviewRestY;
    }

    private void AnimatePlaybackStateChange(bool isPaused)
    {
        if (_isPauseVisualActive == isPaused)
        {
            return;
        }

        PauseIcon.BeginAnimation(OpacityProperty, null);
        LyricsContentTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);

        var shiftAnimation = new DoubleAnimation
        {
            From = LyricsContentTranslateTransform.X,
            To = isPaused ? PauseShiftX : 0,
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

        shiftAnimation.Completed += (_, _) =>
        {
            LyricsContentTranslateTransform.X = isPaused ? PauseShiftX : 0;
        };

        iconAnimation.Completed += (_, _) =>
        {
            PauseIcon.Opacity = isPaused ? 1 : 0;
        };

        LyricsContentTranslateTransform.BeginAnimation(TranslateTransform.XProperty, shiftAnimation);
        PauseIcon.BeginAnimation(OpacityProperty, iconAnimation);
        _isPauseVisualActive = isPaused;
    }

    private void ApplyPlaybackStateVisual(bool immediate)
    {
        _isPauseVisualActive = _viewModel.IsPlaybackPaused;
        LyricsContentTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        PauseIcon.BeginAnimation(OpacityProperty, null);

        if (immediate)
        {
            LyricsContentTranslateTransform.X = _isPauseVisualActive ? PauseShiftX : 0;
            PauseIcon.Opacity = _isPauseVisualActive ? 1 : 0;
            return;
        }

        AnimatePlaybackStateChange(_viewModel.IsPlaybackPaused);
    }

    private void ApplyLoadingState(bool immediate = false)
    {
        var isLoading = _viewModel.IsLoadingLyrics;
        var loadingJustFinished = _wasLoadingLyrics && !isLoading;
        _wasLoadingLyrics = isLoading;
        LogDebug($"ApplyLoadingState loading={isLoading} immediate={immediate} noLyrics={_viewModel.NoTimedLyricsFound} defer={_isDeferringNoLyricsHideMode}");

        LoadingSpinnerImage.BeginAnimation(OpacityProperty, null);
        LoadingSpinnerRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);

        if (isLoading)
        {
            EndNoLyricsHideDeferral("loading started");
            IncomingLyricTextBlock.Foreground = LoadingTextBrush;
            OutgoingLyricTextBlock.Foreground = LoadingTextBrush;
            PreviewLyricTextBlock.Foreground = LoadingTextBrush;
            LyricsContentPanel.Opacity = 0.58;
            LoadingOverlay.Visibility = Visibility.Visible;

            var spinnerFade = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(immediate ? 1 : 160),
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

        if (loadingJustFinished)
        {
            BeginNoLyricsHideDeferral("loading finished pending result");
        }

        LyricsContentPanel.Opacity = 1;
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

            var shouldPlayNotFoundAnimation = !_viewModel.IsLoadingLyrics && _viewModel.NoTimedLyricsFound;
            if (shouldPlayNotFoundAnimation)
            {
                LogDebug("ApplyLoadingState triggering not-found animation");
                PlayNoLyricsFoundAnimation();
            }
            else
            {
                EndNoLyricsHideDeferral("loading finished without animation");
            }
        };

        LoadingSpinnerImage.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void PlayNoLyricsFoundAnimation()
    {
        LogDebug("PlayNoLyricsFoundAnimation started");
        _isNotFoundAnimationRunning = true;

        OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, null);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);

        OutgoingLyricTextBlock.Opacity = 0;
        OutgoingLyricTextBlock.Text = string.Empty;
        OutgoingLyricTranslateTransform.Y = 0;

        IncomingLyricTextBlock.Text = _displayedLyricText;
        IncomingLyricTextBlock.Opacity = string.IsNullOrWhiteSpace(_displayedLyricText) ? 0 : 1;
        IncomingLyricTextBlock.FontSize = GetCurrentLyricFontSize();
        IncomingLyricTranslateTransform.X = 0;
        IncomingLyricTranslateTransform.Y = 0;

        var fadeOutCurrent = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(90),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOutCurrent.Completed += (_, _) =>
        {
            IncomingLyricTextBlock.Text = "Couldn't find matching lyrics!";
            IncomingLyricTextBlock.FontSize = GetCurrentLyricFontSize();
            IncomingLyricTranslateTransform.X = 0;
            IncomingLyricTranslateTransform.Y = 0;

            var fadeInMessage = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(110),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            fadeInMessage.Completed += (_, _) =>
            {
                var shakeAnimation = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(350)
                };
                shakeAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.0)));
                shakeAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(-8, KeyTime.FromPercent(0.14)));
                shakeAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(7, KeyTime.FromPercent(0.28)));
                shakeAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(-6, KeyTime.FromPercent(0.42)));
                shakeAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(5, KeyTime.FromPercent(0.56)));
                shakeAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(-3, KeyTime.FromPercent(0.72)));
                shakeAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(2, KeyTime.FromPercent(0.86)));
                shakeAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));

                var fadeOutMessage = new DoubleAnimation
                {
                    BeginTime = TimeSpan.FromMilliseconds(1350),
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(170),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };

        fadeOutMessage.Completed += (_, _) =>
        {
            _displayedLyricText = string.Empty;
            IncomingLyricTextBlock.Text = string.Empty;
            IncomingLyricTextBlock.Opacity = 0;
                    IncomingLyricTranslateTransform.X = 0;
                    _isNotFoundAnimationRunning = false;
                    EndNoLyricsHideDeferral("animation completed");
                    ApplyHideModeState();
                };

                IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.XProperty, shakeAnimation);
                IncomingLyricTextBlock.BeginAnimation(OpacityProperty, fadeOutMessage);
            };

            IncomingLyricTextBlock.BeginAnimation(OpacityProperty, fadeInMessage);
        };

        IncomingLyricTextBlock.BeginAnimation(OpacityProperty, fadeOutCurrent);
    }

    private void ControlsFadeTimer_OnTick(object? sender, EventArgs e)
    {
        _controlsFadeTimer.Stop();
        if (_isPointerOverWindow)
        {
            HideControls();
        }
    }

    private void MonitorWarningTimer_OnTick(object? sender, EventArgs e)
    {
        _monitorWarningTimer.Stop();
        HideMonitorWarning();
    }

    private void DeferredHideTimer_OnTick(object? sender, EventArgs e)
    {
        if (_isNotFoundAnimationRunning)
        {
            _deferredHideTimer.Stop();
            _deferredHideTimer.Start();
            LogDebug("DeferredHideTimer postponed because animation is still running");
            return;
        }

        EndNoLyricsHideDeferral("timer elapsed");
        ApplyHideModeState();
    }

    private void ShowControls(bool immediate = false)
    {
        _controlsVisible = true;
        ControlButtonsPanel.IsHitTestVisible = true;
        ControlButtonsPanel.BeginAnimation(OpacityProperty, null);

        if (immediate)
        {
            ControlButtonsPanel.Opacity = 1;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ControlButtonsPanel.BeginAnimation(OpacityProperty, animation);
    }

    private void AnimateBorderVisibility(bool hideBorder)
    {
        if (_isBorderHiddenForBlackout == hideBorder)
        {
            return;
        }

        _isBorderHiddenForBlackout = hideBorder;
        SurfaceBorder.BeginAnimation(Border.BorderThicknessProperty, null);

        var animation = new ThicknessAnimation
        {
            To = hideBorder ? new Thickness(0) : new Thickness(0, 0, 0, 1),
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase
            {
                EasingMode = hideBorder ? EasingMode.EaseIn : EasingMode.EaseOut
            }
        };

        SurfaceBorder.BeginAnimation(Border.BorderThicknessProperty, animation);
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
        fadeOut.Completed += (_, _) =>
        {
            MonitorWarningBanner.Visibility = Visibility.Collapsed;
        };
        MonitorWarningBanner.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void HideControls()
    {
        _controlsVisible = false;
        var animation = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        animation.Completed += (_, _) =>
        {
            if (!_controlsVisible)
            {
                ControlButtonsPanel.IsHitTestVisible = false;
            }
        };
        ControlButtonsPanel.BeginAnimation(OpacityProperty, animation);
    }

    private void ScheduleControlsFade()
    {
        _controlsFadeTimer.Stop();
        if (_isPointerOverWindow)
        {
            _controlsFadeTimer.Start();
        }
    }

    private void UpdateProgressBar()
    {
        SongProgressBar.Value = _viewModel.Progress;
    }

    private void UpdateHoverEffect(bool isHovering)
    {
        SurfaceBorder.BeginAnimation(Border.BorderBrushProperty, null);
        SurfaceBorder.BeginAnimation(Border.BackgroundProperty, null);

        if (isHovering)
        {
            var bgAnimation = new ColorAnimation
            {
                To = Color.FromArgb(140, 16, 26, 37),
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var borderAnimation = new ColorAnimation
            {
                To = Color.FromArgb(160, 48, 70, 92),
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            ((SolidColorBrush)SurfaceBorder.Background).BeginAnimation(SolidColorBrush.ColorProperty, bgAnimation);
            ((SolidColorBrush)SurfaceBorder.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty, borderAnimation);
        }
        else
        {
            if (ShouldUseBlackoutMode())
            {
                return;
            }

            var bgAnimation = new ColorAnimation
            {
                To = Color.FromArgb(102, 16, 26, 37),
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var borderAnimation = new ColorAnimation
            {
                To = Color.FromArgb(138, 48, 70, 92),
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            ((SolidColorBrush)SurfaceBorder.Background).BeginAnimation(SolidColorBrush.ColorProperty, bgAnimation);
            ((SolidColorBrush)SurfaceBorder.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty, borderAnimation);
        }
    }

    private void AnimateAlbumArtTransition(byte[]? newArt)
    {
        if (newArt is null || newArt.Length == 0)
        {
            var fadeOut = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                AlbumArtImage.Source = null;
            };
            AlbumArtImage.BeginAnimation(OpacityProperty, fadeOut);
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new System.IO.MemoryStream(newArt);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            if (AlbumArtImage.Source is null)
            {
                AlbumArtImage.Source = bitmap;
                AlbumArtImage.Opacity = 0;
                var fadeIn = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                AlbumArtImage.BeginAnimation(OpacityProperty, fadeIn);
                return;
            }

            AlbumArtOverlayImage.Source = bitmap;
            AlbumArtOverlayBorder.Opacity = 0;
            AlbumArtOverlayBorder.BeginAnimation(OpacityProperty, null);

            var overlayFadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(350),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            overlayFadeIn.Completed += (_, _) =>
            {
                AlbumArtImage.Source = bitmap;
                AlbumArtImage.Opacity = 1;
                AlbumArtOverlayBorder.Opacity = 0;
                AlbumArtOverlayImage.Source = null;
            };

            AlbumArtOverlayBorder.BeginAnimation(OpacityProperty, overlayFadeIn);
        }
        catch
        {
            AlbumArtImage.Source = null;
        }
    }

    private void Window_OnMouseEnter(object sender, MouseEventArgs e)
    {
        _isPointerOverWindow = true;
        ShowControls();
        ScheduleControlsFade();
        UpdateHoverEffect(true);
    }

    private void Window_OnMouseLeave(object sender, MouseEventArgs e)
    {
        _isPointerOverWindow = false;
        _controlsFadeTimer.Stop();
        HideControls();
        UpdateHoverEffect(false);
    }

    private void Window_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPointerOverWindow)
        {
            _isPointerOverWindow = true;
            ShowControls();
        }
        else if (ControlButtonsPanel.Opacity < 0.99)
        {
            ShowControls();
        }

        ScheduleControlsFade();
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow
            {
                Owner = this
            };
            _settingsWindow.SettingsChanged += SettingsWindow_OnSettingsChanged;
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            RefreshSettingsWindowOptions();
            _settingsWindow.Show();
            return;
        }

        RefreshSettingsWindowOptions();
        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
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

        if (_settings.DisplayMode != DisplayMode.AppBar)
        {
            ((App)Application.Current).RestartDisplayWindow();
            return;
        }

        if (_appBarManager is not null && !string.IsNullOrWhiteSpace(_settings.AppBarPreferredMonitorDeviceName))
        {
            if (_appBarManager.SetCurrentMonitor(_settings.AppBarPreferredMonitorDeviceName))
            {
                _lastMonitorWarningKey = null;
            }
            else
            {
                ApplyMonitorSetting();
            }

            UpdateMonitorControls();
            RefreshSettingsWindowOptions();
        }

        _displayedLyricText = _viewModel.CurrentLine;
        _displayedNextLineText = _viewModel.NextLine;
        IncomingLyricTextBlock.Text = _displayedLyricText;
        IncomingLyricTextBlock.FontSize = AppBarDisplayMode.CurrentLyricFontSize;
        IncomingLyricTextBlock.Opacity = 1;
        IncomingLyricTranslateTransform.Y = 0;
        OutgoingLyricTextBlock.Opacity = 0;
        PreviewLyricTextBlock.Text = _displayedNextLineText;
        ApplyNextLineLayout();
        UpdateAlbumArtAndCredit();
        ApplyLyricAlignment();
        ApplyDisplayModeState();
        ApplyPlaybackStateVisual(immediate: true);
        ApplyLoadingState(immediate: true);
        ApplyHideModeState();
    }

    private AppSettings MergeSettings(AppSettings incomingSettings)
    {
        var persistedSettings = _appSettingsService.Load();
        persistedSettings.HideMode = incomingSettings.HideMode;
        persistedSettings.DisplayMode = incomingSettings.DisplayMode;
        persistedSettings.ShowNextLine = incomingSettings.ShowNextLine;
        persistedSettings.AppBarPreferredMonitorDeviceName = incomingSettings.AppBarPreferredMonitorDeviceName;
        persistedSettings.TaskbarPreferredMonitorDeviceName = incomingSettings.TaskbarPreferredMonitorDeviceName;
        persistedSettings.CustomBarHeight = incomingSettings.CustomBarHeight;
        persistedSettings.TaskbarMaximumWidth = incomingSettings.TaskbarMaximumWidth;
        persistedSettings.LyricAlignment = incomingSettings.LyricAlignment;
        persistedSettings.ShowAlbumArt = incomingSettings.ShowAlbumArt;
        persistedSettings.KaraokeMode = incomingSettings.KaraokeMode;
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

    private int GetEffectiveBarHeight(bool showNextLine)
    {
        return AppBarDisplayMode.GetEffectiveHeight(showNextLine, _settings.CustomBarHeight);
    }

    private double GetLyricStageHeight(bool showNextLine, int effectiveBarHeight)
    {
        return AppBarDisplayMode.GetStageHeight(showNextLine, effectiveBarHeight);
    }

    private double GetCurrentLyricFontSize()
    {
        return AppBarDisplayMode.CurrentLyricFontSize;
    }

    private double GetSingleLineStartY()
    {
        return AppBarDisplayMode.IncomingSingleLineStartY;
    }

    private void SwitchMonitorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_appBarManager?.MoveToNextMonitor() == true)
        {
            _settings.AppBarPreferredMonitorDeviceName = _appBarManager.CurrentMonitorDeviceName;
            _settings.PreferredMonitorDeviceName = null;
            _appSettingsService.Save(_settings);
            _lastMonitorWarningKey = null;
            ApplyDisplayModeState();
            UpdateMonitorControls();
            RefreshSettingsWindowOptions();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BeginNoLyricsHideDeferral(string reason)
    {
        _isDeferringNoLyricsHideMode = true;
        _deferredHideTimer.Stop();
        _deferredHideTimer.Start();
        LogDebug($"BeginNoLyricsHideDeferral reason={reason}");
    }

    private void EndNoLyricsHideDeferral(string reason)
    {
        _deferredHideTimer.Stop();
        _isDeferringNoLyricsHideMode = false;
        LogDebug($"EndNoLyricsHideDeferral reason={reason}");
    }

    private static void LogDebug(string message)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Lyrictified");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "ui.log");
            File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
