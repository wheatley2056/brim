namespace Brim.Core.Plugins;

// Protokoll zwischen Brim und dem Messdienst (Brim.exe --telemetry), gleiches Format wie für Plugins.

public static class TelemetryProtocol
{
    /// <summary>Brim → Dienst: nur noch diesen Prozess messen (0 = keinen).</summary>
    public const string Watch = "watch";

    /// <summary>Dienst → Brim: einmal pro Sekunde die Bildrate des beobachteten Prozesses.</summary>
    public const string Frames = "frames";

    /// <summary>Dienst → Brim: ob die Messung läuft oder warum nicht.</summary>
    public const string Status = "status";
}

public sealed record WatchParams(int Pid);

/// <summary><see cref="Stats"/> ist <c>null</c>, solange der Prozess keine Bilder ausgibt.</summary>
public sealed record FramesParams(int Pid, FrameStats? Stats);

public enum TelemetryState
{
    Running,
    /// <summary>Keine Rechte für ETW: Gruppe „Leistungsprotokollbenutzer“ fehlt.</summary>
    Denied,
    Failed,
}

public sealed record StatusParams(TelemetryState State, string? Message = null);
