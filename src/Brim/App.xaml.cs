using Brim.Core;
using Brim.Modules;
using Brim.Modules.Audio;
using Brim.Modules.Nearby;
using Brim.Plugins;
using Brim.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Brim;

public partial class App : Application
{
    /// <summary>Vorführmodus für README-Bilder: eigener Datenordner, Beispieltitel, kein Netz, keine Updates.</summary>
    public static string? DemoDirectory { get; } = Environment.GetEnvironmentVariable("BRIM_DEMO") is { Length: > 0 } d ? Path.GetFullPath(d) : null;

    public static bool IsDemo => DemoDirectory is not null;

    public static string DataDirectory { get; } = DemoDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim");

    private readonly ActivityHub _hub = new();
    private readonly AudioService _audio = new();
    private SettingsStore? _settings;
    private ModuleHost? _modules;
    private NotchWindowManager? _windows;
    private TrayIcon? _tray;
    private NearbyService? _nearby;
    private AutoUpdater? _updater;
    private PluginHostModule? _plugins;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("Unbehandelter Fehler", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unbehandelter Fehler (AppDomain)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("Unbeobachtete Task-Ausnahme", e.Exception);
            e.SetObserved();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Initialize(Path.Combine(DataDirectory, "logs"));
        Log.Info("Brim startet");

        _settings = new SettingsStore(Path.Combine(DataDirectory, "settings.json"));
        var settings = _settings.Load();
        if (!File.Exists(Path.Combine(DataDirectory, "settings.json")))
        {
            // Standardwerte einmal schreiben, damit man sie findet und anpassen kann.
            _settings.Save(settings);
        }

        var shelf = new ShelfStore(DataDirectory, ShelfStore.DefaultTemporaryRoots());
        shelf.Load();
        if (IsDemo && shelf.Items.Count == 0 && Directory.Exists(Path.Combine(DataDirectory, "files")))
        {
            shelf.Add(Directory.EnumerateFileSystemEntries(Path.Combine(DataDirectory, "files")).Order(), ShelfStorage.Reference);
        }

        _nearby = new NearbyService(shelf);
        var nearbyModule = new NearbyModule(_nearby, () => _settings.Current.Share);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        nearbyModule.CopyToClipboard = text =>
        {
            var done = new TaskCompletionSource();
            dispatcher.TryEnqueue(() =>
            {
                try
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(text);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                    Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                }
                catch (Exception ex)
                {
                    Log.Warn("Zwischenablage nicht verfügbar", ex);
                }
                done.TrySetResult();
            });
            return done.Task;
        };
        _windows = new NotchWindowManager(_hub, _settings, shelf, nearbyModule, dispatcher);
        _windows.Build();

        _tray = new TrayIcon(_settings, ExitApp, () => _windows.ToggleExpanded());

        // Eine zweite gestartete Instanz klappt die Notch auf, statt eine weitere zu öffnen.
        AppInstance.GetCurrent().Activated += (_, _) => dispatcher.TryEnqueue(() => _windows?.ToggleExpanded());

        try
        {
            _audio.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Audio nicht verfügbar", ex);
        }
        _modules = new ModuleHost(_hub);
        // Plugins (Plan 4.2): eigene Prozesse, Ordner %LocalAppData%\Brim\Plugins plus eigene Ordner aus den Einstellungen.
        _plugins = new PluginHostModule(
            [Path.Combine(DataDirectory, "Plugins"), .. settings.PluginDirectories],
            new ProcessPluginLauncher(Path.Combine(DataDirectory, "logs", "plugins")),
            s => _settings.Save(s));
        _settings.Changed += s => _plugins.ApplySettings(s);
        // Unbemerkte Updates (nur installierte Fassung); kein Neustart, solange jemand arbeitet oder überträgt.
        _updater = new AutoUpdater(_settings, DataDirectory, () =>
            _windows.IsBusy || _hub.Snapshot().Any(a => a.Activity.ModuleId == NearbyModule.ModuleId && a.Priority >= ActivityPriority.Ongoing));
        Log.Info($"Brim {_updater.CurrentVersion ?? "Entwicklungsfassung"}");

        await _modules.StartAsync(
            [.. CreateModules(nearbyModule)],
            settings);
        if (IsDemo)
        {
            return;
        }
        _updater.Start();

        // Teilen im LAN: Identität, Empfang und Suche (Plan 4.4a). Fehler hier dürfen die Notch nicht stören.
        try
        {
            await _nearby.StartAsync(DataDirectory);
        }
        catch (Exception ex)
        {
            Log.Error("Teilen im Netz nicht verfügbar", ex);
        }
    }

    private IEnumerable<IBrimModule> CreateModules(NearbyModule nearbyModule)
    {
        yield return new ClockModule();
        if (!IsDemo)
        {
            yield return new MediaModule();
        }
        else if (File.Exists(Path.Combine(DataDirectory, "cover.png")))
        {
            // Vorführmodus: Beispieltitel nur, wenn ein Cover bereitliegt; sonst Leerlauf mit Uhr.
            yield return new MediaModule(new DemoTrack("Midnight Signal", "Neon Harbor", "Spotify",
                TimeSpan.FromSeconds(83), TimeSpan.FromSeconds(214), Path.Combine(DataDirectory, "cover.png")));
        }
        yield return new AudioModule(_audio);
        if (!IsDemo)
        {
            // System (Phase 2): Akku und Helligkeit nur, wo es sie gibt; Ressourcen messen nur bei offenem Tab.
            yield return new BatteryModule();
            yield return new BrightnessModule();
            yield return new PrivacyModule();
            yield return new GamesModule(new TelemetryLauncher(Path.Combine(DataDirectory, "logs")));
        }
        yield return new ResourcesModule();
        yield return nearbyModule;
        if (!IsDemo)
        {
            yield return _plugins!;
        }
        yield return new UpdateModule(DataDirectory, IsDemo ? null : _updater!.CurrentVersion);
    }

    private async void ExitApp()
    {
        Log.Info("Brim wird beendet");
        _updater?.Dispose();
        _tray?.Dispose();
        _windows?.Dispose();
        if (_modules is not null)
        {
            await _modules.DisposeAsync();
        }
        _audio.Dispose();
        if (_nearby is not null)
        {
            await _nearby.DisposeAsync();
        }
        Exit();
    }
}
