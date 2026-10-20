using Brim.Core.Widgets;

namespace Brim.Core;

/// <summary>Priorität einer Aktivität (Plan 3, Zustandsmaschine).</summary>
public enum ActivityPriority
{
    /// <summary>Ruhezustand, z. B. Uhrzeit.</summary>
    Idle = 0,
    /// <summary>Laufend: Musik, Timer, Aufnahme. Kompakt, links und rechts je ein Slot.</summary>
    Ongoing = 1,
    /// <summary>Vorübergehend: Lautstärke, Helligkeit, Ladekabel. HUD für kurze Zeit.</summary>
    Transient = 2,
    /// <summary>Kritisch: Akku fast leer, Timer abgelaufen. Erweitert sich kurz von selbst.</summary>
    Critical = 3,
}

/// <summary>
/// Eine Aktivität, die ein Modul meldet. Eingebaute Module und Plugins nutzen dieselbe Form.
/// </summary>
public sealed record Activity
{
    public required string ModuleId { get; init; }
    public required string Id { get; init; }
    public ActivityPriority Priority { get; init; } = ActivityPriority.Ongoing;

    /// <summary>Linker Slot im kompakten Zustand.</summary>
    public Widget? Leading { get; init; }

    /// <summary>Rechter Slot im kompakten Zustand.</summary>
    public Widget? Trailing { get; init; }

    /// <summary>Vollständiger Inhalt (HUD-Zeile oder Inhalt im Tab „Jetzt“). Ersetzt Leading/Trailing, wenn gesetzt und der Zustand ihn nutzt.</summary>
    public Widget? Content { get; init; }

    /// <summary>Großer Inhalt für den erweiterten Zustand (Tab „Jetzt“).</summary>
    public Widget? Expanded { get; init; }

    /// <summary>Inhalt, wenn die Aktivität als Perle neben der Notch erscheint (zweite Aktivität).</summary>
    public Widget? Bead { get; init; }

    /// <summary>Akzentfarbe (Hex), z. B. aus dem Cover. Färbt den Schimmer am unteren Rand.</summary>
    public string? Accent { get; init; }

    /// <summary>
    /// Pegel 0–1, mit dem der Schimmer pulsiert, z. B. die Stimme im Gespräch. <c>null</c> lässt ihn ruhig leuchten.
    /// </summary>
    public double? Glow { get; init; }

    /// <summary>Anzeigedauer für vorübergehende und kritische Aktivitäten.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Höherer Rang gewinnt unter gleich priorisierten laufenden Aktivitäten.</summary>
    public int Rank { get; init; }

    /// <summary>
    /// Läuft im Hintergrund: beansprucht die kompakte Notch nicht, bleibt aber im erweiterten
    /// Zustand erreichbar (z. B. pausierte Musik).
    /// </summary>
    public bool IsBackground { get; init; }

    /// <summary>
    /// Bleibt als kleine Pille sichtbar, auch wenn sich die Notch zurückgezogen hat (Spielmodus „Mini“, Plan 4.8).
    /// Zeigt <see cref="Content"/>.
    /// </summary>
    public bool IsOverlay { get; init; }

    /// <summary>
    /// Vorübergehend als breiteres, zweizeiliges HUD (<see cref="NotchGeometry.HudWide"/>), z. B. Benachrichtigungen
    /// mit Titel und Text.
    /// </summary>
    public bool Wide { get; init; }

    /// <summary>Text für die Sprachausgabe, wenn die Aktivität erscheint.</summary>
    public string? Announcement { get; init; }

    public string Key => ActivityKey.Of(ModuleId, Id);
}

public static class ActivityKey
{
    public static string Of(string moduleId, string id) => moduleId + "/" + id;
}

/// <summary>Eine laufende Aktivität mit Zeitstempeln, wie sie die Zustandsmaschine sieht.</summary>
public sealed record ActiveActivity(Activity Activity, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ExpiresAt, long Sequence)
{
    public string Key => Activity.Key;
    public ActivityPriority Priority => Activity.Priority;
}

/// <summary>Eine Aktion aus der Oberfläche (Knopf, Fortschrittsleiste) an ein Modul.</summary>
public readonly record struct ActionInvocation(string ModuleId, string ActionId, double? Value = null);
