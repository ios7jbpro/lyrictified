using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lyrictified.Services;
using Lyrictified.Styling;
using Lyrictified.ViewModels;
using WpfApplication = System.Windows.Application;

namespace Lyrictified;

public partial class LrcEditorWindow : Window
{
    private readonly MainViewModel? _viewModel;
    private readonly Action<string>? _onSave;
    private readonly Action? _onOpenWindowed;
    private WindowAppearanceManager? _appearanceManager;

    public LrcEditorWindow(MainViewModel? viewModel, Action<string> onSave, Action? onOpenWindowed = null, string? initialContent = null)
    {
        _viewModel = viewModel;
        _onSave = onSave;
        _onOpenWindowed = onOpenWindowed;

        InitializeComponent();

        if (!string.IsNullOrEmpty(initialContent))
        {
            EditorTextBox.Text = initialContent;
        }

        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _appearanceManager = new WindowAppearanceManager(this);
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

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AddTimestampButton_OnClick(object sender, RoutedEventArgs e)
    {
        InsertTimestamp(TimeSpan.Zero);
    }

    private void AddCurrentTimestampButton_OnClick(object sender, RoutedEventArgs e)
    {
        var position = _viewModel?.EstimatedPosition ?? TimeSpan.Zero;
        InsertTimestamp(position);
    }

    private void InsertTimestamp(TimeSpan timestamp)
    {
        var ts = FormatTimestamp(timestamp);

        var text = EditorTextBox.Text;
        var caretIndex = EditorTextBox.CaretIndex;

        if (text.Length == 0)
        {
            EditorTextBox.Text = ts;
            EditorTextBox.CaretIndex = EditorTextBox.Text.Length;
            return;
        }

        var insertAt = Math.Min(caretIndex, text.Length);

        if (insertAt > 0 && text[insertAt - 1] != '\n')
        {
            text = text.Insert(insertAt, "\n");
            insertAt++;
        }

        EditorTextBox.Text = text.Insert(insertAt, ts);
        EditorTextBox.CaretIndex = insertAt + ts.Length;
        EditorTextBox.Focus();
    }

    private static string FormatTimestamp(TimeSpan ts)
    {
        var minutes = (int)ts.TotalMinutes;
        var seconds = ts.Seconds;
        var milliseconds = ts.Milliseconds;
        return $"[{minutes:00}:{seconds:00}.{milliseconds:000}]";
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        _onSave?.Invoke(EditorTextBox.Text);
        _onOpenWindowed?.Invoke();
    }

    private void SaveToDiskButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "LRC files (*.lrc)|*.lrc|All files (*.*)|*.*",
            DefaultExt = ".lrc",
            FileName = "lyrics.lrc"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.WriteAllText(dialog.FileName, EditorTextBox.Text);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void EditorTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
