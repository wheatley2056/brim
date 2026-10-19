namespace Brim.Core;

public enum NotchMode
{
    /// <summary>Zurückgezogen (Vollbild, Regeln).</summary>
    Hidden,
    /// <summary>Ruhe: nur die Uhrzeit.</summary>
    Idle,
    /// <summary>Laufende Aktivität, links und rechts je ein Slot.</summary>
    Compact,
    /// <summary>Vorübergehende Meldung.</summary>
    Hud,
    /// <summary>Kritischer Hinweis, erweitert sich kurz von selbst.</summary>
    Alert,
    /// <summary>Erweitert mit Tabs.</summary>
    Expanded,
}

public enum NotchTab { Now, Shelf, System, Plugins }

/// <summary>Was die Shell gerade zeigen soll.</summary>
public sealed record NotchPresentation(
    NotchMode Mode,
    ActiveActivity? Primary,
    IReadOnlyList<ActiveActivity> Beads,
    ActiveActivity? Idle,
    NotchTab Tab)
{
    public static NotchPresentation Empty { get; } = new(NotchMode.Idle, null, [], null, NotchTab.Now);
}

/// <summary>Eingaben der Shell, die nicht aus Aktivitäten stammen.</summary>
public readonly record struct NotchInput(bool IsExpanded, bool IsHidden, NotchTab Tab);

/// <summary>
/// Entscheidet aus allen Aktivitäten und dem Zustand der Shell, was gezeigt wird.
/// Reine Funktion ohne Seiteneffekte, damit sie vollständig testbar ist.
/// </summary>
public static class NotchStateMachine
{
    /// <summary>Höchstzahl an Perlen neben der Notch.</summary>
    public const int MaxBeads = 2;

    public static NotchPresentation Resolve(IReadOnlyList<ActiveActivity> activities, NotchInput input)
    {
        var critical = Newest(activities, ActivityPriority.Critical);
        var transient = Newest(activities, ActivityPriority.Transient);
        var ongoing = activities
            .Where(a => a.Priority == ActivityPriority.Ongoing)
            .OrderByDescending(a => a.Activity.Rank)
            .ThenByDescending(a => a.Sequence)
            .ToList();
        var idle = Newest(activities, ActivityPriority.Idle);
        var topOngoing = ongoing.FirstOrDefault();
        var foreground = ongoing.Where(a => !a.Activity.IsBackground).ToList();

        // Kritische Hinweise kommen auch durch, wenn die Notch versteckt ist.
        if (input.IsHidden)
        {
            return critical is not null
                ? new NotchPresentation(NotchMode.Alert, critical, [], idle, input.Tab)
                : new NotchPresentation(NotchMode.Hidden, null, [], idle, input.Tab);
        }

        if (input.IsExpanded)
        {
            return new NotchPresentation(NotchMode.Expanded, critical ?? topOngoing, [], idle, input.Tab);
        }

        if (critical is not null)
        {
            return new NotchPresentation(NotchMode.Alert, critical, [], idle, input.Tab);
        }

        if (transient is not null)
        {
            return new NotchPresentation(NotchMode.Hud, transient, [], idle, input.Tab);
        }

        if (foreground.Count > 0)
        {
            return new NotchPresentation(NotchMode.Compact, foreground[0], foreground.Skip(1).Take(MaxBeads).ToList(), idle, input.Tab);
        }

        return new NotchPresentation(NotchMode.Idle, idle, [], idle, input.Tab);
    }

    private static ActiveActivity? Newest(IReadOnlyList<ActiveActivity> activities, ActivityPriority priority) =>
        activities
            .Where(a => a.Priority == priority)
            .OrderByDescending(a => a.UpdatedAt)
            .ThenByDescending(a => a.Sequence)
            .FirstOrDefault();
}
