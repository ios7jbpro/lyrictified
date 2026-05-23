using System.Diagnostics;
using System.Windows;
using Application = System.Windows.Application;
using Lyrictified.Services;
using Lyrictified.Settings;

namespace Lyrictified;

public partial class App : Application
{
    private readonly AppSettingsService _appSettingsService = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Logger.Log("=== App started ===");
        base.OnStartup(e);
        RestartDisplayWindow();
    }

    public void RestartDisplayWindow()
    {
        var settings = _appSettingsService.Load();
        var previousWindow = MainWindow;
        Window nextWindow = settings.DisplayMode == DisplayMode.Taskbar
            ? new TaskbarWindow()
            : new AppBarWindow();

        MainWindow = nextWindow;
        nextWindow.Show();
        if (previousWindow is not null && !ReferenceEquals(previousWindow, nextWindow))
        {
            previousWindow.Close();
    }
}
}
