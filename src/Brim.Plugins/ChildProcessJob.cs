using System.Diagnostics;
using System.Runtime.InteropServices;
using Brim.Core;

namespace Brim.Plugins;

/// <summary>
/// Hält alle Kindprozesse (Plugins, Messdienst) in einem Job-Objekt mit „beim Schließen beenden“. Endet Brim –
/// auch durch einen Absturz –, schließt Windows den Job und beendet sie mit; nichts läuft verwaist weiter.
/// </summary>
public static partial class ChildProcessJob
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private static readonly Lazy<nint> Job = new(Create);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nint MinimumWorkingSetSize;
        public nint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nint ProcessMemoryLimit;
        public nint JobMemoryLimit;
        public nint PeakProcessMemoryUsed;
        public nint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    private static nint Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }
        var job = CreateJobObject(0, null);
        if (job == 0)
        {
            Log.Warn($"Job-Objekt nicht angelegt (Fehler {Marshal.GetLastPInvokeError()})");
            return 0;
        }
        var limits = new ExtendedLimits { Basic = { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            Log.Warn($"Job-Objekt ohne „beim Schließen beenden“ (Fehler {Marshal.GetLastPInvokeError()})");
        }
        // Absichtlich nie geschlossen: Der Griff lebt so lange wie Brim und schließt sich mit dem Prozess.
        return job;
    }

    /// <summary>Prozess in den Job aufnehmen; scheitert es, läuft er trotzdem (nur ohne diese Absicherung).</summary>
    public static void Add(Process process)
    {
        var job = Job.Value;
        if (job == 0)
        {
            return;
        }
        try
        {
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                Log.Info($"Prozess {process.Id} nicht im Job (Fehler {Marshal.GetLastPInvokeError()})");
            }
        }
        catch (InvalidOperationException)
        {
            // Schon beendet.
        }
    }
}
