using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using Brim.Sdk;

namespace Brim.SwyxIt;

/// <summary>Übersetzt zwischen SwyxIt! und Brim: Anrufe als Aktivitäten, Chats als HUD, Status als Karte.</summary>
internal sealed class SwyxBridge
{
    private static readonly string Phone = char.ConvertFromUtf32(0xE717);
    private static readonly string HangUpGlyph = char.ConvertFromUtf32(0xE778);
    private static readonly string Chat = char.ConvertFromUtf32(0xE8BD);

    /// <summary>So oft wird ein klingelnder Anruf erneuert; Brim lässt kritische Hinweise nach 10 s ablaufen.</summary>
    private static readonly TimeSpan RingRefresh = TimeSpan.FromSeconds(5);

    private readonly BrimPlugin _brim;
    private readonly SynchronizationContext _sta;
    private readonly SwyxClient _swyx = new();
    private readonly CallTracker _tracker = new();
    private readonly List<ChatItem> _chats = [];
    private LineChanges _current = new([], [], []);
    private readonly HashSet<int> _shownIncoming = [];
    private DateTime _lastRing;
    private int _retry;

    public SwyxBridge(BrimPlugin brim, SynchronizationContext sta)
    {
        _brim = brim;
        _sta = sta;
        _swyx.LinesChanged += OnLines;
        _swyx.ChatReceived += OnChat;
        // Brim schickt Aktionen auf seinem Thread; COM gehört dem STA-Thread.
        _brim.ActionInvoked += (action, _) => _sta.Post(_ => OnAction(action), null);
    }

    private bool ShowChat => !_brim.Settings.TryGetValue("chat", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.False;

    public void Start()
    {
        _swyx.TryConnect();
        PublishStatus();
    }

    /// <summary>Jede Sekunde auf dem STA-Thread: Verbindung herstellen, Gesprächsdauer, Klingeln erneuern.</summary>
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
            Send(_brim.SetActivityAsync(TalkActivity(call)));
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

    /// <summary>Klingeln: klappt die Notch auf (Recht „alerts“), mit Ablehnen und Annehmen.</summary>
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

    private static ActivityParams TalkActivity(LineSnapshot call)
    {
        var onHold = call.State is LineState.OnHold or LineState.ConferenceOnHold;
        var dialing = call.State is LineState.Dialing or LineState.Alerting;
        var duration = call.Connected is { } start ? CallTracker.Duration(DateTime.Now - start) : "";
        var status = dialing ? "Wählt …" : onHold ? "Gehalten" : duration;
        var color = onHold || dialing ? BrimColors.Slate : BrimColors.Charge;
        return new ActivityParams
        {
            Id = $"call-{call.Line}",
            Priority = ActivityPriority.Ongoing,
            Rank = 9,
            Accent = BrimColors.Charge,
            Leading = new IconWidget(Phone) { Size = 15, Color = color },
            Trailing = new TextWidget(status) { Style = TextStyle.Mono, Color = color, AccessibleName = $"Gespräch mit {call.Caller}, {status}" },
            Bead = new IconWidget(Phone) { Size = 14, Color = color, AccessibleName = $"Gespräch mit {call.Caller}" },
            Expanded = new StackWidget(
                new StackWidget(
                    new IconWidget(Phone) { Size = 22, Color = color },
                    new StackWidget(
                        new TextWidget(call.Caller) { Style = TextStyle.Title, FontSize = 17 },
                        new TextWidget(call.Number.Trim().Length > 0 && call.Name.Trim().Length > 0 ? call.Number : dialing ? "Wählt …" : "Gespräch") { Style = TextStyle.Secondary })
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
                new SpacerWidget { Grow = 1 },
                new StackWidget(
                    new ButtonWidget($"hold:{call.Line}") { Kind = ButtonKind.Secondary, Label = onHold ? "Fortsetzen" : "Halten" },
                    new ButtonWidget($"hangup:{call.Line}")
                    {
                        Glyph = HangUpGlyph,
                        GlyphColor = BrimColors.Danger,
                        Size = 40,
                        AccessibleName = "Auflegen",
                    })
                {
                    Spacing = 10,
                    Justify = Justify.End,
                })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 8,
                Align = CrossAlign.Stretch,
            },
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

    private void OnAction(string action)
    {
        var colon = action.IndexOf(':');
        var verb = colon < 0 ? action : action[..colon];
        _ = int.TryParse(colon < 0 ? "" : action[(colon + 1)..], out var line);
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
                _swyx.HangUp(line);
                break;
            case "hold":
                _swyx.ToggleHold(line);
                break;
            case "open-chat":
                _swyx.OpenChat();
                break;
        }
        _swyx.Scan();
    }

    private static void Send(Task task) => task.ContinueWith(t =>
    {
        if (t.IsFaulted)
        {
            Console.Error.WriteLine($"Brim lehnt ab: {t.Exception?.InnerException?.Message}");
        }
    }, TaskScheduler.Default);

    public void Dispose() => _swyx.Dispose();
}
