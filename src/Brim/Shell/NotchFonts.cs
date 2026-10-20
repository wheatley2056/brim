using Brim.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Brim.Shell;

/// <summary>
/// Schriften der Notch je Bereich (Einstellungen → Aussehen → Schriften). Setzt die Ressourcen, die Widgets beim
/// Zeichnen lesen (<c>NotchTextFont</c>, <c>NotchTitleFont</c>, <c>NotchDisplayFont</c>, <c>NotchMonoFont</c>, <c>NotchClockFont</c>),
/// und liefert die Schriften für Menü und Uhr.
/// Eigene Schriften werden nicht installiert: Brim kopiert die Datei in seinen Datenordner und lädt sie als
/// „Pfad#Familie“.
/// </summary>
internal static class NotchFonts
{
    public static string Directory => Path.Combine(App.DataDirectory, "Fonts");

    private static string? _applied;

    /// <summary>Global für alle Notch-Fenster; ändert nur, wenn sich die Wahl geändert hat.</summary>
    public static (FontFamily Text, FontFamily Menu, FontFamily Clock) Apply(AppearanceSettings appearance)
    {
        var fonts = appearance.Fonts;
        string Of(FontRole role) => FontCatalog.Resolve(fonts, role, File.Exists);
        var resources = Application.Current.Resources;
        var key = string.Join("|", Enum.GetValues<FontRole>().Select(Of));
        if (_applied != key)
        {
            _applied = key;
            resources["NotchTextFont"] = new FontFamily(Of(FontRole.Text));
            resources["NotchTitleFont"] = new FontFamily(Of(FontRole.Headings));
            resources["NotchDisplayFont"] = new FontFamily(Of(FontRole.Headings));
            resources["NotchMonoFont"] = new FontFamily(Of(FontRole.Numbers));
            // Werte neben der Uhr (TextStyle.Status) stehen in derselben Schrift wie die Uhr.
            resources["NotchClockFont"] = new FontFamily(Of(FontRole.Clock));
            Log.Info($"Schriften: Uhr {fonts.Clock}, Menü {fonts.Menu}, Überschriften {fonts.Headings}, Text {fonts.Text}, Zahlen {fonts.Numbers}");
        }
        return ((FontFamily)resources["NotchTextFont"], new FontFamily(Of(FontRole.Menu)), (FontFamily)resources["NotchClockFont"]);
    }

    /// <summary>
    /// Übernimmt eine Schriftdatei: prüfen, Familienname lesen, nach <see cref="Directory"/> kopieren.
    /// </summary>
    /// <exception cref="InvalidDataException">Keine lesbare Schrift.</exception>
    public static (string File, string Family) Import(string source)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (!FontFile.Extensions.Contains(extension))
        {
            throw new InvalidDataException("Nur .ttf, .otf oder .ttc");
        }
        var bytes = File.ReadAllBytes(source);
        var family = FontFile.FamilyName(bytes) ?? throw new InvalidDataException("Keine Schrift erkannt");
        System.IO.Directory.CreateDirectory(Directory);
        var target = Path.Combine(Directory, Path.GetFileName(source));
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllBytes(target, bytes);
        }
        return (target, family);
    }
}
