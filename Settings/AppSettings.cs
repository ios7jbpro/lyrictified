namespace Lyrictified.Settings;

public sealed class AppSettings
{
    public DisplayMode DisplayMode { get; set; } = DisplayMode.AppBar;

    public HideMode HideMode { get; set; } = HideMode.Nothing;

    public HideMode WindowedHideMode { get; set; } = HideMode.Nothing;

    public string? PreferredMonitorDeviceName { get; set; }

    public string? AppBarPreferredMonitorDeviceName { get; set; }

    public string? TaskbarPreferredMonitorDeviceName { get; set; }

    public string? IslandPreferredMonitorDeviceName { get; set; }

    public bool ShowNextLine { get; set; }

    public bool WindowedShowNextLine { get; set; }

    public int? CustomBarHeight { get; set; }

    public double? WindowedWidth { get; set; }

    public double? WindowedHeight { get; set; }

    public int? TaskbarMaximumWidth { get; set; }

    public int? IslandMaximumWidth { get; set; }

    public double IslandScale { get; set; } = 1.0;

    public int? IslandContainerHeight { get; set; }

    public LyricAlignment LyricAlignment { get; set; } = LyricAlignment.Center;

    public LyricAlignment WindowedLyricAlignment { get; set; } = LyricAlignment.Center;

    public bool ShowAlbumArt { get; set; } = true;

    public bool WindowedShowAlbumArt { get; set; } = true;

    public bool AppBarShowProgressBar { get; set; } = true;

    public AppBarAdaptMode AppBarAdaptMode { get; set; } = AppBarAdaptMode.Disabled;

    [System.Text.Json.Serialization.JsonPropertyName("AppBarAdaptToContent")]
    public bool? LegacyAppBarAdaptToContent
    {
        set
        {
            if (value == true && AppBarAdaptMode == AppBarAdaptMode.Disabled)
                AppBarAdaptMode = AppBarAdaptMode.Adapt;
        }
    }

    public int AppBarAdaptThreshold { get; set; } = 130;

    public bool WordByWordMode { get; set; }

    public bool WindowedWordByWordMode { get; set; }

    public bool DisplayTtmlLyrics { get; set; }

    public bool WindowedDisplayTtmlLyrics { get; set; }

    public int MaxCacheSize { get; set; } = 25;

    public bool AutostartWithWindows { get; set; }

    public List<DetectedMediaApp> DetectedMediaApps { get; set; } = new();

    public List<string> IgnoredMediaAppIds { get; set; } = new();

    public string? DebugForceLyricsSource { get; set; }
}
