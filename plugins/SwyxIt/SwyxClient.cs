using System.Diagnostics;
using System.Runtime.InteropServices;
using IpPbx.CLMgrLib;

namespace Brim.SwyxIt;

/// <summary>Eine Chat-Nachricht aus SwyxIt!.</summary>
internal sealed record ChatItem(string From, string Text, DateTime Received);

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
            LinesChanged?.Invoke(snapshots);
        });
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
