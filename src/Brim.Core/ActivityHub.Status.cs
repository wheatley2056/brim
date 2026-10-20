using Brim.Core.Widgets;

namespace Brim.Core;

// Statuswerte für die ruhende Notch neben der Uhr (CPU/RAM, Netzwerk, Akku, Datum, Punkt für Benachrichtigungen).
// Module liefern sie, die Shell zeigt die eingeschalteten (NotchDisplaySettings).
public sealed partial class ActivityHub
{
    private readonly Dictionary<string, Widget> _status = new();

    /// <summary>Setzt einen Statuswert; <c>null</c> entfernt ihn. Gleicher Inhalt löst nichts aus.</summary>
    public void SetStatus(string moduleId, string id, Widget? content)
    {
        var key = ActivityKey.Of(moduleId, id);
        lock (_gate)
        {
            if (content is null ? !_status.Remove(key) : _status.TryGetValue(key, out var old) && old == content)
            {
                return;
            }
            if (content is not null)
            {
                _status[key] = content;
            }
        }
        Changed?.Invoke();
    }

    public Widget? Status(string key)
    {
        lock (_gate)
        {
            return _status.GetValueOrDefault(key);
        }
    }

    // Sieht gerade jemand die Werte? Versteckt, aufgeklappt, gesperrt oder Bildschirm aus: Module dürfen pausieren.
    private readonly Dictionary<object, bool> _idleShown = new(ReferenceEqualityComparer.Instance);
    private bool _screenAway;

    /// <summary><see cref="StatusVisible"/> hat sich geändert.</summary>
    public event Action? StatusVisibilityChanged;

    /// <summary>Irgendeine Notch zeigt gerade die ruhende Ansicht und der Bildschirm ist an und entsperrt.</summary>
    public bool StatusVisible
    {
        get
        {
            lock (_gate)
            {
                return StatusVisibleLocked;
            }
        }
    }

    private bool StatusVisibleLocked => !_screenAway && _idleShown.ContainsValue(true);

    /// <summary>Von jeder Notch: Zeigt sie gerade die ruhende Ansicht? <c>false</c> auch beim Schließen.</summary>
    public void NotifyIdleShown(object source, bool shown)
    {
        ArgumentNullException.ThrowIfNull(source);
        UpdateStatusVisibility(() => _idleShown[source] = shown);
    }

    /// <summary>Bildschirm aus oder Sitzung gesperrt (<c>true</c>), wieder da (<c>false</c>).</summary>
    public void SetScreenAway(bool away) => UpdateStatusVisibility(() => _screenAway = away);

    private void UpdateStatusVisibility(Action change)
    {
        bool before, after;
        lock (_gate)
        {
            before = StatusVisibleLocked;
            change();
            after = StatusVisibleLocked;
        }
        if (before == after)
        {
            return;
        }
        foreach (var handler in StatusVisibilityChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                Log.Error("Modul reagierte fehlerhaft auf die Sichtbarkeit der Statuswerte", ex);
            }
        }
    }
}
