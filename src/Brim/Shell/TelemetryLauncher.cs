using System.Diagnostics;
using Brim.Core;
using Brim.Modules;
using Brim.Telemetry;

namespace Brim.Shell;

/// <summary>Startet den Messdienst als zweiten Brim-Prozess (<c>Brim.exe --telemetry</c>), ohne Fenster.</summary>
internal sealed class TelemetryLauncher(string logDirectory) : ITelemetryLauncher
{
    private static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("Pfad von Brim.exe unbekannt");

    public ITelemetryProcess Start(IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            ArgumentList = { TelemetryHost.Argument },
        };
        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        Directory.CreateDirectory(logDirectory);
        var log = Path.Combine(logDirectory, "telemetry.log");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                try
                {
                    File.AppendAllText(log, line + Environment.NewLine);
                }
                catch (IOException)
                {
                }
            }
        };
        process.Start();
        Brim.Plugins.ChildProcessJob.Add(process);
        process.BeginErrorReadLine();
        return new Running(process);
    }

    public Task<bool> RequestPermissionAsync() => PerformanceLogUsers.RequestAsync(Executable);

    private sealed class Running : ITelemetryProcess
    {
        private readonly Process _process;

        public Running(Process process)
        {
            _process = process;
            _process.Exited += (_, _) => Exited?.Invoke();
        }

        public event Action? Exited;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public void Kill()
        {
            try
            {
                _process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Info($"Messdienst ließ sich nicht beenden: {ex.Message}");
            }
        }

        public void Dispose() => _process.Dispose();
    }
}
