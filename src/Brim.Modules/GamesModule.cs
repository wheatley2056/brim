using System.Globalization;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using Brim.Modules.Sensors;

namespace Brim.Modules;

/// <summary>Der laufende Messdienst.</summary>
public interface ITelemetryProcess : IDisposable
{
    event Action? Exited;
    bool HasExited { get; }
    void Kill();
}

/// <summary>Startet den Messdienst und richtet einmalig die Rechte ein (in der App: <c>Brim.exe --telemetry</c>).</summary>
public interface ITelemetryLauncher
{
    ITelemetryProcess Start(IReadOnlyDictionary<string, string> environment);

    /// <summary>Gruppe „Leistungsprotokollbenutzer“ einrichten (Rückfrage der Benutzerkontensteuerung).</summary>
    Task<bool> RequestPermissionAsync();
}

/// <summary>
/// FPS und Latenz in Spielen (A8). Der Messdienst zählt die Presents des Vordergrundprozesses; das Modul
/// entscheidet, ob ein Spiel läuft (Vordergrund füllt den Monitor und gibt Bilder aus), und zeigt die
/// Werte im Tab „Jetzt“ und – je nach Spielmodus – als Mini-Pille über dem Spiel.
/// </summary>
public sealed class GamesModule : IBrimModule
{
    public const string ModuleId = "games";

    /// <summary>Aktion für Win+Alt+F: im Modus „Bei Bedarf“ FPS für einige Sekunden zeigen.</summary>
    public const string PeekAction = "peek";

    public static readonly TimeSpan PeekDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);
    private const int MaxRestarts = 3;

    private readonly ITelemetryLauncher _launcher;
    private readonly IForegroundSource _foreground;
    private readonly Func<string, Task<long?>> _ping;
    private readonly Lock _gate = new();
    private ModuleContext? _context;
    private ITimer? _poll;
    private ITimer? _peekTimer;
    private ITelemetryProcess? _process;
    private JsonRpcConnection? _connection;
    private ForegroundApp? _app;
    private int _watched;
    private FrameStats? _stats;
    private long? _pingMs;
    private DateTimeOffset _lastPing;
    private DateTimeOffset _peekUntil;
    private TelemetryState? _state;
    private string? _setupNote;
    private int _restarts;
    private bool _disposed;
    private int _pinging;

    public GamesModule(ITelemetryLauncher launcher) : this(launcher, new ForegroundSource(), PingAsync)
    {
    }

    public GamesModule(ITelemetryLauncher launcher, IForegroundSource foreground, Func<string, Task<long?>> ping)
    {
        _launcher = launcher;
        _foreground = foreground;
        _ping = ping;
    }

    public string Id => ModuleId;
    public string DisplayName => "Spiele";

    private GameSettings Settings => _context!.Settings.Games;

    /// <summary>Läuft gerade ein Spiel mit gemessener Bildrate?</summary>
    public bool GameActive
    {
        get
        {
            lock (_gate)
            {
                return IsActive();
            }
        }
    }

    private bool IsActive() => _stats is { Fps: > 0 } && _app is { Fullscreen: true } && _app.Pid == _watched;

    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        if (!Settings.Enabled)
        {
            Log.Info("FPS-Messung ist aus (Games.Enabled)");
            return Task.CompletedTask;
        }
        _ = StartHelperAsync();
        _poll = context.Time.CreateTimer(_ => Poll(), null, PollInterval, PollInterval);
        return Task.CompletedTask;
    }

    // ───────── Messdienst ─────────

    private async Task StartHelperAsync()
    {
        var pipeName = "brim-t-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var process = _launcher.Start(new Dictionary<string, string> { [PluginProtocol.PipeVariable] = pipeName });
            lock (_gate)
            {
                _process = process;
            }
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await server.WaitForConnectionAsync(timeout.Token);
            }
            var connection = new JsonRpcConnection(server) { NotificationHandler = OnHelperMessage };
            lock (_gate)
            {
                _connection = connection;
                _watched = 0;
            }
            connection.Start();
            await connection.Completion.ContinueWith(_ => { }, TaskScheduler.Default);
            Log.Warn("Messdienst beendet");
        }
        catch (Exception ex)
        {
            Log.Warn($"Messdienst startet nicht: {ex.Message}");
            await server.DisposeAsync();
        }

        ITelemetryProcess? old;
        lock (_gate)
        {
            old = _process;
            _process = null;
            _connection = null;
            _stats = null;
            if (_disposed || _state == TelemetryState.Denied || ++_restarts > MaxRestarts)
            {
                old?.Dispose();
                Publish();
                return;
            }
        }
        if (old is { HasExited: false })
        {
            old.Kill();
        }
        old?.Dispose();
        Publish();
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (!_disposed)
        {
            await StartHelperAsync();
        }
    }

    private void OnHelperMessage(string method, JsonElement? parameters)
    {
        switch (method)
        {
            case TelemetryProtocol.Frames when parameters?.Deserialize<FramesParams>(PluginJson.Options) is { } frames:
                lock (_gate)
                {
                    if (frames.Pid != _watched)
                    {
                        return;
                    }
                    _stats = frames.Stats;
                }
                Publish();
                break;
            case TelemetryProtocol.Status when parameters?.Deserialize<StatusParams>(PluginJson.Options) is { } status:
                Log.Info($"Messdienst: {status.State} {status.Message}");
                lock (_gate)
                {
                    _state = status.State;
                }
                Publish();
                break;
        }
    }

    /// <summary>Jede Sekunde: Vordergrund prüfen, dem Dienst den Prozess nennen, ggf. pingen.</summary>
    private void Poll()
    {
        var app = _foreground.Read();
        JsonRpcConnection? connection = null;
        var pid = app?.Pid ?? 0;
        bool active;
        lock (_gate)
        {
            _app = app;
            if (pid != _watched)
            {
                _watched = pid;
                _stats = null;
                connection = _connection;
            }
            active = IsActive();
        }
        if (connection is not null)
        {
            _ = connection.NotifyAsync(TelemetryProtocol.Watch, new WatchParams(pid)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        if (active && Settings.PingHost is { Length: > 0 } host && _context!.Time.GetUtcNow() - _lastPing >= PingInterval)
        {
            _lastPing = _context.Time.GetUtcNow();
            _ = PingOnceAsync(host);
        }
        Publish();
    }

    private async Task PingOnceAsync(string host)
    {
        if (Interlocked.Exchange(ref _pinging, 1) == 1)
        {
            return;
        }
        try
        {
            var ms = await _ping(host);
            lock (_gate)
            {
                _pingMs = ms;
            }
            Publish();
        }
        finally
        {
            Volatile.Write(ref _pinging, 0);
        }
    }

    private static async Task<long?> PingAsync(string host)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 1000);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception ex) when (ex is PingException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // ───────── Darstellung ─────────

    /// <summary>„144 FPS · 23 ms“ – mit Ping, sonst mit der Frametime.</summary>
    public static string Summary(FrameStats stats, long? pingMs, CultureInfo culture) =>
        $"{Math.Round(stats.Fps).ToString("0", culture)} FPS · " +
        (pingMs is { } ping ? $"{ping} ms" : stats.FrameTimeMs.ToString("0.0", culture) + " ms");

    private void Publish()
    {
        if (_context is not { } context)
        {
            return;
        }
        FrameStats? stats;
        ForegroundApp? app;
        long? ping;
        bool active;
        TelemetryState? state;
        lock (_gate)
        {
            stats = _stats;
            app = _app;
            ping = _pingMs;
            active = IsActive();
            state = _state;
        }

        if (state == TelemetryState.Denied)
        {
            PublishSetup(context);
        }
        else
        {
            context.End("setup");
        }

        if (!active || stats is null)
        {
            context.End("game");
            return;
        }
        var culture = CultureInfo.CurrentCulture;
        var overlay = Settings.Overlay switch
        {
            GameOverlay.Mini => true,
            GameOverlay.OnDemand => context.Time.GetUtcNow() < _peekUntil,
            _ => false,
        };
        var summary = Summary(stats, ping, culture);
        context.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "game",
            Priority = ActivityPriority.Ongoing,
            // Ein Spiel soll die Notch nicht belegen; die Werte stehen im Tab „Jetzt“ und in der Mini-Pille.
            IsBackground = true,
            IsOverlay = overlay,
            Rank = 20,
            Content = new StackWidget(new TextWidget(summary) { Style = TextStyle.MonoCaption, FontSize = 11, Color = BrimColors.Fog })
            {
                Justify = Justify.Center,
                AccessibleName = summary,
            },
            Expanded = ExpandedView(app!, stats, ping, culture),
        });
    }

    private static Widget ExpandedView(ForegroundApp app, FrameStats stats, long? ping, CultureInfo culture)
    {
        var details = new List<Widget>
        {
            new TextWidget($"1 % Low {Math.Round(stats.OnePercentLow).ToString("0", culture)}") { Style = TextStyle.Secondary },
            new TextWidget($"Frametime {stats.FrameTimeMs.ToString("0.0", culture)} ms") { Style = TextStyle.Secondary },
        };
        if (ping is { } p)
        {
            details.Add(new TextWidget($"Ping {p} ms") { Style = TextStyle.Secondary });
        }
        return new StackWidget(
            new TextWidget(app.Process.Length > 0 ? app.Process : "Spiel") { Style = TextStyle.Title, FontSize = 15 },
            new StackWidget(
                new TextWidget(Math.Round(stats.Fps).ToString("0", culture)) { Style = TextStyle.Display, FontSize = 34 },
                new TextWidget("FPS") { Style = TextStyle.Secondary },
                new SpacerWidget { Grow = 1 },
                new StackWidget(details) { Orientation = StackOrientation.Vertical, Spacing = 2, Align = CrossAlign.End })
            {
                Spacing = 8,
                Align = CrossAlign.End,
            },
            new ChartWidget(stats.FrameTimes)
            {
                Capacity = FrameCounter.HistoryLength,
                Height = 44,
                Color = BrimColors.Cpu,
                AccessibleName = "Frametime-Verlauf",
            })
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 8,
            Align = CrossAlign.Stretch,
        };
    }

    private void PublishSetup(ModuleContext context) => context.Post(new Activity
    {
        ModuleId = ModuleId,
        Id = "setup",
        Priority = ActivityPriority.Ongoing,
        IsBackground = true,
        Rank = 15,
        Expanded = new StackWidget(
            new TextWidget("FPS-Messung einrichten") { Style = TextStyle.Title, FontSize = 15 },
            new TextWidget(_setupNote ?? "Brim liest die Bildrate wie PresentMon über Windows-Ereignisse. Dafür muss dein Konto einmalig in die Gruppe „Leistungsprotokollbenutzer“ – Windows fragt dazu nach Adminrechten.")
            {
                Style = TextStyle.Secondary,
                Wrap = true,
            },
            new SpacerWidget { Grow = 1 },
            new StackWidget(new ButtonWidget("setup") { Kind = ButtonKind.Secondary, Label = "Einrichten" }) { Justify = Justify.End })
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 6,
            Align = CrossAlign.Stretch,
        },
    });

    public void OnAction(ActionInvocation invocation)
    {
        switch (invocation.ActionId)
        {
            case PeekAction when Settings.Overlay == GameOverlay.OnDemand:
                _peekUntil = _context!.Time.GetUtcNow() + PeekDuration;
                _peekTimer?.Dispose();
                _peekTimer = _context.Time.CreateTimer(_ => Publish(), null, PeekDuration + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
                Publish();
                break;
            case "setup":
                _ = SetupAsync();
                break;
        }
    }

    private async Task SetupAsync()
    {
        var ok = await _launcher.RequestPermissionAsync();
        _setupNote = ok
            ? "Fertig. Melde dich einmal von Windows ab und wieder an, dann misst Brim die Bildrate."
            : "Das hat nicht geklappt. Ohne Adminrechte kann Brim die Gruppe nicht setzen.";
        Publish();
    }

    public async ValueTask DisposeAsync()
    {
        JsonRpcConnection? connection;
        ITelemetryProcess? process;
        lock (_gate)
        {
            _disposed = true;
            connection = _connection;
            process = _process;
        }
        _poll?.Dispose();
        _peekTimer?.Dispose();
        if (connection is not null)
        {
            try
            {
                await connection.NotifyAsync(PluginProtocol.Shutdown, null).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
            {
            }
        }
        if (process is not null)
        {
            var exited = new TaskCompletionSource();
            process.Exited += () => exited.TrySetResult();
            if (!process.HasExited)
            {
                await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            }
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }
}
