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
    private double _scale = 1;

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
        View.ExpandRequested += () => SetExpanded(true, pinned: false);
        View.CollapseRequested += () => SetExpanded(false, pinned: false);
        View.TabRequested += tab =>
        {
            _tab = tab;
            Refresh();
        };
        View.DropTargetChanged += OnDropTargetChanged;
        View.Dropped += OnDropped;
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
        View.Loaded += (_, _) =>
        {
            View.XamlRoot.Changed += (_, _) =>
            {
                if (Math.Abs(View.XamlRoot.RasterizationScale - _scale) > 0.001)
                {
                    Place();
                }
            };
        };

        Closed += (_, _) => _hub.NotifyVisibleTab(this, null);

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

        // Zweimal: Nach dem Verschieben auf einen anderen Monitor kann sich die DPI ändern.
        for (var pass = 0; pass < 2; pass++)
        {
            _scale = Win32.GetDpi(Handle) / 96.0;
            var width = (int)Math.Ceiling(NotchGeometry.Canvas.Width * _scale);
            var height = (int)Math.Ceiling(NotchGeometry.Canvas.Height * _scale);
            var x = bounds.X + (bounds.Width - width) / 2;
            AppWindow.MoveAndResize(new RectInt32(x, top, width, height));
        }
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
        UpdateRegion(animation);
        ScheduleExpiry();
    }

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
        var shape = NotchGeometry.SwellsOnHover(mode) ? NotchGeometry.Swell(NotchGeometry.For(mode)) : View.TargetShape;
        return NotchGeometry.HitArea(shape, View.BeadCount);
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

    private void ApplyRegion((double X, double Y, double Width, double Height) area)
    {
        _region = area;
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
