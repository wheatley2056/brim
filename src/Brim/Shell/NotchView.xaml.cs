using System.Numerics;
using Brim.Core;
using Brim.Modules;
using Brim.Core.Widgets;
using Activity = Brim.Core.Activity;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace Brim.Shell;

/// <summary>Die sichtbare Notch: Form (Compositor) und Inhaltsebenen (XAML).</summary>
public sealed partial class NotchView : UserControl
{
    private readonly ActivityHub _hub;
    private readonly NotchSurface _surface;
    private FrameworkElement[] _layers;
    private readonly Dictionary<string, Slot> _slots = new();
    private readonly ContentPresenter[] _beadPresenters = new ContentPresenter[NotchStateMachine.MaxBeads];
    private NotchPresentation? _current;
    private FrameworkElement? _activeLayer;
    private string? _lastAnnouncement;

    internal NotchView(ActivityHub hub, ShelfStore shelf, SettingsStore settings, NearbyModule nearby, Func<nint> window)
    {
        _hub = hub;
        InitializeComponent();

        var (width, height) = NotchGeometry.Canvas;
        CanvasRoot.Width = width;
        CanvasRoot.Height = height;

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _surface = new NotchSurface(compositor, (float)width, (float)height, NotchGeometry.Idle);
        ElementCompositionPreview.SetElementChildVisual(ShapeHost, _surface.Root);
        ElementCompositionPreview.GetElementVisual(ContentHost).Clip = _surface.CreateContentClip();
        (_blur, _blurBrush) = CreateBlur(compositor, (float)width, (float)height);
        ElementCompositionPreview.SetElementChildVisual(BlurHost, _blur);
        InitializeTabs(compositor);
        InitializeClock();
        InitializeBoard(hub, settings);

        _layers = [IdleLayer, CompactLayer, HudLayer, AlertLayer, DropLayer, ExpandedLayer, MiniLayer];
        foreach (var layer in _layers)
        {
            var visual = ElementCompositionPreview.GetElementVisual(layer);
            visual.CenterPoint = new Vector3((float)layer.Width / 2, 0, 0);
        }

        // Der Inhalt jeder Perle folgt ihrer Form im Compositor (Lage, Größe, Deckkraft). Sonst bliebe
        // das Cover beim Aufklappen stehen, während die Form mit dem Ohr nach außen wandert.
        var beadSlots = NotchGeometry.Beads(NotchGeometry.Compact, _beadPresenters.Length);
        for (var i = 0; i < _beadPresenters.Length; i++)
        {
            var presenter = new ContentPresenter
            {
                Width = NotchGeometry.BeadSize,
                Height = NotchGeometry.BeadSize,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var left = width / 2 + beadSlots[i].Left;
            Canvas.SetLeft(presenter, left);
            Canvas.SetTop(presenter, 0);
            BeadHost.Children.Add(presenter);
            _beadPresenters[i] = presenter;

            ElementCompositionPreview.SetIsTranslationEnabled(presenter, true);
            var visual = ElementCompositionPreview.GetElementVisual(presenter);
            foreach (var (property, expression) in new[]
            {
                ("Translation", $"Vector3(b.Offset.X - {left.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 0, 0)"),
                ("Scale", "b.Scale"),
                ("Opacity", "b.Opacity"),
            })
            {
                var animation = compositor.CreateExpressionAnimation(expression);
                animation.SetReferenceParameter("b", _surface.Bead(i));
                visual.StartAnimation(property, animation);
            }
        }

        CanvasRoot.PointerEntered += (_, _) => HoverChanged?.Invoke(true);
        CanvasRoot.PointerExited += (_, _) => HoverChanged?.Invoke(false);
        ContentHost.Tapped += OnContentTapped;

        InitializeShelf(shelf, settings, nearby, window);
        InitializeSettings(window);
        InitializeIdle(settings);
        KeyDown += OnKeyDown;
    }

    /// <summary>Maus betritt oder verlässt die Notch.</summary>
    public event Action<bool>? HoverChanged;

    /// <summary>Klick auf die kompakte Notch.</summary>
    public event Action? ExpandRequested;

    /// <summary>Escape im erweiterten Zustand.</summary>
    public event Action? CollapseRequested;

    public event Action<NotchTab>? TabRequested;

    public NotchMode Mode => _current?.Mode ?? NotchMode.Idle;

    /// <summary>Aktuelle Zielform; die Region des Fensters folgt ihr.</summary>
    public NotchShape TargetShape => _surface.Target;

    /// <summary>Dauer der zuletzt gestarteten Formänderung; so lange bleibt die Fensterregion groß.</summary>
    public TimeSpan LastMorphDuration { get; private set; }

    public int BeadCount => _current?.Mode == NotchMode.Compact ? _current.Beads.Count : 0;

    internal void Present(NotchPresentation p, AnimationSettings animation, bool reducedMotion)
    {
        var previous = _current;
        _current = p;
        _animation = animation;
        _reducedMotion = reducedMotion;
        var modeChanged = previous?.Mode != p.Mode;

        // Während der Geburt (Einführung) gehört die Form der Animation.
        if (_birth)
        {
            return;
        }

        // Zugeklappt: Bearbeiten von „Jetzt“ und die Einstellungen enden.
        if (p.Mode != NotchMode.Expanded)
        {
            _board?.SetEditing(false);
            SetSettingsOpen(false);
        }
        // Ruhezustand: Werte neben der Uhr bestimmen die Breite, also vor der Form.
        ArrangeIdle();

        // Detailansicht (z. B. CPU) im Tab: Die Notch wird höher.
        _tall = p.Mode == NotchMode.Expanded && !_settingsOpen && p.Tab is NotchTab.System or NotchTab.Plugins
            && _hub.Views(p.Tab).Any(v => v.IsDetail);

        // 1. Form
        var shape = ShapeFor(p.Mode);
        if (modeChanged || shape != _surface.Target)
        {
            // Aus dem Versteck kommt sie mit sichtbarem Federn herunter („Blop“), wie die Dynamic Island.
            Spring? reveal = previous?.Mode == NotchMode.Hidden && p.Mode != NotchMode.Hidden
                ? new Spring(animation.ExpandMs / 1000.0, Math.Min(animation.Damping, 0.6))
                : null;
            LastMorphDuration = _surface.MorphTo(shape, animation, reducedMotion, reveal);
            Morphing?.Invoke(LastMorphDuration);
        }
        var beadCount = p.Mode == NotchMode.Compact ? p.Beads.Count : 0;
        if (beadCount != (previous?.Mode == NotchMode.Compact ? previous.Beads.Count : 0))
        {
            _surface.SetBeadCount(beadCount, animation, reducedMotion);
        }
        if (p.Mode == NotchMode.Hidden)
        {
            _surface.SetVisible(false, TimeSpan.FromMilliseconds(animation.RetractMs * 0.8));
        }
        else if (previous?.Mode == NotchMode.Hidden || previous is null)
        {
            _surface.SetVisible(true, TimeSpan.Zero);
        }
        CanvasRoot.Opacity = p.Mode == NotchMode.Mini ? 0.75 : 1;
        var accent = p.Primary?.Activity.Accent;
        _surface.SetAccent(accent, p.Mode is NotchMode.Compact or NotchMode.Expanded);
        _surface.SetGlow(accent is null ? null : p.Primary?.Activity.Glow, reducedMotion);

        // 2. Inhalte der Ebenen
        var target = p.Mode switch
        {
            NotchMode.Idle => UpdateIdle(p),
            NotchMode.Compact => UpdateCompact(p),
            NotchMode.Hud => UpdateHud(p),
            NotchMode.Alert => UpdateAlert(p),
            NotchMode.Expanded => UpdateExpanded(p),
            NotchMode.Drop => DropLayerFor(),
            NotchMode.Mini => UpdateMini(p),
            _ => null,
        };
        UpdateBeads(p, beadCount);

        // 3. Ebenen wechseln
        if (target != _activeLayer)
        {
            var growing = previous is null || NotchGeometry.For(previous.Mode).Height <= shape.Height;
            SwitchLayer(target, animation, reducedMotion, growing);
        }

        // 4. Uhr gleitet mit derselben Feder wie die Form an ihren neuen Platz.
        var expanding = previous is null || NotchGeometry.For(previous.Mode).Height <= shape.Height;
        var clockSpring = expanding
            ? new Spring(animation.ExpandMs / 1000.0, animation.Damping)
            : new Spring(animation.RetractMs / 1000.0, Math.Min(1.0, animation.Damping + 0.1));
        UpdateClock(p, clockSpring, modeChanged);

        Announce(p, previous);
    }

    // ───────── Hover ─────────

    /// <summary>Weich und mit spürbarem Nachfedern, damit die Notch auf die Maus „reagiert“.</summary>
    private static readonly Spring HoverSpring = new(0.30, 0.58);
    private const double HoverContentScale = 1.06;

    private bool _hovering;
    private string? _nowKey;
    private AnimationSettings _animation = new();
    private bool _reducedMotion;
    private readonly SpringChannel _hoverScale = new(1);

    private NotchForm _form;

    /// <summary>Form und Größe aus Einstellungen → Aussehen. Die Größe skaliert die ganze Zeichenfläche.</summary>
    public void ApplyAppearance(AppearanceSettings appearance)
    {
        _form = appearance.Form;
        // Die Insel schwebt: Alles rückt um den Abstand nach unten; die Zeichenfläche hat dafür Platz.
        CanvasRoot.Padding = new Thickness(0, NotchGeometry.Gap(_form), 0, 0);
        CanvasRoot.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0);
        CanvasRoot.RenderTransform = appearance.Size == 1 ? null : new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = appearance.Size, ScaleY = appearance.Size };

        // Schrift: Widgets lesen die Ressourcen beim nächsten Zeichnen; Uhr, Tabs und feste Texte hier.
        var (text, menu, clock) = NotchFonts.Apply(appearance);
        FontFamily = text;
        SharedClock.FontFamily = clock;
        foreach (var tab in TabStrip.Children.OfType<Button>())
        {
            tab.FontFamily = menu;
        }
        _clockPlaced = false;
    }

    public NotchForm Form => _form;

    public void SetMaterial(NotchMaterial material) => _surface.SetGlass(material != NotchMaterial.Black,
        material is NotchMaterial.Mica or NotchMaterial.MicaAlt ? (byte)0x28 : NotchSurface.GlassTint);

    /// <summary>Eine Formänderung beginnt und dauert so lange (auch das Anschwellen beim Hover).</summary>
    public event Action<TimeSpan>? Morphing;

    /// <summary>Form in diesem Moment, auch mitten in einer Animation.</summary>
    public NotchShape CurrentShape => _surface.Current;

    private bool _tall;

    private NotchShape ShapeFor(NotchMode mode)
    {
        var shape = mode == NotchMode.Idle ? NotchGeometry.For(mode, _form) with { Width = _idle.Width }
            : mode != NotchMode.Expanded ? NotchGeometry.For(mode, _form)
            : _settingsOpen || _onboarding ? NotchGeometry.Styled(LargeShape, _form)
            : _tall ? NotchGeometry.Styled(NotchGeometry.ExpandedTall, _form)
            : NotchGeometry.For(mode, _form);
        return _hovering && NotchGeometry.SwellsOnHover(mode) ? NotchGeometry.Swell(shape) : shape;
    }

    /// <summary>
    /// Maus auf der Notch: Sie schwillt sofort an. Klappt sie danach auf, übernimmt die Feder
    /// den Schwung dieser Bewegung, statt neu anzusetzen.
    /// </summary>
    internal void SetHover(bool hovering)
    {
        if (_hovering == hovering || _current is null)
        {
            return;
        }
        _hovering = hovering;
        var shape = ShapeFor(_current.Mode);
        if (shape != _surface.Target)
        {
            LastMorphDuration = _surface.MorphTo(shape, _animation, _reducedMotion, _reducedMotion ? null : HoverSpring);
            Morphing?.Invoke(LastMorphDuration);
        }
        if (!_reducedMotion && _activeLayer is { } layer && NotchGeometry.SwellsOnHover(_current.Mode))
        {
            ScaleActiveContent(layer, hovering ? HoverContentScale : 1, HoverSpring);
        }
        UpdateClock(_current, HoverSpring, modeChanged: false);
    }

    // ───────── Unschärfe ─────────

    private readonly SpriteVisual _blur;
    private readonly CompositionEffectBrush _blurBrush;
    private CompositionScopedBatch? _blurBatch;

    /// <summary>
    /// Ebene über dem Inhalt, die zeigt, was darunter liegt – weichgezeichnet und auf Schwarz,
    /// damit die Kanten der Notch beim Weichzeichnen nicht durchscheinen.
    /// </summary>
    private (SpriteVisual, CompositionEffectBrush) CreateBlur(Compositor compositor, float width, float height)
    {
        var effect = new CompositeEffect
        {
            Mode = CanvasComposite.SourceOver,
            Sources =
            {
                new ColorSourceEffect { Color = Microsoft.UI.Colors.Black },
                new GaussianBlurEffect
                {
                    Name = "Blur",
                    BlurAmount = 0,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Speed,
                    Source = new CompositionEffectSourceParameter("Backdrop"),
                },
            },
        };
        var brush = compositor.CreateEffectFactory(effect, ["Blur.BlurAmount"]).CreateBrush();
        brush.SetSourceParameter("Backdrop", compositor.CreateBackdropBrush());

        var sprite = compositor.CreateSpriteVisual();
        sprite.Size = new Vector2(width, height);
        sprite.Brush = brush;
        sprite.Clip = _surface.CreateContentClip();
        sprite.IsVisible = false;
        return (sprite, brush);
    }

    /// <summary>Inhalt taucht aus der Unschärfe auf; danach wird die Ebene abgeschaltet, damit sie nichts kostet.</summary>
    private void BlurIn(float amount, TimeSpan duration)
    {
        var compositor = _blur.Compositor;
        _blur.IsVisible = true;
        _blurBrush.Properties.InsertScalar("Blur.BlurAmount", amount);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var sharpen = compositor.CreateScalarKeyFrameAnimation();
        sharpen.InsertKeyFrame(0, amount);
        sharpen.InsertKeyFrame(1, 0, compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.6f), new Vector2(0.3f, 1)));
        sharpen.Duration = duration;
        _blurBrush.StartAnimation("Blur.BlurAmount", sharpen);
        batch.End();
        _blurBatch = batch;
        batch.Completed += (_, _) =>
        {
            // Nur abschalten, wenn kein neuer Übergang begonnen hat.
            if (_blurBatch == batch)
            {
                _blur.IsVisible = false;
            }
        };
    }

    private void ScaleActiveContent(FrameworkElement layer, double to, Spring spring)
    {
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        var (from, velocity) = _hoverScale.Retarget(to, spring, NotchSurface.Now);
        visual.StartAnimation("Scale", NotchSurface.CreateScaleCurve(visual.Compositor, spring, from, to, velocity));
    }

    // ───────── Ebenen ─────────

    private FrameworkElement UpdateIdle(NotchPresentation p)
    {
        // Die Uhr selbst ist ein gemeinsames Element über den Ebenen (NotchView.Clock.cs); daneben die Werte.
        return IdleLayer;
    }

    private FrameworkElement UpdateCompact(NotchPresentation p)
    {
        Render("leading", LeadingPresenter, p.Primary, p.Primary?.Activity.Leading);
        Render("trailing", TrailingPresenter, p.Primary, p.Primary?.Activity.Trailing);
        return CompactLayer;
    }

    private FrameworkElement UpdateHud(NotchPresentation p)
    {
        var a = p.Primary?.Activity;
        Render("hud", HudPresenter, p.Primary, a?.Content ?? CompactFallback(a));
        return HudLayer;
    }

    /// <summary>Spielmodus „Mini“: halbtransparent, damit sie das Spiel möglichst wenig verdeckt (Plan 4.8).</summary>
    private FrameworkElement UpdateMini(NotchPresentation p)
    {
        Render("mini", MiniPresenter, p.Primary, p.Primary?.Activity.Content);
        return MiniLayer;
    }

    private FrameworkElement UpdateAlert(NotchPresentation p)
    {
        var a = p.Primary?.Activity;
        Render("alert", AlertPresenter, p.Primary, a?.Content ?? a?.Expanded ?? CompactFallback(a));
        return AlertLayer;
    }

    private static Widget? CompactFallback(Activity? a) =>
        a is null ? null : new StackWidget(a.Leading ?? new SpacerWidget(), a.Trailing ?? new SpacerWidget())
        {
            Justify = Justify.SpaceBetween,
            Padding = (14, 0, 16, 0),
        };

    private FrameworkElement UpdateExpanded(NotchPresentation p)
    {
        if (_onboarding && _onboardingLayer is not null)
        {
            return _onboardingLayer;
        }
        if (_settingsOpen && _settingsLayer is not null)
        {
            return _settingsLayer;
        }
        ExpandedLayer.Height = _tall ? NotchGeometry.ExpandedTall.Height : NotchGeometry.Expanded.Height;
        // Die Uhr der Kopfzeile ist die gemeinsame Uhr (NotchView.Clock.cs).
        UpdateTabs(p.Tab);

        // Beim Öffnen des Tabs neu prüfen, ob Dateien inzwischen fehlen (markiert, nicht versteckt).
        var shelfShown = p.Tab == NotchTab.Shelf;
        if (shelfShown && (ShelfPresenter.Visibility != Visibility.Visible || _activeLayer != ExpandedLayer))
        {
            _shelfView?.Refresh();
        }
        ShelfPresenter.Visibility = shelfShown ? Visibility.Visible : Visibility.Collapsed;
        if (p.Tab != NotchTab.Now)
        {
            _board!.Visibility = Visibility.Collapsed;
            _board.SetEditing(false);
        }
        var cardsShown = p.Tab is NotchTab.System or NotchTab.Plugins && UpdateTabViews(p.Tab);
        TabViewPanel.Visibility = cardsShown ? Visibility.Visible : Visibility.Collapsed;
        if (p.Tab == NotchTab.Shelf || cardsShown)
        {
            NowPresenter.Visibility = Visibility.Collapsed;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
        }
        else if (p.Tab == NotchTab.Now)
        {
            var source = p.Primary ?? p.Idle;
            Render("now", NowPresenter, source, source?.Activity.Expanded);
            NowPresenter.Visibility = Visibility.Visible;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            _board!.Visibility = Visibility.Visible;
            _board.Update();

            // Anderer Inhalt im offenen Zustand (z. B. Audio-Menü statt Player): weich hereinholen.
            var sourceKey = source?.Key;
            if (sourceKey != _nowKey && _activeLayer == ExpandedLayer && !_reducedMotion && _animation.Style is not AnimationStyle.None)
            {
                BlurIn(8, TimeSpan.FromMilliseconds(220));
                if (NowPresenter.Content is Panel now)
                {
                    var spring = new Spring(_animation.ExpandMs / 1000.0, _animation.Damping);
                    for (var i = 0; i < now.Children.Count; i++)
                    {
                        Arrive(now.Children[i], new Vector3(0, 10 + 4 * i, 0), TimeSpan.FromMilliseconds(35 * i), spring);
                    }
                }
            }
            _nowKey = sourceKey;
        }
        else
        {
            NowPresenter.Visibility = Visibility.Collapsed;
            PlaceholderPanel.Visibility = Visibility.Visible;
            (PlaceholderIcon.Glyph, PlaceholderTitle.Text, PlaceholderText.Text) = p.Tab switch
            {
                NotchTab.System => (Glyphs.Settings, "System", "Keine Messwerte verfügbar."),
                _ => (Glyphs.Puzzle, "Plugins", "Plugins sind abgeschaltet."),
            };
        }
        return ExpandedLayer;
    }

    /// <summary>Inhalt der Perlen; Ein- und Ausblenden kommt von ihrer Form (siehe Konstruktor).</summary>
    private void UpdateBeads(NotchPresentation p, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var bead = p.Beads[i];
            Render("bead" + i, _beadPresenters[i], bead, bead.Activity.Bead ?? bead.Activity.Leading);
        }
    }

    private void SwitchLayer(FrameworkElement? target, AnimationSettings animation, bool reducedMotion, bool growing)
    {
        var instant = reducedMotion || animation.Style == AnimationStyle.None;
        foreach (var layer in _layers)
        {
            if (layer == target || layer.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            var leaving = layer;
            if (instant)
            {
                leaving.Visibility = Visibility.Collapsed;
                continue;
            }
            AnimateOut(leaving, growing, animation);
        }

        _activeLayer = target;
        if (target is null)
        {
            return;
        }
        target.Visibility = Visibility.Visible;
        if (instant)
        {
            var visual = ElementCompositionPreview.GetElementVisual(target);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.Opacity = 1;
            visual.Scale = Vector3.One;
            return;
        }
        AnimateIn(target, growing, animation);
    }

    // Inhaltswechsel mit derselben Feder wie die Form, damit Inhalt und Körper wie ein Stück wirken.
    // Alles läuft im Compositor und damit mit der Bildwiederholrate des Monitors.

    private void AnimateIn(FrameworkElement layer, bool growing, AnimationSettings animation)
    {
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        var compositor = visual.Compositor;
        // Beim Wachsen kommt der Inhalt fast sofort mit; beim Schrumpfen kurz warten, bis Platz ist.
        var delay = TimeSpan.FromMilliseconds(growing ? 30 : 70);
        var spring = new Spring((growing ? animation.ExpandMs : animation.RetractMs) / 1000.0, animation.Damping);

        BlurIn(growing ? 14 : 9, TimeSpan.FromMilliseconds(growing ? 300 : 220));
        Stagger(layer, delay, spring);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1)));
        opacity.Duration = TimeSpan.FromMilliseconds(growing ? 180 : 140);
        opacity.DelayTime = delay;
        opacity.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", opacity);

        // Bleibt die Maus auf der Notch, endet der Inhalt gleich in der angeschwollenen Größe.
        var end = _hovering && _current is { } p && NotchGeometry.SwellsOnHover(p.Mode) ? HoverContentScale : 1;
        _hoverScale.Set(end, NotchSurface.Now);
        var from = growing ? 0.9f : 1.08f;
        visual.StopAnimation("Scale");
        visual.Scale = new Vector3(from, from, 1);
        var scale = NotchSurface.CreateScaleCurve(compositor, spring, from, end, 0);
        scale.DelayTime = delay;
        scale.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>
    /// Teile des Inhalts kommen nacheinander: beim Aufklappen fallen Kopfzeile, Medien und Knöpfe
    /// von oben herein, in der kompakten Notch gleiten Cover und Pegel aus der Mitte nach außen.
    /// </summary>
    private void Stagger(FrameworkElement layer, TimeSpan baseDelay, Spring spring)
    {
        var parts = new List<(UIElement Element, Vector3 Offset)>();
        if (layer == ExpandedLayer)
        {
            parts.Add((ExpandedLayer.Children[0], new Vector3(0, -10, 0)));
            if (NowPresenter.Visibility == Visibility.Visible && NowPresenter.Content is Panel now)
            {
                parts.AddRange(now.Children.Select((c, i) => (c, new Vector3(0, -14 - 6 * i, 0))));
            }
            else if (TabViewPanel.Visibility == Visibility.Visible)
            {
                parts.Add((TabViewMain, new Vector3(0, -14, 0)));
                parts.Add((TabViewSide, new Vector3(0, -20, 0)));
            }
            else
            {
                parts.Add((ShelfPresenter.Visibility == Visibility.Visible ? ShelfPresenter : PlaceholderPanel, new Vector3(0, -14, 0)));
            }
        }
        else if (layer == CompactLayer)
        {
            parts.Add((LeadingPresenter, new Vector3(14, 0, 0)));
            parts.Add((TrailingPresenter, new Vector3(-14, 0, 0)));
        }

        var step = TimeSpan.FromMilliseconds(layer == ExpandedLayer ? 45 : 25);
        for (var i = 0; i < parts.Count; i++)
        {
            Arrive(parts[i].Element, parts[i].Offset, baseDelay + step * i, spring);
        }
    }

    private static void Arrive(UIElement element, Vector3 offset, TimeSpan delay, Spring spring)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        var translation = NotchSurface.CreateVectorCurve(compositor, spring, 1, 0, 0, s => offset * s);
        translation.DelayTime = delay;
        translation.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Translation", translation);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1)));
        opacity.Duration = TimeSpan.FromMilliseconds(180);
        opacity.DelayTime = delay;
        opacity.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", opacity);
    }

    private void AnimateOut(FrameworkElement layer, bool growing, AnimationSettings animation)
    {
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        var compositor = visual.Compositor;
        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1, 0, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(1, 1)));
        opacity.Duration = TimeSpan.FromMilliseconds(80);
        visual.StartAnimation("Opacity", opacity);

        // Der alte Inhalt folgt kurz der Bewegung der Form, statt stehen zu bleiben.
        var to = growing ? 1.1f : 0.9f;
        var spring = new Spring((growing ? animation.ExpandMs : animation.RetractMs) / 1000.0, 1);
        visual.StartAnimation("Scale", NotchSurface.CreateScaleCurve(compositor, spring, visual.Scale.X, to, 0));

        batch.End();
        batch.Completed += (_, _) =>
        {
            if (_activeLayer != layer)
            {
                layer.Visibility = Visibility.Collapsed;
            }
        };
    }

    // ───────── Rendern ─────────

    private sealed class Slot
    {
        public required WidgetRenderer Renderer { get; init; }
        public string? ModuleId { get; set; }
    }

    private void Render(string slotName, ContentPresenter presenter, ActiveActivity? source, Widget? widget) =>
        Render(slotName, presenter, source?.Activity.ModuleId, widget);

    private void Render(string slotName, ContentPresenter presenter, string? moduleId, Widget? widget)
    {
        if (!_slots.TryGetValue(slotName, out var slot))
        {
            Slot? created = null;
            created = new Slot
            {
                Renderer = new WidgetRenderer((action, value) =>
                {
                    if (created!.ModuleId is { } module)
                    {
                        _hub.Invoke(new ActionInvocation(module, action, value));
                    }
                }),
            };
            slot = created;
            _slots[slotName] = slot;
        }

        if (widget is null || moduleId is null)
        {
            presenter.Content = null;
            slot.ModuleId = null;
            return;
        }

        // Wechselt das Modul, nichts wiederverwenden: sonst gehörten Knöpfe dem falschen Besitzer.
        var existing = slot.ModuleId == moduleId ? presenter.Content as FrameworkElement : null;
        slot.ModuleId = moduleId;
        var element = slot.Renderer.Render(widget, existing);
        if (!ReferenceEquals(element, presenter.Content))
        {
            presenter.Content = element;
        }
    }

    // ───────── Eingaben ─────────

    private void OnContentTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_current?.Mode is NotchMode.Idle or NotchMode.Compact or NotchMode.Hud)
        {
            ExpandRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<NotchTab>(tag, out var tab))
        {
            TabRequested?.Invoke(tab);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CollapseRequested?.Invoke();
            e.Handled = true;
        }
    }

    /// <summary>Fokus auf den aktiven Tab, wenn die Notch per Tastenkürzel aufgeklappt wurde.</summary>
    public void FocusTabs()
    {
        var tab = _current?.Tab switch
        {
            NotchTab.Shelf => TabShelf,
            NotchTab.System => TabSystem,
            NotchTab.Plugins => TabPlugins,
            _ => TabNow,
        };
        tab.Focus(FocusState.Keyboard);
    }

    // ───────── Barrierefreiheit ─────────

    private void Announce(NotchPresentation p, NotchPresentation? previous)
    {
        if (p.Mode is not (NotchMode.Hud or NotchMode.Alert or NotchMode.Compact) || p.Primary is null)
        {
            return;
        }
        var text = p.Primary.Activity.Announcement;
        var isNew = previous?.Primary?.Key != p.Primary.Key || previous?.Mode != p.Mode;
        if (text is null || (!isNew && text == _lastAnnouncement) || (p.Mode == NotchMode.Compact && !isNew))
        {
            return;
        }
        _lastAnnouncement = text;
        var peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            p.Mode == NotchMode.Alert ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
            text,
            "Brim." + p.Mode);
    }

    // ───────── Kacheln auf „Jetzt“ ─────────

    private NowBoard? _board;
    private Button? _boardDone;

    /// <summary>
    /// Das Raster ersetzt den einfachen Inhalt von „Jetzt“; der bisherige Inhalt (NowPresenter) wird zur Kachel
    /// „Laufende Aktivität“. Beim Bearbeiten bleibt die Notch offen, und „Fertig“ steht statt der Uhr oben rechts.
    /// </summary>
    private void InitializeBoard(ActivityHub hub, SettingsStore settings)
    {
        // Nicht über NowPresenter.Parent: Im Konstruktor ist Parent noch leer, die Kinder der Raster aber stehen.
        var host = ExpandedLayer.Children.OfType<Panel>().First(p => p.Children.Contains(NowPresenter));
        var index = host.Children.IndexOf(NowPresenter);
        host.Children.Remove(NowPresenter);
        _board = new NowBoard(hub, settings, NowPresenter);
        host.Children.Insert(index, _board);

        _boardDone = new Button
        {
            Content = "Fertig",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 2, 12, 3),
            CornerRadius = new CornerRadius(12),
            Visibility = Visibility.Collapsed,
        };
        _boardDone.Click += (_, _) => _board.SetEditing(false);
        HeaderRow.Children.Add(_boardDone);

        _board.EditingChanged += editing =>
        {
            _boardDone.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            PopupChanged?.Invoke(editing);
            if (_current is { } current)
            {
                UpdateClock(current, new Spring(_animation.ExpandMs / 1000.0, _animation.Damping), modeChanged: false);
            }
        };
    }
}
