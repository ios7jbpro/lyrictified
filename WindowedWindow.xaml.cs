using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lyrictified.DisplayModes;
using Lyrictified.Interop;
using Lyrictified.Models;
using Lyrictified.Services;
using Lyrictified.Settings;
using Lyrictified.Styling;
using Lyrictified.ViewModels;
using MediaColor = System.Windows.Media.Color;
using WpfApplication = System.Windows.Application;
using WpfBrush = System.Windows.Media.Brush;

namespace Lyrictified;

public partial class WindowedWindow : Window, ITrayIconHost
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;
    private const int DwmSubtleBorderColor = 0x006E6254;

    private static readonly WpfBrush ActiveLyricBrush = new SolidColorBrush(MediaColor.FromRgb(245, 247, 250));
    private static readonly WpfBrush InactiveLyricBrush = new SolidColorBrush(MediaColor.FromRgb(126, 140, 155));
    private static readonly WpfBrush LoadingTextBrush = new SolidColorBrush(MediaColor.FromRgb(150, 156, 164));

    private readonly AppSettingsService _appSettingsService = new();
    private readonly DispatcherTimer _progressTimer;
    private readonly MainViewModel _viewModel;
    private readonly List<TextBlock> _lyricTextBlocks = new();
    private readonly Dictionary<TextBlock, LyricLineVisualState> _lyricVisualStates = new();

    private AppBarManager? _monitorHelper;
    private AppSettings _settings;
    private byte[]? _lastAlbumArtData;
    private bool _isPointerOverWindow;
    private bool _isSizeInitialized;
    private int _lastHighlightedLineIndex = -1;
    private DispatcherTimer? _wordAnimTimer;
    private double[]? _wordCharOpacities;
    private DateTime _lastLineChangeTimestamp = DateTime.MinValue;
    private TextBlock? _wordAnimatedTextBlock;
    private string _wordAnimatedLineText = string.Empty;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _trayIcon;
    private WindowAppearanceManager? _appearanceManager;

    public WindowedWindow()
    {
        InitializeComponent();

        _settings = _appSettingsService.Load();
        _viewModel = new MainViewModel();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.UpdateSettings(GetViewModelSettings());
        DataContext = _viewModel;

        LoadingSpinnerImage.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "loading.png"), UriKind.Absolute));

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _progressTimer.Tick += ProgressTimer_OnTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        SizeChanged += Window_OnSizeChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _appearanceManager = new WindowAppearanceManager(this);
        _monitorHelper = new AppBarManager(this, AppBarDisplayMode.DefaultHeight);
        _trayIcon = new TrayIcon(this);
        WindowMaximizeBounds.Attach(this);

        ApplyInitialSize();
        ApplyAppearance();
        ApplyNativeWindowFrame();
        ApplyLyricAlignment();
        UpdatePlayPauseButtonImage();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RebuildLyricsList();
        ApplyLyricsVisibility(animated: false);
        UpdateAlbumArtAndCredit();
        UpdateProgressBar();
        ApplyLoadingState(immediate: true);
        await _viewModel.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowSize();
        _progressTimer.Stop();
        _progressTimer.Tick -= ProgressTimer_OnTick;
        StopWordAnim();
        if (_wordAnimTimer is not null)
        {
            _wordAnimTimer.Tick -= WordAnimTimer_Tick;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _monitorHelper?.Dispose();
        _settingsWindow?.Close();
        _trayIcon?.Dispose();
        _viewModel.Dispose();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _monitorHelper?.RefreshMonitors();
        RefreshSettingsWindowOptions();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        ApplyAppearance();
    }

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isSizeInitialized)
        {
            SaveWindowSize();
            CenterCurrentLyric(animated: false);
        }
    }

    private void Window_OnStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeButtonContent();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        void OnUi(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _ = Dispatcher.InvokeAsync(action);
            }
        }

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Lyrics):
                OnUi(() =>
                {
                    RebuildLyricsList();
                    ApplyLyricsVisibility();
                });
                break;
            case nameof(MainViewModel.CurrentLine):
            case nameof(MainViewModel.CurrentLineIndex):
                OnUi(() => UpdateCurrentLyricHighlight());
                break;
            case nameof(MainViewModel.CurrentWordIndex):
                OnUi(() =>
                {
                    if (_viewModel.WordByWordMode)
                    {
                        StartWordAnim();
                    }
                });
                break;
            case nameof(MainViewModel.NextLine):
                OnUi(RefreshOptionalPreviewLine);
                break;
            case nameof(MainViewModel.IsLoadingLyrics):
            case nameof(MainViewModel.NoTimedLyricsFound):
                OnUi(() =>
                {
                    RebuildLyricsList();
                    ApplyLyricsVisibility();
                    ApplyLoadingState();
                    UpdateLastSearchInfo();
                });
                break;
            case nameof(MainViewModel.IsPlaybackPaused):
                OnUi(UpdatePlayPauseButtonImage);
                break;
            case nameof(MainViewModel.AlbumArt):
            case nameof(MainViewModel.SongTitle):
            case nameof(MainViewModel.SongArtist):
            case nameof(MainViewModel.StatusText):
                OnUi(UpdateAlbumArtAndCredit);
                break;
            case nameof(MainViewModel.Progress):
                OnUi(UpdateProgressBar);
                break;
        }
    }

    private void RebuildLyricsList()
    {
        LyricsListPanel.Children.Clear();
        _lyricTextBlocks.Clear();
        _lyricVisualStates.Clear();
        _lastHighlightedLineIndex = -1;

        var lyrics = _viewModel.Lyrics;
        if (lyrics.Count == 0)
        {
            AddStatusLine(_viewModel.CurrentLine);
            return;
        }

        foreach (var lyric in lyrics)
        {
            var textBlock = CreateLyricTextBlock(lyric.Text);
            LyricsListPanel.Children.Add(textBlock);
            _lyricTextBlocks.Add(textBlock);
        }

        RefreshOptionalPreviewLine();
        UpdateCurrentLyricHighlight(animated: false);
    }

    private TextBlock CreateLyricTextBlock(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = InactiveLyricBrush,
            LineHeight = 34,
            Margin = new Thickness(0, 7, 0, 7),
            Opacity = 0.48,
            HorizontalAlignment = GetHorizontalAlignment(),
            RenderTransform = CreateLyricTransform(),
            RenderTransformOrigin = GetRenderTransformOrigin(),
            TextAlignment = GetTextAlignment(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static TransformGroup CreateLyricTransform()
    {
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(1, 1));
        transform.Children.Add(new TranslateTransform());
        return transform;
    }

    private void AddStatusLine(string text)
    {
        var textBlock = CreateLyricTextBlock(string.IsNullOrWhiteSpace(text) ? "Play something to show lyrics here." : text);
        textBlock.FontSize = 28;
        textBlock.Foreground = _viewModel.IsLoadingLyrics ? LoadingTextBrush : ActiveLyricBrush;
        textBlock.Opacity = 1;
        LyricsListPanel.Children.Add(textBlock);
        _lyricTextBlocks.Add(textBlock);
        ApplyLyricAlignment();
    }

    private void RefreshOptionalPreviewLine()
    {
        if (_viewModel.Lyrics.Count == 0 || _settings.WindowedShowNextLine)
        {
            return;
        }

        var currentIndex = _viewModel.CurrentLineIndex;
        if (currentIndex >= 0 && currentIndex + 1 < _lyricTextBlocks.Count)
        {
            _lyricTextBlocks[currentIndex + 1].Opacity = 0.2;
        }
    }

    private void UpdateCurrentLyricHighlight(bool animated = true)
    {
        if (_lyricTextBlocks.Count == 0)
        {
            return;
        }

        var currentIndex = _viewModel.Lyrics.Count == 0
            ? 0
            : Math.Clamp(_viewModel.CurrentLineIndex, 0, _lyricTextBlocks.Count - 1);
        var lineChanged = currentIndex != _lastHighlightedLineIndex;
        if (animated && !lineChanged)
        {
            ApplyCurrentWordHighlight(currentIndex, resetInlines: false);
            return;
        }

        if (lineChanged)
        {
            _lastLineChangeTimestamp = DateTime.UtcNow;
        }

        for (var i = 0; i < _lyricTextBlocks.Count; i++)
        {
            var distance = Math.Abs(i - currentIndex);
            var textBlock = _lyricTextBlocks[i];
            if (_viewModel.Lyrics.Count > i && i != currentIndex && textBlock.Inlines.Count > 0)
            {
                textBlock.Inlines.Clear();
                textBlock.Text = _viewModel.Lyrics[i].Text;
            }

            textBlock.Foreground = distance == 0 ? ActiveLyricBrush : InactiveLyricBrush;
            textBlock.FontWeight = distance == 0 ? FontWeights.Bold : FontWeights.SemiBold;

            textBlock.FontSize = 30;
            var targetScale = distance == 0 ? 1.14 : 0.9;
            var targetOpacity = distance switch
            {
                0 => 1,
                1 => _settings.WindowedShowNextLine ? 0.62 : 0.2,
                2 => 0.34,
                _ => 0.18
            };
            var targetY = distance == 0
                ? 0
                : i < currentIndex ? -10 : 10;

            ApplyLyricLineState(textBlock, targetScale, targetOpacity, targetY, animated && lineChanged);
        }

        ApplyCurrentWordHighlight(currentIndex, resetInlines: lineChanged);
        _lastHighlightedLineIndex = currentIndex;
        CenterCurrentLyric(animated: true);
    }

    private void ApplyLyricLineState(
        TextBlock textBlock,
        double targetScale,
        double targetOpacity,
        double targetY,
        bool animated)
    {
        if (textBlock.RenderTransform is not TransformGroup transform || transform.Children.Count < 2)
        {
            transform = CreateLyricTransform();
            textBlock.RenderTransform = transform;
        }

        var scale = (ScaleTransform)transform.Children[0];
        var translate = (TranslateTransform)transform.Children[1];
        var targetState = new LyricLineVisualState(targetScale, targetOpacity, targetY);

        if (_lyricVisualStates.TryGetValue(textBlock, out var previousState)
            && previousState.IsCloseTo(targetState))
        {
            return;
        }

        _lyricVisualStates[textBlock] = targetState;

        var currentOpacity = textBlock.Opacity;
        var currentScaleX = scale.ScaleX;
        var currentScaleY = scale.ScaleY;
        var currentY = translate.Y;
        textBlock.BeginAnimation(OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        textBlock.Opacity = currentOpacity;
        scale.ScaleX = currentScaleX;
        scale.ScaleY = currentScaleY;
        translate.Y = currentY;

        if (!animated)
        {
            scale.ScaleX = targetScale;
            scale.ScaleY = targetScale;
            textBlock.Opacity = targetOpacity;
            translate.Y = targetY;
            return;
        }

        textBlock.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });

        var scaleAnimation = new DoubleAnimation
        {
            To = targetScale,
            Duration = TimeSpan.FromMilliseconds(430),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation.Clone());

        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = targetY,
            Duration = TimeSpan.FromMilliseconds(360),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private readonly record struct LyricLineVisualState(double Scale, double Opacity, double Y)
    {
        public bool IsCloseTo(LyricLineVisualState other)
        {
            return Math.Abs(Scale - other.Scale) < 0.001
                && Math.Abs(Opacity - other.Opacity) < 0.001
                && Math.Abs(Y - other.Y) < 0.001;
        }
    }

    private void ApplyCurrentWordHighlight(int currentIndex, bool resetInlines)
    {
        if (!_viewModel.WordByWordMode
            || currentIndex < 0
            || currentIndex >= _lyricTextBlocks.Count
            || _viewModel.CurrentLyricLine?.Words is not { Count: > 0 } words)
        {
            StopWordAnim();
            return;
        }

        var textBlock = _lyricTextBlocks[currentIndex];
        var lineText = _viewModel.CurrentLyricLine.Text;
        if (!resetInlines
            && ReferenceEquals(textBlock, _wordAnimatedTextBlock)
            && string.Equals(_wordAnimatedLineText, lineText, StringComparison.Ordinal)
            && textBlock.Inlines.Count > 0)
        {
            StartWordAnim();
            return;
        }

        SetWordByWordInlines(textBlock, words, lineText);
    }

    private void SetWordByWordInlines(TextBlock textBlock, IReadOnlyList<WordInfo> words, string lineText)
    {
        StopWordAnim();
        for (var i = 0; i < words.Count; i++)
        {
            if (i == 0)
            {
                textBlock.Inlines.Clear();
            }

            var word = words[i].Word;
            for (var j = 0; j < word.Length; j++)
            {
                textBlock.Inlines.Add(new Run(word[j].ToString())
                {
                    Foreground = GetWordBrush(ActiveLyricBrush, 0.15)
                });
            }

            if (i < words.Count - 1)
            {
                textBlock.Inlines.Add(new Run(" ")
                {
                    Foreground = GetWordBrush(ActiveLyricBrush, 0.15)
                });
            }
        }

        _wordAnimatedTextBlock = textBlock;
        _wordAnimatedLineText = lineText;
        _wordCharOpacities = new double[textBlock.Inlines.Count];
        Array.Fill(_wordCharOpacities, 0.15);

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
        {
            _wordAnimTimer.Start();
        }
    }

    private void StopWordAnim()
    {
        if (_wordAnimTimer is not null && _wordAnimTimer.IsEnabled)
        {
            _wordAnimTimer.Stop();
        }
    }

    private void WordAnimTimer_Tick(object? sender, EventArgs e)
    {
        var line = _viewModel.CurrentLyricLine;
        var words = line?.Words;
        var textBlock = _wordAnimatedTextBlock;
        if (words is null || words.Count == 0 || textBlock is null)
        {
            StopWordAnim();
            return;
        }

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

        var inlines = textBlock.Inlines.ToList();
        var totalChars = inlines.Count;
        if (totalChars == 0)
        {
            return;
        }

        if (_wordCharOpacities is null || _wordCharOpacities.Length != totalChars)
        {
            _wordCharOpacities = new double[totalChars];
            Array.Fill(_wordCharOpacities, 0.15);
        }

        var msSinceChange = (DateTime.UtcNow - _lastLineChangeTimestamp).TotalMilliseconds;
        var lookAhead = msSinceChange < 150 ? 0.0 : Math.Min(250.0, (msSinceChange - 150.0) * 2.5);

        var adjustedPos = position.Value + TimeSpan.FromMilliseconds(lookAhead);
        var songDuration = _viewModel.SongDuration;
        if (songDuration > TimeSpan.Zero && adjustedPos > songDuration)
        {
            adjustedPos = songDuration;
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

        fillPos ??= adjustedPos < words[0].Timestamp ? 0.0 : 1.0;

        for (var i = 0; i < totalChars; i++)
        {
            var charPos = (double)i / totalChars;
            var diff = (fillPos.Value - charPos) * totalChars;
            var target = Math.Clamp(0.15 + 0.85 * diff, 0.15, 1.0);
            var current = _wordCharOpacities[i];
            var err = target - current;
            _wordCharOpacities[i] = Math.Abs(err) < 0.003 ? target : current + err * 0.28;

            if (inlines[i] is Run run)
            {
                run.Foreground = GetWordBrush(ActiveLyricBrush, _wordCharOpacities[i]);
            }
        }
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

    private void CenterCurrentLyric(bool animated)
    {
        if (_lyricTextBlocks.Count == 0)
        {
            return;
        }

        var currentIndex = _viewModel.Lyrics.Count == 0
            ? 0
            : Math.Clamp(_viewModel.CurrentLineIndex, 0, _lyricTextBlocks.Count - 1);

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (currentIndex >= _lyricTextBlocks.Count)
            {
                return;
            }

            var line = _lyricTextBlocks[currentIndex];
            var lineTop = GetStableLineTop(currentIndex);
            var target = Math.Max(0, lineTop - (LyricsScrollViewer.ViewportHeight / 2) + (line.ActualHeight / 2));

            if (!animated)
            {
                LyricsScrollViewer.ScrollToVerticalOffset(target);
                return;
            }

            AnimateScrollTo(target);
        }, DispatcherPriority.Loaded);
    }

    private void AnimateScrollTo(double target)
    {
        var currentOffset = LyricsScrollViewer.VerticalOffset;
        BeginAnimation(AnimatedScrollOffsetProperty, null);
        SetCurrentValue(AnimatedScrollOffsetProperty, currentOffset);

        var animation = new DoubleAnimation
        {
            From = currentOffset,
            To = target,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        BeginAnimation(AnimatedScrollOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private double GetStableLineTop(int targetIndex)
    {
        var top = 0.0;
        for (var i = 0; i < targetIndex && i < _lyricTextBlocks.Count; i++)
        {
            var block = _lyricTextBlocks[i];
            top += block.ActualHeight + block.Margin.Top + block.Margin.Bottom;
        }

        return top + (targetIndex < _lyricTextBlocks.Count ? _lyricTextBlocks[targetIndex].Margin.Top : 0);
    }

    public static readonly DependencyProperty AnimatedScrollOffsetProperty =
        DependencyProperty.Register(
            nameof(AnimatedScrollOffset),
            typeof(double),
            typeof(WindowedWindow),
            new PropertyMetadata(0.0, OnAnimatedScrollOffsetChanged));

    public double AnimatedScrollOffset
    {
        get => (double)GetValue(AnimatedScrollOffsetProperty);
        set => SetValue(AnimatedScrollOffsetProperty, value);
    }

    private static void OnAnimatedScrollOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WindowedWindow window)
        {
            window.LyricsScrollViewer.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    private void ApplyAppearance()
    {
        if (_appearanceManager is null)
        {
            return;
        }

        var palette = _appearanceManager.Apply();
        Background = palette.WindowBackground;
        SurfaceBorder.Background = System.Windows.Media.Brushes.Transparent;
        SurfaceBorder.BorderBrush = palette.SurfaceBorder;
        SurfaceBorder.BorderThickness = new Thickness(0, 0, 0, 1);
        SettingsButton.Background = palette.ButtonBackground;
        SettingsButton.BorderBrush = palette.ButtonBorder;
        ProgressBarFill.Background = palette.ProgressBarBrush;
        BackgroundAlbumArtImage.Visibility = Visibility.Visible;
        BackgroundDimOverlay.Visibility = Visibility.Visible;
        BackgroundFallbackBorder.Visibility = BackgroundAlbumArtImage.Source is null
            ? Visibility.Visible
            : Visibility.Collapsed;

        AlbumArtPanel.Visibility = _settings.WindowedShowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
        SongCreditPanel.Visibility = Visibility.Visible;
        ProgressBarTrack.Visibility = Visibility.Visible;
        LyricsGrid.Visibility = Visibility.Visible;
        ApplyNativeWindowFrame();
    }

    private void ApplyLyricsVisibility(bool animated = true)
    {
        var shouldShow = _viewModel.IsLoadingLyrics || !_viewModel.NoTimedLyricsFound;
        var targetOpacity = shouldShow ? 1.0 : 0.0;

        LyricsScrollViewer.BeginAnimation(OpacityProperty, null);

        if (!animated)
        {
            LyricsScrollViewer.Opacity = targetOpacity;
            return;
        }

        LyricsScrollViewer.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void ApplyNativeWindowFrame()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var cornerPreference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, Marshal.SizeOf<int>());

        var borderColor = DwmSubtleBorderColor;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, Marshal.SizeOf<int>());
    }

    private void ApplyLyricAlignment()
    {
        var textAlignment = GetTextAlignment();
        var horizontalAlignment = GetHorizontalAlignment();
        var renderTransformOrigin = GetRenderTransformOrigin();
        foreach (var textBlock in _lyricTextBlocks)
        {
            textBlock.TextAlignment = textAlignment;
            textBlock.HorizontalAlignment = horizontalAlignment;
            textBlock.RenderTransformOrigin = renderTransformOrigin;
        }
    }

    private System.Windows.HorizontalAlignment GetHorizontalAlignment()
    {
        return _settings.WindowedLyricAlignment switch
        {
            LyricAlignment.Left => System.Windows.HorizontalAlignment.Left,
            LyricAlignment.Right => System.Windows.HorizontalAlignment.Right,
            _ => System.Windows.HorizontalAlignment.Stretch
        };
    }

    private System.Windows.Point GetRenderTransformOrigin()
    {
        return _settings.WindowedLyricAlignment switch
        {
            LyricAlignment.Left => new System.Windows.Point(0, 0.5),
            LyricAlignment.Right => new System.Windows.Point(1, 0.5),
            _ => new System.Windows.Point(0.5, 0.5)
        };
    }

    private TextAlignment GetTextAlignment()
    {
        return _settings.WindowedLyricAlignment switch
        {
            LyricAlignment.Left => TextAlignment.Left,
            LyricAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Center
        };
    }

    private void UpdatePlayPauseButtonImage()
    {
        var imageName = _viewModel.IsPlaybackPaused ? "play-player.png" : "pause-player.png";
        PlayPauseButtonImage.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", imageName), UriKind.Absolute));
    }

    private void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.TogglePlayPauseAsync();
    }

    private void ApplyLoadingState(bool immediate = false)
    {
        var isLoading = _viewModel.IsLoadingLyrics;
        LoadingSpinnerImage.BeginAnimation(OpacityProperty, null);
        LoadingSpinnerRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);

        if (isLoading)
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            LoadingSpinnerImage.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(immediate ? 1 : 150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });

            LoadingSpinnerRotateTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromMilliseconds(850),
                RepeatBehavior = RepeatBehavior.Forever
            });
            return;
        }

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

    private void UpdateAlbumArtAndCredit()
    {
        SongTitleTextBlock.Text = _viewModel.SongTitle;
        SongArtistTextBlock.Text = _viewModel.SongArtist;
        SongTimestampTextBlock.Text = FormatTimestamp(_viewModel.StatusText);

        var newArt = _viewModel.AlbumArt;
        if (!IsAlbumArtDifferent(newArt, _lastAlbumArtData))
        {
            return;
        }

        _lastAlbumArtData = newArt;
        UpdateAlbumArt(newArt);
    }

    private void UpdateAlbumArt(byte[]? newArt)
    {
        if (newArt is null || newArt.Length == 0)
        {
            AlbumArtImage.Source = null;
            UpdateAlbumArtBackground(null);
            AlbumArtPanel.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(newArt);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            UpdateAlbumArtBackground(bitmap);
            if (!_settings.WindowedShowAlbumArt)
            {
                AlbumArtPanel.Visibility = Visibility.Collapsed;
                return;
            }

            AlbumArtPanel.Visibility = Visibility.Visible;
            AlbumArtImage.Source = bitmap;
        }
        catch
        {
            AlbumArtImage.Source = null;
            UpdateAlbumArtBackground(null);
        }
    }

    private void UpdateAlbumArtBackground(ImageSource? source)
    {
        BackgroundAlbumArtImage.Source = source;
        var hasArt = source is not null;
        BackgroundAlbumArtImage.Opacity = hasArt ? 1 : 0;
        BackgroundFallbackBorder.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool IsAlbumArtDifferent(byte[]? a, byte[]? b)
    {
        if (a is null && b is null) return false;
        if (a is null || b is null) return true;
        if (a.Length != b.Length) return true;
        return !a.AsSpan().SequenceEqual(b);
    }

    private static string FormatTimestamp(string statusText)
    {
        if (string.IsNullOrWhiteSpace(statusText))
        {
            return string.Empty;
        }

        var dashIndex = statusText.IndexOf(" - ", StringComparison.Ordinal);
        return dashIndex >= 0 ? statusText[(dashIndex + 3)..] : statusText;
    }

    private void UpdateProgressBar()
    {
        _progressTimer.Start();
    }

    private void ProgressTimer_OnTick(object? sender, EventArgs e)
    {
        var position = _viewModel.EstimatedPosition;
        var duration = _viewModel.SongDuration;

        if (position is null || duration <= TimeSpan.Zero)
        {
            ProgressBarFill.Width = 0;
            _progressTimer.Stop();
            return;
        }

        var progress = Math.Clamp(position.Value.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
        ProgressBarFill.Width = Math.Max(0, progress * ProgressBarTrack.ActualWidth);
    }

    private void ApplyInitialSize()
    {
        if (_settings.WindowedWidth.HasValue && _settings.WindowedHeight.HasValue)
        {
            Width = Math.Max(520, _settings.WindowedWidth.Value);
            Height = Math.Max(260, _settings.WindowedHeight.Value);
        }

        _isSizeInitialized = true;
    }

    private void SaveWindowSize()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _settings.WindowedWidth = Width;
        _settings.WindowedHeight = Height;
        _appSettingsService.Save(_settings);
    }

    private void Window_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isPointerOverWindow = true;
        UpdateHoverEffect(true);
    }

    private void Window_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isPointerOverWindow = false;
        UpdateHoverEffect(false);
    }

    private void Window_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPointerOverWindow)
        {
            _isPointerOverWindow = true;
        }
    }

    private void UpdateHoverEffect(bool isHovering)
    {
        var targetBgColor = isHovering ? MediaColor.FromArgb(140, 16, 26, 37) : MediaColor.FromArgb(102, 16, 26, 37);
        var targetBorderColor = isHovering ? MediaColor.FromArgb(160, 48, 70, 92) : MediaColor.FromArgb(138, 48, 70, 92);
        var duration = isHovering ? 150 : 250;

        if (SurfaceBorder.Background is SolidColorBrush bgBrush && bgBrush.Color.A > 0)
        {
            bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            {
                To = targetBgColor,
                Duration = TimeSpan.FromMilliseconds(duration),
                EasingFunction = new QuadraticEase { EasingMode = isHovering ? EasingMode.EaseOut : EasingMode.EaseIn }
            });
        }

        if (SurfaceBorder.BorderBrush is SolidColorBrush borderBrush && borderBrush.Color.A > 0)
        {
            borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            {
                To = targetBorderColor,
                Duration = TimeSpan.FromMilliseconds(duration),
                EasingFunction = new QuadraticEase { EasingMode = isHovering ? EasingMode.EaseOut : EasingMode.EaseIn }
            });
        }
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsFromTray();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeButtonContent();
    }

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void UpdateMaximizeButtonContent()
    {
        if (MaximizeButtonImage is not null)
        {
            MaximizeButtonImage.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", WindowState == WindowState.Maximized ? "restore.png" : "maximize.png"), UriKind.Absolute));
        }
    }

    private void RefreshSettingsWindowOptions()
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _settings = _appSettingsService.Load();
        var monitors = _monitorHelper?.Monitors
            .Select((monitor, index) => new MonitorOption(
                monitor.DeviceName,
                monitor.IsPrimary
                    ? $"Monitor {index + 1} ({monitor.DeviceName}, Primary)"
                    : $"Monitor {index + 1} ({monitor.DeviceName})"))
            .ToList() ?? new List<MonitorOption>();

        _settingsWindow.LoadSettings(_settings, monitors);
        _settingsWindow.UpdateLastSearchInfo(_viewModel.LastSearchInfo);
    }

    private void SettingsWindow_OnSettingsChanged(object? sender, AppSettings settings)
    {
        _settings = MergeSettings(settings);
        _appSettingsService.Save(_settings);
        _viewModel.UpdateSettings(GetViewModelSettings());

        if (_settings.DisplayMode != DisplayMode.Windowed)
        {
            ((App)WpfApplication.Current).RestartDisplayWindow();
            return;
        }

        RebuildLyricsList();
        ApplyLyricsVisibility(animated: false);
        UpdateAlbumArtAndCredit();
        ApplyLyricAlignment();
        UpdatePlayPauseButtonImage();
        ApplyLoadingState(immediate: true);
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
        persistedSettings.CustomBarHeight = incomingSettings.CustomBarHeight;
        persistedSettings.WindowedWidth = incomingSettings.WindowedWidth;
        persistedSettings.WindowedHeight = incomingSettings.WindowedHeight;
        persistedSettings.TaskbarMaximumWidth = incomingSettings.TaskbarMaximumWidth;
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
        persistedSettings.DetectedMediaApps = MergeDetectedApps(incomingSettings.DetectedMediaApps, persistedSettings.DetectedMediaApps);
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

    private AppSettings GetViewModelSettings()
    {
        var copy = _appSettingsService.Load();
        copy.WordByWordMode = _settings.WindowedWordByWordMode;
        return copy;
    }

    private void UpdateLastSearchInfo()
    {
        _settingsWindow?.UpdateLastSearchInfo(_viewModel.LastSearchInfo);
    }

    private void HideToTray()
    {
        Hide();
        _trayIcon ??= new TrayIcon(this);
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        ApplyAppearance();
        CenterCurrentLyric(animated: false);
    }

    public void OpenSettingsFromTray()
    {
        ShowFromTray();

        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow { Owner = this };
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

    public DisplayMode CurrentDisplayMode => DisplayMode.Windowed;

    public void SwitchDisplayMode(DisplayMode mode)
    {
        if (_settings.DisplayMode == mode) return;
        _settings.DisplayMode = mode;
        _appSettingsService.Save(_settings);
        ((App)WpfApplication.Current).RestartDisplayWindow();
    }

    public void ExitApp()
    {
        Close();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
