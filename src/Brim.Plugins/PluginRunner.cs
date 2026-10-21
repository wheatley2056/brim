using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.Plugins;

public enum PluginState
{
    /// <summary>Ausgeschaltet oder noch nicht freigegeben.</summary>
    Off,
    Starting,
    Running,
    /// <summary>Abgestürzt, startet nach einer Pause neu.</summary>
    Restarting,
    /// <summary>Nach drei Abstürzen abgeschaltet (Plan 4.2), bis der Nutzer es wieder einschaltet.</summary>
    Failed,
}

/// <summary>
/// Ein Plugin von Start bis Ende: eigener Prozess, eigene Pipe mit zufälligem Namen, JSON-RPC.
/// Alles, was das Plugin meldet, wird geprüft und unter seiner Kennung an den Hub gegeben.
/// </summary>
internal sealed class PluginRunner : IAsyncDisposable
{
    /// <summary>So lange hat ein Plugin Zeit, sich zu verbinden und „hello“ zu sagen.</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Wartezeiten vor dem 1. und 2. Neustart; der dritte Absturz schaltet ab.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];

    /// <summary>Abstürze in diesem Zeitraum zählen zusammen.</summary>
    public static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);

    public const int MaxCrashes = 3;

    /// <summary>Höchstens so viele Nachrichten pro Sekunde; darüber wird abgelehnt.</summary>
    public const int MaxMessagesPerSecond = 120;

    private readonly ModuleContext _context;
    private readonly IPluginLauncher _launcher;
    private readonly Func<PluginConfig> _config;
    private readonly Action _stateChanged;
    private readonly int _index;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _activities = [];
    private readonly HashSet<string> _views = [];
    private readonly List<DateTimeOffset> _crashes = [];
    private IPluginProcess? _process;
    private JsonRpcConnection? _connection;
    private TaskCompletionSource? _hello;
    private ITimer? _restart;
    private int _generation;
    private bool _stopping;
    private long _rateWindow;
    private int _rateCount;
    private bool _tabVisible;

    public PluginRunner(PluginManifest manifest, ModuleContext context, IPluginLauncher launcher, Func<PluginConfig> config, int index, Action stateChanged)
    {
        Manifest = manifest;
        _context = context;
        _launcher = launcher;
        _config = config;
        _index = index;
        _stateChanged = stateChanged;
    }

    public PluginManifest Manifest { get; }
    public string Id => Manifest.Id;

    // Zustand und Fehler werden von mehreren Threads gelesen (Verwaltung im Tab, Timer für den Neustart);
    // geschrieben werden sie nur unter _gate, gelesen ohne. volatile, damit niemand einen alten Stand sieht.
    private volatile PluginState _state;
    private volatile string? _detail;

    public PluginState State => _state;

    /// <summary>Letzter Fehler, für die Verwaltung im Tab „Plugins“.</summary>
    public string? Detail => _detail;

    private bool Granted(string permission) => _config().Granted.Contains(permission, StringComparer.Ordinal);

    // ───────── Start und Ende ─────────

    public async Task StartAsync()
    {
        int generation;
        lock (_gate)
        {
            if (_state is PluginState.Starting or PluginState.Running)
            {
                return;
            }
            _stopping = false;
            generation = ++_generation;
            // Schon hier „startet“ setzen, im selben Schloss wie die Prüfung: Zwei gleichzeitige Starts
            // (Timer und Nutzer) dürfen nicht beide einen Prozess hochziehen.
            _state = PluginState.Starting;
            _detail = null;
        }
        _stateChanged();

        // Zufälliger Name: Andere Programme können die Pipe nicht erraten und sich nicht als Brim ausgeben.
        var pipeName = "brim-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var process = _launcher.Start(Manifest, new Dictionary<string, string>
            {
                [PluginProtocol.PipeVariable] = pipeName,
                [PluginProtocol.IdVariable] = Id,
            });
            lock (_gate)
            {
                _process = process;
            }
            process.Exited += () => _ = OnStoppedAsync(generation, "Prozess beendet");

            using (var timeout = new CancellationTokenSource(ConnectTimeout))
            {
                await server.WaitForConnectionAsync(timeout.Token);
            }
            var connection = new JsonRpcConnection(server)
            {
                RequestHandler = HandleAsync,
            };
            var hello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _connection = connection;
                _hello = hello;
            }
            connection.Start();
            _ = connection.Completion.ContinueWith(t => OnStoppedAsync(generation,
                t.IsFaulted ? "Protokollfehler: " + t.Exception?.InnerException?.Message : "Verbindung getrennt"), TaskScheduler.Default);
            await hello.Task.WaitAsync(ConnectTimeout);
            // Nur melden, wenn dieser Start noch der aktuelle ist: Ist das Plugin zwischen „hello“ und hier
            // abgestürzt, hat OnStoppedAsync den Zustand schon gesetzt – ein spätes „läuft“ würde den Absturz
            // samt geplantem Neustart überschreiben.
            if (!TrySetState(generation, PluginState.Running, null))
            {
                return;
            }
            Log.Info($"Plugin {Id} läuft");
            if (_tabVisible)
            {
                Notify(PluginProtocol.TabVisibility, new VisibilityParams(true));
            }
            // Zwischen „hello“ und „läuft“ kann sich die Belegung der Kacheln geändert haben; jetzt erst
            // darf sie rausgehen (vorher verwirft Notify alles, was kein laufendes Plugin betrifft).
            SendPlacedWidgets();
        }
        catch (Exception ex)
        {
            await server.DisposeAsync();
            var reason = ex switch
            {
                OperationCanceledException or TimeoutException => "meldet sich nicht",
                _ => ex.Message,
            };
            Log.Warn($"Plugin {Id} startet nicht: {reason}");
            await OnStoppedAsync(generation, reason);
        }
    }

    /// <summary>Höflich beenden: „shutdown“ schicken, kurz warten, dann den Prozess beenden.</summary>
    public async Task StopAsync()
    {
        IPluginProcess? process;
        JsonRpcConnection? connection;
        lock (_gate)
        {
            _stopping = true;
            _generation++;
            process = _process;
            connection = _connection;
            _process = null;
            _connection = null;
            _restart?.Dispose();
            _restart = null;
        }
        if (connection is not null)
        {
            try
            {
                await connection.NotifyAsync(PluginProtocol.Shutdown, null).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException)
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
            process.Dispose();
        }
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
        Cleanup();
        SetState(PluginState.Off, null);
    }

    private async Task OnStoppedAsync(int generation, string reason)
    {
        IPluginProcess? process;
        JsonRpcConnection? connection;
        TimeSpan? restartIn = null;
        lock (_gate)
        {
            if (generation != _generation || _stopping)
            {
                return;
            }
            _generation++;
            process = _process;
            connection = _connection;
            _process = null;
            _connection = null;
            _hello?.TrySetException(new IOException(reason));

            var now = _context.Time.GetUtcNow();
            _crashes.RemoveAll(c => now - c > CrashWindow);
            _crashes.Add(now);
            if (_crashes.Count < MaxCrashes)
            {
                restartIn = Backoff[Math.Min(_crashes.Count - 1, Backoff.Length - 1)];
            }
        }
        Log.Warn($"Plugin {Id} beendet: {reason}");
        if (process is not null)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            process.Dispose();
        }
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
        Cleanup();

        if (restartIn is { } delay)
        {
            // Timer und Zustand im selben Schloss: Wer auf „startet neu“ reagiert (die Verwaltung, Tests mit
            // gestellter Zeit), findet den Neustart geplant vor – und ein sofort feuernder Timer kann den
            // Zustand nicht mehr mit einem veralteten „startet neu“ überschreiben.
            lock (_gate)
            {
                _restart?.Dispose();
                _restart = _context.Time.CreateTimer(_ => _ = StartAsync(), null, delay, Timeout.InfiniteTimeSpan);
                _state = PluginState.Restarting;
                _detail = reason;
            }
            _stateChanged();
        }
        else
        {
            lock (_gate)
            {
                _state = PluginState.Failed;
                _detail = reason;
            }
            _stateChanged();
            Log.Warn($"Plugin {Id} nach {MaxCrashes} Abstürzen abgeschaltet");
        }
    }

    /// <summary>Der Nutzer schaltet ein abgestürztes Plugin wieder ein: Zähler zurücksetzen.</summary>
    public void ResetCrashes()
    {
        lock (_gate)
        {
            _crashes.Clear();
        }
    }

    /// <summary>Alles, was das Plugin gezeigt hat, verschwindet mit ihm.</summary>
    private void Cleanup()
    {
        string[] activities, views;
        lock (_gate)
        {
            activities = [.. _activities];
            views = [.. _views];
            _activities.Clear();
            _views.Clear();
        }
        foreach (var id in activities)
        {
            _context.End(id);
        }
        foreach (var id in views)
        {
            _context.RemoveView(id);
        }
        if (Manifest.Widgets.Count > 0)
        {
            _context.TilePlacementsChanged -= SendPlacedWidgets;
            _context.RemoveTiles(Id + "/");
            _placedWidgets = null;
        }
    }

    // ───────── Widgets für „Jetzt“ ─────────

    private string? _placedWidgets;

    /// <summary>Nach „hello“: Widgets aus dem Manifest in die Galerie, dann melden, welche liegen.</summary>
    private void DefineWidgets()
    {
        if (Manifest.Widgets.Count == 0)
        {
            return;
        }
        foreach (var widget in Manifest.Widgets)
        {
            _context.DefineTile(Scoped(widget.Id), widget.Name, widget.Sizes, Manifest.Name, widget.Glyph, widget.Description);
        }
        _context.TilePlacementsChanged -= SendPlacedWidgets;
        _context.TilePlacementsChanged += SendPlacedWidgets;
        // Erst nach der Antwort auf „hello“ senden: Das Plugin wartet noch darauf.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            SendPlacedWidgets();
        });
    }

    private void SendPlacedWidgets()
    {
        var placed = Manifest.Widgets
            .Select(w => new PlacedWidget(w.Id, _context.PlacedSizes(Scoped(w.Id)).Where(w.Sizes.Contains).OrderBy(s => s).ToList()))
            .Where(w => w.Sizes.Count > 0)
            .ToList();
        var signature = string.Join(";", placed.Select(w => w.Id + ":" + string.Join(",", w.Sizes)));
        lock (_gate)
        {
            // Nur merken, wenn es auch rausgeht: Ein Plugin, das gerade noch startet, bekommt die Belegung
            // sonst nie – die gemerkte Kennung würde den nächsten Versuch als „unverändert“ abtun.
            if (signature == _placedWidgets || _state != PluginState.Running)
            {
                return;
            }
            _placedWidgets = signature;
        }
        Notify(PluginProtocol.WidgetsPlaced, new WidgetsPlacedParams(placed));
    }

    private object? SetWidget(WidgetSetParams p)
    {
        CheckId(p.Id);
        var definition = Manifest.Widgets.FirstOrDefault(w => w.Id == p.Id)
            ?? throw new JsonRpcException(JsonRpcException.InvalidParams, $"Widget „{p.Id}“ steht nicht in widgets im Manifest");
        if (!definition.Sizes.Contains(p.Size))
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, $"Widget „{p.Id}“ hat die Größe {p.Size} nicht im Manifest");
        }
        _context.SetTile(Scoped(p.Id), p.Size, Prepare(p.Content, "content")!);
        return null;
    }

    /// <summary>Setzt den Zustand, wenn dieser Start noch der aktuelle ist (kein Absturz und kein Stop dazwischen).</summary>
    /// <returns><c>false</c>, wenn ein neuerer Zustand gilt und nichts geändert wurde.</returns>
    private bool TrySetState(int generation, PluginState state, string? detail)
    {
        lock (_gate)
        {
            if (generation != _generation || _stopping)
            {
                return false;
            }
            _state = state;
            _detail = detail;
        }
        _stateChanged();
        return true;
    }

    private void SetState(PluginState state, string? detail)
    {
        lock (_gate)
        {
            _state = state;
            _detail = detail;
        }
        _stateChanged();
    }

    // ───────── Brim → Plugin ─────────

    public void Notify(string method, object? parameters)
    {
        JsonRpcConnection? connection;
        lock (_gate)
        {
            connection = State == PluginState.Running || method == PluginProtocol.TabVisibility ? _connection : null;
        }
        if (connection is null)
        {
            return;
        }
        _ = connection.NotifyAsync(method, parameters).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Log.Info($"Plugin {Id} nicht erreichbar: {t.Exception?.InnerException?.Message}");
            }
        }, TaskScheduler.Default);
    }

    public void SetTabVisible(bool visible)
    {
        _tabVisible = visible;
        Notify(PluginProtocol.TabVisibility, new VisibilityParams(visible));
    }

    // ───────── Plugin → Brim ─────────

    /// <summary>Kennung im Hub: <c>&lt;plugin&gt;/&lt;id&gt;</c>; Aktionen kommen als <c>&lt;plugin&gt;|&lt;aktion&gt;</c> zurück.</summary>
    private string Scoped(string id) => Id + "/" + id;

    private Task<object?> HandleAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!WithinRate())
        {
            throw new JsonRpcException(-32000, $"Mehr als {MaxMessagesPerSecond} Nachrichten pro Sekunde");
        }
        if (method != PluginProtocol.Hello && _hello is { Task.IsCompleted: false })
        {
            throw new JsonRpcException(JsonRpcException.InvalidRequest, "Zuerst „hello“ schicken");
        }
        object? result = method switch
        {
            PluginProtocol.Hello => Hello(Parse<HelloParams>(parameters, optional: true) ?? new HelloParams()),
            PluginProtocol.ActivityStart or PluginProtocol.ActivityUpdate => PostActivity(Parse<ActivityParams>(parameters)!),
            PluginProtocol.ActivityEnd => EndActivity(Parse<IdParams>(parameters)!),
            PluginProtocol.HudShow => ShowHud(Parse<HudParams>(parameters)!),
            PluginProtocol.ViewSet => SetView(Parse<ViewParams>(parameters)!),
            PluginProtocol.ViewClear => ClearView(Parse<IdParams>(parameters)!),
            PluginProtocol.LogMessage => WriteLog(Parse<LogParams>(parameters)!),
            PluginProtocol.WidgetSet => SetWidget(Parse<WidgetSetParams>(parameters)!),
            _ => throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Unbekannte Methode {method}"),
        };
        return Task.FromResult(result);
    }

    private static T? Parse<T>(JsonElement? parameters, bool optional = false) where T : class
    {
        if (parameters is not { } p)
        {
            return optional ? null : throw new JsonRpcException(JsonRpcException.InvalidParams, "params fehlen");
        }
        return p.Deserialize<T>(PluginJson.Options) ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "params fehlen");
    }

    private bool WithinRate()
    {
        var second = _context.Time.GetTimestamp() / _context.Time.TimestampFrequency;
        lock (_gate)
        {
            if (second != _rateWindow)
            {
                _rateWindow = second;
                _rateCount = 0;
            }
            return ++_rateCount <= MaxMessagesPerSecond;
        }
    }

    private HelloResult Hello(HelloParams hello)
    {
        if (hello.Api != PluginProtocol.ApiVersion)
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, $"API {hello.Api} wird nicht unterstützt, Brim spricht {PluginProtocol.ApiVersion}");
        }
        var config = _config();
        DefineWidgets();
        var result = new HelloResult
        {
            Id = Id,
            Settings = Manifest.EffectiveSettings(config.Values),
            Permissions = Manifest.Permissions.Where(config.Granted.Contains).ToList(),
        };
        _hello?.TrySetResult();
        return result;
    }

    private static void CheckId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id.Contains('/') || id.Contains('|'))
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, "id muss 1–64 Zeichen ohne „/“ und „|“ haben");
        }
    }

    /// <summary>Prüft einen Baustein und ordnet seine Aktionen diesem Plugin zu.</summary>
    private Widget? Prepare(Widget? widget, string field)
    {
        if (widget is null)
        {
            return null;
        }
        if (WidgetValidator.Validate(widget) is { } error)
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, $"{field}: {error}");
        }
        return WidgetTree.MapActions(widget, action => Id + "|" + action);
    }

    private object? PostActivity(ActivityParams p)
    {
        CheckId(p.Id);
        if (p.Accent is { } accent && !AccentColor.IsValid(accent))
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, $"accent: „{accent}“ ist keine Farbe");
        }
        if (p.Glow is { } glow && !double.IsFinite(glow))
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, "glow: keine Zahl");
        }
        if (p.Announcement is { Length: > WidgetValidator.MaxTextLength })
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, "announcement zu lang");
        }
        if (p.Priority == ActivityPriority.Idle)
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, "priority „idle“ ist der Uhr vorbehalten");
        }
        // Kritische Hinweise klappen die Notch auf; das darf nur, wer das Recht „alerts“ hat.
        var priority = p.Priority == ActivityPriority.Critical && !Granted("alerts") ? ActivityPriority.Transient : p.Priority;
        TimeSpan? duration = p.DurationMs is { } ms ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 100, 24 * 3600 * 1000)) : null;
        if (priority is ActivityPriority.Transient or ActivityPriority.Critical && duration > TimeSpan.FromSeconds(10))
        {
            duration = TimeSpan.FromSeconds(10);
        }
        var activity = new Activity
        {
            ModuleId = _context.ModuleId,
            Id = Scoped(p.Id),
            Priority = priority,
            Leading = Prepare(p.Leading, "leading"),
            Trailing = Prepare(p.Trailing, "trailing"),
            Content = Prepare(p.Content, "content"),
            Expanded = Prepare(p.Expanded, "expanded"),
            Bead = Prepare(p.Bead, "bead"),
            Accent = p.Accent,
            Glow = p.Glow is { } level ? Math.Clamp(level, 0, 1) : null,
            Duration = duration,
            Rank = Math.Clamp(p.Rank, 0, 9),
            IsBackground = p.Background,
            Announcement = p.Announcement,
        };
        lock (_gate)
        {
            _activities.Add(activity.Id);
        }
        _context.Post(activity);
        return null;
    }

    private object? EndActivity(IdParams p)
    {
        CheckId(p.Id);
        lock (_gate)
        {
            _activities.Remove(Scoped(p.Id));
        }
        _context.End(Scoped(p.Id));
        return null;
    }

    private object? ShowHud(HudParams p)
    {
        var content = p.Content ?? new StackWidget(
            p.Glyph is null ? new SpacerWidget() : new IconWidget(p.Glyph) { Size = 16 },
            p.Value is { } value
                ? new ProgressWidget(value) { Grow = 1 }
                : new TextWidget(p.Text ?? "") { Grow = 1 },
            new TextWidget(p.Value is null ? "" : p.Text ?? "") { Style = TextStyle.MonoCaption, FontSize = 12 })
        {
            Spacing = 12,
            Padding = (18, 0, 18, 0),
        };
        return PostActivity(new ActivityParams
        {
            Id = p.Id,
            Priority = ActivityPriority.Transient,
            Content = content,
            DurationMs = p.DurationMs,
            Announcement = p.Announcement ?? p.Text,
        });
    }

    private object? SetView(ViewParams p)
    {
        CheckId(p.Id);
        var content = Prepare(p.Content, "content")!;
        lock (_gate)
        {
            _views.Add(Scoped(p.Id));
        }
        // Plugins in der Reihenfolge ihres Ordners, innerhalb eines Plugins nach seiner Angabe.
        _context.SetView(Scoped(p.Id), content, NotchTab.Plugins, TabColumn.Main, (_index + 1) * 100 + Math.Clamp(p.Order, 0, 99));
        return null;
    }

    private object? ClearView(IdParams p)
    {
        CheckId(p.Id);
        lock (_gate)
        {
            _views.Remove(Scoped(p.Id));
        }
        _context.RemoveView(Scoped(p.Id));
        return null;
    }

    private object? WriteLog(LogParams p)
    {
        var message = $"[{Id}] {(p.Message.Length > 2000 ? p.Message[..2000] : p.Message)}";
        switch (p.Level)
        {
            case "error":
                Log.Error(message);
                break;
            case "warn":
                Log.Warn(message);
                break;
            default:
                Log.Info(message);
                break;
        }
        return null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
