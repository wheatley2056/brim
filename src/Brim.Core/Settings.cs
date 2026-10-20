using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brim.Core;

public enum MonitorMode
{
    /// <summary>Nur Hauptmonitor (Standard).</summary>
    Primary,
    /// <summary>Auf jedem Monitor eine Notch.</summary>
    All,
    /// <summary>Eine Notch, die dem Mauszeiger auf den Monitor folgt.</summary>
    Mouse,
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

/// <summary>Material des Körpers: Schwarz oder eines der Windows-Materialien, das durch die Notch scheint.</summary>
public enum NotchMaterial
{
    Black,
    /// <summary>Acryl: der Desktop dahinter, weichgezeichnet.</summary>
    Acrylic,
    /// <summary>Mica: getönt aus dem Hintergrundbild (Windows 11).</summary>
    Mica,
    /// <summary>Mica Alt: kräftiger getönt, wie Registerkarten in Windows 11.</summary>
    MicaAlt,
}

/// <summary>Aussehen der Notch (Personalisierung, Plan 7h).</summary>
public sealed class AppearanceSettings
{
    public NotchForm Form { get; set; } = NotchForm.Notch;

    /// <summary>Faktor auf alle Zustände (0,8–1,3).</summary>
    public double Size { get; set; } = 1.0;

    public NotchMaterial Material { get; set; } = NotchMaterial.Black;

    /// <summary>Schrift je Bereich (Einstellungen → Aussehen → Schriften).</summary>
    public FontSettings Fonts { get; set; } = new();

    public AppearanceSettings Clamped() => new()
    {
        Form = Enum.IsDefined(Form) ? Form : NotchForm.Notch,
        Material = Enum.IsDefined(Material) ? Material : NotchMaterial.Black,
        Fonts = Fonts ?? new(),
        Size = double.IsFinite(Size) ? Math.Clamp(Size, NotchGeometry.MinSize, NotchGeometry.MaxSize) : 1.0,
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

public enum GameOverlay
{
    /// <summary>Notch verschwindet im Spiel komplett (Standard).</summary>
    Off,
    /// <summary>Schmale Pille mit FPS · ms bleibt, halbtransparent.</summary>
    Mini,
    /// <summary>Unsichtbar; Win+Alt+F blendet FPS für 5 s ein.</summary>
    OnDemand,
}

/// <summary>FPS und Latenz in Spielen (Plan 4.8).</summary>
public sealed class GameSettings
{
    /// <summary>FPS messen (startet den Messdienst; braucht einmalig die Gruppe „Leistungsprotokollbenutzer“).</summary>
    public bool Enabled { get; set; }

    public GameOverlay Overlay { get; set; } = GameOverlay.Off;

    /// <summary>Server für die Netzwerklatenz (Ping), z. B. ein Spielserver; leer = aus.</summary>
    public string PingHost { get; set; } = "";
}

/// <summary>Wie neue Benachrichtigungen in der ruhenden Notch erscheinen.</summary>
public enum NotificationStyle
{
    /// <summary>Kurz als Vorschau mit App, Titel und Text.</summary>
    Preview,
    /// <summary>Die Uhr bleibt; nur ein blauer Punkt, bis die Notch aufgeklappt wird.</summary>
    Dot,
}

/// <summary>Uhr in der ruhenden Notch.</summary>
public enum ClockAlignment { Center, Left, Right }

/// <summary>Datum neben der Uhr.</summary>
public enum DateFormat
{
    /// <summary>„Di 20.“</summary>
    WeekdayDay,
    /// <summary>„Di 20. Okt.“</summary>
    WeekdayDayMonth,
    /// <summary>„20.10.“</summary>
    DayMonth,
}

/// <summary>Einheit der Netzwerkrate neben der Uhr.</summary>
public enum NetworkUnit
{
    /// <summary>Bit pro Sekunde mit Basis 1000, wie der Task-Manager („12,4 Mbit/s“).</summary>
    Bits,
    /// <summary>Byte pro Sekunde mit Basis 1024, wie Downloads im Browser („1,50 MB/s“).</summary>
    Bytes,
}

public static class DateFormats
{
    /// <summary>Muster für <see cref="DateTime.ToString(string, IFormatProvider)"/>.</summary>
    public static string Pattern(DateFormat format) => format switch
    {
        DateFormat.WeekdayDayMonth => "ddd d. MMM",
        DateFormat.DayMonth => "d.M.",
        _ => "ddd d.",
    };
}

/// <summary>Was die ruhende Notch zeigt (Einstellungen → Aussehen → In der Notch).</summary>
public sealed class NotchDisplaySettings
{
    public ClockAlignment Clock { get; set; } = ClockAlignment.Center;

    /// <summary>Bei mittiger Uhr: den ganzen Inhalt zentrieren statt der Uhr, wenn eine Seite länger ist.</summary>
    public bool CenterContent { get; set; }

    public bool Load { get; set; }
    public bool Date { get; set; }
    public DateFormat DateFormat { get; set; } = DateFormat.WeekdayDay;
    public bool Battery { get; set; }
    public bool Network { get; set; }
    public NetworkUnit NetworkUnit { get; set; } = NetworkUnit.Bits;

    /// <summary>Statuswerte im Hub (Modul/Kennung), die gerade eingeschaltet sind, in fester Reihenfolge.</summary>
    public IReadOnlyList<string> EnabledStatus()
    {
        var keys = new List<string>();
        if (Date)
        {
            keys.Add(StatusKeys.Date);
        }
        if (Load)
        {
            keys.Add(StatusKeys.Load);
        }
        if (Network)
        {
            keys.Add(StatusKeys.Network);
        }
        if (Battery)
        {
            keys.Add(StatusKeys.Battery);
        }
        // Der Punkt für Benachrichtigungen erscheint immer, wenn es ihn gibt.
        keys.Add(StatusKeys.NotificationDot);
        return keys;
    }
}

/// <summary>Kennungen der Statuswerte für die ruhende Notch.</summary>
public static class StatusKeys
{
    public const string Date = "clock/date";
    public const string Load = "resources/load";
    public const string Network = "resources/network";
    public const string Battery = "battery/status";
    public const string NotificationDot = "notifications/dot";
}

public sealed class NotificationSettings
{
    public NotificationStyle Style { get; set; } = NotificationStyle.Preview;

    /// <summary>Apps (Anzeigename wie in den Windows-Benachrichtigungen), deren Benachrichtigungen die Notch nicht zeigt.</summary>
    public List<string> MutedApps { get; set; } = [];

    /// <summary>
    /// Bei Präsentation, Vollbild und „Nicht stören“ keine Vorschau, nur der blaue Punkt – wie Windows selbst.
    /// </summary>
    public bool QuietWhenBusy { get; set; } = true;
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
    public GameSettings Games { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public ShareSettings Share { get; set; } = new();
    public AutoHideSettings AutoHide { get; set; } = new();
    public ShelfSettings Shelf { get; set; } = new();
    public MonitorMode Monitors { get; set; } = MonitorMode.Primary;
    public AnimationSettings Animation { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public NowSettings Now { get; set; } = new();
    public NotchDisplaySettings Display { get; set; } = new();
    public InteractionSettings Interaction { get; set; } = new();
    public ClockSettings Clock { get; set; } = new();

    /// <summary>Plugins nach Kennung.</summary>
    public Dictionary<string, PluginConfig> Plugins { get; set; } = [];

    /// <summary>Weitere Ordner mit Plugins, z. B. beim Entwickeln (zusätzlich zu %LocalAppData%\Brim\Plugins).</summary>
    public List<string> PluginDirectories { get; set; } = [];

    /// <summary>
    /// Einführung (Intro, Kurzanleitung, Einrichtung) gesehen. Neu installiert ist es <c>false</c>; wer schon
    /// Einstellungen hat, gilt als eingeführt (siehe <see cref="Onboarding.ShouldRun"/>).
    /// </summary>
    public bool Onboarded { get; set; }

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

    /// <summary>Gab es beim Laden schon eine Einstellungsdatei? Wenn nicht, startet Brim zum ersten Mal.</summary>
    public bool Existed { get; private set; }

    public BrimSettings Load()
    {
        try
        {
            Existed = File.Exists(_path);
            if (Existed)
            {
                Current = JsonSerializer.Deserialize<BrimSettings>(File.ReadAllText(_path), Options) ?? new BrimSettings();
            }
            Current.Now.Upgrade();
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

/// <summary>Wann die Einführung läuft.</summary>
public static class Onboarding
{
    /// <summary>Nur beim ersten Start einer frischen Installation, nicht nach einem Update.</summary>
    public static bool ShouldRun(BrimSettings settings, bool settingsExisted)
    {
        if (settingsExisted && !settings.Onboarded)
        {
            // Bestehende Installation (Update von vor der Einführung): nicht nachträglich zeigen.
            settings.Onboarded = true;
        }
        return !settings.Onboarded;
    }
}
