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
public sealed class AudioService : IDisposable
{
    private const string SystemKey = "system";

    private readonly Lock _gate = new();
    private readonly Dictionary<uint, string> _names = new();
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
        lock (_gate)
        {
            _enumerator = new MMDeviceEnumerator();
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
            _notifications.DeviceStateChanged += (_, _) => DevicesChanged?.Invoke();
            _notifications.DeviceAdded += (_, _) => DevicesChanged?.Invoke();
            _notifications.DeviceRemoved += (_, _) => DevicesChanged?.Invoke();
            AttachDefault();
        }
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow != DataFlow.Render || e.Role != Role.Multimedia)
        {
            return;
        }
        lock (_gate)
        {
            AttachDefault();
        }
        DevicesChanged?.Invoke();
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

    private void OnVolumeNotification(AudioVolumeNotificationData data) => MasterChanged?.Invoke(data.MasterVolume, data.Muted);

    // ───────── Geräte ─────────

    public IReadOnlyList<AudioDevice> Devices()
    {
        lock (_gate)
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
            return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>Schaltet das Standard-Ausgabegerät um. <c>false</c>, wenn Windows es ablehnt.</summary>
    public bool SetDefaultDevice(string deviceId)
    {
        try
        {
            PolicyConfig.SetDefault(deviceId);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Ausgabegerät {deviceId} ließ sich nicht setzen", ex);
            return false;
        }
    }

    // ───────── Gesamtlautstärke ─────────

    public (float Volume, bool Muted) Master
    {
        get
        {
            lock (_gate)
            {
                return _volume is null ? (0, true) : (_volume.MasterVolumeLevelScalar, _volume.Mute);
            }
        }
    }

    public void SetMasterVolume(float value)
    {
        lock (_gate)
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
        }
    }

    public void ToggleMasterMute()
    {
        lock (_gate)
        {
            if (_volume is not null)
            {
                _volume.Mute = !_volume.Mute;
            }
        }
    }

    // ───────── Apps ─────────

    public IReadOnlyList<AudioApp> Apps()
    {
        lock (_gate)
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
            return groups
                .Select(g => new AudioApp(g.Key, g.Value.Name, g.Value.Volume, g.Value.Muted, g.Value.Active))
                .OrderByDescending(a => a.IsActive)
                .ThenBy(a => a.Key == SystemKey)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public void SetAppVolume(string key, float value)
    {
        lock (_gate)
        {
            foreach (var session in Sessions().Where(s => Key(s) == key))
            {
                session.SimpleAudioVolume.Volume = Math.Clamp(value, 0, 1);
                if (value > 0)
                {
                    session.SimpleAudioVolume.Mute = false;
                }
            }
        }
    }

    public void ToggleAppMute(string key)
    {
        lock (_gate)
        {
            var sessions = Sessions().Where(s => Key(s) == key).ToList();
            var mute = !sessions.All(s => s.SimpleAudioVolume.Mute);
            sessions.ForEach(s => s.SimpleAudioVolume.Mute = mute);
        }
    }

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
        lock (_gate)
        {
            if (_volume is not null)
            {
                _volume.OnVolumeNotification -= OnVolumeNotification;
            }
            _device?.Dispose();
            _notifications?.Dispose();
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }
}
