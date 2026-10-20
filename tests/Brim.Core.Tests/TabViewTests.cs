using Brim.Core;
using Brim.Core.Widgets;

namespace Brim.Core.Tests;

public class TabViewTests
{
    private readonly ActivityHub _hub = new();

    private static TabView View(string module, string id, TabColumn column = TabColumn.Main, int order = 0, NotchTab tab = NotchTab.System) =>
        new() { ModuleId = module, Id = id, Content = new TextWidget(id), Column = column, Order = order, Tab = tab };

    [Fact]
    public void Views_are_sorted_by_column_and_order()
    {
        _hub.SetView(View("resources", "processes", TabColumn.Side, 10));
        _hub.SetView(View("battery", "card", TabColumn.Side, 0));
        _hub.SetView(View("resources", "meters"));
        _hub.SetView(View("other", "elsewhere", tab: NotchTab.Plugins));

        Assert.Equal(["resources/meters", "battery/card", "resources/processes"], _hub.Views(NotchTab.System).Select(v => v.Key));
    }

    [Fact]
    public void Setting_again_replaces_and_removing_raises_changed()
    {
        var changes = 0;
        _hub.Changed += () => changes++;

        _hub.SetView(View("battery", "card"));
        _hub.SetView(View("battery", "card") with { Order = 5 });
        _hub.RemoveView("battery", "card");
        _hub.RemoveView("battery", "card");

        Assert.Empty(_hub.Views(NotchTab.System));
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Ending_a_module_also_removes_its_views()
    {
        _hub.SetView(View("battery", "card"));
        _hub.SetView(View("resources", "meters"));

        _hub.EndAll("battery");

        Assert.Equal(["resources/meters"], _hub.Views(NotchTab.System).Select(v => v.Key));
    }

    [Fact]
    public void A_tab_stays_visible_while_any_notch_shows_it()
    {
        var events = new List<(NotchTab, bool)>();
        _hub.TabVisibilityChanged += (tab, visible) => events.Add((tab, visible));
        object first = new(), second = new();

        _hub.NotifyVisibleTab(first, NotchTab.System);
        _hub.NotifyVisibleTab(second, NotchTab.System);
        _hub.NotifyVisibleTab(first, null);
        Assert.True(_hub.IsTabVisible(NotchTab.System));

        _hub.NotifyVisibleTab(second, NotchTab.Shelf);

        Assert.False(_hub.IsTabVisible(NotchTab.System));
        Assert.Equal([(NotchTab.System, true), (NotchTab.Shelf, true), (NotchTab.System, false)], events);
    }

    [Fact]
    public void Repeating_the_same_tab_reports_nothing()
    {
        var events = 0;
        _hub.TabVisibilityChanged += (_, _) => events++;
        var window = new object();

        _hub.NotifyVisibleTab(window, NotchTab.System);
        _hub.NotifyVisibleTab(window, NotchTab.System);
        _hub.NotifyVisibleTab(window, NotchTab.System);

        Assert.Equal(1, events);
    }

    [Fact]
    public void Battery_glyph_has_eleven_levels_per_kind()
    {
        Assert.Equal(char.ConvertFromUtf32(0xEBA0), Glyphs.Battery(0, charging: false, saver: false));
        Assert.Equal(char.ConvertFromUtf32(0xEBAA), Glyphs.Battery(100, charging: false, saver: false));
        Assert.Equal(char.ConvertFromUtf32(0xEBAB + 6), Glyphs.Battery(64, charging: true, saver: false));
        Assert.Equal(char.ConvertFromUtf32(0xEBB6 + 2), Glyphs.Battery(18, charging: false, saver: true));
    }
}
