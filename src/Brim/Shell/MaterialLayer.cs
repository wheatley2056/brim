using System.Numerics;
using Brim.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Content;
using Microsoft.UI.Dispatching;
using WinComp = Windows.UI.Composition;

namespace Brim.Shell;

/// <summary>
/// Acryl oder Mica genau im Umriss der Notch, mit geglätteten Kanten. Statt das Material als Fensterhintergrund
/// über die (nie geglättete) Fensterregion zuzuschneiden, nimmt diese Klasse den Pinsel des Materials selbst
/// entgegen (<see cref="Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop"/>) und füllt damit Visuals
/// des System-Compositors, die mit Vektor-Clips beschnitten sind: Körper und Perlen als abgerundete Rechtecke,
/// Ohren als Pfad. Eingebunden werden sie über eine <see cref="ChildSiteLink"/> unter der Form der Notch.
/// Bleibt der Pinsel aus, meldet <see cref="Failed"/>; das Fenster nimmt dann den alten Weg über die Region.
/// </summary>
internal sealed partial class MaterialLayer : Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop, IDisposable
{
    /// <summary>So lange darf das System für den Pinsel brauchen, bevor der Rückfall greift.</summary>
    private static readonly TimeSpan BrushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Ohren reichen so weit (Anteil ihrer Größe) unter den Körper, damit an der Naht keine Fuge bleibt.</summary>
    private const float EarOverlap = 0.1f;

    private readonly ISystemBackdropControllerWithTargets _controller;
    private readonly SystemBackdropConfiguration _configuration;
    private readonly ContentIsland _parent;
    private readonly Microsoft.UI.Composition.ContainerVisual _placement;
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _timeout;
    private readonly Vector2 _size;
    private readonly List<IDisposable> _owned = [];
    private WinComp.CompositionBrush? _brush;
    private WinComp.SpriteVisual? _body;
    private readonly WinComp.SpriteVisual?[] _ears = new WinComp.SpriteVisual?[2];
    private readonly WinComp.SpriteVisual?[] _beads = new WinComp.SpriteVisual?[NotchStateMachine.MaxBeads];
    private ContentIsland? _island;
    private ChildSiteLink? _link;
    private NotchOutline? _outline;
    private bool _disposed;

    /// <summary>Der Pinsel kam nicht oder das Einbinden schlug fehl.</summary>
    public event Action? Failed;

    /// <param name="parent">Insel des XAML-Inhalts (<c>XamlRoot.ContentIsland</c>).</param>
    /// <param name="placement">Leeres Visual ganz unten in der Form der Notch; hier erscheint das Material.</param>
    /// <exception cref="NotSupportedException">Das System kann dieses Material nicht.</exception>
    public MaterialLayer(NotchMaterial material, ContentIsland parent, Microsoft.UI.Composition.ContainerVisual placement, Vector2 size, DispatcherQueue queue)
    {
        _parent = parent;
        _placement = placement;
        _size = size;
        _queue = queue;
        _controller = MaterialBackdrop.CreateController(material) ?? throw new NotSupportedException($"Material {material} wird nicht unterstützt");
        _timeout = queue.CreateTimer();
        _timeout.Interval = BrushTimeout;
        _timeout.IsRepeating = false;
        _timeout.Tick += (_, _) =>
        {
            if (_island is null && !_disposed)
            {
                Log.Info("Material: kein Pinsel vom System, Rückfall auf die Fensterregion");
                Failed?.Invoke();
            }
        };
        _timeout.Start();

        // Wie bei MaterialBackdrop: erst die Konfiguration, dann das Ziel.
        _configuration = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(this);
    }

    /// <summary>Vom Controller gesetzt: der Pinsel des Materials (System-Compositor).</summary>
    public WinComp.CompositionBrush? SystemBackdrop
    {
        get => _brush;
        set
        {
            _brush = value;
            // Der Controller kann von einem anderen Thread aus setzen; gebaut wird auf dem der Notch.
            if (_queue.HasThreadAccess)
            {
                Attach();
            }
            else
            {
                _queue.TryEnqueue(Attach);
            }
        }
    }

    /// <summary>Umriss in DIP der Zeichenfläche, wie er gerade gezeichnet wird (auch mitten in einer Animation).</summary>
    public void Update(NotchOutline outline)
    {
        _outline = outline;
        if (_body is null)
        {
            return;
        }
        var b = outline.Body;
        _body.Offset = new Vector3((float)b.X, (float)b.Y, 0);
        _body.Size = new Vector2((float)b.Width, (float)b.Height);
        SetRoundClip(_body, b);

        for (var i = 0; i < _ears.Length; i++)
        {
            if (_ears[i] is not { } ear)
            {
                continue;
            }
            if (i >= outline.Ears.Count)
            {
                ear.IsVisible = false;
                continue;
            }
            var e = outline.Ears[i];
            var left = i == 0;
            var size = (float)e.Width;
            var overlap = size * EarOverlap;
            ear.IsVisible = size > 0.01;
            // Das linke Ohr reicht nach rechts unter den Körper, das rechte nach links.
            ear.Offset = new Vector3((float)e.X - (left ? 0 : overlap), (float)e.Y, 0);
            ear.Size = new Vector2(size + overlap, size);
            if (ear.Clip is WinComp.CompositionGeometricClip clip)
            {
                clip.Scale = new Vector2(size, size);
            }
        }

        for (var i = 0; i < _beads.Length; i++)
        {
            if (_beads[i] is not { } bead)
            {
                continue;
            }
            bead.IsVisible = i < outline.Beads.Count;
            if (bead.IsVisible)
            {
                var r = outline.Beads[i];
                bead.Offset = new Vector3((float)r.X, (float)r.Y, 0);
                bead.Size = new Vector2((float)r.Width, (float)r.Height);
                SetRoundClip(bead, r);
            }
        }
    }

    private void Attach()
    {
        if (_disposed || _brush is not { } brush)
        {
            return;
        }
        if (_body is not null)
        {
            // Neuer Pinsel (z. B. nach einem Wechsel des Designs): nur austauschen.
            foreach (var visual in Visuals())
            {
                visual.Brush = brush;
            }
            return;
        }
        try
        {
            var compositor = brush.Compositor;
            var root = compositor.CreateContainerVisual();
            root.Size = _size;
            _owned.Add(root);

            _body = Sprite(compositor, brush);
            _body.Clip = compositor.CreateGeometricClip(compositor.CreateRoundedRectangleGeometry());
            root.Children.InsertAtTop(_body);
            for (var i = 0; i < _ears.Length; i++)
            {
                var ear = Sprite(compositor, brush);
                ear.Clip = compositor.CreateGeometricClip(CreateEarGeometry(compositor, left: i == 0));
                _ears[i] = ear;
                root.Children.InsertAtTop(ear);
            }
            for (var i = 0; i < _beads.Length; i++)
            {
                var bead = Sprite(compositor, brush);
                bead.Clip = compositor.CreateGeometricClip(compositor.CreateRoundedRectangleGeometry());
                bead.IsVisible = false;
                _beads[i] = bead;
                root.Children.InsertAtTop(bead);
            }

            _island = ContentIsland.CreateForSystemVisual(_queue, root);
            _link = ChildSiteLink.Create(_parent, _placement);
            _link.ActualSize = _size;
            _link.ProcessesPointerInput = false;
            _link.ProcessesKeyboardInput = false;
            _link.Connect(_island);
            _timeout.Stop();
            if (_outline is { } outline)
            {
                Update(outline);
            }
            Log.Info("Material: als Vektorform im Umriss der Notch");
        }
        catch (Exception ex)
        {
            Log.Info($"Material: Einbinden fehlgeschlagen ({ex.Message}), Rückfall auf die Fensterregion");
            Failed?.Invoke();
        }
    }

    private IEnumerable<WinComp.SpriteVisual> Visuals()
    {
        if (_body is not null)
        {
            yield return _body;
        }
        foreach (var visual in _ears.Concat(_beads))
        {
            if (visual is not null)
            {
                yield return visual;
            }
        }
    }

    private WinComp.SpriteVisual Sprite(WinComp.Compositor compositor, WinComp.CompositionBrush brush)
    {
        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = brush;
        _owned.Add(sprite);
        return sprite;
    }

    /// <summary>
    /// Abgerundetes Rechteck als Vektor-Clip: wird vom Compositor geglättet, anders als eine Fensterregion.
    /// (RectangleClip mit Radien gibt es im System-Compositor erst ab Windows 11; Brim läuft ab 19041.)
    /// </summary>
    private static void SetRoundClip(WinComp.SpriteVisual visual, RoundRect r)
    {
        if (visual.Clip is not WinComp.CompositionGeometricClip { Geometry: WinComp.CompositionRoundedRectangleGeometry geometry })
        {
            return;
        }
        var radius = (float)Math.Min(r.Radius, Math.Min(r.Width, r.Height) / 2);
        geometry.Offset = Vector2.Zero;
        geometry.Size = new Vector2((float)r.Width, (float)r.Height);
        geometry.CornerRadius = new Vector2(radius, radius);
    }

    /// <summary>
    /// Ohr in 1 × 1 wie in <see cref="NotchSurface"/> (Quadrat minus Viertelkreis), dazu ein Streifen von
    /// <see cref="EarOverlap"/> unter den Körper. Links beginnt das Ohr bei 0, rechts bei <see cref="EarOverlap"/>.
    /// </summary>
    private WinComp.CompositionPathGeometry CreateEarGeometry(WinComp.Compositor compositor, bool left)
    {
        const float k = 0.5523f;
        const float o = EarOverlap;
        using var builder = new CanvasPathBuilder(CanvasDevice.GetSharedDevice());
        if (left)
        {
            builder.BeginFigure(0, 0);
            builder.AddLine(1 + o, 0);
            builder.AddLine(1 + o, 1);
            builder.AddLine(1, 1);
            builder.AddCubicBezier(new Vector2(1, 1 - k), new Vector2(k, 0), new Vector2(0, 0));
        }
        else
        {
            builder.BeginFigure(1 + o, 0);
            builder.AddLine(0, 0);
            builder.AddLine(0, 1);
            builder.AddLine(o, 1);
            builder.AddCubicBezier(new Vector2(o, 1 - k), new Vector2(o + 1 - k, 0), new Vector2(1 + o, 0));
        }
        builder.EndFigure(CanvasFigureLoop.Closed);
        var geometry = CanvasGeometry.CreatePath(builder);
        _owned.Add(geometry);
        return compositor.CreatePathGeometry(new WinComp.CompositionPath(geometry));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timeout.Stop();
        _controller.RemoveAllSystemBackdropTargets();
        (_controller as IDisposable)?.Dispose();
        _link?.Dispose();
        _island?.Dispose();
        foreach (var o in _owned)
        {
            o.Dispose();
        }
        if (_placement.Parent is { } parent)
        {
            parent.Children.Remove(_placement);
        }
        _placement.Dispose();
    }
}
