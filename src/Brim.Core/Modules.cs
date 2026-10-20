using Brim.Core.Widgets;

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

    /// <summary>
    /// Ein Tab ist in einer Notch sichtbar geworden oder in keiner mehr zu sehen. Module mit teuren
    /// Abfragen (Ressourcen) messen nur, solange ihr Tab offen ist (Plan 4.7).
    /// </summary>
    void OnTabVisibilityChanged(NotchTab tab, bool visible) { }
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

    /// <summary>Setzt den Inhalt dieses Moduls in einem Tab (die Modul-ID wird ergänzt).</summary>
    public void SetView(string id, Widget content, NotchTab tab = NotchTab.System, TabColumn column = TabColumn.Main, int order = 0) =>
        _hub.SetView(new TabView { ModuleId = ModuleId, Id = id, Content = content, Tab = tab, Column = column, Order = order });

    public void RemoveView(string id) => _hub.RemoveView(ModuleId, id);

    public bool IsTabVisible(NotchTab tab) => _hub.IsTabVisible(tab);

    /// <summary>Bietet eine Kachel für „Jetzt“ an (Galerie).</summary>
    public void DefineTile(string id, string name, IReadOnlyList<TileSize> sizes, string category = "Brim", string? glyph = null, string? description = null) =>
        _hub.DefineTile(new TileDefinition { ModuleId = ModuleId, Id = id, Name = name, Sizes = sizes, Category = category, Glyph = glyph, Description = description });

    /// <summary>Nimmt Kacheln aus der Galerie, deren Kennung so beginnt (z. B. alle eines Plugins).</summary>
    public void RemoveTiles(string idPrefix) => _hub.RemoveTiles(ModuleId, idPrefix);

    /// <summary>Inhalt einer Kachel in einer Größe.</summary>
    public void SetTile(string id, TileSize size, Widget content) => _hub.SetTile(ModuleId, id, size, content);

    /// <summary>Größen, in denen die Kachel gerade liegt (oder in der Galerie zu sehen ist); leer = nichts zu tun.</summary>
    public IReadOnlyList<TileSize> PlacedSizes(string id) => _hub.PlacedSizes(ModuleId, id);

    /// <summary>Gelegte Kacheln haben sich geändert.</summary>
    public event Action? TilePlacementsChanged
    {
        add => _hub.TilePlacementsChanged += value;
        remove => _hub.TilePlacementsChanged -= value;
    }

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
                _hub.TabVisibilityChanged += module.OnTabVisibilityChanged;
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
                _hub.TabVisibilityChanged -= module.OnTabVisibilityChanged;
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
