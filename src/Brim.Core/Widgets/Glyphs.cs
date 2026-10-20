namespace Brim.Core.Widgets;

/// <summary>Häufig genutzte Zeichen aus Segoe Fluent Icons / Segoe MDL2 Assets (Codepunkte wie in der Microsoft-Dokumentation).</summary>
public static class Glyphs
{
    public static readonly string Play = G(0xE768);
    public static readonly string Pause = G(0xE769);
    public static readonly string Previous = G(0xE892);
    public static readonly string Next = G(0xE893);
    public static readonly string Volume = G(0xE767);
    public static readonly string Volume0 = G(0xE992);
    public static readonly string Volume1 = G(0xE993);
    public static readonly string Volume2 = G(0xE994);
    public static readonly string Volume3 = G(0xE995);
    public static readonly string Mute = G(0xE74F);
    public static readonly string Speakers = G(0xE7F5);
    public static readonly string MusicNote = G(0xEC4F);
    public static readonly string Clock = G(0xE917);
    public static readonly string Calendar = G(0xE787);
    public static readonly string Folder = G(0xE8B7);
    public static readonly string Settings = G(0xE713);
    public static readonly string Puzzle = G(0xEA86);
    public static readonly string Back = G(0xE76B);
    public static readonly string CheckMark = G(0xE73E);
    public static readonly string Headphones = G(0xE7F6);
    public static readonly string Mixer = G(0xE9E9);
    public static readonly string Download = G(0xE896);
    public static readonly string Computer = G(0xE7F4);
    public static readonly string Link = G(0xE71B);
    public static readonly string Updated = G(0xE777);
    public static readonly string Camera = G(0xE714);
    public static readonly string Microphone = G(0xE720);
    public static readonly string Brightness = G(0xE706);
    public static readonly string LightningBolt = G(0xE945);
    public static readonly string OpenInNew = G(0xE8A7);
    public static readonly string Dot = G(0xE91F);
    public static readonly string Shield = G(0xE72E);

    /// <summary>Akkusymbol in elf Stufen (MobBattery0–10), beim Laden mit Blitz, im Energiesparmodus mit Blatt.</summary>
    public static string Battery(int percent, bool charging, bool saver)
    {
        var level = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        var first = charging ? 0xEBAB : saver ? 0xEBB6 : 0xEBA0;
        return G(first + level);
    }

    private static string G(int codePoint) => char.ConvertFromUtf32(codePoint);
}
