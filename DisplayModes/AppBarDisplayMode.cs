namespace Lyrictified.DisplayModes;

public static class AppBarDisplayMode
{
    public const int DefaultHeight = 80;
    public const int ShowNextLineHeight = 136;
    public const int MinimumCustomHeight = 32;
    public const double SingleLineStageHeight = 46;
    public const double ShowNextLineStageHeight = 84;
    public const double CurrentLyricFontSize = 34;
    public const double PreviewLyricFontSize = 20;
    public const double PreviewLyricOpacity = 0.35;
    public const double PreviewRestY = 0;
    public const double PreviewEnterY = 8;
    public const double PreviewPromoteStartY = 42;
    public const double IncomingSingleLineStartY = 12;
    public const double IncomingPromoteStartY = 14;

    public static int GetAutomaticHeight(bool showNextLine)
    {
        return showNextLine ? ShowNextLineHeight : DefaultHeight;
    }

    public static int GetEffectiveHeight(bool showNextLine, int? customHeight)
    {
        if (customHeight is int height && height > 0)
        {
            return Math.Max(height, MinimumCustomHeight);
        }

        return GetAutomaticHeight(showNextLine);
    }

    public static double GetStageHeight(bool showNextLine, int effectiveHeight)
    {
        var automaticBarHeight = GetAutomaticHeight(showNextLine);
        var automaticStageHeight = showNextLine ? ShowNextLineStageHeight : SingleLineStageHeight;
        if (effectiveHeight <= automaticBarHeight)
        {
            return automaticStageHeight;
        }

        return automaticStageHeight + (effectiveHeight - automaticBarHeight);
    }
}
