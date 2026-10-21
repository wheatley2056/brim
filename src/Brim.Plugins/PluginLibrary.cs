using Brim.Core;

namespace Brim.Plugins;

public enum LibraryStatus
{
    /// <summary>Im Katalog, nicht installiert.</summary>
    Available,
    /// <summary>Installiert und aktuell.</summary>
    Installed,
    /// <summary>Neuere Version im Katalog; wird im Hintergrund aktualisiert.</summary>
    UpdateAvailable,
    /// <summary>Neuere Version verlangt neue Rechte: nur mit Zustimmung (Knopf „Aktualisieren“).</summary>
    UpdateNeedsConsent,
    /// <summary>Braucht eine neuere Brim-Version.</summary>
    NeedsNewerBrim,
    /// <summary>Installiert, aber nicht aus dem Katalog oder in einem eigenen Ordner: Die Bibliothek fasst es nicht an.</summary>
    Local,
}

/// <summary>Eine Karte in der Bibliothek.</summary>
public sealed record LibraryItem(string Id, string Name, CatalogEntry? Entry, PluginManifest? Installed, LibraryStatus Status)
{
    /// <summary>Rechte, die ein Update neu verlangt.</summary>
    public IReadOnlyList<string> NewPermissions =>
        Entry is null || Installed is null ? [] : Entry.Permissions.Except(Installed.Permissions).ToList();
}

/// <summary>
/// Plugin-Bibliothek: lädt den Katalog, gleicht ihn mit den installierten Plugins ab und installiert,
/// aktualisiert und entfernt über den <see cref="PluginHostModule"/>. Installierte Plugins aus dem Katalog
/// werden im Hintergrund aktuell gehalten, solange ein Update keine neuen Rechte verlangt.
/// </summary>
public sealed class PluginLibrary : IDisposable
{
    private readonly HttpClient _http;
    private readonly PluginHostModule _host;
    private readonly PluginInstaller _installer;
    private readonly Func<BrimSettings> _settings;
    private readonly Func<string?> _brimVersion;
    private readonly string _cacheFile;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, double> _busy = [];
    private readonly Dictionary<string, string> _errors = [];
    private readonly CancellationTokenSource _cts = new();
    private PluginCatalog? _catalog;

    /// <param name="dataDirectory">Datenordner von Brim; dort liegen der zwischengespeicherte Katalog und der Arbeitsordner.</param>
    /// <param name="brimVersion">Installierte Brim-Version; <c>null</c> in der Entwicklungsfassung (dann passt jedes Plugin).</param>
    public PluginLibrary(HttpClient http, PluginHostModule host, Func<BrimSettings> settings, string dataDirectory, Func<string?> brimVersion)
    {
        _http = http;
        _host = host;
        _settings = settings;
        _brimVersion = brimVersion;
        _cacheFile = Path.Combine(dataDirectory, "catalog.json");
        _installer = new PluginInstaller(http, Path.Combine(dataDirectory, "PluginStaging"));
        _host.Changed += OnChanged;
        LoadCache();
    }

    /// <summary>Katalog, Fortschritt oder installierte Plugins haben sich geändert. Kommt aus beliebigen Threads.</summary>
    public event Action? Changed;

    /// <summary>Letzter Fehler beim Laden des Katalogs (dann gilt der zwischengespeicherte).</summary>
    public string? CatalogError { get; private set; }

    /// <summary>Zeitpunkt, an dem der Katalog zuletzt geladen wurde.</summary>
    public DateTimeOffset? CatalogLoaded { get; private set; }

    public bool HasCatalog => _catalog is not null;

    private void OnChanged() => Changed?.Invoke();

    // ───────── Katalog ─────────

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_cacheFile))
            {
                _catalog = PluginCatalog.Parse(File.ReadAllText(_cacheFile), out _);
                CatalogLoaded = File.GetLastWriteTimeUtc(_cacheFile);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Bibliothek: zwischengespeicherter Katalog unlesbar ({ex.Message})");
        }
    }

    /// <summary>Lädt den Katalog neu. Ohne Netz bleibt der zwischengespeicherte, und <see cref="CatalogError"/> sagt warum.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var url = _settings().Library.CatalogUrl;
        try
        {
            var json = await _http.GetStringAsync(url, cancellationToken);
            var catalog = PluginCatalog.Parse(json, out var skipped);
            foreach (var reason in skipped)
            {
                Log.Warn($"Bibliothek: Eintrag übersprungen: {reason}");
            }
            _catalog = catalog;
            CatalogError = null;
            CatalogLoaded = DateTimeOffset.UtcNow;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                await File.WriteAllTextAsync(_cacheFile, json, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Bibliothek: Katalog nicht zwischengespeichert ({ex.Message})");
            }
            Log.Info($"Bibliothek: {catalog.Plugins.Count} Plugin(s) im Katalog");
        }
        catch (Exception ex) when ((ex is HttpRequestException or InvalidDataException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            CatalogError = ex is InvalidDataException ? ex.Message : "Katalog nicht erreichbar";
            Log.Info($"Bibliothek: Katalog nicht geladen ({ex.Message})");
        }
        OnChanged();
    }

    // ───────── Karten ─────────

    public IReadOnlyList<LibraryItem> Items()
    {
        var installed = _host.Installed.ToDictionary(m => m.Id);
        var items = new List<LibraryItem>();
        foreach (var entry in _catalog?.Plugins ?? [])
        {
            installed.Remove(entry.Id, out var manifest);
            items.Add(new LibraryItem(entry.Id, entry.Name, entry, manifest, StatusOf(entry, manifest)));
        }
        items.AddRange(installed.Values.Select(m => new LibraryItem(m.Id, m.Name, null, m, LibraryStatus.Local)));
        return items;
    }

    private LibraryStatus StatusOf(CatalogEntry entry, PluginManifest? installed)
    {
        if (installed is not null && !_host.IsManaged(entry.Id))
        {
            return LibraryStatus.Local;
        }
        var newer = installed is null || PluginVersion.IsNewer(entry.Version, installed.Version);
        if (!newer)
        {
            return LibraryStatus.Installed;
        }
        if (entry.MinBrim is { } min && PluginVersion.TryParse(_brimVersion()) is { } brim && brim < PluginVersion.TryParse(min))
        {
            return LibraryStatus.NeedsNewerBrim;
        }
        if (installed is null)
        {
            return LibraryStatus.Available;
        }
        return entry.Permissions.Except(installed.Permissions).Any() ? LibraryStatus.UpdateNeedsConsent : LibraryStatus.UpdateAvailable;
    }

    /// <summary>Installiert und im Standardordner, also von der Bibliothek entfernbar?</summary>
    public bool CanRemove(LibraryItem item) => item.Installed is not null && _host.IsManaged(item.Id);

    /// <summary>Fortschritt 0–1, solange das Plugin geladen oder installiert wird.</summary>
    public double? Progress(string id)
    {
        lock (_gate)
        {
            return _busy.TryGetValue(id, out var p) ? p : null;
        }
    }

    /// <summary>Letzter Fehler beim Installieren dieses Plugins.</summary>
    public string? Error(string id)
    {
        lock (_gate)
        {
            return _errors.GetValueOrDefault(id);
        }
    }

    // ───────── Installieren, aktualisieren, entfernen ─────────

    /// <summary>
    /// Installiert oder aktualisiert. Vom Nutzer ausgelöst ist das die Zustimmung zu den Rechten auf der Karte;
    /// das Plugin wird eingeschaltet. Gibt <c>false</c> zurück, wenn es fehlschlug (Grund in <see cref="Error"/>).
    /// </summary>
    public Task<bool> InstallAsync(string id) => InstallAsync(id, byUser: true);

    private async Task<bool> InstallAsync(string id, bool byUser)
    {
        var entry = _catalog?.Plugins.FirstOrDefault(e => e.Id == id);
        if (entry is null)
        {
            return false;
        }
        lock (_gate)
        {
            if (!_busy.TryAdd(id, 0))
            {
                return false;
            }
            _errors.Remove(id);
        }
        OnChanged();
        string? staged = null;
        try
        {
            var progress = new Progress<double>(p =>
            {
                // Progress meldet über den Thread-Pool und kann nach dem Ende ankommen: dann nichts mehr eintragen,
                // sonst bliebe das Plugin als „lädt“ hängen und ließe sich nicht erneut installieren.
                lock (_gate)
                {
                    if (!_busy.ContainsKey(id))
                    {
                        return;
                    }
                    _busy[id] = p;
                }
                OnChanged();
            });
            staged = await _installer.StageAsync(entry, progress, _cts.Token);
            // Vom Nutzer: einschalten mit genau diesen Rechten. Im Hintergrund (nur ohne neue Rechte) bleibt der Schalter, wie er war.
            await _host.InstallAsync(staged, grant: byUser);
            return true;
        }
        catch (Exception ex) when ((ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException
            or InvalidOperationException or TaskCanceledException) && !_cts.IsCancellationRequested)
        {
            Log.Warn($"Bibliothek: {id} nicht installiert ({ex.Message})");
            lock (_gate)
            {
                _errors[id] = ex is HttpRequestException or TaskCanceledException ? "Download fehlgeschlagen" : ex.Message;
            }
            return false;
        }
        finally
        {
            if (staged is not null)
            {
                _installer.Discard(staged);
            }
            lock (_gate)
            {
                _busy.Remove(id);
            }
            OnChanged();
        }
    }

    public async Task<bool> RemoveAsync(string id)
    {
        try
        {
            await _host.RemoveAsync(id);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn($"Bibliothek: {id} nicht entfernt ({ex.Message})");
            lock (_gate)
            {
                _errors[id] = ex.Message;
            }
            OnChanged();
            return false;
        }
    }

    /// <summary>Alle Plugins mit <see cref="LibraryStatus.UpdateAvailable"/> aktualisieren.</summary>
    public async Task UpdateAllAsync()
    {
        foreach (var item in Items().Where(i => i.Status == LibraryStatus.UpdateAvailable))
        {
            Log.Info($"Bibliothek: aktualisiere {item.Id} auf {item.Entry!.Version}");
            await InstallAsync(item.Id, byUser: false);
        }
    }

    // ───────── Im Hintergrund ─────────

    /// <summary>Katalog kurz nach dem Start und dann alle <paramref name="interval"/> laden; Updates installieren.</summary>
    public void Start(Func<TimeSpan> interval)
    {
        _installer.CleanUp();
        _ = LoopAsync(interval);
    }

    private async Task LoopAsync(Func<TimeSpan> interval)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
            while (!_cts.IsCancellationRequested)
            {
                await RefreshAsync(_cts.Token);
                if (_settings().Library.AutoUpdate)
                {
                    await UpdateAllAsync();
                }
                await Task.Delay(interval(), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _host.Changed -= OnChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}
