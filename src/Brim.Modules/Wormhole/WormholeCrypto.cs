using System.Security.Cryptography;
using System.Text;

namespace Brim.Modules.Wormhole;

/// <summary>Schlüsselableitung und Verschlüsselung wie in magic-wormhole (<c>wormhole/_key.py</c>).</summary>
internal static class WormholeCrypto
{
    /// <summary>HKDF-SHA256 ohne Salt mit <paramref name="purpose"/> als Info.</summary>
    public static byte[] DeriveKey(byte[] key, byte[] purpose, int length = Sodium.SecretBoxKeyBytes) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, length, salt: [], info: purpose);

    public static byte[] DeriveKey(byte[] key, string purpose, int length = Sodium.SecretBoxKeyBytes) =>
        DeriveKey(key, Encoding.ASCII.GetBytes(purpose), length);

    /// <summary>Schlüssel je Absender und Phase: <c>"wormhole:phase:" + sha256(side) + sha256(phase)</c>.</summary>
    public static byte[] DerivePhaseKey(byte[] key, string side, string phase)
    {
        byte[] purpose = [.. "wormhole:phase:"u8, .. SHA256.HashData(Encoding.ASCII.GetBytes(side)), .. SHA256.HashData(Encoding.ASCII.GetBytes(phase))];
        return DeriveKey(key, purpose);
    }

    /// <summary>SecretBox mit zufälliger Nonce; Ergebnis <c>Nonce + MAC + Geheimtext</c> (PyNaCl <c>SecretBox.encrypt</c>).</summary>
    public static byte[] Encrypt(byte[] key, byte[] plaintext, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(Sodium.SecretBoxNonceBytes);
        return [.. nonce, .. Sodium.SecretBox(plaintext, nonce, key)];
    }

    public static byte[] Decrypt(byte[] key, byte[] data)
    {
        if (data.Length < Sodium.SecretBoxNonceBytes + Sodium.SecretBoxMacBytes)
        {
            throw new CryptographicException("Verschlüsselte Nachricht zu kurz");
        }
        return Sodium.SecretBoxOpen(data.AsSpan(Sodium.SecretBoxNonceBytes), data.AsSpan(0, Sodium.SecretBoxNonceBytes), key);
    }
}
