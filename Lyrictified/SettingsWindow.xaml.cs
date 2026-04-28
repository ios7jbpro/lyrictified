using System.Windows;
using System.Windows.Controls;
using Lyrictified.DisplayModes;
using Lyrictified.Settings;

namespace Lyrictified;

public partial class SettingsWindow : Window
{
    private bool _isInitializing;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public event EventHandler<AppSettings>? SettingsChanged;

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
            TaskbarMaximumWidthTextBox.Text = settings.TaskbarMaximumWidth?.ToString() ?? string.Empty;

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
            TaskbarMaximumWidth = ParseTaskbarMaximumWidth()
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
            var effectiveHeight = Math.Max(parsedHeight, automaticHeight);
            BarHeightHintTextBlock.Text = $"Auto is {automaticHeight}px for this mode. Custom height is clamped to at least {effectiveHeight}px.";
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
}
