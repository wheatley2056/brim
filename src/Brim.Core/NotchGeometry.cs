namespace Brim.Core;

/// <summary>Grundform der Notch (Einstellungen → Aussehen).</summary>
public enum NotchForm
{
    /// <summary>Wächst aus der Kante, unten rund, mit Ohren (Standard).</summary>
    Notch,
    /// <summary>Frei schwebende Pille mit Abstand zur Kante, alle Ecken rund.</summary>
    Island,
    /// <summary>Flach an der Kante, ohne Ohren, unten leicht gerundet.</summary>
    Edge,
    /// <summary>Eckig mit kleinem Radius, wie ein Reiter.</summary>
    Tab,
}

/// <summary>
/// Form der Notch in DIP: Körper mit unten abgerundeten Ecken und zwei „Ohren“,
/// die den Körper weich in die Bildschirmkante übergehen lassen.
/// <paramref name="Round"/> rundet auch die oberen Ecken (1 = Insel, 0 = an der Kante); dazwischen morpht sie.
/// </summary>
public readonly record struct NotchShape(double Width, double Height, double Radius, double Ear, double Round = 0)
{
    /// <summary>Gesamtbreite inklusive Ohren.</summary>
    public double OuterWidth => Width + 2 * Ear;
}

/// <summary>Abgerundetes Rechteck in DIP der Zeichenfläche.</summary>
public readonly record struct RoundRect(double X, double Y, double Width, double Height, double Radius);

/// <summary>
/// Genauer Umriss, wie er gezeichnet wird – für Glas, dessen Hintergrund das Fenster nur innerhalb seiner
/// Region weichzeichnet. Ohren sind Quadrate, aus denen ein Kreis ausgespart ist (<see cref="EarCutouts"/>).
/// </summary>
public sealed record NotchOutline(RoundRect Body, IReadOnlyList<RoundRect> Ears, IReadOnlyList<RoundRect> EarCutouts, IReadOnlyList<RoundRect> Beads);

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

    /// <summary>Spielmodus „Mini“: schmale Pille mit FPS · ms (Plan 4.8: ca. 120 × 22).</summary>
    public static readonly NotchShape Mini = new(124, 22, 11, 6);

    /// <summary>Zurückgezogen: schmilzt zu einer feinen Linie an der Kante.</summary>
    public static readonly NotchShape Hidden = new(140, 3, 1.5, 0);

    public const double BeadSize = 34;

    /// <summary>Abstand zwischen rechtem Ohr des Körpers und der Perle.</summary>
    public const double BeadGap = 6;

    /// <summary>Spielraum für das Überschwingen der Feder, damit nichts abgeschnitten wird.</summary>
    public const double Overshoot = 16;

    /// <summary>Abstand der Insel zur Bildschirmkante.</summary>
    public const double IslandGap = 6;

    /// <summary>Kleinste und größte Größe (Faktor auf alle Zustände).</summary>
    public const double MinSize = 0.8, MaxSize = 1.3;

    /// <summary>Feste Größe der Zeichenfläche (Fenster), in die alle Formen passen – auch die Insel mit Abstand.</summary>
    public static readonly (double Width, double Height) Canvas =
        (Expanded.OuterWidth + 2 * (BeadGap + BeadSize) + 2 * Overshoot, Expanded.Height + Overshoot + IslandGap);

    /// <summary>Abstand zur Oberkante: Nur die Insel schwebt.</summary>
    public static double Gap(NotchForm form) => form == NotchForm.Island ? IslandGap : 0;

    /// <summary>
    /// Passt eine Grundform (Maße aus den Design-Boards) an den gewählten Stil an. Breiten und Höhen bleiben,
    /// damit Inhalte und Animationen überall gleich passen; es ändern sich Ecken und Ohren.
    /// </summary>
    public static NotchShape Styled(NotchShape shape, NotchForm form) => form switch
    {
        // Ecken werden beim Zeichnen auf die halbe Höhe begrenzt: klein eine Pille, aufgeklappt ein Kärtchen.
        NotchForm.Island => shape with { Ear = 0, Round = 1 },
        NotchForm.Edge => shape with { Ear = 0, Radius = Math.Min(shape.Radius * 0.4, 14) },
        NotchForm.Tab => shape with { Ear = 0, Radius = Math.Min(shape.Radius, Math.Min(4 + shape.Radius * 0.1, 8)) },
        _ => shape,
    };

    /// <summary>
    /// Leicht angeschwollene Form, solange die Maus auf der kompakten Notch ruht –
    /// sofortige Rückmeldung, bevor sie sich ganz aufklappt.
    /// </summary>
    public static NotchShape Swell(NotchShape shape) =>
        shape with { Width = shape.Width + 20, Height = shape.Height + 6, Radius = shape.Radius + 3 };

    /// <summary>Ob die Notch in diesem Zustand auf Hover anschwillt.</summary>
    public static bool SwellsOnHover(NotchMode mode) => mode is NotchMode.Idle or NotchMode.Compact or NotchMode.Hud;

    public static NotchShape For(NotchMode mode, NotchForm form) => Styled(For(mode), form);

    public static NotchShape For(NotchMode mode) => mode switch
    {
        NotchMode.Hidden => Hidden,
        NotchMode.Idle => Idle,
        NotchMode.Compact or NotchMode.Hud => Compact,
        NotchMode.Alert => Alert,
        NotchMode.Expanded => Expanded,
        NotchMode.Drop => ShelfDrop,
        NotchMode.Mini => Mini,
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
    /// Der Abstand der Insel zur Kante gehört dazu, sonst flackert der Hover beim Weg nach oben.
    /// </summary>
    public static (double X, double Y, double Width, double Height) HitArea(NotchShape body, int beadCount, double gap = 0)
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
        return (left, 0, right - left, height + gap);
    }

    /// <summary>Umriss genau wie in NotchSurface gezeichnet, mit dem Abstand der Insel nach unten versetzt.</summary>
    public static NotchOutline Outline(NotchShape body, int beadCount, double gap)
    {
        var center = Canvas.Width / 2;
        var left = center - body.Width / 2;
        var rise = body.Radius * (1 - body.Round);
        var radius = Math.Min(body.Radius, body.Height * (1 - 0.5 * body.Round));
        var shape = new RoundRect(left, gap - rise, body.Width, body.Height + rise, radius);

        var ears = new List<RoundRect>();
        var cutouts = new List<RoundRect>();
        if (body.Ear > 0)
        {
            var e = body.Ear;
            ears.Add(new RoundRect(left - e, gap, e, e, 0));
            ears.Add(new RoundRect(left + body.Width, gap, e, e, 0));
            // Kreis um die äußere untere Ecke des Ohrs: Was darin liegt, ist durchsichtig.
            cutouts.Add(new RoundRect(left - 2 * e, gap, 2 * e, 2 * e, e));
            cutouts.Add(new RoundRect(left + body.Width, gap, 2 * e, 2 * e, e));
        }

        var beads = new List<RoundRect>();
        foreach (var bead in Beads(body, beadCount))
        {
            var beadRise = bead.Size / 2 * (1 - body.Round);
            beads.Add(new RoundRect(center + bead.Left, gap - beadRise, bead.Size, bead.Size + beadRise, bead.Size / 2));
        }
        return new NotchOutline(shape, ears, cutouts, beads);
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
