using System.Globalization;
using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules;
using Brim.Modules.Sensors;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Modules.Tests;

public class ResourceMathTests
{
    [Fact]
    public void Gpu_is_the_busiest_engine_summed_over_processes()
    {
        var engines = new[]
        {
            ("pid_100_luid_0x00000000_0x0000C3B1_phys_0_eng_0_engtype_3D", 20.0),
            ("pid_200_luid_0x00000000_0x0000C3B1_phys_0_eng_0_engtype_3D", 15.0),
            ("pid_100_luid_0x00000000_0x0000C3B1_phys_0_eng_3_engtype_VideoDecode", 30.0),
            ("pid_300_luid_0x00000000_0x0000D111_phys_0_eng_0_engtype_3D", 10.0),
        };

        Assert.Equal(35, ResourceMath.GpuUtilization(engines));
    }

    [Fact]
    public void Gpu_is_unknown_without_engines_and_capped_at_100()
    {
        Assert.Null(ResourceMath.GpuUtilization([]));
        Assert.Equal(100, ResourceMath.GpuUtilization([("pid_1_luid_0x1_phys_0_eng_0_engtype_3D", 80.0), ("pid_2_luid_0x1_phys_0_eng_0_engtype_3D", 70.0)]));
    }

    [Fact]
    public void Top_processes_merge_instances_and_scale_to_the_whole_cpu()
    {
        var processes = new[]
        {
            ("_Total", 800.0),
            ("Idle", 600.0),
            ("chrome", 40.0),
            ("chrome#1", 24.0),
            ("Discord", 24.0),
            ("Brim", 2.4),
            ("notepad", 0.0),
        };

        var top = ResourceMath.TopProcesses(processes, processorCount: 8, count: 3);

        Assert.Equal([new ProcessUsage("chrome", 8), new ProcessUsage("Discord", 3), new ProcessUsage("Brim", 0.3)], top);
    }

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(950, "950 B/s")]
    [InlineData(1536, "1,50 KB/s")]
    [InlineData(12.4 * 1024 * 1024, "12,4 MB/s")]
    [InlineData(250.0 * 1024 * 1024, "250 MB/s")]
    [InlineData(-5, "0 B/s")]
    public void Rates_have_at_most_three_digits(double bytes, string expected)
    {
        Assert.Equal(expected, ResourcesModule.FormatRate(bytes, CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void Percent_shows_one_decimal_below_one_for_processes()
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("23 %", ResourcesModule.Percent(23.4, de));
        Assert.Equal("0,3 %", ResourcesModule.Percent(0.3, de, small: true));
        Assert.Equal("–", ResourcesModule.Percent(null, de));
        Assert.Equal("11,2", ResourcesModule.FormatGigabytes((ulong)(11.2 * 1024 * 1024 * 1024), de));
    }
}

public class ResourcesModuleTests
{
    private sealed class FakeSampler : IResourceSampler
    {
        public int Calls;
        public bool Disposed;

        public ResourceSample Sample()
        {
            Calls++;
            return new ResourceSample(Calls * 10, 4UL << 30, 16UL << 30, null, 1024, 512, [new ProcessUsage("Brim", 1)]);
        }

        public void Dispose() => Disposed = true;
    }

    private readonly FakeTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly FakeSampler _sampler = new();
    private readonly ResourcesModule _module;

    public ResourcesModuleTests()
    {
        _hub = new ActivityHub(_time);
        _module = new ResourcesModule(() => _sampler);
    }

    private Task Start() => _module.StartAsync(new ModuleContext(ResourcesModule.ModuleId, _hub, new BrimSettings()), CancellationToken.None);

    [Fact]
    public async Task Measures_only_while_the_system_tab_is_visible()
    {
        await Start();
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, _sampler.Calls);

        _module.OnTabVisibilityChanged(NotchTab.System, true);
        _time.Advance(TimeSpan.Zero);
        _time.Advance(TimeSpan.FromSeconds(2));
        var whileOpen = _sampler.Calls;
        Assert.InRange(whileOpen, 2, 3);

        _module.OnTabVisibilityChanged(NotchTab.System, false);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(whileOpen, _sampler.Calls);
        Assert.False(_module.IsSampling);

        await _module.DisposeAsync();
        Assert.True(_sampler.Disposed);
    }

    [Fact]
    public async Task Publishes_cards_for_both_columns()
    {
        await Start();
        _module.OnTabVisibilityChanged(NotchTab.System, true);
        _time.Advance(TimeSpan.FromSeconds(1));

        var views = _hub.Views(NotchTab.System);
        Assert.Equal(["resources/meters", "resources/processes"], views.Select(v => v.Key));
        Assert.Equal(TabColumn.Side, views[1].Column);
        var texts = Texts(views[0].Content).ToList();
        Assert.Contains("4,0 / 16,0", texts.Select(t => t.Replace('.', ',')));
        Assert.Contains(Texts(views[1].Content), t => t == "Brim");
    }

    [Fact]
    public async Task A_click_on_a_card_opens_its_detail_and_back_closes_it()
    {
        await Start();
        _module.OnTabVisibilityChanged(NotchTab.System, true);
        _time.Advance(TimeSpan.FromSeconds(1));
        var meters = _hub.Views(NotchTab.System)[0].Content;
        Assert.Contains("detail:cpu", WidgetTree.All(meters).OfType<StackWidget>().Select(w => w.ActionId));
        Assert.Contains("detail:disk", WidgetTree.All(meters).OfType<StackWidget>().Select(w => w.ActionId));

        _module.OnAction(new ActionInvocation(ResourcesModule.ModuleId, "detail:cpu"));
        var detail = Assert.Single(_hub.Views(NotchTab.System), v => v.IsDetail);
        Assert.Contains("Prozessor", Texts(detail.Content));
        Assert.Contains("taskmgr", WidgetTree.All(detail.Content).OfType<ButtonWidget>().Select(b => b.ActionId));

        _module.OnAction(new ActionInvocation(ResourcesModule.ModuleId, "detail:close"));
        Assert.DoesNotContain(_hub.Views(NotchTab.System), v => v.IsDetail);

        // Zuklappen schließt die Detailansicht ebenfalls.
        _module.OnAction(new ActionInvocation(ResourcesModule.ModuleId, "detail:memory"));
        _module.OnExpandedChanged(false);
        Assert.DoesNotContain(_hub.Views(NotchTab.System), v => v.IsDetail);
    }

    [Fact]
    public void Every_detail_view_is_valid()
    {
        var sample = new ResourceSample(37, 6UL << 30, 16UL << 30, 12, 2048, 512, [], 4);
        var detail = new ResourceDetail(250, 3200, 120_000, 3400, 9UL << 30, 24UL << 30, 2UL << 30, 1UL << 30, 1_000_000, 50_000,
            [new ProcessUsage("Brim", 2.5)], [new MemoryUsage("Edge", 800UL << 20)]);
        var history = new ResourcesModule.DetailHistory([1, 2], [30, 31], [5], [1], [100, 200], [50]);
        foreach (var kind in ResourcesModule.DetailTitles.Keys)
        {
            foreach (var (s, d) in new[] { (sample, detail), ((ResourceSample?)null, ResourceDetail.Empty) })
            {
                var view = ResourcesModule.DetailView(kind, s, d, history, System.Globalization.CultureInfo.GetCultureInfo("de-DE"), TimeSpan.FromHours(30));
                Assert.Null(WidgetValidator.Validate(view));
            }
        }
        Assert.Equal("1 d 6 h", ResourcesModule.Uptime(TimeSpan.FromHours(30)));
    }

    [Fact]
    public async Task Other_tabs_do_not_start_measuring()
    {
        await Start();
        _module.OnTabVisibilityChanged(NotchTab.Shelf, true);
        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(0, _sampler.Calls);
    }

    internal static IEnumerable<string> Texts(Widget widget) => widget switch
    {
        TextWidget t => [t.Text],
        StackWidget s => s.Children.SelectMany(Texts),
        ListWidget l => l.Items.SelectMany(Texts),
        _ => [],
    };
}

public class BatteryModuleTests
{
    private sealed class FakeBattery : IBatterySource
    {
        public BatteryState State { get; set; } = BatteryState.None;
        public event Action? Changed;

        public void Set(BatteryState state)
        {
            State = state;
            Changed?.Invoke();
        }

        public BatteryState Read() => State;

        public void Dispose()
        {
        }
    }

    private static BatteryState OnBattery(int percent, TimeSpan? left = null) => new(true, percent, false, false, left, null, false);
    private static BatteryState Charging(int percent) => new(true, percent, true, true, null, TimeSpan.FromMinutes(65), false);

    [Fact]
    public void Colors_charging_blue_low_red_saver_orange_otherwise_green()
    {
        var settings = new BatterySettings();
        Assert.Equal(BrimColors.Charging, BatteryModule.ColorFor(Charging(10), settings));
        Assert.Equal(BrimColors.Danger, BatteryModule.ColorFor(OnBattery(settings.LowPercent), settings));
        Assert.Equal(BrimColors.Danger, BatteryModule.ColorFor(OnBattery(settings.CriticalPercent), settings));
        Assert.Equal(BrimColors.Warning, BatteryModule.ColorFor(OnBattery(60) with { EnergySaver = true }, settings));
        Assert.Equal(BrimColors.Charge, BatteryModule.ColorFor(OnBattery(60), settings));
        // Voll und angesteckt, lädt nicht mehr: grün.
        Assert.Equal(BrimColors.Charge, BatteryModule.ColorFor(new BatteryState(true, 100, false, true, null, null, false), settings));
    }

    private readonly FakeTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly FakeBattery _battery = new();
    private readonly BatteryModule _module;

    public BatteryModuleTests()
    {
        _hub = new ActivityHub(_time);
        _module = new BatteryModule(() => _battery);
    }

    private async Task Start(BatteryState state)
    {
        _battery.State = state;
        await _module.StartAsync(new ModuleContext(BatteryModule.ModuleId, _hub, new BrimSettings()), CancellationToken.None);
    }

    private ActiveActivity? Find(string id) => _hub.Snapshot().FirstOrDefault(a => a.Key == "battery/" + id);

    [Fact]
    public async Task Desktop_pcs_show_nothing()
    {
        await Start(BatteryState.None);

        Assert.Empty(_hub.Snapshot());
        Assert.Empty(_hub.Views(NotchTab.System));
    }

    [Fact]
    public async Task Plugging_in_shows_a_hud_and_the_compact_charge()
    {
        await Start(OnBattery(64));
        Assert.Null(Find("hud"));
        Assert.Null(Find("status"));
        Assert.Single(_hub.Views(NotchTab.System));

        _battery.Set(Charging(64));

        Assert.Equal(ActivityPriority.Transient, Find("hud")?.Priority);
        Assert.Equal(ActivityPriority.Ongoing, Find("status")?.Priority);
        Assert.Equal("Ladekabel angeschlossen, 64 %", Find("hud")?.Activity.Announcement);
    }

    [Fact]
    public async Task Low_battery_warns_once_and_critical_alerts_once()
    {
        await Start(OnBattery(25));

        _battery.Set(OnBattery(20));
        Assert.Equal("Akku schwach, 20 %", Find("hud")?.Activity.Announcement);
        Assert.NotNull(Find("status"));

        _time.Advance(TimeSpan.FromSeconds(5));
        _battery.Set(OnBattery(19));
        Assert.Null(Find("hud"));

        _battery.Set(OnBattery(10));
        Assert.Equal(ActivityPriority.Critical, Find("critical")?.Priority);

        _time.Advance(TimeSpan.FromSeconds(10));
        _battery.Set(OnBattery(9));
        Assert.Null(Find("critical"));
    }

    [Fact]
    public async Task Warnings_rearm_after_charging()
    {
        await Start(OnBattery(15));
        _time.Advance(TimeSpan.FromSeconds(5));
        _battery.Set(Charging(30));
        _time.Advance(TimeSpan.FromSeconds(5));

        _battery.Set(OnBattery(30));
        _battery.Set(OnBattery(20));

        Assert.Equal("Akku schwach, 20 %", Find("hud")?.Activity.Announcement);
    }

    [Theory]
    [InlineData(true, true, 65, null, false, "voll in 1 h 5 min")]
    [InlineData(true, true, null, null, false, "Lädt")]
    [InlineData(false, true, null, null, false, "Angeschlossen")]
    [InlineData(false, false, null, 190.0, true, "noch 3 h 10 min · Energiesparen")]
    [InlineData(false, false, null, 0.2, false, "noch 1 min")]
    [InlineData(false, false, null, null, false, "Akkubetrieb")]
    public void Status_text(bool charging, bool plugged, int? fullMinutes, double? leftMinutes, bool saver, string expected)
    {
        var state = new BatteryState(true, 50, charging, plugged,
            leftMinutes is { } l ? TimeSpan.FromMinutes(l) : null,
            fullMinutes is { } f ? TimeSpan.FromMinutes(f) : null,
            saver);

        Assert.Equal(expected, BatteryModule.StatusText(state));
    }
}

public class PrivacyModuleTests
{
    private sealed class FakeConsent : ICapabilitySource
    {
        public IReadOnlyList<CapabilityUse> Uses { get; set; } = [];
        public event Action? Changed;

        public void Set(params CapabilityUse[] uses)
        {
            Uses = uses;
            Changed?.Invoke();
        }

        public IReadOnlyList<CapabilityUse> Read() => Uses;

        public void Dispose()
        {
        }
    }

    [Theory]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", true, "Windows Camera")]
    [InlineData("MSTeams_8wekyb3d8bbwe", true, "MSTeams")]
    [InlineData("C:#Program Files#Mozilla Firefox#firefox.exe", false, "firefox")]
    [InlineData("C:#Users#Ana#AppData#Local#Discord#app-1.0.9#Discord.exe", false, "Discord")]
    public void App_names_from_consent_store_keys(string key, bool packaged, string expected)
    {
        Assert.Equal(expected, ConsentStore.AppName(key, packaged));
    }

    [Fact]
    public void In_use_while_started_and_not_stopped()
    {
        Assert.True(ConsentStore.InUse(133_000_000_000_000_000, 0));
        Assert.False(ConsentStore.InUse(133_000_000_000_000_000, 133_000_000_000_000_100));
        Assert.False(ConsentStore.InUse(0, 0));
    }

    [Fact]
    public async Task Shows_the_indicator_after_the_burst_settles()
    {
        var time = new FakeTimeProvider();
        var hub = new ActivityHub(time);
        var consent = new FakeConsent();
        var module = new PrivacyModule(() => consent);
        await module.StartAsync(new ModuleContext(PrivacyModule.ModuleId, hub, new BrimSettings()), CancellationToken.None);
        Assert.Empty(hub.Snapshot());

        consent.Set(new CapabilityUse(Capability.Microphone, "Discord"), new CapabilityUse(Capability.Camera, "Teams"));
        Assert.Empty(hub.Snapshot());
        time.Advance(PrivacyModule.Debounce);

        var indicator = Assert.Single(hub.Snapshot()).Activity;
        Assert.Equal("Kamera: Teams. Mikrofon: Discord.", indicator.Announcement);
        Assert.Equal(BrimColors.Camera, ((IconWidget)indicator.Bead!).Color);

        consent.Set();
        time.Advance(PrivacyModule.Debounce);
        Assert.Empty(hub.Snapshot());
    }
}
