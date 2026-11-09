using System.Numerics;

namespace Brim.Core;

/// <summary>Ein Abschnitt eines Umrisses: Linie zum Endpunkt oder kubische Bézierkurve über zwei Kontrollpunkte.</summary>
public readonly record struct OutlineSegment(bool IsLine, Vector2 Control1, Vector2 Control2, Vector2 End)
{
    public static OutlineSegment Line(Vector2 end) => new(true, default, default, end);
    public static OutlineSegment Cubic(Vector2 c1, Vector2 c2, Vector2 end) => new(false, c1, c2, end);
}

/// <summary>Geschlossene Figur aus Startpunkt und Abschnitten.</summary>
public sealed record OutlineFigure(Vector2 Start, IReadOnlyList<OutlineSegment> Segments);

/// <summary>
/// Der ganze Umriss der Notch – Körper und beide Ohren – als <b>eine</b> geschlossene Figur (Review A12). Der
/// Compositor glättet sie als Vektorform am Stück: keine Naht zwischen Körper und Ohr, kein doppelt deckender
/// Streifen bei durchscheinendem Material, saubere Kantenglättung in jeder Größe und Skalierung.
/// <para>
/// Die Figur hat für jede Form denselben Aufbau (gleiche Folge von Linien und Kurven, nur die Punkte wandern).
/// Deshalb kann der Compositor zwischen zwei Umrissen interpolieren (<c>PathKeyFrameAnimation</c>); ein Ohr der
/// Größe 0 oder eine Ecke der Insel ist dieselbe Kurve mit anderen Punkten.
/// </para>
/// </summary>
public static class NotchSilhouette
{
    /// <summary>Kontrollpunkte eines Viertelkreises als kubische Bézierkurve (Anteil des Radius).</summary>
    public const float Kappa = 0.5523f;

    /// <summary>Anzahl der Abschnitte jeder Figur (fest, damit sich Umrisse ineinander morphen lassen).</summary>
    public const int SegmentCount = 10;

    /// <summary>
    /// Umriss der Form, mittig bei <paramref name="centerX"/>. An der Kante liegt die Oberkante knapp über dem Bild
    /// (außerhalb des Fensters), bei der Insel (<see cref="NotchShape.Round"/> = 1) rundet sie auch oben.
    /// </summary>
    /// <param name="gap">Abstand der Insel zur Kante (verschiebt alles nach unten).</param>
    /// <param name="inset">
    /// Rückt den Umriss um so viele DIP nach innen. Außen- und eingerückter Umriss zusammen (gerade/ungerade gefüllt)
    /// ergeben den feinen Rand, wie ihn Flyouts in Windows 11 haben. An den Spitzen der Ohren läuft er gegen null aus.
    /// </param>
    public static OutlineFigure Figure(NotchShape shape, double centerX, double gap = 0, double inset = 0)
    {
        var k = Math.Clamp(shape.Round, 0, 1);
        var d = Math.Max(0, inset);
        var w = Math.Max(0, shape.Width);
        var h = Math.Max(0, shape.Height);
        var e = Math.Max(0, shape.Ear);

        // Wie der Körper in NotchSurface: höchstens halbe Höhe bei der Insel, an der Kante bis zur vollen Höhe.
        var r = Math.Min(Math.Max(0, shape.Radius), h * (1 - 0.5 * k));
        r = Math.Min(r, w / 2);

        var x0 = centerX - w / 2 + d;
        var x1 = centerX + w / 2 - d;
        var y1 = gap + h - d;
        var rb = Math.Max(0, Math.Min(r - d, (x1 - x0) / 2));

        // Ohr: wächst beim Einrücken um d (dieselbe Aussparung, ein Pixel weiter innen). Insel: die oberen Ecken.
        var ed = e + Math.Min(e, d);
        var a = ed + k * rb;
        var b = ed - k * rb;

        // Oberkante: an der Kante knapp außerhalb des Fensters, bei der Insel genau oben (eingerückt um d).
        var t = gap + (2 * k - 1) * d;
        var top = gap - (2 + d) * (1 - k) + k * d;

        // Sehr flache Formen (zurückgezogen): Ohr und Ecke dürfen sich nicht überholen.
        a = Math.Min(a, Math.Max(0, y1 - rb - t));

        var kc = 1 - Kappa;
        var start = P(x0 - b, top);
        OutlineSegment[] segments =
        [
            OutlineSegment.Line(P(x1 + b, top)),
            OutlineSegment.Line(P(x1 + b, t)),
            // Rechtes Ohr (hohl) oder rechte obere Ecke der Insel (gewölbt).
            OutlineSegment.Cubic(P(x1 + b * kc, t), P(x1, t + a * kc), P(x1, t + a)),
            OutlineSegment.Line(P(x1, y1 - rb)),
            OutlineSegment.Cubic(P(x1, y1 - rb + Kappa * rb), P(x1 - rb + Kappa * rb, y1), P(x1 - rb, y1)),
            OutlineSegment.Line(P(x0 + rb, y1)),
            OutlineSegment.Cubic(P(x0 + rb - Kappa * rb, y1), P(x0, y1 - rb + Kappa * rb), P(x0, y1 - rb)),
            OutlineSegment.Line(P(x0, t + a)),
            OutlineSegment.Cubic(P(x0, t + a * kc), P(x0 - b * kc, t), P(x0 - b, t)),
            OutlineSegment.Line(start),
        ];
        return new OutlineFigure(start, segments);
    }

    /// <summary>Breite des feinen Rands in DIP.</summary>
    public const double RimWidth = 1;

    private static Vector2 P(double x, double y) => new((float)x, (float)y);
}
