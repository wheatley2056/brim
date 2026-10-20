using Brim.Core;
using Brim.Core.Widgets;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Brim.Modules;

/// <summary>Ein Bluetooth-Gerät, wie es das Modul sieht.</summary>
public sealed record BluetoothGadget(string Id, string Name, int? BatteryPercent);

public interface IBluetoothSource : IDisposable
{
    /// <summary>Gerät verbunden; <c>initial</c> für Geräte, die schon beim Start verbunden waren.</summary>
    event Action<BluetoothGadget, bool>? Connected;

    event Action<string>? Disconnected;

    void Start();
}

/// <summary>
/// Bluetooth (A3, Plan 4.3): HUD, wenn sich ein Gerät verbindet (Kopfhörer, Maus, Controller), mit Akkustand,
/// wenn Windows ihn kennt. Den Akkustand liest Brim aus derselben Geräteeigenschaft wie die
/// Windows-Einstellungen; eine eigene GATT-Verbindung baut es nicht auf.
/// </summary>
public sealed class BluetoothModule : IBrimModule
{
    public const string ModuleId = "bluetooth";

    private readonly Func<IBluetoothSource> _createSource;
    private IBluetoothSource? _source;
    private ModuleContext? _context;

    public BluetoothModule() : this(() => new DeviceWatcherSource())
    {
    }

    public BluetoothModule(Func<IBluetoothSource> createSource)
    {
        _createSource = createSource;
    }

    public string Id => ModuleId;
    public string DisplayName => "Bluetooth";

    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _source = _createSource();
        _source.Connected += OnConnected;
        _source.Start();
        return Task.CompletedTask;
    }

    private void OnConnected(BluetoothGadget gadget, bool initial)
    {
        // Was schon beim Start verbunden war, ist nichts Neues.
        if (initial || _context is not { } context)
        {
            return;
        }
        var battery = gadget.BatteryPercent;
        var color = battery is <= 20 ? BrimColors.Warning : BrimColors.Fog;
        context.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "hud",
            Priority = ActivityPriority.Transient,
            Duration = TimeSpan.FromSeconds(2.5),
            Announcement = battery is { } b ? $"{gadget.Name} verbunden, Akku {b} %" : $"{gadget.Name} verbunden",
            Content = new StackWidget(
                new IconWidget(LooksLikeHeadphones(gadget.Name) ? Glyphs.Headphones : Glyphs.Bluetooth) { Size = 16 },
                new TextWidget(gadget.Name) { Grow = 1 },
                battery is { } percent
                    ? new StackWidget(
                        new IconWidget(Glyphs.Battery(percent, charging: false, saver: false)) { Size = 14, Color = color },
                        new TextWidget($"{percent} %") { Style = TextStyle.Mono, Color = color })
                    {
                        Spacing = 6,
                    }
                    : new TextWidget("verbunden") { Style = TextStyle.Secondary })
            {
                Spacing = 10,
                Padding = (18, 0, 18, 0),
            },
        });
    }

    public static bool LooksLikeHeadphones(string name) =>
        new[] { "kopfh", "headphone", "headset", "airpods", "buds", "ohrh", "wh-", "wf-", "jabra", "bose" }
            .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    public ValueTask DisposeAsync()
    {
        if (_source is { } source)
        {
            source.Connected -= OnConnected;
            source.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Zwei <see cref="DeviceWatcher"/> (klassisch und Low Energy) auf verbundene Geräte. Der Akkustand steht in
    /// <c>{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2</c>, sofern das Gerät ihn über HFP oder den Battery Service meldet.
    /// </summary>
    private sealed class DeviceWatcherSource : IBluetoothSource
    {
        private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

        private readonly List<DeviceWatcher> _watchers = [];
        private readonly HashSet<string> _initial = [];
        private int _completed;

        public event Action<BluetoothGadget, bool>? Connected;
        public event Action<string>? Disconnected;

        public void Start()
        {
            foreach (var selector in new[]
            {
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
            })
            {
                var watcher = DeviceInformation.CreateWatcher(selector, [BatteryKey]);
                watcher.Added += (_, info) =>
                {
                    var initial = Volatile.Read(ref _completed) < _watchers.Count;
                    Connected?.Invoke(new BluetoothGadget(info.Id, info.Name, Battery(info.Properties)), initial);
                };
                watcher.Removed += (_, update) => Disconnected?.Invoke(update.Id);
                watcher.EnumerationCompleted += (_, _) => Interlocked.Increment(ref _completed);
                _watchers.Add(watcher);
            }
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.Start();
                }
                catch (Exception ex)
                {
                    Log.Info($"Bluetooth nicht verfügbar: {ex.Message}");
                    Interlocked.Increment(ref _completed);
                }
            }
        }

        private static int? Battery(IReadOnlyDictionary<string, object> properties) =>
            properties.TryGetValue(BatteryKey, out var value) && value is byte or int or uint
                ? Math.Clamp(Convert.ToInt32(value), 0, 100)
                : null;

        public void Dispose()
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                    {
                        watcher.Stop();
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"Bluetooth-Beobachtung ließ sich nicht beenden: {ex.Message}");
                }
            }
            _watchers.Clear();
        }
    }
}
