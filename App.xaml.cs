using System.Diagnostics;
using System.Windows;
using Application = System.Windows.Application;
using Lyrictified.Services;
using Lyrictified.Settings;

namespace Lyrictified;

public partial class App : Application
{
    public static string LocalLyricsBaseAddress { get; set; } = "https://lyrictifiedserve.ios7.xyz/";

    private readonly AppSettingsService _appSettingsService = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Logger.Log("=== App started ===");
        base.OnStartup(e);

#if DEBUG
        var chosenBaseAddress = DebugBuildHelper.ShowDialog();
        if (chosenBaseAddress is null)
        {
            Shutdown();
            return;
        }
        LocalLyricsBaseAddress = chosenBaseAddress;
#endif

        RestartDisplayWindow();
    }

    public void RestartDisplayWindow()
    {
        var settings = _appSettingsService.Load();
        var previousWindow = MainWindow;
        Window nextWindow = settings.DisplayMode switch
        {
            DisplayMode.Taskbar => new TaskbarWindow(),
            DisplayMode.Windowed => new WindowedWindow(),
            _ => new AppBarWindow()
        };

        MainWindow = nextWindow;
        nextWindow.Show();
        if (previousWindow is not null && !ReferenceEquals(previousWindow, nextWindow))
        {
            previousWindow.Close();
    }
}
}
