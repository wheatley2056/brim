using Brim.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Velopack;

namespace Brim;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Velopack zuerst: Installieren, Aktualisieren und Deinstallieren rufen Brim kurz mit eigenen
        // Argumenten auf; diese Haken laufen ohne Oberfläche und beenden den Prozess selbst.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => Autostart.Enable())
            .OnAfterUpdateFastCallback(_ => Autostart.Enable())
            .OnBeforeUninstallFastCallback(_ => Autostart.Disable())
            .Run();

        // Messdienst für FPS (Plan 4.8) und seine einmalige Einrichtung: derselbe Prozess, ohne Oberfläche.
        if (args is [Brim.Telemetry.TelemetryHost.Argument])
        {
            return Brim.Telemetry.TelemetryHost.Run();
        }
        if (args is [Brim.Telemetry.PerformanceLogUsers.Argument, var user])
        {
            return Brim.Telemetry.PerformanceLogUsers.Add(user);
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Nur eine Instanz: eine zweite meldet sich bei der ersten und beendet sich.
        var instance = AppInstance.FindOrRegisterForKey(App.IsDemo ? "Brim.Demo" : "Brim.Main");
        if (!instance.IsCurrent)
        {
            instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().Wait();
            return 0;
        }

        Application.Start(_p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
