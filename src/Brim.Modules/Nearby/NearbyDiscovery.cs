using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using Brim.Core;
using Windows.Devices.Enumeration;
using Windows.Win32;
using Windows.Win32.NetworkManagement.Dns;

namespace Brim.Modules.Nearby;

/// <summary>Ein anderes Brim im lokalen Netz.</summary>
public sealed record NearbyPeer(string Id, string Name, string Fingerprint, IPAddress[] Addresses, int Port);

/// <summary>
/// Finden im LAN über DNS-SD/mDNS (Dienst <c>_brim._tcp.local</c>), nur mit Windows-Bordmitteln:
/// Anmelden über <c>DnsServiceRegister</c> (Win32, ab Windows 10 1809), Suchen über einen
/// <see cref="DeviceWatcher"/> für DNS-SD-Dienste (WinRT).
/// </summary>
public sealed unsafe class NearbyDiscovery : IDisposable
{
    private const string DnssdProtocol = "{4526e8c1-8aac-4153-9b16-55e86ada0e54}";
    private const uint DnsRequestPending = 9506;

    private static readonly string[] Properties =
    [
        "System.Devices.Dnssd.InstanceName",
        "System.Devices.Dnssd.HostName",
        "System.Devices.Dnssd.PortNumber",
        "System.Devices.Dnssd.TextAttributes",
        "System.Devices.IpAddress",
    ];

    private readonly NearbyIdentity _identity;
    private readonly ConcurrentDictionary<string, NearbyPeer> _peers = new();
    private readonly ConcurrentDictionary<string, string> _deviceToPeer = new();
    private readonly PDNS_SERVICE_REGISTER_COMPLETE _registered;
    private DNS_SERVICE_REGISTER_REQUEST _request;
    private bool _isRegistered;
    private DeviceWatcher? _watcher;

    public NearbyDiscovery(NearbyIdentity identity)
    {
        _identity = identity;
        _registered = OnRegistered;
    }

    /// <summary>Liste der gefundenen Brims hat sich geändert.</summary>
    public event Action? Changed;

    public IReadOnlyList<NearbyPeer> Peers => _peers.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public NearbyPeer? Find(string id) => _peers.GetValueOrDefault(id);

    /// <summary>Instanzname im Netz; muss eindeutig und kurz sein (höchstens 63 Bytes).</summary>
    private string InstanceName => $"Brim-{_identity.Id}.{NearbyProtocol.ServiceType}.local";

    // ───────── Anmelden ─────────

    public unsafe void Register(int port)
    {
        var host = Environment.MachineName + ".local";
        var keys = new[] { "v", "id", "name", "fp" };
        var values = new[] { NearbyProtocol.Version.ToString(), _identity.Id, Truncate(_identity.Name, 200), _identity.Fingerprint };
        var instance = PInvoke.DnsServiceConstructInstance(InstanceName, host, null, null, (ushort)port, 0, 0, keys, values);
        if (instance is null)
        {
            Log.Warn("Teilen: DNS-SD-Eintrag ließ sich nicht anlegen");
            return;
        }
        _request = new DNS_SERVICE_REGISTER_REQUEST
        {
            Version = (uint)DNS_QUERY_OPTIONS.DNS_QUERY_REQUEST_VERSION1,
            InterfaceIndex = 0,
            pServiceInstance = instance,
            pRegisterCompletionCallback = _registered,
        };
        var status = PInvoke.DnsServiceRegister(in _request);
        if (status != DnsRequestPending)
        {
            Log.Warn($"Teilen: Anmelden im Netz fehlgeschlagen ({status})");
            PInvoke.DnsServiceFreeInstance(instance);
            return;
        }
        _isRegistered = true;
    }

    private unsafe void OnRegistered(uint status, void* context, DNS_SERVICE_INSTANCE* instance)
    {
        if (status == 0)
        {
            Log.Info($"Teilen: im Netz sichtbar als {_identity.Name}");
        }
        else
        {
            Log.Warn($"Teilen: Anmelden im Netz meldete {status}");
        }
        if (instance is not null)
        {
            PInvoke.DnsServiceFreeInstance(instance);
        }
    }

    // ───────── Suchen ─────────

    public void Start()
    {
        var aqs = $"System.Devices.AepService.ProtocolId:=\"{DnssdProtocol}\" AND " +
                  $"System.Devices.Dnssd.Domain:=\"local\" AND System.Devices.Dnssd.ServiceName:=\"{NearbyProtocol.ServiceType}\"";
        _watcher = DeviceInformation.CreateWatcher(aqs, Properties, DeviceInformationKind.AssociationEndpointService);
        _watcher.Added += (_, info) => Upsert(info.Id, info.Properties);
        _watcher.Updated += (_, update) => Upsert(update.Id, update.Properties);
        _watcher.Removed += (_, update) => Remove(update.Id);
        _watcher.Start();
    }

    private void Upsert(string deviceId, IReadOnlyDictionary<string, object> properties)
    {
        try
        {
            var text = Get<string[]>(properties, "System.Devices.Dnssd.TextAttributes") ?? [];
            var txt = text.Select(t => t.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            if (!txt.TryGetValue("id", out var id) || id == _identity.Id || !txt.TryGetValue("fp", out var fingerprint))
            {
                return;
            }
            var addresses = (Get<string[]>(properties, "System.Devices.IpAddress") ?? [])
                .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
                .OfType<IPAddress>()
                // IPv4 zuerst: Link-lokale IPv6-Adressen brauchen eine Schnittstellen-Kennung.
                .OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
                .ToArray();
            var port = Get<ushort?>(properties, "System.Devices.Dnssd.PortNumber") ?? 0;
            if (addresses.Length == 0 || port == 0)
            {
                // Updated-Ereignisse liefern oft nur Teile; vorhandene Daten behalten.
                if (_peers.TryGetValue(id, out var known))
                {
                    _deviceToPeer[deviceId] = known.Id;
                }
                return;
            }
            var peer = new NearbyPeer(id, txt.GetValueOrDefault("name", id), fingerprint, addresses, port);
            _deviceToPeer[deviceId] = id;
            if (!_peers.TryGetValue(id, out var previous) || previous != peer)
            {
                _peers[id] = peer;
                Log.Info($"Teilen: {peer.Name} gefunden ({string.Join(", ", addresses.Select(a => a.ToString()))}:{port})");
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Teilen: Eintrag aus der Suche unlesbar", ex);
        }
    }

    private void Remove(string deviceId)
    {
        if (_deviceToPeer.TryRemove(deviceId, out var id) && _peers.TryRemove(id, out var peer))
        {
            Log.Info($"Teilen: {peer.Name} nicht mehr erreichbar");
            Changed?.Invoke();
        }
    }

    private static T? Get<T>(IReadOnlyDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var value) && value is T typed ? typed : default;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        try
        {
            _watcher?.Stop();
        }
        catch (InvalidOperationException)
        {
        }
        if (_isRegistered)
        {
            PInvoke.DnsServiceDeRegister(in _request);
            _isRegistered = false;
        }
        GC.KeepAlive(_registered);
    }
}
