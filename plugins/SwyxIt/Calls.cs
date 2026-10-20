namespace Brim.SwyxIt;

/// <summary>Zustände einer Leitung, wie <c>CLMgrLineStates</c> in der Swyx-Schnittstelle (Werte identisch).</summary>
internal enum LineState
{
    None = -1,
    Inactive = 0,
    HookOffInternal = 1,
    HookOffExternal = 2,
    Ringing = 3,
    Dialing = 4,
    Alerting = 5,
    Knocking = 6,
    Busy = 7,
    Active = 8,
    OnHold = 9,
    ConferenceActive = 10,
    ConferenceOnHold = 11,
    Terminated = 12,
}

/// <summary>Eine Leitung zu einem Zeitpunkt.</summary>
internal sealed record LineSnapshot(int Line, LineState State, string Name, string Number, string CalledName, bool Outgoing, DateTime? Connected)
{
    /// <summary>Klingelt (oder klopft an) und ist kein eigener Anruf.</summary>
    public bool IsIncoming => State is LineState.Ringing or LineState.Knocking && !Outgoing;

    /// <summary>Gespräch läuft oder wird gehalten.</summary>
    public bool IsCall => State is LineState.Active or LineState.OnHold or LineState.ConferenceActive or LineState.ConferenceOnHold
        or LineState.Alerting or LineState.Dialing;

    /// <summary>Anzeigename: Name, sonst Nummer, sonst „Unbekannt“.</summary>
    public string Caller => Name.Trim() is { Length: > 0 } name ? name : Number.Trim() is { Length: > 0 } number ? number : "Unbekannt";
}

/// <summary>Was sich seit dem letzten Blick auf die Leitungen geändert hat.</summary>
internal sealed record LineChanges(IReadOnlyList<LineSnapshot> Incoming, IReadOnlyList<LineSnapshot> Calls, IReadOnlyList<int> Gone);

/// <summary>
/// Vergleicht Momentaufnahmen der Leitungen. Die Swyx-Schnittstelle meldet nur „etwas hat sich geändert“;
/// deshalb liest das Plugin bei jeder Meldung alle Leitungen und leitet hier ab, was zu zeigen ist.
/// </summary>
internal sealed class CallTracker
{
    private readonly Dictionary<int, LineSnapshot> _shown = new();

    public LineChanges Update(IEnumerable<LineSnapshot> lines)
    {
        var current = lines.Where(l => l.IsIncoming || l.IsCall).ToDictionary(l => l.Line);
        var gone = _shown.Keys.Where(k => !current.ContainsKey(k)).ToList();
        _shown.Clear();
        foreach (var (line, snapshot) in current)
        {
            _shown[line] = snapshot;
        }
        return new LineChanges(
            current.Values.Where(l => l.IsIncoming).OrderBy(l => l.Line).ToList(),
            current.Values.Where(l => l.IsCall).OrderBy(l => l.Line).ToList(),
            gone);
    }

    /// <summary>„3:07“ bzw. „1:02:03“.</summary>
    public static string Duration(TimeSpan time)
    {
        var seconds = Math.Max(0, (long)time.TotalSeconds);
        return seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
    }
}
