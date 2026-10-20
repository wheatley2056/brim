using System.Diagnostics;
using System.Globalization;
using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules.Sensors;

namespace Brim.Modules;

/// <summary>
/// Ressourcen (A7): CPU, RAM, GPU und Netzwerk mit Verläufen der letzten 60 s und den drei
/// Prozessen mit der meisten CPU-Last, aus denselben Quellen wie der Task-Manager.
/// Gemessen wird einmal pro Sekunde und nur, solange der Tab „System“ irgendwo offen ist.
/// </summary>
public sealed partial class ResourcesModule : IBrimModule
{
    public const string ModuleId = "resources";
    public const int HistoryLength = 60;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly Func<IResourceSampler> _createSampler;
    private readonly Lock _gate = new();
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _gpuHistory = new();
    private readonly Queue<double> _memoryHistory = new();
    private readonly Queue<double> _diskHistory = new();
    private readonly Queue<double> _downHistory = new();
    private readonly Queue<double> _upHistory = new();
    private ResourceDetail? _lastDetail;
    private ModuleContext? _context;
    private IResourceSampler? _sampler;
    private ITimer? _timer;
    private ResourceSample? _last;
    private bool _unavailable;
    private int _sampling;

    public ResourcesModule() : this(() => new PdhResourceSampler())
    {
    }

    /// <param name="createSampler">Erst beim ersten Öffnen des Tabs aufgerufen.</param>
    public ResourcesModule(Func<IResourceSampler> createSampler)
    {
        _createSampler = createSampler;
    }

    public string Id => ModuleId;
    public string DisplayName => "Ressourcen";

    /// <summary>Misst gerade (Tab offen)?</summary>
    public bool IsSampling => _timer is not null;

    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        Publish();
        // Werte neben der Uhr (wenn eingeschaltet) auch bei geschlossenem Tab, alle 2 Sekunden.
        _statusTimer = context.Time.CreateTimer(_ => StatusTick(), null, StatusInterval, StatusInterval);
        if (context.IsTabVisible(NotchTab.System))
        {
            OnTabVisibilityChanged(NotchTab.System, true);
        }
        return Task.CompletedTask;
    }

    public void OnTabVisibilityChanged(NotchTab tab, bool visible)
    {
        if (tab != NotchTab.System || _context is not { } context)
        {
            return;
        }
        lock (_gate)
        {
            if (visible && _timer is null && !_unavailable)
            {
                _timer = context.Time.CreateTimer(_ => Tick(), null, TimeSpan.Zero, Interval);
            }
            else if (!visible && _timer is not null)
            {
                _timer.Dispose();
                _timer = null;
            }
        }
        if (!visible)
        {
            CloseDetail();
        }
    }

    private void Tick()
    {
        // Dauert eine Messung länger als der Takt, wird die nächste übersprungen statt gestapelt.
        if (Interlocked.Exchange(ref _sampling, 1) == 1)
        {
            return;
        }
        try
        {
            IResourceSampler? sampler = null;
            lock (_gate)
            {
                if (_timer is null)
                {
                    return;
                }
                try
                {
                    sampler = _sampler ??= _createSampler();
                }
                catch (Exception ex)
                {
                    Log.Error("Ressourcen lassen sich nicht messen", ex);
                    _unavailable = true;
                    _timer.Dispose();
                    _timer = null;
                }
            }
            if (sampler is null)
            {
                Publish();
                return;
            }
            var sample = sampler.Sample();
            var detail = _detail is not null ? sampler.Detail() : null;
            lock (_gate)
            {
                _last = sample;
                _lastDetail = detail ?? _lastDetail;
                Push(_cpuHistory, sample.Cpu);
                Push(_gpuHistory, sample.Gpu);
                Push(_memoryHistory, sample.MemoryTotal > 0 ? 100.0 * sample.MemoryUsed / sample.MemoryTotal : null);
                Push(_diskHistory, sample.Disk);
                Push(_downHistory, sample.NetworkDown);
                Push(_upHistory, sample.NetworkUp);
            }
            Publish();
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    private static void Push(Queue<double> history, double? value)
    {
        if (value is not { } v)
        {
            return;
        }
        history.Enqueue(v);
        while (history.Count > HistoryLength)
        {
            history.Dequeue();
        }
    }

    // ───────── Darstellung ─────────

    private void Publish()
    {
        if (_context is not { } context)
        {
            return;
        }
        ResourceSample? s;
        double[] cpu, gpu;
        lock (_gate)
        {
            s = _last;
            cpu = [.. _cpuHistory];
            gpu = [.. _gpuHistory];
        }

        if (_unavailable)
        {
            context.SetView("meters", Card(null,
                new TextWidget("Ressourcen") { Style = TextStyle.Secondary, FontSize = 12 },
                new TextWidget("Leistungsindikatoren sind auf diesem PC nicht verfügbar.") { Style = TextStyle.Secondary, Wrap = true }));
            context.RemoveView("processes");
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        PublishStatus(s, culture);
        var memoryValue = s is { MemoryTotal: > 0 }
            ? $"{FormatGigabytes(s.MemoryUsed, culture)} / {FormatGigabytes(s.MemoryTotal, culture)}"
            : "–";
        PublishDetail(s, culture);
        var cpuCard = Card("detail:cpu",
            Header("CPU", Percent(s?.Cpu, culture)),
            new ChartWidget(cpu) { Max = 100, Capacity = HistoryLength, Color = BrimColors.Cpu, AccessibleName = "CPU-Verlauf" });
        var memoryCard = Card("detail:memory",
            Header("RAM", memoryValue),
            new StackWidget(
                new ProgressWidget(s is { MemoryTotal: > 0 } m ? (double)m.MemoryUsed / m.MemoryTotal : 0)
                {
                    Thickness = 6,
                    Color = BrimColors.Memory,
                    AccessibleName = "Arbeitsspeicher belegt",
                },
                new TextWidget("GB belegt") { Style = TextStyle.Caption })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 6,
                Align = CrossAlign.Stretch,
            });
        var gpuCard = Card("detail:gpu",
            Header("GPU", Percent(s?.Gpu, culture)),
            new ChartWidget(gpu) { Max = 100, Capacity = HistoryLength, Color = BrimColors.Gpu, AccessibleName = "GPU-Verlauf" });
        var networkCard = Card("detail:network",
            // Datenträger (aktive Zeit) passt als Wert in die Kopfzeile; für eine eigene Karte fehlt der Platz.
            // Ein Klick darauf öffnet die Datenträger, ein Klick auf den Rest das Netzwerk.
            new StackWidget(
                new TextWidget("Netzwerk") { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 },
                new StackWidget(new TextWidget(s?.Disk is { } disk ? "Disk " + Percent(disk, culture) : "Disk") { Style = TextStyle.MonoCaption, FontSize = 11 })
                {
                    ActionId = "detail:disk",
                    CornerRadius = 6,
                    Padding = (4, 1, 4, 1),
                    AccessibleName = s?.Disk is { } d ? $"Datenträger {Percent(d, culture)} aktiv, Details" : "Datenträger, Details",
                })
            {
                Spacing = 6,
            },
            new StackWidget(
                new TextWidget("↓ " + (s is null ? "–" : FormatRate(s.NetworkDown, culture))) { Style = TextStyle.Mono, FontSize = 12 },
                new TextWidget("↑ " + (s is null ? "–" : FormatRate(s.NetworkUp, culture))) { Style = TextStyle.MonoCaption, FontSize = 12 })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 2,
                Align = CrossAlign.Start,
            });

        context.SetView("meters", new StackWidget(
            Row(cpuCard, memoryCard),
            Row(gpuCard, networkCard))
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 10,
            Align = CrossAlign.Stretch,
            Grow = 1,
        });

        var rows = new List<Widget>
        {
            new StackWidget(
                new TextWidget("Top-Prozesse") { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 },
                new ButtonWidget("taskmgr")
                {
                    Glyph = Glyphs.OpenInNew,
                    Size = 22,
                    GlyphSize = 11,
                    GlyphColor = BrimColors.Slate,
                    AccessibleName = "Task-Manager öffnen",
                }),
        };
        rows.AddRange((s?.TopProcesses ?? []).Select(p => (Widget)new StackWidget(
            new TextWidget(p.Name) { Grow = 1 },
            new TextWidget(Percent(p.Cpu, culture, small: true)) { Style = TextStyle.MonoCaption, FontSize = 12 })
        {
            Spacing = 8,
            AccessibleName = $"{p.Name}, {Percent(p.Cpu, culture, small: true)}",
        }));
        if (s is { TopProcesses.Count: 0 })
        {
            rows.Add(new TextWidget("Gerade ist alles ruhig.") { Style = TextStyle.Secondary });
        }
        context.SetView("processes", new StackWidget(rows)
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 5,
            Align = CrossAlign.Stretch,
            Background = BrimColors.Graphite,
            CornerRadius = 14,
            Padding = (12, 8, 8, 8),
            Grow = 1,
        }, column: TabColumn.Side, order: 10);
    }

    /// <param name="actionId">Klick auf die Karte, z. B. „detail:cpu“ für die Detailansicht.</param>
    private static StackWidget Card(string? actionId, params Widget[] children) => new(children)
    {
        ActionId = actionId,
        Orientation = StackOrientation.Vertical,
        Justify = Justify.SpaceBetween,
        Align = CrossAlign.Stretch,
        Background = BrimColors.Graphite,
        CornerRadius = 14,
        Padding = (12, 10, 12, 10),
        Grow = 1,
    };

    private static StackWidget Row(params Widget[] cards) => new(cards)
    {
        Spacing = 10,
        Align = CrossAlign.Stretch,
        Grow = 1,
    };

    private static StackWidget Header(string label, string value) => new(
        new TextWidget(label) { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 },
        new TextWidget(value) { Style = TextStyle.Mono, FontSize = 12 })
    {
        Spacing = 6,
        AccessibleName = $"{label} {value}",
    };

    /// <summary>„23 %“; mit <paramref name="small"/> unter 1 % eine Nachkommastelle wie im Task-Manager.</summary>
    public static string Percent(double? value, CultureInfo culture, bool small = false) => value switch
    {
        null => "–",
        < 1 when small => value.Value.ToString("0.0", culture) + " %",
        _ => Math.Round(value.Value).ToString("0", culture) + " %",
    };

    /// <summary>Bytes als Gigabyte mit einer Nachkommastelle (Basis 1024 wie der Task-Manager).</summary>
    public static string FormatGigabytes(ulong bytes, CultureInfo culture) =>
        (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", culture);

    /// <summary>Datenrate in B/s, KB/s, MB/s oder GB/s mit höchstens drei gültigen Stellen.</summary>
    public static string FormatRate(double bytesPerSecond, CultureInfo culture)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, culture) + " " + units[unit];
    }

    public void OnAction(ActionInvocation invocation)
    {
        if (invocation.ActionId.StartsWith("detail:", StringComparison.Ordinal))
        {
            var kind = invocation.ActionId["detail:".Length..];
            if (kind == "close")
            {
                CloseDetail();
                return;
            }
            _detail = kind;
            // Sofort zeigen, die Zusatzwerte kommen mit der nächsten Messung.
            Publish();
            return;
        }
        if (invocation.ActionId == "taskmgr")
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Abgebrochene Rückfrage der Benutzerkontensteuerung landet ebenfalls hier.
                Log.Info($"Task-Manager nicht gestartet: {ex.Message}");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _statusTimer?.Dispose();
            _statusTimer = null;
            _timer?.Dispose();
            _timer = null;
            _sampler?.Dispose();
            _sampler = null;
        }
        return ValueTask.CompletedTask;
    }

    // ───────── Neben der Uhr ─────────

    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(2);
    private ITimer? _statusTimer;

    public void OnSettingsChanged() => StatusTick();

    private bool StatusWanted => _context?.Settings.Display is { } d && (d.Load || d.Network);

    /// <summary>Misst selbst nur, wenn der Tab zu ist; sonst kommen die Werte mit seinem Takt.</summary>
    private void StatusTick()
    {
        if (_context is not { } context)
        {
            return;
        }
        if (!StatusWanted || _unavailable)
        {
            PublishStatus(null, CultureInfo.CurrentCulture);
            return;
        }
        if (_timer is not null || Interlocked.Exchange(ref _sampling, 1) == 1)
        {
            return;
        }
        try
        {
            IResourceSampler sampler;
            lock (_gate)
            {
                sampler = _sampler ??= _createSampler();
            }
            var sample = sampler.Sample();
            lock (_gate)
            {
                _last = sample;
            }
            PublishStatus(sample, CultureInfo.CurrentCulture);
        }
        catch (Exception ex)
        {
            Log.Warn("Werte für die Notch nicht messbar", ex);
            _unavailable = true;
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    private void PublishStatus(ResourceSample? s, CultureInfo culture)
    {
        if (_context is not { } context)
        {
            return;
        }
        var display = context.Settings.Display;
        context.SetStatus("load", display.Load && s is not null
            ? new TextWidget($"CPU {Percent(s.Cpu, culture)} · RAM {(s.MemoryTotal > 0 ? Percent(100.0 * s.MemoryUsed / s.MemoryTotal, culture) : "–")}")
            {
                Style = TextStyle.Mono,
                FontSize = 12,
                Color = BrimColors.Slate,
            }
            : null);
        context.SetStatus("network", display.Network && s is not null
            ? new TextWidget($"↓ {FormatRate(s.NetworkDown, culture)}  ↑ {FormatRate(s.NetworkUp, culture)}")
            {
                Style = TextStyle.Mono,
                FontSize = 12,
                Color = BrimColors.Slate,
            }
            : null);
    }
}
