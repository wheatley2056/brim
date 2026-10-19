using System.Net;
using System.Security.Authentication;
using Brim.Core;
using Brim.Modules.Nearby;

namespace Brim.Modules.Tests;

/// <summary>Zwei Brims in einem Prozess: echte TLS-Verbindung über Loopback.</summary>
public sealed class NearbyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brim-nearby-" + Guid.NewGuid().ToString("N"));
    private NearbyIdentity _alice = null!;
    private NearbyIdentity _bob = null!;
    private string _source = null!;
    private string _inbox = null!;

    public async Task InitializeAsync()
    {
        _alice = await NearbyIdentity.LoadOrCreateAsync(Path.Combine(_root, "alice"), "Alice");
        _bob = await NearbyIdentity.LoadOrCreateAsync(Path.Combine(_root, "bob"), "Bob");
        _source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        _inbox = Directory.CreateDirectory(Path.Combine(_root, "inbox")).FullName;
    }

    private NearbyServer Server(bool accept, List<string>? completed = null)
    {
        var server = new NearbyServer(_bob)
        {
            ConfirmAsync = (_, _) => Task.FromResult(accept),
            PrepareTarget = name => Path.Combine(_inbox, name),
        };
        if (completed is not null)
        {
            server.Completed += (_, roots) => completed.AddRange(roots);
        }
        server.Start();
        return server;
    }

    private NearbyPeer PeerFor(NearbyServer server, string? fingerprint = null) =>
        new(_bob.Id, _bob.Name, fingerprint ?? _bob.Fingerprint, [IPAddress.Loopback], server.Port);

    [Fact]
    public async Task Identity_survives_restart()
    {
        var again = await NearbyIdentity.LoadOrCreateAsync(Path.Combine(_root, "alice"), "Alice");

        Assert.Equal(_alice.Id, again.Id);
        Assert.Equal(_alice.Fingerprint, again.Fingerprint);
    }

    [Fact]
    public async Task Sends_files_and_folders()
    {
        File.WriteAllText(Path.Combine(_source, "brief.txt"), "Hallo Bob");
        var folder = Directory.CreateDirectory(Path.Combine(_source, "Projekt")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "daten.bin"), Enumerable.Range(0, 700_000).Select(i => (byte)i).ToArray());
        Directory.CreateDirectory(Path.Combine(folder, "leer"));
        var completed = new List<string>();
        using var server = Server(accept: true, completed);
        var reports = new List<TransferProgress>();

        await NearbyClient.SendAsync(_alice, PeerFor(server), [Path.Combine(_source, "brief.txt"), folder],
            new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal("Hallo Bob", File.ReadAllText(Path.Combine(_inbox, "brief.txt")));
        Assert.Equal(700_000, new FileInfo(Path.Combine(_inbox, "Projekt", "daten.bin")).Length);
        Assert.True(Directory.Exists(Path.Combine(_inbox, "Projekt", "leer")));
        Assert.Equal(1, reports[^1].Fraction);
        await WaitFor(() => completed.Count == 2);
    }

    [Fact]
    public async Task Declined_offer_sends_nothing()
    {
        File.WriteAllText(Path.Combine(_source, "geheim.txt"), "x");
        using var server = Server(accept: false);

        await Assert.ThrowsAsync<NearbyDeclinedException>(() =>
            NearbyClient.SendAsync(_alice, PeerFor(server), [Path.Combine(_source, "geheim.txt")], null, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_inbox, "geheim.txt")));
    }

    [Fact]
    public async Task Wrong_fingerprint_is_refused()
    {
        File.WriteAllText(Path.Combine(_source, "a.txt"), "x");
        using var server = Server(accept: true);

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            NearbyClient.SendAsync(_alice, PeerFor(server, fingerprint: _alice.Fingerprint), [Path.Combine(_source, "a.txt")], null, CancellationToken.None));
    }

    [Theory]
    [InlineData("../boese.txt")]
    [InlineData("ordner/../../boese.txt")]
    [InlineData("C:/Windows/boese.txt")]
    [InlineData("a//b")]
    [InlineData("con.")]
    public void Unsafe_paths_are_rejected(string path)
    {
        Assert.Throws<InvalidDataException>(() => NearbyProtocol.SafeCombine(_inbox, path));
    }

    [Fact]
    public void Safe_paths_stay_inside()
    {
        var full = NearbyProtocol.SafeCombine(_inbox, "Projekt/unter/datei.txt");

        Assert.StartsWith(_inbox, full);
    }

    [Fact]
    public async Task Peers_find_each_other_on_the_network()
    {
        // CI-Runner haben meist kein Multicast im Netz; dort nur lokal prüfen.
        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            return;
        }
        using var server = Server(accept: true);
        using var alice = new NearbyDiscovery(_alice);
        using var bob = new NearbyDiscovery(_bob);
        bob.Register(server.Port);
        alice.Start();

        await WaitFor(() => alice.Find(_bob.Id) is not null, TimeSpan.FromSeconds(20));

        var found = alice.Find(_bob.Id)!;
        Assert.Equal("Bob", found.Name);
        Assert.Equal(_bob.Fingerprint, found.Fingerprint);
        Assert.Equal(server.Port, found.Port);
    }

    /// <summary>
    /// Handtest gegen ein laufendes Brim auf diesem Rechner: spielt ein fremdes Brim („Testgerät“) und
    /// schickt ihm eine Datei. Nur mit <c>BRIM_MANUAL_SEND=1</c>; die Antwort gibt der Mensch in der Notch.
    /// </summary>
    [Fact]
    public async Task Manual_send_to_running_brim()
    {
        if (Environment.GetEnvironmentVariable("BRIM_MANUAL_SEND") != "1")
        {
            return;
        }
        var tester = await NearbyIdentity.LoadOrCreateAsync(Path.Combine(_root, "tester"), "Testgerät");
        using var discovery = new NearbyDiscovery(tester);
        discovery.Start();
        await WaitFor(() => discovery.Peers.Any(p => p.Id != _alice.Id && p.Id != _bob.Id), TimeSpan.FromSeconds(20));
        var target = discovery.Peers.First(p => p.Id != _alice.Id && p.Id != _bob.Id);
        var file = Path.Combine(_source, "Gruß vom Testgerät.txt");
        File.WriteAllText(file, "Hallo aus dem Brim-Test.");

        try
        {
            await NearbyClient.SendAsync(tester, target, [file], null, CancellationToken.None);
            Console.WriteLine($"Angenommen von {target.Name}");
        }
        catch (NearbyDeclinedException ex)
        {
            Console.WriteLine($"Abgelehnt: {ex.Message}");
        }
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Zeitüberschreitung");
            await Task.Delay(100);
        }
    }

    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        return Task.CompletedTask;
    }
}
