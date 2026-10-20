// Beispiel-Plugin für Brim: prüft alle 30 Sekunden, ob ein Server antwortet (TCP-Verbindung),
// zeigt Verlauf und Latenz im Tab „Plugins“ und meldet sich in der Notch, wenn er ausfällt.
using System.Diagnostics;
using System.Net.Sockets;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using Brim.Sdk;

await using var brim = await BrimPlugin.ConnectAsync();
var history = new List<double>();
bool? lastUp = null;
using var check = new SemaphoreSlim(1, 1);

brim.ActionInvoked += (action, value) =>
{
    if (action == "check")
    {
        _ = CheckAsync();
    }
};
brim.SettingsChanged += settings =>
{
    history.Clear();
    _ = CheckAsync();
};

using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
_ = Task.Run(async () =>
{
    do
    {
        await CheckAsync();
    }
    while (await timer.WaitForNextTickAsync());
});
await brim.RunAsync();

async Task CheckAsync()
{
    await check.WaitAsync();
    try
    {
        var server = brim.Setting("server", "github.com:443");
        var (host, port) = Split(server);
        double? latency = null;
        string? error = null;
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var watch = Stopwatch.StartNew();
            await client.ConnectAsync(host, port, timeout.Token);
            latency = watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            error = ex is OperationCanceledException ? "keine Antwort" : ex.Message;
        }

        history.Add(latency ?? 0);
        if (history.Count > 30)
        {
            history.RemoveAt(0);
        }
        var up = latency is not null;
        var color = up ? BrimColors.Charge : BrimColors.Danger;
        var status = up ? $"{latency:0} ms" : "nicht erreichbar";

        await brim.SetViewAsync("status", new StackWidget(
            new StackWidget(
                new IconWidget(Glyphs.Dot) { Size = 10, Color = color },
                new TextWidget(server) { Grow = 1 },
                new TextWidget(status) { Style = TextStyle.Mono, FontSize = 12, Color = color })
            {
                Spacing = 8,
            },
            new ChartWidget(history) { Capacity = 30, Color = color, Height = 34, AccessibleName = "Latenz der letzten Prüfungen" },
            new StackWidget(
                new TextWidget(error ?? $"geprüft um {DateTime.Now:HH:mm:ss}") { Style = TextStyle.Caption, Grow = 1 },
                new ButtonWidget("check") { Kind = ButtonKind.Secondary, Label = "Jetzt prüfen" })
            {
                Spacing = 8,
            })
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 10,
            Align = CrossAlign.Stretch,
            Background = BrimColors.Graphite,
            CornerRadius = 14,
            Padding = (12, 10, 12, 10),
        });

        // Läuft alles, bleibt es im Hintergrund; fällt der Server aus, zeigt die Notch es kompakt.
        await brim.SetActivityAsync(new ActivityParams
        {
            Id = "status",
            Background = up,
            Rank = 5,
            Leading = new IconWidget(Glyphs.Dot) { Size = 10, Color = color },
            Trailing = new TextWidget(up ? status : "Server weg") { Style = TextStyle.Caption, FontSize = 12, Color = color },
            Bead = new IconWidget(Glyphs.Dot) { Size = 8, Color = color },
            Announcement = up ? null : $"{server} ist nicht erreichbar",
        });
        if (lastUp != up && lastUp is not null)
        {
            await brim.ShowHudAsync(Glyphs.Dot, up ? $"{host} ist wieder da" : $"{host} ist nicht erreichbar");
        }
        lastUp = up;
    }
    catch (Exception ex) when (ex is IOException or JsonRpcException or TimeoutException)
    {
        Console.Error.WriteLine($"Brim nicht erreichbar: {ex.Message}");
    }
    finally
    {
        check.Release();
    }
}

static (string Host, int Port) Split(string server)
{
    var colon = server.LastIndexOf(':');
    return colon > 0 && int.TryParse(server[(colon + 1)..], out var port)
        ? (server[..colon], port)
        : (server, 443);
}
