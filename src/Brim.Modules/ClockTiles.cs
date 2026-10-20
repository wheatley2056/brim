using System.Globalization;
using Brim.Core;
using Brim.Core.Widgets;

namespace Brim.Modules;

/// <summary>
/// Uhren und Datum als Kacheln für „Jetzt“: digital und analog, mit und ohne Datum. Reine Funktion der Zeit,
/// damit sie ohne Shell testbar ist; das <see cref="ClockModule"/> ruft sie jede Sekunde für gelegte Kacheln auf.
/// </summary>
public static class ClockTiles
{
    public const string Category = "Uhren";

    /// <summary>Alles, was die Galerie unter „Uhren“ zeigt.</summary>
    public static readonly IReadOnlyList<(string Id, string Name, string Description, IReadOnlyList<TileSize> Sizes)> All =
    [
        ("digital", "Digital", "Uhrzeit groß", [TileSize.Small, TileSize.Medium, TileSize.Wide]),
        ("digital-seconds", "Digital mit Sekunden", "Uhrzeit mit Sekunden", [TileSize.Small, TileSize.Medium, TileSize.Wide]),
        ("digital-date", "Digital mit Datum", "Uhrzeit, Wochentag und Datum", [TileSize.Medium, TileSize.Wide, TileSize.Large]),
        ("analog", "Analog", "Zifferblatt mit Strichen", [TileSize.Small, TileSize.Large]),
        ("analog-minimal", "Analog minimal", "Nur Zeiger und vier Punkte", [TileSize.Small, TileSize.Large]),
        ("analog-numbers", "Analog mit Ziffern", "Zifferblatt mit 12, 3, 6 und 9", [TileSize.Small, TileSize.Large]),
        ("analog-date", "Analog mit Datum", "Zifferblatt und Datum daneben", [TileSize.Medium, TileSize.Large]),
        ("date", "Kalenderblatt", "Wochentag, Tag und Monat", [TileSize.Small, TileSize.Medium]),
    ];

    public static Widget Build(string id, TileSize size, DateTimeOffset now, CultureInfo culture)
    {
        var (width, height) = Dashboard.ContentSize(size);
        var time = now.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        var timeWithSeconds = now.ToString(culture.DateTimeFormat.LongTimePattern, culture);
        var weekday = now.ToString("dddd", culture);
        var dayMonth = now.ToString(culture.DateTimeFormat.MonthDayPattern, culture);
        var week = $"KW {ISOWeek.GetWeekOfYear(now.DateTime)}";
        var shortDate = now.ToString("ddd, d. MMM", culture);

        switch (id)
        {
            case "digital":
                return Centered(Digital(time, size switch { TileSize.Small => 30, TileSize.Medium => 40, _ => 46 }));
            case "digital-seconds":
                return Centered(Digital(timeWithSeconds, size switch { TileSize.Small => 22, TileSize.Medium => 34, _ => 44 }));
            case "digital-date":
                return size switch
                {
                    TileSize.Medium => Row(
                        Digital(time, 34),
                        Lines(weekday, dayMonth, 13)),
                    TileSize.Wide => Row(
                        Digital(timeWithSeconds, 42),
                        Lines(weekday, $"{dayMonth} · {week}", 15)),
                    _ => Column(
                        Digital(time, 54),
                        new TextWidget(weekday) { Style = TextStyle.Title, FontSize = 17 },
                        new TextWidget($"{dayMonth} · {week}") { Style = TextStyle.Secondary }),
                };
            case "analog" or "analog-minimal" or "analog-numbers":
                return Centered(Face(id, now, Math.Min(width, height)));
            case "analog-date":
                return size == TileSize.Medium
                    ? Row(Face("analog", now, height), Lines(weekday, dayMonth, 13))
                    : Column(Face("analog", now, height - 34), new TextWidget($"{shortDate} · {week}") { Style = TextStyle.Secondary });
            case "date":
                var day = new TextWidget(now.Day.ToString(culture)) { Style = TextStyle.Display, FontSize = size == TileSize.Small ? 32 : 40 };
                var month = now.ToString("MMMM yyyy", culture);
                return size == TileSize.Small
                    ? Column(new TextWidget(weekday) { Style = TextStyle.Caption, Color = BrimColors.Danger }, day)
                    : Row(day, Lines(weekday, $"{month} · {week}", 13));
            default:
                return new TextWidget(time);
        }
    }

    private static TextWidget Digital(string text, double fontSize) =>
        new(text) { Style = TextStyle.Display, FontSize = fontSize, AccessibleName = text };

    private static ClockFaceWidget Face(string id, DateTimeOffset now, double size) =>
        new(now.Hour, now.Minute, now.Second + now.Millisecond / 1000.0)
        {
            Size = Math.Max(16, Math.Floor(size)),
            Style = id switch { "analog-minimal" => ClockFaceStyle.Minimal, "analog-numbers" => ClockFaceStyle.Numbers, _ => ClockFaceStyle.Classic },
            AccessibleName = now.ToString("t", CultureInfo.CurrentCulture),
        };

    private static StackWidget Lines(string first, string second, double fontSize) =>
        new(new TextWidget(first) { Style = TextStyle.Title, FontSize = fontSize + 2 },
            new TextWidget(second) { Style = TextStyle.Secondary, FontSize = fontSize })
        {
            Orientation = StackOrientation.Vertical,
            Align = CrossAlign.Start,
            Spacing = 1,
            Grow = 1,
        };

    private static StackWidget Centered(Widget content) => new(content) { Justify = Justify.Center, Grow = 1 };

    private static StackWidget Row(params Widget[] children) => new(children) { Spacing = 14 };

    private static StackWidget Column(params Widget[] children) => new(children)
    {
        Orientation = StackOrientation.Vertical,
        Align = CrossAlign.Center,
        Justify = Justify.Center,
        Spacing = 2,
    };
}
