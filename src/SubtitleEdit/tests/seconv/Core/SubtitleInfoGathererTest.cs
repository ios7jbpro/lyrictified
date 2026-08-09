using SeConv.Core;
using System.Text;
using Xunit;

namespace SeConvTests.Core;

public class SubtitleInfoGathererTest : IDisposable
{
    private readonly string _tempDir;

    public SubtitleInfoGathererTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "seconv-info-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string WriteSrt(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(true));
        return path;
    }

    [Fact]
    public void Gather_ReadsBasicSrtFields()
    {
        var path = WriteSrt("a.srt",
            "1\n00:00:01,000 --> 00:00:03,000\nHello world.\n\n" +
            "2\n00:00:05,500 --> 00:00:07,250\nGoodbye.\n");

        var info = SubtitleInfoGatherer.Gather(path);

        Assert.Equal(path, info.Path);
        Assert.Equal("SubRip", info.Format);
        Assert.Equal(".srt", info.Extension);
        Assert.Equal(2, info.ParagraphCount);
        Assert.Equal(1000, info.FirstStartMs);
        Assert.Equal(7250, info.LastEndMs);
        Assert.Equal(6250, info.DurationMs);
        Assert.True(info.FileSizeBytes > 0);
    }

    [Fact]
    public void Gather_DetectsUtf8Bom()
    {
        var path = WriteSrt("bom.srt",
            "1\n00:00:01,000 --> 00:00:03,000\nHello.\n",
            new UTF8Encoding(true));

        var info = SubtitleInfoGatherer.Gather(path);

        Assert.Contains("BOM", info.Encoding);
    }

    [Fact]
    public void Gather_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => SubtitleInfoGatherer.Gather(Path.Combine(_tempDir, "nope.srt")));
    }
}
