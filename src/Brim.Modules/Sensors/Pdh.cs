using System.Runtime.InteropServices;

namespace Brim.Modules.Sensors;

/// <summary>
/// Leistungsindikatoren (PDH), dieselbe Quelle wie der Task-Manager (Plan 4.7).
/// Englische Pfade über <c>PdhAddEnglishCounter</c>, damit es auf jeder Systemsprache funktioniert.
/// </summary>
internal static partial class Pdh
{
    public const uint FmtDouble = 0x00000200;
    /// <summary>Werte über 100 nicht kappen (wir kappen selbst, nachdem summiert wurde).</summary>
    public const uint FmtNoCap100 = 0x00008000;
    private const uint MoreData = 0x800007D2;
    private const uint ValidData = 0;
    private const uint NewData = 1;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValueItem
    {
        public nint Name;
        public CounterValue Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

    /// <summary>Eine Abfrage mit mehreren Zählern. Nicht threadsicher; der Aufrufer serialisiert.</summary>
    public sealed class Query : IDisposable
    {
        private nint _handle;

        public Query()
        {
            Check(PdhOpenQuery(null, 0, out _handle), "PdhOpenQuery");
        }

        /// <summary>Fügt einen Zähler hinzu; <c>null</c>, wenn es ihn auf diesem System nicht gibt (z. B. GPU in einer VM).</summary>
        public Counter? TryAdd(string path)
        {
            var result = PdhAddEnglishCounter(_handle, path, 0, out var counter);
            if (result != 0)
            {
                Core.Log.Info($"Leistungsindikator nicht verfügbar: {path} (0x{result:X8})");
                return null;
            }
            return new Counter(counter);
        }

        public void Collect() => PdhCollectQueryData(_handle);

        public void Dispose()
        {
            if (_handle != 0)
            {
                PdhCloseQuery(_handle);
                _handle = 0;
            }
        }
    }

    public sealed class Counter(nint handle)
    {
        /// <summary>Einzelwert; <c>null</c>, solange erst eine Messung vorliegt oder der Wert ungültig ist.</summary>
        public double? Value()
        {
            if (PdhGetFormattedCounterValue(handle, FmtDouble | FmtNoCap100, out _, out var value) != 0
                || value.CStatus is not (ValidData or NewData))
            {
                return null;
            }
            return value.DoubleValue;
        }

        /// <summary>Alle Instanzen eines Platzhalter-Zählers (<c>(*)</c>) mit Namen.</summary>
        public IReadOnlyList<(string Name, double Value)> Values()
        {
            uint size = 0;
            var result = PdhGetFormattedCounterArray(handle, FmtDouble | FmtNoCap100, ref size, out _, 0);
            if (result != MoreData || size == 0)
            {
                return [];
            }
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(handle, FmtDouble | FmtNoCap100, ref size, out var count, buffer) != 0)
                {
                    return [];
                }
                var items = new List<(string, double)>((int)count);
                var stride = Marshal.SizeOf<CounterValueItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterValueItem>(buffer + i * stride);
                    if (item.Value.CStatus is ValidData or NewData)
                    {
                        items.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.DoubleValue));
                    }
                }
                return items;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static void Check(uint result, string what)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{what} fehlgeschlagen (0x{result:X8})");
        }
    }
}
