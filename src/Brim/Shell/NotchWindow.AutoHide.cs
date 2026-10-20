using Brim.Core;
using Microsoft.UI.Dispatching;
using Windows.Win32.Foundation;

namespace Brim.Shell;

/// <summary>
/// Automatisches Ausblenden (Plan 4.9): Die Shell sammelt die Fensterlage, <see cref="AutoHideRules"/>
/// entscheidet – Vollbild, maximiert, Fenster im Bereich, Vordergrund, App-Regeln, Bildschirmfreigabe,
/// Leerlauf und Ziehen Richtung Snap-Leiste. Die Maus an der Oberkante oder Win+Alt+N holt sie zurück.
/// </summary>
internal sealed partial class NotchWindow
{
    private readonly DispatcherQueueTimer _peekPoll;
    private int _top;
    private AutoHideReason _reason;
    private DispatcherQueueTimer? _hideDelay;
    private bool _keyboardPeek;
    private bool _hideDelayDone;
    private bool _windowDragging;
    private bool _peeking;
    private long? _peekSince;
    private long? _peekLeftSince;

    /// <summary>Wie lange die Maus die zurückgeholte Notch verlassen darf, bevor sie sich wieder versteckt.</summary>
    private const int PeekLeaveMs = 400;

    /// <summary>
    /// Von <see cref="NotchWindowManager"/> bei jeder Änderung der Fensterlage aufgerufen.
    /// Freigabe und Leerlauf gelten für alle Monitore und kommen deshalb fertig mit.
    /// </summary>
    public void EvaluateAutoHide(WindowWatcher watcher, bool screenSharing, TimeSpan idle)
    {
        var rules = _settings.Current.AutoHide;
        _windowDragging = watcher.IsDragging;
        var bounds = Display.OuterBounds;
        var scene = new AutoHideScene
        {
            Windows = watcher.Scene(NotchZone(), new RECT
            {
                left = bounds.X,
                top = bounds.Y,
                right = bounds.X + bounds.Width,
                bottom = bounds.Y + bounds.Height,
            }),
            DraggingNearTop = watcher.IsDragging && CursorNearTop(rules.DragZone),
            ScreenSharing = screenSharing,
            Idle = idle,
        };
        var reason = AutoHideRules.Evaluate(rules, scene);
        if (reason != _reason && reason != AutoHideReason.None)
        {
            var app = scene.Windows.FirstOrDefault(w => w.Foreground || w.InZone);
            Log.Info($"Ausblende-Grund: {AutoHideRules.Describe(reason)}{(app is null ? "" : $" ({app.Process}, {app.Class})")}");
        }
        _reason = reason;
        ApplyAutoHide();
    }

    private void ApplyAutoHide()
    {
        var rules = _settings.Current.AutoHide;
        var reason = _reason != AutoHideReason.None;
        var dragHide = _reason == AutoHideReason.Dragging;
        if (!reason)
        {
            _peeking = false;
            _keyboardPeek = false;
        }

        // Nie unter der Maus wegziehen, solange jemand mit der Notch arbeitet.
        var busy = _expanded || _inside || _dropTarget || _draggingOut || _popupOpen;
        var hide = reason && !_peeking && (!busy || dragHide);

        // Zurückziehen auf Wunsch verzögert (Plan 4.9), Zurückkommen immer sofort. Beim Ziehen eines
        // Fensters zählt jeder Moment, damit die Snap-Leiste frei ist.
        var delay = Math.Clamp(rules.HideDelayMs, 0, 2000);
        var wantHide = hide;
        if (wantHide && !_hidden && delay > 0 && !dragHide && !_hideDelayDone)
        {
            if (_hideDelay is null)
            {
                _hideDelay = DispatcherQueue.CreateTimer();
                _hideDelay.IsRepeating = false;
                _hideDelay.Tick += (_, _) =>
                {
                    _hideDelayDone = true;
                    ApplyAutoHide();
                };
            }
            if (!_hideDelay.IsRunning)
            {
                _hideDelay.Interval = TimeSpan.FromMilliseconds(delay);
                _hideDelay.Start();
            }
            hide = false;
        }
        else
        {
            _hideDelay?.Stop();
        }
        if (!wantHide || _hidden)
        {
            _hideDelayDone = false;
        }

        if (hide != _hidden)
        {
            Log.Info(hide
                ? $"Notch zieht sich zurück ({AutoHideRules.Describe(_reason)})"
                : $"Notch kommt zurück ({(_keyboardPeek ? "Tastenkürzel" : _peeking ? "Oberkante" : "kein Grund mehr")})");
            _hidden = hide;
            if (hide)
            {
                _hoverTimer.Stop();
                _expanded = false;
                _hub.NotifyExpanded(false);
            }
            Refresh();
        }

        // Oberkante nur beobachten, solange die Notch wegen eines Fensters versteckt ist.
        var watchEdge = reason && rules.PeekFromTopEdge && !_windowDragging;
        if (watchEdge && !_peekPoll.IsRunning)
        {
            _peekPoll.Start();
        }
        else if (!watchEdge && _peekPoll.IsRunning)
        {
            _peekPoll.Stop();
            _peekSince = _peekLeftSince = null;
        }
    }

    /// <summary>Etwa 30-mal pro Sekunde, nur solange die Notch versteckt oder zurückgeholt ist.</summary>
    private void PollPeek()
    {
        var now = Environment.TickCount64;
        if (_peeking)
        {
            if (CursorInside() || CursorAtTopEdge())
            {
                _peekLeftSince = null;
            }
            else if (_peekLeftSince is null)
            {
                _peekLeftSince = now;
            }
            else if (now - _peekLeftSince >= PeekLeaveMs && !_expanded)
            {
                _peeking = false;
                _keyboardPeek = false;
                _peekLeftSince = null;
                ApplyAutoHide();
            }
            return;
        }

        if (!CursorAtTopEdge())
        {
            _peekSince = null;
            return;
        }
        _peekSince ??= now;
        if (now - _peekSince >= _settings.Current.AutoHide.PeekDelayMs)
        {
            _peeking = true;
            _peekSince = null;
            ApplyAutoHide();
        }
    }

    /// <summary>
    /// Win+Alt+N bei versteckter Notch: zurückholen und aufklappen. Nach dem Zuklappen zieht sie sich
    /// wieder zurück, sobald die Maus nicht über ihr ist.
    /// </summary>
    private void PeekFromKeyboard()
    {
        _peeking = true;
        _keyboardPeek = true;
        ApplyAutoHide();
    }

    /// <summary>Nach dem Zuklappen: ein Zurückholen per Tastenkürzel endet, wenn die Maus nicht auf der Notch ist.</summary>
    private void EndKeyboardPeek()
    {
        if (_keyboardPeek && !CursorInside() && !CursorAtTopEdge())
        {
            _keyboardPeek = false;
            _peeking = false;
        }
    }

    /// <summary>Bereich der kompakten Notch (mit Platz fürs Anschwellen) in physischen Bildschirmpixeln.</summary>
    private RECT NotchZone()
    {
        var shape = NotchGeometry.Swell(NotchGeometry.Compact);
        var center = AppWindow.Position.X + AppWindow.Size.Width / 2;
        var half = (int)Math.Ceiling((shape.OuterWidth / 2 + 8) * _scale);
        return new RECT
        {
            left = center - half,
            right = center + half,
            top = _top,
            bottom = _top + (int)Math.Ceiling((shape.Height + 8) * _scale),
        };
    }

    /// <summary>Maus ganz oben am Bildschirmrand, ungefähr über der Notch.</summary>
    private bool CursorAtTopEdge()
    {
        if (!Win32.TryGetCursor(out var x, out var y))
        {
            return false;
        }
        var center = AppWindow.Position.X + AppWindow.Size.Width / 2;
        var half = NotchGeometry.Expanded.OuterWidth / 2 * _scale;
        return y <= _top + 1 && Math.Abs(x - center) <= half;
    }

    /// <summary>Zeiger auf diesem Monitor und nah an der Oberkante (beim Ziehen eines Fensters).</summary>
    private bool CursorNearTop(int zoneDip)
    {
        if (!Win32.TryGetCursor(out var x, out var y))
        {
            return false;
        }
        var bounds = Display.OuterBounds;
        return x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y - _top < zoneDip * _scale;
    }
}
