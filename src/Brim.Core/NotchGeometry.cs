namespace Brim.Core;

/// <summary>
/// Form der Notch in DIP: Körper mit unten abgerundeten Ecken und zwei „Ohren“,
/// die den Körper weich in die Bildschirmkante übergehen lassen.
/// </summary>
public readonly record struct NotchShape(double Width, double Height, double Radius, double Ear)
{
    /// <summary>Gesamtbreite inklusive Ohren.</summary>
    public double OuterWidth => Width + 2 * Ear;
}

/// <summary>Eine Perle (zweite, dritte Aktivität) rechts neben dem Körper.</summary>
public readonly record struct BeadShape(double Left, double Size);

/// <summary>Maße aus design/boards/3-states.html und 2-now-playing.html.</summary>
public static class NotchGeometry
{
    public static readonly NotchShape Idle = new(180, 30, 15, 10);
    public static readonly NotchShape Compact = new(300, 34, 17, 10);
    public static readonly NotchShape Alert = new(480, 88, 28, 12);
    public static readonly NotchShape Expanded = new(620, 248, 38, 12);
    public static readonly NotchShape ShelfDrop = new(352, 88, 28, 12);

    /// <summary>Zurückgezogen: schmilzt zu einer feinen Linie an der Kante.</summary>
    public static readonly NotchShape Hidden = new(140, 3, 1.5, 0);

    public const double BeadSize = 34;

    /// <summary>Abstand zwischen rechtem Ohr des Körpers und der Perle.</summary>
    public const double BeadGap = 6;

    /// <summary>Spielraum für das Überschwingen der Feder, damit nichts abgeschnitten wird.</summary>
    public const double Overshoot = 16;

    /// <summary>Feste Größe der Zeichenfläche (Fenster), in die alle Formen passen.</summary>
    public static readonly (double Width, double Height) Canvas =
        (Expanded.OuterWidth + 2 * (BeadGap + BeadSize) + 2 * Overshoot, Expanded.Height + Overshoot);

    /// <summary>
    /// Leicht angeschwollene Form, solange die Maus auf der kompakten Notch ruht –
    /// sofortige Rückmeldung, bevor sie sich ganz aufklappt.
    /// </summary>
    public static NotchShape Swell(NotchShape shape) =>
        shape with { Width = shape.Width + 20, Height = shape.Height + 6, Radius = shape.Radius + 3 };

    /// <summary>Ob die Notch in diesem Zustand auf Hover anschwillt.</summary>
    public static bool SwellsOnHover(NotchMode mode) => mode is NotchMode.Idle or NotchMode.Compact or NotchMode.Hud;

    public static NotchShape For(NotchMode mode) => mode switch
    {
        NotchMode.Hidden => Hidden,
        NotchMode.Idle => Idle,
        NotchMode.Compact or NotchMode.Hud => Compact,
        NotchMode.Alert => Alert,
        NotchMode.Expanded => Expanded,
        NotchMode.Drop => ShelfDrop,
        _ => Idle,
    };

    /// <summary>Perlen rechts vom Körper, Positionen relativ zur Mitte der Zeichenfläche.</summary>
    public static IReadOnlyList<BeadShape> Beads(NotchShape body, int count)
    {
        var result = new List<BeadShape>(count);
        var left = body.Width / 2 + body.Ear + BeadGap;
        for (var i = 0; i < count; i++)
        {
            result.Add(new BeadShape(left, BeadSize));
            left += BeadSize + BeadGap;
        }
        return result;
    }

    /// <summary>
    /// Bereich, der Eingaben annimmt (Fensterregion), relativ zur Zeichenfläche.
    /// Während einer Animation wird die Vereinigung aus alter und neuer Form genutzt.
    /// </summary>
    public static (double X, double Y, double Width, double Height) HitArea(NotchShape body, int beadCount)
    {
        var center = Canvas.Width / 2;
        var left = center - body.Width / 2 - body.Ear;
        var right = center + body.Width / 2 + body.Ear;
        if (beadCount > 0)
        {
            var beads = Beads(body, beadCount);
            right = center + beads[^1].Left + beads[^1].Size;
        }
        var height = Math.Max(body.Height, beadCount > 0 ? BeadSize : 0);
        return (left, 0, right - left, height);
    }

    public static (double X, double Y, double Width, double Height) Union(
        (double X, double Y, double Width, double Height) a,
        (double X, double Y, double Width, double Height) b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var r = Math.Max(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return (x, y, r - x, bottom - y);
    }
}
