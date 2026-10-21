// Beispiel-Plugin für Brim: prüft alle 30 Sekunden, ob ein Server antwortet (TCP-Verbindung),
// zeigt Verlauf und Latenz als Widget auf „Jetzt“ und meldet sich in der Notch, wenn er ausfällt.
using System.Diagnostics;
using System.Net.Sockets;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using Brim.Sdk;

await using var brim = await BrimPlugin.ConnectAsync();
var history = new List<double>();
bool? lastUp = null;
StackWidget? card = null;
using var check = new SemaphoreSlim(1, 1);

// Inhalt nur für die Größen, in denen das Widget gerade liegt (auf „Jetzt“ oder als Vorschau in der Galerie).
brim.WidgetsPlaced += placed => _ = PublishWidgetAsync();

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

        card = TileCard.Card(
            TileCard.Header(Glyphs.Dot, color, server, status),
            new StackWidget(
                new ChartWidget(history) { Capacity = 30, Color = color, Height = 34, AccessibleName = "Latenz der letzten Prüfungen" },
                new StackWidget(
                    new TextWidget(error ?? $"geprüft um {DateTime.Now:HH:mm:ss}") { Style = TextStyle.Caption, Grow = 1 },
                    new ButtonWidget("check") { Kind = ButtonKind.Secondary, Label = "Prüfen" })
                {
                    Spacing = 8,
                })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 6,
                Align = CrossAlign.Stretch,
            });
        await PublishWidgetAsync();

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

async Task PublishWidgetAsync()
{
    if (card is not { } content)
    {
        return;
    }
    foreach (var size in brim.PlacedWidgets.GetValueOrDefault("status") ?? [])
    {
        try
        {
            await brim.SetWidgetAsync("status", size, content);
        }
        catch (Exception ex) when (ex is IOException or JsonRpcException or TimeoutException)
        {
            Console.Error.WriteLine($"Brim nicht erreichbar: {ex.Message}");
        }
    }
}

static (string Host, int Port) Split(string server)
{
    var colon = server.LastIndexOf(':');
    return colon > 0 && int.TryParse(server[(colon + 1)..], out var port)
        ? (server[..colon], port)
        : (server, 443);
}
