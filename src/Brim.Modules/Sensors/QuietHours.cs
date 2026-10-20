using System.Runtime.InteropServices;

namespace Brim.Modules.Sensors;

/// <summary>
/// Will Windows gerade nicht stören? Präsentation, Vollbild-Spiel oder -Video (SHQueryUserNotificationState) und
/// „Nicht stören“/Fokus-Assistent. Für Letzteres gibt es keine öffentliche Schnittstelle; Brim liest denselben
/// Zustand wie das Info-Center (WNF), und wenn das scheitert, gilt er als aus.
/// </summary>
internal static partial class QuietHours
{
    // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE, QUNS_QUIET_TIME
    private const int Busy = 2, Direct3D = 3, Presentation = 4, QuietTime = 6;

    /// <summary>WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED: 0 = aus, 1 = nur Priorität, 2 = nur Wecker.</summary>
    private const ulong QuietHoursProfile = 0x0D83063EA3BF1C75;

    private static bool _wnfBroken;

    public static bool IsActive() => IsBusy() || IsDoNotDisturb();

    private static bool IsBusy()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && state is Busy or Direct3D or Presentation or QuietTime;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool IsDoNotDisturb()
    {
        if (_wnfBroken)
        {
            return false;
        }
        try
        {
            var name = QuietHoursProfile;
            uint size = sizeof(uint);
            return NtQueryWnfStateData(ref name, 0, 0, out _, out var profile, ref size) == 0 && size >= sizeof(uint) && profile != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _wnfBroken = true;
            return false;
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryWnfStateData(ref ulong stateName, nint typeId, nint explicitScope, out uint changeStamp, out uint buffer, ref uint bufferSize);
}
