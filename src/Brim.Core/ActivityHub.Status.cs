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
}
