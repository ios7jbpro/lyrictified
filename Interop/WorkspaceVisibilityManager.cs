using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Lyrictified.Services;

namespace Lyrictified.Interop;

public static class WorkspaceVisibilityManager
{
    public static void PinToAllWorkspaces(Window window)
    {
        window.Loaded -= Window_OnLoaded;
        window.Loaded += Window_OnLoaded;

        TryPin(window, allowAppFallback: false);

        _ = window.Dispatcher.BeginInvoke(
            () => TryPin(window, allowAppFallback: false),
            DispatcherPriority.ApplicationIdle);

        ScheduleRetry(window, TimeSpan.FromMilliseconds(250), allowAppFallback: false);
        ScheduleRetry(window, TimeSpan.FromSeconds(1), allowAppFallback: true);
    }

    private static void ScheduleRetry(Window window, TimeSpan delay, bool allowAppFallback)
    {
        var retryTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = delay
        };

        retryTimer.Tick += (_, _) =>
        {
            retryTimer.Stop();
            TryPin(window, allowAppFallback);
        };
        retryTimer.Start();
    }

    private static bool TryPin(Window window, bool allowAppFallback)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                Logger.Log($"Workspace pin skipped for {window.GetType().Name}: HWND is zero.");
                return false;
            }

            var result = NativeVirtualDesktopPinning.PinWindow(hwnd);
            if (result.WindowPinned)
            {
                var unpinResult = NativeVirtualDesktopPinning.UnpinApplicationId(App.AppUserModelId);
                if (unpinResult.Succeeded)
                {
                    Logger.Log($"Workspace app fallback unpinned: appId={App.AppUserModelId}");
                }
            }
            else if (!result.Succeeded && allowAppFallback)
            {
                var appResult = NativeVirtualDesktopPinning.PinApplicationId(App.AppUserModelId);
                Logger.Log(
                    $"Workspace app pin {window.GetType().Name}: appId={App.AppUserModelId}, success={appResult.Succeeded}, appPinned={appResult.ApplicationPinned}, error={appResult.Error ?? "<none>"}");

                if (appResult.Succeeded)
                {
                    result = appResult;
                }
            }

            Logger.Log(
                $"Workspace pin {window.GetType().Name}: hwnd={hwnd}, success={result.Succeeded}, windowPinned={result.WindowPinned}, appPinned={result.ApplicationPinned}, appId={result.AppUserModelId ?? "<none>"}, error={result.Error ?? "<none>"}");
            return result.Succeeded;
        }
        catch (Exception ex)
        {
            Logger.Log($"Workspace pin failed for {window.GetType().Name}: {ex}");
            return false;
        }
    }

    private static void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.Loaded -= Window_OnLoaded;
        TryPin(window, allowAppFallback: false);
    }
}
