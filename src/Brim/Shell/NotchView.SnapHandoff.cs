using System.Numerics;
using Brim.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Brim.Shell;

/// <summary>
/// Übergabe an die Snap-Leiste: Zieht man ein Fenster nach oben, verschwindet Brim nicht einfach, sondern schrumpft
/// zu einer kurzen Linie in der Akzentfarbe genau dort, wo Windows die Snap-Layouts zeigt, und kehrt danach zurück.
/// </summary>
public sealed partial class NotchView
{
    private const double HandoffWidth = 60, HandoffHeight = 3;

    private Border? _handoff;
    private Visual? _handoffVisual;
    private bool _handoffOn;

    public void SetSnapHandoff(bool on)
    {
        if (on == _handoffOn)
        {
            return;
        }
        _handoffOn = on;
        if (_handoff is null)
        {
            _handoff = new Border
            {
                Width = HandoffWidth,
                Height = HandoffHeight,
                CornerRadius = new CornerRadius(0, 0, 2, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
            CanvasRoot.Children.Add(_handoff);
            _handoffVisual = ElementCompositionPreview.GetElementVisual(_handoff);
            _handoffVisual.Opacity = 0;
            _handoffVisual.CenterPoint = new Vector3((float)HandoffWidth / 2, 0, 0);
        }
        // Akzent jedes Mal frisch: Er folgt Windows und dem hellen Modus.
        _handoff.Background = Brushes.Get(BrimColors.DefaultAccent);

        var visual = _handoffVisual!;
        var compositor = visual.Compositor;
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        var scale = compositor.CreateVector3KeyFrameAnimation();
        if (on)
        {
            // Aus der Breite der Notch zur Linie, während die Notch ausblendet.
            var from = (float)(NotchGeometry.Hidden.Width / HandoffWidth);
            scale.InsertKeyFrame(0, new Vector3(_reducedMotion ? 1 : from, 1, 1));
            scale.InsertKeyFrame(1, Vector3.One, EaseOut(compositor));
            scale.Duration = TimeSpan.FromMilliseconds(_reducedMotion ? 1 : 260);
            opacity.InsertKeyFrame(1, 1);
            opacity.Duration = TimeSpan.FromMilliseconds(120);
        }
        else
        {
            scale.InsertKeyFrame(1, Vector3.One);
            scale.Duration = TimeSpan.FromMilliseconds(1);
            opacity.InsertKeyFrame(1, 0);
            opacity.Duration = TimeSpan.FromMilliseconds(120);
        }
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    private static CompositionEasingFunction EaseOut(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1));
}
