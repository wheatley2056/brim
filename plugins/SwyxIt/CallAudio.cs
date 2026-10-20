using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Brim.SwyxIt;

/// <summary>
/// Pegel des Gesprächs für die Wellenform: die Audio-Sitzungen von SwyxIt! auf Lautsprechern und Mikrofonen
/// (Core Audio, <c>IAudioMeterInformation</c>). So zeigt die Welle wirklich das Gespräch, nicht andere Töne.
/// </summary>
internal sealed class CallAudio : IDisposable
{
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(2);
    private readonly MMDeviceEnumerator _devices = new();
    private readonly List<AudioSessionControl> _sessions = [];
    private DateTime _scanned;

    /// <summary>Lautester Pegel (0–1) über alle Sitzungen von SwyxIt!; mit stummem Mikrofon nur der Lautsprecher.</summary>
    public double Peak(bool includeMicrophone)
    {
        if (DateTime.UtcNow - _scanned > Rescan)
        {
            Scan();
        }
        var peak = 0.0;
        foreach (var session in _sessions)
        {
            try
            {
                if (!includeMicrophone && session.IsCapture)
                {
                    continue;
                }
                peak = Math.Max(peak, session.Control.AudioMeterInformation.MasterPeakValue);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
            {
                // Sitzung inzwischen weg; beim nächsten Durchgang neu suchen.
                _scanned = DateTime.MinValue;
            }
        }
        return peak;
    }

    private sealed record AudioSessionControl(NAudio.CoreAudioApi.AudioSessionControl Control, bool IsCapture);

    private void Scan()
    {
        _scanned = DateTime.UtcNow;
        _sessions.Clear();
        var swyx = Process.GetProcessesByName("SwyxIt!").Select(p =>
        {
            using (p)
            {
                return (uint)p.Id;
            }
        }).ToHashSet();
        if (swyx.Count == 0)
        {
            return;
        }
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            try
            {
                foreach (var device in _devices.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        if (swyx.Contains(sessions[i].GetProcessID))
                        {
                            _sessions.Add(new AudioSessionControl(sessions[i], flow == DataFlow.Capture));
                        }
                    }
                }
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Console.Error.WriteLine($"Audio nicht lesbar: {ex.Message}");
            }
        }
    }

    public void Dispose() => _devices.Dispose();
}
