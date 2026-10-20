namespace Brim.Core;

/// <summary>Aufteilung der ruhenden Notch: wo die Uhr steht und welche Statuswerte links bzw. rechts davon.</summary>
public sealed record IdleArrangement(double Width, double ClockX, IReadOnlyList<int> Left, IReadOnlyList<int> Right);

/// <summary>
/// Die ruhende Notch wächst mit den eingeschalteten Werten. Mittig steht die Uhr in der Mitte und die Werte
/// verteilen sich abwechselnd rechts und links (symmetrisch, damit die Uhr mittig bleibt); links oder rechts
/// steht die Uhr am Rand und alle Werte daneben.
/// </summary>
public static class IdleLayout
{
    public const double Padding = 14, Spacing = 10, MaxWidth = 560;

    /// <param name="items">Breiten der Statuswerte in DIP, in der gewünschten Reihenfolge.</param>
    public static IdleArrangement Arrange(ClockAlignment alignment, double clockWidth, IReadOnlyList<double> items)
    {
        var minimum = NotchGeometry.Idle.Width;
        if (items.Count == 0)
        {
            var width = Math.Max(minimum, clockWidth + 2 * Padding);
            return new IdleArrangement(width, Clock(alignment, width, clockWidth), [], []);
        }
        if (alignment == ClockAlignment.Center)
        {
            var left = new List<int>();
            var right = new List<int>();
            for (var i = 0; i < items.Count; i++)
            {
                (i % 2 == 0 ? right : left).Add(i);
            }
            var side = Math.Max(Sum(left, items), Sum(right, items));
            var width = Math.Clamp(2 * Padding + clockWidth + 2 * (side + Spacing), minimum, MaxWidth);
            return new IdleArrangement(width, (width - clockWidth) / 2, left, right);
        }
        var all = Enumerable.Range(0, items.Count).ToList();
        var total = Math.Clamp(2 * Padding + clockWidth + Spacing + Sum(all, items), minimum, MaxWidth);
        return alignment == ClockAlignment.Left
            ? new IdleArrangement(total, Padding, [], all)
            : new IdleArrangement(total, total - Padding - clockWidth, all, []);
    }

    private static double Clock(ClockAlignment alignment, double width, double clockWidth) => alignment switch
    {
        ClockAlignment.Left => Padding,
        ClockAlignment.Right => width - Padding - clockWidth,
        _ => (width - clockWidth) / 2,
    };

    private static double Sum(IReadOnlyList<int> indices, IReadOnlyList<double> items) =>
        indices.Count == 0 ? 0 : indices.Sum(i => items[i]) + Spacing * (indices.Count - 1);
}
