using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Brim.Telemetry;

/// <summary>
/// Der Messdienst: eine ETW-Echtzeitsitzung auf DXGI und D3D9 wie PresentMon, gezählt werden nur die
/// Presents des Prozesses, den Brim gerade beobachtet. Läuft als eigener Prozess, damit ETW-Rechte und
/// ein möglicher Absturz die Notch nicht berühren (Plan 3, Grundsätze).
/// </summary>
public static class TelemetryHost
{
    public const string Argument = "--telemetry";

    private const string SessionName = "Brim-Frames";

    /// <summary>Microsoft-Windows-DXGI: Present_Start (42), PresentMultiplaneOverlay_Start (55).</summary>
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");

    /// <summary>Microsoft-Windows-D3D9: Present_Start (1), für ältere Spiele.</summary>
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    public static int Run()
    {
        var pipe = Environment.GetEnvironmentVariable(PluginProtocol.PipeVariable);
        if (pipe is null)
        {
            return 2;
        }
        try
        {
            RunAsync(pipe).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunAsync(string pipeName)
    {
        await using var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(10_000);
        var connection = new JsonRpcConnection(stream);
        var counter = new FrameCounter();
        var gate = new Lock();
        var watched = 0;
        var lastEvent = DateTime.MinValue;
        var lastTimestamp = 0.0;
        var stop = new TaskCompletionSource();

        connection.NotificationHandler = (method, parameters) =>
        {
            switch (method)
            {
                case TelemetryProtocol.Watch when parameters?.Deserialize<WatchParams>(PluginJson.Options) is { } watch:
                    lock (gate)
                    {
                        if (watched != watch.Pid)
                        {
                            watched = watch.Pid;
                            counter.Clear();
                        }
                    }
                    break;
                case PluginProtocol.Shutdown:
                    stop.TrySetResult();
                    break;
            }
        };
        connection.Start();

        TraceEventSession? session = null;
        try
        {
            // Eine Sitzung von einem abgestürzten Vorgänger würde den Namen blockieren.
            TraceEventSession.GetActiveSession(SessionName)?.Stop(noThrow: true);
            session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableProvider(Dxgi, TraceEventLevel.Informational);
            session.EnableProvider(D3D9, TraceEventLevel.Informational);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is COMException com && com.HResult == unchecked((int)0x80070005)))
        {
            session?.Dispose();
            await connection.NotifyAsync(TelemetryProtocol.Status, new StatusParams(TelemetryState.Denied, ex.Message));
            await Task.WhenAny(stop.Task, connection.Completion);
            return;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            await connection.NotifyAsync(TelemetryProtocol.Status, new StatusParams(TelemetryState.Failed, ex.Message));
            await Task.WhenAny(stop.Task, connection.Completion);
            return;
        }

        using (session)
        {
            session.Source.AllEvents += e =>
            {
                var isPresent = (e.ProviderGuid == Dxgi && (int)e.ID is 42 or 55) || (e.ProviderGuid == D3D9 && (int)e.ID == 1);
                if (!isPresent)
                {
                    return;
                }
                lock (gate)
                {
                    if (e.ProcessID != watched)
                    {
                        return;
                    }
                    counter.Add(e.TimeStampRelativeMSec);
                    lastTimestamp = e.TimeStampRelativeMSec;
                    lastEvent = DateTime.UtcNow;
                }
            };
            var processing = Task.Run(() => session.Source.Process());
            await connection.NotifyAsync(TelemetryProtocol.Status, new StatusParams(TelemetryState.Running));

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (!stop.Task.IsCompleted && !connection.Completion.IsCompleted)
            {
                var tick = timer.WaitForNextTickAsync().AsTask();
                await Task.WhenAny(tick, stop.Task, connection.Completion);
                if (!tick.IsCompleted)
                {
                    break;
                }
                FramesParams frames;
                lock (gate)
                {
                    // Echtzeit-ETW liefert gepuffert, etwa einmal pro Sekunde. Deshalb zählt die Zeit des
                    // letzten Ereignisses; kam zwei Sekunden nichts, gibt der Prozess keine Bilder mehr aus.
                    var stale = DateTime.UtcNow - lastEvent > TimeSpan.FromSeconds(2);
                    frames = new FramesParams(watched, stale ? null : counter.Snapshot(lastTimestamp));
                }
                try
                {
                    await connection.NotifyAsync(TelemetryProtocol.Frames, frames);
                }
                catch (IOException)
                {
                    break;
                }
            }
            session.Stop(noThrow: true);
            await Task.WhenAny(processing, Task.Delay(1000));
        }
        await connection.DisposeAsync();
    }
}
