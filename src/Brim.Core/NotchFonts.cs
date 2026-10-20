namespace Brim.Core;

/// <summary>Bereiche der Notch, die je eine eigene Schrift bekommen können.</summary>
public enum FontRole
{
    /// <summary>Uhrzeit in Ruhe und oben rechts im erweiterten Zustand.</summary>
    Clock,
    /// <summary>Tabs „Jetzt · Ablage · System · Plugins“.</summary>
    Menu,
    /// <summary>Titel und große Anzeigen (Datum, Dauer, Songtitel).</summary>
    Headings,
    /// <summary>Fließtext, Untertitel, kleine Beschriftungen.</summary>
    Text,
    /// <summary>Messwerte in Monospace (FPS, Ping, Prozent), damit Ziffern nicht springen.</summary>
    Numbers,
}

/// <summary>Eine eigene Schrift: Kopie in Brims Datenordner und ihr Familienname (aus der Datei gelesen).</summary>
public sealed record CustomFont(string File, string Family);

/// <summary>
/// Schrift je Bereich. Ein Wert ist der Name einer mitgelieferten Schrift (<see cref="FontCatalog.BuiltIn"/>)
/// oder der Familienname einer eigenen; <c>null</c> heißt Standard des Designs.
/// </summary>
public sealed class FontSettings
{
    public string? Clock { get; set; }
    public string? Menu { get; set; }
    public string? Headings { get; set; }
    public string? Text { get; set; }
    public string? Numbers { get; set; }

    /// <summary>Hochgeladene Schriften; jede steht für jeden Bereich zur Wahl.</summary>
    public List<CustomFont> Custom { get; set; } = [];

    public string? Get(FontRole role) => role switch
    {
        FontRole.Clock => Clock,
        FontRole.Menu => Menu,
        FontRole.Headings => Headings,
        FontRole.Text => Text,
        _ => Numbers,
    };

    public void Set(FontRole role, string? font)
    {
        switch (role)
        {
            case FontRole.Clock: Clock = font; break;
            case FontRole.Menu: Menu = font; break;
            case FontRole.Headings: Headings = font; break;
            case FontRole.Text: Text = font; break;
            default: Numbers = font; break;
        }
    }
}

/// <summary>Übersetzt die Wahl je Bereich in die Schriftangabe für XAML (mit Ersatzschriften).</summary>
public static class FontCatalog
{
    public const string Segoe = "Segoe UI", Bahnschrift = "Bahnschrift", Cascadia = "Cascadia Mono";

    /// <summary>Gehören zu Windows 11.</summary>
    public static readonly string[] BuiltIn = [Segoe, Bahnschrift, Cascadia];

    private const string SegoeText = "Segoe UI Variable Text, Segoe UI";
    private const string SegoeDisplay = "Segoe UI Variable Display, Segoe UI";
    private const string Mono = "Cascadia Mono, Consolas";

    /// <summary>Was der Standard des Designs je Bereich ist (für die Anzeige in den Einstellungen).</summary>
    public static string DefaultName(FontRole role) => role is FontRole.Clock or FontRole.Numbers ? Cascadia : Segoe;

    /// <param name="exists">Prüft, ob die Datei einer eigenen Schrift noch da ist.</param>
    public static string Resolve(FontSettings settings, FontRole role, Func<string, bool> exists)
    {
        var choice = settings.Get(role) ?? DefaultName(role);
        switch (choice)
        {
            case Segoe:
                return role == FontRole.Headings ? SegoeDisplay : SegoeText;
            case Bahnschrift:
                return "Bahnschrift, " + SegoeText;
            case Cascadia:
                return Mono;
        }
        if (settings.Custom.FirstOrDefault(c => c.Family == choice) is { } custom && exists(custom.File))
        {
            // Als Pfad und als Datei-URI, weil WinUI je nach Version nur eines davon lädt. Fehlt ein Zeichen in
            // der eigenen Schrift, springt Segoe UI ein.
            return $"{custom.File}#{custom.Family}, {new Uri(custom.File).AbsoluteUri}#{custom.Family}, {SegoeText}";
        }
        // Eigene Schrift gelöscht: wieder der Standard.
        return Resolve(new FontSettings(), role, exists);
    }
}
