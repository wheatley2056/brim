using Brim.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using WinUIEx.Messaging;

namespace Brim.Shell;

/// <summary>Hält ein Notch-Fenster pro gewähltem Monitor und reagiert auf Monitor- und Einstellungswechsel.</summary>
internal sealed class NotchWindowManager : IDisposable
{
    private const int HotkeyId = 0xB1;
    private const uint VkN = 0x4E;

    private readonly ActivityHub _hub;
    private readonly SettingsStore _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<NotchWindow> _windows = [];
    private readonly DispatcherQueueTimer _rebuildTimer;
    private DisplayAreaWatcher? _watcher;
    private WindowMessageMonitor? _messages;
    private nint _hotkeyWindow;
    private int _refreshQueued;

    public NotchWindowManager(ActivityHub hub, SettingsStore settings, DispatcherQueue dispatcher)
    {
        _hub = hub;
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
    }

    /// <summary>Fenster passend zu den Monitoren und Einstellungen anlegen, verschieben oder schließen.</summary>
    public void Build()
    {
        var displays = SelectDisplays();
        var keep = new List<NotchWindow>();
        foreach (var display in displays)
        {
            var existing = _windows.FirstOrDefault(w => w.Display.DisplayId.Value == display.DisplayId.Value);
            if (existing is not null)
            {
                existing.UpdateDisplay(display);
                existing.Refresh();
                keep.Add(existing);
            }
            else
            {
                keep.Add(new NotchWindow(display, _hub, _settings));
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
    }

    public void ToggleExpanded()
    {
        var window = _windows.FirstOrDefault(w => w.IsExpanded) ?? _windows.FirstOrDefault();
        window?.ToggleFromKeyboard();
    }

    private List<DisplayArea> SelectDisplays()
    {
        if (_settings.Current.Monitors == MonitorMode.Primary)
        {
            return [DisplayArea.Primary];
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
        _messages = new WindowMessageMonitor(primary);
        _messages.WindowMessageReceived += (_, e) =>
        {
            if (e.Message.MessageId == Win32.WmHotkey && (int)e.Message.WParam == HotkeyId)
            {
                ToggleExpanded();
                e.Handled = true;
            }
        };
    }

    private void UnregisterHotkey()
    {
        if (_hotkeyWindow != 0)
        {
            Win32.UnregisterHotkey(_hotkeyWindow, HotkeyId);
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
        _watcher = null;
        UnregisterHotkey();
        foreach (var window in _windows)
        {
            window.Close();
        }
        _windows.Clear();
    }
}
