using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Nikse.SubtitleEdit.Features.Tools.MergeTwoSubtitles;

public partial class MergeTwoSubtitlesDisplayItem : ObservableObject
{
    [ObservableProperty] private int _number;
    [ObservableProperty] private TimeSpan _startTime;
    [ObservableProperty] private TimeSpan _endTime;
    [ObservableProperty] private string _text = string.Empty;
}
