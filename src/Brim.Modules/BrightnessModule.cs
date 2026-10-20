using System.Management;
using Brim.Core;
using Brim.Core.Widgets;

namespace Brim.Modules;

/// <summary>
/// Helligkeits-HUD (A3) für eingebaute Bildschirme: WMI meldet jede Änderung über
/// <c>WmiMonitorBrightnessEvent</c>, egal ob per Taste, Schnelleinstellungen oder App.
/// Externe Monitore (DDC/CI) melden nichts und sind hier noch nicht dabei.
/// </summary>
public sealed class BrightnessModule : IBrimModule
{
    public const string ModuleId = "brightness";

    private ModuleContext? _context;
    private ManagementEventWatcher? _watcher;

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
                Show(Convert.ToInt32(value));
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
