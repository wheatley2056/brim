using System.Diagnostics;
using System.Numerics;
using Brim.Core;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Brim.Shell;

/// <summary>
/// Morph-Engine (Plan 4.9): Die Form der Notch lebt vollständig im Compositor.
/// Breite, Höhe, Eckenradius und Ohren sind Werte in einem <see cref="CompositionPropertySet"/>;
/// Körper, Ohren, Schimmer, Perlen und der Clip des Inhalts hängen über Ausdrucksanimationen daran.
/// So laufen Federn mit der Bildwiederholrate des Monitors, unabhängig vom UI-Thread,
/// und jede neue Animation startet aus der aktuellen Form.
/// </summary>
internal sealed class NotchSurface : IDisposable
{
    private const string W = "W", H = "H", R = "R", E = "E";

    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _props;
    private readonly ContainerVisual _root;
    private readonly SpriteVisual _shimmer;
    private const byte ShimmerAlpha = 92;
    private readonly CompositionColorGradientStop _shimmerStop;
    private readonly CompositionColorGradientStop _shimmerMid;
    private readonly CompositionColorGradientStop _shimmerEdge;
    private readonly ShapeVisual[] _beads;
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<string, SpringChannel> _channels;
    private NotchShape _target;
    private string? _accent;
    private float _shimmerTarget = -1;
    private double? _glow;

    public NotchSurface(Compositor compositor, float canvasWidth, float canvasHeight, NotchShape initial)
    {
        _compositor = compositor;
        _target = initial;
        _channels = new()
        {
            [W] = new SpringChannel(initial.Width),
            [H] = new SpringChannel(initial.Height),
            [R] = new SpringChannel(initial.Radius),
            [E] = new SpringChannel(initial.Ear),
        };

        _props = compositor.CreatePropertySet();
        _props.InsertScalar("CX", canvasWidth / 2);
        _props.InsertScalar(W, (float)initial.Width);
        _props.InsertScalar(H, (float)initial.Height);
        _props.InsertScalar(R, (float)initial.Radius);
        _props.InsertScalar(E, (float)initial.Ear);

        _root = compositor.CreateContainerVisual();
        _root.Size = new Vector2(canvasWidth, canvasHeight);

        var black = compositor.CreateColorBrush(Colors.Black);

        // Körper: abgerundetes Rechteck, nach oben über die Kante geschoben,
        // damit nur die unteren Ecken rund erscheinen.
        var body = compositor.CreateShapeVisual();
        body.Size = _root.Size;
        var bodyGeometry = CreateBodyGeometry();
        var bodyShape = compositor.CreateSpriteShape(bodyGeometry);
        bodyShape.FillBrush = black;
        body.Shapes.Add(bodyShape);
        _root.Children.InsertAtTop(body);

        // Ohren: Viertelkreis-Aussparung, wie radial-gradient(circle at 0 100%, transparent r, #000 r) im Design.
        _root.Children.InsertAtTop(CreateEar(left: true));
        _root.Children.InsertAtTop(CreateEar(left: false));

        // Schimmer in Akzentfarbe: ein weicher Lichtschein, der von der Mitte der Unterkante aus
        // in alle Richtungen ausläuft – keine Kanten, keine Ecken.
        _shimmer = compositor.CreateSpriteVisual();
        var glow = compositor.CreateRadialGradientBrush();
        glow.MappingMode = CompositionMappingMode.Relative;
        glow.EllipseCenter = new Vector2(0.5f, 1);
        glow.EllipseRadius = new Vector2(0.5f, 1);
        _shimmerStop = compositor.CreateColorGradientStop(0, Color.FromArgb(ShimmerAlpha, 255, 138, 102));
        _shimmerMid = compositor.CreateColorGradientStop(0.45f, Color.FromArgb(ShimmerAlpha / 3, 255, 138, 102));
        _shimmerEdge = compositor.CreateColorGradientStop(1, Color.FromArgb(0, 255, 138, 102));
        glow.ColorStops.Add(_shimmerStop);
        glow.ColorStops.Add(_shimmerMid);
        glow.ColorStops.Add(_shimmerEdge);
        _shimmer.Brush = glow;
        _shimmer.Opacity = 0;
        Bind(_shimmer, "Offset", "Vector3(p.CX - p.W * 0.42, p.H - Min(28, p.H * 0.55), 0)");
        Bind(_shimmer, "Size", "Vector2(p.W * 0.84, Min(28, p.H * 0.55))");
        // Pulsieren (SetGlow) wächst von der Unterkante aus nach oben.
        Bind(_shimmer, "CenterPoint", "Vector3(p.W * 0.42, Min(28, p.H * 0.55), 0)");
        _root.Children.InsertAtTop(_shimmer);

        // Perlen für weitere laufende Aktivitäten.
        _beads = new ShapeVisual[NotchStateMachine.MaxBeads];
        for (var i = 0; i < _beads.Length; i++)
        {
            var bead = compositor.CreateShapeVisual();
            var size = (float)NotchGeometry.BeadSize;
            bead.Size = new Vector2(size, size);
            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.Size = new Vector2(size, size * 1.5f);
            geometry.Offset = new Vector2(0, -size / 2);
            geometry.CornerRadius = new Vector2(size / 2, size / 2);
            var shape = compositor.CreateSpriteShape(geometry);
            shape.FillBrush = black;
            bead.Shapes.Add(shape);
            bead.Opacity = 0;
            bead.Scale = new Vector3(0, 0, 1);
            bead.CenterPoint = new Vector3(0, 0, 0);
            var left = NotchGeometry.BeadGap + i * (NotchGeometry.BeadSize + NotchGeometry.BeadGap);
            Bind(bead, "Offset", $"Vector3(p.CX + p.W / 2 + p.E + {left}, 0, 0)");
            _beads[i] = bead;
            _root.Children.InsertAtTop(bead);
        }
    }

    /// <summary>Die Wurzel, die in den XAML-Baum gehängt wird.</summary>
    public Visual Root => _root;

    public NotchShape Target => _target;

    /// <summary>Form der Perle <paramref name="index"/>; ihr Inhalt hängt sich an Lage, Größe und Deckkraft.</summary>
    public Visual Bead(int index) => _beads[index];

    /// <summary>Clip für den XAML-Inhalt, folgt der Form des Körpers.</summary>
    public CompositionClip CreateContentClip() => _compositor.CreateGeometricClip(CreateBodyGeometry());

    /// <summary>
    /// Animiert zur neuen Form und gibt zurück, wie lange die Bewegung dauert.
    /// Unterbrechbar: Jede neue Bewegung startet aus Position und Geschwindigkeit der laufenden.
    /// </summary>
    public TimeSpan MorphTo(NotchShape shape, AnimationSettings settings, bool reducedMotion, Spring? spring = null)
    {
        var growing = shape.Width * shape.Height >= _target.Width * _target.Height;
        _target = shape;
        var style = reducedMotion && settings.Style == AnimationStyle.Spring ? AnimationStyle.Fade : settings.Style;
        var response = (growing ? settings.ExpandMs : settings.RetractMs) / 1000.0;
        var now = Now;

        switch (style)
        {
            case AnimationStyle.Spring:
                // Ausdehnen mit leichtem Nachfedern, Zurückziehen ruhiger – wie die Dynamic Island.
                var body = spring ?? new Spring(response, growing ? settings.Damping : Math.Min(1.0, settings.Damping + 0.1));
                // Radius und Ohren dürfen nicht überschwingen, sonst klafft kurz eine Lücke.
                var edge = new Spring(body.Response, 1.0);
                var longest = Math.Max(
                    Math.Max(Animate(W, shape.Width, body, now), Animate(H, shape.Height, body, now)),
                    Math.Max(Animate(R, shape.Radius, edge, now), Animate(E, shape.Ear, edge, now)));
                return TimeSpan.FromSeconds(longest);
            case AnimationStyle.EaseOut:
                var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
                var duration = TimeSpan.FromSeconds(response);
                foreach (var (p, to) in new[] { (W, shape.Width), (H, shape.Height), (R, shape.Radius), (E, shape.Ear) })
                {
                    var animation = _compositor.CreateScalarKeyFrameAnimation();
                    animation.InsertExpressionKeyFrame(0, "this.StartingValue");
                    animation.InsertKeyFrame(1, (float)to, easing);
                    animation.Duration = duration;
                    _props.StartAnimation(p, animation);
                    _channels[p].Set(to, now);
                }
                return duration;
            case AnimationStyle.Fade:
                // Überblenden statt Morph (auch bei abgeschalteten Animationseffekten): 150 ms.
                var fade = _compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0, 0.2f);
                fade.InsertKeyFrame(1, 1);
                fade.Duration = TimeSpan.FromMilliseconds(150);
                SetImmediate(shape, now);
                _root.StartAnimation("Opacity", fade);
                return fade.Duration;
            default:
                SetImmediate(shape, now);
                return TimeSpan.Zero;
        }
    }

    /// <summary>Blendet die ganze Notch aus (nach dem Zurückziehen) oder wieder ein.</summary>
    public void SetVisible(bool visible, TimeSpan delay)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, visible ? 1 : 0);
        animation.Duration = TimeSpan.FromMilliseconds(visible ? 120 : 160);
        animation.DelayTime = delay;
        _root.StartAnimation("Opacity", animation);
    }

    /// <summary>Färbt den Schimmer; <c>null</c> blendet ihn aus.</summary>
    public void SetAccent(string? hex, bool visible)
    {
        var target = visible && hex is not null ? 1f : 0f;
        if (target != _shimmerTarget)
        {
            _shimmerTarget = target;
            var opacity = _compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(1, target);
            opacity.Duration = TimeSpan.FromMilliseconds(260);
            _shimmer.StartAnimation("Opacity", opacity);
        }

        if (hex is null || hex == _accent)
        {
            return;
        }
        _accent = hex;
        Tint(TimeSpan.FromMilliseconds(400));
    }

    /// <summary>
    /// Lässt den Schimmer mit einem Pegel (0–1) schwingen: höher und heller, je lauter.
    /// Die Werte kommen etwa 15-mal pro Sekunde; kurze Übergänge glätten dazwischen.
    /// </summary>
    public void SetGlow(double? level, bool reducedMotion)
    {
        if (reducedMotion)
        {
            level = null;
        }
        if (level == _glow || (level is { } l && _glow is { } g && Math.Abs(l - g) < 0.01))
        {
            return;
        }
        var rising = (level ?? 0) >= (_glow ?? 0);
        _glow = level;
        // Schnell hoch, langsamer zurück – wie ein Pegelmesser.
        var duration = TimeSpan.FromMilliseconds(level is null ? 300 : rising ? 70 : 180);
        var x = (float)(level ?? 0);
        var scale = _compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1, level is null ? Vector3.One : new Vector3(1 + 0.12f * x, 0.75f + 1.6f * x, 1));
        scale.Duration = duration;
        _shimmer.StartAnimation("Scale", scale);
        Tint(duration);
    }

    private void Tint(TimeSpan duration)
    {
        if (_accent is null)
        {
            return;
        }
        var (_, r, g, b) = AccentColor.Parse(_accent);
        var gain = _glow is { } level ? 0.55 + 1.45 * level : 1;
        var alpha = (byte)Math.Clamp(ShimmerAlpha * gain, 0, 255);
        foreach (var (stop, a) in new[] { (_shimmerStop, alpha), (_shimmerMid, (byte)(alpha / 3)), (_shimmerEdge, (byte)0) })
        {
            var color = _compositor.CreateColorKeyFrameAnimation();
            color.InsertKeyFrame(1, Color.FromArgb(a, r, g, b));
            color.Duration = duration;
            stop.StartAnimation("Color", color);
        }
    }

    /// <summary>Zeigt so viele Perlen wie nötig; sie wachsen aus dem rechten Ohr.</summary>
    public void SetBeadCount(int count, AnimationSettings settings, bool reducedMotion)
    {
        for (var i = 0; i < _beads.Length; i++)
        {
            var visible = i < count;
            var bead = _beads[i];
            var delay = TimeSpan.FromMilliseconds(visible ? 60 * (i + 1) : 0);
            if (reducedMotion || settings.Style is AnimationStyle.None or AnimationStyle.Fade)
            {
                bead.Scale = visible ? Vector3.One : new Vector3(0, 0, 1);
            }
            else
            {
                // Ausblenden schnell und ohne Nachfedern: Die Perle schrumpft in das Ohr, während die Notch aufgeht.
                var spring = new Spring(settings.ExpandMs / 1000.0 * (visible ? 0.8 : 0.35), visible ? settings.Damping : 1);
                var scale = CreateScaleCurve(_compositor, spring, bead.Scale.X, visible ? 1 : 0, 0);
                scale.DelayTime = delay;
                bead.StartAnimation("Scale", scale);
            }
            var opacity = _compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(1, visible ? 1 : 0);
            opacity.Duration = TimeSpan.FromMilliseconds(visible ? 160 : 90);
            opacity.DelayTime = delay;
            bead.StartAnimation("Opacity", opacity);
        }
    }

    private double Animate(string property, double to, Spring spring, double now)
    {
        var channel = _channels[property];
        var (from, velocity) = channel.Retarget(to, spring, now);
        _props.StartAnimation(property, CreateCurve(_compositor, spring, from, to, velocity, channel.Duration));
        return channel.Duration;
    }

    // ───────── Federkurven ─────────
    // Die eingebaute NaturalMotion-Feder von Composition hält sich nicht an die eingestellte Periode
    // (gemessen: ~2 s statt 0,4 s). Deshalb wird die Feder hier exakt berechnet und dem Compositor als
    // dichte Keyframe-Kurve übergeben. Er interpoliert pro Bild des Monitors (60/144/240 Hz).

    private const double KeyFrameStep = 1.0 / 120;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Monotone Zeit in Sekunden für die Federzustände.</summary>
    public static double Now => Clock.Elapsed.TotalSeconds;

    public static ScalarKeyFrameAnimation CreateCurve(Compositor compositor, Spring spring, double from, double to, double velocity, double? duration = null)
    {
        var length = duration ?? spring.SettlingTime(from, to, velocity);
        var animation = compositor.CreateScalarKeyFrameAnimation();
        var linear = compositor.CreateLinearEasingFunction();
        var steps = Math.Clamp((int)Math.Ceiling(length / KeyFrameStep), 2, 360);
        for (var i = 0; i <= steps; i++)
        {
            var t = length * i / steps;
            var value = i == steps ? to : spring.Evaluate(from, to, velocity, t).Value;
            animation.InsertKeyFrame((float)i / steps, (float)value, linear);
        }
        animation.Duration = TimeSpan.FromSeconds(Math.Max(length, 0.017));
        return animation;
    }

    /// <summary>Kurve über beliebige Werte: <paramref name="sample"/> liefert den Wert zur Zeit t (Sekunden).</summary>
    public static Vector2KeyFrameAnimation CreateCurve2(Compositor compositor, double length, Func<double, Vector2> sample)
    {
        var animation = compositor.CreateVector2KeyFrameAnimation();
        var linear = compositor.CreateLinearEasingFunction();
        var steps = Math.Clamp((int)Math.Ceiling(length / KeyFrameStep), 2, 360);
        for (var i = 0; i <= steps; i++)
        {
            animation.InsertKeyFrame((float)i / steps, sample(length * i / steps), linear);
        }
        animation.Duration = TimeSpan.FromSeconds(Math.Max(length, 0.017));
        return animation;
    }

    /// <inheritdoc cref="CreateCurve2"/>
    public static Vector3KeyFrameAnimation CreateCurve3(Compositor compositor, double length, Func<double, Vector3> sample)
    {
        var animation = compositor.CreateVector3KeyFrameAnimation();
        var linear = compositor.CreateLinearEasingFunction();
        var steps = Math.Clamp((int)Math.Ceiling(length / KeyFrameStep), 2, 360);
        for (var i = 0; i <= steps; i++)
        {
            animation.InsertKeyFrame((float)i / steps, sample(length * i / steps), linear);
        }
        animation.Duration = TimeSpan.FromSeconds(Math.Max(length, 0.017));
        return animation;
    }

    /// <summary>Gleichmäßige Skalierung (x = y) entlang einer Federkurve.</summary>
    public static Vector3KeyFrameAnimation CreateScaleCurve(Compositor compositor, Spring spring, double from, double to, double velocity) =>
        CreateVectorCurve(compositor, spring, from, to, velocity, s => new Vector3(s, s, 1));

    /// <summary>Beliebige Vector3-Eigenschaft entlang einer Federkurve, z. B. Translation.</summary>
    public static Vector3KeyFrameAnimation CreateVectorCurve(Compositor compositor, Spring spring, double from, double to, double velocity, Func<float, Vector3> map)
    {
        var length = spring.SettlingTime(from, to, velocity, tolerance: 0.001);
        var animation = compositor.CreateVector3KeyFrameAnimation();
        var linear = compositor.CreateLinearEasingFunction();
        var steps = Math.Clamp((int)Math.Ceiling(length / KeyFrameStep), 2, 360);
        for (var i = 0; i <= steps; i++)
        {
            var t = length * i / steps;
            var s = (float)(i == steps ? to : spring.Evaluate(from, to, velocity, t).Value);
            animation.InsertKeyFrame((float)i / steps, map(s), linear);
        }
        animation.Duration = TimeSpan.FromSeconds(Math.Max(length, 0.017));
        return animation;
    }

    private void SetImmediate(NotchShape shape, double now)
    {
        foreach (var (p, value) in new[] { (W, shape.Width), (H, shape.Height), (R, shape.Radius), (E, shape.Ear) })
        {
            _props.StopAnimation(p);
            _props.InsertScalar(p, (float)value);
            _channels[p].Set(value, now);
        }
    }
    private CompositionRoundedRectangleGeometry CreateBodyGeometry()
    {
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        Bind(geometry, "Offset", "Vector2(p.CX - p.W / 2, -p.R)");
        Bind(geometry, "Size", "Vector2(p.W, p.H + p.R)");
        Bind(geometry, "CornerRadius", "Vector2(Min(p.R, p.H), Min(p.R, p.H))");
        return geometry;
    }

    private SpriteVisual CreateEar(bool left)
    {
        var ear = _compositor.CreateSpriteVisual();
        var brush = _compositor.CreateRadialGradientBrush();
        brush.MappingMode = CompositionMappingMode.Relative;
        brush.EllipseCenter = new Vector2(left ? 0 : 1, 1);
        brush.EllipseRadius = new Vector2(1, 1);
        brush.ExtendMode = CompositionGradientExtendMode.Clamp;
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0, Color.FromArgb(0, 0, 0, 0)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.94f, Color.FromArgb(0, 0, 0, 0)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(1, Colors.Black));
        ear.Brush = brush;
        Bind(ear, "Size", "Vector2(p.E, p.E)");
        Bind(ear, "Offset", left ? "Vector3(p.CX - p.W / 2 - p.E + 0.5, 0, 0)" : "Vector3(p.CX + p.W / 2 - 0.5, 0, 0)");
        return ear;
    }

    private void Bind(CompositionObject target, string property, string expression)
    {
        var animation = _compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("p", _props);
        target.StartAnimation(property, animation);
        _owned.Add(animation);
    }

    public void Dispose()
    {
        foreach (var o in _owned)
        {
            o.Dispose();
        }
        _root.Dispose();
        _props.Dispose();
    }
}
