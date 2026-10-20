namespace Brim.Core;

/// <summary>
/// Leitet aus einem Cover eine Akzentfarbe ab („Borrowed“ im Design):
/// die kräftigste Farbfamilie, aufgehellt, damit sie auf Schwarz trägt.
/// </summary>
public static class AccentColor
{
    private const int HueBins = 12;

    /// <summary>Pixel im Format BGRA8. Gibt <c>null</c> zurück, wenn das Bild praktisch farblos ist.</summary>
    public static string? FromBgra(ReadOnlySpan<byte> bgra)
    {
        Span<double> weight = stackalloc double[HueBins];
        Span<double> sumR = stackalloc double[HueBins];
        Span<double> sumG = stackalloc double[HueBins];
        Span<double> sumB = stackalloc double[HueBins];
        double total = 0;
        var pixels = bgra.Length / 4;

        for (var i = 0; i < pixels; i++)
        {
            double b = bgra[i * 4] / 255.0, g = bgra[i * 4 + 1] / 255.0, r = bgra[i * 4 + 2] / 255.0, a = bgra[i * 4 + 3] / 255.0;
            if (a < 0.5)
            {
                continue;
            }
            var (h, s, v) = ToHsv(r, g, b);
            if (v < 0.18 || s < 0.22)
            {
                continue;
            }
            // Kräftige, helle Farben zählen mehr als matte.
            var w = s * s * v;
            var bin = (int)(h / 360.0 * HueBins) % HueBins;
            weight[bin] += w;
            sumR[bin] += r * w;
            sumG[bin] += g * w;
            sumB[bin] += b * w;
            total += w;
        }

        if (pixels == 0 || total / pixels < 0.02)
        {
            return null;
        }

        var best = 0;
        for (var i = 1; i < HueBins; i++)
        {
            if (weight[i] > weight[best])
            {
                best = i;
            }
        }

        var (bh, bs, bv) = ToHsv(sumR[best] / weight[best], sumG[best] / weight[best], sumB[best] / weight[best]);
        bs = Math.Clamp(bs, 0.45, 0.75);
        bv = Math.Max(bv, 0.92);
        var (rr, gg, bb) = FromHsv(bh, bs, bv);
        return ToHex(rr, gg, bb);
    }

    public static string ToHex(double r, double g, double b) =>
        $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";

    /// <summary>Liest #RRGGBB oder #AARRGGBB.</summary>
    /// <summary>Ob <paramref name="hex"/> eine Farbe in der Form <c>#RRGGBB</c> oder <c>#AARRGGBB</c> ist.</summary>
    public static bool IsValid(string? hex)
    {
        if (hex is null)
        {
            return false;
        }
        var s = hex.TrimStart('#');
        return s.Length is 6 or 8 && s.All(Uri.IsHexDigit);
    }

    public static (byte A, byte R, byte G, byte B) Parse(string hex)
    {
        var s = hex.TrimStart('#');
        return s.Length switch
        {
            6 => (255, Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16)),
            8 => (Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16), Convert.ToByte(s[6..8], 16)),
            _ => throw new FormatException($"Keine Farbe: {hex}"),
        };
    }

    private static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static (double R, double G, double B) FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }
}
