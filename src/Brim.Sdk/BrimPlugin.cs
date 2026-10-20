using System.IO.Pipes;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.Sdk;

/// <summary>
/// Verbindung eines Plugins zu Brim. Brim startet das Plugin und gibt den Namen der Pipe in
/// <c>BRIM_PIPE</c> mit; <see cref="ConnectAsync(CancellationToken)"/> verbindet sich und sagt „hello“.
/// </summary>
/// <example>
/// <code>
/// await using var brim = await BrimPlugin.ConnectAsync();
/// brim.ActionInvoked += (action, _) => Console.WriteLine(action);
/// await brim.SetViewAsync("main", new TextWidget("Hallo aus dem Plugin"));
/// await brim.RunAsync();
/// </code>
/// </example>
public sealed class BrimPlugin : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly JsonRpcConnection _connection;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private BrimPlugin(JsonRpcConnection connection)
    {
        _connection = connection;
        _connection.NotificationHandler = OnNotification;
    }

    /// <summary>Kennung aus dem Manifest.</summary>
    public string Id { get; private set; } = "";

    /// <summary>Einstellungen aus dem Manifest mit den Werten des Nutzers.</summary>
    public IReadOnlyDictionary<string, JsonElement> Settings { get; private set; } = new Dictionary<string, JsonElement>();

    /// <summary>Rechte, denen der Nutzer zugestimmt hat.</summary>
    public IReadOnlyList<string> Permissions { get; private set; } = [];

    /// <summary>Ein Knopf, Schalter oder Regler aus einem Inhalt dieses Plugins wurde benutzt.</summary>
    public event Action<string, double?>? ActionInvoked;

    /// <summary>Dateien aus der Ablage für eine Ablage-Aktion (Recht „shelf“).</summary>
    public event Action<string, IReadOnlyList<string>>? ShelfItems;

    public event Action<IReadOnlyDictionary<string, JsonElement>>? SettingsChanged;

    /// <summary>Tab „Plugins“ ist sichtbar geworden oder nicht mehr zu sehen; teure Abfragen danach richten.</summary>
    public event Action<bool>? TabVisibilityChanged;

    /// <summary>Die Notch wurde auf- oder zugeklappt; Live-Werte (z. B. eine Wellenform) nur aufgeklappt senden.</summary>
    public event Action<bool>? NotchExpandedChanged;

    /// <summary>Windows-Benachrichtigung einer App aus <c>notificationApps</c> im Manifest (Recht „notifications“).</summary>
    public event Action<NotificationParams>? NotificationPosted;

    /// <summary>
    /// Welche Widgets auf „Jetzt“ liegen (oder in der Galerie zu sehen sind), je Kennung die Größen. Nur für diese
    /// <see cref="SetWidgetAsync"/> schicken; leer = gerade keins.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, IReadOnlyList<TileSize>>>? WidgetsPlaced;

    public IReadOnlyDictionary<string, IReadOnlyList<TileSize>> PlacedWidgets { get; private set; } = new Dictionary<string, IReadOnlyList<TileSize>>();

    /// <summary>Ist die Notch gerade aufgeklappt?</summary>
    public bool NotchExpanded { get; private set; }

    /// <summary>Endet, wenn Brim das Plugin beendet oder die Verbindung abbricht.</summary>
    public Task Completion => Task.WhenAny(_shutdown.Task, _connection.Completion).Unwrap();

    /// <summary>Verbindet sich mit Brim über die Pipe aus <c>BRIM_PIPE</c>.</summary>
    public static Task<BrimPlugin> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var pipe = Environment.GetEnvironmentVariable(PluginProtocol.PipeVariable)
            ?? throw new InvalidOperationException($"{PluginProtocol.PipeVariable} fehlt. Plugins werden von Brim gestartet, nicht direkt.");
        return ConnectAsync(pipe, cancellationToken);
    }

    public static async Task<BrimPlugin> ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync((int)RequestTimeout.TotalMilliseconds, cancellationToken);
        return await ConnectAsync(stream, cancellationToken);
    }

    /// <summary>Über einen beliebigen Datenstrom (Tests).</summary>
    public static async Task<BrimPlugin> ConnectAsync(Stream stream, CancellationToken cancellationToken)
    {
        var plugin = new BrimPlugin(new JsonRpcConnection(stream));
        plugin._connection.Start();
        var hello = await plugin._connection.InvokeAsync<HelloResult>(PluginProtocol.Hello, new HelloParams(), RequestTimeout, cancellationToken)
            ?? throw new IOException("Brim hat „hello“ nicht beantwortet");
        plugin.Id = hello.Id;
        plugin.Settings = hello.Settings;
        plugin.Permissions = hello.Permissions;
        return plugin;
    }

    /// <summary>Wartet, bis Brim das Plugin beendet.</summary>
    public Task RunAsync(CancellationToken cancellationToken = default) => Completion.WaitAsync(cancellationToken);

    /// <summary>Wert einer Einstellung oder <paramref name="fallback"/>.</summary>
    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    // ───────── Aktivitäten, HUD, Tab ─────────

    /// <summary>Startet oder aktualisiert eine Aktivität (kompakte Notch, Perle, Tab „Jetzt“).</summary>
    public Task SetActivityAsync(ActivityParams activity, CancellationToken cancellationToken = default) =>
        CallAsync(PluginProtocol.ActivityUpdate, activity, cancellationToken);

    public Task EndActivityAsync(string id, CancellationToken cancellationToken = default) =>
        CallAsync(PluginProtocol.ActivityEnd, new IdParams(id), cancellationToken);

    /// <summary>Kurze Meldung, z. B. Symbol, Text und ein Wert als Leiste.</summary>
    public Task ShowHudAsync(string? glyph, string text, double? value = null, CancellationToken cancellationToken = default) =>
        CallAsync(PluginProtocol.HudShow, new HudParams { Glyph = glyph, Text = text, Value = value }, cancellationToken);

    public Task ShowHudAsync(HudParams hud, CancellationToken cancellationToken = default) =>
        CallAsync(PluginProtocol.HudShow, hud, cancellationToken);

    /// <summary>Setzt eine Karte im Tab „Plugins“.</summary>
    public Task SetViewAsync(string id, Widget content, int order = 0, CancellationToken cancellationToken = default) =>
        CallAsync(PluginProtocol.ViewSet, new ViewParams { Id = id, Content = content, Order = order }, cancellationToken);

    /// <summary>Inhalt eines Widgets (aus <c>widgets</c> im Manifest) in einer Größe, siehe <see cref="PlacedWidgets"/>.</summary>
    public Task SetWidgetAsync(string id, TileSize size, Widget content, CancellationToken cancellationToken = default) =>
        CallAsync(PluginProtocol.WidgetSet, new WidgetSetParams { Id = id, Size = size, Content = content }, cancellationToken);

    public Task ClearViewAsync(string id, CancellationToken cancellationToken = default) =>
        CallAsync(PluginProtocol.ViewClear, new IdParams(id), cancellationToken);

    /// <summary>Schreibt ins Protokoll von Brim (Stufe „info“, „warn“ oder „error“).</summary>
    public Task LogAsync(string message, string level = "info", CancellationToken cancellationToken = default) =>
        CallAsync(PluginProtocol.LogMessage, new LogParams(message) { Level = level }, cancellationToken);

    private Task CallAsync(string method, object parameters, CancellationToken cancellationToken) =>
        _connection.InvokeAsync<JsonElement>(method, parameters, RequestTimeout, cancellationToken);

    private void OnNotification(string method, JsonElement? parameters)
    {
        switch (method)
        {
            case PluginProtocol.ActionInvoked when parameters?.Deserialize<ActionParams>(PluginJson.Options) is { } action:
                ActionInvoked?.Invoke(action.ActionId, action.Value);
                break;
            case PluginProtocol.ShelfItems when parameters?.Deserialize<ShelfItemsParams>(PluginJson.Options) is { } items:
                ShelfItems?.Invoke(items.ActionId, items.Paths);
                break;
            case PluginProtocol.SettingsChanged when parameters?.Deserialize<SettingsParams>(PluginJson.Options) is { } settings:
                Settings = settings.Settings;
                SettingsChanged?.Invoke(settings.Settings);
                break;
            case PluginProtocol.TabVisibility when parameters?.Deserialize<VisibilityParams>(PluginJson.Options) is { } visibility:
                TabVisibilityChanged?.Invoke(visibility.Visible);
                break;
            case PluginProtocol.NotchExpanded when parameters?.Deserialize<ExpandedParams>(PluginJson.Options) is { } expanded:
                NotchExpanded = expanded.Expanded;
                NotchExpandedChanged?.Invoke(expanded.Expanded);
                break;
            case PluginProtocol.NotificationPosted when parameters?.Deserialize<NotificationParams>(PluginJson.Options) is { } notification:
                NotificationPosted?.Invoke(notification);
                break;
            case PluginProtocol.WidgetsPlaced when parameters?.Deserialize<WidgetsPlacedParams>(PluginJson.Options) is { } placed:
                PlacedWidgets = placed.Widgets.ToDictionary(w => w.Id, w => w.Sizes);
                WidgetsPlaced?.Invoke(PlacedWidgets);
                break;
            case PluginProtocol.Shutdown:
                _shutdown.TrySetResult();
                break;
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
