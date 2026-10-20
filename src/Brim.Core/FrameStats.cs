namespace Brim.Core;

/// <summary>Bildrate eines Spiels wie bei PresentMon (Plan 4.8), aus den Zeitpunkten seiner Presents.</summary>
/// <param name="Fps">Bilder pro Sekunde, Mittel über die letzte Sekunde.</param>
/// <param name="OnePercentLow">Bildrate, die 99 % der Bilder der letzten Sekunden mindestens erreichen (macht Ruckler sichtbar).</param>
/// <param name="FrameTimeMs">Mittlere Frametime der letzten Sekunde in ms.</param>
/// <param name="FrameTimes">Frametimes in ms, älteste zuerst (für den Verlauf).</param>
public sealed record FrameStats(double Fps, double OnePercentLow, double FrameTimeMs, IReadOnlyList<double> FrameTimes);

/// <summary>Sammelt Present-Zeitpunkte (ms) und rechnet daraus <see cref="FrameStats"/>. Nicht threadsicher.</summary>
public sealed class FrameCounter
{
    /// <summary>Zeitraum für 1 % Low.</summary>
    public const double LowWindowMs = 10_000;

    /// <summary>So viele Frametimes gehen in den Verlauf.</summary>
    public const int HistoryLength = 120;

    private readonly Queue<double> _presents = new();

    public void Add(double timestampMs)
    {
        // Ereignisse kommen gebündelt, aber nie rückwärts; Ausreißer verwerfen statt negativer Frametimes.
        if (_presents.Count > 0 && timestampMs < _presents.Last())
        {
            return;
        }
        _presents.Enqueue(timestampMs);
    }

    public void Clear() => _presents.Clear();

    /// <returns><c>null</c>, wenn in der letzten Sekunde weniger als zwei Bilder kamen (kein Spiel, Pause, Ladebildschirm).</returns>
    public FrameStats? Snapshot(double nowMs)
    {
        while (_presents.Count > 0 && _presents.Peek() < nowMs - LowWindowMs)
        {
            _presents.Dequeue();
        }
        var all = _presents.ToArray();
        var lastSecond = all.Where(t => t >= nowMs - 1000).ToArray();
        if (lastSecond.Length < 2)
        {
            return null;
        }

        var frameTimes = new double[all.Length - 1];
        for (var i = 1; i < all.Length; i++)
        {
            frameTimes[i - 1] = all[i] - all[i - 1];
        }
        var recent = frameTimes[^(lastSecond.Length - 1)..];
        var average = recent.Average();

        // 1 % Low: die Frametime, die nur 1 % der Bilder überschreiten, als Bildrate.
        var sorted = frameTimes.Order().ToArray();
        var p99 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.99) - 1)];

        return new FrameStats(
            Fps: average > 0 ? 1000 / average : 0,
            OnePercentLow: p99 > 0 ? 1000 / p99 : 0,
            FrameTimeMs: average,
            FrameTimes: frameTimes[^Math.Min(HistoryLength, frameTimes.Length)..]);
    }
}
