using System.Diagnostics;
using System.Runtime.InteropServices;
using IpPbx.CLMgrLib;

namespace Brim.SwyxIt;

/// <summary>Eine Chat-Nachricht aus SwyxIt!.</summary>
/// <param name="Messenger">Aus Swyx Messenger (über die Windows-Benachrichtigung) statt aus dem klassischen Chat.</param>
internal sealed record ChatItem(string From, string Text, DateTime Received, bool Messenger = false);

/// <summary>
/// Verbindung zu SwyxIt! über den Client Line Manager (COM, <c>CLMgr.ClientLineMgr</c>). Läuft komplett auf
/// dem STA-Thread des Plugins: COM-Objekte und ihre Ereignisse brauchen die Nachrichtenschleife dieses Threads.
/// </summary>
internal sealed class SwyxClient : IDisposable
{
    private ClientLineMgrClass? _manager;
    private IClientLineMgrDisp? _lines;
    private uint _chatReader;

    public event Action<IReadOnlyList<LineSnapshot>>? LinesChanged;
    public event Action<ChatItem>? ChatReceived;

    public bool IsConnected => _lines is not null;

    /// <summary>Mikrofon stumm (gilt für alle Leitungen, wie der Knopf in SwyxIt!).</summary>
    public bool Muted
    {
        get
        {
            try
            {
                return _lines is { } lines && lines.DispMicroEnabled == 0;
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    /// <summary>Laufende Weiterleitung, bis das Ziel klingelt oder sie scheitert.</summary>
    public PendingTransfer? Transfer { get; private set; }

    /// <summary>Meldung für den Nutzer, z. B. warum eine Weiterleitung nicht ging.</summary>
    public event Action<string>? Problem;

    /// <summary>Angemeldeter Swyx-Benutzer, wenn verbunden.</summary>
    public string? User { get; private set; }

    /// <summary>SwyxIt! läuft? Ohne laufenden Client würde COM ihn sonst starten.</summary>
    private static bool ClientRunning() => Process.GetProcessesByName("SwyxIt!").Length > 0;

    public bool TryConnect()
    {
        if (IsConnected)
        {
            return true;
        }
        if (!ClientRunning())
        {
            return false;
        }
        try
        {
            _manager = new ClientLineMgrClass();
            _lines = _manager;
            ((IClientLineMgrEventsDisp_Event)_manager).DispOnLineMgrNotification += OnNotification;
            try
            {
                _chatReader = _lines.DispRegisterChatMessageReader();
            }
            catch (COMException ex)
            {
                Console.Error.WriteLine($"Chat nicht verfügbar: {ex.Message}");
            }
            try
            {
                User = _lines.DispGetCurrentUser;
            }
            catch (COMException)
            {
                User = null;
            }
            Console.WriteLine($"Mit SwyxIt! verbunden ({User})");
            Scan();
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Console.Error.WriteLine($"SwyxIt! nicht erreichbar: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    /// <summary>
    /// Die Schnittstelle meldet nur „etwas hat sich geändert“ (Leitung, Auswahl, Chat …). Deshalb wird bei jeder
    /// Meldung alles neu gelesen; das ist billig und hängt nicht an den Nummern der einzelnen Meldungen.
    /// </summary>
    private void OnNotification(int message, int param)
    {
        Guard(() =>
        {
            Scan();
            DrainChat();
        });
    }

    public void Scan()
    {
        if (_lines is not { } lines)
        {
            return;
        }
        Guard(() =>
        {
            var snapshots = new List<LineSnapshot>();
            for (var i = 0; i < lines.DispNumberOfLines; i++)
            {
                if (lines.DispGetLine(i) is not IClientLineDisp line)
                {
                    continue;
                }
                var state = (LineState)line.DispState;
                DateTime? connected = null;
                if (state is LineState.Active or LineState.OnHold or LineState.ConferenceActive or LineState.ConferenceOnHold)
                {
                    connected = line.DispConnectionStartTime is { Year: > 2000 } start ? start : null;
                }
                snapshots.Add(new LineSnapshot(i, state, line.DispPeerName ?? "", line.DispPeerNumber ?? "",
                    line.DispCalledName ?? "", line.DispIsOutgoingCall != 0, connected));
            }
            AdvanceTransfer(snapshots);
            LinesChanged?.Invoke(snapshots);
        });
    }

    /// <summary>Weiterleitung vorantreiben: verbinden, sobald das Ziel klingelt, oder abbrechen und zurückholen.</summary>
    private void AdvanceTransfer(IReadOnlyList<LineSnapshot> lines)
    {
        if (Transfer is not { } transfer)
        {
            return;
        }
        var target = lines.FirstOrDefault(l => l.Line == transfer.To)?.State ?? LineState.None;
        switch (transfer.Next(target, DateTime.UtcNow))
        {
            case TransferStep.Complete:
                Transfer = null;
                if (Line(transfer.From) is { } from && Line(transfer.To) is { } to)
                {
                    from.DispTransferCall(to);
                    Console.WriteLine($"Weitergeleitet an {transfer.Number}");
                }
                break;
            case TransferStep.Cancel:
                Transfer = null;
                Line(transfer.To)?.DispHookOn();
                if (Line(transfer.From) is { } original)
                {
                    _lines!.DispSelectLine(original);
                    original.DispActivate();
                }
                Problem?.Invoke($"{transfer.Number} ist nicht erreichbar – das Gespräch ist zurück.");
                break;
        }
    }

    private void DrainChat()
    {
        if (_lines is not { } lines || _chatReader == 0)
        {
            return;
        }
        for (var i = 0; i < 50; i++)
        {
            object? item;
            try
            {
                item = lines.DispReadChatMessage(_chatReader);
            }
            catch (COMException)
            {
                // Keine weitere Nachricht.
                return;
            }
            if (item is not IClChatMessageItemDisp message)
            {
                return;
            }
            ChatReceived?.Invoke(new ChatItem(message.DispPeerName ?? "", message.DispMessageText ?? "", DateTime.Now));
            try
            {
                lines.DispAcknowledgeChatMessage(message.DispMessageId, message.DispPeerName, message.DispPeerIpAddress);
            }
            catch (COMException)
            {
            }
        }
    }

    // ───────── Aktionen ─────────

    private IClientLineDisp? Line(int index) => _lines?.DispGetLine(index) as IClientLineDisp;

    /// <summary>Annehmen: Leitung auswählen (laufendes Gespräch wird gehalten), dann abheben.</summary>
    public void Answer(int index) => Guard(() =>
    {
        if (Line(index) is { } line)
        {
            _lines!.DispSelectLine(line);
            line.DispHookOff();
        }
    });

    /// <summary>Ablehnen bzw. Auflegen.</summary>
    public void HangUp(int index) => Guard(() => Line(index)?.DispHookOn());

    public void ToggleHold(int index) => Guard(() =>
    {
        if (Line(index) is { } line)
        {
            if ((LineState)line.DispState is LineState.OnHold or LineState.ConferenceOnHold)
            {
                _lines!.DispSelectLine(line);
                line.DispActivate();
            }
            else
            {
                line.DispHold();
            }
        }
    });

    public void ToggleMute() => Guard(() =>
    {
        if (_lines is { } lines)
        {
            lines.DispMicroEnabled = lines.DispMicroEnabled == 0 ? 1 : 0;
        }
    });

    /// <summary>Tastentöne ins Gespräch (DTMF), z. B. für Sprachmenüs.</summary>
    public void SendDtmf(int index, string digits) => Guard(() => Line(index)?.DispSendDtmf(digits, 0));

    /// <summary>
    /// Weiterleiten an eine Nummer. Klingelt der Anruf noch, wird er direkt umgeleitet; läuft das Gespräch,
    /// wählt Brim das Ziel auf einer freien Leitung und verbindet, sobald es dort klingelt.
    /// </summary>
    public void TransferTo(int index, string number) => Guard(() =>
    {
        if (_lines is not { } lines || Line(index) is not { } line || number.Length == 0)
        {
            return;
        }
        if ((LineState)line.DispState is LineState.Ringing or LineState.Knocking)
        {
            line.DispForwardCall(number);
            return;
        }
        var free = Enumerable.Range(0, lines.DispNumberOfLines)
            .FirstOrDefault(i => i != index && (LineState)(Line(i)?.DispState ?? -1) == LineState.Inactive, -1);
        if (free < 0)
        {
            Problem?.Invoke("Keine freie Leitung zum Weiterleiten.");
            return;
        }
        Transfer = new PendingTransfer(index, free, number, DateTime.UtcNow);
        lines.DispSimpleDialEx2(number, free);
    });

    public void OpenChat() => Guard(() =>
    {
        if (_manager is IClientLineMgrInstantMessaging im)
        {
            im.ShowChatApplication();
        }
    });

    /// <summary>SwyxIt! wurde beendet: COM meldet „Server nicht verfügbar“. Verbindung lösen, später neu versuchen.</summary>
    private void Guard(Action work)
    {
        try
        {
            work();
        }
        catch (COMException ex) when (ex.HResult is unchecked((int)0x800706BA) or unchecked((int)0x80010108) or unchecked((int)0x800706BE))
        {
            Console.Error.WriteLine($"Verbindung zu SwyxIt! verloren: {ex.Message}");
            Disconnect();
            LinesChanged?.Invoke([]);
        }
        catch (COMException ex)
        {
            Console.Error.WriteLine($"SwyxIt! lehnt ab: {ex.Message}");
        }
    }

    private void Disconnect()
    {
        if (_manager is not null)
        {
            try
            {
                ((IClientLineMgrEventsDisp_Event)_manager).DispOnLineMgrNotification -= OnNotification;
                if (_chatReader != 0)
                {
                    _lines?.DispUnRegisterChatMessageReader(_chatReader);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
            }
            Marshal.FinalReleaseComObject(_manager);
        }
        _manager = null;
        _lines = null;
        _chatReader = 0;
        User = null;
    }

    public void Dispose() => Disconnect();
}
