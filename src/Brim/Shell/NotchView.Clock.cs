using System.Numerics;
using Brim.Core;
using Brim.Core.Widgets;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Hosting;

namespace Brim.Shell;

/// <summary>
/// Die Uhr als gemeinsames Element (Shared Element Transition): Im Ruhezustand steht sie mittig
/// in der Notch, aufgeklappt rechts in der Kopfzeile. Beim Wechsel gleitet und skaliert sie
/// mit derselben Feder wie die Form an den neuen Platz.
/// </summary>
public sealed partial class NotchView
{
    /// <summary>Schriftgröße in der Kopfzeile (Design: 12) relativ zur Ruhe (13).</summary>
    private const double HeaderClockScale = 12.0 / 13.0;

    /// <summary>Innenabstand und Kopfzeile des erweiterten Zustands (aus NotchView.xaml).</summary>
    private const double ExpandedPaddingRight = 24, ExpandedPaddingTop = 14, HeaderHeight = 30;

    private readonly SpringChannel _clockX = new(0);
    private readonly SpringChannel _clockY = new(0);
    private readonly SpringChannel _clockScale = new(1);
    private Visual? _clockVisual;
    private bool _clockShown;
    private bool _clockPlaced;

    private void InitializeClock()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(SharedClock, true);
        _clockVisual = ElementCompositionPreview.GetElementVisual(SharedClock);
        _clockVisual.Opacity = 0;
        ElementCompositionPreview.GetElementVisual(ClockHost).Clip = _surface.CreateContentClip();
    }

    /// <param name="spring">Feder der laufenden Formänderung, damit Uhr und Körper wie ein Stück wirken.</param>
    private void UpdateClock(NotchPresentation p, Spring spring, bool modeChanged)
    {
        if (_clockVisual is null)
        {
            return;
        }
        var text = (p.Idle?.Activity.Content as TextWidget)?.Text ?? "";
        if (SharedClock.Text != text)
        {
            SharedClock.Text = text;
            AutomationProperties.SetName(SharedClock, text);
        }

        var visible = p.Mode is NotchMode.Idle or NotchMode.Expanded && text.Length > 0;
        if (!visible)
        {
            FadeClock(false);
            return;
        }

        SharedClock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = SharedClock.DesiredSize.Width;
        var height = SharedClock.DesiredSize.Height;
        var center = NotchGeometry.Canvas.Width / 2;
        double x, y, scale;
        if (p.Mode == NotchMode.Expanded)
        {
            scale = HeaderClockScale;
            x = center + NotchGeometry.Expanded.Width / 2 - ExpandedPaddingRight - width * scale;
            y = ExpandedPaddingTop + HeaderHeight / 2 - height * scale / 2;
        }
        else
        {
            var shape = ShapeFor(NotchMode.Idle);
            scale = _hovering ? HoverContentScale : 1;
            x = center - width * scale / 2;
            y = shape.Height / 2 - height * scale / 2;
        }

        // Kommt die Uhr aus dem Nichts (z. B. aus der kompakten Ansicht), erscheint sie gleich am Ziel.
        var jump = !_clockShown || !_clockPlaced || _reducedMotion || _animation.Style is AnimationStyle.None or AnimationStyle.Fade;
        MoveClock(x, y, scale, spring, jump, modeChanged);
        FadeClock(true);
    }

    private void MoveClock(double x, double y, double scale, Spring spring, bool jump, bool modeChanged)
    {
        var visual = _clockVisual!;
        var now = NotchSurface.Now;
        if (jump)
        {
            _clockX.Set(x, now);
            _clockY.Set(y, now);
            _clockScale.Set(scale, now);
            visual.StopAnimation("Translation");
            visual.StopAnimation("Scale");
            visual.Properties.InsertVector3("Translation", new Vector3((float)x, (float)y, 0));
            visual.Scale = new Vector3((float)scale, (float)scale, 1);
            _clockPlaced = true;
            return;
        }
        if (!modeChanged && Math.Abs(_clockX.Target - x) < 0.5 && Math.Abs(_clockY.Target - y) < 0.5 && Math.Abs(_clockScale.Target - scale) < 0.001)
        {
            return;
        }

        var (fx, vx) = _clockX.Retarget(x, spring, now);
        var (fy, vy) = _clockY.Retarget(y, spring, now);
        var (fs, vs) = _clockScale.Retarget(scale, spring, now);
        var length = Math.Max(_clockX.Duration, Math.Max(_clockY.Duration, _clockScale.Duration));
        double At(SpringChannel c, double from, double to, double v, double t) =>
            t >= c.Duration ? to : spring.Evaluate(from, to, v, t).Value;

        visual.StartAnimation("Translation", NotchSurface.CreateCurve3(visual.Compositor, length,
            t => new Vector3((float)At(_clockX, fx, x, vx, t), (float)At(_clockY, fy, y, vy, t), 0)));
        visual.StartAnimation("Scale", NotchSurface.CreateCurve3(visual.Compositor, length,
            t => { var s = (float)At(_clockScale, fs, scale, vs, t); return new Vector3(s, s, 1); }));
    }

    private void FadeClock(bool show)
    {
        if (_clockShown == show || _clockVisual is null)
        {
            return;
        }
        _clockShown = show;
        var fade = _clockVisual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, show ? 1 : 0);
        fade.Duration = TimeSpan.FromMilliseconds(show ? 180 : 90);
        fade.DelayTime = TimeSpan.FromMilliseconds(show ? 60 : 0);
        _clockVisual.StartAnimation("Opacity", fade);
    }
}
