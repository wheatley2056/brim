namespace Brim.Core;

/// <summary>Seite eines Statuswerts neben der Uhr.</summary>
public enum IdleSide { Left, Right }

/// <summary>Ein Statuswert für die ruhende Notch: Breite in DIP, Seite (bei mittiger Uhr) und Vorrang beim Kürzen.</summary>
/// <param name="Priority">Höher bleibt länger: Wird die Notch zu breit, fällt der Wert mit dem kleinsten Vorrang zuerst weg.</param>
public readonly record struct IdleItem(double Width, IdleSide Side, int Priority);

/// <summary>Aufteilung der ruhenden Notch: wo die Uhr steht und welche Statuswerte links bzw. rechts davon.</summary>
/// <param name="Left">Indizes links der Uhr, von der Uhr nach außen.</param>
/// <param name="Right">Indizes rechts der Uhr, von der Uhr nach außen.</param>
public sealed record IdleArrangement(double Width, double ClockX, IReadOnlyList<int> Left, IReadOnlyList<int> Right);

/// <summary>
/// Die ruhende Notch wächst mit den eingeschalteten Werten. Mittig steht jeder Wert auf seiner festen Seite
/// (Datum und Akku rechts, Systemwerte links, wie im Infobereich); die Uhr bleibt in der Mitte, oder auf Wunsch
/// steht der ganze Inhalt mittig. Links oder rechts steht die Uhr am Rand und alle Werte daneben.
/// Passt nicht alles in <see cref="MaxWidth"/>, fallen Werte mit dem kleinsten Vorrang weg, statt über den Rand zu laufen.
/// </summary>
public static class IdleLayout
{
    public const double Padding = 14, Spacing = 10, MaxWidth = 560;

    /// <param name="items">Statuswerte in der gewünschten Reihenfolge (von der Uhr nach außen).</param>
    /// <param name="centerContent">Mittig: den ganzen Inhalt statt der Uhr zentrieren, wenn eine Seite länger ist.</param>
    public static IdleArrangement Arrange(ClockAlignment alignment, double clockWidth, IReadOnlyList<IdleItem> items, bool centerContent = false)
    {
        var shown = Enumerable.Range(0, items.Count).ToList();
        while (true)
        {
            var arrangement = Place(alignment, clockWidth, items, shown, centerContent);
            if (arrangement.Width <= MaxWidth || shown.Count == 0)
            {
                return arrangement;
            }
            // Den unwichtigsten Wert weglassen; bei Gleichstand den äußersten.
            var drop = shown.OrderBy(i => items[i].Priority).ThenByDescending(i => i).First();
            shown.Remove(drop);
        }
    }

    private static IdleArrangement Place(ClockAlignment alignment, double clockWidth, IReadOnlyList<IdleItem> items, List<int> shown, bool centerContent)
    {
        var minimum = NotchGeometry.Idle.Width;
        if (alignment == ClockAlignment.Center)
        {
            var left = shown.Where(i => items[i].Side == IdleSide.Left).ToList();
            var right = shown.Where(i => items[i].Side == IdleSide.Right).ToList();
            var l = Extent(left, items);
            var r = Extent(right, items);
            if (centerContent)
            {
                var content = clockWidth + l + r;
                var width = Even(Math.Max(minimum, 2 * Padding + content));
                return new IdleArrangement(width, (width - content) / 2 + l, left, right);
            }
            var symmetric = Even(Math.Max(minimum, 2 * Padding + clockWidth + 2 * Math.Max(l, r)));
            return new IdleArrangement(symmetric, (symmetric - clockWidth) / 2, left, right);
        }
        var total = Even(Math.Max(minimum, 2 * Padding + clockWidth + Extent(shown, items)));
        return alignment == ClockAlignment.Left
            ? new IdleArrangement(total, Padding, [], shown)
            : new IdleArrangement(total, total - Padding - clockWidth, shown, []);
    }

    /// <summary>Platz einer Seite neben der Uhr, mit dem Abstand zur Uhr.</summary>
    private static double Extent(IReadOnlyList<int> indices, IReadOnlyList<IdleItem> items) =>
        indices.Count == 0 ? 0 : indices.Sum(i => items[i].Width) + Spacing * indices.Count;

    /// <summary>Auf gerade ganze DIP: So liegen beide Kanten bei 100 % auf ganzen Pixeln und die Rundungen bleiben scharf.</summary>
    private static double Even(double width) => Math.Ceiling(width / 2) * 2;

    /// <summary>Seite und Vorrang je Statuswert (<see cref="StatusKeys"/>); unbekannte stehen rechts mit kleinem Vorrang.</summary>
    public static (IdleSide Side, int Priority) Slot(string key) => key switch
    {
        StatusKeys.Date => (IdleSide.Right, 3),
        StatusKeys.Battery => (IdleSide.Right, 4),
        StatusKeys.NotificationDot => (IdleSide.Right, 5),
        StatusKeys.Load => (IdleSide.Left, 2),
        StatusKeys.Network => (IdleSide.Left, 1),
        _ => (IdleSide.Right, 0),
    };
}

/// <summary>
/// Gleichbleibende Breiten für Statuswerte, deren Text sich ständig ändert („↓ 5,5 KB/s“ → „↓ 155 KB/s“):
/// Wachsen geht sofort, schrumpfen erst, wenn der Wert eine Weile kleiner war. So atmet die Notch nicht
/// bei jeder Messung.
/// </summary>
public sealed class StableWidths(TimeSpan shrinkAfter)
{
    private readonly Dictionary<string, (double Width, DateTimeOffset? SmallerSince)> _widths = new();

    public double Get(string key, double measured, DateTimeOffset now)
    {
        measured = Math.Ceiling(measured);
        if (!_widths.TryGetValue(key, out var entry) || measured >= entry.Width)
        {
            _widths[key] = (measured, null);
            return measured;
        }
        if (entry.SmallerSince is not { } since)
        {
            _widths[key] = (entry.Width, now);
            return entry.Width;
        }
        if (now - since >= shrinkAfter)
        {
            _widths[key] = (measured, null);
            return measured;
        }
        return entry.Width;
    }

    /// <summary>Vergisst Werte, die gerade nicht gezeigt werden; kommen sie wieder, beginnen sie bei ihrer echten Breite.</summary>
    public void Retain(IEnumerable<string> keys)
    {
        var keep = keys.ToHashSet();
        foreach (var key in _widths.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _widths.Remove(key);
        }
    }
}
