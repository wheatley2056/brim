using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Brim.Modules.Wormhole;

/// <summary>
/// Die wenigen libsodium-Funktionen, die Magic Wormhole braucht: NaCl SecretBox (XSalsa20-Poly1305)
/// und Ed25519-Punktrechnung für SPAKE2. libsodium kommt aus dem offiziellen NuGet-Paket „libsodium“.
/// </summary>
internal static unsafe partial class Sodium
{
    private const string Library = "libsodium";

    public const int SecretBoxKeyBytes = 32;
    public const int SecretBoxNonceBytes = 24;
    public const int SecretBoxMacBytes = 16;
    public const int PointBytes = 32;
    public const int ScalarBytes = 32;

    static Sodium()
    {
        if (sodium_init() < 0)
        {
            throw new InvalidOperationException("libsodium ließ sich nicht initialisieren");
        }
    }

    [LibraryImport(Library)] private static partial int sodium_init();
    [LibraryImport(Library)] private static partial int crypto_secretbox_easy(byte* c, byte* m, ulong mlen, byte* n, byte* k);
    [LibraryImport(Library)] private static partial int crypto_secretbox_open_easy(byte* m, byte* c, ulong clen, byte* n, byte* k);
    [LibraryImport(Library)] private static partial int crypto_scalarmult_ed25519_base_noclamp(byte* q, byte* n);
    [LibraryImport(Library)] private static partial int crypto_scalarmult_ed25519_noclamp(byte* q, byte* n, byte* p);
    [LibraryImport(Library)] private static partial int crypto_core_ed25519_add(byte* r, byte* p, byte* q);
    [LibraryImport(Library)] private static partial int crypto_core_ed25519_is_valid_point(byte* p);
    [LibraryImport(Library)] private static partial void crypto_core_ed25519_scalar_negate(byte* neg, byte* s);
    [LibraryImport(Library)] private static partial void crypto_core_ed25519_scalar_reduce(byte* r, byte* s);
    [LibraryImport(Library)] private static partial void crypto_core_ed25519_scalar_random(byte* r);

    // ───────── SecretBox ─────────

    /// <summary>Verschlüsselt; Ergebnis ist <c>MAC (16) + Geheimtext</c>, wie NaCl <c>secretbox_easy</c>.</summary>
    public static byte[] SecretBox(ReadOnlySpan<byte> message, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> key)
    {
        Check(nonce.Length == SecretBoxNonceBytes && key.Length == SecretBoxKeyBytes, "Schlüssel oder Nonce falsch");
        var output = new byte[message.Length + SecretBoxMacBytes];
        fixed (byte* c = output, m = message, n = nonce, k = key)
        {
            Check(crypto_secretbox_easy(c, m, (ulong)message.Length, n, k) == 0, "Verschlüsseln fehlgeschlagen");
        }
        return output;
    }

    /// <summary>Entschlüsselt <c>MAC + Geheimtext</c>; wirft bei falschem Schlüssel oder veränderten Daten.</summary>
    public static byte[] SecretBoxOpen(ReadOnlySpan<byte> box, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> key)
    {
        Check(nonce.Length == SecretBoxNonceBytes && key.Length == SecretBoxKeyBytes, "Schlüssel oder Nonce falsch");
        if (box.Length < SecretBoxMacBytes)
        {
            throw new CryptographicException("Verschlüsselte Daten zu kurz");
        }
        var output = new byte[box.Length - SecretBoxMacBytes];
        fixed (byte* m = output, c = box, n = nonce, k = key)
        {
            if (crypto_secretbox_open_easy(m, c, (ulong)box.Length, n, k) != 0)
            {
                throw new CryptographicException("Entschlüsseln fehlgeschlagen (falscher Schlüssel oder verändert)");
            }
        }
        return output;
    }

    // ───────── Ed25519 ─────────

    public static byte[] ScalarMultBase(ReadOnlySpan<byte> scalar)
    {
        var q = new byte[PointBytes];
        fixed (byte* pq = q, n = scalar)
        {
            Check(crypto_scalarmult_ed25519_base_noclamp(pq, n) == 0, "Skalarmultiplikation fehlgeschlagen");
        }
        return q;
    }

    public static byte[] ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> point)
    {
        var q = new byte[PointBytes];
        fixed (byte* pq = q, n = scalar, p = point)
        {
            Check(crypto_scalarmult_ed25519_noclamp(pq, n, p) == 0, "Skalarmultiplikation fehlgeschlagen");
        }
        return q;
    }

    public static byte[] Add(ReadOnlySpan<byte> p, ReadOnlySpan<byte> q)
    {
        var r = new byte[PointBytes];
        fixed (byte* pr = r, pp = p, pq = q)
        {
            Check(crypto_core_ed25519_add(pr, pp, pq) == 0, "Punktaddition fehlgeschlagen");
        }
        return r;
    }

    /// <summary>Gültiger Punkt der Hauptuntergruppe, kanonisch kodiert, nicht von kleiner Ordnung.</summary>
    public static bool IsValidPoint(ReadOnlySpan<byte> p)
    {
        if (p.Length != PointBytes)
        {
            return false;
        }
        fixed (byte* pp = p)
        {
            return crypto_core_ed25519_is_valid_point(pp) == 1;
        }
    }

    public static byte[] Negate(ReadOnlySpan<byte> scalar)
    {
        var r = new byte[ScalarBytes];
        fixed (byte* pr = r, s = scalar)
        {
            crypto_core_ed25519_scalar_negate(pr, s);
        }
        return r;
    }

    /// <summary>Reduziert eine 64-Byte-Zahl (Little Endian) modulo der Gruppenordnung L.</summary>
    public static byte[] Reduce(ReadOnlySpan<byte> wide)
    {
        Check(wide.Length == 64, "Reduktion braucht 64 Bytes");
        var r = new byte[ScalarBytes];
        fixed (byte* pr = r, s = wide)
        {
            crypto_core_ed25519_scalar_reduce(pr, s);
        }
        return r;
    }

    public static byte[] RandomScalar()
    {
        var r = new byte[ScalarBytes];
        fixed (byte* pr = r)
        {
            crypto_core_ed25519_scalar_random(pr);
        }
        return r;
    }

    private static void Check(bool ok, string message)
    {
        if (!ok)
        {
            throw new CryptographicException(message);
        }
    }
}
