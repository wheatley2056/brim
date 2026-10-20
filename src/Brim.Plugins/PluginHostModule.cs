using System.Diagnostics;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.Plugins;

/// <summary>
/// Plugin-Host (Plan 4.2) als Modul: findet Plugins in den Plugin-Ordnern, startet die eingeschalteten
/// und zeigt im Tab „Plugins“ rechts die Verwaltung. Einschalten heißt zugleich, den im Manifest
/// genannten Rechten zuzustimmen – die Karte nennt sie direkt neben dem Schalter.
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
                if (!File.Exists(Path.Combine(dir, PluginManifest.FileName)))
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
                var id = found[i].Id;
                _runners.Add(new PluginRunner(found[i], _context!, _launcher, () => Config(id), i, Publish));
            }
        }
        Log.Info($"{found.Count} Plugin(s) gefunden");
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
        try
        {
            _save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Einstellungen nicht gespeichert", ex);
        }
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

    public void OnTabVisibilityChanged(NotchTab tab, bool visible)
    {
        if (tab != NotchTab.Plugins)
        {
            return;
        }
        foreach (var runner in Runners())
        {
            runner.SetTabVisible(visible);
        }
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

        var rows = new List<Widget>
        {
            new StackWidget(
                new TextWidget("Plugins") { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 },
                new ButtonWidget("open-folder")
                {
                    Glyph = Glyphs.Folder,
                    Size = 22,
                    GlyphSize = 11,
                    GlyphColor = BrimColors.Slate,
                    AccessibleName = "Plugin-Ordner öffnen",
                }),
        };
        foreach (var runner in runners)
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
            rows.Add(new StackWidget(
                new StackWidget(
                    new TextWidget(manifest.Name) { Grow = 1 },
                    new SwitchWidget("toggle:" + manifest.Id, enabled) { AccessibleName = $"{manifest.Name} einschalten. {permissions}" })
                {
                    Spacing = 6,
                },
                new TextWidget(StateText(runner.State, runner.Detail)) { Style = TextStyle.Caption, Color = stateColor },
                new TextWidget(permissions) { Style = TextStyle.Caption })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 1,
                Align = CrossAlign.Stretch,
                AccessibleName = $"{manifest.Name} {manifest.Version}",
            });
        }
        foreach (var (directory, error) in broken)
        {
            rows.Add(new StackWidget(
                new TextWidget(Path.GetFileName(directory)) { Grow = 0 },
                new TextWidget("Fehlerhaft: " + error) { Style = TextStyle.Caption, Color = BrimColors.Danger, Wrap = true })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 1,
                Align = CrossAlign.Stretch,
            });
        }
        if (runners.Count == 0 && broken.Count == 0)
        {
            rows.Add(new TextWidget("Noch keine Plugins. Lege sie im Plugin-Ordner ab und starte Brim neu.") { Style = TextStyle.Secondary, Wrap = true });
        }

        context.SetView("manage", new StackWidget(
            new ListWidget(rows) { Spacing = 8, Scrollable = true, Grow = 1 })
        {
            Orientation = StackOrientation.Vertical,
            Align = CrossAlign.Stretch,
            Background = BrimColors.Graphite,
            CornerRadius = 14,
            Padding = (12, 8, 8, 8),
            Grow = 1,
        }, NotchTab.Plugins, TabColumn.Side);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(Runners().Select(r => r.StopAsync()));
    }
}
