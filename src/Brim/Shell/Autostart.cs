using Brim.Core;
using Microsoft.Win32;

namespace Brim.Shell;

/// <summary>
/// Start mit Windows für die installierte Fassung (Run-Schlüssel des Nutzers). Zeigt auf den
/// Velopack-Starter im Installationsordner, der auch nach Updates gleich bleibt.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Brim";

    /// <summary>Starter im Installationsordner (<c>…\BrimNotch\Brim.exe</c>), sonst die laufende Datei.</summary>
    private static string LauncherPath()
    {
        var current = AppContext.BaseDirectory.TrimEnd('\\');
        var root = Path.GetDirectoryName(current);
        var stub = root is null ? null : Path.Combine(root, "Brim.exe");
        return stub is not null && File.Exists(stub) ? stub : Environment.ProcessPath ?? Path.Combine(current, "Brim.exe");
    }

    /// <summary>Steht Brim im Run-Schlüssel?</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception ex)
            {
                Log.Warn("Autostart nicht lesbar", ex);
                return false;
            }
        }
    }

    public static void Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, $"\"{LauncherPath()}\"");
        }
        catch (Exception ex)
        {
            Log.Warn("Autostart ließ sich nicht einrichten", ex);
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Autostart ließ sich nicht entfernen", ex);
        }
    }
}
