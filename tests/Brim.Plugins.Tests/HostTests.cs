using System.Collections.Concurrent;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using Brim.Plugins;
using Brim.Sdk;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Plugins.Tests;

/// <summary>Startet „Plugins“ im selben Prozess, verbunden über eine echte Named Pipe.</summary>
internal sealed class InProcessLauncher(Func<string, CancellationToken, Task> plugin) : IPluginLauncher
{
    public ConcurrentBag<FakeProcess> Started { get; } = [];

    public IPluginProcess Start(PluginManifest manifest, IReadOnlyDictionary<string, string> environment)
    {
        var process = new FakeProcess();
        Started.Add(process);
        var pipe = environment[PluginProtocol.PipeVariable];
        _ = Task.Run(async () =>
        {
            try
            {
                await plugin(pipe, process.Token);
            }
            catch (Exception)
            {
                // Wie ein abgestürzter Prozess.
            }
            process.Exit();
        });
        return process;
    }

    internal sealed class FakeProcess : IPluginProcess
    {
        private readonly CancellationTokenSource _cts = new();
        private int _exited;

        public CancellationToken Token => _cts.Token;
        public event Action? Exited;
        public bool HasExited => _exited == 1;

        public void Exit()
        {
            if (Interlocked.Exchange(ref _exited, 1) == 0)
            {
                Exited?.Invoke();
            }
        }

        public void Kill() => _cts.Cancel();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Tests, die <see cref="PluginRunner.ConnectTimeout"/> (statisch) setzen und echte Plugins starten, laufen nacheinander:
/// Sonst verkürzt ein Test die Zeit für „hello“ mitten im anderen.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PluginRunnerCollection
{
    public const string Name = "PluginRunner";
}

[Collection(PluginRunnerCollection.Name)]
public sealed class HostTests : IAsyncDisposable
{
    private const string PluginId = "de.test.demo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brim-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly ActivityHub _hub;
    private readonly FakeTimeProvider _time = new();
    private readonly BrimSettings _settings = new();
    private int _saves;
    private PluginHostModule? _host;

    public HostTests()
    {
        _hub = new ActivityHub(_time);
        PluginRunner.ConnectTimeout = TimeSpan.FromSeconds(5);
    }

    private void Install(string permissions = "[]", string widgets = "[]")
    {
        var dir = Path.Combine(_root, "demo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PluginManifest.FileName),
            $$"""{ "id": "{{PluginId}}", "name": "Demo", "entry": "demo", "permissions": {{permissions}}, "widgets": {{widgets}}, "settings": [{ "key": "greeting", "default": "Hallo" }] }""");
    }

    private async Task<PluginHostModule> StartHost(IPluginLauncher launcher, bool enabled = true, params string[] granted)
    {
        if (enabled)
        {
            _settings.Plugins[PluginId] = new PluginConfig { Enabled = true, Granted = [.. granted] };
        }
        _host = new PluginHostModule([_root], launcher, _ => _saves++);
        _hub.RegisterActionHandler(PluginHostModule.ModuleId, _host.OnAction);
        await _host.StartAsync(new ModuleContext(PluginHostModule.ModuleId, _hub, _settings), CancellationToken.None);
        return _host;
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException("Bedingung nicht erreicht");
            }
            await Task.Delay(10);
        }
    }

    private ActiveActivity? Activity(string id) => _hub.Snapshot().FirstOrDefault(a => a.Key == $"plugins/{PluginId}/{id}");

    [Fact]
    public async Task Plugin_posts_activity_and_view_and_receives_actions()
    {
        Install();
        var actions = new ConcurrentQueue<string>();
        string? greeting = null;
        var viewAccepted = false;
        var launcher = new InProcessLauncher(async (pipe, token) =>
        {
            await using var brim = await BrimPlugin.ConnectAsync(pipe, token);
            greeting = brim.Setting("greeting");
            brim.ActionInvoked += (action, _) => actions.Enqueue(action);
            await brim.SetActivityAsync(new ActivityParams
            {
                Id = "status",
                Leading = new IconWidget(Glyphs.Clock),
                Trailing = new ButtonWidget("ping") { Glyph = Glyphs.Next },
                Rank = 50,
                Glow = 1.7,
            }, token);
#pragma warning disable CS0618 // Ältere Plugins schicken noch view.set: Brim nimmt es an, zeigt es aber nicht.
            await brim.SetViewAsync("card", new TextWidget("Karte"), cancellationToken: token);
#pragma warning restore CS0618
            viewAccepted = true;
            await brim.RunAsync(token);
        });

        var host = await StartHost(launcher);
        await Until(() => Activity("status") is not null && viewAccepted);
        // Der Tab „Plugins“ gehört allein der Verwaltung.
        Assert.Equal(["manage"], _hub.Views(NotchTab.Plugins).Select(v => v.Id));

        Assert.Equal(PluginState.Running, host.StateOf(PluginId));
        Assert.Equal("Hallo", greeting);
        var status = Activity("status")!.Activity;
        Assert.Equal(9, status.Rank);
        Assert.Equal(1, status.Glow);
        var button = Assert.IsType<ButtonWidget>(status.Trailing);
        Assert.Equal($"{PluginId}|ping", button.ActionId);

        _hub.Invoke(new ActionInvocation(PluginHostModule.ModuleId, button.ActionId));
        await Until(() => actions.Contains("ping"));
    }

    [Fact]
    public async Task Critical_needs_the_alerts_permission_and_bad_widgets_are_refused()
    {
        Install();
        Exception? refused = null;
        var launcher = new InProcessLauncher(async (pipe, token) =>
        {
            await using var brim = await BrimPlugin.ConnectAsync(pipe, token);
            await brim.SetActivityAsync(new ActivityParams { Id = "alarm", Priority = ActivityPriority.Critical, Content = new TextWidget("!") }, token);
            try
            {
                await brim.SetActivityAsync(new ActivityParams { Id = "bad", Content = new TextWidget("x") { Color = "lila" } }, token);
            }
            catch (JsonRpcException ex)
            {
                refused = ex;
            }
            await brim.RunAsync(token);
        });

        await StartHost(launcher);
        await Until(() => Activity("alarm") is not null && refused is not null);

        Assert.Equal(ActivityPriority.Transient, Activity("alarm")!.Priority);
        Assert.Contains("keine Farbe", refused!.Message);
        Assert.Null(Activity("bad"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overlay_needs_the_overlay_permission_and_content(bool permitted)
    {
        Install(permitted ? """["overlay"]""" : "[]");
        Exception? refused = null;
        var launcher = new InProcessLauncher(async (pipe, token) =>
        {
            await using var brim = await BrimPlugin.ConnectAsync(pipe, token);
            await brim.SetActivityAsync(new ActivityParams { Id = "fps", Background = true, Overlay = true, Content = new TextWidget("144 FPS") }, token);
            try
            {
                await brim.SetActivityAsync(new ActivityParams { Id = "empty", Overlay = true }, token);
            }
            catch (JsonRpcException ex)
            {
                refused = ex;
            }
            await brim.RunAsync(token);
        });

        await StartHost(launcher, granted: permitted ? ["overlay"] : []);
        await Until(() => Activity("fps") is not null && refused is not null);

        Assert.Equal(permitted, Activity("fps")!.Activity.IsOverlay);
        Assert.Contains("content", refused!.Message);
        Assert.Null(Activity("empty"));
    }

    [Fact]
    public async Task Disabled_plugins_wait_until_switched_on_which_grants_permissions()
    {
        Install("""["network"]""");
        var connected = 0;
        var launcher = new InProcessLauncher(async (pipe, token) =>
        {
            await using var brim = await BrimPlugin.ConnectAsync(pipe, token);
            Interlocked.Increment(ref connected);
            Assert.Equal(["network"], brim.Permissions);
            await brim.RunAsync(token);
        });

        var host = await StartHost(launcher, enabled: false);
        await Task.Delay(100);
        Assert.Empty(launcher.Started);
        var manage = Assert.Single(_hub.Views(NotchTab.Plugins));
        // Volle Breite: Plugins zeigen dort nichts Eigenes.
        Assert.Equal(TabColumn.Main, manage.Column);

        _hub.Invoke(new ActionInvocation(PluginHostModule.ModuleId, "toggle:" + PluginId, 1));
        await Until(() => connected == 1 && host.StateOf(PluginId) == PluginState.Running);

        Assert.Equal(["network"], _settings.Plugins[PluginId].Granted);
        Assert.True(_saves > 0);

        await host.SetEnabledAsync(PluginId, false);
        Assert.Equal(PluginState.Off, host.StateOf(PluginId));
        Assert.All(launcher.Started, p => Assert.True(p.HasExited));
    }

    [Fact]
    public async Task Crashes_restart_with_backoff_and_three_switch_off()
    {
        Install();
        var launcher = new InProcessLauncher(async (pipe, token) =>
        {
            await using var brim = await BrimPlugin.ConnectAsync(pipe, token);
            await brim.SetActivityAsync(new ActivityParams { Id = "x", Leading = new TextWidget("x") }, token);
            throw new InvalidOperationException("Absturz");
        });

        var host = await StartHost(launcher);
        await Until(() => host.StateOf(PluginId) == PluginState.Restarting);
        Assert.Null(Activity("x"));

        _time.Advance(PluginRunner.Backoff[0]);
        await Until(() => launcher.Started.Count == 2 && host.StateOf(PluginId) == PluginState.Restarting);

        _time.Advance(PluginRunner.Backoff[1]);
        await Until(() => host.StateOf(PluginId) == PluginState.Failed);
        Assert.Equal(3, launcher.Started.Count);

        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(100);
        Assert.Equal(3, launcher.Started.Count);
    }

    [Fact]
    public async Task Plugin_that_never_connects_counts_as_crash()
    {
        Install();
        PluginRunner.ConnectTimeout = TimeSpan.FromMilliseconds(300);
        var launcher = new InProcessLauncher((_, token) => Task.Delay(Timeout.Infinite, token));

        var host = await StartHost(launcher);

        await Until(() => host.StateOf(PluginId) == PluginState.Restarting);
        Assert.True(launcher.Started.Single().HasExited);
    }

    [Fact]
    public async Task Notifications_and_expanded_state_reach_permitted_plugins()
    {
        var dir = Path.Combine(_root, "messenger");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PluginManifest.FileName),
            $$"""{ "id": "{{PluginId}}", "name": "Messenger", "entry": "demo", "permissions": ["notifications"], "notificationApps": ["Swyx Messenger"] }""");
        var received = new ConcurrentQueue<string>();
        var expanded = new ConcurrentQueue<bool>();
        var launcher = new InProcessLauncher(async (pipe, token) =>
        {
            await using var brim = await BrimPlugin.ConnectAsync(pipe, token);
            brim.NotificationPosted += n => received.Enqueue($"{n.Title}: {n.Body}");
            brim.NotchExpandedChanged += e => expanded.Enqueue(e);
            // Erst wenn das Plugin zuhört, sagt „läuft“ etwas aus: „hello“ kann beantwortet sein, bevor die
            // Handler oben hängen, und die erste Benachrichtigung ginge verloren.
            await brim.SetActivityAsync(new ActivityParams { Id = "ready", Leading = new TextWidget("bereit") }, token);
            await brim.RunAsync(token);
        });

        var host = await StartHost(launcher, true, "notifications");
        await Until(() => host.StateOf(PluginId) == PluginState.Running && Activity("ready") is not null);

        Assert.True(host.RouteNotification("Swyx Messenger", "Anna", "Hast du kurz Zeit?"));
        Assert.False(host.RouteNotification("Outlook", "Newsletter", "…"));
        host.OnExpandedChanged(true);

        await Until(() => received.Contains("Anna: Hast du kurz Zeit?") && expanded.Contains(true));
    }

    [Fact]
    public async Task Shelf_actions_send_the_files_to_the_plugin()
    {
        var dir = Path.Combine(_root, "upload");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PluginManifest.FileName),
            $$"""{ "id": "{{PluginId}}", "name": "Upload", "entry": "demo", "permissions": ["shelf"], "shelfActions": [{ "id": "up", "label": "Hochladen" }] }""");
        var received = new ConcurrentQueue<string>();
        var launcher = new InProcessLauncher(async (pipe, token) =>
        {
            await using var brim = await BrimPlugin.ConnectAsync(pipe, token);
            brim.ShelfItems += (action, paths) => received.Enqueue(action + ":" + string.Join(",", paths));
            await brim.SetActivityAsync(new ActivityParams { Id = "ready", Leading = new TextWidget("bereit") }, token);
            await brim.RunAsync(token);
        });

        var host = await StartHost(launcher, true, "shelf");
        await Until(() => host.StateOf(PluginId) == PluginState.Running && Activity("ready") is not null);

        var action = Assert.Single(host.ShelfActions());
        Assert.Equal("Hochladen", action.Action.Label);
        Assert.True(host.SendShelfItems(PluginId, action.Action.Id, ["C:/a.txt", "C:/b.txt"]));
        await Until(() => received.Contains("up:C:/a.txt,C:/b.txt"));
    }

    [Fact]
    public async Task Without_permission_notifications_stay_with_brim()
    {
        var dir = Path.Combine(_root, "messenger");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PluginManifest.FileName),
            $$"""{ "id": "{{PluginId}}", "name": "Messenger", "entry": "demo", "permissions": ["notifications"], "notificationApps": ["Swyx Messenger"] }""");
        var launcher = new InProcessLauncher(async (pipe, token) =>
        {
            await using var brim = await BrimPlugin.ConnectAsync(pipe, token);
            await brim.RunAsync(token);
        });

        // Eingeschaltet, aber dem neuen Recht noch nicht zugestimmt: startet nicht.
        var host = await StartHost(launcher, true);
        await Task.Delay(100);

        Assert.False(host.RouteNotification("Swyx Messenger", "Anna", "Hallo"));
    }

    [Fact]
    public async Task Broken_plugins_are_listed_not_started()
    {
        var dir = Path.Combine(_root, "kaputt");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PluginManifest.FileName), """{ "id": "X", "name": "x", "entry": "y" }""");
        var launcher = new InProcessLauncher((_, _) => Task.CompletedTask);

        var host = await StartHost(launcher, enabled: false);

        Assert.Empty(host.Installed);
        var manage = Assert.Single(_hub.Views(NotchTab.Plugins));
        Assert.Contains(WidgetTree.All(manage.Content).OfType<TextWidget>(), t => t.Text.StartsWith("Fehlerhaft"));
    }

    [Fact]
    public async Task Management_lists_widgets_and_opens_the_library()
    {
        Install(widgets: """[{ "id": "calls", "name": "Anrufe", "sizes": ["medium"] }]""");
        var launcher = new InProcessLauncher((_, _) => Task.CompletedTask);
        var host = await StartHost(launcher, enabled: false);
        var opened = 0;
        host.OpenLibrary = () => opened++;

        var manage = Assert.Single(_hub.Views(NotchTab.Plugins));
        var texts = WidgetTree.All(manage.Content).OfType<TextWidget>().Select(t => t.Text).ToList();
        Assert.Contains("Widgets: Anrufe", texts);
        var library = WidgetTree.All(manage.Content).OfType<ButtonWidget>().Single(b => b.Label == "Bibliothek");

        _hub.Invoke(new ActionInvocation(PluginHostModule.ModuleId, library.ActionId));
        Assert.Equal(1, opened);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
        PluginRunner.ConnectTimeout = TimeSpan.FromSeconds(10);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Widgets_appear_in_the_gallery_and_get_content_once_placed()
    {
        Install(widgets: """[{ "id": "calls", "name": "Anrufe", "sizes": ["medium", "large"] }]""");
        var placed = new ConcurrentQueue<IReadOnlyDictionary<string, IReadOnlyList<TileSize>>>();
        Exception? refused = null;
        var launcher = new InProcessLauncher(async (pipe, token) =>
        {
            await using var brim = await BrimPlugin.ConnectAsync(pipe, token);
            brim.WidgetsPlaced += async widgets =>
            {
                placed.Enqueue(widgets);
                try
                {
                    foreach (var size in widgets.GetValueOrDefault("calls") ?? [])
                    {
                        await brim.SetWidgetAsync("calls", size, new ButtonWidget("dial") { Label = $"Anrufe {size}" }, token);
                    }
                    await brim.SetWidgetAsync("calls", TileSize.Small, new TextWidget("zu klein"), token);
                }
                catch (JsonRpcException ex)
                {
                    refused = ex;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException)
                {
                    // Der Handler ist async void: Endet der Test, während er noch sendet, darf das den Testprozess nicht beenden.
                }
            };
            await brim.RunAsync(token);
        });

        await StartHost(launcher);
        var key = $"plugins/{PluginId}/calls";
        await Until(() => _hub.Tile(key) is not null);
        Assert.Equal("Demo", _hub.Tile(key)!.Category);
        Assert.Equal([TileSize.Medium, TileSize.Large], _hub.Tile(key)!.Sizes);

        _hub.SetTilePlacements([(key, TileSize.Large)]);
        await Until(() => _hub.TileContent(key, TileSize.Large) is not null && refused is not null);
        var button = Assert.IsType<ButtonWidget>(_hub.TileContent(key, TileSize.Large));
        Assert.Equal($"{PluginId}|dial", button.ActionId);
        Assert.Contains("Größe", refused!.Message);
        Assert.Null(_hub.TileContent(key, TileSize.Medium));

        // Plugin weg: Widget verschwindet aus der Galerie.
        await _host!.DisposeAsync();
        _host = null;
        Assert.Null(_hub.Tile(key));
    }

    [Fact]
    public void Broken_widget_definitions_are_refused()
    {
        Install(widgets: """[{ "id": "a/b", "name": "X" }]""");
        Assert.Contains("Kennung", Assert.Throws<InvalidDataException>(() => PluginManifest.Load(Path.Combine(_root, "demo"))).Message);
        Install(widgets: """[{ "id": "a", "name": "X", "sizes": [] }]""");
        Assert.Contains("Größe", Assert.Throws<InvalidDataException>(() => PluginManifest.Load(Path.Combine(_root, "demo"))).Message);
    }
}
