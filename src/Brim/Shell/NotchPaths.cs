using Brim.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace Brim.Shell;

/// <summary>Baut aus den Figuren von <see cref="NotchSilhouette"/> Vektorpfade für den Compositor.</summary>
internal static class NotchPaths
{
    /// <summary>Körper und Ohren als eine geglättete Fläche.</summary>
    public static CanvasGeometry Silhouette(NotchShape shape, double centerX, double gap = 0) =>
        Build(NotchSilhouette.Figure(shape, centerX, gap));

    /// <summary>
    /// Feiner Rand: Umriss und um <see cref="NotchSilhouette.RimWidth"/> eingerückter Umriss, gerade/ungerade
    /// gefüllt – übrig bleibt ein 1 DIP breiter Ring, oben an der Kante offen.
    /// </summary>
    public static CanvasGeometry Rim(NotchShape shape, double centerX, double gap = 0) =>
        Build(NotchSilhouette.Figure(shape, centerX, gap), NotchSilhouette.Figure(shape, centerX, gap, NotchSilhouette.RimWidth));

    private static CanvasGeometry Build(params OutlineFigure[] figures)
    {
        using var builder = new CanvasPathBuilder(CanvasDevice.GetSharedDevice());
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);
        foreach (var figure in figures)
        {
            builder.BeginFigure(figure.Start);
            foreach (var s in figure.Segments)
            {
                if (s.IsLine)
                {
                    builder.AddLine(s.End);
                }
                else
                {
                    builder.AddCubicBezier(s.Control1, s.Control2, s.End);
                }
            }
            builder.EndFigure(CanvasFigureLoop.Closed);
        }
        return CanvasGeometry.CreatePath(builder);
    }
}
