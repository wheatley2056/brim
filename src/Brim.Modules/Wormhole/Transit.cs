using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Brim.Core;

namespace Brim.Modules.Wormhole;

/// <summary>Ein Verbindungsweg aus den Transit-Hinweisen.</summary>
internal readonly record struct TransitHint(string Host, int Port, bool Relay);

/// <summary>
/// Transit von magic-wormhole (<c>wormhole/transit.py</c>): Beide Seiten nennen Wege (eigene Adressen,
/// Relay), versuchen alle gleichzeitig, und die erste Verbindung mit gültigem Handschlag gewinnt.
/// Über das Relay geht es auch hinter NAT; über den Proxy auch aus Firmennetzen.
/// </summary>
internal sealed class Transit : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly bool _isSender;
    private readonly byte[] _key;
    private readonly NetConnector _net;
    private readonly TransitHint _ownRelay;
    private readonly string _side = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    private readonly TcpListener _listener;
    private int _chosen;
    private readonly bool _direct;

    public Transit(bool isSender, byte[] transitKey, NetConnector net, string relay, bool direct = true)
    {
        _isSender = isSender;
        _direct = direct;
        _key = transitKey;
        _net = net;
        _ownRelay = ParseRelay(relay);
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
    }

    /// <summary><c>tcp:host:port</c> wie im Original.</summary>
    private static TransitHint ParseRelay(string relay)
    {
        var parts = relay.Split(':');
        if (parts.Length != 3 || parts[0] != "tcp" || !int.TryParse(parts[2], out var port))
        {
            throw new ArgumentException($"Relay muss tcp:host:port sein, nicht „{relay}“");
        }
        return new TransitHint(parts[1], port, true);
    }

    // ───────── Hinweise ─────────

    /// <summary>Unser Transit-Eintrag für die Wormhole-Nachricht <c>{"transit": …}</c>.</summary>
    public JsonObject Describe()
    {
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var direct = new JsonArray();
        foreach (var address in _direct ? LocalAddresses() : [])
        {
            direct.Add(new JsonObject { ["type"] = "direct-tcp-v1", ["priority"] = 0.0, ["hostname"] = address, ["port"] = port });
        }
        direct.Add(new JsonObject
        {
            ["type"] = "relay-v1",
            ["hints"] = new JsonArray(new JsonObject { ["type"] = "direct-tcp-v1", ["priority"] = 0.0, ["hostname"] = _ownRelay.Host, ["port"] = _ownRelay.Port }),
        });
        return new JsonObject
        {
            ["abilities-v1"] = new JsonArray(new JsonObject { ["type"] = "direct-tcp-v1" }, new JsonObject { ["type"] = "relay-v1" }),
            ["hints-v1"] = direct,
        };
    }

    private static IEnumerable<string> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Select(a => a.ToString())
            .Distinct();

    /// <summary>Wege der Gegenseite aus ihrem <c>{"transit": …}</c>.</summary>
    public static List<TransitHint> Parse(JsonNode? transit)
    {
        var result = new List<TransitHint>();
        foreach (var hint in transit?["hints-v1"]?.AsArray() ?? [])
        {
            switch (hint?["type"]?.GetValue<string>())
            {
                case "direct-tcp-v1" when TryHint(hint, relay: false) is { } direct:
                    result.Add(direct);
                    break;
                case "relay-v1":
                    foreach (var inner in hint["hints"]?.AsArray() ?? [])
                    {
                        if (inner?["type"]?.GetValue<string>() == "direct-tcp-v1" && TryHint(inner, relay: true) is { } relay)
                        {
                            result.Add(relay);
                        }
                    }
                    break;
            }
        }
        return result;
    }

    private static TransitHint? TryHint(JsonNode node, bool relay)
    {
        var host = node["hostname"]?.GetValue<string>();
        var port = node["port"]?.GetValue<int>() ?? 0;
        return string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535 ? null : new TransitHint(host, port, relay);
    }

    // ───────── Verbinden ─────────

    private byte[] SenderHandshake => Encoding.ASCII.GetBytes($"transit sender {Convert.ToHexStringLower(WormholeCrypto.DeriveKey(_key, "transit_sender"))} ready\n\n");
    private byte[] ReceiverHandshake => Encoding.ASCII.GetBytes($"transit receiver {Convert.ToHexStringLower(WormholeCrypto.DeriveKey(_key, "transit_receiver"))} ready\n\n");
    private byte[] RelayHandshake => Encoding.ASCII.GetBytes($"please relay {Convert.ToHexStringLower(WormholeCrypto.DeriveKey(_key, "transit_relay_token"))} for side {_side}\n");

    /// <summary>Probiert alle Wege gleichzeitig; liefert die erste ausgehandelte Verbindung.</summary>
    public async Task<RecordPipe> ConnectAsync(IReadOnlyList<TransitHint> theirs, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(Timeout);
        var winner = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new List<Task>();

        // Direkte Wege der Gegenseite (LAN), ohne Proxy.
        foreach (var hint in theirs.Where(h => !h.Relay && _direct))
        {
            attempts.Add(TryAsync(() => _net.ConnectAsync(hint.Host, hint.Port, linked.Token, allowProxy: false), relay: false, winner, linked.Token));
        }
        // Relays (unseres und ihre), über den Proxy, falls eingestellt.
        foreach (var hint in theirs.Where(h => h.Relay).Append(_ownRelay).DistinctBy(h => (h.Host.ToLowerInvariant(), h.Port)))
        {
            attempts.Add(TryAsync(() => _net.ConnectAsync(hint.Host, hint.Port, linked.Token), relay: true, winner, linked.Token));
        }
        // Eingehend: Die Gegenseite verbindet sich mit unseren direkten Hinweisen.
        attempts.Add(AcceptLoopAsync(winner, linked.Token));

        using (linked.Token.Register(() => winner.TrySetException(new TimeoutException("Keine Verbindung zur Gegenseite (direkt und über Relay gescheitert)"))))
        {
            var stream = await winner.Task;
            await linked.CancelAsync(); // übrige Versuche beenden
            await Task.WhenAll(attempts).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return new RecordPipe(stream, _isSender, _key);
        }
    }

    private async Task AcceptLoopAsync(TaskCompletionSource<Stream> winner, CancellationToken ct)
    {
        var pending = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                pending.Add(TryAsync(() => Task.FromResult<Stream>(client.GetStream()), relay: false, winner, ct));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Ein Weg: verbinden, ggf. Relay-Handschlag, Transit-Handschlag, Entscheidung.</summary>
    private async Task TryAsync(Func<Task<Stream>> open, bool relay, TaskCompletionSource<Stream> winner, CancellationToken ct)
    {
        Stream? stream = null;
        try
        {
            stream = await open();
            if (relay)
            {
                await stream.WriteAsync(RelayHandshake, ct);
                await ExpectAsync(stream, "ok\n"u8.ToArray(), ct);
            }
            await stream.WriteAsync(_isSender ? SenderHandshake : ReceiverHandshake, ct);
            await ExpectAsync(stream, _isSender ? ReceiverHandshake : SenderHandshake, ct);

            if (_isSender)
            {
                // Der Absender entscheidet: erste fertige Verbindung bekommt „go“, alle anderen „nevermind“.
                // Atomar, sonst könnten zwei Wege gleichzeitig „go“ senden und die Seiten sich uneinig sein.
                if (Interlocked.CompareExchange(ref _chosen, 1, 0) != 0)
                {
                    await stream.WriteAsync("nevermind\n"u8.ToArray(), ct);
                    stream.Dispose();
                    return;
                }
                await stream.WriteAsync("go\n"u8.ToArray(), ct);
            }
            else
            {
                await ExpectAsync(stream, "go\n"u8.ToArray(), ct);
            }
            if (!winner.TrySetResult(stream))
            {
                stream.Dispose();
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Log.Info($"Wormhole-Transit: Weg verworfen ({(relay ? "Relay" : "direkt")}): {ex.Message}");
            }
            stream?.Dispose();
        }
    }

    /// <summary>Liest genau die erwarteten Bytes; jede Abweichung bricht ab (wie <c>_check_and_remove</c>).</summary>
    private static async Task ExpectAsync(Stream stream, byte[] expected, CancellationToken ct)
    {
        var buffer = new byte[expected.Length];
        await stream.ReadExactlyAsync(buffer, ct);
        if (!buffer.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException("Transit-Handschlag passt nicht");
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Verschlüsselte Datensätze über die Transit-Verbindung: <c>[Länge uint32 BE][Nonce 24][SecretBox]</c>,
/// die Nonce zählt je Richtung ab 0 hoch (Big Endian) und wird beim Empfang geprüft.
/// </summary>
internal sealed class RecordPipe : IAsyncDisposable
{
    private const int MaxRecord = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private ulong _sendNonce;
    private ulong _receiveNonce;

    public RecordPipe(Stream stream, bool isSender, byte[] transitKey)
    {
        _stream = stream;
        var senderKey = WormholeCrypto.DeriveKey(transitKey, "transit_record_sender_key");
        var receiverKey = WormholeCrypto.DeriveKey(transitKey, "transit_record_receiver_key");
        (_sendKey, _receiveKey) = isSender ? (senderKey, receiverKey) : (receiverKey, senderKey);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> record, CancellationToken ct)
    {
        var nonce = new byte[Sodium.SecretBoxNonceBytes];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(16), _sendNonce++);
        var box = Sodium.SecretBox(record.Span, nonce, _sendKey);
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)(nonce.Length + box.Length));
        await _stream.WriteAsync(length, ct);
        await _stream.WriteAsync(nonce, ct);
        await _stream.WriteAsync(box, ct);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await _stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length < Sodium.SecretBoxNonceBytes + Sodium.SecretBoxMacBytes || length > MaxRecord)
        {
            throw new InvalidDataException($"Ungültige Datensatzlänge {length}");
        }
        var frame = new byte[length];
        await _stream.ReadExactlyAsync(frame, ct);
        var nonce = frame.AsSpan(0, Sodium.SecretBoxNonceBytes);
        if (nonce[..16].ContainsAnyExcept((byte)0) || BinaryPrimitives.ReadUInt64BigEndian(nonce[16..]) != _receiveNonce)
        {
            throw new CryptographicException("Datensatz außer der Reihe (Nonce)");
        }
        _receiveNonce++;
        return Sodium.SecretBoxOpen(frame.AsSpan(Sodium.SecretBoxNonceBytes), nonce, _receiveKey);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
