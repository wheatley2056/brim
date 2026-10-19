using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Brim.Core;
using Brim.Modules.Wormhole;

namespace Brim.Modules.Tests;

/// <summary>
/// Teilen per Code durch einen Proxy: kleiner lokaler HTTP-CONNECT- bzw. SOCKS5-Proxy, über den
/// Mailbox und Relay laufen müssen. Braucht Internet (öffentliche Wormhole-Server), daher nur mit
/// <c>BRIM_WORMHOLE_CLI</c> gesetzt, wie die anderen Netztests.
/// </summary>
public sealed class ProxyTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "brim-proxy-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    [Theory]
    [InlineData("http")]
    [InlineData("socks5")]
    public async Task Brim_to_brim_through_proxy(string kind)
    {
        if (Environment.GetEnvironmentVariable("BRIM_WORMHOLE_CLI") is null)
        {
            return;
        }
        using var proxy = new TestProxy(socks: kind == "socks5");
        var settings = new ShareSettings { Proxy = ProxyMode.Manual, ProxyUrl = $"{kind}://127.0.0.1:{proxy.Port}" };
        var file = Path.Combine(_root, "durch-den-proxy.bin");
        File.WriteAllBytes(file, Enumerable.Range(0, 500_000).Select(i => (byte)(i * 7)).ToArray());
        var inbox = Directory.CreateDirectory(Path.Combine(_root, "inbox")).FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var codeReady = new TaskCompletionSource<string>();

        // Ohne direkte Wege: alles muss über Mailbox und Relay – also durch den Proxy.
        var send = new WormholeTransfer(settings) { UseDirect = false }.SendAsync([file], codeReady.SetResult, null, timeout.Token);
        var code = await codeReady.Task.WaitAsync(timeout.Token);
        var received = await new WormholeTransfer(settings) { UseDirect = false }.ReceiveAsync(code,
            (_, _) => Task.FromResult(true), name => Path.Combine(inbox, name), null, timeout.Token);
        await send;

        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Assert.Single(received)));
        Assert.Contains(proxy.Targets, t => t.StartsWith("relay.magic-wormhole.io:", StringComparison.Ordinal));
        Assert.Contains(proxy.Targets, t => t.StartsWith("transit.magic-wormhole.io:", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_proxy_url_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new NetConnector(ProxyMode.Manual, "ftp://proxy:21"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Minimaler Proxy für Tests: HTTP CONNECT oder SOCKS5 ohne Anmeldung, merkt sich die Ziele.</summary>
    private sealed class TestProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly bool _socks;
        private readonly CancellationTokenSource _cts = new();

        public TestProxy(bool socks)
        {
            _socks = socks;
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public List<string> Targets { get; } = [];

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleAsync(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            try
            {
                var (host, port) = _socks ? await SocksAsync(stream) : await ConnectAsync(stream);
                lock (Targets)
                {
                    Targets.Add($"{host}:{port}");
                }
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(host, port, _cts.Token);
                if (_socks)
                {
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, _cts.Token);
                }
                else
                {
                    await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), _cts.Token);
                }
                var up = upstream.GetStream();
                await Task.WhenAny(stream.CopyToAsync(up, _cts.Token), up.CopyToAsync(stream, _cts.Token));
            }
            catch (Exception)
            {
                // Verbindungsende gehört zum normalen Ablauf.
            }
        }

        private static async Task<(string, int)> ConnectAsync(NetworkStream stream)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(one);
                head.Append((char)one[0]);
            }
            var target = head.ToString().Split(' ')[1];
            var colon = target.LastIndexOf(':');
            return (target[..colon], int.Parse(target[(colon + 1)..]));
        }

        private static async Task<(string, int)> SocksAsync(NetworkStream stream)
        {
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting);
            await stream.ReadExactlyAsync(new byte[greeting[1]]);
            await stream.WriteAsync(new byte[] { 5, 0 });
            var request = new byte[5];
            await stream.ReadExactlyAsync(request);
            if (request[3] != 3)
            {
                throw new InvalidDataException("Test-Proxy versteht nur Hostnamen");
            }
            var name = new byte[request[4]];
            await stream.ReadExactlyAsync(name);
            var port = new byte[2];
            await stream.ReadExactlyAsync(port);
            return (Encoding.ASCII.GetString(name), BinaryPrimitives.ReadUInt16BigEndian(port));
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
