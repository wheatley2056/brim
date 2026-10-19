using Brim.Core;
using Brim.Modules;
using Brim.Modules.Audio;
using Brim.Modules.Nearby;
using Brim.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Brim;

public partial class App : Application
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim");

    private readonly ActivityHub _hub = new();
    private readonly AudioService _audio = new();
    private SettingsStore? _settings;
    private ModuleHost? _modules;
    private NotchWindowManager? _windows;
    private TrayIcon? _tray;
    private NearbyService? _nearby;

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
        await _modules.StartAsync([new ClockModule(), new MediaModule(), new AudioModule(_audio), nearbyModule], settings);

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

    private async void ExitApp()
    {
        Log.Info("Brim wird beendet");
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
