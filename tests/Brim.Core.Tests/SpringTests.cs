using Brim.Core;

namespace Brim.Core.Tests;

public class SpringTests
{
    [Fact]
    public void Default_expand_spring_is_quick()
    {
        // 380 ms Response, Dämpfung 0,8: 620 → 180 px soll nach ~0,25 s zu 95 % da sein und unter 0,7 s ruhen.
        var spring = new Spring(0.38, 0.8);

        var (x, _) = spring.Evaluate(620, 180, 0, 0.25);
        Assert.InRange(Math.Abs(x - 180), 0, 0.06 * 440);
        Assert.InRange(spring.SettlingTime(620, 180, 0), 0.3, 0.7);
    }

    [Fact]
    public void Underdamped_spring_overshoots_slightly()
    {
        var spring = new Spring(0.38, 0.8);
        var max = Enumerable.Range(1, 200).Select(i => spring.Evaluate(0, 100, 0, i / 240.0).Value).Max();

        Assert.InRange(max, 100.5, 103);
    }

    [Fact]
    public void Critically_damped_spring_never_overshoots()
    {
        var spring = new Spring(0.32, 1);
        var max = Enumerable.Range(1, 400).Select(i => spring.Evaluate(0, 100, 0, i / 240.0).Value).Max();

        Assert.True(max <= 100.0001);
    }

    [Fact]
    public void Velocity_matches_numeric_derivative()
    {
        var spring = new Spring(0.38, 0.8);
        const double t = 0.1, h = 1e-5;

        var (_, v) = spring.Evaluate(0, 100, 250, t);
        var numeric = (spring.Evaluate(0, 100, 250, t + h).Value - spring.Evaluate(0, 100, 250, t - h).Value) / (2 * h);

        Assert.Equal(numeric, v, 2);
    }

    [Fact]
    public void Retarget_continues_from_current_value_and_velocity()
    {
        var channel = new SpringChannel(180);
        channel.Retarget(620, new Spring(0.38, 0.8), now: 0);
        var mid = channel.Sample(0.08);

        var (from, velocity) = channel.Retarget(180, new Spring(0.32, 0.9), now: 0.08);

        Assert.Equal(mid.Value, from, 6);
        Assert.Equal(mid.Velocity, velocity, 6);
        Assert.True(velocity > 0, "lief beim Umlenken noch nach außen");
    }

    [Fact]
    public void Finished_channel_rests_at_target()
    {
        var channel = new SpringChannel(0);
        channel.Retarget(10, new Spring(0.3, 1), 0);

        Assert.Equal((10d, 0d), channel.Sample(5));
    }
}
