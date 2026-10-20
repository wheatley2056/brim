using System.Globalization;
using Brim.Core;
using Brim.Core.Widgets;

namespace Brim.Modules;

/// <summary>
/// Timer und Stoppuhr (A3, Plan 4.3). Laufend kompakt mit Ring und Restzeit, abgelaufen als kritischer
/// Hinweis. Geöffnet wird die Auswahl aus dem Tab „Jetzt“ (Knopf neben der Uhr) über <see cref="ShowPanel"/>.
/// </summary>
public sealed class TimerModule : IBrimModule
{
    public const string ModuleId = "timer";

    /// <summary>Aktion für andere Module: Auswahl öffnen bzw. schließen.</summary>
    public const string ShowPanel = "show-panel";

    public static readonly int[] PresetMinutes = [1, 5, 10, 25];

    private readonly Lock _gate = new();
    private ModuleContext? _context;
    private ITimer? _ticker;
    private bool _panel;

    // Timer: Ende (läuft) oder eingefrorene Restzeit (pausiert).
    private DateTimeOffset? _timerEnd;
    private TimeSpan? _timerPaused;
    private TimeSpan _timerTotal;

    // Stoppuhr: Startpunkt (läuft) und angesammelte Zeit.
    private DateTimeOffset? _stopwatchStart;
    private TimeSpan _stopwatchElapsed;
    private bool _stopwatchUsed;

    public string Id => ModuleId;
    public string DisplayName => "Timer";

    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        return Task.CompletedTask;
    }

    private DateTimeOffset Now => _context!.Time.GetUtcNow();

    // ───────── Zustand ─────────

    /// <summary>Restzeit des Timers, <c>null</c> ohne Timer.</summary>
    public TimeSpan? TimerRemaining
    {
        get
        {
            lock (_gate)
            {
                return _timerPaused ?? (_timerEnd is { } end ? Max(end - Now, TimeSpan.Zero) : null);
            }
        }
    }

    public TimeSpan StopwatchElapsed
    {
        get
        {
            lock (_gate)
            {
                return _stopwatchElapsed + (_stopwatchStart is { } start ? Now - start : TimeSpan.Zero);
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public void StartTimer(TimeSpan duration)
    {
        lock (_gate)
        {
            _timerTotal = duration;
            _timerEnd = Now + duration;
            _timerPaused = null;
            _panel = false;
        }
        _context?.End("done");
        _context?.End("panel");
        Update();
    }

    private void Update()
    {
        bool tick;
        lock (_gate)
        {
            tick = _timerEnd is not null || _stopwatchStart is not null;
            if (_timerEnd is { } end && end <= Now && _timerPaused is null)
            {
                _timerEnd = null;
                Finished();
            }
        }
        Publish();
        EnsureTicker(tick);
    }

    /// <summary>Einmal pro Sekunde, genau auf die Sekundengrenze der Anzeige, nur solange etwas läuft.</summary>
    private void EnsureTicker(bool running)
    {
        lock (_gate)
        {
            if (running && _ticker is null)
            {
                _ticker = _context!.Time.CreateTimer(_ => Update(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
            else if (!running && _ticker is not null)
            {
                _ticker.Dispose();
                _ticker = null;
            }
        }
    }

    private void Finished() => _context?.Post(new Activity
    {
        ModuleId = ModuleId,
        Id = "done",
        Priority = ActivityPriority.Critical,
        Duration = TimeSpan.FromSeconds(10),
        Announcement = "Timer abgelaufen",
        Content = new StackWidget(
            new IconWidget(Glyphs.Clock) { Size = 24, Color = BrimColors.Warning },
            new StackWidget(
                new TextWidget("Timer abgelaufen") { Style = TextStyle.Title, FontSize = 15 },
                new TextWidget(Format(_timerTotal) + " sind um") { Style = TextStyle.Secondary })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 2,
                Align = CrossAlign.Start,
                Grow = 1,
            },
            new ButtonWidget("again") { Kind = ButtonKind.Secondary, Label = "Noch mal" },
            new ButtonWidget("ok") { Kind = ButtonKind.Secondary, Label = "OK" })
        {
            Spacing = 10,
        },
    });

    // ───────── Darstellung ─────────

    /// <summary>„4:59“, „1:02:03“; Stoppuhr mit Zehnteln.</summary>
    public static string Format(TimeSpan time, bool tenths = false)
    {
        // Timer runden auf, damit „0:00“ erst beim Ablauf erscheint.
        var seconds = tenths ? (long)Math.Floor(time.TotalSeconds) : (long)Math.Ceiling(time.TotalSeconds - 0.0005);
        var text = seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
        return tenths ? text + CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator + (int)(time.TotalMilliseconds / 100 % 10) : text;
    }

    private void Publish()
    {
        var context = _context;
        if (context is null)
        {
            return;
        }
        TimeSpan? remaining = TimerRemaining;
        bool timerPaused, stopwatchRunning, stopwatchUsed, panel;
        TimeSpan total;
        lock (_gate)
        {
            timerPaused = _timerPaused is not null;
            stopwatchRunning = _stopwatchStart is not null;
            stopwatchUsed = _stopwatchUsed;
            total = _timerTotal;
            panel = _panel;
        }
        var elapsed = StopwatchElapsed;

        if (remaining is { } left)
        {
            var progress = total > TimeSpan.Zero ? left / total : 0;
            var text = Format(left);
            context.Post(new Activity
            {
                ModuleId = ModuleId,
                Id = "timer",
                Priority = ActivityPriority.Ongoing,
                Rank = 30,
                Leading = new RingWidget(progress) { Size = 18, Color = timerPaused ? BrimColors.Slate : BrimColors.Warning },
                Trailing = new TextWidget(text) { Style = TextStyle.Mono, Color = timerPaused ? BrimColors.Slate : BrimColors.Warning, AccessibleName = $"Timer {text}" },
                Bead = new RingWidget(progress) { Size = 16, Color = BrimColors.Warning, AccessibleName = $"Timer {text}" },
                Expanded = TimerView(text, progress, timerPaused),
                Announcement = $"Timer {Format(total)} läuft",
            });
        }
        else
        {
            context.End("timer");
        }

        if (stopwatchUsed)
        {
            var text = Format(elapsed, tenths: !stopwatchRunning);
            context.Post(new Activity
            {
                ModuleId = ModuleId,
                Id = "stopwatch",
                Priority = ActivityPriority.Ongoing,
                // Angehalten nur im Tab „Jetzt“, nicht in der kompakten Notch.
                IsBackground = !stopwatchRunning,
                Rank = 25,
                Leading = new IconWidget(Glyphs.Stopwatch) { Size = 15, Color = BrimColors.Fog },
                Trailing = new TextWidget(Format(elapsed)) { Style = TextStyle.Mono, AccessibleName = $"Stoppuhr {Format(elapsed)}" },
                Bead = new IconWidget(Glyphs.Stopwatch) { Size = 14, AccessibleName = "Stoppuhr" },
                Expanded = StopwatchView(text, stopwatchRunning),
            });
        }
        else
        {
            context.End("stopwatch");
        }

        if (panel)
        {
            context.Post(new Activity
            {
                ModuleId = ModuleId,
                Id = "panel",
                Priority = ActivityPriority.Ongoing,
                IsBackground = true,
                Rank = 1000,
                Expanded = PanelView(),
            });
        }
        else
        {
            context.End("panel");
        }
    }

    private static Widget Header(string title) => new StackWidget(
        new ButtonWidget("back") { Glyph = Glyphs.Back, Size = 28, GlyphSize = 12, AccessibleName = "Zurück" },
        new TextWidget(title) { Style = TextStyle.Title, FontSize = 15, Grow = 1 })
    {
        Spacing = 6,
    };

    private static Widget PanelView() => new StackWidget(
        Header("Timer"),
        new StackWidget(PresetMinutes.Select(m => (Widget)new ButtonWidget("preset:" + m) { Kind = ButtonKind.Secondary, Label = $"{m} min" }).ToList())
        {
            Spacing = 8,
        },
        new SpacerWidget { Grow = 1 },
        new StackWidget(
            new IconWidget(Glyphs.Stopwatch) { Size = 16, Color = BrimColors.Slate },
            new TextWidget("Stoppuhr") { Style = TextStyle.Secondary, Grow = 1 },
            new ButtonWidget("stopwatch:start") { Kind = ButtonKind.Secondary, Label = "Starten" })
        {
            Spacing = 10,
        })
    {
        Orientation = StackOrientation.Vertical,
        Spacing = 10,
        Align = CrossAlign.Stretch,
    };

    private static Widget TimerView(string text, double progress, bool paused) => new StackWidget(
        new StackWidget(
            new RingWidget(progress) { Size = 56, Thickness = 5, Color = paused ? BrimColors.Slate : BrimColors.Warning },
            new StackWidget(
                new TextWidget(text) { Style = TextStyle.Display, FontSize = 40, Color = paused ? BrimColors.Slate : BrimColors.Fog },
                new TextWidget(paused ? "Angehalten" : "Timer") { Style = TextStyle.Secondary })
            {
                Orientation = StackOrientation.Vertical,
                Align = CrossAlign.Start,
            })
        {
            Spacing = 16,
        },
        new SpacerWidget { Grow = 1 },
        new StackWidget(
            new ButtonWidget("timer:add") { Kind = ButtonKind.Secondary, Label = "+1 min" },
            new ButtonWidget("timer:stop") { Kind = ButtonKind.Secondary, Label = "Beenden" },
            new ButtonWidget("timer:toggle")
            {
                Kind = ButtonKind.Primary,
                Glyph = paused ? Glyphs.Play : Glyphs.Pause,
                AccessibleName = paused ? "Fortsetzen" : "Anhalten",
            })
        {
            Spacing = 10,
            Justify = Justify.End,
        })
    {
        Orientation = StackOrientation.Vertical,
        Spacing = 8,
        Align = CrossAlign.Stretch,
    };

    private static Widget StopwatchView(string text, bool running) => new StackWidget(
        new StackWidget(
            new IconWidget(Glyphs.Stopwatch) { Size = 28, Color = BrimColors.Slate },
            new TextWidget(text) { Style = TextStyle.Display, FontSize = 40 })
        {
            Spacing = 16,
        },
        new SpacerWidget { Grow = 1 },
        new StackWidget(
            new ButtonWidget("stopwatch:reset") { Kind = ButtonKind.Secondary, Label = "Zurücksetzen" },
            new ButtonWidget("stopwatch:toggle")
            {
                Kind = ButtonKind.Primary,
                Glyph = running ? Glyphs.Pause : Glyphs.Play,
                AccessibleName = running ? "Anhalten" : "Weiter",
            })
        {
            Spacing = 10,
            Justify = Justify.End,
        })
    {
        Orientation = StackOrientation.Vertical,
        Spacing = 8,
        Align = CrossAlign.Stretch,
    };

    // ───────── Aktionen ─────────

    public void OnAction(ActionInvocation invocation)
    {
        var id = invocation.ActionId;
        lock (_gate)
        {
            switch (id)
            {
                case ShowPanel:
                    _panel = !_panel;
                    break;
                case "back":
                    _panel = false;
                    break;
                case "timer:toggle" when _timerPaused is { } left:
                    _timerEnd = Now + left;
                    _timerPaused = null;
                    break;
                case "timer:toggle" when _timerEnd is { } end:
                    _timerPaused = Max(end - Now, TimeSpan.Zero);
                    break;
                case "timer:add":
                    _timerTotal += TimeSpan.FromMinutes(1);
                    if (_timerPaused is { } paused)
                    {
                        _timerPaused = paused + TimeSpan.FromMinutes(1);
                    }
                    else if (_timerEnd is { } running)
                    {
                        _timerEnd = running + TimeSpan.FromMinutes(1);
                    }
                    break;
                case "timer:stop":
                    _timerEnd = null;
                    _timerPaused = null;
                    break;
                case "stopwatch:start":
                    _stopwatchElapsed = TimeSpan.Zero;
                    _stopwatchStart = Now;
                    _stopwatchUsed = true;
                    _panel = false;
                    break;
                case "stopwatch:toggle" when _stopwatchStart is { } start:
                    _stopwatchElapsed += Now - start;
                    _stopwatchStart = null;
                    break;
                case "stopwatch:toggle":
                    _stopwatchStart = Now;
                    break;
                case "stopwatch:reset":
                    _stopwatchStart = null;
                    _stopwatchElapsed = TimeSpan.Zero;
                    _stopwatchUsed = false;
                    break;
                case "ok":
                    break;
            }
        }
        if (id.StartsWith("preset:", StringComparison.Ordinal) && int.TryParse(id["preset:".Length..], out var minutes))
        {
            StartTimer(TimeSpan.FromMinutes(minutes));
            return;
        }
        if (id == "again")
        {
            StartTimer(_timerTotal);
            return;
        }
        if (id == "ok")
        {
            _context?.End("done");
        }
        Update();
    }

    public void OnExpandedChanged(bool expanded)
    {
        if (!expanded && _panel)
        {
            lock (_gate)
            {
                _panel = false;
            }
            Publish();
        }
    }

    public ValueTask DisposeAsync()
    {
        _ticker?.Dispose();
        return ValueTask.CompletedTask;
    }
}
