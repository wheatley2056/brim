using Brim.Core;
using Brim.Modules;
using Brim.Modules.Audio;
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

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _windows = new NotchWindowManager(_hub, _settings, dispatcher);
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
        await _modules.StartAsync([new ClockModule(), new MediaModule(), new AudioModule(_audio)], settings);
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
        Exit();
    }
}
