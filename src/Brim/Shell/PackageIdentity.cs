using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Brim.Core;
using Windows.Management.Deployment;

namespace Brim.Shell;

/// <summary>
/// Paketidentität über das Identitätspaket (build/identity): Ohne gekaufte Signatur vertraut Windows dem
/// selbst ausgestellten Zertifikat erst, wenn es einmal (mit Adminrechten) unter „Vertrauenswürdige Personen“
/// liegt. Danach registriert Brim das Paket für den Nutzer, mit der Installation als externem Ort.
/// Wirksam ab dem nächsten Start.
/// </summary>
internal static class PackageIdentity
{
    /// <summary>Erhöhter Aufruf: nur das Zertifikat vertrauen.</summary>
    public const string Argument = "--trust-identity";

    private const string PackageName = "Brim.Notch";
    private const string Publisher = "CN=Brim";

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "identity");
    private static string PackagePath => Path.Combine(Folder, "Brim.Identity.msix");
    private static string CertificatePath => Path.Combine(Folder, "Brim.Identity.cer");

    /// <summary>Das Paket liegt bei (nur in der installierten Fassung aus einem Release).</summary>
    public static bool Available => File.Exists(PackagePath) && File.Exists(CertificatePath);

    /// <summary>Läuft dieser Prozess mit Paketidentität?</summary>
    public static bool HasIdentity
    {
        get
        {
            try
            {
                return Windows.ApplicationModel.Package.Current?.Id is not null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static bool IsRegistered
    {
        get
        {
            try
            {
                return new PackageManager().FindPackagesForUser("", PackageName, Publisher).Any();
            }
            catch (Exception ex)
            {
                Log.Info($"Paketregistrierung nicht lesbar: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Im erhöhten Prozess: Zertifikat unter „Vertrauenswürdige Personen“ des Computers ablegen.</summary>
    public static int Trust()
    {
        try
        {
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(X509CertificateLoader.LoadCertificateFromFile(CertificatePath));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <returns><c>null</c> bei Erfolg, sonst ein Grund für den Nutzer.</returns>
    public static async Task<string?> SetupAsync()
    {
        if (!Available)
        {
            return "Nur in der installierten Fassung möglich.";
        }
        try
        {
            using var elevated = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { Argument },
            });
            if (elevated is null)
            {
                return "Brim ließ sich nicht mit Adminrechten starten.";
            }
            await elevated.WaitForExitAsync();
            if (elevated.ExitCode != 0)
            {
                return "Das Zertifikat ließ sich nicht hinterlegen.";
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "Die Rückfrage nach Adminrechten wurde abgelehnt.";
        }

        try
        {
            var result = await new PackageManager().AddPackageByUriAsync(new Uri(PackagePath), new AddPackageOptions
            {
                ExternalLocationUri = new Uri(AppContext.BaseDirectory),
            }).AsTask();
            if (result.ExtendedErrorCode is { } error)
            {
                Log.Warn($"Identitätspaket nicht registriert: {result.ErrorText}", error);
                return result.ErrorText;
            }
            Log.Info("Identitätspaket registriert");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("Identitätspaket nicht registriert", ex);
            return ex.Message;
        }
    }

    /// <summary>Beim Deinstallieren: Registrierung wieder entfernen.</summary>
    public static void Remove()
    {
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser("", PackageName, Publisher))
            {
                manager.RemovePackageAsync(package.Id.FullName).AsTask().Wait(TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Identitätspaket ließ sich nicht entfernen", ex);
        }
    }

    /// <summary>Brim neu starten: der neue Prozess wartet, bis dieser beendet ist (nur eine Instanz).</summary>
    public static void Restart(Action exit)
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            ArgumentList = { "--wait-for", Environment.ProcessId.ToString() },
        })?.Dispose();
        exit();
    }
}
