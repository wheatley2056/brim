using Brim.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Brim.Shell;

/// <summary>
/// Schriften der Notch (Einstellungen → Aussehen). Setzt die Ressourcen, die Widgets beim Zeichnen lesen
/// (<c>NotchTextFont</c>, <c>NotchDisplayFont</c>, <c>NotchMonoFont</c>), und liefert die Schrift der Uhr.
/// Eigene Schriften werden nicht installiert: Brim kopiert die Datei in seinen Datenordner und lädt sie als
/// „Pfad#Familie“.
/// </summary>
internal static class NotchFonts
{
    private const string Text = "Segoe UI Variable Text, Segoe UI";
    private const string Display = "Segoe UI Variable Display, Segoe UI";
    private const string Mono = "Cascadia Mono, Consolas";

    public static string Directory => Path.Combine(App.DataDirectory, "Fonts");

    private static NotchFont _applied = (NotchFont)(-1);
    private static string? _appliedCustom;

    public static (string Text, string Display, string Mono, string Clock) For(AppearanceSettings appearance)
    {
        switch (appearance.Font)
        {
            case NotchFont.Bahnschrift:
                return ("Bahnschrift, Segoe UI", "Bahnschrift, Segoe UI", Mono, "Bahnschrift, Segoe UI");
            case NotchFont.Mono:
                return (Mono, Mono, Mono, Mono);
            case NotchFont.Custom when appearance.CustomFontFile is { } file && File.Exists(file):
                // Als Pfad und als Datei-URI, weil WinUI je nach Version nur eines davon lädt. Fehlt ein Zeichen in
                // der eigenen Schrift, springt Segoe UI ein.
                var family = appearance.CustomFontFamily;
                var custom = $"{file}#{family}, {new Uri(file).AbsoluteUri}#{family}, {Text}";
                return (custom, custom, Mono, custom);
            default:
                return (Text, Display, Mono, Mono);
        }
    }

    /// <summary>Global für alle Notch-Fenster; ändert nur, wenn sich die Wahl geändert hat.</summary>
    public static (FontFamily Text, FontFamily Clock) Apply(AppearanceSettings appearance)
    {
        var fonts = For(appearance);
        var resources = Application.Current.Resources;
        if (_applied != appearance.Font || _appliedCustom != appearance.CustomFontFile)
        {
            _applied = appearance.Font;
            _appliedCustom = appearance.CustomFontFile;
            resources["NotchTextFont"] = new FontFamily(fonts.Text);
            resources["NotchDisplayFont"] = new FontFamily(fonts.Display);
            resources["NotchMonoFont"] = new FontFamily(fonts.Mono);
            Log.Info($"Schrift: {appearance.Font} {appearance.CustomFontFamily}");
        }
        return ((FontFamily)resources["NotchTextFont"], new FontFamily(fonts.Clock));
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
