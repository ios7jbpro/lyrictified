#if DEBUG
using Lyrictified;

namespace Lyrictified.Services;

internal static class DebugBuildHelper
{
    public static string? ShowDialog()
    {
        string? result = null;
        bool ignoreCache = false;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new DebugStartupDialog();
            if (dialog.ShowDialog() == true)
            {
                result = dialog.Result;
                ignoreCache = dialog.IgnoreLocalCache;
            }
        });

        App.IgnoreLocalCache = ignoreCache;
        return result;
    }
}
#endif
