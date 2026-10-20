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
