using System.Buffers.Binary;
using System.Text;

namespace Brim.Core;

/// <summary>
/// Liest den Familiennamen aus einer Schriftdatei (TrueType/OpenType, auch .ttc), damit Brim eine eigene Schrift
/// laden kann, ohne sie im System zu installieren: WinUI braucht „Pfad#Familie“.
/// </summary>
public static class FontFile
{
    public static readonly string[] Extensions = [".ttf", ".otf", ".ttc"];

    /// <summary>Familienname (bevorzugt der typografische, sonst der normale) oder <c>null</c>, wenn keine Schrift.</summary>
    public static string? FamilyName(ReadOnlySpan<byte> data)
    {
        try
        {
            var font = 0;
            if (data.Length >= 16 && data[..4].SequenceEqual("ttcf"u8))
            {
                // Sammlung: die erste Schrift.
                font = (int)U32(data, 12);
            }
            var tables = U16(data, font + 4);
            for (var i = 0; i < tables; i++)
            {
                var record = font + 12 + 16 * i;
                if (data.Slice(record, 4).SequenceEqual("name"u8))
                {
                    return FromNameTable(data, (int)U32(data, record + 8));
                }
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Abgeschnitten oder keine Schrift.
        }
        return null;
    }

    private static string? FromNameTable(ReadOnlySpan<byte> data, int table)
    {
        var count = U16(data, table + 2);
        var strings = table + U16(data, table + 4);
        string? best = null;
        var bestScore = -1;
        for (var i = 0; i < count; i++)
        {
            var r = table + 6 + 12 * i;
            var platform = U16(data, r);
            var encoding = U16(data, r + 2);
            var language = U16(data, r + 4);
            var nameId = U16(data, r + 6);
            if (nameId is not (1 or 16))
            {
                continue;
            }
            var bytes = data.Slice(strings + U16(data, r + 10), U16(data, r + 8));
            string? text = platform switch
            {
                0 or 3 when platform == 0 || encoding is 0 or 1 or 10 => Encoding.BigEndianUnicode.GetString(bytes),
                1 when encoding == 0 => Encoding.Latin1.GetString(bytes),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            // Typografischer Name vor dem normalen, Windows/Englisch vor allem anderen.
            var score = (nameId == 16 ? 4 : 0) + (platform == 3 ? 2 : 0) + (platform != 3 || language == 0x409 ? 1 : 0);
            if (score > bestScore)
            {
                best = text.Trim();
                bestScore = score;
            }
        }
        return best;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
}
