namespace Nikse.SubtitleEdit.Features.Tools.BatchConvert;

public class SortByOption
{
    public string Key { get; set; }
    public string DisplayName { get; set; }

    public SortByOption(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    public override string ToString()
    {
        return DisplayName;
    }
}
