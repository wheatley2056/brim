namespace Brim.Core;

/// <summary>Hell oder dunkel (Einstellungen → Aussehen).</summary>
public enum NotchTheme
{
    /// <summary>Wie Windows: folgt dem Windows-Modus, also dem der Taskleiste – nicht dem der Apps.</summary>
    System,
    Dark,
    Light,
}

/// <summary>Woher die Akzentfarbe für Fortschritt, Schalter, aktiven Tab und Wiedergabe-Knopf kommt.</summary>
public enum AccentSource
{
    /// <summary>Akzentfarbe von Windows (Einstellungen → Personalisierung → Farben).</summary>
    Windows,

    /// <summary>Aus dem Cover der Musik, sonst Pfirsich.</summary>
    Cover,

    /// <summary>Immer Brims Pfirsich.</summary>
    Brim,
}

/// <summary>
/// Farben für den hellen Modus. Alle Module und Ansichten beschreiben ihre Farben für den dunklen Grund (Plan 4.2:
/// deklarative Widgets); im hellen Modus rechnet die Notch sie an einer Stelle um. Grautöne kehren sich um
/// (heller Text wird dunkel, dunkle Flächen werden hell), Farben werden so weit abgedunkelt, dass sie auf hellem
/// Mica lesbar bleiben.
/// </summary>
public static class ThemePalette
{
    /// <summary>Unterhalb dieses Abstands zwischen hellstem und dunkelstem Kanal gilt eine Farbe als Grau.</summary>
    private const int GrayChroma = 24;

    /// <summary>Text im hellen Modus nie ganz schwarz, wie in Windows 11.</summary>
    private const int DarkestText = 0x1A;

    /// <summary>Grund im hellen Modus, gegen den Kontraste gerechnet werden (helles Mica).</summary>
    public const string LightSurface = "#EEF1F6";

    /// <summary>
    /// Hell nur mit Material: Der schwarze Körper soll wie ein Loch im Bild wirken und bleibt deshalb dunkel.
    /// </summary>
    public static bool IsLight(NotchTheme theme, NotchMaterial material, bool windowsUsesLight) =>
        material != NotchMaterial.Black && (theme == NotchTheme.Light || (theme == NotchTheme.System && windowsUsesLight));

    /// <summary>Farbe für den hellen Modus. Unlesbare Werte kommen unverändert zurück.</summary>
    public static string ForLight(string color)
    {
        if (!NotchPalette.IsColor(color))
        {
            return color;
        }
        var (a, r, g, b) = AccentColor.Parse(color);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max - min < GrayChroma)
        {
            // Grau: umkehren. Durchscheinend Weiß (Flächen, Spuren) wird durchscheinend Schwarz, etwas zarter,
            // weil Schwarz auf Hell stärker wirkt als Weiß auf Dunkel.
            var v = (r + g + b) / 3;
            var inverted = 255 - v;
            if (a == 255)
            {
                var gray = (byte)Math.Max(inverted, DarkestText);
                return Hex(255, gray, gray, gray);
            }
            var alpha = v >= 128 ? (byte)Math.Round(a * 0.8) : a;
            return Hex(alpha, (byte)inverted, (byte)inverted, (byte)inverted);
        }

        // Farbe: Ton behalten, so weit abdunkeln, dass sie auf hellem Grund mindestens 4,5 : 1 erreicht.
        var (h, s, value) = AccentColor.ToHsv(r / 255.0, g / 255.0, b / 255.0);
        s = Math.Max(s, 0.55);
        var (lr, lg, lb) = AccentColor.FromHsv(h, s, value);
        var surface = Luminance(AccentColor.Parse(LightSurface));
        while (value > 0.2 && Contrast(surface, Luminance(((byte)255, ToByte(lr), ToByte(lg), ToByte(lb)))) < 4.5)
        {
            value -= 0.02;
            (lr, lg, lb) = AccentColor.FromHsv(h, s, value);
        }
        return Hex(a, ToByte(lr), ToByte(lg), ToByte(lb));
    }

    /// <summary>Kontrastverhältnis nach WCAG zwischen zwei Farben.</summary>
    public static double Contrast(string a, string b) =>
        Contrast(Luminance(AccentColor.Parse(a)), Luminance(AccentColor.Parse(b)));

    private static double Contrast(double la, double lb) => (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);

    private static double Luminance((byte A, byte R, byte G, byte B) c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    private static string Hex(byte a, byte r, byte g, byte b) => a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{a:X2}{r:X2}{g:X2}{b:X2}";
}
