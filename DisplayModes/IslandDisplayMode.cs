using Lyrictified.Interop;

namespace Lyrictified.DisplayModes;

public static class IslandDisplayMode
{
    public const int WindowHeight = 48;
    public const int MinimumMaximumWidth = 100;
    public const int DefaultMaximumWidth = 880;
    public const double LyricFontSize = 20;
    public const double StageHeight = 24;
    public const double HorizontalMargin = 12;
    public const double BackgroundHorizontalPadding = 36;
    public const double MinimumBackgroundWidth = 96;
    public const double WidthAnimationSlideOffset = 8;

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
        var left = monitor.Bounds.left + ((monitorWidth - width) / 2);
        var top = monitor.Bounds.top;
        return (left, top, width, WindowHeight);
    }
}
