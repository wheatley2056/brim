using System.Diagnostics;
using System.Security.Principal;

namespace Brim.Telemetry;

/// <summary>
/// Einrichtung, einmalig mit Adminrechten (Plan 4.8): Der Nutzer kommt in die Gruppe
/// „Leistungsprotokollbenutzer“. Danach darf der Messdienst ETW ohne Adminrechte lesen.
/// </summary>
public static class PerformanceLogUsers
{
    public const string Argument = "--telemetry-setup";

    /// <summary>Bekannte SID der Gruppe; ihr Name hängt von der Systemsprache ab.</summary>
    private static readonly SecurityIdentifier Group = new("S-1-5-32-559");

    /// <summary>Ist der aktuelle Nutzer schon Mitglied (nach dem nächsten Anmelden wirksam)?</summary>
    public static bool IsMember()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Groups?.Contains(Group) == true;
    }

    /// <summary>Startet Brim mit Adminrechten (Rückfrage der Benutzerkontensteuerung), um <paramref name="user"/> aufzunehmen.</summary>
    public static async Task<bool> RequestAsync(string brimExe)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(brimExe)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { Argument, user },
            });
            if (process is null)
            {
                return false;
            }
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Rückfrage abgelehnt.
            return false;
        }
    }

    /// <summary>Läuft im erhöhten Prozess: <c>net localgroup &lt;Gruppe&gt; &lt;Nutzer&gt; /add</c>.</summary>
    public static int Add(string user)
    {
        var name = ((NTAccount)Group.Translate(typeof(NTAccount))).Value;
        var group = name[(name.IndexOf('\\') + 1)..];
        using var process = Process.Start(new ProcessStartInfo("net.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "localgroup", group, user, "/add" },
        });
        process!.WaitForExit();
        // 1378: schon Mitglied – auch gut.
        return process.ExitCode is 0 or 2 ? 0 : process.ExitCode;
    }
}
