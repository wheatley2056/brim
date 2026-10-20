using Brim.Core.Widgets;

namespace Brim.Core.Tests;

public class DashboardTests
{
    private static TilePlacement T(TileSize size, string key = "m/x") => new(key, size);

    [Fact]
    public void Default_is_the_activity_over_the_whole_page()
    {
        var now = new NowSettings();
        var slot = Assert.Single(Dashboard.Pack(now.Tiles));
        Assert.Equal(NowSettings.ActivityTile, now.Tiles[0].Key);
        Assert.Equal(new TileSlot(0, 0, 0, 0, 4, 2), slot);
    }

    [Fact]
    public void Tiles_flow_in_reading_order_and_fill_gaps()
    {
        // Groß links, zwei kleine rechts oben, ein mittleres rechts unten, dann neue Seite.
        var slots = Dashboard.Pack([T(TileSize.Large), T(TileSize.Small), T(TileSize.Small), T(TileSize.Medium), T(TileSize.Wide)]);

        Assert.Equal(new TileSlot(0, 0, 0, 0, 2, 2), slots[0]);
        Assert.Equal(new TileSlot(1, 0, 2, 0, 1, 1), slots[1]);
        Assert.Equal(new TileSlot(2, 0, 3, 0, 1, 1), slots[2]);
        Assert.Equal(new TileSlot(3, 0, 2, 1, 2, 1), slots[3]);
        Assert.Equal(new TileSlot(4, 1, 0, 0, 4, 1), slots[4]);
        Assert.Equal(2, Dashboard.PageCount(slots));
    }

    [Fact]
    public void Order_is_kept_across_pages()
    {
        // Der breite Streifen passt nicht mehr auf Seite 1; die kleine Kachel danach rutscht nicht zurück davor.
        var slots = Dashboard.Pack([T(TileSize.Wide), T(TileSize.Medium), T(TileSize.Large), T(TileSize.Small)]);

        Assert.Equal(0, slots[1].Page);
        Assert.Equal(1, slots[2].Page);
        Assert.Equal(1, slots[3].Page);
    }

    [Fact]
    public void Move_and_next_size()
    {
        var tiles = new[] { T(TileSize.Small, "a"), T(TileSize.Small, "b"), T(TileSize.Small, "c") };
        Assert.Equal(["b", "c", "a"], Dashboard.Move(tiles, 0, 2).Select(t => t.Key));
        Assert.Equal(["c", "a", "b"], Dashboard.Move(tiles, 2, 0).Select(t => t.Key));

        Assert.Equal(TileSize.Large, Dashboard.NextSize(TileSize.Medium, [TileSize.Small, TileSize.Medium, TileSize.Large]));
        Assert.Equal(TileSize.Small, Dashboard.NextSize(TileSize.Large, [TileSize.Small, TileSize.Medium, TileSize.Large]));
        Assert.Equal(TileSize.Small, Dashboard.NextSize(TileSize.Full, [TileSize.Small]));
    }

    [Fact]
    public void Hub_keeps_catalog_content_and_placements()
    {
        var hub = new ActivityHub();
        var context = new ModuleContext("clock", hub, new BrimSettings());
        var placed = 0;
        context.TilePlacementsChanged += () => placed++;

        context.DefineTile("analog", "Analog", [TileSize.Small, TileSize.Large], category: "Uhren");
        Assert.Equal("clock/analog", Assert.Single(hub.TileCatalog()).Key);

        hub.SetTilePlacements([("clock/analog", TileSize.Large)]);
        hub.SetTilePlacements([("clock/analog", TileSize.Large)]);
        Assert.Equal(1, placed);
        Assert.Equal([TileSize.Large], context.PlacedSizes("analog"));
        Assert.Empty(context.PlacedSizes("digital"));

        var changes = 0;
        hub.Changed += () => changes++;
        context.SetTile("analog", TileSize.Large, new TextWidget("12:00"));
        context.SetTile("analog", TileSize.Large, new TextWidget("12:00"));
        Assert.Equal(1, changes);
        Assert.Equal(new TextWidget("12:00"), hub.TileContent("clock/analog", TileSize.Large));

        hub.RemoveTiles("clock");
        Assert.Empty(hub.TileCatalog());
        Assert.Null(hub.TileContent("clock/analog", TileSize.Large));
    }
}

public class OnboardingTests
{
    [Fact]
    public void Runs_only_on_a_fresh_install()
    {
        Assert.True(Onboarding.ShouldRun(new BrimSettings(), settingsExisted: false));

        var updated = new BrimSettings();
        Assert.False(Onboarding.ShouldRun(updated, settingsExisted: true));
        Assert.True(updated.Onboarded);

        Assert.False(Onboarding.ShouldRun(new BrimSettings { Onboarded = true }, settingsExisted: false));
    }
}

public class IdleLayoutTests
{
    [Fact]
    public void Without_extras_the_notch_keeps_its_size_and_the_clock_its_place()
    {
        var center = IdleLayout.Arrange(ClockAlignment.Center, 60, []);
        Assert.Equal(NotchGeometry.Idle.Width, center.Width);
        Assert.Equal((center.Width - 60) / 2, center.ClockX);
        Assert.Equal(IdleLayout.Padding, IdleLayout.Arrange(ClockAlignment.Left, 60, []).ClockX);
    }

    [Fact]
    public void Centered_clock_stays_in_the_middle_with_values_on_both_sides()
    {
        var a = IdleLayout.Arrange(ClockAlignment.Center, 60, [80, 50, 40]);
        Assert.Equal([0, 2], a.Right);
        Assert.Equal([1], a.Left);
        // Breiteste Seite: 80 + 10 + 40 = 130 → 2 × 14 + 60 + 2 × (130 + 10).
        Assert.Equal(368, a.Width);
        Assert.Equal((a.Width - 60) / 2, a.ClockX);
    }

    [Fact]
    public void Left_and_right_put_all_values_beside_the_clock()
    {
        var left = IdleLayout.Arrange(ClockAlignment.Left, 60, [80, 50]);
        Assert.Equal([0, 1], left.Right);
        Assert.Equal(2 * 14 + 60 + 10 + 80 + 10 + 50, left.Width);
        Assert.Equal(14, left.ClockX);

        var right = IdleLayout.Arrange(ClockAlignment.Right, 60, [80, 50]);
        Assert.Equal([0, 1], right.Left);
        Assert.Equal(right.Width - 14 - 60, right.ClockX);

        Assert.Equal(IdleLayout.MaxWidth, IdleLayout.Arrange(ClockAlignment.Left, 60, [300, 300]).Width);
    }

    [Fact]
    public void Status_values_come_and_go()
    {
        var hub = new ActivityHub();
        var changes = 0;
        hub.Changed += () => changes++;
        hub.SetStatus("clock", "date", new Brim.Core.Widgets.TextWidget("Di. 20."));
        hub.SetStatus("clock", "date", new Brim.Core.Widgets.TextWidget("Di. 20."));
        Assert.Equal(1, changes);
        Assert.NotNull(hub.Status(StatusKeys.Date));
        hub.SetStatus("clock", "date", null);
        hub.SetStatus("clock", "date", null);
        Assert.Equal(2, changes);
        Assert.Null(hub.Status(StatusKeys.Date));

        Assert.Equal([StatusKeys.Load, StatusKeys.Battery, StatusKeys.NotificationDot], new NotchDisplaySettings { Load = true, Battery = true }.EnabledStatus());
    }
}
