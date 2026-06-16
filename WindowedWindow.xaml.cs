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
    private readonly List<TtmlLyricRow> _ttmlLyricRows = new();
    private readonly List<TtmlRenderedLine> _ttmlRenderedLines = new();
    private string _ttmlLyricsListKey = string.Empty;
    private readonly Dictionary<TextBlock, bool> _ttmlPrimaryActiveStates = new();
    private readonly Dictionary<TextBlock, bool> _ttmlSubLineActiveStates = new();
    private readonly Dictionary<TextBlock, double[]> _ttmlWordOpacityStates = new();
    private readonly HashSet<TextBlock> _ttmlWordActiveTextBlocks = new();
    private readonly Dictionary<TextBlock, (int Distance, bool IsActiveMain)> _ttmlMainRowStates = new();
    private int _lastTtmlCurrentRowIndex = -1;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _trayIcon;
    private WindowAppearanceManager? _appearanceManager;
    private bool IsTtmlLayoutActive => _settings.WindowedDisplayTtmlLyrics
        && _viewModel.HasTtmlLyrics
        && !_viewModel.IsLoadingLyrics
        && !_viewModel.NoTimedLyricsFound;

    private bool IsCleanedLrcActive => !IsTtmlLayoutActive && _viewModel.CleanedLrcLyrics.Count > 0;

    private IReadOnlyList<LyricLine> GetEffectiveLyrics() =>
        IsCleanedLrcActive ? _viewModel.CleanedLrcLyrics : _viewModel.Lyrics;

    private int GetEffectiveCurrentIndex() =>
        IsCleanedLrcActive ? _viewModel.CleanedLrcCurrentLineIndex : _viewModel.CurrentLineIndex;

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
        ApplyLyricLayoutMode();
        RenderTtmlLyrics();
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
            if (IsTtmlLayoutActive)
            {
                UpdateTtmlLyricHighlight(animated: false);
            }
            else
            {
                CenterCurrentLyric(animated: false);
            }
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
                    ApplyLyricLayoutMode();
                    RenderTtmlLyrics();
                    ApplyLyricsVisibility();
                });
                break;
            case nameof(MainViewModel.CurrentLine):
                OnUi(() =>
                {
                    if (!IsTtmlLayoutActive)
                    {
                        UpdateCurrentLyricHighlight();
                    }
                });
                break;
            case nameof(MainViewModel.CurrentLineIndex):
                OnUi(() =>
                {
                    if (IsTtmlLayoutActive)
                    {
                        RenderTtmlLyrics();
                    }
                    else
                    {
                        UpdateCurrentLyricHighlight();
                    }
                });
                break;
            case nameof(MainViewModel.CurrentWordIndex):
                OnUi(() =>
                {
                    if (IsTtmlLayoutActive || _viewModel.WordByWordMode)
                    {
                        StartWordAnim();
                    }
                });
                break;
            case nameof(MainViewModel.NextLine):
                OnUi(() =>
                {
                    if (!IsTtmlLayoutActive)
                    {
                        RefreshOptionalPreviewLine();
                    }
                });
                break;
            case nameof(MainViewModel.IsLoadingLyrics):
            case nameof(MainViewModel.NoTimedLyricsFound):
                OnUi(() =>
                {
                    RebuildLyricsList();
                    ApplyLyricLayoutMode();
                    RenderTtmlLyrics();
                    ApplyLyricsVisibility();
                    ApplyLoadingState();
                    UpdateLastSearchInfo();
                });
                break;
            case nameof(MainViewModel.ActiveLyricLines):
            case nameof(MainViewModel.HasTtmlLyrics):
                OnUi(() =>
                {
                    ApplyLyricLayoutMode();
                    RenderTtmlLyrics();
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
        _wordAnimatedTextBlock = null;
        _wordAnimatedLineText = string.Empty;
        StopWordAnim();

        var lyrics = GetEffectiveLyrics();
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

    private void ApplyLyricLayoutMode()
    {
        var useTtmlLayout = IsTtmlLayoutActive;
        LyricsScrollViewer.Visibility = useTtmlLayout ? Visibility.Collapsed : Visibility.Visible;
        TtmlLyricsScrollViewer.Visibility = useTtmlLayout ? Visibility.Visible : Visibility.Collapsed;
        LyricsScrollViewer.Opacity = useTtmlLayout ? 0 : LyricsScrollViewer.Opacity;
        TtmlLyricsScrollViewer.Opacity = useTtmlLayout ? 1 : 0;

        if (!useTtmlLayout)
        {
            ClearTtmlLyricsList();
        }
    }

    private void RenderTtmlLyrics()
    {
        if (!IsTtmlLayoutActive)
        {
            return;
        }

        EnsureTtmlLyricsList();
        UpdateTtmlLyricHighlight();
    }

    private void EnsureTtmlLyricsList()
    {
        var lyrics = _viewModel.Lyrics.Where(line => line.IsTtml).ToList();
        var key = string.Join(";", lyrics.Select(GetTtmlLineKey));
        if (string.Equals(_ttmlLyricsListKey, key, StringComparison.Ordinal))
        {
            return;
        }

        ClearTtmlLyricsList();
        _ttmlLyricsListKey = key;

        if (lyrics.Count == 0)
        {
            return;
        }

        var mainLines = lyrics.Where(line => !line.IsBackground).ToList();
        var backgroundLines = lyrics.Where(line => line.IsBackground).ToList();
        var assignedBackgroundLines = new HashSet<LyricLine>();

        foreach (var mainLine in mainLines)
        {
            var subLines = backgroundLines
                .Where(backgroundLine => ReferenceEquals(GetBestMainLineForSubLine(backgroundLine, mainLines), mainLine))
                .ToList();
            foreach (var subLine in subLines)
            {
                assignedBackgroundLines.Add(subLine);
            }

            AddTtmlRow(mainLine, subLines);
        }

        foreach (var orphanSubLine in backgroundLines.Where(line => !assignedBackgroundLines.Contains(line)))
        {
            AddTtmlRow(orphanSubLine, []);
        }
    }

    private void ClearTtmlLyricsList()
    {
        _ttmlLyricsListKey = string.Empty;
        _ttmlLyricRows.Clear();
        _ttmlRenderedLines.Clear();
        _ttmlPrimaryActiveStates.Clear();
        _ttmlSubLineActiveStates.Clear();
        _ttmlWordOpacityStates.Clear();
        _ttmlWordActiveTextBlocks.Clear();
        _ttmlMainRowStates.Clear();
        _lastTtmlCurrentRowIndex = -1;
        TtmlLyricsListPanel.Children.Clear();
    }

    private void AddTtmlRow(LyricLine mainLine, IReadOnlyList<LyricLine> subLines)
    {
        var rowPanel = new StackPanel
        {
            Margin = new Thickness(0, 7, 0, 7),
            HorizontalAlignment = GetHorizontalAlignment()
        };

        var mainTextBlock = CreateTtmlLineTextBlock(mainLine, isSubLine: false);
        rowPanel.Children.Add(mainTextBlock);

        var subLineBlocks = new List<TtmlSubLineBlock>();
        foreach (var subLine in subLines)
        {
            var subLineTextBlock = CreateTtmlLineTextBlock(subLine, isSubLine: true);
            subLineTextBlock.Visibility = Visibility.Visible;
            subLineTextBlock.Opacity = 0;
            subLineTextBlock.MaxHeight = 0;
            subLineTextBlock.RenderTransform = new ScaleTransform(0.94, 0.94);
            subLineTextBlock.RenderTransformOrigin = GetRenderTransformOrigin();
            rowPanel.Children.Add(subLineTextBlock);
            subLineBlocks.Add(new TtmlSubLineBlock(subLine, subLineTextBlock));
        }

        TtmlLyricsListPanel.Children.Add(rowPanel);
        _ttmlLyricRows.Add(new TtmlLyricRow(mainLine, rowPanel, mainTextBlock, subLineBlocks));
    }

    private void UpdateTtmlLyricHighlight(bool animated = true)
    {
        if (_ttmlLyricRows.Count == 0)
        {
            return;
        }

        var activeLines = _viewModel.ActiveLyricLines.Where(line => line.IsTtml).ToList();
        var activeSet = new HashSet<LyricLine>(activeLines);
        var activeMainLines = activeLines.Where(line => !line.IsBackground).ToList();
        var currentMainLine = activeMainLines.LastOrDefault()
            ?? activeLines
                .Where(line => line.IsBackground)
                .Select(line => GetBestMainLineForSubLine(line, _ttmlLyricRows.Select(row => row.MainLine).ToList()))
                .LastOrDefault(line => line is not null)
            ?? _viewModel.CurrentLyricLine;

        var currentRowIndex = _ttmlLyricRows.FindIndex(row => ReferenceEquals(row.MainLine, currentMainLine));
        if (currentRowIndex < 0)
        {
            currentRowIndex = Math.Clamp(_viewModel.CurrentLineIndex, 0, _ttmlLyricRows.Count - 1);
        }

        _ttmlRenderedLines.Clear();
        var activeWordBlocksThisFrame = new HashSet<TextBlock>();
        for (var i = 0; i < _ttmlLyricRows.Count; i++)
        {
            var row = _ttmlLyricRows[i];
            var distance = Math.Abs(i - currentRowIndex);
            var isActiveMain = activeSet.Contains(row.MainLine);
            var targetOpacity = distance switch
            {
                0 => 1,
                1 => 0.72,
                2 => 0.46,
                _ => 0.28
            };
            if (isActiveMain)
            {
                targetOpacity = 1;
            }

            var stateChanged = true;
            if (_ttmlMainRowStates.TryGetValue(row.MainTextBlock, out var lastState))
            {
                stateChanged = lastState.Distance != distance || lastState.IsActiveMain != isActiveMain;
            }

            if (stateChanged)
            {
                _ttmlMainRowStates[row.MainTextBlock] = (distance, isActiveMain);
                row.MainTextBlock.Foreground = distance == 0 || isActiveMain ? ActiveLyricBrush : InactiveLyricBrush;
                row.MainTextBlock.FontWeight = distance == 0 || isActiveMain ? FontWeights.Bold : FontWeights.SemiBold;
                ApplyTtmlRowState(row.MainPanel, targetOpacity);
                ApplyTtmlPrimaryLineState(row.MainTextBlock, distance == 0 || isActiveMain, animated);
            }

            AddActiveTtmlRenderedLine(row.MainLine, row.MainTextBlock, isActiveMain, activeWordBlocksThisFrame);
            if (!isActiveMain)
            {
                ResetTtmlWordRuns(row.MainLine, row.MainTextBlock);
            }

            foreach (var subLineBlock in row.SubLineBlocks)
            {
                var isActiveSubLine = activeSet.Contains(subLineBlock.Line);
                ApplyTtmlSubLineState(subLineBlock.TextBlock, isActiveSubLine, animated);
                if (isActiveSubLine)
                {
                    AddActiveTtmlRenderedLine(subLineBlock.Line, subLineBlock.TextBlock, active: true, activeWordBlocksThisFrame);
                }
                else
                {
                    ResetTtmlWordRuns(subLineBlock.Line, subLineBlock.TextBlock);
                }
            }
        }

        _ttmlWordActiveTextBlocks.RemoveWhere(textBlock => !activeWordBlocksThisFrame.Contains(textBlock));

        if (currentRowIndex != _lastTtmlCurrentRowIndex || !animated)
        {
            _lastTtmlCurrentRowIndex = currentRowIndex;
            CenterTtmlCurrentLyric(currentRowIndex, animated);
        }

        if (_ttmlRenderedLines.Any(line => line.Line.Words?.Count > 0))
        {
            StartWordAnim();
        }
        else if (IsTtmlLayoutActive)
        {
            StopWordAnim();
        }
    }

    private void AddActiveTtmlRenderedLine(LyricLine line, TextBlock textBlock, bool active, ISet<TextBlock> activeWordBlocksThisFrame)
    {
        if (line.Words is not { Count: > 0 } || !active)
        {
            return;
        }

        var inlines = textBlock.Inlines.ToList();
        activeWordBlocksThisFrame.Add(textBlock);
        var newlyActive = !_ttmlWordActiveTextBlocks.Contains(textBlock);
        _ttmlWordActiveTextBlocks.Add(textBlock);
        if (!_ttmlWordOpacityStates.TryGetValue(textBlock, out var opacities)
            || opacities.Length != inlines.Count)
        {
            opacities = new double[inlines.Count];
            _ttmlWordOpacityStates[textBlock] = opacities;
            newlyActive = true;
        }

        if (newlyActive)
        {
            for (var i = 0; i < opacities.Length; i++)
            {
                opacities[i] = 0.18;
            }

            ResetTtmlWordRuns(line, textBlock);
        }

        _ttmlRenderedLines.Add(new TtmlRenderedLine(line, textBlock, opacities));
    }

    private void ResetTtmlWordRuns(LyricLine line, TextBlock textBlock)
    {
        if (textBlock.Inlines.Count == 0)
        {
            return;
        }

        var baseBrush = line.IsBackground ? GetWordBrush(ActiveLyricBrush, 0.72) : ActiveLyricBrush;
        var dimBrush = GetWordBrush(baseBrush, 0.18);
        foreach (var inline in textBlock.Inlines)
        {
            if (inline is Run run)
            {
                run.Foreground = dimBrush;
            }
        }
    }

    private void ApplyTtmlRowState(StackPanel rowPanel, double targetOpacity)
    {
        rowPanel.BeginAnimation(OpacityProperty, null);
        rowPanel.RenderTransform = null;
        rowPanel.Opacity = targetOpacity;
    }

    private void ApplyTtmlPrimaryLineState(TextBlock textBlock, bool active, bool animated)
    {
        if (_ttmlPrimaryActiveStates.TryGetValue(textBlock, out var previousActive)
            && previousActive == active)
        {
            return;
        }

        _ttmlPrimaryActiveStates[textBlock] = active;

        if (textBlock.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            textBlock.RenderTransform = scale;
            textBlock.RenderTransformOrigin = GetRenderTransformOrigin();
        }

        var currentScaleX = scale.ScaleX;
        var currentScaleY = scale.ScaleY;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = currentScaleX;
        scale.ScaleY = currentScaleY;

        var targetScale = active ? 1.08 : 1;
        if (!animated)
        {
            scale.ScaleX = targetScale;
            scale.ScaleY = targetScale;
            return;
        }

        var scaleAnimation = new DoubleAnimation
        {
            To = targetScale,
            Duration = TimeSpan.FromMilliseconds(360),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation.Clone());
    }

    private void ApplyTtmlSubLineState(TextBlock textBlock, bool active, bool animated)
    {
        if (_ttmlSubLineActiveStates.TryGetValue(textBlock, out var previousActive)
            && previousActive == active)
        {
            return;
        }

        _ttmlSubLineActiveStates[textBlock] = active;

        if (textBlock.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(0.94, 0.94);
            textBlock.RenderTransform = scale;
            textBlock.RenderTransformOrigin = GetRenderTransformOrigin();
        }

        var currentOpacity = textBlock.Opacity;
        var currentMaxHeight = textBlock.MaxHeight;
        var currentScaleX = scale.ScaleX;
        var currentScaleY = scale.ScaleY;
        textBlock.BeginAnimation(OpacityProperty, null);
        textBlock.BeginAnimation(MaxHeightProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        textBlock.Opacity = currentOpacity;
        textBlock.MaxHeight = currentMaxHeight;
        scale.ScaleX = currentScaleX;
        scale.ScaleY = currentScaleY;

        if (active)
        {
            textBlock.Visibility = Visibility.Visible;
        }

        var targetOpacity = active ? 0.78 : 0;
        var targetScale = active ? 1 : 0.94;
        if (!animated)
        {
            textBlock.Opacity = targetOpacity;
            textBlock.MaxHeight = active ? GetTtmlSubLineExpandedHeight(textBlock) : 0;
            scale.ScaleX = targetScale;
            scale.ScaleY = targetScale;
            return;
        }

        var heightAnimation = new DoubleAnimation
        {
            To = active ? GetTtmlSubLineExpandedHeight(textBlock) : 0,
            Duration = TimeSpan.FromMilliseconds(active ? 280 : 190),
            EasingFunction = new CubicEase { EasingMode = active ? EasingMode.EaseOut : EasingMode.EaseIn }
        };

        var opacityAnimation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(active ? 240 : 180),
            EasingFunction = new QuadraticEase { EasingMode = active ? EasingMode.EaseOut : EasingMode.EaseIn }
        };

        textBlock.BeginAnimation(MaxHeightProperty, heightAnimation);
        textBlock.BeginAnimation(OpacityProperty, opacityAnimation);
        var scaleAnimation = new DoubleAnimation
        {
            To = targetScale,
            Duration = TimeSpan.FromMilliseconds(active ? 280 : 180),
            EasingFunction = new CubicEase { EasingMode = active ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation.Clone());
    }

    private static double GetTtmlSubLineExpandedHeight(TextBlock textBlock)
    {
        return textBlock.LineHeight > 0
            ? textBlock.LineHeight + textBlock.Margin.Top + textBlock.Margin.Bottom
            : textBlock.FontSize * 1.35 + textBlock.Margin.Top + textBlock.Margin.Bottom;
    }

    private TextBlock CreateTtmlLineTextBlock(LyricLine line, bool isSubLine)
    {
        var textBlock = new TextBlock
        {
            Text = line.Text,
            FontSize = isSubLine ? 22 : 32,
            FontWeight = isSubLine ? FontWeights.SemiBold : FontWeights.Bold,
            Foreground = isSubLine ? GetWordBrush(ActiveLyricBrush, 0.72) : ActiveLyricBrush,
            LineHeight = isSubLine ? 29 : 40,
            Margin = isSubLine ? new Thickness(0, 2, 0, 0) : new Thickness(0, 0, 0, 2),
            Opacity = isSubLine ? 0.78 : 1,
            HorizontalAlignment = GetHorizontalAlignment(),
            TextAlignment = GetTextAlignment(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap
        };

        if (!isSubLine)
        {
            textBlock.RenderTransform = new ScaleTransform(1, 1);
            textBlock.RenderTransformOrigin = GetRenderTransformOrigin();
        }

        SetTtmlWordByWordInlines(textBlock, line, isSubLine);
        return textBlock;
    }

    private void SetTtmlWordByWordInlines(TextBlock textBlock, LyricLine line, bool isSubLine)
    {
        if (line.Words is not { Count: > 0 } words)
        {
            return;
        }

        textBlock.Inlines.Clear();
        var baseBrush = isSubLine ? GetWordBrush(ActiveLyricBrush, 0.72) : ActiveLyricBrush;
        var dimBrush = GetWordBrush(baseBrush, 0.18);
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i].Word;
            for (var j = 0; j < word.Length; j++)
            {
                textBlock.Inlines.Add(new Run(word[j].ToString()) { Foreground = dimBrush });
            }

            if (i < words.Count - 1)
            {
                textBlock.Inlines.Add(new Run(" ") { Foreground = dimBrush });
            }
        }

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
        var lyrics = GetEffectiveLyrics();
        if (lyrics.Count == 0 || _settings.WindowedShowNextLine)
        {
            return;
        }

        var currentIndex = GetEffectiveCurrentIndex();
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

        var effectiveLyrics = GetEffectiveLyrics();
        var effectiveIndex = GetEffectiveCurrentIndex();
        var currentIndex = effectiveLyrics.Count == 0
            ? 0
            : Math.Clamp(effectiveIndex, 0, _lyricTextBlocks.Count - 1);
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
            if (effectiveLyrics.Count > i && i != currentIndex && textBlock.Inlines.Count > 0)
            {
                textBlock.Inlines.Clear();
                textBlock.Text = effectiveLyrics[i].Text;
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

    private sealed record TtmlRenderedLine(LyricLine Line, TextBlock TextBlock, double[]? CharOpacities);

    private sealed record TtmlLyricRow(
        LyricLine MainLine,
        StackPanel MainPanel,
        TextBlock MainTextBlock,
        IReadOnlyList<TtmlSubLineBlock> SubLineBlocks);

    private sealed record TtmlSubLineBlock(LyricLine Line, TextBlock TextBlock);

    private void ApplyCurrentWordHighlight(int currentIndex, bool resetInlines)
    {
        if (!_viewModel.WordByWordMode
            || currentIndex < 0
            || currentIndex >= _lyricTextBlocks.Count
            || _viewModel.CurrentLyricLine?.Words is not { Count: > 0 } words)
        {
            StopWordAnim();
            _wordAnimatedTextBlock = null;
            _wordAnimatedLineText = string.Empty;
            return;
        }

        var textBlock = _lyricTextBlocks[currentIndex];
        var lineText = _viewModel.CurrentLyricLine.Text;

        if (IsCleanedLrcActive)
        {
            var effectiveLyrics = _viewModel.CleanedLrcLyrics;
            if (currentIndex >= effectiveLyrics.Count
                || !string.Equals(lineText, effectiveLyrics[currentIndex].Text, StringComparison.Ordinal))
            {
                StopWordAnim();
                _wordAnimatedTextBlock = null;
                _wordAnimatedLineText = string.Empty;
                return;
            }
        }

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
        if (IsTtmlLayoutActive)
        {
            UpdateTtmlWordHighlights();
            return;
        }

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

        var updatedAny = false;
        foreach (var renderedLine in _ttmlRenderedLines)
        {
            updatedAny |= UpdateTtmlWordHighlightsForLine(renderedLine, position.Value);
        }

        if (!updatedAny)
        {
            StopWordAnim();
        }
    }

    private bool UpdateTtmlWordHighlightsForLine(TtmlRenderedLine renderedLine, TimeSpan position)
    {
        var words = renderedLine.Line.Words;
        var opacities = renderedLine.CharOpacities;
        if (words is null || words.Count == 0 || opacities is null)
        {
            return false;
        }

        var inlines = renderedLine.TextBlock.Inlines.ToList();
        if (inlines.Count == 0)
        {
            return false;
        }

        var baseBrush = renderedLine.Line.IsBackground ? GetWordBrush(ActiveLyricBrush, 0.72) : ActiveLyricBrush;
        var totalChars = inlines.Count;
        var runningChars = 0;
        double? fillPos = null;
        var smoothing = 0.32;
        var lastWordEnd = GetTtmlWordEnd(renderedLine.Line, words, words.Count - 1);
        var postWordHoldEnd = lastWordEnd + TimeSpan.FromMilliseconds(180);

        for (var wordIndex = 0; wordIndex < words.Count; wordIndex++)
        {
            var word = words[wordIndex];
            var wordChars = word.Word.Length;
            var hasSpace = wordIndex < words.Count - 1 ? 1 : 0;
            var wordVisualSpan = wordChars + hasSpace;
            var wordEnd = GetTtmlWordEnd(renderedLine.Line, words, wordIndex);

            if (position >= word.Timestamp && position < wordEnd)
            {
                var timeProgress = wordEnd > word.Timestamp
                    ? (position - word.Timestamp).TotalMilliseconds / (wordEnd - word.Timestamp).TotalMilliseconds
                    : 1;
                var visualStart = (double)runningChars / totalChars;
                var visualEnd = (double)(runningChars + wordVisualSpan) / totalChars;
                fillPos = visualStart + Math.Clamp(timeProgress, 0, 1) * (visualEnd - visualStart);

                var msPerChar = Math.Max(1, (wordEnd - word.Timestamp).TotalMilliseconds / Math.Max(1, wordVisualSpan));
                smoothing = Math.Clamp(16.0 / (msPerChar * 0.8), 0.32, 0.95);
                break;
            }

            runningChars += wordVisualSpan;
        }

        if (fillPos is null)
        {
            if (position < words[0].Timestamp)
            {
                fillPos = 0.0;
            }
            else if (position <= postWordHoldEnd)
            {
                fillPos = 1.0;
                smoothing = 0.45;
            }
            else
            {
                return false;
            }
        }

        for (var i = 0; i < totalChars; i++)
        {
            var charPos = (double)i / totalChars;
            var diff = (fillPos.Value - charPos) * totalChars;
            var target = Math.Clamp(0.18 + 0.82 * diff, 0.18, 1.0);
            var current = opacities[i];
            var err = target - current;
            opacities[i] = Math.Abs(err) < 0.003 ? target : current + err * smoothing;

            if (inlines[i] is Run run)
            {
                run.Foreground = GetWordBrush(baseBrush, opacities[i]);
            }
        }

        return true;
    }

    private static TimeSpan GetTtmlWordEnd(LyricLine line, IReadOnlyList<WordInfo> words, int wordIndex)
    {
        var word = words[wordIndex];
        if (word.EndTime is { } endTime && endTime > word.Timestamp)
        {
            return endTime;
        }

        if (wordIndex + 1 < words.Count && words[wordIndex + 1].Timestamp > word.Timestamp)
        {
            return words[wordIndex + 1].Timestamp;
        }

        if (line.EndTime is { } lineEnd && lineEnd > word.Timestamp)
        {
            return lineEnd;
        }

        if (wordIndex > 0)
        {
            var previousGap = word.Timestamp - words[wordIndex - 1].Timestamp;
            if (previousGap > TimeSpan.Zero)
            {
                return word.Timestamp + previousGap;
            }
        }

        return word.Timestamp + TimeSpan.FromMilliseconds(500);
    }

    private static bool LinesOverlap(LyricLine first, LyricLine second)
    {
        var firstEnd = first.EndTime ?? first.Timestamp + TimeSpan.FromSeconds(4);
        var secondEnd = second.EndTime ?? second.Timestamp + TimeSpan.FromSeconds(4);
        return first.Timestamp < secondEnd && second.Timestamp < firstEnd;
    }

    private static LyricLine? GetBestMainLineForSubLine(LyricLine subLine, IReadOnlyList<LyricLine> mainLines)
    {
        return mainLines
            .Where(mainLine => LinesOverlap(mainLine, subLine))
            .OrderBy(mainLine => Math.Abs((mainLine.Timestamp - subLine.Timestamp).TotalMilliseconds))
            .FirstOrDefault();
    }

    private static string GetTtmlLineKey(LyricLine line)
    {
        return $"{line.Timestamp.Ticks}|{line.EndTime?.Ticks}|{line.IsBackground}|{line.Text}";
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

        var effectiveLyrics = GetEffectiveLyrics();
        var effectiveIndex = GetEffectiveCurrentIndex();
        var currentIndex = effectiveLyrics.Count == 0
            ? 0
            : Math.Clamp(effectiveIndex, 0, _lyricTextBlocks.Count - 1);

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

    private void CenterTtmlCurrentLyric(int currentRowIndex, bool animated)
    {
        if (currentRowIndex < 0 || currentRowIndex >= _ttmlLyricRows.Count)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (currentRowIndex >= _ttmlLyricRows.Count)
            {
                return;
            }

            var row = _ttmlLyricRows[currentRowIndex].MainPanel;
            var lineTop = GetStableTtmlRowTop(currentRowIndex);
            var target = Math.Max(0, lineTop - (TtmlLyricsScrollViewer.ViewportHeight / 2) + (row.ActualHeight / 2));

            if (!animated)
            {
                TtmlLyricsScrollViewer.ScrollToVerticalOffset(target);
                return;
            }

            AnimateTtmlScrollTo(target);
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

    private void AnimateTtmlScrollTo(double target)
    {
        var currentOffset = TtmlLyricsScrollViewer.VerticalOffset;
        BeginAnimation(AnimatedTtmlScrollOffsetProperty, null);
        SetCurrentValue(AnimatedTtmlScrollOffsetProperty, currentOffset);

        var animation = new DoubleAnimation
        {
            From = currentOffset,
            To = target,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        BeginAnimation(AnimatedTtmlScrollOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
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

    private double GetStableTtmlRowTop(int targetIndex)
    {
        var top = 0.0;
        for (var i = 0; i < targetIndex && i < _ttmlLyricRows.Count; i++)
        {
            var row = _ttmlLyricRows[i].MainPanel;
            top += row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
        }

        return top + (targetIndex < _ttmlLyricRows.Count ? _ttmlLyricRows[targetIndex].MainPanel.Margin.Top : 0);
    }

    public static readonly DependencyProperty AnimatedScrollOffsetProperty =
        DependencyProperty.Register(
            nameof(AnimatedScrollOffset),
            typeof(double),
            typeof(WindowedWindow),
            new PropertyMetadata(0.0, OnAnimatedScrollOffsetChanged));

    public static readonly DependencyProperty AnimatedTtmlScrollOffsetProperty =
        DependencyProperty.Register(
            nameof(AnimatedTtmlScrollOffset),
            typeof(double),
            typeof(WindowedWindow),
            new PropertyMetadata(0.0, OnAnimatedTtmlScrollOffsetChanged));

    public double AnimatedScrollOffset
    {
        get => (double)GetValue(AnimatedScrollOffsetProperty);
        set => SetValue(AnimatedScrollOffsetProperty, value);
    }

    public double AnimatedTtmlScrollOffset
    {
        get => (double)GetValue(AnimatedTtmlScrollOffsetProperty);
        set => SetValue(AnimatedTtmlScrollOffsetProperty, value);
    }

    private static void OnAnimatedScrollOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WindowedWindow window)
        {
            window.LyricsScrollViewer.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    private static void OnAnimatedTtmlScrollOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WindowedWindow window)
        {
            window.TtmlLyricsScrollViewer.ScrollToVerticalOffset((double)e.NewValue);
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
        var activeViewer = IsTtmlLayoutActive ? TtmlLyricsScrollViewer : LyricsScrollViewer;

        LyricsScrollViewer.BeginAnimation(OpacityProperty, null);
        TtmlLyricsScrollViewer.BeginAnimation(OpacityProperty, null);

        if (!animated)
        {
            activeViewer.Opacity = targetOpacity;
            if (!ReferenceEquals(activeViewer, LyricsScrollViewer))
            {
                LyricsScrollViewer.Opacity = 0;
            }
            else
            {
                TtmlLyricsScrollViewer.Opacity = 0;
            }
            return;
        }

        activeViewer.BeginAnimation(OpacityProperty, new DoubleAnimation
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

        foreach (var row in _ttmlLyricRows)
        {
            row.MainPanel.HorizontalAlignment = horizontalAlignment;
            row.MainTextBlock.TextAlignment = textAlignment;
            row.MainTextBlock.HorizontalAlignment = horizontalAlignment;
            row.MainTextBlock.RenderTransformOrigin = renderTransformOrigin;
            foreach (var subLineBlock in row.SubLineBlocks)
            {
                subLineBlock.TextBlock.TextAlignment = textAlignment;
                subLineBlock.TextBlock.HorizontalAlignment = horizontalAlignment;
                subLineBlock.TextBlock.RenderTransformOrigin = renderTransformOrigin;
            }
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
        ApplyLyricLayoutMode();
        RenderTtmlLyrics();
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
        persistedSettings.IslandPreferredMonitorDeviceName = incomingSettings.IslandPreferredMonitorDeviceName;
        persistedSettings.CustomBarHeight = incomingSettings.CustomBarHeight;
        persistedSettings.WindowedWidth = incomingSettings.WindowedWidth;
        persistedSettings.WindowedHeight = incomingSettings.WindowedHeight;
        persistedSettings.TaskbarMaximumWidth = incomingSettings.TaskbarMaximumWidth;
        persistedSettings.IslandMaximumWidth = incomingSettings.IslandMaximumWidth;
        persistedSettings.IslandScale = incomingSettings.IslandScale;
        persistedSettings.IslandContainerHeight = incomingSettings.IslandContainerHeight;
        persistedSettings.IslandCornerRadius = incomingSettings.IslandCornerRadius;
        persistedSettings.IslandHoverOpacity = incomingSettings.IslandHoverOpacity;
        persistedSettings.IslandAnimationMode = incomingSettings.IslandAnimationMode;
        persistedSettings.IslandAnimationManualSpeed = incomingSettings.IslandAnimationManualSpeed;
        persistedSettings.TaskbarAnimationMode = incomingSettings.TaskbarAnimationMode;
        persistedSettings.TaskbarAnimationManualSpeed = incomingSettings.TaskbarAnimationManualSpeed;
        persistedSettings.LyricAlignment = incomingSettings.LyricAlignment;
        persistedSettings.ShowAlbumArt = incomingSettings.ShowAlbumArt;
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
