using System.Numerics;
using Brim.Core;

namespace Brim.Core.Tests;

public class SilhouetteTests
{
    private const double Cx = 500;

    public static TheoryData<NotchMode, NotchForm> Shapes()
    {
        var data = new TheoryData<NotchMode, NotchForm>();
        foreach (var mode in new[] { NotchMode.Hidden, NotchMode.Idle, NotchMode.Compact, NotchMode.Alert, NotchMode.Expanded, NotchMode.Mini })
        {
            foreach (var form in Enum.GetValues<NotchForm>())
            {
                data.Add(mode, form);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_shape_has_the_same_structure_so_the_compositor_can_morph_between_them(NotchMode mode, NotchForm form)
    {
        var shape = NotchGeometry.For(mode, form);
        var reference = NotchSilhouette.Figure(NotchGeometry.Idle, Cx);
        foreach (var figure in new[] { NotchSilhouette.Figure(shape, Cx), NotchSilhouette.Figure(shape, Cx, inset: NotchSilhouette.RimWidth) })
        {
            Assert.Equal(NotchSilhouette.SegmentCount, figure.Segments.Count);
            Assert.Equal(reference.Segments.Select(s => s.IsLine), figure.Segments.Select(s => s.IsLine));
            Assert.Equal(figure.Start, figure.Segments[^1].End);
            Assert.All(figure.Segments, s => Assert.True(float.IsFinite(s.End.X) && float.IsFinite(s.End.Y)));
        }
    }

    [Fact]
    public void Notch_reaches_from_ear_tip_to_ear_tip_and_down_to_its_height()
    {
        var shape = NotchGeometry.Idle;
        var points = Flatten(NotchSilhouette.Figure(shape, Cx));

        Assert.Equal(Cx - shape.OuterWidth / 2, points.Min(p => p.X), 3);
        Assert.Equal(Cx + shape.OuterWidth / 2, points.Max(p => p.X), 3);
        Assert.Equal(shape.Height, points.Max(p => p.Y), 3);
        // Die Oberkante liegt über dem Bild, damit an der Kante nichts zu sehen ist.
        Assert.True(points.Min(p => p.Y) < 0);
    }

    [Fact]
    public void Area_is_body_plus_ears_minus_rounded_corners()
    {
        var s = NotchGeometry.Compact;
        var area = Math.Abs(Area(Flatten(NotchSilhouette.Figure(s, Cx))));
        var corners = 2 * s.Radius * s.Radius * (1 - Math.PI / 4);
        var ears = 2 * s.Ear * s.Ear * (1 - Math.PI / 4);
        var aboveEdge = 2 * s.OuterWidth;
        Assert.Equal(s.Width * s.Height - corners + ears + aboveEdge, area, 0);
    }

    [Fact]
    public void Island_is_a_pill_below_the_gap()
    {
        var shape = NotchGeometry.For(NotchMode.Idle, NotchForm.Island);
        var gap = NotchGeometry.Gap(NotchForm.Island);
        var points = Flatten(NotchSilhouette.Figure(shape, Cx, gap));

        Assert.Equal(gap, points.Min(p => p.Y), 3);
        Assert.Equal(gap + shape.Height, points.Max(p => p.Y), 3);
        Assert.Equal(shape.Width, points.Max(p => p.X) - points.Min(p => p.X), 3);
        var r = shape.Height / 2;
        var expected = shape.Width * shape.Height - 4 * r * r * (1 - Math.PI / 4);
        Assert.Equal(expected, Math.Abs(Area(points)), 0);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Rim_is_one_dip_wide_and_lies_inside(NotchMode mode, NotchForm form)
    {
        var shape = NotchGeometry.For(mode, form);
        if (shape.Height < 4)
        {
            return;
        }
        var outer = Flatten(NotchSilhouette.Figure(shape, Cx));
        var inner = Flatten(NotchSilhouette.Figure(shape, Cx, inset: 1));

        Assert.Equal(outer.Max(p => p.Y) - 1, inner.Max(p => p.Y), 3);
        // Seiten des Körpers um genau 1 DIP eingerückt.
        var side = NotchSilhouette.Figure(shape, Cx, inset: 1).Segments[6].End;
        Assert.Equal(Cx - shape.Width / 2 + 1, side.X, 3);
        // Gleicher Umlaufsinn, sonst hebt der Rand den Körper auf.
        Assert.Equal(Math.Sign(Area(outer)), Math.Sign(Area(inner)));
    }

    private static List<Vector2> Flatten(OutlineFigure figure)
    {
        var points = new List<Vector2> { figure.Start };
        var current = figure.Start;
        foreach (var s in figure.Segments)
        {
            if (s.IsLine)
            {
                points.Add(s.End);
            }
            else
            {
                for (var i = 1; i <= 64; i++)
                {
                    var t = i / 64f;
                    var u = 1 - t;
                    points.Add(u * u * u * current + 3 * u * u * t * s.Control1 + 3 * u * t * t * s.Control2 + t * t * t * s.End);
                }
            }
            current = s.End;
        }
        return points;
    }

    private static double Area(List<Vector2> points)
    {
        double sum = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        return sum / 2;
    }
}
