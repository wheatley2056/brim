using Brim.Core;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Core.Tests;

public class FrameCounterTests
{
    [Fact]
    public void Steady_60_fps()
    {
        var counter = new FrameCounter();
        for (var i = 0; i <= 120; i++)
        {
            counter.Add(i * 1000.0 / 60);
        }

        var stats = counter.Snapshot(2000)!;

        Assert.Equal(60, stats.Fps, 0.5);
        Assert.Equal(16.67, stats.FrameTimeMs, 0.05);
        Assert.Equal(60, stats.OnePercentLow, 0.5);
    }

    [Fact]
    public void Stutters_show_in_one_percent_low_not_in_average()
    {
        var counter = new FrameCounter();
        var t = 0.0;
        for (var i = 0; i < 600; i++)
        {
            // Alle 100 Bilder ein Ruckler von 50 ms, sonst 10 ms.
            t += i % 100 == 99 ? 50 : 10;
            counter.Add(t);
        }

        var stats = counter.Snapshot(t)!;

        Assert.InRange(stats.Fps, 90, 100);
        Assert.Equal(20, stats.OnePercentLow, 0.5);
    }

    [Fact]
    public void No_frames_no_stats_and_history_is_bounded()
    {
        var counter = new FrameCounter();
        Assert.Null(counter.Snapshot(1000));

        for (var i = 0; i < 1000; i++)
        {
            counter.Add(i * 5);
        }
        var stats = counter.Snapshot(4995)!;
        Assert.Equal(FrameCounter.HistoryLength, stats.FrameTimes.Count);

        // Zehn Sekunden später ist alles verfallen.
        Assert.Null(counter.Snapshot(20_000));
    }

    [Fact]
    public void Out_of_order_timestamps_are_dropped()
    {
        var counter = new FrameCounter();
        counter.Add(100);
        counter.Add(50);
        counter.Add(200);

        Assert.All(counter.Snapshot(200)!.FrameTimes, ft => Assert.True(ft > 0));
    }
}

public class MiniModeTests
{
    private readonly ActivityHub _hub = new(new FakeTimeProvider());

    [Fact]
    public void Hidden_notch_keeps_the_overlay_pill()
    {
        _hub.Post(new Activity { ModuleId = "games", Id = "game", IsBackground = true, IsOverlay = true });

        var p = NotchStateMachine.Resolve(_hub.Snapshot(), new NotchInput(false, IsHidden: true, NotchTab.Now));

        Assert.Equal(NotchMode.Mini, p.Mode);
        Assert.Equal(NotchGeometry.Mini, NotchGeometry.For(p.Mode));
    }

    [Fact]
    public void Critical_still_wins_and_without_overlay_it_stays_hidden()
    {
        _hub.Post(new Activity { ModuleId = "games", Id = "game", IsBackground = true });
        Assert.Equal(NotchMode.Hidden, NotchStateMachine.Resolve(_hub.Snapshot(), new NotchInput(false, true, NotchTab.Now)).Mode);

        _hub.Post(new Activity { ModuleId = "games", Id = "game", IsBackground = true, IsOverlay = true });
        _hub.Post(new Activity { ModuleId = "battery", Id = "critical", Priority = ActivityPriority.Critical });
        Assert.Equal(NotchMode.Alert, NotchStateMachine.Resolve(_hub.Snapshot(), new NotchInput(false, true, NotchTab.Now)).Mode);
    }

    [Fact]
    public void Overlay_does_not_take_the_visible_compact_notch()
    {
        _hub.Post(new Activity { ModuleId = "games", Id = "game", IsBackground = true, IsOverlay = true });

        Assert.Equal(NotchMode.Idle, NotchStateMachine.Resolve(_hub.Snapshot(), new NotchInput(false, false, NotchTab.Now)).Mode);
    }
}
