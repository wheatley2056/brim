// SwyxIt!-Plugin für Brim: Brim startet es (BRIM_PIPE), es verbindet sich mit dem laufenden SwyxIt!.
// COM braucht einen STA-Thread mit Nachrichtenschleife; dafür sorgt die WinForms-Schleife ohne Fenster.
using System.Windows.Forms;
using Brim.Sdk;
using Brim.SwyxIt;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        BrimPlugin brim;
        try
        {
            brim = BrimPlugin.ConnectAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Keine Verbindung zu Brim: {ex.Message}");
            return 1;
        }

        var sta = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(sta);
        var bridge = new SwyxBridge(brim, sta);
        // Beendet Brim das Plugin, endet die Schleife.
        _ = brim.Completion.ContinueWith(_ => sta.Post(_ => Application.ExitThread(), null), TaskScheduler.Default);

        using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => bridge.Tick();
        timer.Start();
        bridge.Start();
        Application.Run();

        bridge.Dispose();
        brim.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        return 0;
    }
}
