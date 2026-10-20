using System.Diagnostics;
using Brim.Core;
using Microsoft.UI.Dispatching;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Brim.Shell;

/// <summary>
/// Beobachtet die Fenster anderer Apps über <c>SetWinEventHook</c> (offizielle Schnittstelle,
/// ereignisgesteuert): Vordergrundwechsel, Verschieben, Ein-/Ausblenden, Minimieren.
/// Daraus entscheiden die Notch-Fenster, ob sie sich zurückziehen (Plan 4.9).
/// </summary>
internal sealed class WindowWatcher : IDisposable
{
    // Ereigniskonstanten aus WinUser.h
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectCloaked = 0x8017;
    private const uint EventObjectUncloaked = 0x8018;
    private const int ObjIdWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    /// <summary>Fensterklassen der Shell, die nie als „Fenster über der Notch“ zählen.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _debounce;
    private readonly DispatcherQueueTimer _dragPoll;
    private readonly List<HWINEVENTHOOK> _hooks = [];
    private readonly WINEVENTPROC _callback;
    private readonly uint _ownProcess = (uint)Environment.ProcessId;

    public WindowWatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = OnWinEvent;

        // Viele Ereignisse kurz hintereinander (z. B. beim Verschieben) zu einer Prüfung bündeln.
        _debounce = dispatcher.CreateTimer();
        _debounce.IsRepeating = false;
        _debounce.Interval = TimeSpan.FromMilliseconds(40);
        _debounce.Tick += (_, _) => Changed?.Invoke();

        // Nur während ein Fenster gezogen wird: Zeigerposition für die Snap-Leiste verfolgen.
        _dragPoll = dispatcher.CreateTimer();
        _dragPoll.IsRepeating = true;
        _dragPoll.Interval = TimeSpan.FromMilliseconds(33);
        _dragPoll.Tick += (_, _) => DragMoved?.Invoke();

        Hook(EventSystemForeground, EventSystemForeground);
        Hook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd);
        Hook(EventSystemMinimizeStart, EventSystemMinimizeEnd);
        Hook(EventObjectDestroy, EventObjectHide);
        Hook(EventObjectLocationChange, EventObjectLocationChange);
        Hook(EventObjectCloaked, EventObjectUncloaked);
    }

    /// <summary>Fensterlage hat sich geändert; Notch-Fenster prüfen, ob sie verdeckt werden.</summary>
    public event Action? Changed;

    /// <summary>Ein Fenster wird gerade verschoben oder in der Größe geändert.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>Beginn/Ende des Ziehens.</summary>
    public event Action<bool>? DraggingChanged;

    /// <summary>Während des Ziehens etwa 30-mal pro Sekunde.</summary>
    public event Action? DragMoved;

    private void Hook(uint min, uint max)
    {
        var hook = PInvoke.SetWinEventHook(min, max, HMODULE.Null, _callback, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        if (hook.IsNull)
        {
            Log.Warn($"WinEvent-Hook {min:X}–{max:X} nicht möglich");
            return;
        }
        _hooks.Add(hook);
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Nur ganze Fenster interessieren, nicht Cursor, Textmarken oder Steuerelemente darin.
        if (idObject != ObjIdWindow || idChild != 0)
        {
            return;
        }
        switch (@event)
        {
            case EventSystemMoveSizeStart:
                SetDragging(true);
                return;
            case EventSystemMoveSizeEnd:
                SetDragging(false);
                break;
            case EventObjectLocationChange when !IsTopLevel(hwnd):
                return;
        }
        if (!_debounce.IsRunning)
        {
            _debounce.Start();
        }
    }

    private void SetDragging(bool dragging)
    {
        if (IsDragging == dragging)
        {
            return;
        }
        IsDragging = dragging;
        if (dragging)
        {
            _dragPoll.Start();
        }
        else
        {
            _dragPoll.Stop();
        }
        DraggingChanged?.Invoke(dragging);
    }

    private static bool IsTopLevel(HWND hwnd) => PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) == hwnd;

    /// <summary>
    /// Fensterlage für die Regeln (Plan 4.9) auf einem Monitor: Fenster im Bereich der Notch und das
    /// Vordergrundfenster, jeweils mit Prozess, Vollbild und Maximiert. <paramref name="zone"/> und
    /// <paramref name="monitor"/> in physischen Bildschirmpixeln.
    /// </summary>
    public IReadOnlyList<SceneWindow> Scene(RECT zone, RECT monitor)
    {
        var result = new List<SceneWindow>();
        var own = _ownProcess;
        var foreground = PInvoke.GetForegroundWindow();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (!Counts(hwnd, own, out var b))
            {
                return true;
            }
            var inZone = b.left < zone.right && b.right > zone.left && b.top < zone.bottom && b.bottom > zone.top;
            // Vordergrund zählt nur, wenn das Fenster überwiegend auf diesem Monitor liegt (Mitte darin).
            var centerX = (b.left + b.right) / 2;
            var centerY = (b.top + b.bottom) / 2;
            var onMonitor = centerX >= monitor.left && centerX < monitor.right && centerY >= monitor.top && centerY < monitor.bottom;
            var isForeground = hwnd == foreground && onMonitor;
            if (!inZone && !isForeground)
            {
                return true;
            }
            result.Add(new SceneWindow(ProcessOf(hwnd), ClassOf(hwnd))
            {
                InZone = inZone,
                Fullscreen = b.left <= monitor.left && b.top <= monitor.top && b.right >= monitor.right && b.bottom >= monitor.bottom,
                Maximized = PInvoke.IsZoomed(hwnd),
                Foreground = isForeground,
            });
            return true;
        }, default);
        return result;
    }

    /// <summary>
    /// Läuft eine Bildschirmfreigabe? Durchsucht Klasse und Titel aller sichtbaren Fenster anderer Apps,
    /// auch schwebender Werkzeugleisten, nach den Merkmalen aus den Einstellungen.
    /// </summary>
    public bool IsScreenSharing(IReadOnlyList<string> markers)
    {
        if (markers.Count == 0)
        {
            return false;
        }
        var windows = new List<(string, string)>();
        var own = _ownProcess;
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (PInvoke.IsWindowVisible(hwnd) && !IsCloaked(hwnd) && ProcessId(hwnd) != own)
            {
                windows.Add((ClassOf(hwnd), TitleOf(hwnd)));
            }
            return true;
        }, default);
        return ScreenShare.IsSharing(windows, markers);
    }

    private readonly Dictionary<uint, string> _processNames = new();

    /// <summary>Prozessname ohne Endung; zwischengespeichert, weil die Abfrage teuer ist.</summary>
    private string ProcessOf(HWND hwnd)
    {
        var pid = ProcessId(hwnd);
        if (_processNames.TryGetValue(pid, out var name))
        {
            return name;
        }
        if (_processNames.Count > 256)
        {
            // Prozess-IDs werden wiederverwendet; ab und zu neu anfangen genügt.
            _processNames.Clear();
        }
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            name = "";
        }
        _processNames[pid] = name;
        return name;
    }

    private static unsafe uint ProcessId(HWND hwnd)
    {
        uint pid;
        _ = PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        return pid;
    }

    private static unsafe bool IsCloaked(HWND hwnd)
    {
        int cloaked;
        return PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int)).Succeeded && cloaked != 0;
    }

    private static unsafe string TitleOf(HWND hwnd)
    {
        var length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }
        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            var read = PInvoke.GetWindowText(hwnd, p, buffer.Length);
            return new string(p, 0, read);
        }
    }

    private static unsafe bool Counts(HWND hwnd, uint ownProcess, out RECT bounds)
    {
        bounds = default;
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd))
        {
            return false;
        }
        if (ProcessId(hwnd) == ownProcess)
        {
            return false;
        }
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) || exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_NOACTIVATE))
        {
            return false;
        }
        // Verhüllte Fenster (andere virtuelle Desktops, angehaltene UWP-Apps) sind nicht zu sehen.
        if (IsCloaked(hwnd))
        {
            return false;
        }
        if (ShellClasses.Contains(ClassOf(hwnd)))
        {
            return false;
        }
        // Sichtbarer Rahmen ohne die unsichtbaren Ränder zum Größeändern.
        RECT frame;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT)).Failed)
        {
            return false;
        }
        bounds = frame;
        return frame.right - frame.left > 1 && frame.bottom - frame.top > 1;
    }

    private static unsafe string ClassOf(HWND hwnd)
    {
        var buffer = stackalloc char[256];
        var length = PInvoke.GetClassName(hwnd, buffer, 256);
        return new string(buffer, 0, length);
    }

    public void Dispose()
    {
        _debounce.Stop();
        _dragPoll.Stop();
        foreach (var hook in _hooks)
        {
            PInvoke.UnhookWinEvent(hook);
        }
        _hooks.Clear();
    }
}
