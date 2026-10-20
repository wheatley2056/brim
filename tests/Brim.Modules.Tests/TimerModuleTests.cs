using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Modules.Tests;

public class TimerModuleTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly TimerModule _module = new();

    public TimerModuleTests()
    {
        _hub = new ActivityHub(_time);
        _module.StartAsync(new ModuleContext(TimerModule.ModuleId, _hub, new BrimSettings()), CancellationToken.None).Wait();
    }

    private ActiveActivity? Find(string id) => _hub.Snapshot().FirstOrDefault(a => a.Key == "timer/" + id);

    private void Press(string action) => _module.OnAction(new ActionInvocation(TimerModule.ModuleId, action));

    private string Trailing(string id) => ((TextWidget)Find(id)!.Activity.Trailing!).Text;

    [Theory]
    [InlineData(299.2, false, "5:00")]
    [InlineData(59, false, "0:59")]
    [InlineData(3723, false, "1:02:03")]
    [InlineData(0, false, "0:00")]
    public void Format(double seconds, bool tenths, string expected)
    {
        Assert.Equal(expected, TimerModule.Format(TimeSpan.FromSeconds(seconds), tenths));
    }

    [Fact]
    public void Preset_runs_down_and_ends_with_a_critical_notice()
    {
        Press(TimerModule.ShowPanel);
        Assert.NotNull(Find("panel"));

        Press("preset:1");
        Assert.Null(Find("panel"));
        Assert.Equal("1:00", Trailing("timer"));

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("0:30", Trailing("timer"));

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(Find("timer"));
        Assert.Equal(ActivityPriority.Critical, Find("done")?.Priority);

        Press("again");
        Assert.Null(Find("done"));
        Assert.Equal("1:00", Trailing("timer"));
    }

    [Fact]
    public void Pause_freezes_and_plus_one_minute_extends()
    {
        Press("preset:5");
        _time.Advance(TimeSpan.FromSeconds(60));
        Press("timer:toggle");
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("4:00", Trailing("timer"));
        Assert.Null(Find("done"));

        Press("timer:add");
        Press("timer:toggle");
        Assert.Equal("5:00", Trailing("timer"));

        Press("timer:stop");
        Assert.Null(Find("timer"));
    }

    [Fact]
    public void Stopwatch_counts_pauses_and_resets()
    {
        Press("stopwatch:start");
        _time.Advance(TimeSpan.FromSeconds(83));
        Assert.Equal("1:23", Trailing("stopwatch"));
        Assert.False(Find("stopwatch")!.Activity.IsBackground);

        Press("stopwatch:toggle");
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("1:23", Trailing("stopwatch"));
        Assert.True(Find("stopwatch")!.Activity.IsBackground);

        Press("stopwatch:reset");
        Assert.Null(Find("stopwatch"));
    }
}

public class BluetoothModuleTests
{
    private sealed class FakeSource : IBluetoothSource
    {
        public event Action<BluetoothGadget, bool>? Connected;
        public event Action<BluetoothGadget>? Updated;
        public event Action<string>? Disconnected;
        public void Start() { }
        public void Connect(BluetoothGadget gadget, bool initial) => Connected?.Invoke(gadget, initial);
        public void Update(BluetoothGadget gadget) => Updated?.Invoke(gadget);
        public void Disconnect(string id) => Disconnected?.Invoke(id);
        public void Dispose() { }
    }

    [Fact]
    public async Task New_connections_show_a_hud_with_battery_but_initial_ones_do_not()
    {
        var hub = new ActivityHub(new FakeTimeProvider());
        var source = new FakeSource();
        var module = new BluetoothModule(() => source);
        await module.StartAsync(new ModuleContext(BluetoothModule.ModuleId, hub, new BrimSettings()), CancellationToken.None);

        source.Connect(new BluetoothGadget("1", "MX Master", 90), initial: true);
        Assert.Empty(hub.Snapshot());

        source.Connect(new BluetoothGadget("2", "AirPods Pro", 15), initial: false);
        var hud = Assert.Single(hub.Snapshot());
        Assert.Equal(ActivityPriority.Transient, hud.Priority);
        Assert.Equal("AirPods Pro verbunden, Akku 15 %", hud.Activity.Announcement);
        Assert.True(BluetoothModule.LooksLikeHeadphones("Sony WH-1000XM5"));
    }

    [Theory]
    [InlineData("BluetoothLE#BluetoothLEb4:8c:9d:01:02:03-c8:a0:f1:2b:4e:21", "C8A0F12B4E21")]
    [InlineData("Bluetooth#Bluetooth00:1a:7d:da:71:13-00:1b:66:aa:bb:cc", "001B66AABBCC")]
    [InlineData("kaputt", null)]
    public void Device_node_is_found_by_the_remote_address(string endpoint, string? expected)
    {
        Assert.Equal(expected, BluetoothModule.RemoteAddress(endpoint));
    }

    [Fact]
    public async Task System_tab_lists_all_connected_devices_including_those_from_startup()
    {
        var hub = new ActivityHub(new FakeTimeProvider());
        var source = new FakeSource();
        var module = new BluetoothModule(() => source);
        await module.StartAsync(new ModuleContext(BluetoothModule.ModuleId, hub, new BrimSettings()), CancellationToken.None);

        source.Connect(new BluetoothGadget("1", "MX Master 3S", null), initial: true);
        source.Connect(new BluetoothGadget("2", "AirPods Pro", 80), initial: false);
        source.Update(new BluetoothGadget("1", "MX Master 3S", 55));

        var card = Assert.Single(hub.Views(NotchTab.System));
        Assert.Equal(TabColumn.Side, card.Column);
        var texts = Brim.Core.Widgets.WidgetTree.All(card.Content).OfType<TextWidget>().Select(t => t.Text).ToList();
        Assert.Equal(["Bluetooth", "AirPods Pro", "80 %", "MX Master 3S", "55 %"], texts);

        source.Disconnect("1");
        source.Disconnect("2");
        Assert.Empty(hub.Views(NotchTab.System));
    }
}
