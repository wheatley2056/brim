using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brim.Core;

public enum ShelfItemMode
{
    /// <summary>Pfad wird gemerkt, die Datei bleibt, wo sie ist.</summary>
    Reference,
    /// <summary>Datei liegt als Kopie in der Ablage (temporäre Herkunft oder Einstellung).</summary>
    Copy,
}

/// <summary>Ein Eintrag in der Ablage (Plan 4.4).</summary>
public sealed record ShelfItem(string Id, string Path, ShelfItemMode Mode, DateTimeOffset AddedAt)
{
    [JsonIgnore]
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;

    [JsonIgnore]
    public bool IsFolder => Directory.Exists(Path);

    [JsonIgnore]
    public bool Exists => File.Exists(Path) || Directory.Exists(Path);
}

/// <summary>
/// Hält die Ablage und speichert sie in <c>shelf.json</c>. Kopien landen in einem eigenen Ordner
/// und werden beim Entfernen wieder gelöscht; Originale werden nie angefasst.
/// </summary>
public sealed class ShelfStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;
    private readonly string _copies;
    private readonly IReadOnlyList<string> _temporaryRoots;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private List<ShelfItem> _items = [];

    /// <param name="directory">Datenordner, z. B. %LocalAppData%\Brim.</param>
    /// <param name="temporaryRoots">Ordner, deren Dateien flüchtig sind und deshalb kopiert werden.</param>
    public ShelfStore(string directory, IReadOnlyList<string> temporaryRoots, TimeProvider? time = null)
    {
        _file = System.IO.Path.Combine(directory, "shelf.json");
        _copies = System.IO.Path.Combine(directory, "Shelf");
        _temporaryRoots = temporaryRoots.Select(Normalize).ToList();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Standardliste flüchtiger Orte: Temp, Browser- und Outlook-Zwischenspeicher.</summary>
    public static IReadOnlyList<string> DefaultTemporaryRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            System.IO.Path.GetTempPath(),
            System.IO.Path.Combine(local, "Microsoft", "Windows", "INetCache"),
            System.IO.Path.Combine(local, "Temp"),
        ];
    }

    public string CopyDirectory => _copies;

    public event Action? Changed;

    public IReadOnlyList<ShelfItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToList();
            }
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_file))
                {
                    _items = JsonSerializer.Deserialize<List<ShelfItem>>(File.ReadAllText(_file), Options) ?? [];
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn("Ablage unlesbar, beginne leer", ex);
                _items = [];
            }
        }
    }

    /// <summary>Liegt der Pfad an einem flüchtigen Ort (Temp, Browser-Cache)?</summary>
    public bool IsTemporary(string path)
    {
        var normalized = Normalize(path);
        return _temporaryRoots.Any(root => normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fügt Dateien oder Ordner hinzu. Bereits vorhandene Pfade werden nicht doppelt aufgenommen.
    /// Flüchtige Dateien (oder alle, wenn <paramref name="storage"/> Kopie ist) werden kopiert.
    /// </summary>
    public IReadOnlyList<ShelfItem> Add(IEnumerable<string> paths, ShelfStorage storage)
    {
        var added = new List<ShelfItem>();
        lock (_gate)
        {
            foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                if (_items.Any(i => string.Equals(Normalize(i.Path), Normalize(path), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var copy = storage == ShelfStorage.Copy || IsTemporary(path);
                var id = Guid.NewGuid().ToString("N");
                var finalPath = copy ? CopyIn(path, id) : path;
                if (finalPath is null)
                {
                    continue;
                }
                var item = new ShelfItem(id, finalPath, copy ? ShelfItemMode.Copy : ShelfItemMode.Reference, _time.GetUtcNow());
                _items.Add(item);
                added.Add(item);
            }
            if (added.Count > 0)
            {
                Save();
            }
        }
        if (added.Count > 0)
        {
            Changed?.Invoke();
        }
        return added;
    }

    /// <summary>
    /// Zielpfad für eine empfangene Datei oder einen Ordner im Kopien-Ordner. Nach dem Empfang
    /// mit <see cref="AddReceived"/> aufnehmen; beim Entfernen wird die Kopie wieder gelöscht.
    /// </summary>
    public string ReserveIncoming(string name)
    {
        var safe = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        var folder = System.IO.Path.Combine(_copies, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return System.IO.Path.Combine(folder, safe.Length == 0 ? "Datei" : safe);
    }

    /// <summary>Nimmt empfangene Einträge (aus <see cref="ReserveIncoming"/>) als Kopien auf.</summary>
    public IReadOnlyList<ShelfItem> AddReceived(IEnumerable<string> paths)
    {
        var added = new List<ShelfItem>();
        lock (_gate)
        {
            foreach (var path in paths)
            {
                var id = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? Guid.NewGuid().ToString("N");
                var item = new ShelfItem(id, path, ShelfItemMode.Copy, _time.GetUtcNow());
                _items.Add(item);
                added.Add(item);
            }
            if (added.Count > 0)
            {
                Save();
            }
        }
        if (added.Count > 0)
        {
            Changed?.Invoke();
        }
        return added;
    }

    public void Remove(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        bool removed;
        lock (_gate)
        {
            var gone = _items.Where(i => set.Contains(i.Id)).ToList();
            gone.ForEach(DeleteCopy);
            removed = _items.RemoveAll(i => set.Contains(i.Id)) > 0;
            if (removed)
            {
                Save();
            }
        }
        if (removed)
        {
            Changed?.Invoke();
        }
    }

    public void Clear() => Remove(Items.Select(i => i.Id));

    private string? CopyIn(string source, string id)
    {
        try
        {
            // Je Eintrag ein eigener Unterordner: gleiche Dateinamen kollidieren nicht, der Name bleibt erhalten.
            var target = System.IO.Path.Combine(_copies, id, System.IO.Path.GetFileName(source.TrimEnd('\\', '/')));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            if (Directory.Exists(source))
            {
                CopyTree(source, target);
            }
            else
            {
                File.Copy(source, target);
            }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Konnte {source} nicht in die Ablage kopieren", ex);
            return null;
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyTree(dir, System.IO.Path.Combine(target, System.IO.Path.GetFileName(dir)));
        }
    }

    private void DeleteCopy(ShelfItem item)
    {
        if (item.Mode != ShelfItemMode.Copy)
        {
            return;
        }
        try
        {
            // Nur innerhalb des eigenen Kopien-Ordners löschen – nie ein Original.
            var folder = System.IO.Path.GetDirectoryName(item.Path);
            if (folder is not null && Normalize(folder).StartsWith(Normalize(_copies), StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Kopie {item.Path} ließ sich nicht löschen", ex);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, Options));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Ablage konnte nicht gespeichert werden", ex);
        }
    }

    private static string Normalize(string path) =>
        System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
        + System.IO.Path.DirectorySeparatorChar;
}
