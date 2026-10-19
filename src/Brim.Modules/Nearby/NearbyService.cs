using Brim.Core;

namespace Brim.Modules.Nearby;

/// <summary>
/// Teilen zwischen Brims im LAN (Plan 4.4a, Stufe 1): bündelt Identität, Empfang und Suche.
/// Empfangene Dateien landen als Kopien in der Ablage.
/// </summary>
public sealed class NearbyService : IAsyncDisposable
{
    private readonly ShelfStore _shelf;
    private NearbyServer? _server;
    private NearbyDiscovery? _discovery;

    public NearbyService(ShelfStore shelf)
    {
        _shelf = shelf;
    }

    public NearbyIdentity? Identity { get; private set; }

    public ShelfStore Shelf => _shelf;

    public IReadOnlyList<NearbyPeer> Peers => _discovery?.Peers ?? [];

    /// <summary>Gefundene Brims haben sich geändert.</summary>
    public event Action? PeersChanged;

    /// <summary>
    /// Fragt den Nutzer, ob ein Angebot angenommen wird. Wer sich hier anmeldet (das Modul mit der
    /// Annahme-Frage in der Notch), entscheidet; ohne Anmeldung wird alles abgelehnt.
    /// </summary>
    public Func<IncomingOffer, CancellationToken, Task<bool>>? ConfirmAsync { get; set; }

    public event Action<Offer, TransferProgress>? ReceiveProgress;
    public event Action<Offer, IReadOnlyList<ShelfItem>>? Received;
    public event Action<Offer?, string>? ReceiveFailed;

    public async Task StartAsync(string dataDirectory, string? displayName = null)
    {
        Identity = await NearbyIdentity.LoadOrCreateAsync(dataDirectory, displayName);
        _discovery = new NearbyDiscovery(Identity);
        _discovery.Changed += () => PeersChanged?.Invoke();

        _server = new NearbyServer(Identity)
        {
            ConfirmAsync = (offer, ct) => ConfirmAsync?.Invoke(offer, ct) ?? Task.FromResult(false),
            PrepareTarget = _shelf.ReserveIncoming,
            ExpectedFingerprint = id => _discovery.Find(id)?.Fingerprint,
        };
        _server.Progress += (offer, progress) => ReceiveProgress?.Invoke(offer, progress);
        _server.Completed += (offer, paths) => Received?.Invoke(offer, _shelf.AddReceived(paths));
        _server.Failed += (offer, reason) => ReceiveFailed?.Invoke(offer, reason);
        _server.Start();

        _discovery.Register(_server.Port);
        _discovery.Start();
        Log.Info($"Teilen: bereit auf Port {_server.Port}");
    }

    /// <summary>Schickt Dateien und Ordner an ein anderes Brim. Wirft <see cref="NearbyDeclinedException"/> bei Ablehnung.</summary>
    public Task SendAsync(NearbyPeer peer, IReadOnlyList<string> paths, IProgress<TransferProgress>? progress, CancellationToken ct) =>
        NearbyClient.SendAsync(Identity ?? throw new InvalidOperationException("Teilen ist nicht gestartet"), peer, paths, progress, ct);

    public ValueTask DisposeAsync()
    {
        _discovery?.Dispose();
        _server?.Dispose();
        return ValueTask.CompletedTask;
    }
}
