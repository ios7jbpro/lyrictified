using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lyrictified.DisplayModes;
using Lyrictified.Interop;
using Lyrictified.Models;
using Lyrictified.Services;
using Lyrictified.Settings;
using Lyrictified.Styling;
using Lyrictified.ViewModels;
using WpfBrush = System.Windows.Media.Brush;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using MediaColor = System.Windows.Media.Color;
using WpfApplication = System.Windows.Application;

namespace Lyrictified;

public partial class AppBarWindow : Window, ITrayIconHost
{
    private static readonly TimeSpan ControlFadeDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MonitorWarningDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NoLyricsHideDeferralDuration = TimeSpan.FromMilliseconds(2600);
    private static readonly WpfBrush BlackoutBrush = new SolidColorBrush(Colors.Black);
    private static readonly WpfBrush PreviewLyricBrush = new SolidColorBrush(MediaColor.FromRgb(245, 247, 250));
    private static readonly WpfBrush LoadingTextBrush = new SolidColorBrush(MediaColor.FromRgb(150, 156, 164));

    private static readonly WpfBrush LightForegroundBrush = new SolidColorBrush(MediaColor.FromRgb(245, 247, 250));
    private static readonly WpfBrush DarkForegroundBrush = new SolidColorBrush(MediaColor.FromRgb(26, 26, 26));
    private static readonly WpfBrush LightPreviewBrush = new SolidColorBrush(MediaColor.FromRgb(245, 247, 250));
    private static readonly WpfBrush DarkPreviewBrush = new SolidColorBrush(MediaColor.FromRgb(100, 100, 100));
    private static readonly WpfBrush LightSongArtistBrush = new SolidColorBrush(MediaColor.FromRgb(157, 177, 196));
    private static readonly WpfBrush DarkSongArtistBrush = new SolidColorBrush(MediaColor.FromRgb(100, 100, 100));
    private static readonly WpfBrush LightSongTimestampBrush = new SolidColorBrush(MediaColor.FromRgb(122, 143, 163));
    private static readonly WpfBrush DarkSongTimestampBrush = new SolidColorBrush(MediaColor.FromRgb(120, 120, 120));
    private static readonly WpfBrush LightButtonForegroundBrush = new SolidColorBrush(MediaColor.FromRgb(245, 247, 250));
    private static readonly WpfBrush DarkButtonForegroundBrush = new SolidColorBrush(MediaColor.FromRgb(26, 26, 26));
    private static readonly WpfBrush LightPauseIconBrush = new SolidColorBrush(MediaColor.FromRgb(245, 247, 250));
    private static readonly WpfBrush DarkPauseIconBrush = new SolidColorBrush(MediaColor.FromRgb(26, 26, 26));

    private bool _exactModeIsBrightBackground;

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _controlsFadeTimer;
    private readonly DispatcherTimer _monitorWarningTimer;
    private readonly DispatcherTimer _deferredHideTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _adaptToContentTimer;
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
    private byte[]? _lastAlbumArtData;

    private bool IsAlbumArtDifferent(byte[]? a, byte[]? b)
    {
        if (a is null && b is null) return false;
        if (a is null || b is null) return true;
        if (a.Length != b.Length) return true;
        return !a.AsSpan().SequenceEqual(b);
    }
    private string _lastProgressSongId = string.Empty;
    private TrayIcon? _trayIcon;
    private bool IsPreviewModeEnabled => IsTtmlLayoutActive || _settings.ShowNextLine;
    private DispatcherTimer? _wordAnimTimer;
    private double[]? _wordCharOpacities;
    private double[]? _ttmlPrimaryWordCharOpacities;
    private double[]? _ttmlSecondaryWordCharOpacities;
    private string? _ttmlPrimaryLineKey;
    private string? _ttmlSecondaryLineKey;
    private DateTime _lastLineChangeTimestamp = DateTime.MinValue;
    private bool IsTtmlLayoutActive => _settings.DisplayTtmlLyrics
        && _viewModel.HasTtmlLyrics
        && !_viewModel.IsLoadingLyrics
        && !_viewModel.NoTimedLyricsFound;

    private bool IsCleanedLrcActive => !IsTtmlLayoutActive && _viewModel.CleanedLrcLyrics.Count > 0;

    private string GetEffectiveCurrentLine() =>
        IsCleanedLrcActive ? _viewModel.TaskbarCurrentLine : _viewModel.CurrentLine;

    private string GetEffectiveNextLine() =>
        IsCleanedLrcActive ? _viewModel.NonTtmlNextLine : _viewModel.NextLine;

    public AppBarWindow()
    {
        InitializeComponent();
        _appSettingsService = new AppSettingsService();
        _settings = _appSettingsService.Load();

        _viewModel = new MainViewModel();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = _viewModel;

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

        _progressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _progressTimer.Tick += ProgressTimer_OnTick;

        _adaptToContentTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _adaptToContentTimer.Tick += AdaptToContentTimer_OnTick;

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
        WorkspaceVisibilityManager.PinToAllWorkspaces(this);

        _trayIcon = new TrayIcon(this);

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsTtmlLayoutActive)
        {
            RenderTtmlLyrics();
        }
        else if (_viewModel.WordByWordMode && _viewModel.CurrentLyricLine?.Words?.Count > 0)
        {
            SetWordByWordInlines();
        }
        else
        {
            IncomingLyricTextBlock.Text = GetEffectiveCurrentLine();
        }
        PreviewLyricTextBlock.Text = GetEffectiveNextLine();
        _displayedLyricText = GetEffectiveCurrentLine();
        _displayedNextLineText = GetEffectiveNextLine();
        _lastKnownLoadingLyrics = _viewModel.IsLoadingLyrics;
        ApplyNextLineLayout();
        ApplyLyricLayoutMode();
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
        _progressTimer.Stop();
        _progressTimer.Tick -= ProgressTimer_OnTick;
        _adaptToContentTimer.Stop();
        _adaptToContentTimer.Tick -= AdaptToContentTimer_OnTick;
        StopWordAnim();
        if (_wordAnimTimer is not null)
            _wordAnimTimer.Tick -= WordAnimTimer_Tick;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _appBarManager?.Dispose();
        _settingsWindow?.Close();
        _viewModel.Dispose();
        _trayIcon?.Dispose();
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
        if (e.PropertyName == nameof(MainViewModel.CurrentLine)
            || e.PropertyName == nameof(MainViewModel.TaskbarCurrentLine))
        {
            if (Dispatcher.CheckAccess())
            {
                if (IsTtmlLayoutActive)
                {
                    RenderTtmlLyrics();
                }
                else
                {
                    HandleCurrentLineChanged(GetEffectiveCurrentLine());
                }
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (IsTtmlLayoutActive)
                    {
                        RenderTtmlLyrics();
                    }
                    else
                    {
                        HandleCurrentLineChanged(GetEffectiveCurrentLine());
                    }
                });
            }
        }

        if (e.PropertyName == nameof(MainViewModel.CurrentWordIndex))
        {
            if (IsTtmlLayoutActive || _viewModel.WordByWordMode)
            {
                StartWordAnim();
            }
        }

        if (e.PropertyName == nameof(MainViewModel.NextLine)
            || e.PropertyName == nameof(MainViewModel.NonTtmlNextLine))
        {
            if (IsTtmlLayoutActive)
            {
                if (Dispatcher.CheckAccess())
                {
                    RenderTtmlLyrics();
                }
                else
                {
                    _ = Dispatcher.InvokeAsync(RenderTtmlLyrics);
                }

                return;
            }

            if (Dispatcher.CheckAccess())
            {
                HandleNextLineChanged(GetEffectiveNextLine());
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() => HandleNextLineChanged(GetEffectiveNextLine()));
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

            UpdateLastSearchInfo();

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

        if (e.PropertyName == nameof(MainViewModel.ActiveLyricLines)
            || e.PropertyName == nameof(MainViewModel.HasTtmlLyrics)
            || e.PropertyName == nameof(MainViewModel.Lyrics))
        {
            if (Dispatcher.CheckAccess())
            {
                ApplyNextLineLayout();
                if (IsTtmlLayoutActive)
                {
                    RenderTtmlLyrics();
                }
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    ApplyNextLineLayout();
                    if (IsTtmlLayoutActive)
                    {
                        RenderTtmlLyrics();
                    }
                });
            }
        }

        if (e.PropertyName == nameof(MainViewModel.IsPlaybackPaused))
        {
            if (Dispatcher.CheckAccess())
            {
                AnimatePlaybackStateChange(_viewModel.IsPlaybackPaused);
                ApplyHideModeState();
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    AnimatePlaybackStateChange(_viewModel.IsPlaybackPaused);
                    ApplyHideModeState();
                });
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
        var hideBorder = isBlackout || !_settings.AppBarShowProgressBar;

        if (isBlackout)
        {
            StopAdaptiveBackground();
            Background = BlackoutBrush;
            SurfaceBorder.Background = BlackoutBrush;
            SurfaceBorder.BorderBrush = BlackoutBrush;
            SettingsButton.Background = BlackoutBrush;
            SettingsButton.BorderBrush = BlackoutBrush;
            CloseButton.Background = BlackoutBrush;
            CloseButton.BorderBrush = BlackoutBrush;
            SwitchMonitorButton.Background = BlackoutBrush;
            SwitchMonitorButton.BorderBrush = BlackoutBrush;
        }
        else
        {
            var palette = _appearanceManager.Apply();

            if (_settings.AppBarAdaptMode == AppBarAdaptMode.Disabled)
            {
                StopAdaptiveBackground();
                Background = palette.WindowBackground;
                SurfaceBorder.Background = palette.SurfaceBackground;
                SurfaceBorder.BorderBrush = palette.SurfaceBorder;
                SettingsButton.Background = palette.ButtonBackground;
                SettingsButton.BorderBrush = palette.ButtonBorder;
                CloseButton.Background = palette.ButtonBackground;
                CloseButton.BorderBrush = palette.ButtonBorder;
                SwitchMonitorButton.Background = palette.ButtonBackground;
                SwitchMonitorButton.BorderBrush = palette.ButtonBorder;
                ProgressBarFill.Background = palette.ProgressBarBrush;
            }
            else
            {
                Background = palette.WindowBackground;
                SettingsButton.Background = palette.ButtonBackground;
                SettingsButton.BorderBrush = palette.ButtonBorder;
                CloseButton.Background = palette.ButtonBackground;
                CloseButton.BorderBrush = palette.ButtonBorder;
                SwitchMonitorButton.Background = palette.ButtonBackground;
                SwitchMonitorButton.BorderBrush = palette.ButtonBorder;
                ProgressBarFill.Background = palette.ProgressBarBrush;

                if (_settings.AppBarShowProgressBar)
                {
                    SurfaceBorder.BorderBrush = palette.SurfaceBorder;
                }
                else
                {
                    SurfaceBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
                }

                StartAdaptiveBackground();
            }
        }

        AlbumArtPanel.Visibility = isBlackout ? Visibility.Collapsed : (_settings.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed);
        SongCreditPanel.Visibility = isBlackout ? Visibility.Collapsed : Visibility.Visible;
        ProgressBarTrack.Visibility = isBlackout || !_settings.AppBarShowProgressBar ? Visibility.Collapsed : Visibility.Visible;
        LyricsContentPanel.Visibility = isBlackout ? Visibility.Collapsed : Visibility.Visible;
        if (isBlackout)
        {
            AlbumArtPanel.BeginAnimation(OpacityProperty, null);
            SongCreditPanel.BeginAnimation(OpacityProperty, null);
            AlbumArtPanel.Opacity = 1;
            SongCreditPanel.Opacity = 1;
        }
        else
        {
            UpdateSongInfoHoverOpacity();
        }
        AnimateBorderVisibility(hideBorder);
        if (!_viewModel.IsLoadingLyrics)
        {
            ApplyForegroundColors();
        }
    }

    private void ApplyForegroundColors()
    {
        var isExactBright = _settings.AppBarAdaptMode == AppBarAdaptMode.Exact && _exactModeIsBrightBackground;
        var activeBrush = isExactBright ? DarkForegroundBrush : LightForegroundBrush;
        var previewBrush = isExactBright ? DarkPreviewBrush : PreviewLyricBrush;
        var songArtistBrush = isExactBright ? DarkSongArtistBrush : LightSongArtistBrush;
        var songTimestampBrush = isExactBright ? DarkSongTimestampBrush : LightSongTimestampBrush;
        var buttonForegroundBrush = isExactBright ? DarkButtonForegroundBrush : LightButtonForegroundBrush;
        var pauseIconBrush = isExactBright ? DarkPauseIconBrush : LightPauseIconBrush;

        IncomingLyricTextBlock.Foreground = activeBrush;
        OutgoingLyricTextBlock.Foreground = activeBrush;
        PreviewLyricTextBlock.Foreground = previewBrush;
        TtmlPrimaryLyricTextBlock.Foreground = activeBrush;
        TtmlSecondaryLyricTextBlock.Foreground = previewBrush;

        SongTitleTextBlock.Foreground = activeBrush;
        SongArtistTextBlock.Foreground = songArtistBrush;
        SongTimestampTextBlock.Foreground = songTimestampBrush;

        SettingsButton.Foreground = buttonForegroundBrush;
        CloseButton.Foreground = buttonForegroundBrush;
        SwitchMonitorButton.Foreground = buttonForegroundBrush;

        if (PauseBar1 is not null)
            PauseBar1.Fill = pauseIconBrush;
        if (PauseBar2 is not null)
            PauseBar2.Fill = pauseIconBrush;
    }

    private void ApplyNextLineLayout()
    {
        var enabled = IsPreviewModeEnabled;
        var effectiveHeight = GetEffectiveBarHeight(enabled);
        LyricStage.Height = GetLyricStageHeight(enabled, effectiveHeight);

        var lyricVerticalAlign = enabled ? VerticalAlignment.Top : VerticalAlignment.Center;
        OutgoingLyricTextBlock.VerticalAlignment = lyricVerticalAlign;
        IncomingLyricTextBlock.VerticalAlignment = lyricVerticalAlign;

        PreviewLyricTextBlock.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        PreviewLyricTextBlock.Text = _displayedNextLineText;
        PreviewLyricTextBlock.FontSize = AppBarDisplayMode.PreviewLyricFontSize;
        PreviewLyricTranslateTransform.Y = AppBarDisplayMode.PreviewRestY;
        PreviewLyricTextBlock.Opacity = enabled && !string.IsNullOrWhiteSpace(_displayedNextLineText) ? AppBarDisplayMode.PreviewLyricOpacity : 0;
        ApplyForegroundColors();
        IncomingLyricTextBlock.FontSize = GetCurrentLyricFontSize();
        OutgoingLyricTextBlock.FontSize = GetCurrentLyricFontSize();
        TtmlLyricStage.Height = LyricStage.Height;
        TtmlPrimaryLyricTextBlock.FontSize = AppBarDisplayMode.CurrentLyricFontSize;
        TtmlSecondaryLyricTextBlock.FontSize = AppBarDisplayMode.PreviewLyricFontSize;
        _appBarManager?.SetHeight(effectiveHeight);
        ApplyDynamicScale(effectiveHeight);
        ApplyLyricAlignment();
        ApplyLyricLayoutMode();
    }

    private void ApplyLyricLayoutMode()
    {
        var useTtmlLayout = IsTtmlLayoutActive;
        LyricStage.Visibility = useTtmlLayout ? Visibility.Collapsed : Visibility.Visible;
        TtmlLyricStage.Visibility = useTtmlLayout ? Visibility.Visible : Visibility.Collapsed;

        if (useTtmlLayout)
        {
            PreviewLyricTextBlock.Visibility = Visibility.Collapsed;
            TtmlPrimaryLyricTextBlock.FontSize = AppBarDisplayMode.CurrentLyricFontSize;
            TtmlSecondaryLyricTextBlock.FontSize = AppBarDisplayMode.PreviewLyricFontSize;
            TtmlSecondaryLyricTextBlock.Opacity = AppBarDisplayMode.PreviewLyricOpacity;
        }
        else
        {
            _ttmlPrimaryLineKey = null;
            _ttmlSecondaryLineKey = null;
            _ttmlPrimaryWordCharOpacities = null;
            _ttmlSecondaryWordCharOpacities = null;
        }
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

        var newArt = _viewModel.AlbumArt;
        var artChanged = IsAlbumArtDifferent(newArt, _lastAlbumArtData);
        if (artChanged)
        {
            _lastAlbumArtData = newArt;
            AnimateAlbumArtTransition(newArt);
        }
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
            LyricAlignment.Left => System.Windows.HorizontalAlignment.Left,
            LyricAlignment.Right => System.Windows.HorizontalAlignment.Right,
            _ => System.Windows.HorizontalAlignment.Stretch
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
        TtmlPrimaryLyricTextBlock.HorizontalAlignment = horizontalAlignment;
        TtmlPrimaryLyricTextBlock.TextAlignment = textAlignment;
        TtmlSecondaryLyricTextBlock.HorizontalAlignment = horizontalAlignment;
        TtmlSecondaryLyricTextBlock.TextAlignment = textAlignment;

        var colDefs = MainGrid.ColumnDefinitions;
        colDefs.Clear();
        colDefs.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // PauseSpacer
        colDefs.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // AlbumArt/Buttons
        colDefs.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // Lyrics
        colDefs.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Buttons/AlbumArt

        Grid.SetColumn(PauseSpacer, 0);
        Grid.SetColumn(PauseIcon, 0);
        Grid.SetColumnSpan(PauseIcon, 4);

        switch (alignment)
        {
            case LyricAlignment.Left:
                Grid.SetColumn(LyricsGrid, 1);
                Grid.SetColumnSpan(LyricsGrid, 2);
                Grid.SetColumn(AlbumArtPanel, 3);
                AlbumArtPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                Grid.SetColumn(ControlButtonsPanel, 3);
                ControlButtonsPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                if (AlbumArtPanel.Children.IndexOf(AlbumArtBorder) < AlbumArtPanel.Children.IndexOf(SongCreditPanel))
                {
                    AlbumArtPanel.Children.Remove(AlbumArtBorder);
                    AlbumArtPanel.Children.Add(AlbumArtBorder);
                }
                SongTitleTextBlock.TextAlignment = TextAlignment.Right;
                SongArtistTextBlock.TextAlignment = TextAlignment.Right;
                SongTimestampTextBlock.TextAlignment = TextAlignment.Right;
                SongCreditPanel.Margin = new Thickness(0, 0, 4, 0);
                AlbumArtBorder.Margin = new Thickness(0);
                AlbumArtPanel.Margin = new Thickness(14, 0, 0, 0);
                break;
            case LyricAlignment.Right:
                Grid.SetColumn(ControlButtonsPanel, 1);
                ControlButtonsPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                Grid.SetColumn(LyricsGrid, 2);
                Grid.SetColumnSpan(LyricsGrid, 2);
                Grid.SetColumn(AlbumArtPanel, 1);
                AlbumArtPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                if (AlbumArtPanel.Children.IndexOf(AlbumArtBorder) > AlbumArtPanel.Children.IndexOf(SongCreditPanel))
                {
                    AlbumArtPanel.Children.Remove(AlbumArtBorder);
                    AlbumArtPanel.Children.Insert(0, AlbumArtBorder);
                }
                SongTitleTextBlock.TextAlignment = TextAlignment.Left;
                SongArtistTextBlock.TextAlignment = TextAlignment.Left;
                SongTimestampTextBlock.TextAlignment = TextAlignment.Left;
                SongCreditPanel.Margin = new Thickness(4, 0, 0, 0);
                AlbumArtBorder.Margin = new Thickness(0);
                AlbumArtPanel.Margin = new Thickness(0, 0, 14, 0);
                break;
            default:
                Grid.SetColumn(AlbumArtPanel, 1);
                AlbumArtPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                Grid.SetColumn(LyricsGrid, 2);
                Grid.SetColumnSpan(LyricsGrid, 1);
                Grid.SetColumn(ControlButtonsPanel, 3);
                ControlButtonsPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                if (AlbumArtPanel.Children.IndexOf(AlbumArtBorder) > AlbumArtPanel.Children.IndexOf(SongCreditPanel))
                {
                    AlbumArtPanel.Children.Remove(AlbumArtBorder);
                    AlbumArtPanel.Children.Insert(0, AlbumArtBorder);
                }
                SongTitleTextBlock.TextAlignment = TextAlignment.Left;
                SongArtistTextBlock.TextAlignment = TextAlignment.Left;
                SongTimestampTextBlock.TextAlignment = TextAlignment.Left;
                SongCreditPanel.Margin = new Thickness(4, 0, 0, 0);
                AlbumArtBorder.Margin = new Thickness(0);
                AlbumArtPanel.Margin = new Thickness(0, 0, 14, 0);
                break;
        }
    }

    private void ApplyDynamicScale(int effectiveHeight)
    {
        var autoHeight = AppBarDisplayMode.GetAutomaticHeight(IsPreviewModeEnabled);

        if (effectiveHeight < autoHeight)
        {
            var scaleFactor = (double)effectiveHeight / autoHeight;
            scaleFactor = Math.Max(scaleFactor, 0.4);

            var artSize = Math.Max(28, 56 * scaleFactor);
            AlbumArtBorder.Width = artSize;
            AlbumArtBorder.Height = artSize;
            AlbumArtOverlayBorder.Width = artSize;
            AlbumArtOverlayBorder.Height = artSize;
            SongCreditPanel.Visibility = ShouldUseBlackoutMode() ? Visibility.Collapsed : (effectiveHeight < 50 ? Visibility.Collapsed : Visibility.Visible);

            var isCompact = effectiveHeight < 70;
            SongTitleTextBlock.FontSize = isCompact ? 11 : 13;
            SongArtistTextBlock.FontSize = isCompact ? 9 : 11;
            SongTimestampTextBlock.FontSize = isCompact ? 8 : 10;

            LyricsContentPanel.LayoutTransform = new ScaleTransform(scaleFactor, scaleFactor);
        }
        else
        {
            AlbumArtBorder.Width = 56;
            AlbumArtBorder.Height = 56;
            AlbumArtOverlayBorder.Width = 56;
            AlbumArtOverlayBorder.Height = 56;
            SongCreditPanel.Visibility = ShouldUseBlackoutMode() ? Visibility.Collapsed : Visibility.Visible;
            SongArtistTextBlock.FontSize = 11;
            SongTimestampTextBlock.FontSize = 10;
            LyricsContentPanel.LayoutTransform = null;
        }

        AlbumArtPanel.Visibility = ShouldUseBlackoutMode() ? Visibility.Collapsed : (_settings.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed);
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

        if (_viewModel.IsLoadingLyrics)
        {
            _displayedLyricText = string.Empty;
            StopWordAnim();
            IncomingLyricTextBlock.Inlines.Clear();
            IncomingLyricTextBlock.Text = string.Empty;
            OutgoingLyricTextBlock.Text = string.Empty;
            PreviewLyricTextBlock.Text = string.Empty;
            return;
        }

        var wbw = _viewModel.WordByWordMode;
        var wc = IsCleanedLrcActive ? 0 : (_viewModel.CurrentLyricLine?.Words?.Count ?? 0);
        Logger.Log($"HandleCurrentLineChanged: WordByWord={wbw} Words={wc} text='{newCurrentLine}'");

        if (wbw && wc > 0)
        {
            if (!string.Equals(_displayedLyricText, newCurrentLine, StringComparison.Ordinal))
            {
                StopWordAnim();
                _lastLineChangeTimestamp = DateTime.UtcNow;

                var oldInlines = IncomingLyricTextBlock.Inlines.ToList();
                OutgoingLyricTextBlock.Inlines.Clear();
                foreach (var inline in oldInlines)
                {
                    var src = (Run)inline;
                    OutgoingLyricTextBlock.Inlines.Add(new Run(src.Text) { Foreground = src.Foreground });
                }

                SetWordByWordInlines();

                OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, null);
                OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
                IncomingLyricTextBlock.BeginAnimation(OpacityProperty, null);
                IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);

                OutgoingLyricTextBlock.Opacity = 1;
                OutgoingLyricTranslateTransform.Y = 0;
                IncomingLyricTextBlock.Opacity = 0;
                IncomingLyricTranslateTransform.Y = 14;

                var fadeOut = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(220),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                var slideOut = new DoubleAnimation
                {
                    To = -14,
                    Duration = TimeSpan.FromMilliseconds(220),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                var fadeIn = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    BeginTime = TimeSpan.FromMilliseconds(60),
                    Duration = TimeSpan.FromMilliseconds(280),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var slideIn = new DoubleAnimation
                {
                    From = 14,
                    To = 0,
                    BeginTime = TimeSpan.FromMilliseconds(60),
                    Duration = TimeSpan.FromMilliseconds(320),
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
                };

                OutgoingLyricTextBlock.BeginAnimation(OpacityProperty, fadeOut);
                OutgoingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);
                IncomingLyricTextBlock.BeginAnimation(OpacityProperty, fadeIn);
                IncomingLyricTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);

                _displayedLyricText = newCurrentLine;
            }
            else
            {
                StartWordAnim();
            }
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

    private static WpfBrush GetWordBrush(WpfBrush baseBrush, double opacity)
    {
        if (baseBrush is SolidColorBrush solid)
        {
            var color = solid.Color;
            return new SolidColorBrush(MediaColor.FromArgb((byte)(color.A * opacity), color.R, color.G, color.B));
        }
        return baseBrush;
    }

    private void SetWordByWordInlines()
    {
        var lyricLine = _viewModel.CurrentLyricLine;
        var words = lyricLine?.Words;
        if (words is null || words.Count == 0)
        {
            Logger.Log("SetWordByWordInlines: no words");
            return;
        }

        Logger.Log($"SetWordByWordInlines: {words.Count} words");

        IncomingLyricTextBlock.Inlines.Clear();

        var dimBrush = GetWordBrush(IncomingLyricTextBlock.Foreground, 0.15);
        var totalChars = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i].Word;
            for (var j = 0; j < word.Length; j++)
            {
                IncomingLyricTextBlock.Inlines.Add(new Run(word[j].ToString()) { Foreground = dimBrush });
                totalChars++;
            }

            if (i < words.Count - 1)
            {
                IncomingLyricTextBlock.Inlines.Add(new Run(" ") { Foreground = dimBrush });
                totalChars++;
            }
        }

        _wordCharOpacities = new double[totalChars];
        Array.Fill(_wordCharOpacities, 0.15);

        OutgoingLyricTextBlock.Text = string.Empty;
        _displayedLyricText = _viewModel.CurrentLine;

        StartWordAnim();
    }

    private void RenderTtmlLyrics()
    {
        if (!IsTtmlLayoutActive)
        {
            return;
        }

        ApplyLyricLayoutMode();

        var (primaryLine, secondaryLine) = GetTtmlDisplayLines();
        var primaryKey = GetTtmlLineKey(primaryLine);
        var secondaryKey = GetTtmlLineKey(secondaryLine);

        if (!string.Equals(_ttmlPrimaryLineKey, primaryKey, StringComparison.Ordinal))
        {
            RenderTtmlLine(TtmlPrimaryLyricTextBlock, primaryLine, ref _ttmlPrimaryWordCharOpacities, 1);
            _ttmlPrimaryLineKey = primaryKey;
        }

        if (!string.Equals(_ttmlSecondaryLineKey, secondaryKey, StringComparison.Ordinal))
        {
            RenderTtmlLine(TtmlSecondaryLyricTextBlock, secondaryLine, ref _ttmlSecondaryWordCharOpacities, AppBarDisplayMode.PreviewLyricOpacity);
            _ttmlSecondaryLineKey = secondaryKey;
        }

        if ((primaryLine?.Words?.Count > 0) || (secondaryLine?.Words?.Count > 0))
        {
            StartWordAnim();
        }
    }

    private (LyricLine? Primary, LyricLine? Secondary) GetTtmlDisplayLines()
    {
        var activeLines = _viewModel.ActiveLyricLines
            .Where(line => line.IsTtml)
            .ToList();

        if (activeLines.Count == 0 && _viewModel.CurrentLyricLine?.IsTtml == true)
        {
            activeLines.Add(_viewModel.CurrentLyricLine);
        }

        var primary = activeLines.LastOrDefault(line => !line.IsBackground)
            ?? activeLines.LastOrDefault();
        var secondary = activeLines.LastOrDefault(line => line.IsBackground && !ReferenceEquals(line, primary))
            ?? activeLines.LastOrDefault(line => !ReferenceEquals(line, primary));

        return (primary, secondary);
    }

    private static string? GetTtmlLineKey(LyricLine? line)
    {
        return line is null
            ? null
            : $"{line.Timestamp.Ticks}|{line.EndTime?.Ticks}|{line.IsBackground}|{line.Text}";
    }

    private void RenderTtmlLine(TextBlock textBlock, LyricLine? line, ref double[]? charOpacities, double targetOpacity)
    {
        textBlock.BeginAnimation(OpacityProperty, null);
        textBlock.Inlines.Clear();

        if (line is null || string.IsNullOrWhiteSpace(line.Text))
        {
            textBlock.Text = string.Empty;
            textBlock.Opacity = 0;
            charOpacities = null;
            return;
        }

        textBlock.Opacity = targetOpacity;
        if (line.Words is not { Count: > 0 } words)
        {
            textBlock.Text = line.Text;
            charOpacities = null;
            return;
        }

        textBlock.Text = string.Empty;
        var dimBrush = GetWordBrush(textBlock.Foreground, 0.18);
        var totalChars = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i].Word;
            for (var j = 0; j < word.Length; j++)
            {
                textBlock.Inlines.Add(new Run(word[j].ToString()) { Foreground = dimBrush });
                totalChars++;
            }

            if (i < words.Count - 1)
            {
                textBlock.Inlines.Add(new Run(" ") { Foreground = dimBrush });
                totalChars++;
            }
        }

        charOpacities = new double[totalChars];
        Array.Fill(charOpacities, 0.18);
    }

    private void UpdateWordInlineHighlight()
    {
        StartWordAnim();
    }

    private void StartWordAnim()
    {
        if (_wordAnimTimer is null)
        {
            _wordAnimTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _wordAnimTimer.Tick += WordAnimTimer_Tick;
        }
        if (!_wordAnimTimer.IsEnabled)
            _wordAnimTimer.Start();
    }

    private void StopWordAnim()
    {
        if (_wordAnimTimer is not null && _wordAnimTimer.IsEnabled)
            _wordAnimTimer.Stop();
    }

    private void WordAnimTimer_Tick(object? sender, EventArgs e)
    {
        if (IsTtmlLayoutActive)
        {
            UpdateTtmlWordHighlights();
            return;
        }

        var line = _viewModel.CurrentLyricLine;
        var words = line?.Words;
        if (words is null || words.Count == 0)
        {
            StopWordAnim();
            return;
        }

        if (_viewModel.IsPlaybackPaused) return;

        var position = _viewModel.EstimatedPosition;
        if (position is null)
        {
            StopWordAnim();
            return;
        }

        var msSinceChange = (DateTime.UtcNow - _lastLineChangeTimestamp).TotalMilliseconds;
        var lookAhead = msSinceChange < 150 ? 0.0 : Math.Min(250.0, (msSinceChange - 150.0) * 2.5);

        var adjustedPos = position.Value + TimeSpan.FromMilliseconds(lookAhead);
        var songDuration = _viewModel.SongDuration;
        if (songDuration > TimeSpan.Zero && adjustedPos > songDuration)
            adjustedPos = songDuration;

        var inlines = IncomingLyricTextBlock.Inlines.ToList();
        var totalChars = inlines.Count;
        if (totalChars == 0) return;

        if (_wordCharOpacities is null || _wordCharOpacities.Length != totalChars)
        {
            _wordCharOpacities = new double[totalChars];
            Array.Fill(_wordCharOpacities, 0.15);
        }

        var runningChars = 0;
        double? fillPos = null;

        var lineStartTime = words[0].Timestamp;
        var lastWordEndTime = words[^1].Timestamp + TimeSpan.FromMilliseconds(800);
        if (words.Count >= 2)
        {
            var avgGap = words[^1].Timestamp - words[^2].Timestamp;
            var naturalEnd = words[^1].Timestamp + (avgGap > TimeSpan.Zero ? avgGap : TimeSpan.FromMilliseconds(500));
            var maxEnd = lineStartTime + TimeSpan.FromSeconds(4);
            lastWordEndTime = naturalEnd < maxEnd ? naturalEnd : maxEnd;
        }

        for (var i = 0; i < words.Count; i++)
        {
            var wordChars = words[i].Word.Length;
            var hasSpace = i < words.Count - 1 ? 1 : 0;
            var wordVisualSpan = wordChars + hasSpace;

            var wordStartTime = words[i].Timestamp;
            TimeSpan wordEndTime;
            if (i + 1 < words.Count)
            {
                var naturalNext = words[i + 1].Timestamp;
                var maxAllowed = lineStartTime + TimeSpan.FromSeconds(4);
                wordEndTime = naturalNext < maxAllowed ? naturalNext : maxAllowed;
            }
            else
            {
                wordEndTime = lastWordEndTime;
            }

            if (adjustedPos >= wordStartTime && adjustedPos < wordEndTime)
            {
                var timeProgress = wordEndTime > wordStartTime
                    ? (adjustedPos - wordStartTime).TotalMilliseconds / (wordEndTime - wordStartTime).TotalMilliseconds
                    : 0;

                var visualStart = (double)runningChars / totalChars;
                var visualEnd = (double)(runningChars + wordVisualSpan) / totalChars;
                fillPos = visualStart + timeProgress * (visualEnd - visualStart);
                break;
            }

            runningChars += wordVisualSpan;
        }

        if (fillPos is null)
        {
            fillPos = adjustedPos < words[0].Timestamp ? 0.0 : 1.0;
        }

        var baseBrush = IncomingLyricTextBlock.Foreground;
        for (var i = 0; i < totalChars; i++)
        {
            var charPos = (double)i / totalChars;
            var diff = (fillPos.Value - charPos) * totalChars;
            var target = Math.Clamp(0.15 + 0.85 * diff, 0.15, 1.0);

            var current = _wordCharOpacities[i];
            var err = target - current;
            if (Math.Abs(err) < 0.003)
                _wordCharOpacities[i] = target;
            else
                _wordCharOpacities[i] = current + err * 0.28;

            ((Run)inlines[i]).Foreground = GetWordBrush(baseBrush, _wordCharOpacities[i]);
        }
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
        OutgoingLyricTextBlock.Inlines.Clear();
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
        PauseSpacer.BeginAnimation(FrameworkElement.WidthProperty, null);
        SurfaceBorder.BeginAnimation(Border.PaddingProperty, null);

        var spacerAnimation = new DoubleAnimation
        {
            From = PauseSpacer.ActualWidth,
            To = isPaused ? 42 : 0,
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
            PauseSpacer.Width = isPaused ? 42 : 0;
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

        PauseSpacer.BeginAnimation(FrameworkElement.WidthProperty, spacerAnimation);
        PauseIcon.BeginAnimation(OpacityProperty, iconAnimation);
        _isPauseVisualActive = isPaused;
    }

    private void ApplyPlaybackStateVisual(bool immediate)
    {
        _isPauseVisualActive = _viewModel.IsPlaybackPaused;
        PauseSpacer.BeginAnimation(FrameworkElement.WidthProperty, null);
        PauseIcon.BeginAnimation(OpacityProperty, null);

        if (immediate)
        {
            PauseSpacer.Width = _isPauseVisualActive ? 42 : 0;
            PauseIcon.Opacity = _isPauseVisualActive ? 1 : 0;
            PauseIcon.Visibility = _isPauseVisualActive ? Visibility.Visible : Visibility.Collapsed;
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
            TtmlPrimaryLyricTextBlock.Foreground = LoadingTextBrush;
            TtmlSecondaryLyricTextBlock.Foreground = LoadingTextBrush;
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

        var currentText = _displayedLyricText;
        var fadeOutCurrent = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOutCurrent.Completed += (_, _) =>
        {
            var songTitle = _viewModel.SongTitle;
            var displayText = string.IsNullOrWhiteSpace(songTitle) ? "No lyrics found" : songTitle;
            IncomingLyricTextBlock.Text = displayText;
            IncomingLyricTextBlock.FontSize = GetCurrentLyricFontSize();
            IncomingLyricTranslateTransform.X = 0;
            IncomingLyricTranslateTransform.Y = 0;

            var fadeInTitle = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(350),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            fadeInTitle.Completed += (_, _) =>
            {
                var fadeOutTitle = new DoubleAnimation
                {
                    BeginTime = TimeSpan.FromMilliseconds(2500),
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };

                fadeOutTitle.Completed += (_, _) =>
                {
                    _displayedLyricText = string.Empty;
                    IncomingLyricTextBlock.Text = string.Empty;
                    IncomingLyricTextBlock.Opacity = 0;
                    IncomingLyricTranslateTransform.X = 0;
                    _isNotFoundAnimationRunning = false;
                    EndNoLyricsHideDeferral("animation completed");
                    ApplyHideModeState();
                };

                IncomingLyricTextBlock.BeginAnimation(OpacityProperty, fadeOutTitle);
            };

            IncomingLyricTextBlock.BeginAnimation(OpacityProperty, fadeInTitle);
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
        }
        else
        {
            var animation = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(140),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            ControlButtonsPanel.BeginAnimation(OpacityProperty, animation);
        }

        UpdateSongInfoHoverOpacity();
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

        UpdateSongInfoHoverOpacity();
    }

    private void UpdateSongInfoHoverOpacity()
    {
        if (ShouldUseBlackoutMode())
        {
            return;
        }

        if (_settings.LyricAlignment == LyricAlignment.Center)
        {
            AlbumArtPanel.BeginAnimation(OpacityProperty, null);
            SongCreditPanel.BeginAnimation(OpacityProperty, null);
            AlbumArtPanel.Opacity = 1;
            SongCreditPanel.Opacity = 1;
            return;
        }

        var targetOpacity = _controlsVisible ? 0.2 : 1.0;
        var duration = _controlsVisible ? 140 : 220;
        var easing = _controlsVisible
            ? (QuadraticEase)new QuadraticEase { EasingMode = EasingMode.EaseOut }
            : new QuadraticEase { EasingMode = EasingMode.EaseIn };

        AlbumArtPanel.BeginAnimation(OpacityProperty, null);
        SongCreditPanel.BeginAnimation(OpacityProperty, null);

        if (_controlsVisible)
        {
            AlbumArtPanel.Opacity = targetOpacity;
            SongCreditPanel.Opacity = targetOpacity;
        }
        else
        {
            var anim = new DoubleAnimation
            {
                To = targetOpacity,
                Duration = TimeSpan.FromMilliseconds(duration),
                EasingFunction = easing
            };
            AlbumArtPanel.BeginAnimation(OpacityProperty, anim);
            SongCreditPanel.BeginAnimation(OpacityProperty, anim);
        }
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
        var currentSongId = _viewModel.SongTitle ?? string.Empty;

        if (currentSongId != _lastProgressSongId)
        {
            _lastProgressSongId = currentSongId;
            ProgressBarFill.BeginAnimation(WidthProperty, null);
            ProgressBarFill.Width = 0;
        }

        _progressTimer.Start();
    }

    private void ProgressTimer_OnTick(object? sender, EventArgs e)
    {
        var position = _viewModel.EstimatedPosition;
        var duration = _viewModel.SongDuration;

        if (position is null || duration <= TimeSpan.Zero)
        {
            _progressTimer.Stop();
            return;
        }

        var progress = Math.Clamp(position.Value.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
        var fillWidth = Math.Max(0, progress * ProgressBarTrack.ActualWidth);
        var targetWidth = double.IsNaN(fillWidth) || double.IsInfinity(fillWidth) ? 0 : fillWidth;

        ProgressBarFill.BeginAnimation(WidthProperty, null);
        ProgressBarFill.Width = targetWidth;
    }

    private void UpdateHoverEffect(bool isHovering)
    {
        if (ShouldUseBlackoutMode() || _settings.AppBarAdaptMode != AppBarAdaptMode.Disabled)
        {
            return;
        }

        var targetBgColor = isHovering ? MediaColor.FromArgb(140, 16, 26, 37) : MediaColor.FromArgb(102, 16, 26, 37);
        var targetBorderColor = isHovering ? MediaColor.FromArgb(160, 48, 70, 92) : MediaColor.FromArgb(138, 48, 70, 92);
        var duration = isHovering ? 150 : 250;

        if (SurfaceBorder.Background is SolidColorBrush bgBrush && bgBrush.Color.A > 0)
        {
            bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            var bgAnim = new ColorAnimation
            {
                To = targetBgColor,
                Duration = TimeSpan.FromMilliseconds(duration),
                EasingFunction = new QuadraticEase { EasingMode = isHovering ? EasingMode.EaseOut : EasingMode.EaseIn }
            };
            bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, bgAnim);
        }

        if (SurfaceBorder.BorderBrush is SolidColorBrush borderBrush && borderBrush.Color.A > 0)
        {
            borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            var borderAnim = new ColorAnimation
            {
                To = targetBorderColor,
                Duration = TimeSpan.FromMilliseconds(duration),
                EasingFunction = new QuadraticEase { EasingMode = isHovering ? EasingMode.EaseOut : EasingMode.EaseIn }
            };
            borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderAnim);
        }
    }

    private void StartAdaptiveBackground()
    {
        if (!_adaptToContentTimer.IsEnabled)
        {
            _adaptToContentTimer.Start();
            UpdateAdaptiveBackground();
        }
    }

    private void StopAdaptiveBackground()
    {
        _adaptToContentTimer.Stop();
    }

    private void AdaptToContentTimer_OnTick(object? sender, EventArgs e)
    {
        if (!IsVisible || ShouldUseBlackoutMode() || _settings.AppBarAdaptMode == AppBarAdaptMode.Disabled)
        {
            return;
        }

        UpdateAdaptiveBackground();
    }

    private void UpdateAdaptiveBackground()
    {
        try
        {
            var width = (int)Math.Ceiling(ActualWidth);
            var height = (int)Math.Ceiling(ActualHeight);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var screenPoint = PointToScreen(new System.Windows.Point(0, 0));
            var screenX = (int)Math.Round(screenPoint.X);
            var screenY = (int)Math.Round(screenPoint.Y);
            var sampleHeight = Math.Min(40, height);

            using var stripBitmap = new Bitmap(width, sampleHeight);
            using (var g = Graphics.FromImage(stripBitmap))
            {
                g.CopyFromScreen(screenX, screenY + height, 0, 0, new System.Drawing.Size(width, sampleHeight));
            }

            var avgWidth = Math.Max(1, width / 8);
            using var avgBitmap = new Bitmap(avgWidth, 1);
            using (var g = Graphics.FromImage(avgBitmap))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(stripBitmap, 0, 0, avgWidth, 1);
            }

            long rSum = 0, gSum = 0, bSum = 0;
            for (int x = 0; x < avgBitmap.Width; x++)
            {
                var pixel = avgBitmap.GetPixel(x, 0);
                rSum += pixel.R;
                gSum += pixel.G;
                bSum += pixel.B;
            }

            var count = avgBitmap.Width;
            var avgR = (double)rSum / count;
            var avgG = (double)gSum / count;
            var avgB = (double)bSum / count;

            var luminance = 0.299 * avgR + 0.587 * avgG + 0.114 * avgB;
            var luminanceThreshold = Math.Clamp(_settings.AppBarAdaptThreshold, 0, 255);
            const double TargetLuminance = 100.0;

            double factor;
            if (luminance > luminanceThreshold)
            {
                factor = TargetLuminance / luminance;
            }
            else
            {
                factor = 1.0;
            }

            if (_settings.AppBarAdaptMode == AppBarAdaptMode.Exact)
            {
                var exactR = (byte)Math.Clamp(avgR, 0, 255);
                var exactG = (byte)Math.Clamp(avgG, 0, 255);
                var exactB = (byte)Math.Clamp(avgB, 0, 255);

                var isBright = 0.299 * exactR + 0.587 * exactG + 0.114 * exactB > 160;
                if (isBright != _exactModeIsBrightBackground)
                {
                    _exactModeIsBrightBackground = isBright;
                    ApplyForegroundColors();
                }

                SurfaceBorder.Background = new SolidColorBrush(MediaColor.FromRgb(exactR, exactG, exactB));
            }
            else
            {
                var finalR = (byte)Math.Clamp(avgR * factor, 0, 255);
                var finalG = (byte)Math.Clamp(avgG * factor, 0, 255);
                var finalB = (byte)Math.Clamp(avgB * factor, 0, 255);

                var topColor = MediaColor.FromArgb(185, finalR, finalG, finalB);
                var bottomColor = MediaColor.FromArgb(225, (byte)(finalR * 0.85), (byte)(finalG * 0.85), (byte)(finalB * 0.85));

                SurfaceBorder.Background = new System.Windows.Media.LinearGradientBrush(topColor, bottomColor, 90.0);
            }
        }
        catch
        {
            // Ignore capture failures
        }
    }

    private void AnimateAlbumArtTransition(byte[]? newArt)
    {
        if (!_settings.ShowAlbumArt || ShouldUseBlackoutMode())
        {
            AlbumArtPanel.Visibility = Visibility.Collapsed;
            return;
        }

        AlbumArtPanel.Visibility = Visibility.Visible;

        if (newArt is null || newArt.Length == 0)
            return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new System.IO.MemoryStream(newArt);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            AlbumArtImage.BeginAnimation(OpacityProperty, null);
            AlbumArtImage.Source = bitmap;
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            AlbumArtImage.BeginAnimation(OpacityProperty, fadeIn);
            AlbumArtOverlayBorder.Opacity = 0;
            AlbumArtOverlayImage.Source = null;
        }
        catch
        {
            AlbumArtImage.Source = null;
        }
    }

    private void Window_OnMouseEnter(object sender, WpfMouseEventArgs e)
    {
        _isPointerOverWindow = true;
        ShowControls();
        ScheduleControlsFade();
        UpdateHoverEffect(true);
    }

    private void Window_OnMouseLeave(object sender, WpfMouseEventArgs e)
    {
        _isPointerOverWindow = false;
        _controlsFadeTimer.Stop();
        HideControls();
        UpdateHoverEffect(false);
    }

    private void Window_OnMouseMove(object sender, WpfMouseEventArgs e)
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
            _settingsWindow.ForceLyricsRefreshRequested += (_, _) => _ = _viewModel.ForceLyricsRefreshAsync();
            _settingsWindow.DebugForceNoLyricsRequested += (_, _) => _viewModel.ForceNoLyrics();
            _settingsWindow.DebugForceSimulateLyricsRequested += (_, _) => _viewModel.ForceSimulateLyrics();
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

    private void UpdateLastSearchInfo()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.UpdateLastSearchInfo(_viewModel.LastSearchInfo);
        }
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
        _settingsWindow.UpdateLastSearchInfo(_viewModel.LastSearchInfo);
    }

    private void SettingsWindow_OnSettingsChanged(object? sender, AppSettings settings)
    {
        _settings = MergeSettings(settings);
        _appSettingsService.Save(_settings);
        _viewModel.UpdateSettings(_settings);

        if (_settings.DisplayMode != DisplayMode.AppBar)
        {
            ((App)WpfApplication.Current).RestartDisplayWindow();
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

        if (IsTtmlLayoutActive)
        {
            RenderTtmlLyrics();
        }
        else if (_viewModel.WordByWordMode && _viewModel.CurrentLyricLine?.Words?.Count > 0)
        {
            IncomingLyricTextBlock.Inlines.Clear();
            OutgoingLyricTextBlock.Inlines.Clear();
            OutgoingLyricTextBlock.Text = string.Empty;
            SetWordByWordInlines();
            IncomingLyricTextBlock.FontSize = AppBarDisplayMode.CurrentLyricFontSize;
            IncomingLyricTextBlock.Opacity = 1;
            IncomingLyricTranslateTransform.Y = 0;
            OutgoingLyricTextBlock.Opacity = 0;
        }
        else
        {
            StopWordAnim();
            IncomingLyricTextBlock.Inlines.Clear();
            IncomingLyricTextBlock.Text = _displayedLyricText;
            IncomingLyricTextBlock.FontSize = AppBarDisplayMode.CurrentLyricFontSize;
            IncomingLyricTextBlock.Opacity = 1;
            IncomingLyricTranslateTransform.Y = 0;
            OutgoingLyricTextBlock.Opacity = 0;
        }
        PreviewLyricTextBlock.Text = _displayedNextLineText;
        ApplyNextLineLayout();
        ApplyLyricLayoutMode();
        UpdateAlbumArtAndCredit();
        ApplyLyricAlignment();
        ApplyDisplayModeState();
        ApplyPlaybackStateVisual(immediate: true);
        ApplyLoadingState(immediate: true);

        if (!IsVisible)
        {
            Show();
        }

        ApplyAppearance();
        _appBarManager?.SetHeight(GetEffectiveBarHeight(IsPreviewModeEnabled));
        _appBarManager?.Reposition();
    }

    private AppSettings MergeSettings(AppSettings incomingSettings)
    {
        var persistedSettings = _appSettingsService.Load();
        persistedSettings.HideMode = incomingSettings.HideMode;
        persistedSettings.DisplayMode = incomingSettings.DisplayMode;
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
        persistedSettings.AppBarShowProgressBar = incomingSettings.AppBarShowProgressBar;
        persistedSettings.AppBarAdaptMode = incomingSettings.AppBarAdaptMode;
        persistedSettings.AppBarAdaptThreshold = incomingSettings.AppBarAdaptThreshold;
        persistedSettings.WordByWordMode = incomingSettings.WordByWordMode;
        persistedSettings.DisplayTtmlLyrics = incomingSettings.DisplayTtmlLyrics;
        persistedSettings.AutostartWithWindows = incomingSettings.AutostartWithWindows;
        persistedSettings.MaxCacheSize = incomingSettings.MaxCacheSize;
            persistedSettings.WindowedShowNextLine = incomingSettings.WindowedShowNextLine;
            persistedSettings.WindowedLyricAlignment = incomingSettings.WindowedLyricAlignment;
            persistedSettings.WindowedShowAlbumArt = incomingSettings.WindowedShowAlbumArt;
            persistedSettings.WindowedWordByWordMode = incomingSettings.WindowedWordByWordMode;
            persistedSettings.WindowedDisplayTtmlLyrics = incomingSettings.WindowedDisplayTtmlLyrics;
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
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        _appBarManager?.Detach();
        _trayIcon ??= new TrayIcon(this);
    }

    public void ShowFromTray()
    {
        Show();
        WorkspaceVisibilityManager.PinToAllWorkspaces(this);
        if (_appBarManager is null)
        {
            _appBarManager = new AppBarManager(this, AppBarDisplayMode.DefaultHeight);
        }

        if (!_appBarManager.IsAttached)
        {
            _appBarManager.Attach();
        }
        _appBarManager.SetHeight(GetEffectiveBarHeight(IsPreviewModeEnabled));
        _appBarManager.Reposition();
        ApplyAppearance();
        ApplyHideModeState();
        ApplyNextLineLayout();
        ApplyDisplayModeState();
    }

    public void ExitApp()
    {
        Close();
    }

    public void OpenSettingsFromTray()
    {
        ShowFromTray();
        SettingsButton_OnClick(this, new RoutedEventArgs());
    }

    public DisplayMode CurrentDisplayMode => DisplayMode.AppBar;

    public void SwitchDisplayMode(DisplayMode mode)
    {
        if (_settings.DisplayMode == mode) return;
        _settings.DisplayMode = mode;
        _appSettingsService.Save(_settings);
        ((App)WpfApplication.Current).RestartDisplayWindow();
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

    private void UpdateTtmlWordHighlights()
    {
        if (_viewModel.IsPlaybackPaused)
        {
            return;
        }

        var position = _viewModel.EstimatedPosition;
        if (position is null)
        {
            StopWordAnim();
            return;
        }

        var (primaryLine, secondaryLine) = GetTtmlDisplayLines();
        var updatedPrimary = UpdateWordHighlightsForLine(TtmlPrimaryLyricTextBlock, primaryLine, position.Value, ref _ttmlPrimaryWordCharOpacities);
        var updatedSecondary = UpdateWordHighlightsForLine(TtmlSecondaryLyricTextBlock, secondaryLine, position.Value, ref _ttmlSecondaryWordCharOpacities);

        if (!updatedPrimary && !updatedSecondary)
        {
            StopWordAnim();
        }
    }

    private bool UpdateWordHighlightsForLine(TextBlock textBlock, LyricLine? line, TimeSpan position, ref double[]? charOpacities)
    {
        var words = line?.Words;
        if (words is null || words.Count == 0)
        {
            return false;
        }

        var inlines = textBlock.Inlines.ToList();
        if (inlines.Count == 0)
        {
            RenderTtmlLine(textBlock, line, ref charOpacities, textBlock.Opacity <= 0 ? 1 : textBlock.Opacity);
            inlines = textBlock.Inlines.ToList();
        }

        if (charOpacities is null || charOpacities.Length != inlines.Count)
        {
            charOpacities = new double[inlines.Count];
            Array.Fill(charOpacities, 0.18);
        }

        var charIndex = 0;
        var fullBrush = GetWordBrush(textBlock.Foreground, 1);
        var dimBrush = GetWordBrush(textBlock.Foreground, 0.18);

        for (var wordIndex = 0; wordIndex < words.Count; wordIndex++)
        {
            var word = words[wordIndex];
            var nextTimestamp = word.EndTime
                ?? (wordIndex + 1 < words.Count ? words[wordIndex + 1].Timestamp : line!.EndTime)
                ?? word.Timestamp + TimeSpan.FromMilliseconds(650);

            var targetOpacity = position >= word.Timestamp && position < nextTimestamp
                ? 1
                : position >= nextTimestamp
                    ? 1
                    : 0.18;

            for (var i = 0; i < word.Word.Length && charIndex < inlines.Count; i++, charIndex++)
            {
                charOpacities[charIndex] += (targetOpacity - charOpacities[charIndex]) * 0.35;
                if (inlines[charIndex] is Run run)
                {
                    run.Foreground = charOpacities[charIndex] >= 0.6
                        ? GetWordBrush(textBlock.Foreground, charOpacities[charIndex])
                        : dimBrush;
                }
            }

            if (wordIndex < words.Count - 1 && charIndex < inlines.Count)
            {
                if (inlines[charIndex] is Run spaceRun)
                {
                    spaceRun.Foreground = targetOpacity > 0.5 ? fullBrush : dimBrush;
                }

                charIndex++;
            }
        }

        return true;
    }
}
