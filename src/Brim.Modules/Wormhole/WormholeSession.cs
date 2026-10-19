using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Brim.Core;

namespace Brim.Modules.Wormhole;

/// <summary>Code war falsch oder jemand hat dazwischengefunkt.</summary>
public sealed class WormholeWrongCodeException() : Exception("Der Code passt nicht (vertippt oder schon benutzt).");

/// <summary>Die Gegenseite hat abgebrochen oder abgelehnt.</summary>
public sealed class WormholeRemoteException(string message) : Exception(message);

/// <summary>
/// Eine Sitzung über den Mailbox-Server (Client-Protokoll von magic-wormhole): Anmelden, Code vergeben
/// oder beanspruchen, SPAKE2, Versionen tauschen, danach Ende-zu-Ende-verschlüsselte Nachrichten in fester
/// Reihenfolge. Der Server sieht nur verschlüsselte Daten.
/// </summary>
public sealed class WormholeSession : IAsyncDisposable
{
    /// <summary>Gleiche App-Kennung wie das Original, damit Brim mit „wormhole send/receive“ zusammenarbeitet.</summary>
    public const string AppId = "lothar.com/wormhole/text-or-file-xfer";

    private readonly WebSocket _socket;
    private readonly string _side = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));
    private readonly Channel<JsonObject> _responses = Channel.CreateUnbounded<JsonObject>();
    private readonly Channel<(string Phase, byte[] Body)> _messages = Channel.CreateUnbounded<(string, byte[])>();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<int, byte[]> _early = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader;
    private Spake2? _pake;
    private byte[]? _key;
    private string? _theirSide;
    private string? _nameplate;
    private string? _mailbox;
    private int _nextPhase;
    private int _expectedPhase;
    private Exception? _readerError;

    private WormholeSession(WebSocket socket)
    {
        _socket = socket;
    }

    public string Code { get; private set; } = "";

    /// <summary>Gemeinsamer Schlüssel, nachdem <see cref="EstablishAsync"/> durch ist.</summary>
    public byte[] Key => _key ?? throw new InvalidOperationException("Noch kein Schlüssel");

    // ───────── Aufbau ─────────

    /// <summary>Absender: neuen Code holen. <paramref name="words"/> Wörter nach der Nummer (Original: 2).</summary>
    public static async Task<WormholeSession> CreateAsync(NetConnector net, string mailboxUrl, int words, CancellationToken ct)
    {
        var session = await ConnectAsync(net, mailboxUrl, ct);
        await session.TxAsync(new JsonObject { ["type"] = "allocate" }, ct);
        var allocated = await session.ExpectAsync("allocated", ct);
        var nameplate = allocated["nameplate"]!.GetValue<string>();
        session.Code = $"{nameplate}-{WordList.Choose(words)}";
        await session.ClaimAndOpenAsync(nameplate, ct);
        return session;
    }

    /// <summary>Empfänger: mit einem Code beitreten, z. B. „7-guitarist-revenge“.</summary>
    public static async Task<WormholeSession> JoinAsync(NetConnector net, string mailboxUrl, string code, CancellationToken ct)
    {
        code = Normalize(code);
        var nameplate = code.Split('-')[0];
        if (nameplate.Length == 0 || !nameplate.All(char.IsAsciiDigit) || !code.Contains('-'))
        {
            throw new ArgumentException("Ein Code sieht so aus: 7-guitarist-revenge");
        }
        var session = await ConnectAsync(net, mailboxUrl, ct);
        session.Code = code;
        await session.ClaimAndOpenAsync(nameplate, ct);
        return session;
    }

    /// <summary>Leerzeichen zu Bindestrichen, Kleinschrift, NFC – Codes werden gern mit Leerzeichen abgetippt.</summary>
    public static string Normalize(string code) =>
        string.Join("-", code.Trim().ToLowerInvariant().Split([' ', '-', '\t'], StringSplitOptions.RemoveEmptyEntries)).Normalize(NormalizationForm.FormC);

    private static async Task<WormholeSession> ConnectAsync(NetConnector net, string mailboxUrl, CancellationToken ct)
    {
        var socket = await net.ConnectWebSocketAsync(new Uri(mailboxUrl), ct);
        var session = new WormholeSession(socket);
        session._reader = session.ReadLoopAsync();
        await session.TxAsync(new JsonObject
        {
            ["type"] = "bind",
            ["appid"] = AppId,
            ["side"] = session._side,
            ["client_version"] = new JsonArray("brim", "0.1"),
        }, ct);
        return session;
    }

    private async Task ClaimAndOpenAsync(string nameplate, CancellationToken ct)
    {
        _nameplate = nameplate;
        await TxAsync(new JsonObject { ["type"] = "claim", ["nameplate"] = nameplate }, ct);
        var claimed = await ExpectAsync("claimed", ct);
        _mailbox = claimed["mailbox"]!.GetValue<string>();
        await TxAsync(new JsonObject { ["type"] = "open", ["mailbox"] = _mailbox }, ct);

        _pake = new Spake2(Encoding.UTF8.GetBytes(Code.Normalize(NormalizationForm.FormC)), Encoding.UTF8.GetBytes(AppId));
        var body = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["pake_v1"] = Convert.ToHexStringLower(_pake.Start()) });
        await AddAsync("pake", body, ct);
    }

    /// <summary>
    /// Wartet auf die Gegenseite, leitet den Schlüssel ab und prüft ihn über die Versions-Nachricht.
    /// Ein falscher Code fällt hier auf (<see cref="WormholeWrongCodeException"/>).
    /// </summary>
    public async Task EstablishAsync(CancellationToken ct)
    {
        var (_, pakeBody) = await NextRawAsync("pake", ct);
        var theirPake = JsonNode.Parse(pakeBody)?["pake_v1"]?.GetValue<string>() ?? throw new WormholeWrongCodeException();
        try
        {
            _key = _pake!.Finish(Convert.FromHexString(theirPake));
        }
        catch (CryptographicException)
        {
            throw new WormholeWrongCodeException();
        }

        var versions = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["app_versions"] = new JsonObject() });
        await AddAsync("version", WormholeCrypto.Encrypt(WormholeCrypto.DerivePhaseKey(_key, _side, "version"), versions), ct);

        // Nameplate freigeben: Der Code ist ab jetzt verbraucht.
        await TxAsync(new JsonObject { ["type"] = "release", ["nameplate"] = _nameplate }, ct);

        var (_, theirVersion) = await NextRawAsync("version", ct);
        try
        {
            WormholeCrypto.Decrypt(WormholeCrypto.DerivePhaseKey(_key, _theirSide!, "version"), theirVersion);
        }
        catch (CryptographicException)
        {
            throw new WormholeWrongCodeException();
        }
    }

    // ───────── Nachrichten ─────────

    /// <summary>Schickt eine Anwendungsnachricht (Phase 0, 1, 2 …), verschlüsselt für die Gegenseite.</summary>
    public async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var phase = (_nextPhase++).ToString(CultureInfo.InvariantCulture);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(message);
        await AddAsync(phase, WormholeCrypto.Encrypt(WormholeCrypto.DerivePhaseKey(Key, _side, phase), plaintext), ct);
    }

    /// <summary>Nächste Anwendungsnachricht der Gegenseite, in Reihenfolge.</summary>
    public async Task<JsonObject> ReceiveAsync(CancellationToken ct)
    {
        while (!_early.ContainsKey(_expectedPhase))
        {
            var (phase, body) = await NextRawAsync(null, ct);
            if (int.TryParse(phase, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                _early[number] = body;
            }
        }
        var phaseText = _expectedPhase.ToString(CultureInfo.InvariantCulture);
        var data = _early[_expectedPhase];
        _early.Remove(_expectedPhase);
        _expectedPhase++;
        var plaintext = WormholeCrypto.Decrypt(WormholeCrypto.DerivePhaseKey(Key, _theirSide!, phaseText), data);
        return JsonNode.Parse(plaintext) as JsonObject ?? throw new InvalidDataException("Nachricht ist kein Objekt");
    }

    public byte[] DeriveKey(string purpose, int length = 32) => WormholeCrypto.DeriveKey(Key, purpose, length);

    // ───────── Leitung zum Server ─────────

    private async Task AddAsync(string phase, byte[] body, CancellationToken ct) =>
        await TxAsync(new JsonObject { ["type"] = "add", ["phase"] = phase, ["body"] = Convert.ToHexStringLower(body) }, ct);

    private async Task TxAsync(JsonObject message, CancellationToken ct)
    {
        message["id"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendGate.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<JsonObject> ExpectAsync(string type, CancellationToken ct)
    {
        while (true)
        {
            JsonObject message;
            try
            {
                message = await _responses.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                throw _readerError ?? new IOException("Verbindung zum Wormhole-Server beendet");
            }
            if (message["type"]?.GetValue<string>() == type)
            {
                return message;
            }
        }
    }

    /// <summary>Nächste Nachricht der Gegenseite (eigene Echos werden übersprungen); optional nur eine Phase.</summary>
    private async Task<(string Phase, byte[] Body)> NextRawAsync(string? phase, CancellationToken ct)
    {
        while (true)
        {
            (string Phase, byte[] Body) message;
            try
            {
                message = await _messages.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                throw _readerError ?? new IOException("Verbindung zum Wormhole-Server beendet");
            }
            if (phase is null || message.Phase == phase)
            {
                return message;
            }
            if (int.TryParse(message.Phase, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                _early[number] = message.Body; // früh eingetroffene Anwendungsnachricht aufheben
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    frame.Write(buffer, 0, result.Count);
                    if (frame.Length > 16 * 1024 * 1024)
                    {
                        throw new InvalidDataException("Nachricht vom Server zu groß");
                    }
                }
                while (!result.EndOfMessage);

                if (JsonNode.Parse(frame.ToArray()) is not JsonObject message)
                {
                    continue;
                }
                switch (message["type"]?.GetValue<string>())
                {
                    case "ack" or "pong":
                        break;
                    case "welcome":
                        if (message["welcome"]?["error"]?.GetValue<string>() is { } refused)
                        {
                            throw new IOException($"Wormhole-Server lehnt ab: {refused}");
                        }
                        break;
                    case "error":
                        throw new IOException($"Wormhole-Server meldet: {message["error"]}");
                    case "message":
                        var side = message["side"]?.GetValue<string>();
                        if (side == _side)
                        {
                            break; // eigenes Echo
                        }
                        _theirSide ??= side;
                        if (side != _theirSide)
                        {
                            break; // dritte Partei im selben Postfach: ignorieren
                        }
                        _messages.Writer.TryWrite((message["phase"]!.GetValue<string>(), Convert.FromHexString(message["body"]!.GetValue<string>())));
                        break;
                    default:
                        _responses.Writer.TryWrite(message);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _readerError = ex;
        }
        finally
        {
            _responses.Writer.TryComplete();
            _messages.Writer.TryComplete();
        }
    }

    /// <summary>Postfach schließen. <paramref name="mood"/> wie im Original: happy, lonely, errory, scary.</summary>
    public async Task CloseAsync(string mood = "happy")
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (_mailbox is not null && _socket.State == WebSocketState.Open)
            {
                await TxAsync(new JsonObject { ["type"] = "close", ["mailbox"] = _mailbox, ["mood"] = mood }, timeout.Token);
                await ExpectAsync("closed", timeout.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException)
        {
            Log.Info($"Wormhole: Schließen unvollständig ({ex.Message})");
        }
        _mailbox = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(_key is null ? "lonely" : "happy");
        await _cts.CancelAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
        }
        _socket.Dispose();
        if (_reader is not null)
        {
            await _reader.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        _cts.Dispose();
    }
}
