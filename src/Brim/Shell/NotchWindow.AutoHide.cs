using Brim.Core;
using Microsoft.UI.Dispatching;
using Windows.Win32.Foundation;

namespace Brim.Shell;

/// <summary>
/// Automatisches Ausblenden (Plan 4.9): Die Notch zieht sich zurück, wenn ein App-Fenster in ihrem
/// Bereich liegt (maximiert, Vollbild, einfach hochgeschoben) oder wenn ein Fenster Richtung Oberkante
/// gezogen wird und Windows die Snap-Leiste zeigt. Die Maus an der Oberkante holt sie zurück.
/// </summary>
internal sealed partial class NotchWindow
{
    private readonly DispatcherQueueTimer _peekPoll;
    private int _top;
    private bool _overlapped;
    private string? _overlapClass;
    private bool _dragHide;
    private bool _windowDragging;
    private bool _peeking;
    private long? _peekSince;
    private long? _peekLeftSince;

    /// <summary>Wie lange die Maus die zurückgeholte Notch verlassen darf, bevor sie sich wieder versteckt.</summary>
    private const int PeekLeaveMs = 400;

    /// <summary>Von <see cref="NotchWindowManager"/> bei jeder Änderung der Fensterlage aufgerufen.</summary>
    public void EvaluateAutoHide(WindowWatcher watcher)
    {
        var rules = _settings.Current.AutoHide;
        _windowDragging = watcher.IsDragging;
        _overlapped = rules.WhenOverlapped && watcher.AnyWindowIn(NotchZone(), out _overlapClass);
        _dragHide = rules.WhileDraggingWindows && watcher.IsDragging && CursorNearTop(rules.DragZone);
        ApplyAutoHide();
    }

    private void ApplyAutoHide()
    {
        var rules = _settings.Current.AutoHide;
        var reason = _overlapped || _dragHide;
        if (!reason)
        {
            _peeking = false;
        }

        // Nie unter der Maus wegziehen, solange jemand mit der Notch arbeitet.
        var busy = _expanded || _inside || _dropTarget || _draggingOut || _popupOpen;
        var hide = reason && !_peeking && (!busy || _dragHide);
        if (hide != _hidden)
        {
            Log.Info(hide
                ? $"Notch zieht sich zurück ({(_dragHide ? "Fenster wird gezogen" : $"Fenster im Bereich: {_overlapClass}")})"
                : $"Notch kommt zurück ({(_peeking ? "Oberkante" : "Bereich frei")})");
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
