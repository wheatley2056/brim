namespace Brim.Core;

/// <summary>
/// Gedämpfte Feder nach dem Modell von SwiftUI: <c>Response</c> ist die Periode der ungedämpften
/// Schwingung, <c>DampingFraction</c> die Dämpfung (1 = kritisch, kein Überschwingen).
/// Analytisch gelöst, damit Position und Geschwindigkeit zu jedem Zeitpunkt bekannt sind
/// und eine neue Bewegung nahtlos aus der laufenden heraus starten kann.
/// </summary>
public readonly record struct Spring(double Response, double DampingFraction)
{
    /// <summary>Position und Geschwindigkeit (Einheiten pro Sekunde) zum Zeitpunkt <paramref name="t"/> in Sekunden.</summary>
    public (double Value, double Velocity) Evaluate(double from, double to, double velocity, double t)
    {
        if (t <= 0)
        {
            return (from, velocity);
        }
        var omega = 2 * Math.PI / Math.Max(Response, 0.001);
        var zeta = Math.Clamp(DampingFraction, 0.05, 1.0);
        var d0 = from - to;

        if (zeta >= 0.999)
        {
            // Kritisch gedämpft
            var c = velocity + omega * d0;
            var e = Math.Exp(-omega * t);
            return (to + e * (d0 + c * t), e * (velocity - omega * c * t));
        }

        var a = zeta * omega;
        var w = omega * Math.Sqrt(1 - zeta * zeta);
        var b = (velocity + a * d0) / w;
        var decay = Math.Exp(-a * t);
        var cos = Math.Cos(w * t);
        var sin = Math.Sin(w * t);
        var value = to + decay * (d0 * cos + b * sin);
        var v = decay * (velocity * cos + (-a * b - w * d0) * sin);
        return (value, v);
    }

    /// <summary>Zeit, bis die Feder auf <paramref name="tolerance"/> Einheiten zur Ruhe gekommen ist (höchstens 3 s).</summary>
    public double SettlingTime(double from, double to, double velocity, double tolerance = 0.25)
    {
        const double step = 1.0 / 240;
        var calm = 0.0;
        for (var t = step; t < 3; t += step)
        {
            var (x, v) = Evaluate(from, to, velocity, t);
            if (Math.Abs(x - to) < tolerance && Math.Abs(v) < tolerance * 20)
            {
                calm += step;
                // Ein kurzes Stück ruhig bleiben, damit ein Nulldurchgang beim Schwingen nicht zählt.
                if (calm >= 0.03)
                {
                    return t;
                }
            }
            else
            {
                calm = 0;
            }
        }
        return 3;
    }
}

/// <summary>Ein animierter Wert, der seine Feder kennt und deshalb jederzeit umgelenkt werden kann.</summary>
public sealed class SpringChannel
{
    private double _from;
    private double _velocity;
    private double _startedAt;
    private Spring _spring = new(0.4, 1);

    public SpringChannel(double initial)
    {
        _from = initial;
        Target = initial;
    }

    public double Target { get; private set; }

    /// <summary>Dauer der zuletzt gestarteten Bewegung in Sekunden.</summary>
    public double Duration { get; private set; }

    /// <summary>Wert und Geschwindigkeit zum Zeitpunkt <paramref name="now"/> (Sekunden, monoton).</summary>
    public (double Value, double Velocity) Sample(double now)
    {
        var t = now - _startedAt;
        if (t >= Duration)
        {
            return (Target, 0);
        }
        return _spring.Evaluate(_from, Target, _velocity, t);
    }

    /// <summary>Startet eine neue Bewegung aus dem aktuellen Zustand heraus.</summary>
    public (double From, double Velocity) Retarget(double to, Spring spring, double now)
    {
        var (x, v) = Sample(now);
        _from = x;
        _velocity = v;
        _startedAt = now;
        _spring = spring;
        Target = to;
        Duration = spring.SettlingTime(x, to, v);
        return (x, v);
    }

    /// <summary>Springt ohne Animation auf einen Wert.</summary>
    public void Set(double value, double now)
    {
        _from = Target = value;
        _velocity = 0;
        _startedAt = now;
        Duration = 0;
    }
}
