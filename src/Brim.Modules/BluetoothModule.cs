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

    /// <summary>Name oder Akkustand eines verbundenen Geräts hat sich geändert.</summary>
    event Action<BluetoothGadget>? Updated;

    event Action<string>? Disconnected;

    void Start();
}

/// <summary>
/// Bluetooth (A3, Plan 4.3): Karte im Tab „System“ mit allen verbundenen Geräten und HUD, wenn sich eines neu
/// verbindet (Kopfhörer, Maus, Controller), jeweils mit Akkustand, wenn Windows ihn kennt. Den Akkustand liest Brim aus derselben Geräteeigenschaft wie die
/// Windows-Einstellungen; eine eigene GATT-Verbindung baut es nicht auf.
/// </summary>
public sealed class BluetoothModule : IBrimModule
{
    public const string ModuleId = "bluetooth";

    private readonly Func<IBluetoothSource> _createSource;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, BluetoothGadget> _connected = new();
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
        _source.Updated += OnUpdated;
        _source.Disconnected += OnDisconnected;
        _source.Start();
        return Task.CompletedTask;
    }

    /// <summary>Verbundene Geräte, nach Name sortiert.</summary>
    public IReadOnlyList<BluetoothGadget> Connected
    {
        get
        {
            lock (_gate)
            {
                return _connected.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        }
    }

    private void OnUpdated(BluetoothGadget gadget)
    {
        lock (_gate)
        {
            if (!_connected.ContainsKey(gadget.Id))
            {
                return;
            }
            _connected[gadget.Id] = gadget;
        }
        PublishCard();
    }

    private void OnDisconnected(string id)
    {
        lock (_gate)
        {
            _connected.Remove(id);
        }
        PublishCard();
    }

    private void PublishCard()
    {
        if (_context is not { } context)
        {
            return;
        }
        var gadgets = Connected;
        if (gadgets.Count == 0)
        {
            context.RemoveView("card");
            return;
        }
        var rows = new List<Widget>
        {
            new TextWidget("Bluetooth") { Style = TextStyle.Secondary, FontSize = 12 },
        };
        rows.AddRange(gadgets.Select(g => (Widget)new StackWidget(
            new IconWidget(LooksLikeHeadphones(g.Name) ? Glyphs.Headphones : Glyphs.Bluetooth) { Size = 13, Color = BrimColors.Slate },
            new TextWidget(g.Name) { Grow = 1 },
            g.BatteryPercent is { } percent
                ? new TextWidget($"{percent} %") { Style = TextStyle.MonoCaption, FontSize = 12, Color = percent <= 20 ? BrimColors.Warning : BrimColors.Slate }
                : new SpacerWidget())
        {
            Spacing = 8,
            AccessibleName = g.BatteryPercent is { } b ? $"{g.Name}, Akku {b} %" : g.Name,
        }));
        context.SetView("card", new StackWidget(rows)
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 4,
            Align = CrossAlign.Stretch,
            Background = BrimColors.Graphite,
            CornerRadius = 14,
            Padding = (12, 8, 12, 8),
        }, column: TabColumn.Side, order: 5);
    }

    private void OnConnected(BluetoothGadget gadget, bool initial)
    {
        lock (_gate)
        {
            _connected[gadget.Id] = gadget;
        }
        Log.Info($"Bluetooth verbunden: {gadget.Name}{(gadget.BatteryPercent is { } p ? $", Akku {p} %" : ", Akku unbekannt")}");
        PublishCard();

        // Was schon beim Start verbunden war, ist nichts Neues: dafür kein HUD.
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
            source.Updated -= OnUpdated;
            source.Disconnected -= OnDisconnected;
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

        private readonly Dictionary<string, BluetoothGadget> _known = new();

        public event Action<BluetoothGadget, bool>? Connected;
        public event Action<BluetoothGadget>? Updated;
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
                    var gadget = new BluetoothGadget(info.Id, info.Name, Battery(info.Properties));
                    lock (_known)
                    {
                        _known[info.Id] = gadget;
                    }
                    Connected?.Invoke(gadget, initial);
                };
                // Der Akkustand kommt oft erst nach dem Verbinden; Windows meldet ihn dann als Änderung.
                watcher.Updated += (_, update) =>
                {
                    BluetoothGadget? gadget;
                    lock (_known)
                    {
                        if (!_known.TryGetValue(update.Id, out var known))
                        {
                            return;
                        }
                        gadget = Battery(update.Properties) is { } battery ? known with { BatteryPercent = battery } : null;
                        if (gadget is not null)
                        {
                            _known[update.Id] = gadget;
                        }
                    }
                    if (gadget is not null)
                    {
                        Updated?.Invoke(gadget);
                    }
                };
                watcher.Removed += (_, update) =>
                {
                    lock (_known)
                    {
                        _known.Remove(update.Id);
                    }
                    Disconnected?.Invoke(update.Id);
                };
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
