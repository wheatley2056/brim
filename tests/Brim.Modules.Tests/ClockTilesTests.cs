using System.Globalization;
using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules;

namespace Brim.Modules.Tests;

public class ClockTilesTests
{
    private static readonly CultureInfo German = new("de-DE");
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 14, 32, 5, TimeSpan.FromHours(2));

    [Fact]
    public void Every_clock_builds_a_valid_widget_in_every_size_it_offers()
    {
        foreach (var (id, _, _, sizes) in ClockTiles.All)
        {
            foreach (var size in sizes)
            {
                var widget = ClockTiles.Build(id, size, Now, German);
                Assert.Null(WidgetValidator.Validate(widget));
            }
        }
    }

    [Fact]
    public void Digital_and_analog_show_the_time()
    {
        var texts = WidgetTree.All(ClockTiles.Build("digital-date", TileSize.Wide, Now, German)).OfType<TextWidget>().Select(t => t.Text).ToList();
        Assert.Contains("14:32:05", texts);
        Assert.Contains("Dienstag", texts);
        Assert.Contains(texts, t => t.Contains("KW 43"));

        var face = WidgetTree.All(ClockTiles.Build("analog-minimal", TileSize.Large, Now, German)).OfType<ClockFaceWidget>().Single();
        Assert.Equal((14, 32, 5), ((int)face.Hour, (int)face.Minute, (int)face.Second));
        Assert.Equal(ClockFaceStyle.Minimal, face.Style);
        // Große Kachel: Zifferblatt so hoch wie der Platz (170 − 2 × 12).
        Assert.Equal(146, face.Size);
    }
}
