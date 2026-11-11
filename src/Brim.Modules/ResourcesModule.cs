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
        // Werte neben der Uhr (wenn eingeschaltet) auch bei geschlossenem Tab, alle 2 Sekunden – aber nur, solange
        // jemand sie sieht; sonst ruht die Messung.
        _statusTimer = context.Time.CreateTimer(_ => StatusTick(), null, StatusInterval, StatusInterval);
        context.StatusVisibilityChanged += StatusTick;
        // Kachel „System“ für „Jetzt“: misst im selben 2-s-Takt, solange sie liegt und „Jetzt“ offen ist.
        context.DefineTile(LoadTile, "System", [TileSize.Small, TileSize.Medium], "System", Glyphs.Cpu, "Auslastung von CPU und Arbeitsspeicher");
        context.TilePlacementsChanged += StatusTick;
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
            if (TileWanted)
            {
                PublishTiles(sample, CultureInfo.CurrentCulture);
            }
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
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        PublishStatus(s, culture);
        PublishDetail(s, culture);

        // Design „Windows-11-nativ“: Messwerte nebeneinander in Karten am unteren Rand (neben dem Akku), Werte
        // wie im Task-Manager; dazu GPU, Datenträger und eine Zeile mit den Top-Prozessen.
        var unit = context.Settings.Display.NetworkUnit;
        var memoryPercent = s is { MemoryTotal: > 0 } m ? 100.0 * m.MemoryUsed / m.MemoryTotal : (double?)null;
        context.SetView("cpu", Card("detail:cpu", "CPU", Percent(s?.Cpu, culture),
            new ChartWidget(cpu) { Max = 100, Capacity = HistoryLength, Height = 16, Thickness = 1.5, Color = BrimColors.DefaultAccent, AccessibleName = "CPU-Verlauf" }),
            column: TabColumn.Cards, order: 10);
        context.SetView("gpu", Card("detail:gpu", "GPU", Percent(s?.Gpu, culture),
            new ChartWidget(gpu) { Max = 100, Capacity = HistoryLength, Height = 16, Thickness = 1.5, Color = BrimColors.DefaultAccent, AccessibleName = "GPU-Verlauf" }),
            column: TabColumn.Cards, order: 11);
        context.SetView("memory", Card("detail:memory", "RAM", Percent(memoryPercent, culture),
            Caption(s is { MemoryTotal: > 0 } mm ? $"{FormatGigabytes(mm.MemoryUsed, culture)} von {Math.Round(mm.MemoryTotal / (1024.0 * 1024 * 1024)).ToString("0", culture)} GB" : "–")),
            column: TabColumn.Cards, order: 12);
        context.SetView("disk", Card("detail:disk", "Datenträger", Percent(s?.Disk, culture), Caption("aktiv")),
            column: TabColumn.Cards, order: 13);
        context.SetView("network", Card("detail:network", "Netzwerk", s is null ? "–" : FormatRate(s.NetworkDown + s.NetworkUp, unit, culture),
            Caption(s is null ? "–" : $"↓ {FormatRate(s.NetworkDown, unit, culture)} ↑ {FormatRate(s.NetworkUp, unit, culture)}")),
            column: TabColumn.Cards, order: 14);
        context.SetView("processes", Processes(s?.TopProcesses ?? [], culture), order: 0);
    }

    /// <summary>Top-Prozesse in einer Zeile unter den Reglern; der Knopf rechts öffnet den Task-Manager.</summary>
    public static Widget Processes(IReadOnlyList<ProcessUsage> processes, CultureInfo culture)
    {
        var children = new List<Widget> { new TextWidget("Top-Prozesse") { Style = TextStyle.Secondary, FontSize = 12 } };
        children.AddRange(processes.Take(3).Select(p => (Widget)new TextWidget($"{p.Name} {Percent(p.Cpu, culture, small: true)}") { FontSize = 12, Grow = 1 }));
        if (processes.Count == 0)
        {
            children.Add(new TextWidget("Gerade ist alles ruhig.") { FontSize = 12, Grow = 1 });
        }
        children.Add(new ButtonWidget("taskmgr")
        {
            Glyph = Glyphs.OpenInNew,
            Size = 22,
            GlyphSize = 11,
            GlyphColor = BrimColors.Slate,
            AccessibleName = "Task-Manager öffnen",
        });
        return new StackWidget(children)
        {
            Spacing = 12,
            ActionId = "detail:cpu",
            Background = BrimColors.Graphite,
            CornerRadius = 8,
            Padding = (12, 4, 6, 4),
            AccessibleName = "Top-Prozesse: " + string.Join(", ", processes.Take(3).Select(p => $"{p.Name} {Percent(p.Cpu, culture, small: true)}")),
        };
    }

    private static TextWidget Caption(string text) => new(text) { Style = TextStyle.Secondary, FontSize = 12 };

    /// <summary>Messwert-Karte: Name, Wert groß, darunter Verlauf oder Erklärung; ein Klick öffnet die Details.</summary>
    public static StackWidget Card(string actionId, string label, string value, Widget below) => new(
        new TextWidget(label) { Style = TextStyle.Secondary, FontSize = 12 },
        new TextWidget(value) { Style = TextStyle.Value },
        below)
    {
        ActionId = actionId,
        Orientation = StackOrientation.Vertical,
        Spacing = 4,
        Align = CrossAlign.Stretch,
        Background = BrimColors.Graphite,
        CornerRadius = 8,
        Padding = (12, 10, 12, 10),
        AccessibleName = $"{label} {value}, Details",
    };

    /// <param name="actionId">Klick auf die Karte, z. B. „detail:cpu“ für die Detailansicht.</param>
    private static StackWidget Card(string? actionId, params Widget[] children) => new(children)
    {
        ActionId = actionId,
        Orientation = StackOrientation.Vertical,
        Justify = Justify.SpaceBetween,
        Align = CrossAlign.Stretch,
        Background = BrimColors.Graphite,
        CornerRadius = 8,
        Padding = (12, 10, 12, 10),
        Grow = 1,
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
    public static string FormatRate(double bytesPerSecond, CultureInfo culture) =>
        Format(bytesPerSecond, 1024, ["B/s", "KB/s", "MB/s", "GB/s"], culture);

    /// <summary>Datenrate in Bit pro Sekunde (Basis 1000) wie der Task-Manager, z. B. „12,4 Mbit/s“.</summary>
    public static string FormatBitRate(double bytesPerSecond, CultureInfo culture) =>
        Format(bytesPerSecond * 8, 1000, ["bit/s", "kbit/s", "Mbit/s", "Gbit/s"], culture);

    /// <summary>Rate in der Einheit aus den Einstellungen.</summary>
    public static string FormatRate(double bytesPerSecond, NetworkUnit unit, CultureInfo culture) =>
        unit == NetworkUnit.Bytes ? FormatRate(bytesPerSecond, culture) : FormatBitRate(bytesPerSecond, culture);

    private static string Format(double perSecond, double step, string[] units, CultureInfo culture)
    {
        var value = Math.Max(0, perSecond);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= step;
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
        if (_context is { } context)
        {
            context.StatusVisibilityChanged -= StatusTick;
            context.TilePlacementsChanged -= StatusTick;
        }
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

    public const string LoadTile = "load";

    private bool TileWanted => _context is { } context && context.PlacedSizes(LoadTile).Count > 0 && context.IsTabVisible(NotchTab.Now);

    /// <summary>Misst selbst nur, wenn der Tab zu ist; sonst kommen die Werte mit seinem Takt.</summary>
    private void StatusTick()
    {
        if (_context is not { } context)
        {
            return;
        }
        var tiles = TileWanted;
        if ((!StatusWanted && !tiles) || _unavailable)
        {
            PublishStatus(null, CultureInfo.CurrentCulture);
            return;
        }
        // Niemand sieht die Werte (versteckt, aufgeklappt, gesperrt, Bildschirm aus) und keine Kachel liegt offen:
        // nicht messen, die letzten bleiben.
        var seen = (StatusWanted && context.IsStatusVisible) || tiles;
        if (!seen || _timer is not null || Interlocked.Exchange(ref _sampling, 1) == 1)
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
            PublishTiles(sample, CultureInfo.CurrentCulture);
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
                Style = TextStyle.Status,
            }
            : null);
        context.SetStatus("network", display.Network && s is not null
            ? new TextWidget($"↓ {FormatRate(s.NetworkDown, display.NetworkUnit, culture)}  ↑ {FormatRate(s.NetworkUp, display.NetworkUnit, culture)}")
            {
                Style = TextStyle.Status,
            }
            : null);
    }

    private void PublishTiles(ResourceSample? s, CultureInfo culture)
    {
        if (_context is not { } context)
        {
            return;
        }
        foreach (var size in context.PlacedSizes(LoadTile))
        {
            context.SetTile(LoadTile, size, LoadTileContent(size, s, culture));
        }
    }

    /// <summary>Kachel „System“: Kopfzeile und je ein Balken für CPU und RAM (mittel: auch GPU).</summary>
    public static Widget LoadTileContent(TileSize size, ResourceSample? s, CultureInfo culture)
    {
        var memory = s is { MemoryTotal: > 0 } ? 100.0 * s.MemoryUsed / s.MemoryTotal : (double?)null;
        var rows = new List<Widget>
        {
            Bar("CPU", s?.Cpu, BrimColors.Cpu, culture),
            Bar("RAM", memory, BrimColors.Memory, culture),
        };
        if (size != TileSize.Small && s?.Gpu is { } gpu)
        {
            rows.Add(Bar("GPU", gpu, BrimColors.Gpu, culture));
        }
        return TileCard.Card(
            TileCard.Header(Glyphs.Cpu, BrimColors.Cpu, "System"),
            // Klein: Kopfzeile und zwei Zeilen füllen die 57 DIP fast ganz, daher enger.
            new StackWidget(rows) { Orientation = StackOrientation.Vertical, Align = CrossAlign.Stretch, Spacing = size == TileSize.Small ? 3 : 5 });
    }

    private static StackWidget Bar(string name, double? value, string color, CultureInfo culture) =>
        new(new TextWidget(name) { Style = TextStyle.Caption, FixedWidth = 30 },
            new ProgressWidget(Math.Clamp((value ?? 0) / 100, 0, 1)) { Color = color, Grow = 1, AccessibleName = name },
            new TextWidget(Percent(value, culture, small: true)) { Style = TextStyle.Caption, Color = BrimColors.Fog, FixedWidth = 34 })
        {
            Spacing = 6,
            AccessibleName = $"{name} {Percent(value, culture, small: true)}",
        };
}
