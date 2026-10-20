namespace Brim.Core;

/// <summary>Regel für eine App (Plan 4.9: „Bestimmte Apps … immer zeigen / immer ausblenden“).</summary>
public enum AppVisibility
{
    /// <summary>Allgemeine Regeln gelten.</summary>
    Default,
    /// <summary>Ist die App im Vordergrund, bleibt die Notch; ihre Fenster verdecken sie nie.</summary>
    AlwaysShow,
    /// <summary>Ist die App im Vordergrund, zieht sich die Notch zurück.</summary>
    AlwaysHide,
}

public sealed class AppRule
{
    /// <summary>Prozessname ohne „.exe“, z. B. <c>obs64</c>; Groß-/Kleinschreibung egal.</summary>
    public string Process { get; set; } = "";

    public AppVisibility Visibility { get; set; }
}

/// <summary>Ein sichtbares App-Fenster auf dem Monitor der Notch, wie es die Regeln sehen.</summary>
public sealed record SceneWindow(string Process, string Class)
{
    /// <summary>Liegt im Bereich der Notch.</summary>
    public bool InZone { get; init; }

    /// <summary>Füllt den ganzen Monitor (Spiel, Video, F11, Präsentation).</summary>
    public bool Fullscreen { get; init; }

    public bool Maximized { get; init; }

    /// <summary>Das Vordergrundfenster, und es liegt auf diesem Monitor.</summary>
    public bool Foreground { get; init; }
}

/// <summary>Alles, was eine Notch für ihre Entscheidung braucht; von der Shell gesammelt.</summary>
public sealed record AutoHideScene
{
    public IReadOnlyList<SceneWindow> Windows { get; init; } = [];

    /// <summary>Ein Fenster wird gerade Richtung Oberkante gezogen (Snap-Leiste).</summary>
    public bool DraggingNearTop { get; init; }

    /// <summary>Eine Bildschirmfreigabe läuft (Teams, Zoom, Meet …), siehe <see cref="ScreenShare"/>.</summary>
    public bool ScreenSharing { get; init; }

    /// <summary>Zeit seit der letzten Eingabe.</summary>
    public TimeSpan Idle { get; init; }
}

public enum AutoHideReason { None, Dragging, ScreenSharing, App, Fullscreen, Maximized, Overlap, Foreground, Idle }

/// <summary>
/// Das Regelwerk aus Plan 4.9 als reine Funktion. Reihenfolge: Ziehen und Bildschirmfreigabe (Privatsphäre)
/// gehen immer vor, dann die App im Vordergrund mit eigener Regel, dann Fensterlage und Leerlauf.
/// </summary>
public static class AutoHideRules
{
    public static AutoHideReason Evaluate(AutoHideSettings settings, AutoHideScene scene)
    {
        if (scene.DraggingNearTop && settings.WhileDraggingWindows)
        {
            return AutoHideReason.Dragging;
        }
        if (scene.ScreenSharing && settings.WhenScreenSharing)
        {
            return AutoHideReason.ScreenSharing;
        }

        var foreground = scene.Windows.FirstOrDefault(w => w.Foreground);
        switch (foreground is null ? AppVisibility.Default : RuleFor(settings, foreground.Process))
        {
            case AppVisibility.AlwaysHide:
                return AutoHideReason.App;
            case AppVisibility.AlwaysShow:
                return AutoHideReason.None;
        }

        // Fenster von Apps mit „immer zeigen“ verdecken die Notch nie, auch im Hintergrund nicht.
        var counted = scene.Windows.Where(w => RuleFor(settings, w.Process) != AppVisibility.AlwaysShow).ToList();
        var inZone = counted.Where(w => w.InZone).ToList();
        if (settings.WhenFullscreen && inZone.Any(w => w.Fullscreen))
        {
            return AutoHideReason.Fullscreen;
        }
        if (settings.WhenMaximized && inZone.Any(w => w.Maximized && !w.Fullscreen))
        {
            return AutoHideReason.Maximized;
        }
        if (settings.WhenOverlapped && inZone.Any(w => !w.Maximized && !w.Fullscreen))
        {
            return AutoHideReason.Overlap;
        }
        if (settings.WhenAnyForeground && counted.Any(w => w.Foreground))
        {
            return AutoHideReason.Foreground;
        }
        if (settings.IdleSeconds > 0 && scene.Idle >= TimeSpan.FromSeconds(settings.IdleSeconds))
        {
            return AutoHideReason.Idle;
        }
        return AutoHideReason.None;
    }

    public static AppVisibility RuleFor(AutoHideSettings settings, string process)
    {
        var name = process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
        foreach (var rule in settings.Apps)
        {
            var ruleName = rule.Process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? rule.Process[..^4] : rule.Process;
            if (string.Equals(ruleName, name, StringComparison.OrdinalIgnoreCase))
            {
                return rule.Visibility;
            }
        }
        return AppVisibility.Default;
    }

    /// <summary>Für das Protokoll.</summary>
    public static string Describe(AutoHideReason reason) => reason switch
    {
        AutoHideReason.Dragging => "Fenster wird gezogen",
        AutoHideReason.ScreenSharing => "Bildschirmfreigabe",
        AutoHideReason.App => "App-Regel",
        AutoHideReason.Fullscreen => "Vollbild",
        AutoHideReason.Maximized => "maximiertes Fenster",
        AutoHideReason.Overlap => "Fenster im Bereich",
        AutoHideReason.Foreground => "Fenster im Vordergrund",
        AutoHideReason.Idle => "Leerlauf",
        _ => "kein Grund",
    };
}

/// <summary>
/// Erkennt laufende Bildschirmfreigaben an den Fenstern, die Konferenz-Apps und Browser dabei öffnen
/// (Freigabeleisten). Windows bietet dafür keine Schnittstelle; die Merkmale stehen deshalb in den
/// Einstellungen und lassen sich ergänzen.
/// </summary>
public static class ScreenShare
{
    /// <summary>Standardmerkmale. <c>class:</c> prüft die Fensterklasse, sonst wird der Titel durchsucht.</summary>
    public static readonly IReadOnlyList<string> DefaultMarkers =
    [
        // Zoom: schwebende Leiste während der Freigabe
        "class:ZPToolBarParentWndClass",
        // Teams (klassisch und neu)
        "Sharing control bar",
        "Freigabesteuerungsleiste",
        // Chrome, Edge (Meet, Teams im Browser, Discord im Browser …)
        "is sharing your screen",
        "is sharing a window",
        "is sharing this tab",
        "gibt Ihren Bildschirm frei",
        "gibt ein Fenster frei",
        "gibt diesen Tab frei",
        // Webex
        "You're sharing",
    ];

    public static bool IsSharing(IEnumerable<(string Class, string Title)> windows, IReadOnlyList<string> markers)
    {
        foreach (var (windowClass, title) in windows)
        {
            foreach (var marker in markers)
            {
                if (marker.StartsWith("class:", StringComparison.OrdinalIgnoreCase)
                    ? string.Equals(windowClass, marker[6..], StringComparison.Ordinal)
                    : marker.Length > 0 && title.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
