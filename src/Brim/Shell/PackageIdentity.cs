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

    /// <summary>Ordner, für den das Paket registriert ist (externer Ort), oder <c>null</c>.</summary>
    public static string? RegisteredLocation
    {
        get
        {
            try
            {
                return new PackageManager().FindPackagesForUser("", PackageName, Publisher).FirstOrDefault()?.EffectiveExternalLocation?.Path;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Ist das Paket für einen anderen Ordner registriert als den, aus dem dieses Brim läuft?</summary>
    public static bool RegisteredElsewhere => RegisteredLocation is { } location && !SameFolder(location, AppContext.BaseDirectory);

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Vertraut der PC dem Zertifikat, das dieser Fassung beiliegt?</summary>
    private static bool? CertificateTrusted
    {
        get
        {
            if (!File.Exists(CertificatePath))
            {
                return null;
            }
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(CertificatePath);
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly);
                return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count > 0;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Alles, was für Kalender und Benachrichtigungen zählt, zum Ansehen und Kopieren.</summary>
    public static string Diagnose()
    {
        string YesNo(bool? value) => value switch { true => "ja", false => "nein", null => "unbekannt" };
        string identity;
        try
        {
            identity = Windows.ApplicationModel.Package.Current?.Id.FullName ?? "nein";
        }
        catch (InvalidOperationException)
        {
            identity = "nein";
        }
        var registered = RegisteredLocation;
        var lines = new List<string>
        {
            $"Brim {typeof(PackageIdentity).Assembly.GetName().Version} läuft aus: {AppContext.BaseDirectory}",
            $"Identitätspaket liegt bei (installierte Fassung): {YesNo(Available)}",
            $"Zertifikat vertraut: {YesNo(CertificateTrusted)}",
            $"Paket registriert: {(IsRegistered ? "ja, für " + (registered ?? "unbekannten Ordner") : "nein")}",
            $"Paketidentität in diesem Prozess: {identity}",
            $"Kalender: {Brim.Modules.Sensors.FeatureStatus.Calendar}",
            $"Benachrichtigungen: {Brim.Modules.Sensors.FeatureStatus.Notifications}",
        };
        if (RegisteredElsewhere)
        {
            lines.Add("→ Das Paket gehört zu einem anderen Brim-Ordner; dieses Brim bekommt deshalb keine Identität.");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Registrierung für einen anderen Ordner entfernen und für diesen neu einrichten.</summary>
    public static async Task<string?> ReinstallAsync()
    {
        Remove();
        return await SetupAsync();
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
