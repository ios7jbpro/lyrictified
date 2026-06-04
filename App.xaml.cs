using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Application = System.Windows.Application;
using Lyrictified.Services;
using Lyrictified.Settings;

namespace Lyrictified;

public partial class App : Application
{
    public const string AppUserModelId = "Lyrictified.App";
    public static string LocalLyricsBaseAddress { get; set; } = "https://lyrictifiedserve.ios7.xyz/";
    public static bool IgnoreLocalCache { get; set; }

    private readonly AppSettingsService _appSettingsService = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Logger.Log("=== App started ===");
        TrySetAppUserModelId();
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

        WindowsAutostartService.Apply(_appSettingsService.Load().AutostartWithWindows);
        RestartDisplayWindow();
    }

    private static void TrySetAppUserModelId()
    {
        try
        {
            var hr = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            Logger.Log($"Set AppUserModelID '{AppUserModelId}': hr=0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Set AppUserModelID failed: {ex}");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

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
