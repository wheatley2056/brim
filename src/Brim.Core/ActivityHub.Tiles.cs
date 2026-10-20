using Brim.Core.Widgets;

namespace Brim.Core;

// Kacheln für die Seite „Jetzt“: Module und Plugins melden, was sie anbieten (Galerie), und liefern Inhalt
// nur für Größen, die gerade liegen oder in der Galerie zu sehen sind.
public sealed partial class ActivityHub
{
    private readonly Dictionary<string, TileDefinition> _tileDefinitions = new();
    private readonly Dictionary<(string Key, TileSize Size), Widget> _tileContent = new();
    private HashSet<(string Key, TileSize Size)> _placedTiles = [];

    /// <summary>Was gezeigt wird, hat sich geändert: Module liefern Inhalt für diese Größen (auf dem Thread der Shell).</summary>
    public event Action? TilePlacementsChanged;

    public void DefineTile(TileDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            _tileDefinitions[definition.Key] = definition;
        }
        Changed?.Invoke();
    }

    /// <summary>Entfernt Kacheln eines Moduls aus der Galerie, optional nur die mit diesem Anfang der Kennung (ein Plugin).</summary>
    public void RemoveTiles(string moduleId, string idPrefix = "")
    {
        var prefix = moduleId + "/" + idPrefix;
        lock (_gate)
        {
            foreach (var key in _tileDefinitions.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _tileDefinitions.Remove(key);
            }
            foreach (var key in _tileContent.Keys.Where(k => k.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _tileContent.Remove(key);
            }
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<TileDefinition> TileCatalog()
    {
        lock (_gate)
        {
            return _tileDefinitions.Values.OrderBy(d => d.Category, StringComparer.CurrentCulture).ThenBy(d => d.Name, StringComparer.CurrentCulture).ToList();
        }
    }

    public TileDefinition? Tile(string key)
    {
        lock (_gate)
        {
            return _tileDefinitions.GetValueOrDefault(key);
        }
    }

    /// <summary>Inhalt einer Kachel in einer Größe; ändert er sich nicht, löst es nichts aus.</summary>
    public void SetTile(string moduleId, string id, TileSize size, Widget content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var key = (ActivityKey.Of(moduleId, id), size);
        lock (_gate)
        {
            if (_tileContent.TryGetValue(key, out var old) && old == content)
            {
                return;
            }
            _tileContent[key] = content;
        }
        Changed?.Invoke();
    }

    public Widget? TileContent(string key, TileSize size)
    {
        lock (_gate)
        {
            return _tileContent.GetValueOrDefault((key, size));
        }
    }

    /// <summary>Von der Shell: diese Kacheln liegen (oder stehen in der offenen Galerie).</summary>
    public void SetTilePlacements(IEnumerable<(string Key, TileSize Size)> placements)
    {
        var next = placements.ToHashSet();
        lock (_gate)
        {
            if (next.SetEquals(_placedTiles))
            {
                return;
            }
            _placedTiles = next;
        }
        foreach (var handler in TilePlacementsChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                Log.Error("Modul reagierte fehlerhaft auf neue Kacheln", ex);
            }
        }
    }

    /// <summary>Größen, in denen eine Kachel dieses Moduls gerade gebraucht wird.</summary>
    public IReadOnlyList<TileSize> PlacedSizes(string moduleId, string id)
    {
        var key = ActivityKey.Of(moduleId, id);
        lock (_gate)
        {
            return _placedTiles.Where(p => p.Key == key).Select(p => p.Size).Distinct().ToList();
        }
    }
}
