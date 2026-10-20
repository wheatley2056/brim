using System.Diagnostics;
using Brim.Core;

namespace Brim.Plugins;

/// <summary>Ein laufender Plugin-Prozess.</summary>
public interface IPluginProcess : IDisposable
{
    /// <summary>Der Prozess hat sich beendet (auch nach <see cref="Kill"/>); von einem beliebigen Thread.</summary>
    event Action? Exited;

    bool HasExited { get; }

    void Kill();
}

/// <summary>Startet Plugins; in Tests durch eine Variante ersetzt, die das Plugin im selben Prozess laufen lässt.</summary>
public interface IPluginLauncher
{
    IPluginProcess Start(PluginManifest manifest, IReadOnlyDictionary<string, string> environment);
}

/// <summary>Startet das Plugin als eigenen Prozess ohne Fenster; Ausgaben landen in <c>logs\plugins\&lt;id&gt;.log</c>.</summary>
public sealed class ProcessPluginLauncher(string logDirectory) : IPluginLauncher
{
    /// <summary>Größe, ab der das Protokoll eines Plugins neu beginnt.</summary>
    private const long MaxLogBytes = 1024 * 1024;

    public IPluginProcess Start(PluginManifest manifest, IReadOnlyDictionary<string, string> environment)
    {
        var entry = manifest.ResolveEntry() ?? throw new FileNotFoundException($"entry {manifest.Entry} fehlt");
        var info = new ProcessStartInfo(entry)
        {
            WorkingDirectory = manifest.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in manifest.Args)
        {
            info.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, manifest.Id + ".log");
        if (File.Exists(logPath) && new FileInfo(logPath).Length > MaxLogBytes)
        {
            File.Delete(logPath);
        }
        var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        log.WriteLine($"── {DateTime.Now:yyyy-MM-dd HH:mm:ss} Start {manifest.Name} {manifest.Version} ──");

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var gate = new Lock();
        void Write(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (gate)
            {
                log.WriteLine(line);
            }
        }
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new Running(process, log, gate);
    }

    private sealed class Running : IPluginProcess
    {
        private readonly Process _process;
        private readonly StreamWriter _log;
        private readonly Lock _gate;

        public Running(Process process, StreamWriter log, Lock gate)
        {
            _process = process;
            _log = log;
            _gate = gate;
            _process.Exited += (_, _) =>
            {
                lock (_gate)
                {
                    try
                    {
                        _log.WriteLine($"── Ende (Code {_process.ExitCode}) ──");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                    {
                    }
                }
                Exited?.Invoke();
            };
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
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                Log.Info($"Plugin-Prozess ließ sich nicht beenden: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _process.Dispose();
            lock (_gate)
            {
                _log.Dispose();
            }
        }
    }
}
