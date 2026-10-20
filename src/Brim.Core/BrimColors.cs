namespace Brim.Core;

/// <summary>Farben aus dem Design (design/boards/1-identity.html).</summary>
public static class BrimColors
{
    /// <summary>Körper der Notch.</summary>
    public const string Obsidian = "#000000";
    /// <summary>Flächen, Karten.</summary>
    public const string Graphite = "#1C1D21";
    /// <summary>Tab-Leiste, Hintergrund der Pillen.</summary>
    public const string TabTrack = "#121316";
    /// <summary>Aktiver Tab, Spur von Fortschrittsleisten.</summary>
    public const string Track = "#2A2B30";
    /// <summary>Text.</summary>
    public const string Fog = "#F2F3F5";
    /// <summary>Sekundärtext (7,8:1 auf Schwarz).</summary>
    public const string Slate = "#9A9EA8";
    /// <summary>Laden, Erfolg.</summary>
    public const string Charge = "#5BD98A";
    /// <summary>Akku lädt (mit Blitz).</summary>
    public const string Charging = "#4FA3FF";
    /// <summary>Warnung, Timer.</summary>
    public const string Warning = "#FFB547";
    /// <summary>Kamera in Benutzung (wie der grüne Punkt am Mac).</summary>
    public const string Camera = Charge;
    /// <summary>Mikrofon in Benutzung (orange).</summary>
    public const string Microphone = Warning;
    /// <summary>Kritisch, z. B. Akku fast leer.</summary>
    public const string Danger = "#FF5C5C";
    /// <summary>Verlauf CPU.</summary>
    public const string Cpu = "#4FC3E8";
    /// <summary>Arbeitsspeicher.</summary>
    public const string Memory = "#8C7BFF";
    /// <summary>Verlauf GPU.</summary>
    public const string Gpu = Charge;
    /// <summary>Standardakzent, wenn kein Cover eine Farbe liefert.</summary>
    public const string DefaultAccent = "#FF8A66";
}
