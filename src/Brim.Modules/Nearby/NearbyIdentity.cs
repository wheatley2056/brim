using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Brim.Core;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace Brim.Modules.Nearby;

/// <summary>
/// Wer dieses Brim ist: feste Kennung, Anzeigename und ein selbstsigniertes Zertifikat für TLS.
/// Der Fingerabdruck des Zertifikats wird bei der Suche mit angekündigt; die Gegenseite prüft ihn
/// beim Verbinden, damit sich niemand im LAN dazwischenschalten kann.
/// </summary>
public sealed class NearbyIdentity
{
    private NearbyIdentity(string id, string name, X509Certificate2 certificate)
    {
        Id = id;
        Name = name;
        Certificate = certificate;
        Fingerprint = FingerprintOf(certificate);
    }

    public string Id { get; }
    public string Name { get; }
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

    public static string FingerprintOf(X509Certificate certificate) =>
        Convert.ToBase64String(SHA256.HashData(certificate.GetRawCertData())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Lädt die Identität oder legt sie beim ersten Start an (geschützt mit DPAPI für diesen Nutzer).</summary>
    public static async Task<NearbyIdentity> LoadOrCreateAsync(string directory, string? displayName = null)
    {
        var file = Path.Combine(directory, "identity.bin");
        var name = displayName ?? DefaultName();
        if (File.Exists(file))
        {
            try
            {
                var protectedBytes = await File.ReadAllBytesAsync(file);
                var plain = await new DataProtectionProvider().UnprotectAsync(CryptographicBuffer.CreateFromByteArray(protectedBytes));
                CryptographicBuffer.CopyToByteArray(plain, out var pfx);
                var loaded = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
                return new NearbyIdentity(loaded.Subject.Replace("CN=", ""), name, loaded);
            }
            catch (Exception ex)
            {
                Log.Warn("Identität unlesbar, lege eine neue an", ex);
            }
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + id, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false)); // Server- und Client-Auth
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        var bytes = created.Export(X509ContentType.Pfx);

        // SChannel braucht einen persistierbaren Schlüssel; deshalb über PFX neu laden statt das Objekt zu nutzen.
        var certificate = X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.UserKeySet);
        Directory.CreateDirectory(directory);
        var sealedBytes = await new DataProtectionProvider("LOCAL=user").ProtectAsync(CryptographicBuffer.CreateFromByteArray(bytes));
        CryptographicBuffer.CopyToByteArray(sealedBytes, out var stored);
        await File.WriteAllBytesAsync(file, stored);
        Log.Info($"Neue Brim-Identität {id}");
        return new NearbyIdentity(id, name, certificate);
    }

    private static string DefaultName()
    {
        var user = Environment.UserName;
        return string.IsNullOrWhiteSpace(user) ? Environment.MachineName : $"{user} ({Environment.MachineName})";
    }
}
