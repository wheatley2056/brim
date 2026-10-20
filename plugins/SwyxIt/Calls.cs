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

/// <summary>Was mit einer laufenden Weiterleitung als Nächstes passiert.</summary>
internal enum TransferStep { Wait, Complete, Cancel }

/// <summary>
/// Weiterleiten eines laufenden Gesprächs: Brim wählt das Ziel auf einer freien Leitung (das Gespräch wird
/// dabei gehalten) und verbindet beide, sobald es beim Ziel klingelt oder abgenommen wird.
/// </summary>
internal sealed record PendingTransfer(int From, int To, string Number, DateTime Started)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public TransferStep Next(LineState target, DateTime now) => target switch
    {
        LineState.Alerting or LineState.Active => TransferStep.Complete,
        LineState.Busy or LineState.Terminated => TransferStep.Cancel,
        _ when now - Started > Timeout => TransferStep.Cancel,
        _ => TransferStep.Wait,
    };
}

/// <summary>Eingabe über die Tastatur in der Notch: Ziffern sammeln (Weiterleiten) oder sofort senden (DTMF).</summary>
internal static class Keypad
{
    public static readonly string[] Keys = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "0", "#"];

    /// <summary>Ziffer anhängen; höchstens 32 Zeichen, nur Tasten der Tastatur und ein führendes „+“.</summary>
    public static string Append(string digits, string key) =>
        digits.Length >= 32 || !(Keys.Contains(key) || (key == "+" && digits.Length == 0)) ? digits : digits + key;

    public static string Backspace(string digits) => digits.Length == 0 ? digits : digits[..^1];
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

    /// <summary>Pegelverlauf für die Wellenform: neuer Wert rechts, feste Länge, leicht verstärkt (Sprache ist leise).</summary>
    public static double[] Push(double[] history, double peak)
    {
        var next = new double[history.Length];
        Array.Copy(history, 1, next, 0, history.Length - 1);
        next[^1] = Math.Clamp(Math.Sqrt(Math.Max(0, peak)) * 1.15, 0, 1);
        return next;
    }

    /// <summary>„3:07“ bzw. „1:02:03“.</summary>
    public static string Duration(TimeSpan time)
    {
        var seconds = Math.Max(0, (long)time.TotalSeconds);
        return seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
    }
}
