using System.Text;
using Brim.Modules.Wormhole;

namespace Brim.Modules.Tests;

/// <summary>
/// Vektoren aus der Referenz-Implementierung (python-spake2 0.9, magic-wormhole 0.24, PyNaCl),
/// erzeugt mit festen Zufallswerten. Stimmen sie, versteht Brim das Original.
/// </summary>
public class WormholeCryptoTests
{
    private static readonly byte[] Code = "7-guitarist-revenge"u8.ToArray();
    private static readonly byte[] AppId = "lothar.com/wormhole/text-or-file-xfer"u8.ToArray();

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void Password_scalar_matches_reference()
    {
        Assert.Equal("88ae30fd728f30424326649751616902c39ea4ecd2280bb80646f292912e0c09",
            Convert.ToHexStringLower(Spake2.PasswordToScalar(Code)));
    }

    [Fact]
    public void Spake2_exchange_matches_reference()
    {
        var a = new Spake2(Code, AppId, Hex("9ee347fe6465c8674f6114ebf4302f82b10cbb23307fcb0df3b47a3b6815e80a"));
        var b = new Spake2(Code, AppId, Hex("4ff3999faf677e77c82531330b687fef6219764760fe961be669f576d02ad005"));

        var ma = a.Start();
        var mb = b.Start();

        Assert.Equal("53d9d9db8fbeea822e2ee39d348f539a8eef7ca2ed997ce4ce7f0b0aad416e0d55", Convert.ToHexStringLower(ma));
        Assert.Equal("538c1a063f63d7350deddff87e725879c7fc77f7dce828a563449afd6b17170e61", Convert.ToHexStringLower(mb));
        Assert.Equal("15d65e15451cf3465928ddb27517623466c08d3dd91413fa347c36726e4b1297", Convert.ToHexStringLower(a.Finish(mb)));
        Assert.Equal("15d65e15451cf3465928ddb27517623466c08d3dd91413fa347c36726e4b1297", Convert.ToHexStringLower(b.Finish(ma)));
    }

    [Fact]
    public void Different_codes_give_different_keys()
    {
        var a = new Spake2(Code, AppId);
        var b = new Spake2("7-guitarist-revenue"u8.ToArray(), AppId);
        var ma = a.Start();
        var mb = b.Start();

        Assert.NotEqual(Convert.ToHexString(a.Finish(mb)), Convert.ToHexString(b.Finish(ma)));
    }

    [Fact]
    public void Reflected_message_is_rejected()
    {
        var a = new Spake2(Code, AppId);
        var ma = a.Start();

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => a.Finish(ma));
    }

    [Fact]
    public void Key_derivation_and_secretbox_match_reference()
    {
        var key = Hex("15d65e15451cf3465928ddb27517623466c08d3dd91413fa347c36726e4b1297");

        Assert.Equal("31184b8cde380cfa1ca995cf1dfb7e57d1d07c7344bb3e51b6505f5c7d7ef658",
            Convert.ToHexStringLower(WormholeCrypto.DeriveKey(key, "lothar.com/wormhole/text-or-file-xfer/transit-key")));
        var phaseKey = WormholeCrypto.DerivePhaseKey(key, "abc123side", "version");
        Assert.Equal("ee9169911486153df920e462eb22ed5a19f66adb12f04d314ce5cc2f09a7faf1", Convert.ToHexStringLower(phaseKey));

        var nonce = Enumerable.Range(0, 24).Select(i => (byte)i).ToArray();
        var box = WormholeCrypto.Encrypt(phaseKey, Encoding.UTF8.GetBytes("{\"app_versions\": {}}"), nonce);
        Assert.Equal("000102030405060708090a0b0c0d0e0f1011121314151617fc92ce0d922e9088a0c343ae3a8c26ff0f88d9984996903e33f4c33d26e30f3895afcc15",
            Convert.ToHexStringLower(box));
        Assert.Equal("{\"app_versions\": {}}", Encoding.UTF8.GetString(WormholeCrypto.Decrypt(phaseKey, box)));
    }
}
