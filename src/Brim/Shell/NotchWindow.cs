using Brim.Core;
using Brim.Modules;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinUIEx;
using WinUIEx.Messaging;

namespace Brim.Shell;

/// <summary>
/// Ein Notch-Fenster pro Monitor (Plan 4.1). Rahmenlos, transparent, immer oben,
/// nie aktiviert per Klick. Nur die Fläche der Notch nimmt Eingaben an (Fensterregion).
/// </summary>
internal sealed partial class NotchWindow : WindowEx
{
    private readonly ActivityHub _hub;
    private readonly SettingsStore _settings;
    private readonly UISettings _uiSettings = new();
    private readonly DispatcherQueueTimer _hoverTimer;
    private readonly DispatcherQueueTimer _collapseTimer;
    private readonly DispatcherQueueTimer _expiryTimer;
    private readonly DispatcherQueueTimer _regionTimer;
    private readonly DispatcherQueueTimer _hoverPoll;
    private readonly WindowMessageMonitor _messages;
    private bool _expanded;
    private bool _pinned;
    private bool _hidden;
    private bool _inside;
    private bool _dropTarget;
    private bool _draggingOut;
    private bool _popupOpen;
    private NotchTab _tab = NotchTab.Now;
    private (double X, double Y, double Width, double Height) _region;
    private (double X, double Y, double Width, double Height) _regionTarget;
    /// <summary>DIP der Zeichenfläche → physische Pixel: DPI des Monitors mal gewählte Größe.</summary>
    private double _scale = 1;
    private double _dpiScale = 1;
    private bool? _glass;
    private NotchMaterial? _material;
    private double _glassUntil;

    /// <summary>Material als geglättete Vektorform; null bei Schwarz oder im Rückfall über die Fensterregion.</summary>
    private MaterialLayer? _materialLayer;

    /// <summary>Einmal fehlgeschlagen: für diese Sitzung beim alten Weg bleiben.</summary>
    private bool _vectorMaterialFailed;
    private bool _surfaceVisible = true;

    /// <summary>Material über die Fensterregion zugeschnitten (treppige Kanten) – nur noch als Rückfall.</summary>
    private bool RegionGlass => _glass == true && _materialLayer is null;

    public NotchWindow(DisplayArea display, ActivityHub hub, SettingsStore settings, ShelfStore shelf, NearbyModule nearby)
    {
        Display = display;
        _hub = hub;
        _settings = settings;

        Title = "Brim";
        View = new NotchView(hub, shelf, settings, nearby, () => Handle);
        Content = View;
        SystemBackdrop = new TransparentTintBackdrop { TintColor = Microsoft.UI.Colors.Transparent };


        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        Handle = this.GetWindowHandle();

        // WinUI zieht oben einen Rahmen für das Verändern der Größe ein, den Windows weiß zeichnet.
        // Ohne Nicht-Client-Bereich gehört das ganze Fenster der Notch.
        _messages = new WindowMessageMonitor(this);
        _messages.WindowMessageReceived += (_, e) =>
        {
            if (e.Message.MessageId == Win32.WmNcCalcSize && e.Message.WParam != 0)
            {
                e.Result = 0;
                e.Handled = true;
            }
        };
        Win32.MakeOverlay(Handle);

        var queue = DispatcherQueue;
        _hoverTimer = CreateTimer(queue, () => SetExpanded(true, pinned: false));
        _collapseTimer = CreateTimer(queue, CollapseIfIdle);
        _expiryTimer = CreateTimer(queue, Refresh);
        _regionTimer = CreateTimer(queue, ShrinkRegion);
        _hoverPoll = CreateTimer(queue, PollHover);
        _peekPoll = CreateTimer(queue, PollPeek);
        _peekPoll.IsRepeating = true;
        _peekPoll.Interval = TimeSpan.FromMilliseconds(33);
        _hoverPoll.IsRepeating = true;
        _hoverPoll.Interval = TimeSpan.FromMilliseconds(50);

        View.HoverChanged += OnPointer;
        View.Morphing += OnMorphing;
        View.ExpandRequested += () => SetExpanded(true, pinned: false);
        View.CollapseRequested += () => SetExpanded(false, pinned: false);
        View.TabRequested += tab =>
        {
            _tab = tab;
            Refresh();
        };
        View.DropTargetChanged += OnDropTargetChanged;
        View.Dropped += OnDropped;
        View.BirthFinished += OnBirthFinished;
        View.OnboardingFinished += OnOnboardingFinished;
        View.HintChanged += OnHintChanged;
        View.OnboardingStarted += OnOnboardingStarted;
        View.SettingsOpenChanged += open =>
        {
            // Einstellungen: offen bleiben wie bei einem Menü, und Tastatur annehmen.
            Win32.SetActivatable(Handle, open);
            _popupOpen = open;
            if (open)
            {
                _collapseTimer.Stop();
            }
            else if (!CursorInside())
            {
                SetInside(false);
                StartCollapse();
            }
            // Nicht mitten in Present (Zuklappen schließt die Einstellungen) neu zeichnen.
            DispatcherQueue.TryEnqueue(Refresh);
        };
        View.GalleryChanged += open =>
        {
            // Galerie von „Jetzt“: Suche braucht die Tastatur; die Notch wird höher (Form und Region neu).
            Win32.SetActivatable(Handle, open);
            DispatcherQueue.TryEnqueue(Refresh);
        };
        View.PopupChanged += open =>
        {
            _popupOpen = open;
            // Menü zu und Maus woanders: jetzt zuklappen.
            if (!open && !CursorInside())
            {
                SetInside(false);
                StartCollapse();
            }
        };
        View.DraggingOut += active =>
        {
            _draggingOut = active;
            // Nach dem Herausziehen zuklappen, wenn die Maus nicht mehr über der Notch ist.
            if (!active && !CursorInside())
            {
                SetInside(false);
                StartCollapse();
            }
        };
        View.SurfaceVisibleChanging += (visible, delay) =>
        {
            _surfaceVisible = visible;
            _materialLayer?.SetVisible(visible, delay);
        };
        View.Loaded += (_, _) =>
        {
            // Vor dem Laden gibt es keine Insel für das Material; jetzt nachholen.
            if (_material is { } material && material != NotchMaterial.Black && _materialLayer is null && !_vectorMaterialFailed)
            {
                UseMaterial(material);
                ApplyRegion(_region == default ? CurrentHitArea() : _region);
            }
            View.XamlRoot.Changed += (_, _) =>
            {
                if (Math.Abs(View.XamlRoot.RasterizationScale - _dpiScale) > 0.001)
                {
                    Place();
                }
            };
        };

        Closed += (_, _) =>
        {
            _materialLayer?.Dispose();
            _materialLayer = null;
            _hub.NotifyVisibleTab(this, null);
            _hub.NotifyIdleShown(this, false);
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= TrackGlass;
        };

        Place();
        Refresh();
        AppWindow.Show(activateWindow: false);
    }

    public DisplayArea Display { get; private set; }
    public NotchView View { get; }
    public nint Handle { get; }
    public bool IsExpanded => _expanded;

    /// <summary>Arbeitet gerade jemand mit dieser Notch? Dann kein Neustart für Updates.</summary>
    public bool IsBusy => _expanded || _inside || _dropTarget || _draggingOut || _popupOpen;

    /// <summary>Bildwiederholrate des Monitors in Hz (0 = unbekannt).</summary>
    public int RefreshRate { get; private set; }

    public void UpdateDisplay(DisplayArea display)
    {
        Display = display;
        Place();
    }

    /// <summary>Oben mittig auf dem Monitor; unter einer oben angedockten Taskleiste.</summary>
    public void Place()
    {
        var bounds = Display.OuterBounds;
        var top = bounds.Y;
        if (Win32.TaskbarPosition() is { Edge: Win32.EdgeTop } taskbar
            && taskbar.Rect.left < bounds.X + bounds.Width && taskbar.Rect.right > bounds.X
            && taskbar.Rect.top <= bounds.Y + 1)
        {
            top = taskbar.Rect.bottom;
        }

        var appearance = _settings.Current.Appearance.Clamped();
        View.ApplyAppearance(appearance);
        if (_material != appearance.Material)
        {
            // Jedes Windows-Material scheint nur im Umriss durch (Region wie bei Glas).
            _material = appearance.Material;
            _glass = appearance.Material != NotchMaterial.Black;
            UseMaterial(appearance.Material);
            View.SetMaterial(appearance.Material);
            _regionTarget = default;
        }
        // Neue Form sofort zeigen, nicht erst bei der nächsten Aktivität.
        _regionTarget = default;

        // Zweimal: Nach dem Verschieben auf einen anderen Monitor kann sich die DPI ändern.
        for (var pass = 0; pass < 2; pass++)
        {
            _dpiScale = Win32.GetDpi(Handle) / 96.0;
            _scale = _dpiScale * appearance.Size;
            var width = (int)Math.Ceiling(NotchGeometry.Canvas.Width * _scale);
            var height = (int)Math.Ceiling(NotchGeometry.Canvas.Height * _scale);
            var x = bounds.X + (bounds.Width - width) / 2;
            AppWindow.MoveAndResize(new RectInt32(x, top, width, height));
        }
        // Platz für Einstellungen und Einführung: Monitor in DIP der Zeichenfläche, unter der Notch-Oberkante.
        View.SetAvailableSize(bounds.Width / _scale, (bounds.Y + bounds.Height - top) / _scale);
        ApplyRegion(_region == default ? CurrentHitArea() : _region);
        _top = top;

        // Der Compositor taktet die Animationen mit dieser Rate; protokolliert zur Kontrolle.
        RefreshRate = Win32.GetRefreshRate(Handle);
        Log.Info($"Monitor {bounds.Width}×{bounds.Height}, {RefreshRate} Hz, Skalierung {_scale:P0}");
    }

    /// <summary>Neu bewerten, was gezeigt wird, und die Ansicht aktualisieren.</summary>
    public void Refresh()
    {
        var presentation = NotchStateMachine.Resolve(_hub.Snapshot(), new NotchInput(_expanded, _hidden, _tab, _dropTarget));
        var animation = _settings.Current.Animation.Clamped();
        View.Present(presentation, animation, reducedMotion: !_uiSettings.AnimationsEnabled);
        // Module mit teuren Abfragen (Ressourcen) messen nur, solange ihr Tab irgendwo offen ist.
        _hub.NotifyVisibleTab(this, presentation.Mode == NotchMode.Expanded ? presentation.Tab : null);
        // Werte neben der Uhr werden nur gemessen, solange irgendeine Notch ruht und sie zeigt.
        _hub.NotifyIdleShown(this, presentation.Mode == NotchMode.Idle);
        UpdateRegion(animation);
        ScheduleExpiry();
    }

    // ───────── Einführung ─────────

    /// <summary>Erster Start: Die Notch wird aus der Kante geboren und ruht dann; die Schritte kommen beim ersten Aufklappen.</summary>
    public void StartOnboarding()
    {
        // Naht, Begrüßung und Schimmer liegen oben in der Mitte; daneben gehen Klicks weiter durch.
        ApplyRegion((NotchGeometry.Canvas.Width / 2 - 260, 0, 520, 180));
        _regionTarget = default;
        _popupOpen = true;
        View.StartOnboarding(_settings.Current.Animation.Clamped(), reducedMotion: !_uiSettings.AnimationsEnabled);
    }

    private void OnBirthFinished()
    {
        // Geboren: ruhen wie sonst auch, ohne Fokus zu nehmen.
        _popupOpen = false;
        _regionTarget = default;
        Refresh();
    }

    private void OnHintChanged(bool show)
    {
        if (!show)
        {
            _regionTarget = default;
            ApplyRegion(CurrentHitArea());
            return;
        }
        // Die Sprechblase unter der Notch muss sichtbar sein (die Region schneidet auch das Zeichnen ab).
        var notch = CurrentHitArea();
        var hint = View.HintArea;
        var left = Math.Min(notch.X, hint.X);
        var top = Math.Min(notch.Y, hint.Y);
        var right = Math.Max(notch.X + notch.Width, hint.X + hint.Width);
        var bottom = Math.Max(notch.Y + notch.Height, hint.Y + hint.Height);
        ApplyRegion((left, top, right - left, bottom - top));
    }

    private void OnOnboardingStarted()
    {
        // Tastatur für die Schritte (z. B. Rückfrage beim Einrichten), offen bis „Loslegen“.
        Win32.SetActivatable(Handle, true);
        _popupOpen = true;
        _tab = NotchTab.Now;
        SetExpanded(true, pinned: true);
    }

    private void OnOnboardingFinished(bool restart)
    {
        Win32.SetActivatable(Handle, false);
        _popupOpen = false;
        SetExpanded(false, pinned: false);
        OnboardingFinished?.Invoke(restart);
    }

    /// <summary>Einführung beendet (an die App: speichern, ggf. neu starten).</summary>
    public event Action<bool>? OnboardingFinished;

    /// <summary>Per Tastenkürzel oder zweiter Instanz auf- und zuklappen.</summary>
    public void ToggleFromKeyboard()
    {
        if (_expanded)
        {
            SetExpanded(false, pinned: false);
            return;
        }
        if (_hidden)
        {
            PeekFromKeyboard();
        }
        SetExpanded(true, pinned: true);
        Win32.Activate(Handle);
        View.FocusTabs();
    }

    private void SetExpanded(bool expanded, bool pinned)
    {
        _hoverTimer.Stop();
        _collapseTimer.Stop();
        _pinned = expanded && pinned;
        if (_expanded == expanded)
        {
            return;
        }
        _expanded = expanded;
        _hub.NotifyExpanded(expanded);
        if (!expanded)
        {
            _tab = NotchTab.Now;
        }
        Refresh();
        if (!expanded)
        {
            // Lag inzwischen ein Fenster unter der Notch, zieht sie sich jetzt zurück.
            EndKeyboardPeek();
            ApplyAutoHide();
        }
    }

    /// <summary>
    /// XAML meldet Betreten/Verlassen. Ein „Verlassen“ kommt aber auch, wenn sich nur die Fensterregion
    /// ändert (Aufklappen); deshalb zählt die echte Cursorposition. Solange die Maus drin ist, prüft ein
    /// leichter Takt, wann sie wirklich geht.
    /// </summary>
    private void OnPointer(bool entered)
    {
        // Die Mini-Pille über einem Spiel reagiert nicht auf die Maus.
        if (View.Mode == NotchMode.Mini)
        {
            SetInside(false);
            return;
        }
        if (entered || CursorInside())
        {
            SetInside(true);
        }
        else
        {
            SetInside(false);
        }
    }

    private void PollHover()
    {
        if (!CursorInside())
        {
            SetInside(false);
        }
    }

    private void SetInside(bool inside)
    {
        if (_inside == inside)
        {
            return;
        }
        _inside = inside;
        if (inside)
        {
            _hoverPoll.Start();
        }
        else
        {
            _hoverPoll.Stop();
        }

        var interaction = _settings.Current.Interaction;
        View.SetHover(inside);
        if (inside)
        {
            _collapseTimer.Stop();
            if (!_expanded && View.Mode != NotchMode.Hidden)
            {
                _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, interaction.HoverExpandDelayMs));
                _hoverTimer.Start();
            }
        }
        else
        {
            _hoverTimer.Stop();
            if (_expanded)
            {
                // Per Tastatur geöffnet: erst schließen, wenn die Maus einmal drin war und wieder geht.
                _pinned = false;
                StartCollapse();
            }
            else
            {
                ApplyAutoHide();
            }
        }
    }

    private void StartCollapse()
    {
        if (!_expanded)
        {
            return;
        }
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, _settings.Current.Interaction.HoverCollapseDelayMs));
        _collapseTimer.Start();
    }

    /// <summary>Zuklappen – außer während Dateien herausgezogen werden, sonst verschwände die Quelle mitten im Ziehen.</summary>
    private void CollapseIfIdle()
    {
        if (_draggingOut || _dropTarget || _popupOpen)
        {
            return;
        }
        SetExpanded(false, pinned: false);
    }

    // ───────── Ablage ─────────

    private void OnDropTargetChanged(bool over)
    {
        if (over)
        {
            _collapseTimer.Stop();
            _hoverTimer.Stop();
            _dropTarget = true;
            if (_expanded)
            {
                _tab = NotchTab.Shelf;
            }
            Refresh();
            return;
        }
        // DragLeave kommt auch beim Wechsel zwischen inneren Elementen; zählt nur, wenn die Maus wirklich draußen ist.
        if (!CursorInside())
        {
            _dropTarget = false;
            Refresh();
            if (_expanded)
            {
                StartCollapse();
            }
        }
    }

    /// <summary>Nach dem Ablegen klappt die Notch auf und zeigt die Ablage mit dem neuen Eintrag.</summary>
    private void OnDropped()
    {
        _dropTarget = false;
        _tab = NotchTab.Shelf;
        if (_expanded)
        {
            Refresh();
        }
        else
        {
            SetExpanded(true, pinned: false);
        }
        // Während des Ziehens gab es keine Zeigerereignisse; Hover-Zustand nachziehen.
        SetInside(CursorInside());
        if (!_inside)
        {
            StartCollapse();
        }
    }

    private void ScheduleExpiry()
    {
        _expiryTimer.Stop();
        if (_hub.NextExpiry() is { } next)
        {
            var due = next - _hub.Time.GetUtcNow() + TimeSpan.FromMilliseconds(15);
            _expiryTimer.Interval = due > TimeSpan.Zero ? due : TimeSpan.FromMilliseconds(1);
            _expiryTimer.Start();
        }
    }

    // ───────── Region ─────────

    /// <summary>
    /// Eingabefläche. Enthält den Platz fürs Anschwellen schon im Voraus: Jede Änderung der
    /// Fensterregion lässt Windows ein „Maus hat verlassen“ melden, Hover darf sie also nicht ändern.
    /// </summary>
    private (double X, double Y, double Width, double Height) CurrentHitArea()
    {
        var mode = View.Mode;
        if (mode == NotchMode.Hidden)
        {
            return (0, 0, 0, 0);
        }
        var shape = View.HitShape(mode);
        return NotchGeometry.HitArea(shape, View.BeadCount, NotchGeometry.Gap(View.Form));
    }

    /// <summary>Liegt der Mauszeiger wirklich in der aktuellen Eingabefläche?</summary>
    private bool CursorInside()
    {
        if (!Win32.TryGetCursor(out var x, out var y))
        {
            return false;
        }
        var origin = AppWindow.Position;
        var lx = (x - origin.X) / _scale;
        var ly = (y - origin.Y) / _scale;
        var area = _region;
        return lx >= area.X && lx < area.X + area.Width && ly >= area.Y && ly < area.Y + area.Height;
    }

    private void UpdateRegion(AnimationSettings animation)
    {
        var target = CurrentHitArea();
        if (target == _regionTarget)
        {
            return;
        }
        _regionTarget = target;
        // Während der Animation die Vereinigung aus alter und neuer Form, damit nichts abgeschnitten wird;
        // danach auf die Zielform verkleinern, damit darunterliegende Fenster wieder klickbar sind.
        var union = _region == default ? target : NotchGeometry.Union(_region, target);
        if (target.Width > 0)
        {
            union = (union.X - NotchGeometry.Overshoot, 0, union.Width + 2 * NotchGeometry.Overshoot, Math.Min(NotchGeometry.Canvas.Height, union.Height + NotchGeometry.Overshoot));
        }
        ApplyRegion(union);
        _regionTimer.Stop();
        _regionTimer.Interval = View.LastMorphDuration + TimeSpan.FromMilliseconds(60);
        _regionTimer.Start();
    }

    private void ShrinkRegion()
    {
        _regionTimer.Stop();
        ApplyRegion(CurrentHitArea());
    }

    /// <summary>Bei Glas folgt das Acryl der Form Bild für Bild, solange sie sich bewegt.</summary>
    private void OnMorphing(TimeSpan duration)
    {
        if (_glass != true)
        {
            return;
        }
        _glassUntil = Math.Max(_glassUntil, NotchSurface.Now + duration.TotalSeconds + 0.06);
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= TrackGlass;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += TrackGlass;
    }

    private void TrackGlass(object? sender, object e)
    {
        if (_glass != true || NotchSurface.Now > _glassUntil)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= TrackGlass;
        }
        if (_materialLayer is not null)
        {
            UpdateMaterialLayer();
        }
        else if (_glass == true && _region.Width > 0)
        {
            ApplyGlassRegion();
        }
    }

    /// <summary>
    /// Material wählen: bei Acryl und Mica als Vektorform im Umriss (<see cref="MaterialLayer"/>), sonst – und wenn
    /// das System das nicht kann – als Fensterhintergrund, den die Region zuschneidet.
    /// </summary>
    private void UseMaterial(NotchMaterial material)
    {
        if (_materialLayer is { } old)
        {
            old.Dispose();
            if (!old.IsAttached)
            {
                // Abgebrochen, bevor es lief: kein Absturz, die Markierung muss weg.
                VectorMaterialGuard.End();
            }
        }
        _materialLayer = null;
        var transparent = new TransparentTintBackdrop { TintColor = Microsoft.UI.Colors.Transparent };
        if (material == NotchMaterial.Black)
        {
            SystemBackdrop = transparent;
            return;
        }
        if (!_vectorMaterialFailed && !VectorMaterialGuard.Blocked && View.XamlRoot is not null)
        {
            try
            {
                VectorMaterialGuard.Begin();
                Win32.UseHostBackdrop(Handle);
                var layer = new MaterialLayer(material, Handle, DispatcherQueue);
                layer.Attached += VectorMaterialGuard.Attached;
                layer.Failed += () => DispatcherQueue.TryEnqueue(() =>
                {
                    VectorMaterialGuard.End();
                    if (_materialLayer == layer)
                    {
                        _vectorMaterialFailed = true;
                        UseMaterial(material);
                        ApplyRegion(_region);
                    }
                });
                _materialLayer = layer;
                layer.SetVisible(_surfaceVisible, TimeSpan.Zero);
                SystemBackdrop = transparent;
                UpdateMaterialLayer();
                return;
            }
            catch (Exception ex)
            {
                Log.Info($"Material als Vektorform nicht möglich ({ex.Message}), Rückfall auf die Fensterregion");
                VectorMaterialGuard.End();
                _vectorMaterialFailed = true;
            }
        }
        SystemBackdrop = new MaterialBackdrop(material);
    }

    /// <summary>Material auf die Form in diesem Moment legen (ohne Abstand der Insel: der steckt im Rand der Zeichenfläche).</summary>
    private void UpdateMaterialLayer() =>
        _materialLayer?.Update(NotchGeometry.Outline(View.CurrentShape, View.BeadCount, 0), _scale);

    /// <summary>Bei Glas genau der gezeichnete Umriss, sonst schiene das Acryl um die Notch herum durch.</summary>
    private void ApplyGlassRegion() =>
        Win32.SetRegion(Handle, NotchGeometry.Outline(View.CurrentShape, View.BeadCount, NotchGeometry.Gap(View.Form)), _scale);

    private void ApplyRegion((double X, double Y, double Width, double Height) area)
    {
        _region = area;
        UpdateMaterialLayer();
        if (RegionGlass && area.Width > 0)
        {
            ApplyGlassRegion();
            return;
        }
        Win32.SetRegion(Handle,
            (int)Math.Floor(area.X * _scale),
            (int)Math.Floor(area.Y * _scale),
            (int)Math.Ceiling(area.Width * _scale),
            (int)Math.Ceiling(area.Height * _scale));
    }

    private static DispatcherQueueTimer CreateTimer(DispatcherQueue queue, Action tick)
    {
        var timer = queue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }
}
