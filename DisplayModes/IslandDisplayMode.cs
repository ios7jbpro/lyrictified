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
    public const double VerticalScalePadding = 8;
    public const double AnimationOverflowPadding = 48;
    public const int MinimumContainerHeight = 20;

    public static double GetSingleLineStartY()
    {
        return 8;
    }

    public static int GetEffectiveMaximumWidth(int? configuredMaximumWidth)
    {
        return Math.Max(configuredMaximumWidth ?? DefaultMaximumWidth, MinimumMaximumWidth);
    }

    public static double GetEffectiveScale(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            return 1.0;
        }

        return Math.Clamp(scale, 0.5, 2.0);
    }

    public static int GetEffectiveContainerHeight(int configuredContainerHeight)
    {
        return Math.Max(configuredContainerHeight, MinimumContainerHeight);
    }

    public static double GetEffectiveCornerRadius(double radius)
    {
        if (double.IsNaN(radius) || double.IsInfinity(radius) || radius < 0)
        {
            return 14;
        }

        return Math.Clamp(radius, 0, 40);
    }

    public static double GetEffectiveHoverOpacity(double opacity)
    {
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
        {
            return 0.16;
        }

        return Math.Clamp(opacity, 0, 1);
    }

    public static double GetAutomaticContainerHeight(double scale)
    {
        return (WindowHeight * GetEffectiveScale(scale)) + (VerticalScalePadding * 2);
    }

    public static double GetEffectiveContainerHeight(int? configuredContainerHeight, double scale)
    {
        return configuredContainerHeight.HasValue
            ? GetEffectiveContainerHeight(configuredContainerHeight.Value)
            : GetAutomaticContainerHeight(scale);
    }

    public static (double Left, double Top, double Width, double Height) GetWindowBounds(DisplayMonitor monitor, int? configuredMaximumWidth, double scale, int? configuredContainerHeight)
    {
        var monitorWidth = monitor.Bounds.right - monitor.Bounds.left;
        var effectiveScale = GetEffectiveScale(scale);
        var maximumWidth = GetEffectiveMaximumWidth(configuredMaximumWidth);
        var logicalWidth = Math.Min(maximumWidth, Math.Max(MinimumMaximumWidth, monitorWidth / effectiveScale));
        var width = Math.Min(monitorWidth, logicalWidth * effectiveScale);
        var left = monitor.Bounds.left + ((monitorWidth - width) / 2);
        var top = monitor.Bounds.top;
        var height = GetEffectiveContainerHeight(configuredContainerHeight, effectiveScale);
        return (left, top, width, height);
    }
}
