using Brim.Core;

namespace Brim.Shell;

/// <summary>
/// Schutz vor Abstürzen im System beim Material als Vektorform (<see cref="MaterialLayer"/>): Ein Absturz im
/// Compositor lässt sich nicht abfangen. Deshalb liegt während des Versuchs eine Markierung im Datenordner; erst
/// wenn das Material ein paar Sekunden ohne Absturz läuft, verschwindet sie. Findet Brim sie beim nächsten Start,
/// ist es beim letzten Mal dabei abgestürzt und nimmt gleich den alten Weg über die Fensterregion.
/// </summary>
internal static class VectorMaterialGuard
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
    private static readonly string Marker = Path.Combine(App.DataDirectory, "material-vector.trying");
    private static bool? _blocked;
    private static int _pending;

    /// <summary>Beim letzten Versuch abgestürzt: für diese Sitzung gesperrt.</summary>
    public static bool Blocked
    {
        get
        {
            if (_blocked is null)
            {
                _blocked = File.Exists(Marker);
                if (_blocked == true)
                {
                    Log.Warn("Material: Brim ist beim letzten Versuch mit der Vektorform abgestürzt, nehme die Fensterregion. "
                        + $"Zum erneuten Versuch {Marker} löschen.");
                }
            }
            return _blocked.Value;
        }
    }

    public static void Begin()
    {
        _pending++;
        Try(() => File.WriteAllText(Marker, DateTimeOffset.Now.ToString("O")));
    }

    /// <summary>Eingebunden; nach einer Weile ohne Absturz gilt der Versuch als gelungen.</summary>
    public static void Attached() => _ = Task.Delay(Settle).ContinueWith(_ => End(), TaskScheduler.Default);

    public static void End()
    {
        if (Interlocked.Decrement(ref _pending) <= 0)
        {
            _pending = 0;
            Try(() => File.Delete(Marker));
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (IOException ex)
        {
            Log.Warn("Material: Markierung nicht schreibbar", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn("Material: Markierung nicht schreibbar", ex);
        }
    }
}
