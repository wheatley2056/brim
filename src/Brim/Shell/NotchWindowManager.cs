using Brim.Core;
using Brim.Modules;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinUIEx.Messaging;

namespace Brim.Shell;

/// <summary>Hält ein Notch-Fenster pro gewähltem Monitor und reagiert auf Monitor- und Einstellungswechsel.</summary>
internal sealed class NotchWindowManager : IDisposable
{
    private const int HotkeyId = 0xB1;
    private const int FpsHotkeyId = 0xB2;
    private const uint VkN = 0x4E;
    private const uint VkF = 0x46;

    private readonly ActivityHub _hub;
    private readonly SettingsStore _settings;
    private readonly ShelfStore _shelf;
    private readonly NearbyModule _nearby;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<NotchWindow> _windows = [];
    private readonly DispatcherQueueTimer _rebuildTimer;
    private DisplayAreaWatcher? _watcher;
    private readonly WindowWatcher _windowWatcher;
    private WindowMessageMonitor? _messages;
    private nint _hotkeyWindow;
    private int _refreshQueued;

    public NotchWindowManager(ActivityHub hub, SettingsStore settings, ShelfStore shelf, NearbyModule nearby, DispatcherQueue dispatcher)
    {
        _hub = hub;
        _shelf = shelf;
        _nearby = nearby;
        _settings = settings;
        _dispatcher = dispatcher;
        _rebuildTimer = dispatcher.CreateTimer();
        _rebuildTimer.IsRepeating = false;
        _rebuildTimer.Interval = TimeSpan.FromMilliseconds(500);
        _rebuildTimer.Tick += (_, _) => Build();

        _hub.Changed += QueueRefresh;
        _settings.Changed += _ => dispatcher.TryEnqueue(Build);

        _watcher = DisplayArea.CreateWatcher();
        _watcher.Added += (_, _) => QueueRebuild();
        _watcher.Removed += (_, _) => QueueRebuild();
        _watcher.Updated += (_, _) => QueueRebuild();
        _watcher.Start();

        // Andere Fenster beobachten: Überlappen und Ziehen Richtung Oberkante blenden die Notch aus.
        _windowWatcher = new WindowWatcher(dispatcher);
        _windowWatcher.Changed += EvaluateAutoHide;
        _windowWatcher.DraggingChanged += _ => EvaluateAutoHide();
        // Beim Ziehen rund 30-mal pro Sekunde: Freigabe nicht jedes Mal neu suchen.
        _windowWatcher.DragMoved += () => EvaluateAutoHide(checkSharing: false);

        // Leerlauf-Regel: nur wenn eingeschaltet, und gemeldet wird nur das Überschreiten der Schwelle.
        _idleTimer = dispatcher.CreateTimer();
        _idleTimer.IsRepeating = true;
        _idleTimer.Interval = TimeSpan.FromMilliseconds(500);
        _idleTimer.Tick += (_, _) =>
        {
            var idle = IsIdle();
            if (idle != _wasIdle)
            {
                EvaluateAutoHide(checkSharing: false);
            }
        };
    }

    private readonly DispatcherQueueTimer _idleTimer;
    private bool _sharing;
    private bool _wasIdle;

    // „Monitor mit Maus“: eine Notch, die dem Zeiger folgt (Plan 5, Monitore).
    private DispatcherQueueTimer? _mouseTimer;

    // Gesperrt oder Bildschirm aus: Statuswerte pausieren (Plan 4.1).
    private WindowMessageMonitor? _session;
    private nint _sessionWindow;
    private nint _powerNotification;
    private bool _locked;
    private bool _displayOff;

    private bool IsIdle() =>
        _settings.Current.AutoHide.IdleSeconds is > 0 and var seconds && Win32.IdleTime() >= TimeSpan.FromSeconds(seconds);

    private void EvaluateAutoHide() => EvaluateAutoHide(checkSharing: true);

    private void EvaluateAutoHide(bool checkSharing)
    {
        var rules = _settings.Current.AutoHide;
        if (checkSharing)
        {
            var sharing = rules.WhenScreenSharing && _windowWatcher.IsScreenSharing(rules.ScreenShareMarkers);
            if (sharing != _sharing)
            {
                Log.Info(sharing ? "Bildschirmfreigabe erkannt" : "Bildschirmfreigabe beendet");
            }
            _sharing = sharing;
        }
        _wasIdle = IsIdle();
        var idle = rules.IdleSeconds > 0 ? Win32.IdleTime() : TimeSpan.Zero;
        foreach (var window in _windows)
        {
            window.EvaluateAutoHide(_windowWatcher, _sharing, idle);
        }

        if (rules.IdleSeconds > 0 && !_idleTimer.IsRunning)
        {
            _idleTimer.Start();
        }
        else if (rules.IdleSeconds <= 0 && _idleTimer.IsRunning)
        {
            _idleTimer.Stop();
        }
    }

    /// <summary>Fenster passend zu den Monitoren und Einstellungen anlegen, verschieben oder schließen.</summary>
    public void Build()
    {
        var displays = SelectDisplays();
        var keep = new List<NotchWindow>();
        var follow = _settings.Current.Monitors == MonitorMode.Mouse;
        foreach (var display in displays)
        {
            // Folgt die Notch der Maus, wandert dasselbe Fenster mit, statt neu zu entstehen.
            var existing = _windows.FirstOrDefault(w => w.Display.DisplayId.Value == display.DisplayId.Value)
                ?? (follow && _windows.Count == 1 ? _windows[0] : null);
            if (existing is not null)
            {
                existing.UpdateDisplay(display);
                existing.Refresh();
                keep.Add(existing);
            }
            else
            {
                keep.Add(new NotchWindow(display, _hub, _settings, _shelf, _nearby));
            }
        }
        foreach (var gone in _windows.Except(keep).ToList())
        {
            gone.Close();
        }
        _windows.Clear();
        _windows.AddRange(keep);
        Log.Info($"Notch auf {_windows.Count} Monitor(en)");
        RegisterHotkey();
        WatchSession();
        UpdateMouseFollow(follow);
        EvaluateAutoHide();
    }

    private void UpdateMouseFollow(bool follow)
    {
        if (!follow)
        {
            _mouseTimer?.Stop();
            return;
        }
        if (_mouseTimer is null)
        {
            _mouseTimer = _dispatcher.CreateTimer();
            _mouseTimer.IsRepeating = true;
            _mouseTimer.Interval = TimeSpan.FromMilliseconds(300);
            _mouseTimer.Tick += (_, _) => FollowMouse();
        }
        _mouseTimer.Start();
    }

    /// <summary>Wechselt die Maus den Monitor, zieht die Notch nach – nur, wenn sie gerade nichts zu tun hat.</summary>
    private void FollowMouse()
    {
        if (_windows.Count != 1 || _windows[0].IsBusy)
        {
            return;
        }
        var display = MouseDisplay();
        if (display.DisplayId.Value != _windows[0].Display.DisplayId.Value)
        {
            Build();
        }
    }

    private static DisplayArea MouseDisplay() =>
        Win32.TryGetCursor(out var x, out var y)
            ? DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Primary)
            : DisplayArea.Primary;

    private void WatchSession()
    {
        var primary = _windows.FirstOrDefault();
        if (primary is null || primary.Handle == _sessionWindow)
        {
            return;
        }
        UnwatchSession();
        _sessionWindow = primary.Handle;
        _powerNotification = Win32.WatchSession(primary.Handle);
        _session = new WindowMessageMonitor(primary);
        _session.WindowMessageReceived += (_, e) =>
        {
            if (e.Message.MessageId == Win32.WmWtsSessionChange)
            {
                switch ((int)e.Message.WParam)
                {
                    case Win32.WtsSessionLock:
                        _locked = true;
                        break;
                    case Win32.WtsSessionUnlock:
                        _locked = false;
                        break;
                    default:
                        return;
                }
            }
            else if (e.Message.MessageId == Win32.WmPowerBroadcast && (int)e.Message.WParam == Win32.PbtPowerSettingChange
                && Win32.DisplayState(e.Message.LParam) is { } state)
            {
                _displayOff = state == 0;
            }
            else
            {
                return;
            }
            _hub.SetScreenAway(_locked || _displayOff);
        };
    }

    private void UnwatchSession()
    {
        if (_sessionWindow != 0)
        {
            Win32.UnwatchSession(_sessionWindow, _powerNotification);
            _sessionWindow = 0;
            _powerNotification = 0;
        }
        _session?.Dispose();
        _session = null;
    }

    public bool IsBusy => _windows.Any(w => w.IsBusy);

    /// <summary>Einführung auf dem Hauptmonitor (erster Start oder „Einführung erneut zeigen“).</summary>
    public void StartOnboarding()
    {
        if (_windows.FirstOrDefault() is not { } window)
        {
            return;
        }
        window.OnboardingFinished -= OnOnboardingFinished;
        window.OnboardingFinished += OnOnboardingFinished;
        window.StartOnboarding();
    }

    private void OnOnboardingFinished(bool restart) => OnboardingFinished?.Invoke(restart);

    /// <summary>Einführung beendet; <c>true</c>, wenn Brim neu starten soll.</summary>
    public event Action<bool>? OnboardingFinished;

    public void ToggleExpanded()
    {
        var window = _windows.FirstOrDefault(w => w.IsExpanded) ?? _windows.FirstOrDefault();
        window?.ToggleFromKeyboard();
    }

    private List<DisplayArea> SelectDisplays()
    {
        switch (_settings.Current.Monitors)
        {
            case MonitorMode.Primary:
                return [DisplayArea.Primary];
            case MonitorMode.Mouse:
                return [MouseDisplay()];
        }
        // FindAll liefert eine Projektion, die man per Index lesen muss.
        var all = DisplayArea.FindAll();
        var result = new List<DisplayArea>(all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            result.Add(all[i]);
        }
        return result;
    }

    private void RegisterHotkey()
    {
        var primary = _windows.FirstOrDefault();
        if (primary is null || primary.Handle == _hotkeyWindow || !_settings.Current.Interaction.HotkeyEnabled)
        {
            return;
        }
        UnregisterHotkey();
        if (!Win32.RegisterHotkey(primary.Handle, HotkeyId, VkN))
        {
            Log.Warn("Tastenkürzel Win+Alt+N ist bereits vergeben");
            return;
        }
        _hotkeyWindow = primary.Handle;
        // Win+Alt+F: im Spielmodus „Bei Bedarf“ FPS für 5 s einblenden (Plan 4.8).
        if (!Win32.RegisterHotkey(primary.Handle, FpsHotkeyId, VkF))
        {
            Log.Warn("Tastenkürzel Win+Alt+F ist bereits vergeben");
        }
        _messages = new WindowMessageMonitor(primary);
        _messages.WindowMessageReceived += (_, e) =>
        {
            if (e.Message.MessageId != Win32.WmHotkey)
            {
                return;
            }
            switch ((int)e.Message.WParam)
            {
                case HotkeyId:
                    ToggleExpanded();
                    e.Handled = true;
                    break;
                case FpsHotkeyId:
                    _hub.Invoke(new ActionInvocation(GamesModule.ModuleId, GamesModule.PeekAction));
                    e.Handled = true;
                    break;
            }
        };
    }

    private void UnregisterHotkey()
    {
        if (_hotkeyWindow != 0)
        {
            Win32.UnregisterHotkey(_hotkeyWindow, HotkeyId);
            Win32.UnregisterHotkey(_hotkeyWindow, FpsHotkeyId);
            _hotkeyWindow = 0;
        }
        _messages?.Dispose();
        _messages = null;
    }

    private void QueueRefresh()
    {
        // Module melden von beliebigen Threads; mehrere Meldungen pro Frame werden zusammengefasst.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            foreach (var window in _windows)
            {
                window.Refresh();
            }
        });
    }

    private void QueueRebuild() => _dispatcher.TryEnqueue(() =>
    {
        _rebuildTimer.Stop();
        _rebuildTimer.Start();
    });

    public void Dispose()
    {
        _hub.Changed -= QueueRefresh;
        _watcher?.Stop();
        _idleTimer.Stop();
        _mouseTimer?.Stop();
        UnwatchSession();
        _windowWatcher.Dispose();
        _watcher = null;
        UnregisterHotkey();
        foreach (var window in _windows)
        {
            window.Close();
        }
        _windows.Clear();
    }
}
