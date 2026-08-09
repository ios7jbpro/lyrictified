using SeConv.Core;
using Xunit;

namespace SeConvTests.Core;

public class ChangeSpeedTest : IDisposable
{
    private readonly string _tempRoot;

    private const string SrtContent = """
        1
        00:00:01,000 --> 00:00:03,000
        First.

        2
        00:00:10,000 --> 00:00:11,000
        Second.

        3
        00:00:20,000 --> 00:00:22,500
        Third.

        """;

    public ChangeSpeedTest()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ChangeSpeedTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task ConvertAsync_ChangeSpeed125_ScalesAllLinesByOver1_25()
    {
        // 125% means times are scaled by 100/125 = 0.8 (faster -> earlier).
        var input = Path.Combine(_tempRoot, "in.srt");
        await File.WriteAllTextAsync(input, SrtContent, TestContext.Current.CancellationToken);
        var outDir = Path.Combine(_tempRoot, "out");
        Directory.CreateDirectory(outDir);

        var converter = new SubtitleConverter();
        var result = await converter.ConvertAsync(new ConversionOptions
        {
            Patterns = [input],
            Format = "SubRip",
            OutputFolder = outDir,
            Overwrite = true,
            ChangeSpeedPercent = 125.0,
        });

        Assert.True(result.Success, string.Join("; ", result.Errors));

        var outText = await File.ReadAllTextAsync(Path.Combine(outDir, "in.srt"), TestContext.Current.CancellationToken);

        // Line 1: 1.000 -> 0.800, 3.000 -> 2.400
        Assert.Contains("00:00:00,800 --> 00:00:02,400", outText);
        // Line 2: 10.000 -> 8.000, 11.000 -> 8.800
        Assert.Contains("00:00:08,000 --> 00:00:08,800", outText);
        // Line 3: 20.000 -> 16.000, 22.500 -> 18.000
        Assert.Contains("00:00:16,000 --> 00:00:18,000", outText);
    }

    [Fact]
    public async Task ConvertAsync_ChangeSpeed50_DoublesTimes()
    {
        var input = Path.Combine(_tempRoot, "in.srt");
        await File.WriteAllTextAsync(input, SrtContent, TestContext.Current.CancellationToken);
        var outDir = Path.Combine(_tempRoot, "out");
        Directory.CreateDirectory(outDir);

        var converter = new SubtitleConverter();
        var result = await converter.ConvertAsync(new ConversionOptions
        {
            Patterns = [input],
            Format = "SubRip",
            OutputFolder = outDir,
            Overwrite = true,
            ChangeSpeedPercent = 50.0,
        });

        Assert.True(result.Success, string.Join("; ", result.Errors));

        var outText = await File.ReadAllTextAsync(Path.Combine(outDir, "in.srt"), TestContext.Current.CancellationToken);

        // 1.000 -> 2.000, 3.000 -> 6.000
        Assert.Contains("00:00:02,000 --> 00:00:06,000", outText);
    }

    [Fact]
    public async Task ConvertAsync_ChangeSpeed100_LeavesTimesUnchanged()
    {
        var input = Path.Combine(_tempRoot, "in.srt");
        await File.WriteAllTextAsync(input, SrtContent, TestContext.Current.CancellationToken);
        var outDir = Path.Combine(_tempRoot, "out");
        Directory.CreateDirectory(outDir);

        var converter = new SubtitleConverter();
        var result = await converter.ConvertAsync(new ConversionOptions
        {
            Patterns = [input],
            Format = "SubRip",
            OutputFolder = outDir,
            Overwrite = true,
            ChangeSpeedPercent = 100.0,
        });

        Assert.True(result.Success, string.Join("; ", result.Errors));

        var outText = await File.ReadAllTextAsync(Path.Combine(outDir, "in.srt"), TestContext.Current.CancellationToken);

        Assert.Contains("00:00:01,000 --> 00:00:03,000", outText);
        Assert.Contains("00:00:10,000 --> 00:00:11,000", outText);
        Assert.Contains("00:00:20,000 --> 00:00:22,500", outText);
    }
}
