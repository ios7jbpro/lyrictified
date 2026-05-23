namespace Lyrictified.DisplayModes;

public static class AppBarDisplayMode
{
    public const int DefaultHeight = 110;
    public const int ShowNextLineHeight = 136;
    public const int MinimumCustomHeight = 72;
    public const double SingleLineStageHeight = 38;
    public const double ShowNextLineStageHeight = 76;
    public const double CurrentLyricFontSize = 30;
    public const double PreviewLyricFontSize = 18;
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
        var automaticHeight = GetAutomaticHeight(showNextLine);
        if (customHeight is not int height)
        {
            return automaticHeight;
        }

        return Math.Max(Math.Max(height, automaticHeight), MinimumCustomHeight);
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
