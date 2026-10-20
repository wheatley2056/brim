using Brim.SwyxIt;

namespace Brim.SwyxIt.Tests;

public class CallTrackerTests
{
    private static LineSnapshot Line(int index, LineState state, string name = "Anna Berg", string number = "+49 30 1234", bool outgoing = false) =>
        new(index, state, name, number, "Vertrieb", outgoing, null);

    [Fact]
    public void Ringing_and_knocking_are_incoming_but_not_own_calls()
    {
        var tracker = new CallTracker();

        var changes = tracker.Update([
            Line(0, LineState.Active),
            Line(1, LineState.Knocking),
            Line(2, LineState.Ringing, outgoing: true),
            Line(3, LineState.Inactive),
        ]);

        Assert.Equal([1], changes.Incoming.Select(c => c.Line));
        Assert.Equal([0], changes.Calls.Select(c => c.Line));
    }

    [Fact]
    public void Lines_that_go_idle_are_reported_once()
    {
        var tracker = new CallTracker();
        tracker.Update([Line(0, LineState.Ringing)]);

        var answered = tracker.Update([Line(0, LineState.Active)]);
        Assert.Empty(answered.Gone);
        Assert.Single(answered.Calls);

        var ended = tracker.Update([Line(0, LineState.Inactive)]);
        Assert.Equal([0], ended.Gone);
        Assert.Empty(tracker.Update([Line(0, LineState.Inactive)]).Gone);
    }

    [Theory]
    [InlineData("Anna Berg", "+49 30 1234", "Anna Berg")]
    [InlineData(" ", "+49 30 1234", "+49 30 1234")]
    [InlineData("", "", "Unbekannt")]
    public void Caller_falls_back_to_number(string name, string number, string expected)
    {
        Assert.Equal(expected, Line(0, LineState.Ringing, name, number).Caller);
    }

    [Fact]
    public void Duration_format()
    {
        Assert.Equal("3:07", CallTracker.Duration(TimeSpan.FromSeconds(187)));
        Assert.Equal("1:02:03", CallTracker.Duration(TimeSpan.FromSeconds(3723)));
        Assert.Equal("0:00", CallTracker.Duration(TimeSpan.FromSeconds(-4)));
    }
}
