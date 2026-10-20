using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using Brim.Sdk;

namespace Brim.SwyxIt;

/// <summary>
/// Übersetzt zwischen SwyxIt! und Brim: Anrufe als Aktivitäten mit Schnellmenü (Stumm, Tastatur, Weiterleiten,
/// Halten), eine echte Wellenform des Gesprächs, Chats aus SwyxIt! und Swyx Messenger als HUD, Status als Karte.
/// Alles hier läuft auf dem STA-Thread des Plugins.
/// </summary>
internal sealed class SwyxBridge
{
    private static readonly string Phone = char.ConvertFromUtf32(0xE717);
    private static readonly string HangUpGlyph = char.ConvertFromUtf32(0xE778);
    private static readonly string Chat = char.ConvertFromUtf32(0xE8BD);
    private static readonly string Microphone = char.ConvertFromUtf32(0xE720);
    private static readonly string MicrophoneOff = char.ConvertFromUtf32(0xEC54);
    private static readonly string BackspaceGlyph = char.ConvertFromUtf32(0xE750);

    /// <summary>So oft wird ein klingelnder Anruf erneuert; Brim lässt kritische Hinweise nach 10 s ablaufen.</summary>
    private static readonly TimeSpan RingRefresh = TimeSpan.FromSeconds(5);

    /// <summary>Balken der Wellenform; bei 3 px Breite und Abstand füllt das die aufgeklappte Notch.</summary>
    private const int WaveLength = 90;

    private enum Panel { None, Dialpad, Transfer }

    private readonly BrimPlugin _brim;
    private readonly SynchronizationContext _sta;
    private readonly SwyxClient _swyx = new();
    private readonly CallAudio _audio = new();
    private readonly CallTracker _tracker = new();
    private readonly List<ChatItem> _chats = [];
    private readonly HashSet<int> _shownIncoming = [];
    private LineChanges _current = new([], [], []);
    private DateTime _lastRing;
    private int _retry;
    private double[] _wave = new double[WaveLength];

    // Schnellmenü: Tastatur für Tastentöne oder für die Nummer beim Weiterleiten.
    private Panel _panel;
    private int _panelLine = -1;
    private string _digits = "";
    private string? _notice;

    public SwyxBridge(BrimPlugin brim, SynchronizationContext sta)
    {
        _brim = brim;
        _sta = sta;
        _swyx.LinesChanged += OnLines;
        _swyx.ChatReceived += OnChat;
        _swyx.Problem += message => ShowNotice(message);
        // Brim schickt auf seinem Thread; COM gehört dem STA-Thread.
        _brim.ActionInvoked += (action, _) => _sta.Post(_ => OnAction(action), null);
        _brim.NotificationPosted += n => _sta.Post(_ => OnChat(new ChatItem(n.Title, n.Body, DateTime.Now, Messenger: true)), null);
        _brim.NotchExpandedChanged += expanded => _sta.Post(_ =>
        {
            if (!expanded)
            {
                ClosePanel();
            }
        }, null);
    }

    private bool ShowChat => !_brim.Settings.TryGetValue("chat", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.False;

    public void Start()
    {
        _swyx.TryConnect();
        PublishStatus();
    }

    /// <summary>Etwa 15-mal pro Sekunde: Pegel für die Wellenform, nur solange ein Gespräch läuft und die Notch offen ist.</summary>
    public void WaveTick()
    {
        if (!_brim.NotchExpanded || _panel != Panel.None || _current.Calls.FirstOrDefault(c => c.State == LineState.Active) is not { } call)
        {
            return;
        }
        _wave = CallTracker.Push(_wave, _audio.Peak(includeMicrophone: !_swyx.Muted));
        Send(_brim.SetActivityAsync(TalkActivity(call)));
    }

    /// <summary>Jede Sekunde: Verbindung herstellen, Gesprächsdauer, Klingeln erneuern.</summary>
    public void Tick()
    {
        if (!_swyx.IsConnected)
        {
            // Alle 10 s nachsehen, ob SwyxIt! inzwischen läuft.
            if (++_retry % 10 == 0 && _swyx.TryConnect())
            {
                PublishStatus();
            }
            return;
        }
        // Weiterleitung braucht auch ohne Meldung von SwyxIt! einen Blick (Zeitlimit).
        if (_swyx.Transfer is not null)
        {
            _swyx.Scan();
        }
        foreach (var call in _current.Calls)
        {
            Send(_brim.SetActivityAsync(TalkActivity(call)));
        }
        if (_current.Incoming.Count > 0 && DateTime.UtcNow - _lastRing >= RingRefresh)
        {
            _lastRing = DateTime.UtcNow;
            foreach (var call in _current.Incoming)
            {
                Send(_brim.SetActivityAsync(RingActivity(call)));
            }
        }
    }

    private void OnLines(IReadOnlyList<LineSnapshot> lines)
    {
        var changes = _tracker.Update(lines);
        var before = _current;
        _current = changes;

        foreach (var line in changes.Gone)
        {
            Send(_brim.EndActivityAsync($"ring-{line}"));
            Send(_brim.EndActivityAsync($"call-{line}"));
            _shownIncoming.Remove(line);
            if (line == _panelLine)
            {
                ClosePanel(publish: false);
            }
        }
        foreach (var call in changes.Incoming)
        {
            Send(_brim.EndActivityAsync($"call-{call.Line}"));
            if (_shownIncoming.Add(call.Line) || before.Incoming.All(c => c != call))
            {
                _lastRing = DateTime.UtcNow;
                Send(_brim.SetActivityAsync(RingActivity(call)));
            }
        }
        foreach (var call in changes.Calls)
        {
            if (_shownIncoming.Remove(call.Line))
            {
                Send(_brim.EndActivityAsync($"ring-{call.Line}"));
            }
            // Das Ziel einer Weiterleitung nicht als eigenes Gespräch zeigen.
            if (_swyx.Transfer?.To == call.Line)
            {
                continue;
            }
            Send(_brim.SetActivityAsync(TalkActivity(call)));
        }
        if (changes.Calls.Count == 0)
        {
            _wave = new double[WaveLength];
        }
        if (!_swyx.IsConnected)
        {
            PublishStatus();
        }
    }

    private void OnChat(ChatItem chat)
    {
        _chats.Insert(0, chat);
        if (_chats.Count > 5)
        {
            _chats.RemoveAt(_chats.Count - 1);
        }
        PublishStatus();
        if (!ShowChat)
        {
            return;
        }
        var from = chat.From.Length > 0 ? chat.From : "Chat";
        Send(_brim.ShowHudAsync(new HudParams
        {
            Id = "chat",
            DurationMs = 6000,
            Announcement = $"Nachricht von {from}: {chat.Text}",
            Content = new StackWidget(
                new IconWidget(Chat) { Size = 15, Color = BrimColors.Cpu },
                new TextWidget(from) { Style = TextStyle.Body, FontSize = 12 },
                new TextWidget(chat.Text) { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 })
            {
                Spacing = 8,
                Padding = (18, 0, 18, 0),
            },
        }));
    }

    private void ShowNotice(string message)
    {
        _notice = message;
        Send(_brim.ShowHudAsync(Phone, message));
    }

    // ───────── Darstellung ─────────

    private static string Detail(LineSnapshot call)
    {
        var parts = new List<string>();
        if (call.Name.Trim().Length > 0 && call.Number.Trim().Length > 0)
        {
            parts.Add(call.Number);
        }
        if (call.CalledName.Trim().Length > 0)
        {
            parts.Add("an " + call.CalledName);
        }
        if (call.State == LineState.Knocking)
        {
            parts.Add("klopft an");
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "Eingehender Anruf";
    }

    /// <summary>Klingeln: klappt die Notch auf (Recht „alerts“), mit Weiterleiten, Ablehnen und Annehmen.</summary>
    private static ActivityParams RingActivity(LineSnapshot call) => new()
    {
        Id = $"ring-{call.Line}",
        Priority = ActivityPriority.Critical,
        DurationMs = 10_000,
        Accent = BrimColors.Charge,
        Announcement = $"Anruf von {call.Caller}",
        Content = new StackWidget(
            new IconWidget(Phone) { Size = 24, Color = BrimColors.Charge },
            new StackWidget(
                new TextWidget(call.Caller) { Style = TextStyle.Title, FontSize = 15 },
                new TextWidget(Detail(call)) { Style = TextStyle.Secondary })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 2,
                Align = CrossAlign.Start,
                Grow = 1,
            },
            new ButtonWidget($"decline:{call.Line}") { Kind = ButtonKind.Secondary, Label = "Ablehnen", AccessibleName = $"Anruf von {call.Caller} ablehnen" },
            new ButtonWidget($"answer:{call.Line}") { Kind = ButtonKind.Secondary, Label = "Annehmen", AccessibleName = $"Anruf von {call.Caller} annehmen" })
        {
            Spacing = 10,
        },
    };

    private ActivityParams TalkActivity(LineSnapshot call)
    {
        var onHold = call.State is LineState.OnHold or LineState.ConferenceOnHold;
        var dialing = call.State is LineState.Dialing or LineState.Alerting;
        var transferring = _swyx.Transfer?.From == call.Line;
        var duration = call.Connected is { } start ? CallTracker.Duration(DateTime.Now - start) : "";
        var status = transferring ? "Leitet weiter …" : dialing ? "Wählt …" : onHold ? "Gehalten" : duration;
        var color = onHold || dialing || transferring ? BrimColors.Slate : BrimColors.Charge;
        var muted = _swyx.Muted;
        return new ActivityParams
        {
            Id = $"call-{call.Line}",
            Priority = ActivityPriority.Ongoing,
            Rank = 9,
            Accent = BrimColors.Charge,
            Leading = new IconWidget(muted ? MicrophoneOff : Phone) { Size = 15, Color = muted ? BrimColors.Danger : color },
            Trailing = new TextWidget(status) { Style = TextStyle.Mono, Color = color, AccessibleName = $"Gespräch mit {call.Caller}, {status}" },
            Bead = new IconWidget(Phone) { Size = 14, Color = color, AccessibleName = $"Gespräch mit {call.Caller}" },
            Expanded = _panel != Panel.None && _panelLine == call.Line
                ? KeypadView(call)
                : TalkView(call, status, color, onHold, muted),
        };
    }

    /// <summary>Kopfzeile: Telefon, Name, Nummer, Dauer – darunter die Welle und das Schnellmenü.</summary>
    private Widget TalkView(LineSnapshot call, string status, string color, bool onHold, bool muted) => new StackWidget(
        new StackWidget(
            new IconWidget(Phone) { Size = 22, Color = color },
            new StackWidget(
                new TextWidget(call.Caller) { Style = TextStyle.Title, FontSize = 17 },
                new TextWidget(call.Number.Trim().Length > 0 && call.Name.Trim().Length > 0 ? call.Number : "Gespräch") { Style = TextStyle.Secondary })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 2,
                Align = CrossAlign.Start,
                Grow = 1,
            },
            new TextWidget(status) { Style = TextStyle.Display, FontSize = 30, Color = onHold ? BrimColors.Slate : BrimColors.Fog })
        {
            Spacing = 12,
        },
        new WaveWidget(onHold ? new double[WaveLength] : _wave)
        {
            Height = 40,
            Color = muted ? BrimColors.Slate : BrimColors.Charge,
            AccessibleName = "Pegel des Gesprächs",
            Grow = 1,
        },
        new StackWidget(
            new ButtonWidget($"mute:{call.Line}")
            {
                Glyph = muted ? MicrophoneOff : Microphone,
                GlyphColor = muted ? BrimColors.Danger : BrimColors.Fog,
                Size = 36,
                GlyphSize = 15,
                AccessibleName = muted ? "Mikrofon einschalten" : "Stummschalten",
            },
            new ButtonWidget($"dialpad:{call.Line}") { Kind = ButtonKind.Secondary, Label = "Tastatur" },
            new ButtonWidget($"transfer:{call.Line}") { Kind = ButtonKind.Secondary, Label = "Weiterleiten" },
            new ButtonWidget($"hold:{call.Line}") { Kind = ButtonKind.Secondary, Label = onHold ? "Fortsetzen" : "Halten" },
            new SpacerWidget { Grow = 1 },
            new ButtonWidget($"hangup:{call.Line}")
            {
                Glyph = HangUpGlyph,
                GlyphColor = BrimColors.Danger,
                Size = 40,
                AccessibleName = "Auflegen",
            })
        {
            Spacing = 8,
        })
    {
        Orientation = StackOrientation.Vertical,
        Spacing = 8,
        Align = CrossAlign.Stretch,
    };

    /// <summary>Tastatur an der Stelle des Schnellmenüs: Nummer fürs Weiterleiten oder Tastentöne.</summary>
    private Widget KeypadView(LineSnapshot call)
    {
        var transfer = _panel == Panel.Transfer;
        StackWidget Row(int from) => new(Keypad.Keys[from..(from + 6)]
            .Select(k => (Widget)new ButtonWidget("key:" + k) { Kind = ButtonKind.Secondary, Label = k, Grow = 1, AccessibleName = $"Taste {k}" })
            .ToList())
        {
            Spacing = 6,
            Align = CrossAlign.Stretch,
        };
        return new StackWidget(
            new StackWidget(
                new TextWidget(transfer ? $"{call.Caller} weiterleiten an" : "Tastentöne") { Style = TextStyle.Secondary, FontSize = 12 },
                new TextWidget(_digits.Length > 0 ? _digits : transfer ? "Nummer eingeben" : "")
                {
                    Style = TextStyle.Mono,
                    FontSize = 22,
                    Color = _digits.Length > 0 ? BrimColors.Fog : BrimColors.Slate,
                    Grow = 1,
                    AccessibleName = _digits,
                },
                new ButtonWidget("backspace") { Glyph = BackspaceGlyph, Size = 32, GlyphSize = 14, AccessibleName = "Löschen" })
            {
                Spacing = 12,
            },
            Row(0),
            Row(6),
            new StackWidget(
                new ButtonWidget("keypad-close") { Kind = ButtonKind.Secondary, Label = transfer ? "Abbrechen" : "Fertig" },
                new SpacerWidget { Grow = 1 },
                transfer
                    ? new ButtonWidget("transfer-go") { Kind = ButtonKind.Secondary, Label = "Weiterleiten", AccessibleName = $"An {_digits} weiterleiten" }
                    : new SpacerWidget())
            {
                Spacing = 8,
            })
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 6,
            Align = CrossAlign.Stretch,
        };
    }

    private void PublishStatus()
    {
        var rows = new List<Widget>
        {
            new StackWidget(
                new IconWidget(Phone) { Size = 14, Color = _swyx.IsConnected ? BrimColors.Charge : BrimColors.Slate },
                new TextWidget("SwyxIt!") { Grow = 1 },
                new TextWidget(_swyx.IsConnected ? _swyx.User ?? "Verbunden" : "läuft nicht") { Style = TextStyle.Caption, FontSize = 12 })
            {
                Spacing = 8,
            },
        };
        foreach (var chat in _chats)
        {
            rows.Add(new StackWidget(
                new TextWidget(chat.Received.ToString("HH:mm")) { Style = TextStyle.MonoCaption },
                new TextWidget(chat.From) { FontSize = 12 },
                new TextWidget(chat.Text) { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 })
            {
                Spacing = 8,
            });
        }
        if (_swyx.IsConnected)
        {
            rows.Add(new StackWidget(new ButtonWidget("open-chat") { Kind = ButtonKind.Secondary, Label = "Chat öffnen" }) { Justify = Justify.End });
        }
        Send(_brim.SetViewAsync("status", new StackWidget(rows)
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 6,
            Align = CrossAlign.Stretch,
            Background = BrimColors.Graphite,
            CornerRadius = 14,
            Padding = (12, 10, 12, 10),
        }));
    }

    // ───────── Aktionen ─────────

    private void OpenPanel(Panel panel, int line)
    {
        _panel = panel;
        _panelLine = line;
        _digits = "";
    }

    private void ClosePanel(bool publish = true)
    {
        if (_panel == Panel.None)
        {
            return;
        }
        var line = _panelLine;
        _panel = Panel.None;
        _panelLine = -1;
        _digits = "";
        if (publish && _current.Calls.FirstOrDefault(c => c.Line == line) is { } call)
        {
            Send(_brim.SetActivityAsync(TalkActivity(call)));
        }
    }

    private void OnAction(string action)
    {
        var colon = action.IndexOf(':');
        var verb = colon < 0 ? action : action[..colon];
        var argument = colon < 0 ? "" : action[(colon + 1)..];
        _ = int.TryParse(argument, out var line);
        switch (verb)
        {
            case "answer":
                Send(_brim.EndActivityAsync($"ring-{line}"));
                _swyx.Answer(line);
                break;
            case "decline":
                Send(_brim.EndActivityAsync($"ring-{line}"));
                _swyx.HangUp(line);
                break;
            case "hangup":
                ClosePanel(publish: false);
                _swyx.HangUp(line);
                break;
            case "hold":
                _swyx.ToggleHold(line);
                break;
            case "mute":
                _swyx.ToggleMute();
                break;
            case "dialpad":
                OpenPanel(Panel.Dialpad, line);
                break;
            case "transfer":
                OpenPanel(Panel.Transfer, line);
                break;
            case "key":
                _digits = Keypad.Append(_digits, argument);
                // Tastentöne gehen sofort ins Gespräch; beim Weiterleiten wird nur gesammelt.
                if (_panel == Panel.Dialpad)
                {
                    _swyx.SendDtmf(_panelLine, argument);
                }
                break;
            case "backspace":
                _digits = Keypad.Backspace(_digits);
                break;
            case "keypad-close":
                ClosePanel();
                break;
            case "transfer-go" when _digits.Length > 0:
                var (from, number) = (_panelLine, _digits);
                ClosePanel(publish: false);
                _swyx.TransferTo(from, number);
                break;
            case "open-chat":
                _swyx.OpenChat();
                break;
        }
        _swyx.Scan();
        // Tastatur und Stumm sofort zeigen, nicht erst mit dem nächsten Sekundentakt.
        foreach (var call in _current.Calls)
        {
            Send(_brim.SetActivityAsync(TalkActivity(call)));
        }
    }

    private static void Send(Task task) => task.ContinueWith(t =>
    {
        if (t.IsFaulted)
        {
            Console.Error.WriteLine($"Brim lehnt ab: {t.Exception?.InnerException?.Message}");
        }
    }, TaskScheduler.Default);

    public void Dispose()
    {
        _swyx.Dispose();
        _audio.Dispose();
    }
}
