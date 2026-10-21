using System.Diagnostics;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.Plugins;

/// <summary>
/// Plugin-Host (Plan 4.2) als Modul: findet Plugins in den Plugin-Ordnern und startet die eingeschalteten.
/// Der Tab „Plugins“ ist nur zum Ein- und Ausschalten da; Plugins zeigen dort nichts Eigenes, ihre Inhalte
/// kommen als Widgets auf „Jetzt“. Einschalten heißt zugleich, den im Manifest genannten Rechten zuzustimmen –
/// die Zeile nennt sie direkt neben dem Schalter.
/// </summary>
public sealed class PluginHostModule : IBrimModule
{
    public const string ModuleId = "plugins";

    private readonly IReadOnlyList<string> _directories;
    private readonly IPluginLauncher _launcher;
    private readonly Action<BrimSettings> _save;
    private readonly Lock _gate = new();
    private readonly List<PluginRunner> _runners = [];
    private readonly List<(string Directory, string Error)> _broken = [];
    private readonly SemaphoreSlim _changing = new(1, 1);
    private ModuleContext? _context;
    private BrimSettings? _settings;

    /// <param name="directories">Ordner mit je einem Unterordner pro Plugin; der erste ist der Standardordner.</param>
    /// <param name="save">Speichert geänderte Einstellungen (Schalter und Rechte).</param>
    public PluginHostModule(IReadOnlyList<string> directories, IPluginLauncher launcher, Action<BrimSettings> save)
    {
        _directories = directories;
        _launcher = launcher;
        _save = save;
    }

    public string Id => ModuleId;
    public string DisplayName => "Plugins";

    public IReadOnlyList<PluginManifest> Installed
    {
        get
        {
            lock (_gate)
            {
                return _runners.Select(r => r.Manifest).ToList();
            }
        }
    }

    /// <summary>Öffnet die Bibliothek (Einstellungen → Plugins); von der App gesetzt.</summary>
    public Action? OpenLibrary { get; set; }

    /// <summary>Etwas an den Plugins hat sich geändert (Zustand, installiert, entfernt). Kommt aus beliebigen Threads.</summary>
    public event Action? Changed;

    /// <summary>Standardordner; hierhin installiert die Bibliothek.</summary>
    public string DefaultDirectory => _directories[0];

    /// <summary>
    /// Liegt das Plugin im Standardordner? Nur dann darf die Bibliothek es ersetzen oder entfernen; Plugins aus
    /// eigenen Ordnern (<c>PluginDirectories</c>, beim Entwickeln) fasst sie nicht an.
    /// </summary>
    public bool IsManaged(string id)
    {
        var runner = Runners().FirstOrDefault(r => r.Id == id);
        return runner is null || IsInside(runner.Manifest.Directory, DefaultDirectory);
    }

    /// <summary>Eingeschaltet und allen Rechten zugestimmt?</summary>
    public bool IsEnabled(string id) => Runners().FirstOrDefault(r => r.Id == id) is { } runner && IsEnabled(runner.Manifest);

    public PluginState StateOf(string id)
    {
        lock (_gate)
        {
            return _runners.FirstOrDefault(r => r.Id == id)?.State ?? PluginState.Off;
        }
    }

    public async Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _settings = context.Settings;
        CleanLeftovers();
        Discover();
        Publish();
        foreach (var runner in Runners().Where(r => IsEnabled(r.Manifest)))
        {
            // Nicht auf jedes Plugin warten: ein langsames hält die anderen und die Notch nicht auf.
            _ = runner.StartAsync();
        }
        await Task.CompletedTask;
    }

    private List<PluginRunner> Runners()
    {
        lock (_gate)
        {
            return [.. _runners];
        }
    }

    private void Discover()
    {
        var found = new List<PluginManifest>();
        var broken = new List<(string, string)>();
        foreach (var root in _directories.Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(dir, PluginManifest.FileName)) || IsLeftover(dir))
                {
                    continue;
                }
                try
                {
                    var manifest = PluginManifest.Load(dir);
                    if (found.Any(m => m.Id == manifest.Id))
                    {
                        throw new InvalidDataException($"Kennung {manifest.Id} gibt es schon");
                    }
                    found.Add(manifest);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Plugin in {dir} ist fehlerhaft: {ex.Message}");
                    broken.Add((dir, ex.Message));
                }
            }
        }
        lock (_gate)
        {
            _broken.Clear();
            _broken.AddRange(broken);
            for (var i = 0; i < found.Count; i++)
            {
                _runners.Add(CreateRunner(found[i], i));
            }
        }
        Log.Info($"{found.Count} Plugin(s) gefunden");
    }

    private PluginRunner CreateRunner(PluginManifest manifest, int index) =>
        new(manifest, _context!, _launcher, () => Config(manifest.Id), index, Publish);

    // ───────── Installieren und Entfernen (Bibliothek) ─────────

    /// <summary>
    /// Bringt ein geprüftes Plugin aus dem Bereitstellungsordner in den Standardordner und startet es. Eine ältere
    /// Fassung wird vorher beendet und ersetzt; ihre Einstellungen bleiben. Mit <paramref name="grant"/> stimmt der
    /// Nutzer den Rechten zu und schaltet es ein (Installieren in der Bibliothek). Ohne bleibt es, wie es war;
    /// neue Rechte schalten es wie bisher aus, bis der Nutzer zustimmt.
    /// </summary>
    public async Task InstallAsync(string staged, bool grant)
    {
        if (_context is null)
        {
            throw new InvalidOperationException("Plugin-Host läuft nicht");
        }
        var manifest = PluginManifest.Load(staged);
        await _changing.WaitAsync();
        try
        {
            var old = Runners().FirstOrDefault(r => r.Id == manifest.Id);
            if (old is not null && !IsInside(old.Manifest.Directory, DefaultDirectory))
            {
                throw new InvalidOperationException($"{manifest.Name} liegt in einem eigenen Ordner ({old.Manifest.Directory}) und wird dort verwaltet");
            }
            var target = old?.Manifest.Directory ?? Path.Combine(DefaultDirectory, manifest.Id);
            if (old is not null)
            {
                await old.StopAsync();
            }
            try
            {
                Replace(staged, target);
            }
            catch
            {
                // Alte Fassung läuft weiter, als wäre nichts gewesen.
                if (old is not null && IsEnabled(old.Manifest))
                {
                    _ = old.StartAsync();
                }
                throw;
            }

            var installed = PluginManifest.Load(target);
            var runner = CreateRunner(installed, old is null ? Runners().Count : IndexOf(old));
            lock (_gate)
            {
                if (old is not null)
                {
                    _runners[_runners.IndexOf(old)] = runner;
                }
                else
                {
                    _runners.Add(runner);
                }
                _broken.RemoveAll(b => string.Equals(Path.GetFullPath(b.Directory), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase));
            }
            if (grant)
            {
                var config = Config(installed.Id);
                config.Enabled = true;
                config.Granted = [.. installed.Permissions];
                SaveSettings();
            }
            Log.Info($"Plugin {installed.Id} {installed.Version} installiert");
            Publish();
            if (IsEnabled(installed))
            {
                await runner.StartAsync();
            }
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>Beendet das Plugin, löscht seinen Ordner und vergisst Schalter, Rechte und Einstellungen.</summary>
    public async Task RemoveAsync(string id)
    {
        await _changing.WaitAsync();
        try
        {
            var runner = Runners().FirstOrDefault(r => r.Id == id) ?? throw new InvalidOperationException($"{id} ist nicht installiert");
            if (!IsInside(runner.Manifest.Directory, DefaultDirectory))
            {
                throw new InvalidOperationException($"{runner.Manifest.Name} liegt in einem eigenen Ordner und wird dort verwaltet");
            }
            await runner.StopAsync();
            lock (_gate)
            {
                _runners.Remove(runner);
            }
            // Erst umbenennen, dann löschen: Hält Windows noch eine Datei fest, ist das Plugin trotzdem weg.
            var trash = runner.Manifest.Directory + ".removed-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(runner.Manifest.Directory, trash);
            PluginInstaller.TryDeleteDirectory(trash);
            var plugins = _settings!.Plugins;
            lock (plugins)
            {
                plugins.Remove(id);
            }
            SaveSettings();
            Log.Info($"Plugin {id} entfernt");
            Publish();
        }
        finally
        {
            _changing.Release();
        }
    }

    private int IndexOf(PluginRunner runner)
    {
        lock (_gate)
        {
            return _runners.IndexOf(runner);
        }
    }

    /// <summary>Alten Ordner beiseite, neuen an seine Stelle; klappt das nicht, kommt der alte zurück.</summary>
    private static void Replace(string staged, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string? backup = null;
        if (Directory.Exists(target))
        {
            backup = target + ".old-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(target, backup);
        }
        try
        {
            Directory.Move(staged, target);
        }
        catch
        {
            if (backup is not null)
            {
                Directory.Move(backup, target);
            }
            throw;
        }
        if (backup is not null)
        {
            PluginInstaller.TryDeleteDirectory(backup);
        }
    }

    /// <summary>Reste aus <see cref="Replace"/> und <see cref="RemoveAsync"/>, die Windows beim letzten Mal festhielt.</summary>
    private void CleanLeftovers()
    {
        if (!Directory.Exists(DefaultDirectory))
        {
            return;
        }
        foreach (var dir in Directory.EnumerateDirectories(DefaultDirectory))
        {
            if (IsLeftover(dir))
            {
                PluginInstaller.TryDeleteDirectory(dir);
            }
        }
    }

    private static bool IsLeftover(string dir)
    {
        var name = Path.GetFileName(dir);
        return name.Contains(".old-", StringComparison.Ordinal) || name.Contains(".removed-", StringComparison.Ordinal);
    }

    private static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveSettings()
    {
        try
        {
            _save(_settings!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Einstellungen nicht gespeichert", ex);
        }
    }

    private PluginConfig Config(string id)
    {
        var plugins = _settings!.Plugins;
        lock (plugins)
        {
            if (!plugins.TryGetValue(id, out var config))
            {
                config = new PluginConfig();
                plugins[id] = config;
            }
            return config;
        }
    }

    /// <summary>Eingeschaltet und allen verlangten Rechten zugestimmt (neue Rechte nach einem Update brauchen neue Zustimmung).</summary>
    private bool IsEnabled(PluginManifest manifest)
    {
        var config = Config(manifest.Id);
        return config.Enabled && manifest.Permissions.All(config.Granted.Contains);
    }

    // ───────── Aktionen ─────────

    public void OnAction(ActionInvocation invocation)
    {
        var action = invocation.ActionId;
        if (action.StartsWith("toggle:", StringComparison.Ordinal))
        {
            var id = action["toggle:".Length..];
            _ = SetEnabledAsync(id, invocation.Value is not 0);
            return;
        }
        if (action == "open-folder")
        {
            OpenFolder();
            return;
        }
        if (action == "open-library")
        {
            OpenLibrary?.Invoke();
            return;
        }
        // Aktionen aus Plugin-Inhalten: „<plugin>|<aktion>“
        var bar = action.IndexOf('|');
        if (bar <= 0)
        {
            return;
        }
        var target = Runners().FirstOrDefault(r => r.Id == action[..bar]);
        target?.Notify(PluginProtocol.ActionInvoked, new ActionParams(action[(bar + 1)..], invocation.Value));
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        var runner = Runners().FirstOrDefault(r => r.Id == id);
        if (runner is null || _settings is null)
        {
            return;
        }
        var config = Config(id);
        config.Enabled = enabled;
        if (enabled)
        {
            // Einschalten ist die Zustimmung zu genau den Rechten, die neben dem Schalter stehen.
            config.Granted = [.. runner.Manifest.Permissions];
        }
        Log.Info($"Plugin {id} {(enabled ? "eingeschaltet" : "ausgeschaltet")}");
        SaveSettings();
        if (enabled)
        {
            runner.ResetCrashes();
            await runner.StartAsync();
        }
        else
        {
            await runner.StopAsync();
        }
    }

    /// <summary>Neue Einstellungen: geänderte Werte an die Plugins weitergeben.</summary>
    public void ApplySettings(BrimSettings settings)
    {
        _settings = settings;
        foreach (var runner in Runners().Where(r => r.State == PluginState.Running))
        {
            runner.Notify(PluginProtocol.SettingsChanged, new SettingsParams(runner.Manifest.EffectiveSettings(Config(runner.Id).Values)));
        }
    }

    /// <summary>Aktionen laufender Plugins mit dem Recht „shelf“ für das Kontextmenü der Ablage.</summary>
    public IReadOnlyList<(string PluginId, string PluginName, ShelfActionDefinition Action)> ShelfActions() =>
        Runners()
            .Where(r => r.State == PluginState.Running && Config(r.Id).Granted.Contains("shelf"))
            .SelectMany(r => r.Manifest.ShelfActions.Select(a => (r.Id, r.Manifest.Name, a)))
            .ToList();

    /// <summary>Für die Ablage (Slot <c>shelf.action</c>): Dateien an ein Plugin mit dem Recht „shelf“ geben.</summary>
    public bool SendShelfItems(string pluginId, string actionId, IReadOnlyList<string> paths)
    {
        var runner = Runners().FirstOrDefault(r => r.Id == pluginId && r.State == PluginState.Running);
        if (runner is null || !Config(pluginId).Granted.Contains("shelf"))
        {
            return false;
        }
        runner.Notify(PluginProtocol.ShelfItems, new ShelfItemsParams(actionId, paths));
        return true;
    }

    public void OnExpandedChanged(bool expanded)
    {
        foreach (var runner in Runners())
        {
            runner.Notify(PluginProtocol.NotchExpanded, new ExpandedParams(expanded));
        }
    }

    /// <summary>
    /// Benachrichtigung an das Plugin geben, das ihre App in <c>notificationApps</c> nennt und das Recht hat.
    /// <c>true</c>, wenn ein laufendes Plugin sie übernimmt; Brim zeigt sie dann nicht selbst.
    /// </summary>
    public bool RouteNotification(string app, string title, string body)
    {
        var runner = Runners().FirstOrDefault(r =>
            r.State == PluginState.Running
            && Config(r.Id).Granted.Contains("notifications")
            && r.Manifest.NotificationApps.Any(a => app.Contains(a, StringComparison.OrdinalIgnoreCase)));
        if (runner is null)
        {
            return false;
        }
        runner.Notify(PluginProtocol.NotificationPosted, new NotificationParams(app, title, body));
        return true;
    }

    private void OpenFolder()
    {
        var folder = _directories.FirstOrDefault();
        if (folder is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Log.Warn("Plugin-Ordner lässt sich nicht öffnen", ex);
        }
    }

    // ───────── Verwaltung im Tab „Plugins“ ─────────

    public static string StateText(PluginState state, string? detail) => state switch
    {
        PluginState.Starting => "Startet …",
        PluginState.Running => "Läuft",
        PluginState.Restarting => "Abgestürzt, startet neu" + (detail is null ? "" : $" ({detail})"),
        PluginState.Failed => "Nach drei Abstürzen abgeschaltet" + (detail is null ? "" : $" ({detail})"),
        _ => "Aus",
    };

    private void Publish()
    {
        Changed?.Invoke();
        var context = _context;
        if (context is null)
        {
            return;
        }
        List<PluginRunner> runners;
        List<(string Directory, string Error)> broken;
        lock (_gate)
        {
            runners = [.. _runners];
            broken = [.. _broken];
        }

        var header = new StackWidget(
            new IconWidget(Glyphs.Puzzle) { Size = 14, Color = BrimColors.Slate },
            new TextWidget("Plugins") { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 },
            new ButtonWidget("open-library")
            {
                Kind = ButtonKind.Secondary,
                Glyph = Glyphs.Download,
                Label = "Bibliothek",
                Size = 26,
                GlyphSize = 12,
                AccessibleName = "Plugin-Bibliothek öffnen",
            },
            new ButtonWidget("open-folder")
            {
                Glyph = Glyphs.Folder,
                Size = 26,
                GlyphSize = 12,
                GlyphColor = BrimColors.Slate,
                AccessibleName = "Plugin-Ordner öffnen",
            })
        {
            Spacing = 8,
        };

        var rows = new List<Widget>();
        foreach (var runner in runners)
        {
            rows.Add(PluginRow(runner));
        }
        foreach (var (directory, error) in broken)
        {
            rows.Add(Row(
                new IconWidget(Glyphs.Puzzle) { Size = 16, Color = BrimColors.Danger },
                [
                    new TextWidget(Path.GetFileName(directory)),
                    new TextWidget("Fehlerhaft: " + error) { Style = TextStyle.Caption, Color = BrimColors.Danger, Wrap = true },
                ],
                null));
        }
        if (rows.Count == 0)
        {
            rows.Add(new TextWidget("Noch keine Plugins. Über „Bibliothek“ findest du welche.") { Style = TextStyle.Secondary, Wrap = true });
        }
        else
        {
            rows.Add(new TextWidget("Inhalte der Plugins findest du als Widgets auf „Jetzt“: „Jetzt“ lange drücken, dann „+ Widget“.")
            {
                Style = TextStyle.Caption,
                Wrap = true,
            });
        }

        // Volle Breite: Die Seite gehört allein der Verwaltung.
        context.SetView("manage", new StackWidget(
            header,
            new ListWidget(rows) { Spacing = 6, Scrollable = true, Grow = 1 })
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 10,
            Align = CrossAlign.Stretch,
            Background = BrimColors.Graphite,
            CornerRadius = 14,
            Padding = (14, 10, 10, 10),
            Grow = 1,
        }, NotchTab.Plugins, TabColumn.Main);
    }

    /// <summary>Eine Zeile: Name und Version, Zustand, Rechte, Widgets – rechts der Schalter.</summary>
    private Widget PluginRow(PluginRunner runner)
    {
        var manifest = runner.Manifest;
        var enabled = IsEnabled(manifest);
        var permissions = manifest.Permissions.Count == 0
            ? "Braucht keine Rechte"
            : "Darf: " + string.Join(", ", manifest.Permissions.Select(p => PluginManifest.KnownPermissions[p]));
        var stateColor = runner.State switch
        {
            PluginState.Running => BrimColors.Charge,
            PluginState.Failed => BrimColors.Danger,
            PluginState.Restarting => BrimColors.Warning,
            _ => BrimColors.Slate,
        };
        var lines = new List<Widget>
        {
            new StackWidget(
                new TextWidget(manifest.Name),
                new TextWidget(manifest.Version) { Style = TextStyle.MonoCaption, Color = BrimColors.Slate })
            {
                Spacing = 8,
            },
            new TextWidget($"{StateText(runner.State, runner.Detail)} · {permissions}") { Style = TextStyle.Caption, Color = stateColor, Wrap = true },
        };
        if (manifest.Widgets.Count > 0)
        {
            lines.Add(new TextWidget("Widgets: " + string.Join(", ", manifest.Widgets.Select(w => w.Name))) { Style = TextStyle.Caption, Wrap = true });
        }
        var toggle = new SwitchWidget("toggle:" + manifest.Id, enabled) { AccessibleName = $"{manifest.Name} einschalten. {permissions}" };
        return Row(new IconWidget(Glyphs.Puzzle) { Size = 16, Color = enabled ? BrimColors.Fog : BrimColors.Slate }, lines, toggle);
    }

    private static Widget Row(Widget icon, IReadOnlyList<Widget> lines, Widget? trailing)
    {
        var text = new StackWidget(lines)
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 1,
            Align = CrossAlign.Stretch,
            Grow = 1,
        };
        return new StackWidget(trailing is null ? [icon, text] : [icon, text, trailing])
        {
            Spacing = 12,
            Background = BrimColors.TabTrack,
            CornerRadius = 10,
            Padding = (12, 8, 10, 8),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(Runners().Select(r => r.StopAsync()));
    }
}
