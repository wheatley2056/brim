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

public sealed class BrimSettings
{
    public MonitorMode Monitors { get; set; } = MonitorMode.Primary;
    public AnimationSettings Animation { get; set; } = new();
    public InteractionSettings Interaction { get; set; } = new();
    public ClockSettings Clock { get; set; } = new();

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
