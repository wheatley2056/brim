using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Modules.Tests;

public class CalendarModuleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 13, 40, 0, TimeSpan.Zero));
    private readonly ActivityHub _hub;
    private List<CalendarEntry> _entries = [];

    public CalendarModuleTests()
    {
        _hub = new ActivityHub(_time);
    }

    private async Task<CalendarModule> Start()
    {
        var module = new CalendarModule(_ => Task.FromResult<IReadOnlyList<CalendarEntry>>(_entries));
        await module.StartAsync(new ModuleContext(CalendarModule.ModuleId, _hub, new BrimSettings()), CancellationToken.None);
        return module;
    }

    private ActiveActivity? Find(string id) => _hub.Snapshot().FirstOrDefault(a => a.Key == "calendar/" + id);

    private CalendarEntry Meeting(int startsInMinutes, string subject = "Teamrunde", Uri? link = null) =>
        new(subject, _time.GetUtcNow().AddMinutes(startsInMinutes), TimeSpan.FromMinutes(30), "Raum 2", link);

    [Fact]
    public async Task Shows_fifteen_minutes_before_with_countdown()
    {
        _entries = [Meeting(40)];
        var module = await Start();
        Assert.Null(Find("next"));
        Assert.StartsWith("Nächster Termin:", module.NextSummary);

        _time.Advance(TimeSpan.FromMinutes(26));
        await Task.Delay(50);
        var next = Find("next")!;
        Assert.Equal("in 14 min", ((TextWidget)next.Activity.Trailing!).Text);
    }

    [Fact]
    public async Task At_start_a_hud_once_and_join_for_online_meetings()
    {
        _entries = [Meeting(1, link: new Uri("https://teams.example/join"))];
        await Start();
        Assert.Null(Find("hud"));

        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        Assert.Equal("Jetzt: Teamrunde", Find("hud")?.Activity.Announcement);
        Assert.Contains(WidgetTree.All(Find("next")!.Activity.Expanded!).OfType<ButtonWidget>(), b => b.ActionId == "join");

        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(Find("hud"));
    }

    [Fact]
    public async Task Gone_after_the_linger_time()
    {
        _entries = [Meeting(0)];
        await Start();
        Assert.NotNull(Find("next"));

        _time.Advance(CalendarModule.Linger + TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        Assert.Null(Find("next"));
    }

    [Fact]
    public void Countdown_text()
    {
        var now = _time.GetUtcNow();
        Assert.Equal("in 1 min", CalendarModule.Countdown(new CalendarEntry("x", now.AddSeconds(20), TimeSpan.FromMinutes(5), null, null), now));
        Assert.Equal("jetzt", CalendarModule.Countdown(new CalendarEntry("x", now.AddMinutes(-2), TimeSpan.FromMinutes(5), null, null), now));
    }
}

public class NotificationModuleTests
{
    [Fact]
    public async Task Preview_uses_first_line_as_title_and_respects_muted_apps()
    {
        var settings = new BrimSettings();
        settings.Notifications.MutedApps.Add("SwyxIt!");
        var hub = new ActivityHub(new FakeTimeProvider());
        var module = new NotificationModule();
        await module.StartAsync(new ModuleContext(NotificationModule.ModuleId, hub, settings), CancellationToken.None);

        var preview = module.Preview("Outlook", ["Anna Berg", "Kurze Frage", "zum Angebot"]);
        Assert.Equal(new NotificationPreview("Outlook", "Anna Berg", "Kurze Frage zum Angebot"), preview);
        Assert.Null(module.Preview("swyxit!", ["Anruf"]));
        Assert.Null(module.Preview("Brim", ["Update"]));
        Assert.Null(module.Preview("Outlook", []));

        module.Show(preview!);
        Assert.Equal(ActivityPriority.Transient, Assert.Single(hub.Snapshot()).Priority);
    }
}
