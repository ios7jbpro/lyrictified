namespace Nikse.SubtitleEdit.Features.Video.TextToSpeech.Voices;

public class ChatterboxVoice
{
    public string Voice { get; set; }
    public string FilePath { get; set; }

    public override string ToString()
    {
        return Voice;
    }

    public ChatterboxVoice()
    {
        Voice = string.Empty;
        FilePath = string.Empty;
    }

    public ChatterboxVoice(string voice, string filePath)
    {
        Voice = voice;
        FilePath = filePath;
    }
}
