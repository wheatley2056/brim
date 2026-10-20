using System.Globalization;
using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules.Sensors;
using Windows.ApplicationModel.Appointments;
using Windows.System;

namespace Brim.Modules;

/// <summary>Ein Termin, wie das Modul ihn braucht.</summary>
public sealed record CalendarEntry(string Subject, DateTimeOffset Start, TimeSpan Duration, string? Location, Uri? MeetingLink)
{
    public DateTimeOffset End => Start + Duration;
}

/// <summary>
/// Kalender (A3, Plan 4.3 und 4.5): nächster Termin aus dem Windows-Kalender (<c>AppointmentStore</c>, braucht
/// Paketidentität). 15 Minuten vorher erscheint er kompakt mit Countdown, zu Beginn kurz als HUD; im Tab
/// „Jetzt“ steht er unter der Uhr. Online-Besprechungen bekommen „Beitreten“.
/// </summary>
public sealed class CalendarModule : IBrimModule
{
    public const string ModuleId = "calendar";

    /// <summary>So lange vor Beginn erscheint der Termin in der kompakten Notch.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(15);

    /// <summary>So lange nach Beginn bleibt er dort (zum Beitreten).</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset, Task<IReadOnlyList<CalendarEntry>>>? _source;
    private readonly Lock _gate = new();
    private ModuleContext? _context;
    private AppointmentStore? _store;
    private ITimer? _timer;
    private IReadOnlyList<CalendarEntry> _entries = [];
    private string? _announced;

    public CalendarModule()
    {
    }

    /// <summary>Für Tests: Termine ab einem Zeitpunkt.</summary>
    public CalendarModule(Func<DateTimeOffset, Task<IReadOnlyList<CalendarEntry>>> source)
    {
        _source = source;
    }

    public string Id => ModuleId;
    public string DisplayName => "Kalender";

    /// <summary>Für die Uhr im Tab „Jetzt“: „Nächster Termin: 14:00 · Teamrunde“.</summary>
    public string? NextSummary
    {
        get
        {
            var next = Next(_context?.Time.GetUtcNow() ?? DateTimeOffset.Now);
            return next is null ? null : $"Nächster Termin: {next.Start.ToLocalTime():t} · {next.Subject}";
        }
    }

    public async Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        context.DefineTile("agenda", "Termine", [TileSize.Medium, TileSize.Wide, TileSize.Large], "Kalender", Glyphs.Calendar,
            "Die nächsten Termine aus dem Windows-Kalender");
        context.TilePlacementsChanged += () => PublishTiles(context.Time.GetUtcNow());
        if (_source is null)
        {
            if (!Identity.Present)
            {
                Log.Info("Kalender braucht Paketidentität (Einstellungen → Kalender und Benachrichtigungen)");
                FeatureStatus.Calendar = "Aus: Brim läuft ohne Paketidentität.";
                return;
            }
            try
            {
                _store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
                if (_store is null)
                {
                    // Zugriff in Windows verweigert (Datenschutz → Kalender).
                    FeatureStatus.Calendar = "Aus: Windows verweigert den Zugriff (Einstellungen → Datenschutz → Kalender).";
                    Log.Info("Kalender: Zugriff verweigert");
                    return;
                }
                var calendars = await _store.FindAppointmentCalendarsAsync();
                FeatureStatus.Calendar = calendars.Count == 0
                    ? "Kein Kalender gefunden. Brim liest Konten aus der Windows-Kalender-App bzw. dem neuen Outlook, nicht aus dem klassischen Outlook."
                    : $"Aktiv: {calendars.Count} Kalender ({string.Join(", ", calendars.Select(c => c.DisplayName).Take(5))}).";
                Log.Info($"Kalender: {calendars.Count} Kalender");
                _store.ChangeTracker.Enable();
                _store.StoreChanged += (_, _) => _ = RefreshAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("Kalender nicht verfügbar", ex);
                FeatureStatus.Calendar = $"Fehler: {ex.Message}";
                return;
            }
        }
        await RefreshAsync();
        // Jede Minute: Countdown weiterzählen und neu lesen (auch ohne Änderungsmeldung).
        _timer = context.Time.CreateTimer(_ => _ = RefreshAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private async Task RefreshAsync()
    {
        if (_context is not { } context)
        {
            return;
        }
        var now = context.Time.GetUtcNow();
        try
        {
            var entries = _source is not null ? await _source(now) : await ReadStoreAsync(now);
            lock (_gate)
            {
                _entries = entries;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Termine nicht lesbar", ex);
        }
        Publish(now);
    }

    private async Task<IReadOnlyList<CalendarEntry>> ReadStoreAsync(DateTimeOffset now)
    {
        var options = new FindAppointmentsOptions { MaxCount = 30 };
        foreach (var property in new[]
        {
            AppointmentProperties.Subject, AppointmentProperties.StartTime, AppointmentProperties.Duration,
            AppointmentProperties.Location, AppointmentProperties.AllDay, AppointmentProperties.OnlineMeetingLink,
            AppointmentProperties.IsCanceledMeeting,
        })
        {
            options.FetchProperties.Add(property);
        }
        var found = await _store!.FindAppointmentsAsync(now - TimeSpan.FromHours(1), TimeSpan.FromHours(25), options);
        return found
            .Where(a => !a.AllDay && !a.IsCanceledMeeting)
            .Select(a => new CalendarEntry(
                string.IsNullOrWhiteSpace(a.Subject) ? "Termin" : a.Subject.Trim(),
                a.StartTime,
                a.Duration,
                string.IsNullOrWhiteSpace(a.Location) ? null : a.Location.Trim(),
                Uri.TryCreate(a.OnlineMeetingLink, UriKind.Absolute, out var link) ? link : null))
            .ToList();
    }

    /// <summary>Der nächste Termin, der noch nicht vorbei ist (laufende zuerst, bis <see cref="Linger"/> nach Beginn).</summary>
    public CalendarEntry? Next(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _entries
                .Where(e => e.End > now && e.Start + Linger > now || e.Start > now)
                .OrderBy(e => e.Start)
                .FirstOrDefault();
        }
    }

    /// <summary>„in 12 min“, „jetzt“.</summary>
    public static string Countdown(CalendarEntry entry, DateTimeOffset now)
    {
        var minutes = (int)Math.Ceiling((entry.Start - now).TotalMinutes);
        return minutes <= 0 ? "jetzt" : $"in {minutes} min";
    }

    // ───────── Kachel für „Jetzt“ ─────────

    private void PublishTiles(DateTimeOffset now)
    {
        if (_context is not { } context)
        {
            return;
        }
        foreach (var size in context.PlacedSizes("agenda"))
        {
            context.SetTile("agenda", size, AgendaTile(size, now, Identity.Present || _source is not null));
        }
    }

    /// <summary>Kommende Termine (laufende zuerst); oben der nächste groß.</summary>
    public Widget AgendaTile(TileSize size, DateTimeOffset now, bool available)
    {
        List<CalendarEntry> upcoming;
        lock (_gate)
        {
            upcoming = _entries.Where(e => e.End > now).OrderBy(e => e.Start).ToList();
        }
        if (upcoming.Count == 0)
        {
            return TileCard.Card(
                TileCard.Header(Glyphs.Calendar, BrimColors.Danger, "Termine"),
                TileCard.Empty(Glyphs.Calendar, available ? "Heute keine Termine mehr" : "Kalender einrichten (Einstellungen)"));
        }
        var culture = CultureInfo.CurrentCulture;
        var rows = size switch { TileSize.Large => 3, _ => 1 };
        var items = new List<Widget>();
        foreach (var entry in upcoming.Take(rows))
        {
            var countdown = entry.Start <= now ? "jetzt" : Countdown(entry, now);
            var soon = entry.Start - now <= TimeSpan.FromMinutes(5);
            var when = entry.Start.ToLocalTime().ToString("t", culture);
            var line = new List<Widget>
            {
                new StackWidget(
                    new TextWidget(entry.Subject) { Style = TextStyle.Title, FontSize = 14 },
                    new TextWidget(entry.Location is null ? when : $"{when} · {entry.Location}") { Style = TextStyle.Secondary, FontSize = 12 })
                {
                    Orientation = StackOrientation.Vertical,
                    Align = CrossAlign.Stretch,
                    Grow = 1,
                },
            };
            if (size != TileSize.Medium && entry.MeetingLink is not null && entry == upcoming[0])
            {
                line.Add(new ButtonWidget("join") { Kind = ButtonKind.Secondary, Label = "Beitreten" });
            }
            line.Add(new TextWidget(countdown) { Style = TextStyle.Mono, Color = soon ? BrimColors.Warning : BrimColors.Slate });
            items.Add(new StackWidget(line) { Spacing = 10 });
        }
        return TileCard.Card(
            TileCard.Header(Glyphs.Calendar, BrimColors.Danger, "Termine", upcoming.Count > rows ? $"+{upcoming.Count - rows}" : null),
            new StackWidget(items)
            {
                Orientation = StackOrientation.Vertical,
                Align = CrossAlign.Stretch,
                Spacing = 8,
            });
    }

    private void Publish(DateTimeOffset now)
    {
        var context = _context!;
        PublishTiles(now);
        var next = Next(now);
        if (next is null || next.Start - now > Lead || now - next.Start > Linger)
        {
            context.End("next");
            return;
        }
        var countdown = Countdown(next, now);
        var soon = next.Start - now <= TimeSpan.FromMinutes(5);
        var color = soon ? BrimColors.Warning : BrimColors.Fog;
        var culture = CultureInfo.CurrentCulture;
        var time = $"{next.Start.ToLocalTime().ToString("t", culture)}–{next.End.ToLocalTime().ToString("t", culture)}";

        var buttons = new List<Widget> { new SpacerWidget { Grow = 1 } };
        if (next.MeetingLink is not null)
        {
            buttons.Add(new ButtonWidget("join") { Kind = ButtonKind.Secondary, Label = "Beitreten" });
        }
        context.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "next",
            Priority = ActivityPriority.Ongoing,
            Rank = 12,
            Leading = new IconWidget(Glyphs.Calendar) { Size = 15, Color = color },
            Trailing = new TextWidget(countdown) { Style = TextStyle.Mono, Color = color, AccessibleName = $"{next.Subject} {countdown}" },
            Bead = new IconWidget(Glyphs.Calendar) { Size = 14, Color = color, AccessibleName = next.Subject },
            Announcement = $"{next.Subject} {countdown}",
            Expanded = new StackWidget(
                new StackWidget(
                    new IconWidget(Glyphs.Calendar) { Size = 22, Color = color },
                    new StackWidget(
                        new TextWidget(next.Subject) { Style = TextStyle.Title, FontSize = 17 },
                        new TextWidget(next.Location is null ? time : $"{time} · {next.Location}") { Style = TextStyle.Secondary })
                    {
                        Orientation = StackOrientation.Vertical,
                        Spacing = 2,
                        Align = CrossAlign.Start,
                        Grow = 1,
                    },
                    new TextWidget(countdown) { Style = TextStyle.Display, FontSize = 28, Color = color })
                {
                    Spacing = 12,
                },
                new SpacerWidget { Grow = 1 },
                new StackWidget(buttons))
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 8,
                Align = CrossAlign.Stretch,
            },
        });

        // Zu Beginn einmal kurz melden.
        var key = next.Subject + next.Start.ToUnixTimeSeconds();
        if (now >= next.Start && _announced != key)
        {
            _announced = key;
            context.Post(new Activity
            {
                ModuleId = ModuleId,
                Id = "hud",
                Priority = ActivityPriority.Transient,
                Duration = TimeSpan.FromSeconds(4),
                Announcement = $"Jetzt: {next.Subject}",
                Content = new StackWidget(
                    new IconWidget(Glyphs.Calendar) { Size = 16, Color = BrimColors.Warning },
                    new TextWidget("Jetzt") { Style = TextStyle.Secondary },
                    new TextWidget(next.Subject) { Grow = 1 })
                {
                    Spacing = 10,
                    Padding = (18, 0, 18, 0),
                },
            });
        }
    }

    public void OnAction(ActionInvocation invocation)
    {
        if (invocation.ActionId == "join" && Next(_context!.Time.GetUtcNow())?.MeetingLink is { } link)
        {
            _ = Launcher.LaunchUriAsync(link);
        }
    }

    public ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
