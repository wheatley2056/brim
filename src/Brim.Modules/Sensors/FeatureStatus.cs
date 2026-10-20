namespace Brim.Modules.Sensors;

/// <summary>
/// Was Kalender und Benachrichtigungen gerade tun, in Worten für den Nutzer (Einstellungen → Kalender und
/// Benachrichtigungen → Diagnose). Die Module schreiben bei jedem Schritt hinein.
/// </summary>
public static class FeatureStatus
{
    public static string Calendar { get; set; } = "Nicht gestartet (Modul abgeschaltet?)";

    public static string Notifications { get; set; } = "Nicht gestartet (Modul abgeschaltet?)";
}
