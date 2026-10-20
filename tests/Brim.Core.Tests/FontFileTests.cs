using System.Buffers.Binary;
using System.Text;

namespace Brim.Core.Tests;

public class FontFileTests
{
    /// <summary>Kleinste Schrift mit Tabelle „name“: genug für den Familiennamen.</summary>
    private static byte[] Font(params (ushort Platform, ushort Encoding, ushort Language, ushort NameId, string Text)[] names)
    {
        var strings = new List<byte>();
        var records = new List<(ushort, ushort, ushort, ushort, ushort, ushort)>();
        foreach (var (platform, encoding, language, nameId, text) in names)
        {
            var bytes = platform == 1 ? Encoding.Latin1.GetBytes(text) : Encoding.BigEndianUnicode.GetBytes(text);
            records.Add((platform, encoding, language, nameId, (ushort)bytes.Length, (ushort)strings.Count));
            strings.AddRange(bytes);
        }
        const int tableAt = 12 + 16;
        var stringOffset = 6 + 12 * records.Count;
        var data = new byte[tableAt + stringOffset + strings.Count];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), 1);
        "name"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), tableAt);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(tableAt + 2), (ushort)records.Count);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(tableAt + 4), (ushort)stringOffset);
        for (var i = 0; i < records.Count; i++)
        {
            var (p, e, l, n, len, off) = records[i];
            var at = tableAt + 6 + 12 * i;
            foreach (var (value, k) in new[] { (p, 0), (e, 2), (l, 4), (n, 6), (len, 8), (off, 10) })
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at + k), value);
            }
        }
        strings.CopyTo(data, tableAt + stringOffset);
        return data;
    }

    [Fact]
    public void Reads_the_family_preferring_the_typographic_english_windows_name()
    {
        var font = Font(
            (1, 0, 0, 1, "Mac Name"),
            (3, 1, 0x407, 1, "Deutsch"),
            (3, 1, 0x409, 1, "Inter Medium"),
            (3, 1, 0x409, 16, "Inter"));

        Assert.Equal("Inter", FontFile.FamilyName(font));
        Assert.Equal("Inter Medium", FontFile.FamilyName(Font((3, 1, 0x409, 1, "Inter Medium"), (3, 1, 0x409, 2, "Bold"))));
        Assert.Equal("Mac Name", FontFile.FamilyName(Font((1, 0, 0, 1, "Mac Name"))));
    }

    [Fact]
    public void Anything_else_is_not_a_font()
    {
        Assert.Null(FontFile.FamilyName([]));
        Assert.Null(FontFile.FamilyName("PK\u0003\u0004 kein Font"u8.ToArray()));
        Assert.Null(FontFile.FamilyName(Font((3, 1, 0x409, 2, "Bold"))));
    }
}

public class FontCatalogTests
{
    private static readonly Func<string, bool> Exists = _ => true;

    [Fact]
    public void Defaults_follow_the_design()
    {
        var fonts = new FontSettings();
        Assert.StartsWith("Cascadia Mono", FontCatalog.Resolve(fonts, FontRole.Clock, Exists));
        Assert.StartsWith("Cascadia Mono", FontCatalog.Resolve(fonts, FontRole.Numbers, Exists));
        Assert.StartsWith("Segoe UI Variable Display", FontCatalog.Resolve(fonts, FontRole.Headings, Exists));
        Assert.StartsWith("Segoe UI Variable Text", FontCatalog.Resolve(fonts, FontRole.Menu, Exists));
    }

    [Fact]
    public void Each_role_has_its_own_font_and_custom_fonts_fall_back()
    {
        var file = OperatingSystem.IsWindows() ? @"C:\Brim\Fonts\Inter.ttf" : "/brim/fonts/Inter.ttf";
        var fonts = new FontSettings { Clock = FontCatalog.Bahnschrift, Text = "Inter", Custom = [new CustomFont(file, "Inter")] };

        Assert.StartsWith("Bahnschrift", FontCatalog.Resolve(fonts, FontRole.Clock, Exists));
        var text = FontCatalog.Resolve(fonts, FontRole.Text, Exists);
        Assert.StartsWith(file + "#Inter, file:///", text);
        Assert.EndsWith("Segoe UI", text);
        Assert.StartsWith("Segoe UI Variable Display", FontCatalog.Resolve(fonts, FontRole.Headings, Exists));

        // Datei weg oder unbekannter Name: Standard.
        Assert.StartsWith("Segoe UI Variable Text", FontCatalog.Resolve(fonts, FontRole.Text, _ => false));
        fonts.Set(FontRole.Menu, "Gibt es nicht");
        Assert.StartsWith("Segoe UI Variable Text", FontCatalog.Resolve(fonts, FontRole.Menu, Exists));
    }
}
