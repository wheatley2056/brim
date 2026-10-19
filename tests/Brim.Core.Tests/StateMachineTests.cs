using Brim.Core;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Core.Tests;

public class StateMachineTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 14, 32, 0, TimeSpan.Zero));
    private readonly ActivityHub _hub;

    public StateMachineTests()
    {
        _hub = new ActivityHub(_time);
    }

    private static Activity Make(string id, ActivityPriority priority, string module = "test", int rank = 0) =>
        new() { ModuleId = module, Id = id, Priority = priority, Rank = rank };

    private NotchPresentation Resolve(bool expanded = false, bool hidden = false) =>
        NotchStateMachine.Resolve(_hub.Snapshot(), new NotchInput(expanded, hidden, NotchTab.Now));

    [Fact]
    public void Without_activities_shows_idle()
    {
        _hub.Post(Make("clock", ActivityPriority.Idle));

        var p = Resolve();

        Assert.Equal(NotchMode.Idle, p.Mode);
        Assert.Equal("test/clock", p.Primary?.Key);
    }

    [Fact]
    public void Ongoing_beats_idle_and_further_ones_become_beads()
    {
        _hub.Post(Make("clock", ActivityPriority.Idle));
        _hub.Post(Make("media", ActivityPriority.Ongoing, rank: 10));
        _hub.Post(Make("timer", ActivityPriority.Ongoing));

        var p = Resolve();

        Assert.Equal(NotchMode.Compact, p.Mode);
        Assert.Equal("test/media", p.Primary?.Key);
        Assert.Equal(["test/timer"], p.Beads.Select(b => b.Key));
    }

    [Fact]
    public void Beads_are_capped()
    {
        for (var i = 0; i < 5; i++)
        {
            _hub.Post(Make("a" + i, ActivityPriority.Ongoing));
        }

        Assert.Equal(NotchStateMachine.MaxBeads, Resolve().Beads.Count);
    }

    [Fact]
    public void Hud_shows_for_its_duration_then_falls_back()
    {
        _hub.Post(Make("media", ActivityPriority.Ongoing));
        _hub.Post(Make("volume", ActivityPriority.Transient));

        Assert.Equal(NotchMode.Hud, Resolve().Mode);

        _time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(NotchMode.Hud, Resolve().Mode);

        _time.Advance(TimeSpan.FromSeconds(0.2));
        var p = Resolve();
        Assert.Equal(NotchMode.Compact, p.Mode);
        Assert.Equal("test/media", p.Primary?.Key);
    }

    [Fact]
    public void Updating_a_hud_extends_it()
    {
        _hub.Post(Make("volume", ActivityPriority.Transient));
        _time.Advance(TimeSpan.FromSeconds(1.2));
        _hub.Post(Make("volume", ActivityPriority.Transient));
        _time.Advance(TimeSpan.FromSeconds(1.2));

        Assert.Equal(NotchMode.Hud, Resolve().Mode);
    }

    [Fact]
    public void Expanded_wins_over_hud_and_shows_top_ongoing()
    {
        _hub.Post(Make("media", ActivityPriority.Ongoing));
        _hub.Post(Make("volume", ActivityPriority.Transient));

        var p = Resolve(expanded: true);

        Assert.Equal(NotchMode.Expanded, p.Mode);
        Assert.Equal("test/media", p.Primary?.Key);
    }

    [Fact]
    public void Critical_interrupts_expanded_and_returns_afterwards()
    {
        _hub.Post(Make("media", ActivityPriority.Ongoing));
        _hub.Post(Make("ask", ActivityPriority.Critical));

        Assert.Equal(NotchMode.Alert, Resolve(expanded: true).Mode);

        _hub.End("test", "ask");
        Assert.Equal(NotchMode.Expanded, Resolve(expanded: true).Mode);
    }

    [Fact]
    public void Hidden_stays_hidden_except_for_critical()
    {
        _hub.Post(Make("media", ActivityPriority.Ongoing));
        Assert.Equal(NotchMode.Hidden, Resolve(hidden: true).Mode);

        _hub.Post(Make("battery", ActivityPriority.Critical));
        Assert.Equal(NotchMode.Alert, Resolve(hidden: true).Mode);

        _time.Advance(ActivityHub.DefaultCriticalDuration);
        Assert.Equal(NotchMode.Hidden, Resolve(hidden: true).Mode);
    }

    [Fact]
    public void Background_activity_only_appears_when_expanded()
    {
        _hub.Post(Make("clock", ActivityPriority.Idle));
        _hub.Post(Make("media", ActivityPriority.Ongoing) with { IsBackground = true });

        Assert.Equal(NotchMode.Idle, Resolve().Mode);
        Assert.Equal("test/media", Resolve(expanded: true).Primary?.Key);
    }

    [Fact]
    public void Dragging_over_opens_the_drop_zone_unless_already_expanded()
    {
        _hub.Post(Make("media", ActivityPriority.Ongoing));

        var drop = NotchStateMachine.Resolve(_hub.Snapshot(), new NotchInput(false, false, NotchTab.Now, IsDropTarget: true));
        Assert.Equal(NotchMode.Drop, drop.Mode);
        Assert.Equal(NotchGeometry.ShelfDrop, NotchGeometry.For(drop.Mode));

        var open = NotchStateMachine.Resolve(_hub.Snapshot(), new NotchInput(true, false, NotchTab.Shelf, IsDropTarget: true));
        Assert.Equal(NotchMode.Expanded, open.Mode);
    }

    [Fact]
    public void Ending_an_activity_removes_it()
    {
        _hub.Post(Make("media", ActivityPriority.Ongoing));
        _hub.End("test", "media");

        Assert.Equal(NotchMode.Idle, Resolve().Mode);
    }

    [Fact]
    public void Next_expiry_points_at_the_hud()
    {
        _hub.Post(Make("media", ActivityPriority.Ongoing));
        Assert.Null(_hub.NextExpiry());

        _hub.Post(Make("volume", ActivityPriority.Transient));
        Assert.Equal(_time.GetUtcNow() + ActivityHub.DefaultTransientDuration, _hub.NextExpiry());
    }

    [Fact]
    public void Failing_action_handler_does_not_throw()
    {
        _hub.RegisterActionHandler("test", _ => throw new InvalidOperationException("kaputt"));

        _hub.Invoke(new ActionInvocation("test", "play"));
    }
}
