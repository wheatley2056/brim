using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules.Sensors;

namespace Brim.Modules;

// Detailansichten im Tab „System“: Klick auf CPU, RAM, GPU, Netzwerk oder Datenträger. Die Notch wird höher,
// oben links geht es zurück, oben rechts in den Task-Manager.
public sealed partial class ResourcesModule
{
    public static readonly IReadOnlyDictionary<string, string> DetailTitles = new Dictionary<string, string>
    {
        ["cpu"] = "Prozessor",
        ["memory"] = "Arbeitsspeicher",
        ["gpu"] = "Grafikprozessor",
        ["disk"] = "Datenträger",
        ["network"] = "Netzwerk",
    };

    private static readonly string Back = char.ConvertFromUtf32(0xE76B);

    private volatile string? _detail;

    /// <summary>Offene Detailansicht oder <c>null</c>.</summary>
    public string? OpenDetail => _detail;

    public void OnExpandedChanged(bool expanded)
    {
        if (!expanded)
        {
            CloseDetail();
        }
    }

    private void CloseDetail()
    {
        if (_detail is null)
        {
            return;
        }
        _detail = null;
        _context?.RemoveView("detail");
    }

    private void PublishDetail(ResourceSample? sample, CultureInfo culture)
    {
        if (_context is not { } context || _detail is not { } kind || !DetailTitles.ContainsKey(kind))
        {
            return;
        }
        ResourceDetail detail;
        DetailHistory history;
        lock (_gate)
        {
            detail = _lastDetail ?? ResourceDetail.Empty;
            history = new DetailHistory([.. _cpuHistory], [.. _memoryHistory], [.. _gpuHistory], [.. _diskHistory], [.. _downHistory], [.. _upHistory]);
        }
        context.SetDetail("detail", DetailView(kind, sample, detail, history, culture, TimeSpan.FromMilliseconds(Environment.TickCount64)));
    }

    public sealed record DetailHistory(double[] Cpu, double[] Memory, double[] Gpu, double[] Disk, double[] Down, double[] Up);

    /// <summary>Detailansicht als reine Funktion der Messwerte (testbar).</summary>
    public static Widget DetailView(string kind, ResourceSample? s, ResourceDetail d, DetailHistory history, CultureInfo culture, TimeSpan uptime)
    {
        var header = new StackWidget(
            new ButtonWidget("detail:close") { Glyph = Back, Size = 28, GlyphSize = 13, AccessibleName = "Zurück" },
            new TextWidget(DetailTitles[kind]) { Style = TextStyle.Title, FontSize = 17, Grow = 1 },
            new ButtonWidget("taskmgr") { Kind = ButtonKind.Secondary, Glyph = Glyphs.OpenInNew, GlyphSize = 12, Label = "Task-Manager", AccessibleName = "Im Task-Manager öffnen" })
        {
            Spacing = 8,
        };
        var body = kind switch
        {
            "cpu" => Cpu(s, d, history, culture, uptime),
            "memory" => Memory(s, d, history, culture),
            "gpu" => Gpu(s, d, history, culture),
            "disk" => Disk(s, d, history, culture),
            _ => Network(s, history, culture),
        };
        return new StackWidget([header, .. body])
        {
            Orientation = StackOrientation.Vertical,
            Align = CrossAlign.Stretch,
            Spacing = 10,
            Grow = 1,
        };
    }

    private static List<Widget> Cpu(ResourceSample? s, ResourceDetail d, DetailHistory h, CultureInfo culture, TimeSpan uptime) =>
    [
        Chart(h.Cpu, 100, BrimColors.Cpu, "Auslastung", Percent(s?.Cpu, culture)),
        new StackWidget(
            Stats(
                ("Geschwindigkeit", d.CpuMhz is { } mhz ? (mhz / 1000).ToString("0.00", culture) + " GHz" : "–"),
                ("Logische Prozessoren", Environment.ProcessorCount.ToString(culture)),
                ("Prozesse", Number(d.Processes, culture)),
                ("Threads", Number(d.Threads, culture)),
                ("Handles", Number(d.Handles, culture)),
                ("Betriebszeit", Uptime(uptime))),
            Processes("Top-Prozesse", d.TopCpu.Select(p => (p.Name, Percent(p.Cpu, culture, small: true)))))
        {
            Spacing = 10,
            Align = CrossAlign.Stretch,
            Grow = 1,
        },
    ];

    private static List<Widget> Memory(ResourceSample? s, ResourceDetail d, DetailHistory h, CultureInfo culture)
    {
        var total = s?.MemoryTotal ?? 0;
        var used = s?.MemoryUsed ?? 0;
        return
        [
            Chart(h.Memory, 100, BrimColors.Memory, "In Verwendung", total > 0 ? $"{FormatGigabytes(used, culture)} GB ({Percent(100.0 * used / total, culture)})" : "–"),
            new StackWidget(
                Stats(
                    ("Gesamt", total > 0 ? FormatGigabytes(total, culture) + " GB" : "–"),
                    ("Verfügbar", total > 0 ? FormatGigabytes(total - used, culture) + " GB" : "–"),
                    ("Zugesichert", d.Committed is { } c && d.CommitLimit is { } l ? $"{FormatGigabytes(c, culture)} / {FormatGigabytes(l, culture)} GB" : "–"),
                    ("Zwischengespeichert", d.Cached is { } cached ? FormatGigabytes(cached, culture) + " GB" : "–")),
                Processes("Größter Speicherbedarf", d.TopMemory.Select(p => (p.Name, Megabytes(p.Bytes, culture)))))
            {
                Spacing = 10,
                Align = CrossAlign.Stretch,
                Grow = 1,
            },
        ];
    }

    private static List<Widget> Gpu(ResourceSample? s, ResourceDetail d, DetailHistory h, CultureInfo culture) =>
    [
        Chart(h.Gpu, 100, BrimColors.Gpu, "Auslastung (stärkste Engine)", Percent(s?.Gpu, culture)),
        Stats(
            ("Dedizierter Speicher in Gebrauch", d.GpuDedicated is { } bytes ? FormatGigabytes(bytes, culture) + " GB" : "–"),
            ("Auslastung", Percent(s?.Gpu, culture))),
    ];

    private static List<Widget> Disk(ResourceSample? s, ResourceDetail d, DetailHistory h, CultureInfo culture)
    {
        var drives = new List<Widget>();
        foreach (var drive in Drives())
        {
            drives.Add(new StackWidget(
                new TextWidget(drive.Name) { Style = TextStyle.Mono, FontSize = 12, FixedWidth = 40 },
                new ProgressWidget(drive.Used) { Thickness = 6, Color = drive.Used > 0.9 ? BrimColors.Danger : BrimColors.Cpu, Grow = 1, AccessibleName = $"{drive.Name} belegt" },
                new TextWidget($"{FormatGigabytes(drive.Free, culture)} GB frei") { Style = TextStyle.MonoCaption, FixedWidth = 100 })
            {
                Spacing = 10,
            });
        }
        return
        [
            Chart(h.Disk, 100, BrimColors.Cpu, "Aktive Zeit", Percent(s?.Disk, culture)),
            new StackWidget(
                Stats(
                    ("Lesen", d.DiskRead is { } r ? FormatRate(r, culture) : "–"),
                    ("Schreiben", d.DiskWrite is { } w ? FormatRate(w, culture) : "–")),
                Panel("Laufwerke", drives.Count > 0 ? drives : [new TextWidget("Keine Laufwerke gefunden") { Style = TextStyle.Secondary }]))
            {
                Spacing = 10,
                Align = CrossAlign.Stretch,
                Grow = 1,
            },
        ];
    }

    private static List<Widget> Network(ResourceSample? s, DetailHistory h, CultureInfo culture)
    {
        var adapters = Adapters().Select(a => (Widget)new StackWidget(
            new StackWidget(
                new TextWidget(a.Name) { FontSize = 12 },
                new TextWidget(a.Description) { Style = TextStyle.Caption })
            {
                Orientation = StackOrientation.Vertical,
                Align = CrossAlign.Stretch,
                Grow = 1,
            },
            new StackWidget(
                new TextWidget(a.Address ?? "–") { Style = TextStyle.MonoCaption },
                new TextWidget(a.Speed) { Style = TextStyle.MonoCaption })
            {
                Orientation = StackOrientation.Vertical,
                Align = CrossAlign.End,
            })
        {
            Spacing = 8,
        }).ToList();
        var max = Math.Max(1, Math.Max(h.Down.DefaultIfEmpty(0).Max(), h.Up.DefaultIfEmpty(0).Max()));
        return
        [
            new StackWidget(
                Chart(h.Down, max, BrimColors.Cpu, "Empfangen", s is null ? "–" : FormatRate(s.NetworkDown, culture)),
                Chart(h.Up, max, BrimColors.Gpu, "Gesendet", s is null ? "–" : FormatRate(s.NetworkUp, culture)))
            {
                Spacing = 10,
                Align = CrossAlign.Stretch,
            },
            Panel("Verbindungen", adapters.Count > 0 ? adapters : [new TextWidget("Keine aktive Verbindung") { Style = TextStyle.Secondary }]),
        ];
    }

    // ───────── Bausteine ─────────

    private static StackWidget Chart(double[] values, double max, string color, string label, string value) => new(
        new StackWidget(
            new TextWidget(label) { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 },
            new TextWidget(value) { Style = TextStyle.Mono, FontSize = 13 })
        {
            Spacing = 6,
        },
        new ChartWidget(values) { Max = max, Capacity = HistoryLength, Height = 70, Color = color, AccessibleName = $"{label} {value}" })
    {
        Orientation = StackOrientation.Vertical,
        Align = CrossAlign.Stretch,
        Spacing = 6,
        Background = BrimColors.Graphite,
        CornerRadius = 14,
        Padding = (12, 10, 12, 10),
        Grow = 1,
    };

    private static StackWidget Stats(params (string Label, string Value)[] stats) => Panel(null,
        stats.Select(s => (Widget)new StackWidget(
            new TextWidget(s.Label) { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 },
            new TextWidget(s.Value) { Style = TextStyle.Mono, FontSize = 12 })
        {
            Spacing = 8,
            AccessibleName = $"{s.Label} {s.Value}",
        }).ToList());

    private static StackWidget Processes(string title, IEnumerable<(string Name, string Value)> processes)
    {
        var rows = processes.Select(p => (Widget)new StackWidget(
            new TextWidget(p.Name) { FontSize = 12, Grow = 1 },
            new TextWidget(p.Value) { Style = TextStyle.MonoCaption })
        {
            Spacing = 8,
        }).ToList();
        return Panel(title, rows.Count > 0 ? rows : [new TextWidget("Gerade ist alles ruhig.") { Style = TextStyle.Secondary }]);
    }

    private static StackWidget Panel(string? title, IReadOnlyList<Widget> rows)
    {
        var children = new List<Widget>();
        if (title is not null)
        {
            children.Add(new TextWidget(title) { Style = TextStyle.Secondary, FontSize = 12 });
        }
        children.AddRange(rows);
        return new StackWidget(children)
        {
            Orientation = StackOrientation.Vertical,
            Align = CrossAlign.Stretch,
            Spacing = 5,
            Background = BrimColors.Graphite,
            CornerRadius = 14,
            Padding = (12, 10, 12, 10),
            Grow = 1,
        };
    }

    private static string Number(int? value, CultureInfo culture) => value?.ToString("N0", culture) ?? "–";

    private static string Megabytes(ulong bytes, CultureInfo culture) => (bytes / (1024.0 * 1024)).ToString("N0", culture) + " MB";

    public static string Uptime(TimeSpan uptime) =>
        uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays} d {uptime.Hours} h" : $"{uptime.Hours} h {uptime.Minutes} min";

    private sealed record DriveUsage(string Name, double Used, ulong Free);

    private static IEnumerable<DriveUsage> Drives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            DriveUsage? usage = null;
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.TotalSize > 0)
                {
                    usage = new DriveUsage(drive.Name.TrimEnd('\\'), 1 - (double)drive.TotalFreeSpace / drive.TotalSize, (ulong)drive.TotalFreeSpace);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            if (usage is not null)
            {
                yield return usage;
            }
        }
    }

    private sealed record AdapterInfo(string Name, string Description, string? Address, string Speed);

    private static IEnumerable<AdapterInfo> Adapters()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }
        foreach (var adapter in interfaces.Where(a => a.OperationalStatus == OperationalStatus.Up
            && a.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)).Take(4))
        {
            string? address = null;
            try
            {
                address = adapter.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            }
            catch (NetworkInformationException)
            {
            }
            var speed = adapter.Speed switch
            {
                >= 1_000_000_000 => $"{adapter.Speed / 1_000_000_000.0:0.#} Gbit/s",
                > 0 => $"{adapter.Speed / 1_000_000} Mbit/s",
                _ => "",
            };
            yield return new AdapterInfo(adapter.Name, adapter.Description, address, speed);
        }
    }
}
