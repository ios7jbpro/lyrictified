using System.Diagnostics;
using System.Windows;
using Lyrictified.Services;

namespace Lyrictified;

public partial class DebugStartupDialog : Window
{
    public string? Result { get; private set; }
    public bool IgnoreLocalCache { get; private set; }
    private ShutdownMode _previousShutdownMode;

    public DebugStartupDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _previousShutdownMode = System.Windows.Application.Current.ShutdownMode;
        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.ShutdownMode = _previousShutdownMode;
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    private void BtnLocal_OnClick(object sender, RoutedEventArgs e)
    {
        var auto = LocalServerDetector.TryAutoDetect();
        if (auto is not null)
        {
            Result = auto;
            IgnoreLocalCache = ShowCacheDialog();
            DialogResult = true;
            return;
        }

        var serverDialog = new DebugServerDialog { Owner = this };
        if (serverDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(serverDialog.ServerUrl))
        {
            Result = serverDialog.ServerUrl;
            IgnoreLocalCache = ShowCacheDialog();
            DialogResult = true;
        }
    }

    private void BtnPublic_OnClick(object sender, RoutedEventArgs e)
    {
        Result = "https://api.lyrictified.xyz/";
        IgnoreLocalCache = ShowCacheDialog();
        DialogResult = true;
    }

    private void BtnGet_OnClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://github.com/ios7jbpro/Lyrictified-Server")
        {
            UseShellExecute = true
        });
    }

    private void BtnExit_OnClick(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    private bool ShowCacheDialog()
    {
        var cacheDialog = new DebugCacheDialog { Owner = this };
        cacheDialog.ShowDialog();
        return cacheDialog.IgnoreCache;
    }
}
