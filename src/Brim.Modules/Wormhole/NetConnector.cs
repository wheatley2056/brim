using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Brim.Core;

namespace Brim.Modules.Wormhole;

/// <summary>
/// Baut TCP-Verbindungen auf – direkt, über einen HTTP-Proxy (CONNECT) oder SOCKS5 – und darauf
/// WebSockets. Mailbox und Transit-Relay laufen so auf demselben Weg durch Proxys.
/// </summary>
public sealed class NetConnector
{
    private readonly ProxyMode _mode;
    private readonly Uri? _manual;

    public NetConnector(ProxyMode mode, string? proxyUrl)
    {
        _mode = mode;
        if (mode == ProxyMode.Manual)
        {
            if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out _manual) || _manual.Scheme is not ("http" or "socks5" or "socks5h"))
            {
                throw new ArgumentException($"Proxy muss http://host:port oder socks5://host:port sein, nicht „{proxyUrl}“");
            }
        }
    }

    public static NetConnector From(ShareSettings settings) => new(settings.Proxy, settings.ProxyUrl);

    /// <summary>Welcher Proxy gilt für dieses Ziel? <c>null</c> = direkt.</summary>
    public Uri? ProxyFor(string host, int port)
    {
        switch (_mode)
        {
            case ProxyMode.None:
                return null;
            case ProxyMode.Manual:
                return _manual;
            default:
                var target = new Uri($"http://{host}:{port}");
                var system = HttpClient.DefaultProxy;
                if (system.IsBypassed(target))
                {
                    return null;
                }
                var proxy = system.GetProxy(target);
                return proxy is null || proxy == target ? null : proxy;
        }
    }

    /// <summary>TCP-Verbindung zum Ziel, gegebenenfalls durch den Proxy getunnelt.</summary>
    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct, bool allowProxy = true)
    {
        var proxy = allowProxy ? ProxyFor(host, port) : null;
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            if (proxy is null)
            {
                await tcp.ConnectAsync(host, port, ct);
                return tcp.GetStream();
            }
            await tcp.ConnectAsync(proxy.Host, proxy.Port, ct);
            var stream = tcp.GetStream();
            if (proxy.Scheme.StartsWith("socks", StringComparison.Ordinal))
            {
                await Socks5Async(stream, proxy, host, port, ct);
            }
            else
            {
                await HttpConnectAsync(stream, proxy, host, port, ct);
            }
            return stream;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    // ───────── HTTP CONNECT ─────────

    private static async Task HttpConnectAsync(Stream stream, Uri proxy, string host, int port, CancellationToken ct)
    {
        var request = new StringBuilder()
            .Append($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n");
        if (!string.IsNullOrEmpty(proxy.UserInfo))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(proxy.UserInfo)));
            request.Append($"Proxy-Authorization: Basic {credentials}\r\n");
        }
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct);

        var head = await ReadHttpHeadAsync(stream, ct);
        var status = head.Split(' ', 3);
        if (status.Length < 2 || status[1] != "200")
        {
            throw new IOException($"Proxy lehnt die Verbindung zu {host}:{port} ab: {head.Split('\r')[0]}");
        }
    }

    /// <summary>Liest bis zur Leerzeile, Byte für Byte, damit nichts von den folgenden Daten verschluckt wird.</summary>
    private static async Task<string> ReadHttpHeadAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                throw new EndOfStreamException("Verbindung während des HTTP-Kopfs beendet");
            }
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
        }
        throw new IOException("HTTP-Kopf zu lang");
    }

    // ───────── SOCKS5 (RFC 1928/1929) ─────────

    private static async Task Socks5Async(Stream stream, Uri proxy, string host, int port, CancellationToken ct)
    {
        var hasAuth = !string.IsNullOrEmpty(proxy.UserInfo);
        await stream.WriteAsync(hasAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct);
        var choice = new byte[2];
        await stream.ReadExactlyAsync(choice, ct);
        if (choice[0] != 5 || choice[1] == 0xFF)
        {
            throw new IOException("SOCKS5-Proxy akzeptiert keine unserer Anmeldearten");
        }
        if (choice[1] == 2)
        {
            var parts = Uri.UnescapeDataString(proxy.UserInfo).Split(':', 2);
            var user = Encoding.UTF8.GetBytes(parts[0]);
            var pass = Encoding.UTF8.GetBytes(parts.Length > 1 ? parts[1] : "");
            await stream.WriteAsync((byte[])[1, (byte)user.Length, .. user, (byte)pass.Length, .. pass], ct);
            var auth = new byte[2];
            await stream.ReadExactlyAsync(auth, ct);
            if (auth[1] != 0)
            {
                throw new IOException("SOCKS5-Anmeldung abgelehnt");
            }
        }

        var name = Encoding.ASCII.GetBytes(host);
        var portBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        await stream.WriteAsync((byte[])[5, 1, 0, 3, (byte)name.Length, .. name, .. portBytes], ct);

        var reply = new byte[4];
        await stream.ReadExactlyAsync(reply, ct);
        if (reply[1] != 0)
        {
            throw new IOException($"SOCKS5-Proxy meldet Fehler {reply[1]} für {host}:{port}");
        }
        var skip = reply[3] switch
        {
            1 => 4,
            4 => 16,
            3 => (await ReadByteAsync(stream, ct)),
            _ => throw new IOException("SOCKS5-Antwort unbekannt"),
        };
        await stream.ReadExactlyAsync(new byte[skip + 2], ct); // gebundene Adresse und Port
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, ct);
        return one[0];
    }

    // ───────── WebSocket ─────────

    /// <summary>
    /// WebSocket-Verbindung (ws:// oder wss://) über <see cref="ConnectAsync"/>. Der Upgrade-Handschlag
    /// wird hier selbst gesprochen, damit er auch durch reine CONNECT- oder SOCKS-Proxys geht.
    /// </summary>
    public async Task<WebSocket> ConnectWebSocketAsync(Uri url, CancellationToken ct)
    {
        var secure = url.Scheme == "wss";
        var port = url.IsDefaultPort ? (secure ? 443 : 80) : url.Port;
        var stream = await ConnectAsync(url.Host, port, ct);
        if (secure)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = url.Host }, ct);
            stream = ssl;
        }

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request =
            $"GET {url.PathAndQuery} HTTP/1.1\r\n" +
            $"Host: {url.Host}:{port}\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n" +
            "User-Agent: Brim\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);

        var head = await ReadHttpHeadAsync(stream, ct);
        if (!head.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
        {
            stream.Dispose();
            throw new WebSocketException($"Server lehnt WebSocket ab: {head.Split('\r')[0]}");
        }
        var expected = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        if (!head.Contains(expected, StringComparison.Ordinal))
        {
            stream.Dispose();
            throw new WebSocketException("WebSocket-Antwort passt nicht zur Anfrage");
        }
        Log.Info($"Wormhole: verbunden mit {url.Host}:{port}{(ProxyFor(url.Host, port) is { } p ? " über " + p.Host : "")}");
        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.FromSeconds(30) });
    }
}
