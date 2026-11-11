using Brim.Core;
using Windows.UI.Shell;

namespace Brim.Modules.Sensors;

/// <summary>
/// Fokus-Sitzung von Windows 11 (Uhr-App, Benachrichtigungscenter) über <see cref="FocusSessionManager"/>.
/// Gibt es erst ab Windows 11 (22H2); davor ist <see cref="IsSupported"/> falsch und alles andere tut nichts.
/// Die Restzeit kennt Brim nur für Sitzungen, die es selbst gestartet hat.
/// </summary>
public sealed class Focus : IDisposable
{
    /// <summary>So lange dauert eine Sitzung, die Brim startet (wie die Vorgabe der Uhr-App).</summary>
    public static readonly TimeSpan DefaultLength = TimeSpan.FromMinutes(30);

    private static readonly Lazy<Focus> SharedInstance = new(() => new Focus());

    /// <summary>Eine Instanz für Kachel und Kopfzeile, damit beide dieselbe Restzeit kennen.</summary>
    public static Focus Shared => SharedInstance.Value;

    private readonly FocusSessionManager? _manager;
    private DateTimeOffset? _end;

    private Focus()
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
                && Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Shell.FocusSessionManager")
                && FocusSessionManager.IsSupported)
            {
                _manager = FocusSessionManager.GetDefault();
                _manager.IsFocusActiveChanged += OnChanged;
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Fokus-Sitzung nicht verfügbar: {ex.Message}");
            _manager = null;
        }
    }

    /// <summary>Kommt von einem beliebigen Thread.</summary>
    public event Action? Changed;

    public bool IsSupported => _manager is not null;

    public bool IsActive
    {
        get
        {
            try
            {
                return _manager?.IsFocusActive == true;
            }
            catch (Exception ex)
            {
                Log.Warn("Fokus-Sitzung nicht lesbar", ex);
                return false;
            }
        }
    }

    /// <summary>Restzeit einer von Brim gestarteten Sitzung, sonst <c>null</c>.</summary>
    public TimeSpan? Remaining => IsActive && _end is { } end && end > DateTimeOffset.Now ? end - DateTimeOffset.Now : null;

    /// <summary>Ende einer von Brim gestarteten Sitzung, sonst <c>null</c>.</summary>
    public DateTimeOffset? End => IsActive ? _end : null;

    public void Toggle()
    {
        if (_manager is not { } manager)
        {
            return;
        }
        try
        {
            if (manager.IsFocusActive)
            {
                manager.DeactivateFocus();
                _end = null;
            }
            else
            {
                var end = DateTimeOffset.Now + DefaultLength;
                if (manager.TryStartFocusSession(end) is not null)
                {
                    _end = end;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Fokus-Sitzung ließ sich nicht schalten", ex);
        }
        Changed?.Invoke();
    }

    private void OnChanged(FocusSessionManager sender, object args)
    {
        if (!IsActive)
        {
            _end = null;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_manager is { } manager)
        {
            manager.IsFocusActiveChanged -= OnChanged;
        }
    }
}
