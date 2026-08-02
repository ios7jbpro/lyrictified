using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Application = System.Windows.Application;
using Lyrictified.Services;
using Lyrictified.Settings;

namespace Lyrictified;

public partial class App : Application
{
    public const string AppUserModelId = "Lyrictified.App";
    public static string LocalLyricsBaseAddress { get; set; } = "https://api.lyrictified.xyz/";
    public static bool IgnoreLocalCache { get; set; }
    public static bool IsWineBridge { get; private set; }
    public static int WineBridgePort { get; private set; }

    private static readonly int WM_SHOW_SETTINGS = (int)RegisterWindowMessage("Lyrictified_ShowSettings");
    private static Mutex? _instanceMutex;

    private readonly AppSettingsService _appSettingsService = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        ParseLaunchArguments(e.Args);
        if (!TryAcquireMutex())
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        Logger.Log("=== App started ===");
        TrySetAppUserModelId();
        base.OnStartup(e);

        var settings = _appSettingsService.Load();
        if (!settings.SuppressVmWarning && VmDetectionService.IsRunningInVirtualMachine())
        {
            Logger.Log("VM detected.");
            VmDetectionService.ShowVmWarningNotification();
        }

#if DEBUG
        var chosenBaseAddress = DebugBuildHelper.ShowDialog();
        if (chosenBaseAddress is null)
        {
            Shutdown();
            return;
        }
        LocalLyricsBaseAddress = chosenBaseAddress;
#endif

        WindowsAutostartService.Apply(settings.AutostartWithWindows);
        RestartDisplayWindow();
    }

    private static void ParseLaunchArguments(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            var parts = argument.TrimStart('-', '/').Split('=', 2);
            if (parts.Length != 2)
                continue;

            if (parts[0].Equals("wine", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("1", StringComparison.OrdinalIgnoreCase))
            {
                IsWineBridge = true;
            }
            else if (parts[0].Equals("port", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parts[1], out var port) && port is >= 1 and <= 65535)
            {
                WineBridgePort = port;
            }
        }

        if (IsWineBridge && WineBridgePort == 0)
        {
            Logger.Log("Wine bridge requested without a valid port; using no media source.");
        }
    }

    private static bool TryAcquireMutex()
    {
        try
        {
            if (Mutex.TryOpenExisting("Lyrictified_SingleInstance", out var existing))
            {
                try
                {
                    if (existing.WaitOne(0))
                    {
                        _instanceMutex = existing;
                        return true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    _instanceMutex = existing;
                    return true;
                }
                existing.Dispose();
                return false;
            }

            _instanceMutex = new Mutex(false, "Lyrictified_SingleInstance");
            if (_instanceMutex.WaitOne(0))
            {
                return true;
            }

            _instanceMutex.Dispose();
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(currentProcess.ProcessName);

            foreach (var process in processes)
            {
                if (process.Id == currentProcess.Id)
                    continue;

                var targetHwnd = IntPtr.Zero;
                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out int pid);
                    if (pid == process.Id)
                    {
                        targetHwnd = hwnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (targetHwnd != IntPtr.Zero)
                {
                    _ = ShowWindowAsync(targetHwnd, 5);
                    _ = SetForegroundWindow(targetHwnd);
                    _ = PostMessage(targetHwnd, WM_SHOW_SETTINGS, IntPtr.Zero, IntPtr.Zero);
                    break;
                }
            }
        }
        catch
        {
            // Best effort
        }
    }

    private static IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SHOW_SETTINGS)
        {
            if (Current is App app && app.MainWindow is ITrayIconHost host)
            {
                app.Dispatcher.BeginInvoke(() => host.OpenSettingsFromTray());
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

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
        if (IsWineBridge && settings.DisplayMode is DisplayMode.AppBar or DisplayMode.Wallpaper)
        {
            settings.DisplayMode = DisplayMode.Windowed;
            _appSettingsService.Save(settings);
        }
        var previousWindow = MainWindow;
        Window nextWindow = settings.DisplayMode switch
        {
            DisplayMode.Island => new IslandWindow(),
            DisplayMode.Wallpaper => new WallpaperWindow(),
            DisplayMode.Taskbar => new TaskbarWindow(),
            DisplayMode.Windowed => new WindowedWindow(),
            _ => new AppBarWindow()
        };

        MainWindow = nextWindow;
        nextWindow.Show();

        if (PresentationSource.FromVisual(nextWindow) is HwndSource hwndSource)
        {
            hwndSource.AddHook(WndProcHook);
        }
        else
        {
            nextWindow.SourceInitialized += OnNextWindowSourceInitialized;
        }

        if (previousWindow is not null && !ReferenceEquals(previousWindow, nextWindow))
        {
            previousWindow.Close();
        }
    }

    private void OnNextWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.SourceInitialized -= OnNextWindowSourceInitialized;
            if (PresentationSource.FromVisual(window) is HwndSource hwndSource)
            {
                hwndSource.AddHook(WndProcHook);
            }
        }
    }
}
