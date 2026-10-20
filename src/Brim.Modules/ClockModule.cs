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
        Publish();
        ScheduleNext();
        return Task.CompletedTask;
    }

    private void ScheduleNext()
    {
        var now = _context!.Time.GetLocalNow();
        var showSeconds = _context.Settings.Clock.ShowSeconds;
        // Auf die nächste volle Minute (bzw. Sekunde) ausrichten, damit die Anzeige nie nachhinkt.
        var next = showSeconds
            ? now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)).AddSeconds(1)
            : now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMinute)).AddMinutes(1);
        var due = next - now + TimeSpan.FromMilliseconds(20);
        _timer?.Dispose();
        _timer = _context.Time.CreateTimer(_ =>
        {
            Publish();
            ScheduleNext();
        }, null, due, Timeout.InfiniteTimeSpan);
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
        var week = ISOWeek.GetWeekOfYear(now.DateTime);
        var date = now.ToString("dddd", culture) + ", " + now.ToString(culture.DateTimeFormat.MonthDayPattern, culture);

        context.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "time",
            Priority = ActivityPriority.Idle,
            Content = new TextWidget(time) { Style = TextStyle.Mono, AccessibleName = time },
            // Die Uhrzeit selbst wandert beim Aufklappen in die Kopfzeile; hier steht groß das Datum.
            Expanded = new StackWidget(
                new TextWidget(date) { Style = TextStyle.Display, FontSize = 30 },
                new TextWidget($"Kalenderwoche {week}") { Style = TextStyle.Secondary, FontSize = 14 },
                new SpacerWidget { Grow = 1 },
                new StackWidget(
                    new TextWidget(_agenda?.Invoke() ?? "Gerade läuft nichts.") { Style = TextStyle.Secondary, Grow = 1 },
                    new ButtonWidget("timer") { Glyph = Glyphs.Stopwatch, GlyphColor = BrimColors.Slate, AccessibleName = "Timer und Stoppuhr" },
                    new ButtonWidget("output") { Glyph = Glyphs.Speakers, GlyphColor = BrimColors.Slate, AccessibleName = "Audioausgabe wählen" },
                    new ButtonWidget("mixer") { Glyph = Glyphs.Volume, GlyphColor = BrimColors.Slate, AccessibleName = "Lautstärke" }))
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 4,
                Align = CrossAlign.Stretch,
            },
        });
    }

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
