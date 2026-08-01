using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Lyrictified.DisplayModes;
using Lyrictified.Interop;
using Lyrictified.Services;
using Lyrictified.Settings;
using Lyrictified.Styling;

namespace Lyrictified;

public partial class SettingsWindow : Window
{
    private static readonly HttpClient GitHubClient = CreateGitHubClient();
    private static readonly TimeSpan TextSettingsChangeDelay = TimeSpan.FromMilliseconds(600);
    private static readonly ContributorItem[] Contributors =
    [
        new ContributorItem("ios7jbpro", "https://github.com/ios7jbpro"),
        new ContributorItem("cotunjr", "https://github.com/cotunjr")
    ];
    private readonly ObservableCollection<DetectedAppRuleItem> _detectedApps = new();
    private readonly DispatcherTimer _textSettingsChangedTimer;
    private bool _isInitializing;
    private WindowAppearanceManager? _appearanceManager;

    public SettingsWindow()
    {
        Logger.Log("SettingsWindow constructed");
        _textSettingsChangedTimer = new DispatcherTimer
        {
            Interval = TextSettingsChangeDelay
        };
        _textSettingsChangedTimer.Tick += TextSettingsChangedTimer_OnTick;
        InitializeComponent();
        DetectedAppsListBox.ItemsSource = _detectedApps;
        ContributorsItemsControl.ItemsSource = Contributors;
        IslandAnimationDefaultTile.Tag = LoadAnimationModeImage("island-animation-default.png");
        IslandAnimationSlideInTile.Tag = LoadAnimationModeImage("island-animation-slide-in.png");
        IslandAnimationSlideInManualTile.Tag = LoadAnimationModeImage("island-animation-slide-in-manual.png");
        IslandAnimationWordFadeTile.Tag = LoadAnimationModeImage("island-animation-fade-in.png");
        IslandAnimationFlashInTile.Tag = LoadAnimationModeImage("island-animation-flash-in.png");
        _ = LoadContributorAvatarsAsync();
#if DEBUG
        DebugTabItem.Visibility = Visibility.Visible;
#else
        DebugTabItem.Visibility = Visibility.Collapsed;
#endif
        SourceInitialized += OnSourceInitialized;

        var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
#if DEBUG
        var buildSuffix = "-debug";
#else
        var buildSuffix = "-release";
#endif
        AppVersionTextBlock.Text = appVersion is not null ? $"v{appVersion.ToString(3)}{buildSuffix}" : "";
    }

    public event EventHandler<AppSettings>? SettingsChanged;
    public event EventHandler? ForceLyricsRefreshRequested;
    public event EventHandler? DebugForceNoLyricsRequested;
    public event EventHandler? DebugForceSimulateLyricsRequested;

    private static HttpClient CreateGitHubClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Lyrictified");
        return client;
    }

    private static BitmapImage LoadAnimationModeImage(string imageName)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", imageName), UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static async Task LoadContributorAvatarsAsync()
    {
        Logger.Log("Loading contributor avatars");
        foreach (var contributor in Contributors)
        {
            try
            {
                var profile = await GitHubClient.GetFromJsonAsync<GitHubProfile>($"users/{contributor.Name}");
                var contributors = await GitHubClient.GetFromJsonAsync<GitHubContributor[]>($"repos/ios7jbpro/Lyrictified/contributors");
                contributor.CommitCount = contributors?.FirstOrDefault(item => string.Equals(item.Login, contributor.Name, StringComparison.OrdinalIgnoreCase))?.Contributions ?? 0;
                Logger.Log($"GitHub profile loaded: {profile?.AvatarUrl ?? "<none>"}");
                if (!string.IsNullOrWhiteSpace(profile?.AvatarUrl))
                {
                    var imageBytes = await GitHubClient.GetByteArrayAsync(profile.AvatarUrl);
                    using var stream = new MemoryStream(imageBytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    contributor.AvatarImage = bitmap;
                    Logger.Log($"Contributor avatar decoded: {imageBytes.Length} bytes");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load GitHub avatar for {contributor.Name}: {ex.Message}");
            }
        }
    }

    private sealed record GitHubProfile([property: JsonPropertyName("avatar_url")] string AvatarUrl);
    private sealed record GitHubContributor([property: JsonPropertyName("login")] string Login, [property: JsonPropertyName("contributions")] int Contributions);

    private void OpenContributorGitHubButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string url })
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    public void UpdateLastSearchInfo(string info)
    {
        LastSearchInfoTextBox.Text = string.IsNullOrWhiteSpace(info) ? "No search yet" : info;
    }

    public void UpdateVmDetectionInfo()
    {
        var result = VmDetectionService.GetVmDetectionResult();
        VmDetectionInfoTextBox.Text = VmDetectionService.FormatResult(result);
    }

    private AppSettings? _lastLoadedSettings;

    public void LoadSettings(AppSettings settings, IReadOnlyList<MonitorOption> monitors)
    {
        _isInitializing = true;
        _lastLoadedSettings = settings;
        try
        {
            var appBarMonitor = settings.AppBarPreferredMonitorDeviceName ?? settings.PreferredMonitorDeviceName;
            var taskbarMonitor = settings.TaskbarPreferredMonitorDeviceName ?? settings.PreferredMonitorDeviceName;
            var islandMonitor = settings.IslandPreferredMonitorDeviceName ?? settings.PreferredMonitorDeviceName;

            DisplayModeComboBox.SelectedIndex = settings.DisplayMode switch
            {
                DisplayMode.Windowed => 1,
                DisplayMode.Taskbar => 2,
                DisplayMode.Island => 3,
                _ => 0
            };
            HideModeComboBox.SelectedIndex = settings.HideMode switch
            {
                HideMode.Blackout => 1,
                HideMode.Hide => 2,
                _ => 0
            };

            ShowNextLineComboBox.SelectedIndex = settings.ShowNextLine ? 1 : 0;
            CustomHeightTextBox.Text = settings.CustomBarHeight?.ToString() ?? string.Empty;
            LyricAlignmentComboBox.SelectedIndex = settings.LyricAlignment switch
            {
                LyricAlignment.Left => 0,
                LyricAlignment.Right => 2,
                _ => 1
            };
            ShowAlbumArtComboBox.SelectedIndex = settings.ShowAlbumArt ? 0 : 1;
            AppBarShowProgressBarComboBox.SelectedIndex = settings.AppBarShowProgressBar ? 0 : 1;
            AppBarAdaptModeComboBox.SelectedIndex = settings.AppBarAdaptMode switch
            {
                AppBarAdaptMode.Adapt => 1,
                AppBarAdaptMode.Exact => 2,
                _ => 0
            };
            AppBarAdaptThresholdSlider.Value = Math.Clamp(settings.AppBarAdaptThreshold, 0, 255);
            AppBarAdaptThresholdValueTextBlock.Text = AppBarAdaptThresholdSlider.Value.ToString("F0");
            WordByWordComboBox.SelectedIndex = settings.WordByWordMode ? 1 : 0;
            DisplayTtmlLyricsComboBox.SelectedIndex = settings.DisplayTtmlLyrics ? 1 : 0;
            MaxCacheSizeTextBox.Text = settings.MaxCacheSize.ToString();
            AutostartWithWindowsCheckBox.IsChecked = settings.AutostartWithWindows;
            SuppressVmWarningCheckBox.IsChecked = settings.SuppressVmWarning;
            SuppressVmWarningCard.Visibility = VmDetectionService.IsRunningInVirtualMachine() ? Visibility.Visible : Visibility.Collapsed;

            WindowedShowNextLineComboBox.SelectedIndex = settings.WindowedShowNextLine ? 1 : 0;
            WindowedLyricAlignmentComboBox.SelectedIndex = settings.WindowedLyricAlignment switch
            {
                LyricAlignment.Left => 0,
                LyricAlignment.Right => 2,
                _ => 1
            };
            WindowedShowAlbumArtComboBox.SelectedIndex = settings.WindowedShowAlbumArt ? 0 : 1;
            WindowedWordByWordComboBox.SelectedIndex = settings.WindowedWordByWordMode ? 1 : 0;
            WindowedDisplayTtmlLyricsComboBox.SelectedIndex = settings.WindowedDisplayTtmlLyrics ? 1 : 0;

            TaskbarMaximumWidthTextBox.Text = settings.TaskbarMaximumWidth?.ToString() ?? string.Empty;
            TaskbarHeightTextBox.Text = settings.TaskbarHeight?.ToString() ?? string.Empty;
            TaskbarAnimationModeComboBox.SelectedIndex = settings.TaskbarAnimationMode switch
            {
                IslandAnimationMode.SlideIn => 1,
                IslandAnimationMode.SlideInManual => 2,
                _ => 0
            };
            TaskbarAnimationManualSpeedSlider.Value = Math.Clamp(settings.TaskbarAnimationManualSpeed, 0.5, 2.5);
            TaskbarAnimationManualSpeedValueTextBlock.Text = $"{TaskbarAnimationManualSpeedSlider.Value:F1}x";
            UpdateTaskbarAnimationManualSpeedVisibility();
            IslandMaximumWidthTextBox.Text = settings.IslandMaximumWidth?.ToString() ?? string.Empty;
            IslandScaleTextBox.Text = (GetEffectiveIslandScale(settings.IslandScale) * 100).ToString("F0");
            IslandContainerHeightTextBox.Text = settings.IslandContainerHeight?.ToString() ?? string.Empty;
            IslandCornerRadiusTextBox.Text = GetEffectiveIslandCornerRadius(settings.IslandCornerRadius).ToString("F0");
            IslandHideInFullscreenCheckBox.IsChecked = settings.IslandHideInFullscreen;
            IslandTimeoutTextBox.Text = settings.IslandTimeout.ToString();
            IslandHoverOpacitySlider.Value = Math.Clamp(GetEffectiveIslandHoverOpacity(settings.IslandHoverOpacity) * 100, 0, 100);
            IslandHoverOpacityValueTextBlock.Text = $"{(int)IslandHoverOpacitySlider.Value}%";
            SetIslandAnimationModeSelection(settings.IslandAnimationMode);
            IslandAnimationManualSpeedSlider.Value = Math.Clamp(settings.IslandAnimationManualSpeed, 0.5, 2.5);
            IslandAnimationManualSpeedValueTextBlock.Text = $"{IslandAnimationManualSpeedSlider.Value:F1}x";
            UpdateIslandAnimationManualSpeedVisibility();
            DebugForceLyricsSourceComboBox.SelectedIndex = settings.DebugForceLyricsSource switch
            {
                "Local" => 1,
                "LrcLib" => 2,
                "Synced" => 3,
                _ => 0
            };
            LoadDetectedApps(settings);

            AppBarMonitorComboBox.ItemsSource = monitors;
            AppBarMonitorComboBox.SelectedValue = appBarMonitor;

            if (AppBarMonitorComboBox.SelectedIndex < 0 && monitors.Count > 0)
            {
                AppBarMonitorComboBox.SelectedIndex = 0;
            }

            TaskbarMonitorComboBox.ItemsSource = monitors;
            TaskbarMonitorComboBox.SelectedValue = taskbarMonitor;

            if (TaskbarMonitorComboBox.SelectedIndex < 0 && monitors.Count > 0)
            {
                TaskbarMonitorComboBox.SelectedIndex = 0;
            }

            IslandMonitorComboBox.ItemsSource = monitors;
            IslandMonitorComboBox.SelectedValue = islandMonitor;

            if (IslandMonitorComboBox.SelectedIndex < 0 && monitors.Count > 0)
            {
                IslandMonitorComboBox.SelectedIndex = 0;
            }

            UpdateAppBarControls();
            UpdateBarHeightHint();
            UpdateTaskbarMaximumWidthHint();
            UpdateTaskbarHeightHint();
            UpdateIslandMaximumWidthHint();
            UpdateIslandScaleHint();
            UpdateIslandContainerHeightHint();
            UpdateIslandCornerRadiusHint();
            UpdateVmDetectionInfo();
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void DisplayModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAppBarControls();
        UpdateBarHeightHint();
        RaiseSettingsChanged();
    }

    private void HideModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void ShowNextLineComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBarHeightHint();
        RaiseSettingsChanged();
    }

    private void AppBarMonitorComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void TaskbarMonitorComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void IslandMonitorComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void WindowedShowNextLineComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void WindowedLyricAlignmentComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void WindowedShowAlbumArtComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void WindowedWordByWordComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void WindowedDisplayTtmlLyricsComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (WindowedDisplayTtmlLyricsComboBox.SelectedIndex == 1)
        {
            var result = System.Windows.MessageBox.Show(
                "TTML support in Windowed mode is highly experimental and buggy.\n\n" +
                "By enabling this feature, you agree not to report bugs related to it.\n\n" +
                "Do you want to continue and enable it?",
                "Experimental Feature Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                _isInitializing = true;
                WindowedDisplayTtmlLyricsComboBox.SelectedIndex = 0;
                _isInitializing = false;
                return;
            }
        }

        RaiseSettingsChanged();
    }

    private void CustomHeightTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateBarHeightHint();
        RaiseTextSettingsChanged();
    }

    private void LyricAlignmentComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void ShowAlbumArtComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void AppBarShowProgressBarComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void AppBarAdaptModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAdaptThresholdVisibility();
        RaiseSettingsChanged();
    }

    private void AppBarAdaptThresholdSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AppBarAdaptThresholdValueTextBlock is not null)
        {
            AppBarAdaptThresholdValueTextBlock.Text = e.NewValue.ToString("F0");
        }

        RaiseTextSettingsChanged();
    }

    private void WordByWordComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void DisplayTtmlLyricsComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void TaskbarMaximumWidthTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTaskbarMaximumWidthHint();
        RaiseTextSettingsChanged();
    }

    private void TaskbarHeightTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTaskbarHeightHint();
        RaiseTextSettingsChanged();
    }

    private void IslandMaximumWidthTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateIslandMaximumWidthHint();
        RaiseTextSettingsChanged();
    }

    private void IslandScaleTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateIslandScaleHint();
        RaiseTextSettingsChanged();
    }

    private void IslandContainerHeightTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateIslandContainerHeightHint();
        RaiseTextSettingsChanged();
    }

    private void IslandCornerRadiusTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateIslandCornerRadiusHint();
        RaiseTextSettingsChanged();
    }

    private void IslandHideInFullscreenCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void IslandTimeoutTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        RaiseTextSettingsChanged();
    }

    private void IslandAnimationTile_OnChecked(object sender, RoutedEventArgs e)
    {
        UpdateIslandAnimationManualSpeedVisibility();
        RaiseSettingsChanged();
    }

    private void SetIslandAnimationModeSelection(IslandAnimationMode mode)
    {
        IslandAnimationDefaultTile.IsChecked = mode == IslandAnimationMode.Default;
        IslandAnimationSlideInTile.IsChecked = mode == IslandAnimationMode.SlideIn;
        IslandAnimationSlideInManualTile.IsChecked = mode == IslandAnimationMode.SlideInManual;
        IslandAnimationWordFadeTile.IsChecked = mode == IslandAnimationMode.WordFade;
        IslandAnimationFlashInTile.IsChecked = mode == IslandAnimationMode.FlashIn;
    }

    private IslandAnimationMode GetSelectedIslandAnimationMode()
    {
        if (IslandAnimationSlideInTile.IsChecked == true)
        {
            return IslandAnimationMode.SlideIn;
        }

        if (IslandAnimationSlideInManualTile.IsChecked == true)
        {
            return IslandAnimationMode.SlideInManual;
        }

        if (IslandAnimationWordFadeTile.IsChecked == true)
        {
            return IslandAnimationMode.WordFade;
        }

        if (IslandAnimationFlashInTile.IsChecked == true)
        {
            return IslandAnimationMode.FlashIn;
        }

        return IslandAnimationMode.Default;
    }

    private void UpdateIslandAnimationManualSpeedVisibility()
    {
        if (IslandAnimationManualSpeedPanel is not null)
        {
            IslandAnimationManualSpeedPanel.Visibility =
                IslandAnimationSlideInManualTile.IsChecked == true
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private void IslandAnimationManualSpeedSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IslandAnimationManualSpeedValueTextBlock is not null)
        {
            IslandAnimationManualSpeedValueTextBlock.Text = $"{e.NewValue:F1}x";
        }

        RaiseTextSettingsChanged();
    }

    private void IslandHoverOpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IslandHoverOpacityValueTextBlock is not null)
        {
            IslandHoverOpacityValueTextBlock.Text = $"{(int)e.NewValue}%";
        }

        RaiseTextSettingsChanged();
    }

    private void TaskbarAnimationModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTaskbarAnimationManualSpeedVisibility();
        RaiseSettingsChanged();
    }

    private void UpdateTaskbarAnimationManualSpeedVisibility()
    {
        if (TaskbarAnimationManualSpeedPanel is not null)
        {
            TaskbarAnimationManualSpeedPanel.Visibility =
                TaskbarAnimationModeComboBox.SelectedIndex == 2
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private void TaskbarAnimationManualSpeedSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TaskbarAnimationManualSpeedValueTextBlock is not null)
        {
            TaskbarAnimationManualSpeedValueTextBlock.Text = $"{e.NewValue:F1}x";
        }

        RaiseTextSettingsChanged();
    }

    private void AutostartWithWindowsCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void SuppressVmWarningCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void RaiseSettingsChanged()
    {
        if (_isInitializing)
        {
            return;
        }

        var settings = new AppSettings
        {
            DisplayMode = GetSelectedDisplayMode(),
            HideMode = (HideModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "Blackout" => HideMode.Blackout,
                "Hide" => HideMode.Hide,
                _ => HideMode.Nothing
            },
            ShowNextLine = ShowNextLineComboBox.SelectedIndex == 1,
            PreferredMonitorDeviceName = null,
            AppBarPreferredMonitorDeviceName = AppBarMonitorComboBox.SelectedValue as string,
            TaskbarPreferredMonitorDeviceName = TaskbarMonitorComboBox.SelectedValue as string,
            IslandPreferredMonitorDeviceName = IslandMonitorComboBox.SelectedValue as string,
            CustomBarHeight = ParseCustomHeight(),
            TaskbarMaximumWidth = ParseTaskbarMaximumWidth(),
            TaskbarHeight = ParseTaskbarHeight(),
            TaskbarAnimationMode = TaskbarAnimationModeComboBox.SelectedIndex switch
            {
                1 => IslandAnimationMode.SlideIn,
                2 => IslandAnimationMode.SlideInManual,
                _ => IslandAnimationMode.Default
            },
            TaskbarAnimationManualSpeed = TaskbarAnimationManualSpeedSlider?.Value ?? 1.0,
            IslandMaximumWidth = ParseIslandMaximumWidth(),
            IslandScale = ParseIslandScale(),
            IslandContainerHeight = ParseIslandContainerHeight(),
            IslandCornerRadius = ParseIslandCornerRadius(),
            IslandHideInFullscreen = IslandHideInFullscreenCheckBox.IsChecked == true,
            IslandTimeout = ParseIslandTimeout(),
            IslandHoverOpacity = GetEffectiveIslandHoverOpacity(IslandHoverOpacitySlider.Value / 100.0),
            IslandAnimationMode = GetSelectedIslandAnimationMode(),
            IslandAnimationManualSpeed = IslandAnimationManualSpeedSlider?.Value ?? 1.0,
            LyricAlignment = (LyricAlignmentComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "Left" => LyricAlignment.Left,
                "Right" => LyricAlignment.Right,
                _ => LyricAlignment.Center
            },
            ShowAlbumArt = ShowAlbumArtComboBox.SelectedIndex == 0,
            AppBarShowProgressBar = AppBarShowProgressBarComboBox.SelectedIndex == 0,
            AppBarAdaptMode = AppBarAdaptModeComboBox.SelectedIndex switch
            {
                1 => AppBarAdaptMode.Adapt,
                2 => AppBarAdaptMode.Exact,
                _ => AppBarAdaptMode.Disabled
            },
            AppBarAdaptThreshold = (int)AppBarAdaptThresholdSlider.Value,
            WordByWordMode = WordByWordComboBox.SelectedIndex == 1,
            DisplayTtmlLyrics = DisplayTtmlLyricsComboBox.SelectedIndex == 1,
            AutostartWithWindows = AutostartWithWindowsCheckBox.IsChecked == true,
            SuppressVmWarning = SuppressVmWarningCheckBox.IsChecked == true,
            WindowedShowNextLine = WindowedShowNextLineComboBox.SelectedIndex == 1,
            WindowedLyricAlignment = (WindowedLyricAlignmentComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "Left" => LyricAlignment.Left,
                "Right" => LyricAlignment.Right,
                _ => LyricAlignment.Center
            },
            WindowedShowAlbumArt = WindowedShowAlbumArtComboBox.SelectedIndex == 0,
            WindowedWordByWordMode = WindowedWordByWordComboBox.SelectedIndex == 1,
            WindowedDisplayTtmlLyrics = WindowedDisplayTtmlLyricsComboBox.SelectedIndex == 1,
            MaxCacheSize = ParseMaxCacheSize(),
            DebugForceLyricsSource = (DebugForceLyricsSourceComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "Local" => "Local",
                "LrcLib" => "LrcLib",
                "Synced" => "Synced",
                _ => null
            },
            WindowedWidth = _lastLoadedSettings?.WindowedWidth,
            WindowedHeight = _lastLoadedSettings?.WindowedHeight,
            DetectedMediaApps = _detectedApps
                .Select(app => new DetectedMediaApp(app.AppId, app.DisplayName))
                .ToList(),
            IgnoredMediaAppIds = _detectedApps
                .Where(app => app.IsIgnored)
                .Select(app => app.AppId)
                .ToList()
        };

        SettingsChanged?.Invoke(this, settings);
    }

    private void RaiseTextSettingsChanged()
    {
        if (_isInitializing)
        {
            return;
        }

        _textSettingsChangedTimer.Stop();
        _textSettingsChangedTimer.Start();
    }

    private void TextSettingsChangedTimer_OnTick(object? sender, EventArgs e)
    {
        _textSettingsChangedTimer.Stop();
        RaiseSettingsChanged();
    }

    private int? ParseCustomHeight()
    {
        if (string.IsNullOrWhiteSpace(CustomHeightTextBox.Text))
        {
            return null;
        }

        return int.TryParse(CustomHeightTextBox.Text, out var parsedHeight) ? parsedHeight : null;
    }

    private int? ParseTaskbarMaximumWidth()
    {
        if (string.IsNullOrWhiteSpace(TaskbarMaximumWidthTextBox.Text))
        {
            return null;
        }

        return int.TryParse(TaskbarMaximumWidthTextBox.Text, out var parsedWidth) ? parsedWidth : null;
    }

    private int? ParseTaskbarHeight()
    {
        if (string.IsNullOrWhiteSpace(TaskbarHeightTextBox.Text))
        {
            return null;
        }

        return int.TryParse(TaskbarHeightTextBox.Text, out var parsedHeight) ? parsedHeight : null;
    }

    private int? ParseIslandMaximumWidth()
    {
        if (string.IsNullOrWhiteSpace(IslandMaximumWidthTextBox.Text))
        {
            return null;
        }

        return int.TryParse(IslandMaximumWidthTextBox.Text, out var parsedWidth) ? parsedWidth : null;
    }

    private double ParseIslandScale()
    {
        if (!double.TryParse(IslandScaleTextBox.Text, out var parsedPercent))
        {
            return 1.0;
        }

        return GetEffectiveIslandScale(parsedPercent / 100.0);
    }

    private int? ParseIslandContainerHeight()
    {
        if (string.IsNullOrWhiteSpace(IslandContainerHeightTextBox.Text))
        {
            return null;
        }

        return int.TryParse(IslandContainerHeightTextBox.Text, out var parsedHeight)
            ? IslandDisplayMode.GetEffectiveContainerHeight(parsedHeight)
            : null;
    }

    private double ParseIslandCornerRadius()
    {
        if (!double.TryParse(IslandCornerRadiusTextBox.Text, out var parsedRadius))
        {
            return 14;
        }

        return GetEffectiveIslandCornerRadius(parsedRadius);
    }

    private static double GetEffectiveIslandScale(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            return 1.0;
        }

        return Math.Clamp(scale, 0.5, 2.0);
    }

    private static double GetEffectiveIslandCornerRadius(double radius)
    {
        if (double.IsNaN(radius) || double.IsInfinity(radius) || radius < 0)
        {
            return 14;
        }

        return Math.Clamp(radius, 0, 40);
    }

    private static double GetEffectiveIslandHoverOpacity(double opacity)
    {
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
        {
            return 0.16;
        }

        return Math.Clamp(opacity, 0, 1);
    }

    private int ParseIslandTimeout()
    {
        if (int.TryParse(IslandTimeoutTextBox.Text, out var parsed) && parsed >= 0)
        {
            return parsed;
        }

        return 10;
    }

    private int ParseMaxCacheSize()
    {
        if (int.TryParse(MaxCacheSizeTextBox.Text, out var parsed) && parsed >= 0)
        {
            return parsed;
        }

        return 25;
    }

    private void MaxCacheSizeTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }

    private void MaxCacheSizeTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            var text = (string)e.DataObject.GetData(typeof(string));
            if (!int.TryParse(text, out _))
            {
                e.CancelCommand();
            }
        }
        else
        {
            e.CancelCommand();
        }
    }

    private void MaxCacheSizeTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitMaxCacheSize();
        e.Handled = true;
    }

    private void MaxCacheSizeTextBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        CommitMaxCacheSize();
    }

    private void CommitMaxCacheSize()
    {
        if (string.IsNullOrWhiteSpace(MaxCacheSizeTextBox.Text))
        {
            MaxCacheSizeTextBox.Text = "0";
        }

        RaiseSettingsChanged();
    }

    private void UpdateBarHeightHint()
    {
        if (GetSelectedDisplayMode() == DisplayMode.Taskbar)
        {
            BarHeightHintTextBlock.Text = $"Taskbar mode height can be set in the Taskbar tab ({TaskbarDisplayMode.WindowHeight}px default).";
            return;
        }

        if (GetSelectedDisplayMode() == DisplayMode.Island)
        {
            BarHeightHintTextBlock.Text = $"Island mode uses a fixed {IslandDisplayMode.WindowHeight}px height.";
            return;
        }

        var automaticHeight = AppBarDisplayMode.GetAutomaticHeight(ShowNextLineComboBox.SelectedIndex == 1);
        if (int.TryParse(CustomHeightTextBox.Text, out var parsedHeight))
        {
            BarHeightHintTextBlock.Text = $"Auto is {automaticHeight}px. Custom height minimum is {AppBarDisplayMode.MinimumCustomHeight}px.";
            return;
        }

        BarHeightHintTextBlock.Text = $"Leave empty to use the automatic {automaticHeight}px height for this mode.";
    }

    private void UpdateTaskbarMaximumWidthHint()
    {
        if (int.TryParse(TaskbarMaximumWidthTextBox.Text, out var parsedWidth))
        {
            var effectiveWidth = TaskbarDisplayMode.GetEffectiveMaximumWidth(parsedWidth);
            TaskbarMaximumWidthHintTextBlock.Text = $"Taskbar width is clamped to at least {effectiveWidth}px.";
            return;
        }

        TaskbarMaximumWidthHintTextBlock.Text = $"Leave empty to use the default {TaskbarDisplayMode.DefaultMaximumWidth}px taskbar width cap.";
    }

    private void UpdateTaskbarHeightHint()
    {
        if (int.TryParse(TaskbarHeightTextBox.Text, out var parsedHeight))
        {
            var effectiveHeight = TaskbarDisplayMode.GetEffectiveHeight(parsedHeight);
            TaskbarHeightHintTextBlock.Text = $"Taskbar height is clamped to at least {effectiveHeight}px.";
            return;
        }

        TaskbarHeightHintTextBlock.Text = $"Leave empty to use the default {TaskbarDisplayMode.WindowHeight}px taskbar height.";
    }

    private void UpdateIslandMaximumWidthHint()
    {
        if (int.TryParse(IslandMaximumWidthTextBox.Text, out var parsedWidth))
        {
            var effectiveWidth = IslandDisplayMode.GetEffectiveMaximumWidth(parsedWidth);
            IslandMaximumWidthHintTextBlock.Text = $"Island transparent container width cap: {effectiveWidth}px.";
            return;
        }

        IslandMaximumWidthHintTextBlock.Text = $"Leave empty to use the default {IslandDisplayMode.DefaultMaximumWidth}px island width cap.";
    }

    private void UpdateIslandScaleHint()
    {
        if (double.TryParse(IslandScaleTextBox.Text, out var parsedPercent))
        {
            var effectivePercent = GetEffectiveIslandScale(parsedPercent / 100.0) * 100;
            IslandScaleHintTextBlock.Text = $"Effective Island scale: {effectivePercent:F0}%.";
            return;
        }

        IslandScaleHintTextBlock.Text = "Allowed range: 50% to 200%.";
    }

    private void UpdateIslandContainerHeightHint()
    {
        if (int.TryParse(IslandContainerHeightTextBox.Text, out var parsedHeight))
        {
            var effectiveHeight = IslandDisplayMode.GetEffectiveContainerHeight(parsedHeight);
            IslandContainerHeightHintTextBlock.Text = $"Transparent container height is clamped to {effectiveHeight}px.";
            return;
        }

        IslandContainerHeightHintTextBlock.Text = "Leave empty to use automatic height.";
    }

    private void UpdateIslandCornerRadiusHint()
    {
        if (double.TryParse(IslandCornerRadiusTextBox.Text, out var parsedRadius))
        {
            var effectiveRadius = GetEffectiveIslandCornerRadius(parsedRadius);
            IslandCornerRadiusHintTextBlock.Text = $"Effective background corner radius: {effectiveRadius:F0}px.";
            return;
        }

        IslandCornerRadiusHintTextBlock.Text = "Allowed range: 0px to 40px.";
    }

    private void UpdateAppBarControls()
    {
        var isAppBarMode = GetSelectedDisplayMode() == DisplayMode.AppBar;
        HideModeComboBox.IsEnabled = isAppBarMode;
        ShowNextLineComboBox.IsEnabled = isAppBarMode;
        CustomHeightTextBox.IsEnabled = isAppBarMode;
        AppBarShowProgressBarComboBox.IsEnabled = isAppBarMode;
        AppBarAdaptModeComboBox.IsEnabled = isAppBarMode;
        UpdateAdaptThresholdVisibility();
    }

    private void UpdateAdaptThresholdVisibility()
    {
        if (AppBarAdaptThresholdGrid is null || AppBarAdaptModeComboBox is null)
        {
            return;
        }

        var visible = AppBarAdaptModeComboBox.SelectedIndex == 1;
        AppBarAdaptThresholdGrid.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private DisplayMode GetSelectedDisplayMode()
    {
        return (DisplayModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Windowed" => DisplayMode.Windowed,
            "Taskbar" => DisplayMode.Taskbar,
            "Island" => DisplayMode.Island,
            _ => DisplayMode.AppBar
        };
    }

    private void LoadDetectedApps(AppSettings settings)
    {
        _detectedApps.Clear();

        var ignoredIds = new HashSet<string>(settings.IgnoredMediaAppIds, StringComparer.OrdinalIgnoreCase);
        foreach (var detectedApp in settings.DetectedMediaApps
                     .Where(app => !string.IsNullOrWhiteSpace(app.AppId))
                     .OrderBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(app => app.AppId, StringComparer.OrdinalIgnoreCase))
        {
            _detectedApps.Add(new DetectedAppRuleItem(
                detectedApp.AppId,
                string.IsNullOrWhiteSpace(detectedApp.DisplayName) ? detectedApp.AppId : detectedApp.DisplayName,
                ignoredIds.Contains(detectedApp.AppId)));
        }

        DetectedAppsEmptyStateTextBlock.Visibility = _detectedApps.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetectedAppsListBox.Visibility = _detectedApps.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void DetectedAppIgnoreCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void ForceResearchButton_OnClick(object sender, RoutedEventArgs e)
    {
        ForceLyricsRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshVmDetectionButton_OnClick(object sender, RoutedEventArgs e)
    {
        UpdateVmDetectionInfo();
    }

    private void ForceNoLyricsButton_OnClick(object sender, RoutedEventArgs e)
    {
        DebugForceNoLyricsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ForceSimulateLyricsButton_OnClick(object sender, RoutedEventArgs e)
    {
        DebugForceSimulateLyricsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DebugForceLyricsSourceComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _appearanceManager = new WindowAppearanceManager(this);
        WindowMaximizeBounds.Attach(this);
        ApplyAppearance();
        ApplyNativeWindowFrame();
    }

    private void ApplyAppearance()
    {
        if (_appearanceManager is null)
        {
            return;
        }

        var palette = _appearanceManager.Apply();
        Background = palette.WindowBackground;
        SurfaceBorder.Background = palette.SurfaceBackground;
        SurfaceBorder.BorderBrush = palette.SurfaceBorder;
        SurfaceBorder.BorderThickness = new Thickness(0, 0, 0, 1);
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

        var cornerPreference = 2;
        _ = DwmSetWindowAttribute(hwnd, 33, ref cornerPreference, Marshal.SizeOf<int>());

        var borderColor = 0x006E6254;
        _ = DwmSetWindowAttribute(hwnd, 34, ref borderColor, Marshal.SizeOf<int>());
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
        Close();
    }

    private void Window_OnStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeButtonContent();
    }

    private void UpdateMaximizeButtonContent()
    {
        if (MaximizeButtonImage is not null)
        {
            MaximizeButtonImage.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", WindowState == WindowState.Maximized ? "restore.png" : "maximize.png"), UriKind.Absolute));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private sealed class DetectedAppRuleItem : INotifyPropertyChanged
    {
        private bool _isIgnored;

        public DetectedAppRuleItem(string appId, string displayName, bool isIgnored)
        {
            AppId = appId;
            DisplayName = displayName;
            _isIgnored = isIgnored;
        }

        public string AppId { get; }

        public string DisplayName { get; }

        public bool IsIgnored
        {
            get => _isIgnored;
            set
            {
                if (_isIgnored == value)
                {
                    return;
                }

                _isIgnored = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIgnored)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class ContributorItem : INotifyPropertyChanged
    {
        public ContributorItem(string name, string githubUrl)
        {
            Name = name;
            GitHubUrl = githubUrl;
        }

        public string Name { get; }

        public string GitHubUrl { get; }

        public int CommitCount
        {
            get;
            set
            {
                if (value == field) return;
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CommitSummary)));
            }
        }

        public string CommitSummary => $"{CommitCount:N0} commits pushed to Lyrictified";

        public ImageSource? AvatarImage
        {
            get;
            set
            {
                if (value == field) return;
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarImage)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    }
}
