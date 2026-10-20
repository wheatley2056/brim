using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brim.Core;

public enum MonitorMode
{
    /// <summary>Nur Hauptmonitor (Standard).</summary>
    Primary,
    /// <summary>Auf jedem Monitor eine Notch.</summary>
    All,
}

public enum AnimationStyle { Spring, EaseOut, Fade, None }

public sealed class AnimationSettings
{
    public AnimationStyle Style { get; set; } = AnimationStyle.Spring;

    /// <summary>Dauer Ausdehnen in ms (150–800).</summary>
    public int ExpandMs { get; set; } = 380;

    /// <summary>Dauer Zurückziehen in ms (120–600).</summary>
    public int RetractMs { get; set; } = 320;

    /// <summary>Dämpfung der Feder (0,5–1,0).</summary>
    public double Damping { get; set; } = 0.8;

    public AnimationSettings Clamped() => new()
    {
        Style = Style,
        ExpandMs = Math.Clamp(ExpandMs, 150, 800),
        RetractMs = Math.Clamp(RetractMs, 120, 600),
        Damping = Math.Clamp(Damping, 0.5, 1.0),
    };
}

public sealed class InteractionSettings
{
    /// <summary>Wie lange die Maus über der Notch ruhen muss, bevor sie sich erweitert.</summary>
    public int HoverExpandDelayMs { get; set; } = 200;

    /// <summary>Verzögerung, bevor sie sich nach dem Verlassen wieder zusammenzieht.</summary>
    public int HoverCollapseDelayMs { get; set; } = 250;

    /// <summary>Globales Tastenkürzel zum Erweitern (Win+Alt+N).</summary>
    public bool HotkeyEnabled { get; set; } = true;
}

public sealed class ClockSettings
{
    public bool ShowSeconds { get; set; }
}

public enum ShelfStorage
{
    /// <summary>Nur den Pfad merken, nichts verschieben (Standard).</summary>
    Reference,
    /// <summary>Immer nach %LocalAppData%\Brim\Shelf kopieren.</summary>
    Copy,
}

public sealed class ShelfSettings
{
    public ShelfStorage Storage { get; set; } = ShelfStorage.Reference;

    /// <summary>Eintrag entfernen, nachdem er erfolgreich herausgezogen wurde.</summary>
    public bool RemoveAfterDragOut { get; set; }
}

public sealed class AutoHideSettings
{
    /// <summary>Zurückziehen, wenn ein normales App-Fenster im Bereich der Notch liegt.</summary>
    public bool WhenOverlapped { get; set; } = true;

    /// <summary>Zurückziehen bei Vollbild (Spiel, Video, F11, Präsentation).</summary>
    public bool WhenFullscreen { get; set; } = true;

    /// <summary>Zurückziehen, wenn ein maximiertes Fenster unter der Notch liegt.</summary>
    public bool WhenMaximized { get; set; } = true;

    /// <summary>„Nur Desktop“: zurückziehen, sobald irgendein App-Fenster auf diesem Monitor im Vordergrund ist.</summary>
    public bool WhenAnyForeground { get; set; }

    /// <summary>Zurückziehen, solange der Bildschirm in Teams, Zoom, Meet usw. geteilt wird (Privatsphäre).</summary>
    public bool WhenScreenSharing { get; set; } = true;

    /// <summary>Merkmale laufender Freigaben (siehe <see cref="ScreenShare.DefaultMarkers"/>).</summary>
    public List<string> ScreenShareMarkers { get; set; } = [.. ScreenShare.DefaultMarkers];

    /// <summary>Nach so vielen Sekunden ohne Eingabe zurückziehen; 0 = aus.</summary>
    public int IdleSeconds { get; set; }

    /// <summary>Verzögerung vor dem Zurückziehen in ms (0–2000); Zurückkommen ist immer sofort.</summary>
    public int HideDelayMs { get; set; }

    /// <summary>Regeln pro App, z. B. OBS immer zeigen.</summary>
    public List<AppRule> Apps { get; set; } = [];

    /// <summary>Zurückziehen, während ein Fenster Richtung Oberkante gezogen wird (Snap-Leiste).</summary>
    public bool WhileDraggingWindows { get; set; } = true;

    /// <summary>Abstand zur Oberkante in DIP, ab dem das Ziehen eines Fensters die Notch versteckt.</summary>
    public int DragZone { get; set; } = 200;

    /// <summary>Maus an die Oberkante holt die versteckte Notch zurück.</summary>
    public bool PeekFromTopEdge { get; set; } = true;

    /// <summary>Verweilzeit an der Oberkante (Plan 4.9: 300 ms).</summary>
    public int PeekDelayMs { get; set; } = 300;
}

public enum ProxyMode
{
    /// <summary>Proxy aus den Windows-Einstellungen (Standard).</summary>
    System,
    /// <summary>Immer direkt verbinden.</summary>
    None,
    /// <summary>Eigener Proxy aus <see cref="ShareSettings.ProxyUrl"/>.</summary>
    Manual,
}

/// <summary>Teilen übers Internet per Code (Magic Wormhole, Plan 4.4a Stufe 2).</summary>
public sealed class ShareSettings
{
    /// <summary>Vermittlungsserver (Mailbox). Standard: öffentlicher Server des Magic-Wormhole-Projekts.</summary>
    public string MailboxUrl { get; set; } = "ws://relay.magic-wormhole.io:4000/v1";

    /// <summary>Weiterleitung, wenn keine direkte Verbindung möglich ist (<c>tcp:host:port</c>).</summary>
    public string TransitRelay { get; set; } = "tcp:transit.magic-wormhole.io:4001";

    public ProxyMode Proxy { get; set; } = ProxyMode.System;

    /// <summary>Bei <see cref="ProxyMode.Manual"/>: <c>http://host:port</c> oder <c>socks5://host:port</c>, optional mit Benutzer:Passwort.</summary>
    public string? ProxyUrl { get; set; }
}

/// <summary>Ein Plugin aus Sicht des Nutzers (Plan 4.2, Sicherheit).</summary>
public sealed class PluginConfig
{
    public bool Enabled { get; set; }

    /// <summary>Rechte, denen der Nutzer beim Einschalten zugestimmt hat.</summary>
    public List<string> Granted { get; set; } = [];

    /// <summary>Werte für die Einstellungen aus dem Manifest.</summary>
    public Dictionary<string, JsonElement> Values { get; set; } = [];
}

public sealed class BatterySettings
{
    /// <summary>Beim Laden Akkustand in der kompakten Notch zeigen (Plan 4.6).</summary>
    public bool CompactWhileCharging { get; set; } = true;

    /// <summary>Darunter zeigt die kompakte Notch den Akkustand und warnt einmal (Prozent).</summary>
    public int LowPercent { get; set; } = 20;

    /// <summary>Darunter klappt sich die Notch mit einem kritischen Hinweis kurz auf (Prozent).</summary>
    public int CriticalPercent { get; set; } = 10;
}

public sealed class UpdateSettings
{
    /// <summary>Automatisch aktualisieren (nur in der installierten Fassung).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Wie oft nach Updates gesehen wird.</summary>
    public int CheckHours { get; set; } = 4;

    /// <summary>So lange ohne Eingabe, bevor Brim für ein Update neu startet.</summary>
    public int IdleMinutes { get; set; } = 3;
}

public sealed class BrimSettings
{
    public UpdateSettings Updates { get; set; } = new();
    public BatterySettings Battery { get; set; } = new();
    public ShareSettings Share { get; set; } = new();
    public AutoHideSettings AutoHide { get; set; } = new();
    public ShelfSettings Shelf { get; set; } = new();
    public MonitorMode Monitors { get; set; } = MonitorMode.Primary;
    public AnimationSettings Animation { get; set; } = new();
    public InteractionSettings Interaction { get; set; } = new();
    public ClockSettings Clock { get; set; } = new();

    /// <summary>Plugins nach Kennung.</summary>
    public Dictionary<string, PluginConfig> Plugins { get; set; } = [];

    /// <summary>Weitere Ordner mit Plugins, z. B. beim Entwickeln (zusätzlich zu %LocalAppData%\Brim\Plugins).</summary>
    public List<string> PluginDirectories { get; set; } = [];

    /// <summary>Module, die der Nutzer abgeschaltet hat (Modul-IDs).</summary>
    public List<string> DisabledModules { get; set; } = [];
}

/// <summary>Lädt und speichert <see cref="BrimSettings"/> als JSON.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public BrimSettings Current { get; private set; } = new();

    public event Action<BrimSettings>? Changed;

    public BrimSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                Current = JsonSerializer.Deserialize<BrimSettings>(File.ReadAllText(_path), Options) ?? new BrimSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Einstellungen unlesbar, nutze Standard: {_path}", ex);
            Current = new BrimSettings();
        }
        return Current;
    }

    public void Save(BrimSettings settings)
    {
        Current = settings;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, _path, overwrite: true);
        Changed?.Invoke(settings);
    }
}
