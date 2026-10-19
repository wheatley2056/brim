using System.Diagnostics;

namespace Brim.Core;

/// <summary>Schlichtes Protokoll nach %LocalAppData%\Brim\logs\brim.log und in die Debug-Ausgabe.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static string? _path;

    public static void Initialize(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "brim.log");
        // Einfache Rotation: ab 1 MB neu beginnen.
        try
        {
            var info = new FileInfo(_path);
            if (info.Exists && info.Length > 1_000_000)
            {
                File.Move(_path, Path.Combine(directory, "brim.old.log"), overwrite: true);
            }
        }
        catch (IOException)
        {
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("FAIL", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{(ex is null ? "" : Environment.NewLine + ex)}";
        Debug.WriteLine(line);
        if (_path is null)
        {
            return;
        }
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }
}
