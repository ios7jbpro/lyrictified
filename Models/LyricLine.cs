namespace Lyrictified.Models;

public sealed record WordInfo(TimeSpan Timestamp, string Word);

public sealed record LyricLine(TimeSpan Timestamp, string Text, IReadOnlyList<WordInfo>? Words = null);
