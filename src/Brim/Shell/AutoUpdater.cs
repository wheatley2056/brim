using System.Text.Json;
using Brim.Core;
using Brim.Modules;
using Microsoft.UI.Dispatching;
using Velopack;
using Velopack.Sources;

namespace Brim.Shell;

/// <summary>
/// Unbemerkte Updates über Velopack aus den GitHub-Releases: im Hintergrund prüfen und laden,
/// anwenden erst, wenn niemand am Rechner arbeitet (keine Eingabe, Notch zu, keine Übertragung).
/// Nach dem Neustart zeigt <see cref="UpdateModule"/> den Changelog in der Notch.
/// </summary>
internal sealed class AutoUpdater : IDisposable
{
    public const string RepositoryUrl = "https://github.com/wheatley2056/brim";

    private readonly SettingsStore _settings;
    private readonly Func<bool> _isBusy;
    private readonly string _notesFile;
    private readonly CancellationTokenSource _cts = new();
    private readonly UpdateManager _manager;
    private VelopackAsset? _ready;

    /// <param name="isBusy">Wird gerade mit Brim gearbeitet (aufgeklappt, Übertragung)? Dann nicht neu starten.</param>
    public AutoUpdater(SettingsStore settings, string dataDirectory, Func<bool> isBusy)
    {
        _settings = settings;
        _isBusy = isBusy;
        _notesFile = UpdateModule.NotesFile(dataDirectory);

        // Für Tests: lokaler Ordner statt GitHub (BRIM_UPDATE_SOURCE).
        var local = Environment.GetEnvironmentVariable("BRIM_UPDATE_SOURCE");
        _manager = string.IsNullOrEmpty(local)
            ? new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false))
            : new UpdateManager(local);
    }

    /// <summary>Installierte Fassung? Entwicklungsbuilds aktualisieren sich nicht selbst.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    public void Start()
    {
        if (!_manager.IsInstalled)
        {
            Log.Info("Updates: Entwicklungsfassung, kein automatisches Update");
            return;
        }
        _ = LoopAsync();
    }

    private TimeSpan CheckInterval =>
        Seconds("BRIM_UPDATE_CHECK_SECONDS") ?? TimeSpan.FromHours(Math.Max(1, _settings.Current.Updates.CheckHours));

    private TimeSpan IdleNeeded =>
        Seconds("BRIM_UPDATE_IDLE_SECONDS") ?? TimeSpan.FromMinutes(Math.Max(1, _settings.Current.Updates.IdleMinutes));

    private static TimeSpan? Seconds(string variable) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out var s) ? TimeSpan.FromSeconds(s) : null;

    private async Task LoopAsync()
    {
        try
        {
            await Task.Delay(Seconds("BRIM_UPDATE_CHECK_SECONDS") ?? TimeSpan.FromMinutes(1), _cts.Token);
            while (!_cts.IsCancellationRequested)
            {
                if (_settings.Current.Updates.Enabled && _ready is null)
                {
                    await CheckAndDownloadAsync();
                }
                if (_ready is not null)
                {
                    await ApplyWhenIdleAsync(_ready);
                }
                await Task.Delay(CheckInterval, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckAndDownloadAsync()
    {
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return;
            }
            var target = update.TargetFullRelease;
            Log.Info($"Updates: {target.Version} gefunden, lade im Hintergrund");
            await _manager.DownloadUpdatesAsync(update, null, _cts.Token);
            _ready = target;
            Log.Info($"Updates: {target.Version} bereit, warte auf eine ruhige Minute");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Kein Netz, GitHub nicht erreichbar: still beim nächsten Mal erneut versuchen.
            Log.Info($"Updates: Prüfen fehlgeschlagen ({ex.Message})");
        }
    }

    /// <summary>Wartet minütlich auf einen ruhigen Moment und startet dann mit dem Update neu.</summary>
    private async Task ApplyWhenIdleAsync(VelopackAsset target)
    {
        var poll = TimeSpan.FromSeconds(Math.Min(60, Math.Max(2, IdleNeeded.TotalSeconds / 2)));
        var until = DateTime.UtcNow + CheckInterval;
        while (DateTime.UtcNow < until && !_cts.IsCancellationRequested)
        {
            if (Win32.IdleTime() >= IdleNeeded && !_isBusy())
            {
                Log.Info($"Updates: wende {target.Version} an");
                SaveNotes(target);
                _manager.ApplyUpdatesAndRestart(target, ["--updated"]);
                return; // beendet den Prozess
            }
            await Task.Delay(poll, _cts.Token);
        }
    }

    /// <summary>Changelog für den Hinweis nach dem Neustart aufheben.</summary>
    private void SaveNotes(VelopackAsset target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_notesFile)!);
            File.WriteAllText(_notesFile, JsonSerializer.Serialize(new UpdateNotes(target.Version.ToString(), target.NotesMarkdown ?? "")));
        }
        catch (Exception ex)
        {
            Log.Warn("Updates: Changelog ließ sich nicht sichern", ex);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
