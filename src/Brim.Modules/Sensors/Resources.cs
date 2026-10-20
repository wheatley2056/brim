using System.Diagnostics;
using System.Runtime.InteropServices;
using Brim.Core;

namespace Brim.Modules.Sensors;

/// <summary>Ein Prozess (gleichnamige zusammengefasst) mit seinem Anteil an der gesamten CPU in Prozent.</summary>
public sealed record ProcessUsage(string Name, double Cpu);

/// <summary>Eine Messung, Werte wie im Task-Manager. <c>null</c> = auf diesem System nicht messbar.</summary>
public sealed record ResourceSample(
    double? Cpu,
    ulong MemoryUsed,
    ulong MemoryTotal,
    double? Gpu,
    double NetworkDown,
    double NetworkUp,
    IReadOnlyList<ProcessUsage> TopProcesses);

public interface IResourceSampler : IDisposable
{
    /// <summary>Misst seit dem letzten Aufruf. Der erste Aufruf nach dem Start liefert für Raten noch keine Werte.</summary>
    ResourceSample Sample();
}

/// <summary>Rechenregeln, die der Task-Manager für zusammengesetzte Werte nutzt.</summary>
public static class ResourceMath
{
    /// <summary>
    /// GPU-Auslastung aus <c>\GPU Engine(*)\Utilization Percentage</c>: pro Engine (Adapter + Engine-Nummer)
    /// über alle Prozesse summiert, davon das Maximum – so zeigt es der Task-Manager.
    /// Instanznamen: <c>pid_1234_luid_0x00000000_0x0000C3B1_phys_0_eng_3_engtype_3D</c>.
    /// </summary>
    public static double? GpuUtilization(IEnumerable<(string Name, double Value)> engines)
    {
        var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in engines)
        {
            var start = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            var end = name.IndexOf("_engtype", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                continue;
            }
            var key = end > start ? name[start..end] : name[start..];
            perEngine[key] = perEngine.GetValueOrDefault(key) + value;
        }
        return perEngine.Count == 0 ? null : Math.Clamp(perEngine.Values.Max(), 0, 100);
    }

    /// <summary>
    /// Top-Prozesse aus <c>\Process(*)\% Processor Time</c>. Die Werte gelten pro Kern und werden auf die
    /// ganze CPU umgerechnet; gleichnamige Instanzen (<c>chrome</c>, <c>chrome#1</c>) zählen zusammen.
    /// </summary>
    public static IReadOnlyList<ProcessUsage> TopProcesses(IEnumerable<(string Name, double Value)> processes, int processorCount, int count)
    {
        var perName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in processes)
        {
            var hash = instance.IndexOf('#');
            var name = hash >= 0 ? instance[..hash] : instance;
            if (name is "_Total" or "Idle" or "" || value <= 0)
            {
                continue;
            }
            perName[name] = perName.GetValueOrDefault(name) + value;
        }
        var cores = Math.Max(1, processorCount);
        return perName
            .Select(p => new ProcessUsage(p.Key, Math.Clamp(p.Value / cores, 0, 100)))
            .OrderByDescending(p => p.Cpu)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();
    }
}

/// <summary>Misst über PDH und <c>GlobalMemoryStatusEx</c>.</summary>
internal sealed partial class PdhResourceSampler : IResourceSampler
{
    private readonly Lock _gate = new();
    private readonly Pdh.Query _query = new();
    private readonly Pdh.Counter? _cpu;
    private readonly Pdh.Counter? _gpu;
    private readonly Pdh.Counter? _received;
    private readonly Pdh.Counter? _sent;
    private readonly Pdh.Counter? _processes;
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);

    public PdhResourceSampler()
    {
        // „Processor Utility“ entspricht dem Task-Manager seit Windows 8 (berücksichtigt den Turbotakt).
        _cpu = _query.TryAdd(@"\Processor Information(_Total)\% Processor Utility")
            ?? _query.TryAdd(@"\Processor(_Total)\% Processor Time");
        _gpu = _query.TryAdd(@"\GPU Engine(*)\Utilization Percentage");
        _received = _query.TryAdd(@"\Network Interface(*)\Bytes Received/sec");
        _sent = _query.TryAdd(@"\Network Interface(*)\Bytes Sent/sec");
        _processes = _query.TryAdd(@"\Process(*)\% Processor Time");
        _query.Collect();
    }

    public ResourceSample Sample()
    {
        lock (_gate)
        {
            _query.Collect();
            var (used, total) = Memory();
            var top = _processes is null
                ? []
                : ResourceMath.TopProcesses(_processes.Values(), Environment.ProcessorCount, 3)
                    .Select(p => p with { Name = DisplayName(p.Name) })
                    .ToList();
            return new ResourceSample(
                _cpu?.Value() is { } cpu ? Math.Clamp(cpu, 0, 100) : null,
                used,
                total,
                _gpu is null ? null : ResourceMath.GpuUtilization(_gpu.Values()),
                _received?.Values().Sum(v => v.Value) ?? 0,
                _sent?.Values().Sum(v => v.Value) ?? 0,
                top);
        }
    }

    /// <summary>Anzeigename wie im Task-Manager (Dateibeschreibung), sonst der Prozessname.</summary>
    private string DisplayName(string processName)
    {
        if (_displayNames.TryGetValue(processName, out var cached))
        {
            return cached;
        }
        var name = processName.Length > 0 ? char.ToUpperInvariant(processName[0]) + processName[1..] : processName;
        try
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                var description = processes.Select(p =>
                {
                    try
                    {
                        return p.MainModule?.FileVersionInfo.FileDescription;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        // Erhöhte oder geschützte Prozesse: Name genügt.
                        return null;
                    }
                }).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
                if (description is { Length: <= 40 })
                {
                    name = description.Trim();
                }
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        _displayNames[processName] = name;
        return name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static (ulong Used, ulong Total) Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            Log.Warn("GlobalMemoryStatusEx fehlgeschlagen");
            return (0, 0);
        }
        return (status.TotalPhys - status.AvailPhys, status.TotalPhys);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _query.Dispose();
        }
    }
}
