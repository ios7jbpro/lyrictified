using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;

namespace LibSETests.SubtitleFormats;

public class PacTest
{
    [Fact]
    public void PacItalic1()
    {
        var target = new Pac();
        var subtitle = new Subtitle();
        string subText = "Now <i>go</i> on!";
        subtitle.Paragraphs.Add(new Paragraph(subText, 0, 999));
        var ms = new MemoryStream();
        target.Save("test.pac", ms, subtitle);
        var reload = new Subtitle();
        target.LoadSubtitle(reload, ms.ToArray());
        Assert.True(reload.Paragraphs[0].Text == "Now <i>go</i> on!");
    }

    [Fact]
    public void PacItalic2()
    {
        var target = new Pac();
        var subtitle = new Subtitle();
        string subText = "<i>Now go on!</i>";
        subtitle.Paragraphs.Add(new Paragraph(subText, 0, 999));
        var ms = new MemoryStream();
        target.Save("test.pac", ms, subtitle);
        var reload = new Subtitle();
        target.LoadSubtitle(reload, ms.ToArray());
        Assert.True(reload.Paragraphs[0].Text == "<i>Now go on!</i>");
    }

    [Fact]
    public void PacItalic3()
    {
        var target = new Pac();
        var subtitle = new Subtitle();
        string subText = "<i>Now</i>. Go on!";
        subtitle.Paragraphs.Add(new Paragraph(subText, 0, 999));
        var ms = new MemoryStream();
        target.Save("test.pac", ms, subtitle);
        var reload = new Subtitle();
        target.LoadSubtitle(reload, ms.ToArray());
        Assert.True(reload.Paragraphs[0].Text == "<i>Now</i>. Go on!");
    }

    [Fact]
    public void PacItalic4()
    {
        var target = new Pac();
        var subtitle = new Subtitle();
        string subText = "V <i>Now</i> Go on!";
        subtitle.Paragraphs.Add(new Paragraph(subText, 0, 999));
        var ms = new MemoryStream();
        target.Save("test.pac", ms, subtitle);
        var reload = new Subtitle();
        target.LoadSubtitle(reload, ms.ToArray());
        Assert.True(reload.Paragraphs[0].Text == "V <i>Now</i> Go on!");
    }

    [Fact]
    public void PacItalic5()
    {
        var target = new Pac();
        var subtitle = new Subtitle();
        string subText = "V <i>Now</i> G";
        subtitle.Paragraphs.Add(new Paragraph(subText, 0, 999));
        var ms = new MemoryStream();
        target.Save("test.pac", ms, subtitle);
        var reload = new Subtitle();
        target.LoadSubtitle(reload, ms.ToArray());
        Assert.True(reload.Paragraphs[0].Text == "V <i>Now</i> G");
    }

    [Fact]
    public void PacItalic6()
    {
        var target = new Pac();
        var subtitle = new Subtitle();
        string subText = "V <i>Now</i>.";
        subtitle.Paragraphs.Add(new Paragraph(subText, 0, 999));
        var ms = new MemoryStream();
        target.Save("test.pac", ms, subtitle);
        var reload = new Subtitle();
        target.LoadSubtitle(reload, ms.ToArray());
        Assert.True(reload.Paragraphs[0].Text == "V <i>Now</i>.");
    }

    [Theory]
    [InlineData("{\\an7}<i>Italic left text</i>", "{\\an7}")]
    [InlineData("{\\an8}<i>Italic center text</i>", "{\\an8}")]
    [InlineData("{\\an9}<i>Italic right text</i>", "{\\an9}")]
    public void PacItalicAlignmentIsPreserved(string input, string expectedAlignmentTag)
    {
        // Some external PAC writers set bit 0x04 of the alignment byte (after the 0xFE marker)
        // as an italic flag in addition to using <...> markers in the text. The reader must mask
        // out this italic bit so the alignment value is interpreted correctly.
        var target = new Pac { CodePage = Pac.CodePageLatin };
        var subtitle = new Subtitle();
        subtitle.Paragraphs.Add(new Paragraph(input, 0, 999));
        var ms = new MemoryStream();
        target.Save("test.pac", ms, subtitle);
        var bytes = ms.ToArray();

        for (var i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == 0xFE)
            {
                bytes[i + 1] |= 0x04;
            }
        }

        var reload = new Subtitle();
        target.LoadSubtitle(reload, bytes);
        Assert.StartsWith(expectedAlignmentTag, reload.Paragraphs[0].Text);
    }
}