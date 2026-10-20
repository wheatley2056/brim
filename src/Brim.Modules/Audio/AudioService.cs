using System.Collections.Concurrent;
using System.Diagnostics;
using Brim.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Brim.Modules.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>Eine App, die gerade Ton ausgibt (alle Sitzungen eines Prozesses zusammengefasst).</summary>
public sealed record AudioApp(string Key, string Name, float Volume, bool Muted, bool IsActive);

/// <summary>
/// Gemeinsamer Zugang zu Core Audio für Lautstärke-HUD und die Audio-Menüs:
/// Ausgabegeräte, Gesamtlautstärke und Lautstärke pro App. Alles über NAudio
/// (offizielle Core-Audio-Schnittstellen), nur das Umschalten des Standardgeräts über
/// <see cref="PolicyConfig"/>.
/// </summary>
/// <remarks>
/// Alle Core-Audio-Objekte leben auf einem eigenen MTA-Thread und werden nur dort benutzt.
/// Vorher entstanden sie auf dem UI-Thread; beim Umschalten des Ausgabegeräts baute die
/// Benachrichtigung von Windows sie unter einer Sperre neu auf, .NET leitete das an den UI-Thread
/// zurück, und der wartete auf dieselbe Sperre – Brim hing. Benachrichtigungen reihen deshalb nur
/// Arbeit ein und kehren sofort zurück (so verlangt es auch die Dokumentation von
/// <c>IMMNotificationClient</c>), Ereignisse gehen über den Threadpool hinaus, und wer auf ein
/// Ergebnis wartet, wartet höchstens <see cref="CallTimeout"/>.
/// </remarks>
public sealed class AudioService : IDisposable
{
    private const string SystemKey = "system";

    /// <summary>Länger wartet niemand auf den Audio-Thread; danach gilt der Ersatzwert.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Dictionary<uint, string> _names = new();
    private Thread? _thread;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private MMDevice? _device;
    private AudioEndpointVolume? _volume;

    /// <summary>Gesamtlautstärke oder Stummschaltung des Standardgeräts hat sich geändert (0..1, stumm).</summary>
    public event Action<float, bool>? MasterChanged;

    /// <summary>Geräte hinzugekommen, entfernt oder Standard gewechselt.</summary>
    public event Action? DevicesChanged;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Brim Audio" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        // Fehler hier (z. B. kein Audiodienst) landen im Protokoll; die Notch läuft ohne Audio weiter.
        Call(() =>
        {
            _enumerator = new MMDeviceEnumerator();
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
            _notifications.DeviceStateChanged += (_, _) => Raise(DevicesChanged);
            _notifications.DeviceAdded += (_, _) => Raise(DevicesChanged);
            _notifications.DeviceRemoved += (_, _) => Raise(DevicesChanged);
            AttachDefault();
            return true;
        }, false);
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.Warn("Audio-Aufruf fehlgeschlagen", ex);
            }
        }
    }

    /// <summary>Arbeit für den Audio-Thread einreihen, ohne zu warten.</summary>
    private void Post(Action work)
    {
        try
        {
            _queue.Add(work);
        }
        catch (InvalidOperationException)
        {
            // Schon beendet.
        }
    }

    /// <summary>Auf dem Audio-Thread ausführen und höchstens <see cref="CallTimeout"/> auf das Ergebnis warten.</summary>
    private T Call<T>(Func<T> work, T fallback)
    {
        if (Thread.CurrentThread == _thread)
        {
            return work();
        }
        if (_thread is null || _queue.IsAddingCompleted)
        {
            return fallback;
        }
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                result.SetResult(work());
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        });
        try
        {
            if (result.Task.Wait(CallTimeout))
            {
                return result.Task.Result;
            }
            Log.Warn("Audio antwortet nicht rechtzeitig");
        }
        catch (AggregateException ex)
        {
            Log.Warn("Audio-Aufruf fehlgeschlagen", ex.InnerException);
        }
        return fallback;
    }

    private void Do(Action work) => Call(() =>
    {
        work();
        return true;
    }, false);

    /// <summary>Ereignisse nie auf dem Thread einer Windows-Benachrichtigung auslösen: Empfänger dürfen warten.</summary>
    private static void Raise(Action? handler)
    {
        if (handler is null)
        {
            return;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Log.Warn("Audio-Ereignis fehlgeschlagen", ex);
            }
        });
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow != DataFlow.Render || e.Role != Role.Multimedia)
        {
            return;
        }
        Post(() =>
        {
            AttachDefault();
            Raise(DevicesChanged);
        });
    }

    private void AttachDefault()
    {
        if (_volume is not null)
        {
            _volume.OnVolumeNotification -= OnVolumeNotification;
        }
        _device?.Dispose();
        _device = null;
        _volume = null;
        if (_enumerator is null || !_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device))
        {
            Log.Info("Kein Audio-Ausgabegerät gefunden");
            return;
        }
        _device = device;
        _volume = device.AudioEndpointVolume;
        _volume.OnVolumeNotification += OnVolumeNotification;
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        var (volume, muted) = (data.MasterVolume, data.Muted);
        Raise(() => MasterChanged?.Invoke(volume, muted));
    }

    // ───────── Geräte ─────────

    public IReadOnlyList<AudioDevice> Devices() => Call(() =>
        {
            if (_enumerator is null)
            {
                return [];
            }
            var defaultId = _device?.ID;
            var result = new List<AudioDevice>();
            using var collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (var i = 0; i < collection.Count; i++)
            {
                using var device = collection[i];
                result.Add(new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultId));
            }
            return (IReadOnlyList<AudioDevice>)result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }, []);

    /// <summary>
    /// Schaltet das Standard-Ausgabegerät um, auf dem Audio-Thread; der Aufrufer wartet nicht.
    /// <c>false</c>, wenn Windows es ablehnt.
    /// </summary>
    public Task<bool> SetDefaultDeviceAsync(string deviceId)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                PolicyConfig.SetDefault(deviceId);
                result.SetResult(true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Ausgabegerät {deviceId} ließ sich nicht setzen", ex);
                result.SetResult(false);
            }
        });
        return result.Task;
    }

    // ───────── Gesamtlautstärke ─────────

    public (float Volume, bool Muted) Master =>
        Call(() => _volume is null ? (0f, true) : (_volume.MasterVolumeLevelScalar, _volume.Mute), (0f, true));

    public void SetMasterVolume(float value) => Do(() =>
        {
            if (_volume is null)
            {
                return;
            }
            _volume.MasterVolumeLevelScalar = Math.Clamp(value, 0, 1);
            if (_volume.Mute && value > 0)
            {
                _volume.Mute = false;
            }
        });

    public void ToggleMasterMute() => Do(() =>
        {
            if (_volume is not null)
            {
                _volume.Mute = !_volume.Mute;
            }
        });

    // ───────── Apps ─────────

    public IReadOnlyList<AudioApp> Apps() => Call(() =>
        {
            var groups = new Dictionary<string, (string Name, float Volume, bool Muted, bool Active)>();
            foreach (var session in Sessions())
            {
                var key = Key(session);
                var simple = session.SimpleAudioVolume;
                var active = session.State == AudioSessionState.AudioSessionStateActive;
                if (groups.TryGetValue(key, out var existing))
                {
                    groups[key] = (existing.Name, Math.Max(existing.Volume, simple.Volume), existing.Muted && simple.Mute, existing.Active || active);
                }
                else
                {
                    groups[key] = (NameOf(session), simple.Volume, simple.Mute, active);
                }
            }
            return (IReadOnlyList<AudioApp>)groups
                .Select(g => new AudioApp(g.Key, g.Value.Name, g.Value.Volume, g.Value.Muted, g.Value.Active))
                .OrderByDescending(a => a.IsActive)
                .ThenBy(a => a.Key == SystemKey)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }, []);

    public void SetAppVolume(string key, float value) => Do(() =>
        {
            foreach (var session in Sessions().Where(s => Key(s) == key))
            {
                session.SimpleAudioVolume.Volume = Math.Clamp(value, 0, 1);
                if (value > 0)
                {
                    session.SimpleAudioVolume.Mute = false;
                }
            }
        });

    public void ToggleAppMute(string key) => Do(() =>
        {
            var sessions = Sessions().Where(s => Key(s) == key).ToList();
            var mute = !sessions.All(s => s.SimpleAudioVolume.Mute);
            sessions.ForEach(s => s.SimpleAudioVolume.Mute = mute);
        });

    private List<AudioSessionControl> Sessions()
    {
        var result = new List<AudioSessionControl>();
        if (_device is null)
        {
            return result;
        }
        var manager = _device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session.State != AudioSessionState.AudioSessionStateExpired)
            {
                result.Add(session);
            }
        }
        return result;
    }

    private static string Key(AudioSessionControl session) =>
        session.IsSystemSoundsSession ? SystemKey : session.GetProcessID.ToString();

    private string NameOf(AudioSessionControl session)
    {
        if (session.IsSystemSoundsSession)
        {
            return "Systemklänge";
        }
        var pid = session.GetProcessID;
        if (_names.TryGetValue(pid, out var cached))
        {
            return cached;
        }
        var name = !string.IsNullOrWhiteSpace(session.DisplayName) && !session.DisplayName.StartsWith('@')
            ? session.DisplayName
            : ProcessName(pid);
        _names[pid] = name;
        return name;
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            try
            {
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description;
                }
            }
            catch (Exception)
            {
                // Kein Zugriff auf das Modul (andere Integritätsstufe): Prozessname genügt.
            }
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return $"Prozess {pid}";
        }
    }

    public void Dispose()
    {
        Post(() =>
        {
            if (_volume is not null)
            {
                _volume.OnVolumeNotification -= OnVolumeNotification;
            }
            _device?.Dispose();
            _notifications?.Dispose();
            _enumerator?.Dispose();
            _enumerator = null;
        });
        _queue.CompleteAdding();
        if (_thread is not null && Thread.CurrentThread != _thread)
        {
            _thread.Join(CallTimeout);
        }
    }
}
