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

public class QuickMenuTests
{
    [Fact]
    public void Keypad_collects_digits_with_limits()
    {
        var digits = "";
        foreach (var key in new[] { "+", "4", "9", "x", "+", "3", "0" })
        {
            digits = Keypad.Append(digits, key);
        }
        Assert.Equal("+4930", digits);
        Assert.Equal("+493", Keypad.Backspace(digits));
        Assert.Equal("", Keypad.Backspace(""));
        Assert.Equal(32, Enumerable.Range(0, 40).Aggregate("", (d, _) => Keypad.Append(d, "1")).Length);
    }

    [Theory]
    // Zustände als Zahl, weil LineState und TransferStep intern sind (wie CLMgrLineStates: 4 wählt, 5 klingelt, 7 besetzt, 8 aktiv).
    [InlineData(4, 1, "Wait")]
    [InlineData(5, 1, "Complete")]
    [InlineData(8, 1, "Complete")]
    [InlineData(7, 1, "Cancel")]
    [InlineData(0, 1, "Wait")]
    [InlineData(4, 25, "Cancel")]
    public void Transfer_completes_when_the_target_rings(int target, int secondsLater, string expected)
    {
        var started = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        var transfer = new PendingTransfer(0, 1, "220", started);

        Assert.Equal(expected, transfer.Next((LineState)target, started.AddSeconds(secondsLater)).ToString());
    }

    [Fact]
    public void Wave_history_shifts_left_and_stays_in_range()
    {
        var history = new double[4];

        history = CallTracker.Push(history, 0.25);
        history = CallTracker.Push(history, 4);

        Assert.Equal(4, history.Length);
        Assert.Equal(0, history[1]);
        Assert.Equal(0.575, history[2], 3);
        Assert.Equal(1, history[3]);
    }
}
