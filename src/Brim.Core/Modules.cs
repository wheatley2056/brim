namespace Brim.Core;

/// <summary>
/// Ein Baustein der Notch. Eingebaute Funktionen sind selbst Module und nutzen
/// dieselbe Schnittstelle wie später Plugins (Plan 3, Grundsätze).
/// </summary>
public interface IBrimModule : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }

    Task StartAsync(ModuleContext context, CancellationToken cancellationToken);

    /// <summary>Knopf gedrückt oder Fortschrittsleiste angeklickt.</summary>
    void OnAction(ActionInvocation invocation) { }

    /// <summary>
    /// Die Notch wurde auf- oder zugeklappt. Module können so Abfragen nur bei Sichtbarkeit laufen
    /// lassen und Untermenüs beim Zuklappen zurücksetzen.
    /// </summary>
    void OnExpandedChanged(bool expanded) { }
}

/// <summary>Was ein Modul von der Shell bekommt.</summary>
public sealed class ModuleContext
{
    private readonly ActivityHub _hub;

    public ModuleContext(string moduleId, ActivityHub hub, BrimSettings settings)
    {
        ModuleId = moduleId;
        _hub = hub;
        Settings = settings;
    }

    public string ModuleId { get; }
    public BrimSettings Settings { get; }
    public TimeProvider Time => _hub.Time;

    /// <summary>Startet oder aktualisiert eine Aktivität dieses Moduls.</summary>
    public void Post(Activity activity)
    {
        if (activity.ModuleId != ModuleId)
        {
            throw new ArgumentException($"Modul {ModuleId} darf keine Aktivität für {activity.ModuleId} melden.");
        }
        _hub.Post(activity);
    }

    public void End(string id) => _hub.End(ModuleId, id);

    /// <summary>Löst eine Aktion in einem anderen Modul aus, z. B. „Audio-Menü öffnen“ aus dem Player.</summary>
    public void Send(string moduleId, string actionId, double? value = null) =>
        _hub.Invoke(new ActionInvocation(moduleId, actionId, value));
}

/// <summary>Startet Module, isoliert ihre Fehler und verteilt Aktionen.</summary>
public sealed class ModuleHost : IAsyncDisposable
{
    private readonly ActivityHub _hub;
    private readonly List<IBrimModule> _running = [];
    private readonly CancellationTokenSource _cts = new();

    public ModuleHost(ActivityHub hub)
    {
        _hub = hub;
    }

    public IReadOnlyList<IBrimModule> Running => _running;

    public async Task StartAsync(IEnumerable<IBrimModule> modules, BrimSettings settings)
    {
        foreach (var module in modules)
        {
            if (settings.DisabledModules.Contains(module.Id))
            {
                Log.Info($"Modul {module.Id} ist abgeschaltet");
                continue;
            }
            try
            {
                await module.StartAsync(new ModuleContext(module.Id, _hub, settings), _cts.Token);
                _hub.RegisterActionHandler(module.Id, module.OnAction);
                _hub.ExpandedChanged += module.OnExpandedChanged;
                _running.Add(module);
                Log.Info($"Modul {module.Id} gestartet");
            }
            catch (Exception ex)
            {
                // Ein fehlerhaftes Modul darf die Notch nicht mitreißen.
                Log.Error($"Modul {module.Id} konnte nicht starten", ex);
                _hub.EndAll(module.Id);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        foreach (var module in _running)
        {
            try
            {
                _hub.UnregisterActionHandler(module.Id);
                _hub.ExpandedChanged -= module.OnExpandedChanged;
                await module.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warn($"Modul {module.Id} ließ sich nicht sauber beenden", ex);
            }
        }
        _running.Clear();
        _cts.Dispose();
    }
}
