using System.Diagnostics;
using System.Text.Json.Nodes;
using Brim.Core;
using Brim.Modules.Wormhole;

namespace Brim.Modules.Tests;

/// <summary>
/// Echte Gegenproben mit dem Original-Programm (magic-wormhole, Python) über die öffentlichen Server.
/// Nur mit <c>BRIM_WORMHOLE_CLI=Pfad\zu\wormhole.exe</c>, da Netz und Installation nötig sind.
/// </summary>
public sealed class WormholeInteropTests : IDisposable
{
    private static readonly string? Cli = Environment.GetEnvironmentVariable("BRIM_WORMHOLE_CLI");
    private static readonly ShareSettings Settings = new() { Proxy = ProxyMode.None };
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "brim-wh-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private static string RandomCode() => $"{Random.Shared.Next(100, 999)}-brim-{Guid.NewGuid().ToString("N")[..6]}";

    private Process StartCli(params string[] args)
    {
        var info = new ProcessStartInfo(Cli!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = _root,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        return Process.Start(info)!;
    }

    private static async Task<string> FinishAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return $"exit={process.ExitCode}\n{await output}\n{await error}";
    }

    [Fact]
    public async Task Receives_text_from_original()
    {
        if (Cli is null)
        {
            return;
        }
        var code = RandomCode();
        using var cli = StartCli("send", "--code", code, "--text", "Hallo von wormhole");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await Task.Delay(1500, timeout.Token); // Original zuerst am Server anmelden lassen

        await using var session = await WormholeSession.JoinAsync(NetConnector.From(Settings), Settings.MailboxUrl, code, timeout.Token);
        await session.EstablishAsync(timeout.Token);
        var offer = await session.ReceiveAsync(timeout.Token);
        await session.SendAsync(new JsonObject { ["answer"] = new JsonObject { ["message_ack"] = "ok" } }, timeout.Token);

        Assert.Equal("Hallo von wormhole", offer["offer"]?["message"]?.GetValue<string>());
        var log = await FinishAsync(cli);
        Assert.True(cli.ExitCode == 0, log);
    }

    private string Source(string name, int bytes)
    {
        var path = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[bytes];
        new Random(bytes).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static void SameContent(string expected, string actual) =>
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(expected))),
                     Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(actual))));

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // nur Relay: prüft den Relay-Handschlag
    public async Task Original_receives_file_from_brim(bool direct)
    {
        if (Cli is null)
        {
            return;
        }
        var file = Source("bild.bin", 3_000_000);
        var output = Path.Combine(_root, "out", "bild.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Process? cli = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var transfer = new WormholeTransfer(Settings) { UseDirect = direct };

        await transfer.SendAsync([file], code =>
        {
            cli = direct
                ? StartCli("receive", "--accept-file", "-o", output, code)
                : StartCli("receive", "--accept-file", "--no-listen", "-o", output, code);
        }, null, timeout.Token);

        var log = await FinishAsync(cli!);
        Assert.True(cli!.ExitCode == 0, log);
        SameContent(file, output);
    }

    [Fact]
    public async Task Original_receives_folder_from_brim()
    {
        if (Cli is null)
        {
            return;
        }
        var a = Source("Projekt/a.txt", 1000);
        var b = Source("Projekt/unter/b.bin", 200_000);
        var output = Path.Combine(_root, "out", "Projekt");
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        Process? cli = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        await new WormholeTransfer(Settings).SendAsync([Path.Combine(_root, "src", "Projekt")],
            code => cli = StartCli("receive", "--accept-file", "-o", output, code), null, timeout.Token);

        var log = await FinishAsync(cli!);
        Assert.True(cli!.ExitCode == 0, log);
        SameContent(a, Path.Combine(output, "a.txt"));
        SameContent(b, Path.Combine(output, "unter", "b.bin"));
        // Rechte kommen mit: das Original entpackt beschreibbar, nicht schreibgeschützt.
        Assert.False(new FileInfo(Path.Combine(output, "a.txt")).IsReadOnly);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Brim_receives_file_from_original(bool direct)
    {
        if (Cli is null)
        {
            return;
        }
        var file = Source("brief.pdf", 1_500_000);
        var code = RandomCode();
        using var cli = direct
            ? StartCli("send", "--code", code, file)
            : StartCli("send", "--no-listen", "--code", code, file);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await Task.Delay(1500, timeout.Token);
        var inbox = Directory.CreateDirectory(Path.Combine(_root, "inbox")).FullName;
        WormholeOffer? seen = null;

        var received = await new WormholeTransfer(Settings) { UseDirect = direct }.ReceiveAsync(code,
            (offer, _) => { seen = offer; return Task.FromResult(true); },
            name => Path.Combine(inbox, name), null, timeout.Token);

        Assert.Equal("brief.pdf", seen?.Name);
        SameContent(file, Assert.Single(received));
        var log = await FinishAsync(cli);
        Assert.True(cli.ExitCode == 0, log);
    }

    [Fact]
    public async Task Brim_receives_folder_from_original()
    {
        if (Cli is null)
        {
            return;
        }
        var a = Source("Fotos/1.jpg", 400_000);
        var b = Source("Fotos/Urlaub/2.jpg", 300_000);
        var code = RandomCode();
        using var cli = StartCli("send", "--code", code, Path.Combine(_root, "src", "Fotos"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await Task.Delay(2000, timeout.Token);
        var inbox = Directory.CreateDirectory(Path.Combine(_root, "inbox")).FullName;

        var received = await new WormholeTransfer(Settings).ReceiveAsync(code,
            (_, _) => Task.FromResult(true), name => Path.Combine(inbox, name), null, timeout.Token);

        var folder = Assert.Single(received);
        Assert.Equal("Fotos", Path.GetFileName(folder));
        SameContent(a, Path.Combine(folder, "1.jpg"));
        SameContent(b, Path.Combine(folder, "Urlaub", "2.jpg"));
        await FinishAsync(cli);
    }

    [Fact]
    public async Task Declining_tells_the_original()
    {
        if (Cli is null)
        {
            return;
        }
        var file = Source("nein.txt", 100);
        var code = RandomCode();
        using var cli = StartCli("send", "--code", code, file);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await Task.Delay(1500, timeout.Token);

        var received = await new WormholeTransfer(Settings).ReceiveAsync(code,
            (_, _) => Task.FromResult(false), name => Path.Combine(_root, name), null, timeout.Token);

        Assert.Empty(received);
        var log = await FinishAsync(cli);
        Assert.NotEqual(0, cli.ExitCode);
        Assert.Contains("rejected", log);
    }

    [Fact]
    public async Task Brim_to_brim_over_public_relay()
    {
        if (Cli is null)
        {
            return;
        }
        var file = Source("zwischen-brims.bin", 2_000_000);
        var inbox = Directory.CreateDirectory(Path.Combine(_root, "inbox")).FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var codeReady = new TaskCompletionSource<string>();

        var send = new WormholeTransfer(Settings) { UseDirect = false }.SendAsync([file], codeReady.SetResult, null, timeout.Token);
        var code = await codeReady.Task.WaitAsync(timeout.Token);
        var received = await new WormholeTransfer(Settings) { UseDirect = false }.ReceiveAsync(code,
            (_, _) => Task.FromResult(true), name => Path.Combine(inbox, name), null, timeout.Token);
        await send;

        SameContent(file, Assert.Single(received));
    }

    [Fact]
    public async Task Wrong_code_is_reported()
    {
        if (Cli is null)
        {
            return;
        }
        var file = Source("x.txt", 10);
        var code = RandomCode();
        using var cli = StartCli("send", "--code", code, file);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await Task.Delay(1500, timeout.Token);
        var wrong = code.Split('-')[0] + "-falsch-geraten";

        await Assert.ThrowsAsync<WormholeWrongCodeException>(() => new WormholeTransfer(Settings).ReceiveAsync(wrong,
            (_, _) => Task.FromResult(true), name => Path.Combine(_root, name), null, timeout.Token));

        cli.Kill();
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, System.IO.FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
