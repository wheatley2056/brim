using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Brim.Modules.Sensors;

/// <summary>Das Fenster im Vordergrund: Prozess und ob es seinen Monitor ganz füllt (Spiel, Video).</summary>
public sealed record ForegroundApp(int Pid, string Process, bool Fullscreen);

public interface IForegroundSource
{
    ForegroundApp? Read();
}

internal sealed class ForegroundSource : IForegroundSource
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd" };
    private readonly Dictionary<int, string> _names = new();

    public unsafe ForegroundApp? Read()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        if (hwnd.IsNull)
        {
            return null;
        }
        var buffer = stackalloc char[64];
        var length = PInvoke.GetClassName(hwnd, buffer, 64);
        if (ShellClasses.Contains(new string(buffer, 0, length)))
        {
            return null;
        }
        uint pid;
        _ = PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (pid == 0 || pid == Environment.ProcessId)
        {
            return null;
        }

        var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        var fullscreen = PInvoke.GetMonitorInfo(monitor, &info) && PInvoke.GetWindowRect(hwnd, out var rect)
            && rect.left <= info.rcMonitor.left && rect.top <= info.rcMonitor.top
            && rect.right >= info.rcMonitor.right && rect.bottom >= info.rcMonitor.bottom;
        return new ForegroundApp((int)pid, Name((int)pid), fullscreen);
    }

    private string Name(int pid)
    {
        if (_names.TryGetValue(pid, out var name))
        {
            return name;
        }
        if (_names.Count > 128)
        {
            _names.Clear();
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
            try
            {
                if (process.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 and <= 40 } description)
                {
                    name = description.Trim();
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Geschützte Spiele (Anti-Cheat) verweigern den Blick ins Modul; Prozessname genügt.
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            name = "";
        }
        _names[pid] = name;
        return name;
    }
}
