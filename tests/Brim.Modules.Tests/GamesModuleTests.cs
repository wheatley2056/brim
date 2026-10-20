using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Modules;
using Brim.Modules.Sensors;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Modules.Tests;

/// <summary>Messdienst im selben Prozess: verbindet sich über die echte Pipe und schickt, was der Test will.</summary>
internal sealed class FakeTelemetry : ITelemetryLauncher
{
    public JsonRpcConnection? Connection { get; private set; }
    public readonly TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly List<int> Watched = [];
    public bool PermissionRequested;

    public ITelemetryProcess Start(IReadOnlyDictionary<string, string> environment)
    {
        var pipe = environment[PluginProtocol.PipeVariable];
        _ = Task.Run(async () =>
        {
            var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(5000);
            Connection = new JsonRpcConnection(stream)
            {
                NotificationHandler = (method, p) =>
                {
                    if (method == TelemetryProtocol.Watch)
                    {
                        lock (Watched)
                        {
                            Watched.Add(p!.Value.Deserialize<WatchParams>(PluginJson.Options)!.Pid);
                        }
                    }
                },
            };
            Connection.Start();
            Connected.TrySetResult();
        });
        return new Process();
    }

    public Task<bool> RequestPermissionAsync()
    {
        PermissionRequested = true;
        return Task.FromResult(true);
    }

    public Task Send(string method, object parameters) => Connection!.NotifyAsync(method, parameters);

    private sealed class Process : ITelemetryProcess
    {
        public event Action? Exited { add { } remove { } }
        public bool HasExited => true;
        public void Kill() { }
        public void Dispose() { }
    }
}

public sealed class GamesModuleTests : IAsyncDisposable
{
    private sealed class FakeForeground : IForegroundSource
    {
        public ForegroundApp? App { get; set; }
        public ForegroundApp? Read() => App;
    }

    private static readonly FrameStats Smooth = new(144, 120, 6.9, [6.9, 7.0, 6.8]);

    private readonly FakeTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly FakeTelemetry _telemetry = new();
    private readonly FakeForeground _foreground = new();
    private readonly BrimSettings _settings = new() { Games = { Enabled = true } };
    private readonly GamesModule _module;

    public GamesModuleTests()
    {
        _hub = new ActivityHub(_time);
        _module = new GamesModule(_telemetry, _foreground, _ => Task.FromResult<long?>(23));
    }

    private async Task Start()
    {
        await _module.StartAsync(new ModuleContext(GamesModule.ModuleId, _hub, _settings), CancellationToken.None);
        await _telemetry.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
    }

    private static async Task Until(Func<bool> condition)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > 5000)
            {
                throw new TimeoutException();
            }
            await Task.Delay(10);
        }
    }

    private Activity? Game => _hub.Snapshot().FirstOrDefault(a => a.Key == "games/game")?.Activity;

    private async Task PlayAt(int pid, bool fullscreen = true)
    {
        _foreground.App = new ForegroundApp(pid, "Cyberpunk 2077", fullscreen);
        _time.Advance(GamesModule.PollInterval);
        await Until(() => { lock (_telemetry.Watched) { return _telemetry.Watched.Contains(pid); } });
        await _telemetry.Send(TelemetryProtocol.Frames, new FramesParams(pid, Smooth));
    }

    [Fact]
    public async Task Off_by_default_starts_nothing()
    {
        _settings.Games.Enabled = false;
        await _module.StartAsync(new ModuleContext(GamesModule.ModuleId, _hub, _settings), CancellationToken.None);

        Assert.False(_telemetry.Connected.Task.IsCompleted);
    }

    [Fact]
    public async Task Fullscreen_game_with_frames_shows_fps_in_the_background()
    {
        _settings.Games.PingHost = "game.example";
        await Start();
        await PlayAt(4242);

        await Until(() => Game is not null);
        Assert.True(Game!.IsBackground);
        Assert.False(Game.IsOverlay);
        Assert.Equal("144 FPS · 6,9 ms", GamesModule.Summary(Smooth, null, CultureInfo.GetCultureInfo("de-DE")));

        // Ping kommt beim nächsten Takt dazu.
        _time.Advance(GamesModule.PollInterval);
        await Until(() => Game?.Content is { } c && c.AccessibleName == GamesModule.Summary(Smooth, 23, CultureInfo.CurrentCulture));
    }

    [Fact]
    public async Task Windowed_apps_or_frames_for_another_process_are_ignored()
    {
        await Start();
        await PlayAt(100, fullscreen: false);
        await Task.Delay(100);
        Assert.Null(Game);

        await _telemetry.Send(TelemetryProtocol.Frames, new FramesParams(999, Smooth));
        await Task.Delay(100);
        Assert.Null(Game);
    }

    [Fact]
    public async Task Mini_mode_marks_the_overlay()
    {
        _settings.Games.Overlay = GameOverlay.Mini;
        await Start();
        await PlayAt(4242);

        await Until(() => Game is { IsOverlay: true });
    }

    [Fact]
    public async Task On_demand_shows_for_five_seconds()
    {
        _settings.Games.Overlay = GameOverlay.OnDemand;
        await Start();
        await PlayAt(4242);
        await Until(() => Game is not null);
        Assert.False(Game!.IsOverlay);

        _hub.RegisterActionHandler(GamesModule.ModuleId, _module.OnAction);
        _hub.Invoke(new ActionInvocation(GamesModule.ModuleId, GamesModule.PeekAction));
        Assert.True(Game!.IsOverlay);

        _time.Advance(GamesModule.PeekDuration + TimeSpan.FromMilliseconds(100));
        Assert.False(Game!.IsOverlay);
    }

    [Fact]
    public async Task Missing_rights_offer_the_setup()
    {
        await Start();
        await _telemetry.Send(TelemetryProtocol.Status, new StatusParams(TelemetryState.Denied, "Zugriff verweigert"));

        await Until(() => _hub.Snapshot().Any(a => a.Key == "games/setup"));
        _module.OnAction(new ActionInvocation(GamesModule.ModuleId, "setup"));
        await Until(() => _telemetry.PermissionRequested);
    }

    public async ValueTask DisposeAsync() => await _module.DisposeAsync();
}
