using System.Security.Cryptography;

namespace Brim.Modules.Wormhole;

/// <summary>Codes wie im Original: Nameplate + Wörter, das erste dreisilbig, dann abwechselnd (z. B. <c>7-guitarist-revenge</c>).</summary>
internal static partial class WordList
{
    public static string Choose(int count)
    {
        var words = new string[count];
        for (var i = 0; i < count; i++)
        {
            var b = RandomNumberGenerator.GetInt32(256);
            words[i] = i % 2 == 0 ? Odd[b] : Even[b];
        }
        return string.Join("-", words);
    }

    /// <summary>Alle Wörter, z. B. für die Vervollständigung beim Eintippen.</summary>
    public static IEnumerable<string> All => Odd.Concat(Even);
}
