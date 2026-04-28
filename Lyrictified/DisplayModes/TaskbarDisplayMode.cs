using Lyrictified.Interop;

namespace Lyrictified.DisplayModes;

public static class TaskbarDisplayMode
{
    public const int WindowHeight = 48;
    public const int MinimumMaximumWidth = 100;
    public const int DefaultMaximumWidth = 880;
    public const double LyricFontSize = 20;
    public const double StageHeight = 24;
    public const double HorizontalMargin = 12;
    public const double BottomOffset = 0;

    public static double GetSingleLineStartY()
    {
        return 8;
    }

    public static int GetEffectiveMaximumWidth(int? configuredMaximumWidth)
    {
        return Math.Max(configuredMaximumWidth ?? DefaultMaximumWidth, MinimumMaximumWidth);
    }

    public static (double Left, double Top, double Width, double Height) GetWindowBounds(DisplayMonitor monitor, int? configuredMaximumWidth)
    {
        var monitorWidth = monitor.Bounds.right - monitor.Bounds.left;
        var maximumWidth = GetEffectiveMaximumWidth(configuredMaximumWidth);
        var width = Math.Min(maximumWidth, Math.Max(MinimumMaximumWidth, monitorWidth / 2));
        var left = monitor.Bounds.left + HorizontalMargin;
        var top = monitor.Bounds.bottom - WindowHeight - BottomOffset;
        return (left, top, width, WindowHeight);
    }
}
