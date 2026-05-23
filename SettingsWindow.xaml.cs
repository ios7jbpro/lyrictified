using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Lyrictified.DisplayModes;
using Lyrictified.Settings;

namespace Lyrictified;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<DetectedAppRuleItem> _detectedApps = new();
    private bool _isInitializing;

    public SettingsWindow()
    {
        InitializeComponent();
        DetectedAppsListBox.ItemsSource = _detectedApps;
    }

    public event EventHandler<AppSettings>? SettingsChanged;

    public void UpdateLastSearchInfo(string info)
    {
        if (_isInitializing)
        {
            return;
        }

        LastSearchInfoTextBox.Text = string.IsNullOrWhiteSpace(info) ? "No search yet" : info;
    }

    public void LoadSettings(AppSettings settings, IReadOnlyList<MonitorOption> monitors)
    {
        _isInitializing = true;
        try
        {
            var appBarMonitor = settings.AppBarPreferredMonitorDeviceName ?? settings.PreferredMonitorDeviceName;
            var taskbarMonitor = settings.TaskbarPreferredMonitorDeviceName ?? settings.PreferredMonitorDeviceName;

            DisplayModeComboBox.SelectedIndex = settings.DisplayMode == DisplayMode.Taskbar ? 1 : 0;
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
            WordByWordComboBox.SelectedIndex = settings.WordByWordMode ? 1 : 0;
            MaxCacheSizeTextBox.Text = settings.MaxCacheSize.ToString();
            TaskbarMaximumWidthTextBox.Text = settings.TaskbarMaximumWidth?.ToString() ?? string.Empty;
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

            UpdateAppBarControls();
            UpdateBarHeightHint();
            UpdateTaskbarMaximumWidthHint();
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

    private void CustomHeightTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateBarHeightHint();
        RaiseSettingsChanged();
    }

    private void LyricAlignmentComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void ShowAlbumArtComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void WordByWordComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void TaskbarMaximumWidthTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTaskbarMaximumWidthHint();
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
            CustomBarHeight = ParseCustomHeight(),
            TaskbarMaximumWidth = ParseTaskbarMaximumWidth(),
            LyricAlignment = (LyricAlignmentComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "Left" => LyricAlignment.Left,
                "Right" => LyricAlignment.Right,
                _ => LyricAlignment.Center
            },
            ShowAlbumArt = ShowAlbumArtComboBox.SelectedIndex == 0,
            WordByWordMode = WordByWordComboBox.SelectedIndex == 1,
            MaxCacheSize = ParseMaxCacheSize(),
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

    private void MaxCacheSizeTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        RaiseSettingsChanged();
    }

    private void UpdateBarHeightHint()
    {
        if (GetSelectedDisplayMode() == DisplayMode.Taskbar)
        {
            BarHeightHintTextBlock.Text = $"Taskbar mode uses a fixed {TaskbarDisplayMode.WindowHeight}px height.";
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

    private void UpdateAppBarControls()
    {
        var isAppBarMode = GetSelectedDisplayMode() == DisplayMode.AppBar;
        HideModeComboBox.IsEnabled = isAppBarMode;
        ShowNextLineComboBox.IsEnabled = isAppBarMode;
        CustomHeightTextBox.IsEnabled = isAppBarMode;
    }

    private DisplayMode GetSelectedDisplayMode()
    {
        return (DisplayModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Taskbar" => DisplayMode.Taskbar,
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
}
