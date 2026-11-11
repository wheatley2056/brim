using System.Management;
using Brim.Core;
using Brim.Core.Widgets;

namespace Brim.Modules;

/// <summary>
/// Helligkeits-HUD (A3) für eingebaute Bildschirme: WMI meldet jede Änderung über
/// <c>WmiMonitorBrightnessEvent</c>, egal ob per Taste, Schnelleinstellungen oder App.
/// Im Tab „System“ steht ein Regler wie im Windows-Flyout (<c>WmiSetBrightness</c>).
/// Externe Monitore (DDC/CI) melden nichts und sind hier noch nicht dabei.
/// </summary>
public sealed class BrightnessModule : IBrimModule
{
    public const string ModuleId = "brightness";

    private const string SliderAction = "set";

    private ModuleContext? _context;
    private ManagementEventWatcher? _watcher;
    private int _percent = -1;

    /// <summary>Gewünschter Wert beim Ziehen; nur der neueste wird geschrieben, WMI ist langsam.</summary>
    private int _wanted = -1;
    private int _writing;
    private int _written = -1;

    public string Id => ModuleId;
    public string DisplayName => "Helligkeit";

    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        // WMI anzumelden dauert spürbar; nicht auf dem UI-Thread, damit die Notch sofort da ist.
        _ = Task.Run(Watch, cancellationToken);
        return Task.CompletedTask;
    }

    private void Watch()
    {
        try
        {
            _watcher = new ManagementEventWatcher(
                new ManagementScope(@"root\WMI"),
                new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += OnEvent;
            _watcher.Start();
            if (ReadCurrent() is { } current)
            {
                _percent = current;
                PublishSlider();
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Desktop-PC ohne eingebauten Bildschirm: Die Klasse gibt es dann nicht.
            Log.Info($"Helligkeit nicht verfügbar: {ex.Message}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            if (e.NewEvent["Brightness"] is { } value)
            {
                var percent = Math.Clamp(Convert.ToInt32(value), 0, 100);
                var changed = percent != _percent;
                _percent = percent;
                PublishSlider();
                // Kommt die Änderung vom eigenen Regler, braucht es kein HUD.
                if (changed && Volatile.Read(ref _writing) == 0)
                {
                    Show(percent);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Helligkeitsereignis unlesbar", ex);
        }
    }

    /// <summary>Zeigt das HUD für eine Helligkeit in Prozent.</summary>
    public void Show(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _context?.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "hud",
            Priority = ActivityPriority.Transient,
            Announcement = $"Helligkeit {percent}",
            Content = new StackWidget(
                new IconWidget(Glyphs.Brightness) { Size = 16 },
                new ProgressWidget(percent / 100.0) { Grow = 1, AccessibleName = "Helligkeit" },
                new TextWidget(percent.ToString()) { Style = TextStyle.MonoCaption, FontSize = 12 })
            {
                Spacing = 12,
                Padding = (18, 0, 18, 0),
            },
        });
    }

    private static int? ReadCurrent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery("WmiMonitorBrightness"));
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    return Convert.ToInt32(item["CurrentBrightness"]);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Info($"Helligkeit nicht lesbar: {ex.Message}");
        }
        return null;
    }

    /// <summary>Regler im Tab „System“: Symbol, Spur in der Akzentfarbe, Wert rechts (Design „Windows-11-nativ“).</summary>
    public static Widget Slider(int percent) => new StackWidget(
        new IconWidget(Glyphs.Brightness) { Size = 18, AccessibleName = "Helligkeit" },
        new ProgressWidget(percent / 100.0)
        {
            SeekActionId = SliderAction,
            Thumb = true,
            Grow = 1,
            AccessibleName = $"Helligkeit {percent}",
        },
        new TextWidget(percent.ToString()) { Style = TextStyle.Secondary, FontSize = 12, FixedWidth = 36 })
    {
        Spacing = 14,
    };

    private void PublishSlider()
    {
        if (_context is { } context && _percent >= 0)
        {
            context.SetView("slider", Slider(_percent), order: -5);
        }
    }

    public void OnAction(ActionInvocation invocation)
    {
        if (invocation.ActionId != SliderAction || invocation.Value is not { } value)
        {
            return;
        }
        _percent = Math.Clamp((int)Math.Round(value * 100), 0, 100);
        Volatile.Write(ref _wanted, _percent);
        PublishSlider();
        if (Interlocked.Exchange(ref _writing, 1) == 0)
        {
            _ = Task.Run(WriteLoop);
        }
    }

    private void WriteLoop()
    {
        while (true)
        {
            while (Volatile.Read(ref _wanted) is var wanted && wanted != _written)
            {
                Write(wanted);
                _written = wanted;
            }
            // Das Ereignis zur eigenen Änderung kommt kurz danach; so lange kein HUD.
            Thread.Sleep(300);
            Volatile.Write(ref _writing, 0);
            // Kam während der Pause noch ein Wert, weiterschreiben, sofern kein anderer Lauf übernommen hat.
            if (Volatile.Read(ref _wanted) == _written || Interlocked.Exchange(ref _writing, 1) != 0)
            {
                return;
            }
        }
    }

    private static void Write(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery("WmiMonitorBrightnessMethods"));
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    item.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)percent]);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn("Helligkeit nicht einstellbar", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_watcher is { } watcher)
        {
            watcher.EventArrived -= OnEvent;
            try
            {
                watcher.Stop();
            }
            catch (ManagementException)
            {
            }
            watcher.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
