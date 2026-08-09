using System.Collections.Generic;

namespace Nikse.SubtitleEdit.Logic.Config;

public class SeSsa
{
    public List<SeAssaStyle> StoredStyles { get; set; }

    public SeSsa()
    {
        StoredStyles = new List<SeAssaStyle>();
    }
}
