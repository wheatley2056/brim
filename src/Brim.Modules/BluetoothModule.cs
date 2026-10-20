using System.Text.RegularExpressions;
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
public sealed partial class BluetoothModule : IBrimModule
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

    [GeneratedRegex("(?:[0-9a-f]{2}:){5}[0-9a-f]{2}", RegexOptions.IgnoreCase)]
    private static partial Regex MacPattern();

    /// <summary>
    /// MAC-Adresse des Geräts aus der Kennung des Bluetooth-Endpunkts, z. B.
    /// <c>BluetoothLE#BluetoothLEb4:8c:9d:01:02:03-c8:a0:f1:2b:4e:21</c> → <c>C8A0F12B4E21</c>
    /// (die erste Adresse ist der eigene Adapter). So heißt der Geräteknoten, an dem der Akkustand hängt.
    /// </summary>
    public static string? RemoteAddress(string endpointId)
    {
        var matches = MacPattern().Matches(endpointId);
        return matches.Count == 0 ? null : matches[^1].Value.Replace(":", "").ToUpperInvariant();
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
    /// <c>{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2</c> – aber nicht am Bluetooth-Endpunkt, sondern an den
    /// PnP-Geräteknoten des Geräts (<c>BTHLE\DEV_&lt;MAC&gt;…</c>, bei Headsets am Freisprech-Knoten). Die findet
    /// Brim über die MAC-Adresse und fragt sie nach dem Verbinden und dann jede Minute ab, wie die
    /// Windows-Einstellungen.
    /// </summary>
    private sealed class DeviceWatcherSource : IBluetoothSource
    {
        private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        private static readonly TimeSpan BatteryInterval = TimeSpan.FromMinutes(1);
        private Timer? _batteryTimer;

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
                watcher.Added += (sender, info) =>
                {
                    var initial = Volatile.Read(ref _completed) < _watchers.Count;
                    var gadget = new BluetoothGadget(info.Id, info.Name, Battery(info.Properties));
                    lock (_known)
                    {
                        _known[info.Id] = gadget;
                    }
                    Connected?.Invoke(gadget, initial);
                    Task.Run(() => RefreshBatteryAsync(gadget.Id));
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
            _batteryTimer = new Timer(_ => RefreshAll(), null, BatteryInterval, BatteryInterval);
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

        /// <summary>Akkustand am Geräteknoten lesen und als Änderung melden, wenn er neu oder anders ist.</summary>
        private async Task RefreshBatteryAsync(string endpointId)
        {
            if (RemoteAddress(endpointId) is not { } mac)
            {
                return;
            }
            int? battery = null;
            try
            {
                var nodes = await DeviceInformation.FindAllAsync(
                    $"System.Devices.DeviceInstanceId:~~\"{mac}\"", [BatteryKey], DeviceInformationKind.Device);
                battery = nodes.Select(n => Battery(n.Properties)).FirstOrDefault(b => b is not null);
            }
            catch (Exception ex)
            {
                Log.Info($"Akkustand für {mac} nicht lesbar: {ex.Message}");
                return;
            }
            BluetoothGadget? changed = null;
            lock (_known)
            {
                if (battery is not null && _known.TryGetValue(endpointId, out var known) && known.BatteryPercent != battery)
                {
                    changed = known with { BatteryPercent = battery };
                    _known[endpointId] = changed;
                }
            }
            if (changed is not null)
            {
                Updated?.Invoke(changed);
            }
        }

        private void RefreshAll()
        {
            string[] ids;
            lock (_known)
            {
                ids = [.. _known.Keys];
            }
            foreach (var id in ids)
            {
                _ = RefreshBatteryAsync(id);
            }
        }

        private static int? Battery(IReadOnlyDictionary<string, object> properties) =>
            properties.TryGetValue(BatteryKey, out var value) && value is byte or int or uint
                ? Math.Clamp(Convert.ToInt32(value), 0, 100)
                : null;

        public void Dispose()
        {
            _batteryTimer?.Dispose();
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
