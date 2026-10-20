using System.Globalization;
using Brim.Core;
using Brim.Core.Widgets;

namespace Brim.Modules;

/// <summary>Uhrzeit (A5): Standardinhalt im Ruhezustand, Datum und Kalenderwoche im erweiterten Zustand.</summary>
public sealed class ClockModule : IBrimModule
{
    public const string ModuleId = "clock";

    private readonly Func<string?>? _agenda;
    private ModuleContext? _context;
    private ITimer? _timer;
    private readonly Lock _timerGate = new();

    /// <param name="agenda">Liefert den nächsten Termin für den Tab „Jetzt“ (Kalender), sonst <c>null</c>.</param>
    public ClockModule(Func<string?>? agenda = null)
    {
        _agenda = agenda;
    }

    public string Id => ModuleId;
    public string DisplayName => "Uhr";

    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        foreach (var (id, name, description, sizes) in ClockTiles.All)
        {
            context.DefineTile(id, name, sizes, ClockTiles.Category, Glyphs.Clock, description);
        }
        context.DefineTile(QuickTile, "Schnellzugriff", [TileSize.Medium, TileSize.Small], "Brim", Glyphs.LightningBolt, "Timer, Audioausgabe und Lautstärke");
        context.TilePlacementsChanged += () =>
        {
            PublishTiles();
            ScheduleNext();
        };
        Publish();
        ScheduleNext();
        return Task.CompletedTask;
    }

    private void ScheduleNext()
    {
        // Vom Timer und von der Shell (neue Kacheln) aufgerufen: nie zwei Timer gleichzeitig.
        lock (_timerGate)
        {
            ScheduleNextLocked();
        }
    }

    private void ScheduleNextLocked()
    {
        var now = _context!.Time.GetLocalNow();
        // Kacheln mit Sekundenzeiger oder Sekunden brauchen jede Sekunde ein Bild.
        var showSeconds = _context.Settings.Clock.ShowSeconds || TilesPlaced;
        // Auf die nächste volle Minute (bzw. Sekunde) ausrichten, damit die Anzeige nie nachhinkt.
        var next = showSeconds
            ? now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)).AddSeconds(1)
            : now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMinute)).AddMinutes(1);
        var due = next - now + TimeSpan.FromMilliseconds(20);
        _timer?.Dispose();
        _timer = _context.Time.CreateTimer(_ =>
        {
            Publish();
            PublishTiles();
            ScheduleNext();
        }, null, due, Timeout.InfiniteTimeSpan);
    }

    private bool TilesPlaced => _context is { } context && ClockTiles.All.Any(t => context.PlacedSizes(t.Id).Count > 0);

    /// <summary>Uhren-Kacheln, die liegen, solange „Jetzt“ irgendwo offen ist.</summary>
    private void PublishTiles()
    {
        if (_context is not { } context || !context.IsTabVisible(NotchTab.Now))
        {
            return;
        }
        foreach (var size in context.PlacedSizes(QuickTile))
        {
            context.SetTile(QuickTile, size, QuickTileContent(size));
        }
        var now = context.Time.GetLocalNow();
        foreach (var (id, _, _, _) in ClockTiles.All)
        {
            foreach (var size in context.PlacedSizes(id))
            {
                context.SetTile(id, size, ClockTiles.Build(id, size, now, CultureInfo.CurrentCulture));
            }
        }
    }

    /// <summary>Datum und Sekunden wirken sofort: neu zeichnen und den Takt (Minute oder Sekunde) neu ansetzen.</summary>
    public void OnSettingsChanged()
    {
        Publish();
        ScheduleNext();
    }

    public void OnTabVisibilityChanged(NotchTab tab, bool visible)
    {
        if (tab == NotchTab.Now && visible)
        {
            PublishTiles();
        }
    }

    private void Publish()
    {
        var context = _context!;
        var now = context.Time.GetLocalNow();
        var culture = CultureInfo.CurrentCulture;
        var timePattern = culture.DateTimeFormat.ShortTimePattern;
        if (context.Settings.Clock.ShowSeconds)
        {
            timePattern = culture.DateTimeFormat.LongTimePattern;
        }
        var time = now.ToString(timePattern, culture);
        // Datum neben der Uhr in der ruhenden Notch (Einstellungen → Aussehen → In der Notch).
        var display = context.Settings.Display;
        context.SetStatus("date", display.Date
            ? new TextWidget(now.ToString(DateFormats.Pattern(display.DateFormat), culture)) { Style = TextStyle.Status, AccessibleName = now.ToString("D", culture) }
            : null);
        var week = ISOWeek.GetWeekOfYear(now.DateTime);
        var weekday = now.ToString("dddd", culture);
        var date = now.ToString(culture.DateTimeFormat.MonthDayPattern, culture);
        var agenda = _agenda?.Invoke();

        context.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "time",
            Priority = ActivityPriority.Idle,
            Content = new TextWidget(time) { Style = TextStyle.Mono, AccessibleName = time },
            // Die Uhrzeit selbst wandert beim Aufklappen in die Kopfzeile. Hier steht „Heute“: Wochentag, Datum,
            // Woche und nächster Termin, unten der Schnellzugriff (Entwurf „Jetzt neu“).
            Expanded = new StackWidget(
                new TextWidget(weekday.ToUpper(culture)) { Style = TextStyle.Caption, FontSize = 11, Color = BrimColors.DefaultAccent },
                new TextWidget(date) { Style = TextStyle.Display, FontSize = 30, AccessibleName = $"{weekday}, {date}" },
                new TextWidget(agenda is null ? $"Kalenderwoche {week}" : $"KW {week} · {agenda}") { Style = TextStyle.Secondary, FontSize = 12 },
                new SpacerWidget { Grow = 1 },
                QuickActions(40, 18))
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 2,
                Align = CrossAlign.Stretch,
            },
        });
    }

    public const string QuickTile = "quick";

    /// <summary>Timer, Audioausgabe und Lautstärke als runde Knöpfe – in „Heute“ und als Kachel „Schnellzugriff“.</summary>
    public static StackWidget QuickActions(double size, double glyphSize) =>
        new(new ButtonWidget("timer") { Glyph = Glyphs.Stopwatch, Kind = ButtonKind.Round, Size = size, GlyphSize = glyphSize, AccessibleName = "Timer und Stoppuhr" },
            new ButtonWidget("output") { Glyph = Glyphs.Speakers, Kind = ButtonKind.Round, Size = size, GlyphSize = glyphSize, AccessibleName = "Audioausgabe wählen" },
            new ButtonWidget("mixer") { Glyph = Glyphs.Volume, Kind = ButtonKind.Round, Size = size, GlyphSize = glyphSize, AccessibleName = "Lautstärke" })
        {
            Spacing = 8,
        };

    /// <summary>Kachel „Schnellzugriff“: klein nur die Knöpfe, mittel mit Kopfzeile.</summary>
    public static Widget QuickTileContent(TileSize size) => size == TileSize.Small
        ? new StackWidget(QuickActions(34, 15) with { Spacing = 5 }) { Justify = Justify.Center, Grow = 1 }
        : TileCard.Card(TileCard.Header(Glyphs.LightningBolt, BrimColors.DefaultAccent, "Schnellzugriff"), QuickActions(36, 16) with { Spacing = 12 });

    /// <summary>Audio-Menüs sind auch ohne laufende Musik erreichbar.</summary>
    public void OnAction(ActionInvocation invocation)
    {
        switch (invocation.ActionId)
        {
            case "output":
                _context?.Send(AudioModule.ModuleId, AudioModule.ShowOutput);
                break;
            case "mixer":
                _context?.Send(AudioModule.ModuleId, AudioModule.ShowMixer);
                break;
            case "timer":
                _context?.Send(TimerModule.ModuleId, TimerModule.ShowPanel);
                break;
        }
    }

    public ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
