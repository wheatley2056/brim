using System.Security.Cryptography;

namespace Brim.Modules.Wormhole;

/// <summary>
/// SPAKE2 in der symmetrischen Variante auf Ed25519, bitgenau wie python-spake2 0.9
/// (<c>SPAKE2_Symmetric</c>), das Magic Wormhole verwendet. Beide Seiten leiten aus dem kurzen Code
/// denselben 32-Byte-Schlüssel ab; wer den Code nicht kennt, erfährt nichts.
/// </summary>
internal sealed class Spake2
{
    /// <summary>Kennbyte der symmetrischen Seite ('S').</summary>
    private const byte SideSymmetric = 0x53;

    /// <summary>
    /// Punkt S = arbitrary_element(b"symmetric") aus python-spake2 (Hash-auf-Kurve-Verfahren mit unbekanntem
    /// diskretem Logarithmus). Aus der Referenz-Implementierung übernommen und per Test geprüft.
    /// </summary>
    private static readonly byte[] S = Convert.FromHexString("6f00dae87c1be1a73b5922ef431cd8f57879569c222d22b1cd71e8546ab8e6f1");

    private readonly byte[] _password;
    private readonly byte[] _id;
    private readonly byte[] _pwScalar;
    private readonly byte[] _xyScalar;
    private byte[]? _outbound;

    public Spake2(byte[] password, byte[] idSymmetric, byte[]? xyScalar = null)
    {
        _password = password;
        _id = idSymmetric;
        _pwScalar = PasswordToScalar(password);
        _xyScalar = xyScalar ?? Sodium.RandomScalar();
    }

    /// <summary>
    /// Passwort zu Skalar wie python-spake2: HKDF-SHA256 (leeres Salt, Info „SPAKE2 pw“) auf 48 Bytes,
    /// als Big-Endian-Zahl gelesen und modulo L reduziert.
    /// </summary>
    public static byte[] PasswordToScalar(byte[] password)
    {
        var expanded = HKDF.DeriveKey(HashAlgorithmName.SHA256, password, 48, salt: [], info: "SPAKE2 pw"u8.ToArray());
        var wide = new byte[64];
        for (var i = 0; i < expanded.Length; i++)
        {
            wide[i] = expanded[expanded.Length - 1 - i]; // Big Endian → Little Endian
        }
        return Sodium.Reduce(wide);
    }

    /// <summary>Erste Nachricht: <c>'S' + (xy·B + pw·S)</c>.</summary>
    public byte[] Start()
    {
        var element = Sodium.Add(Sodium.ScalarMultBase(_xyScalar), Sodium.ScalarMult(_pwScalar, S));
        _outbound = element;
        return [SideSymmetric, .. element];
    }

    /// <summary>Verarbeitet die Nachricht der Gegenseite und liefert den gemeinsamen Schlüssel.</summary>
    public byte[] Finish(byte[] inbound)
    {
        if (_outbound is null)
        {
            throw new InvalidOperationException("Start() zuerst aufrufen");
        }
        if (inbound.Length != 33 || inbound[0] != SideSymmetric)
        {
            throw new CryptographicException("Unerwartete SPAKE2-Nachricht");
        }
        var element = inbound[1..];
        if (!Sodium.IsValidPoint(element))
        {
            throw new CryptographicException("Ungültiger Punkt in der SPAKE2-Nachricht");
        }
        if (element.AsSpan().SequenceEqual(_outbound))
        {
            throw new CryptographicException("Eigene Nachricht zurückgespiegelt");
        }

        // K = (Y − pw·S)·xy
        var unblinded = Sodium.Add(element, Sodium.ScalarMult(Sodium.Negate(_pwScalar), S));
        var k = Sodium.ScalarMult(_xyScalar, unblinded);

        // Transkript: Nachrichten sortiert, damit beide Seiten dasselbe hashen.
        var (first, second) = element.AsSpan().SequenceCompareTo(_outbound) < 0 ? (element, _outbound) : (_outbound, element);
        byte[] transcript = [.. SHA256.HashData(_password), .. SHA256.HashData(_id), .. first, .. second, .. k];
        return SHA256.HashData(transcript);
    }
}
