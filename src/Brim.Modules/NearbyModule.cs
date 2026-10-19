using System.Collections.Concurrent;
using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules.Nearby;
using Brim.Modules.Wormhole;

namespace Brim.Modules;

/// <summary>
/// Teilen zwischen Brims in der Notch: Annahme-Frage beim Empfänger (kritischer Hinweis, auch wenn
/// die Notch versteckt ist), Fortschritt als laufende Aktivität, Ergebnis als HUD.
/// </summary>
public sealed class NearbyModule : IBrimModule
{
    public const string ModuleId = "nearby";

    private static readonly TimeSpan PromptDuration = TimeSpan.FromSeconds(60);

    private readonly NearbyService _service;
    private readonly Func<ShareSettings> _share;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();
    private ModuleContext? _context;
    private long _lastProgressTicks;
    private CancellationTokenSource? _codeCts;
    private string? _currentCode;

    public NearbyModule(NearbyService service, Func<ShareSettings> share)
    {
        _service = service;
        _share = share;
    }

    /// <summary>Von der Shell gesetzt: Text in die Zwischenablage (braucht den UI-Thread).</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    public string Id => ModuleId;
    public string DisplayName => "Teilen";

    public NearbyService Service => _service;

    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _service.ConfirmAsync = ConfirmAsync;
        _service.ReceiveProgress += (offer, p) => ShowProgress("receive", $"Von {offer.SenderName}", p);
        _service.Received += OnReceived;
        _service.ReceiveFailed += (offer, reason) =>
        {
            _context?.End("receive");
            Hud(BrimColors.Warning, offer is null ? "Empfang abgebrochen" : $"Empfang von {offer.SenderName} abgebrochen");
        };
        return Task.CompletedTask;
    }

    // ───────── Empfangen ─────────

    private Task<bool> ConfirmAsync(IncomingOffer incoming, CancellationToken ct)
    {
        var offer = incoming.Offer;
        var files = offer.Entries.Count(e => !e.Directory);
        var what = offer.TopLevel.Count == 1 ? offer.TopLevel[0] : $"{files} Dateien";
        var detail = $"{what} · {FormatSize(offer.TotalBytes)}" + (incoming.Verified ? "" : " · unbestätigt");
        return AskAsync($"{offer.SenderName} möchte etwas senden", detail, $"{offer.SenderName} möchte dir {what} senden", ct);
    }

    /// <summary>Annahme-Frage in der Notch (kritischer Hinweis, 60 s); ohne Antwort gilt sie als abgelehnt.</summary>
    private async Task<bool> AskAsync(string title, string detail, string announcement, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("N")[..8];
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = answer;

        _context?.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "ask-" + key,
            Priority = ActivityPriority.Critical,
            Duration = PromptDuration,
            Announcement = announcement,
            Content = new StackWidget(
                new IconWidget(Glyphs.Download) { Size = 18 },
                new StackWidget(
                    new TextWidget(title) { Style = TextStyle.Body, FontSize = 14 },
                    new TextWidget(detail) { Style = TextStyle.Secondary })
                {
                    Orientation = StackOrientation.Vertical,
                    Spacing = 2,
                    Align = CrossAlign.Stretch,
                    Grow = 1,
                },
                new ButtonWidget("decline:" + key) { Kind = ButtonKind.Secondary, Label = "Ablehnen" },
                new ButtonWidget("accept:" + key) { Kind = ButtonKind.Secondary, Label = "Annehmen" })
            {
                Spacing = 12,
            },
        });

        try
        {
            using (ct.Register(() => answer.TrySetResult(false)))
            {
                return await answer.Task;
            }
        }
        finally
        {
            _pending.TryRemove(key, out _);
            _context?.End("ask-" + key);
        }
    }

    private void OnReceived(Offer offer, IReadOnlyList<ShelfItem> items)
    {
        _context?.End("receive");
        var what = items.Count == 1 ? items[0].Name : $"{items.Count} Einträge";
        Hud(BrimColors.Charge, $"{what} von {offer.SenderName} in der Ablage");
    }

    public void OnAction(ActionInvocation invocation)
    {
        var id = invocation.ActionId;
        switch (id)
        {
            case "code-copy" when _currentCode is { } code:
                _ = CopyAsync(code);
                return;
            case "code-cancel":
                _codeCts?.Cancel();
                return;
        }
        var split = id.IndexOf(':');
        if (split < 0)
        {
            return;
        }
        var verb = id[..split];
        var key = id[(split + 1)..];
        if (_pending.TryGetValue(key, out var answer))
        {
            answer.TrySetResult(verb == "accept");
        }
    }

    // ───────── Senden ─────────

    /// <summary>Schickt Dateien an ein Brim in der Nähe und zeigt Fortschritt und Ergebnis in der Notch.</summary>
    public async Task SendAsync(NearbyPeer peer, IReadOnlyList<string> paths)
    {
        ShowProgress("send", $"An {peer.Name}", new TransferProgress(0, 1), waiting: true);
        try
        {
            await _service.SendAsync(peer, paths, new Progress(p => ShowProgress("send", $"An {peer.Name}", p)), CancellationToken.None);
            _context?.End("send");
            Hud(BrimColors.Charge, $"An {peer.Name} gesendet");
        }
        catch (NearbyDeclinedException)
        {
            _context?.End("send");
            Hud(BrimColors.Warning, $"{peer.Name} hat abgelehnt");
        }
        catch (Exception ex)
        {
            Log.Warn($"Teilen: Senden an {peer.Name} fehlgeschlagen", ex);
            _context?.End("send");
            Hud(BrimColors.Warning, $"{peer.Name} nicht erreichbar");
        }
    }

    // ───────── Per Code (Magic Wormhole) ─────────

    /// <summary>
    /// Holt einen Code, zeigt ihn in der Notch (und kopiert ihn), wartet auf den Empfänger und sendet.
    /// Der Empfänger kann Brim sein oder jedes Magic-Wormhole-Programm („wormhole receive“).
    /// </summary>
    public async Task SendByCodeAsync(IReadOnlyList<string> paths)
    {
        _codeCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _codeCts = cts;
        var transfer = new WormholeTransfer(_share());
        var what = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\', '/')) : $"{paths.Count} Einträge";
        try
        {
            ShowCode("…", what, connecting: true);
            await transfer.SendAsync(paths, code =>
            {
                _currentCode = code;
                ShowCode(code, what, connecting: false);
                _ = CopyAsync(code);
            }, new Progress(p =>
            {
                _context?.End("code");
                ShowProgress("send", $"Per Code: {what}", p);
            }), cts.Token);
            _context?.End("send");
            Hud(BrimColors.Charge, $"{what} gesendet");
        }
        catch (OperationCanceledException)
        {
            Hud(BrimColors.Slate, "Senden per Code abgebrochen");
        }
        catch (WormholeRemoteException ex)
        {
            Hud(BrimColors.Warning, ex.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase) ? "Empfänger hat abgelehnt" : ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn("Wormhole: Senden fehlgeschlagen", ex);
            Hud(BrimColors.Warning, Friendly(ex));
        }
        finally
        {
            _context?.End("code");
            _context?.End("send");
            _currentCode = null;
            if (_codeCts == cts)
            {
                _codeCts = null;
            }
        }
    }

    /// <summary>Empfängt mit einem Code von Brim oder einem anderen Magic-Wormhole-Programm in die Ablage.</summary>
    public async Task ReceiveByCodeAsync(string code)
    {
        var transfer = new WormholeTransfer(_share());
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        try
        {
            ShowProgress("receive", "Per Code", new TransferProgress(0, 1), waiting: true);
            var paths = await transfer.ReceiveAsync(code,
                (offer, ct) =>
                {
                    _context?.End("receive");
                    var detail = offer.IsDirectory
                        ? $"Ordner {offer.Name} · {offer.FileCount} Dateien · {FormatSize(offer.Size)}"
                        : $"{offer.Name} · {FormatSize(offer.Size)}";
                    return AskAsync("Per Code: Annehmen?", detail, $"Per Code angeboten: {offer.Name}", ct);
                },
                _service.Shelf.ReserveIncoming,
                new Progress(p => ShowProgress("receive", "Per Code", p)),
                cts.Token);
            _context?.End("receive");
            if (paths.Count > 0)
            {
                var items = _service.Shelf.AddReceived(paths);
                Hud(BrimColors.Charge, $"{(items.Count == 1 ? items[0].Name : $"{items.Count} Einträge")} in der Ablage");
            }
        }
        catch (WormholeWrongCodeException)
        {
            Hud(BrimColors.Warning, "Code passt nicht");
        }
        catch (Exception ex)
        {
            Log.Warn("Wormhole: Empfang fehlgeschlagen", ex);
            Hud(BrimColors.Warning, Friendly(ex));
        }
        finally
        {
            _context?.End("receive");
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        WormholeRemoteException remote => remote.Message,
        TimeoutException => "Keine Verbindung zur Gegenseite",
        System.Net.Sockets.SocketException or System.Net.WebSockets.WebSocketException or IOException => "Wormhole-Server nicht erreichbar",
        ArgumentException argument => argument.Message,
        _ => "Teilen per Code fehlgeschlagen",
    };

    /// <summary>Der Code bleibt in der kompakten Notch sichtbar, bis jemand ihn eingibt.</summary>
    private void ShowCode(string code, string what, bool connecting) =>
        _context?.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "code",
            Priority = ActivityPriority.Ongoing,
            Rank = 30,
            Announcement = connecting ? null : $"Code für {what}: {code}",
            Leading = new IconWidget(Glyphs.Link) { Size = 15, Color = BrimColors.Charge },
            Trailing = new TextWidget(connecting ? "Code wird geholt …" : code) { Style = TextStyle.Mono, FontSize = 12 },
            Expanded = new StackWidget(
                new TextWidget($"Code für {what}") { Style = TextStyle.Secondary, FontSize = 14 },
                new TextWidget(code) { Style = TextStyle.Mono, FontSize = 28 },
                new TextWidget("Auf dem anderen Gerät in Brim eingeben – oder mit „wormhole receive“.") { Style = TextStyle.Secondary },
                new SpacerWidget { Grow = 1 },
                new StackWidget(
                    new ButtonWidget("code-copy") { Kind = ButtonKind.Secondary, Label = "Code kopieren" },
                    new ButtonWidget("code-cancel") { Kind = ButtonKind.Secondary, Label = "Abbrechen" })
                {
                    Spacing = 8,
                })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 6,
                Align = CrossAlign.Start,
            },
        });

    private async Task CopyAsync(string code)
    {
        if (CopyToClipboard is { } copy)
        {
            await copy(code);
            Hud(BrimColors.Charge, $"Code {code} kopiert");
        }
    }

    // ───────── Anzeige ─────────

    private void ShowProgress(string id, string label, TransferProgress progress, bool waiting = false)
    {
        // Höchstens etwa zehnmal pro Sekunde melden; Anfang und Ende immer.
        var now = Environment.TickCount64;
        if (!waiting && progress.Done < progress.Total && now - Interlocked.Read(ref _lastProgressTicks) < 100)
        {
            return;
        }
        Interlocked.Exchange(ref _lastProgressTicks, now);
        var percent = waiting ? "wartet" : $"{(int)Math.Round(progress.Fraction * 100)} %";
        _context?.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = id,
            Priority = ActivityPriority.Ongoing,
            Rank = 20,
            Leading = new RingWidget(waiting ? 0.08 : progress.Fraction) { Size = 18, Color = BrimColors.Charge, TrackColor = "#203A2A" },
            Trailing = new TextWidget($"{label} · {percent}") { Style = TextStyle.MonoCaption, FontSize = 12 },
            Bead = new RingWidget(progress.Fraction) { Size = 18, Color = BrimColors.Charge, TrackColor = "#203A2A" },
            Expanded = new StackWidget(
                new TextWidget(label) { Style = TextStyle.Title },
                new ProgressWidget(progress.Fraction) { Color = BrimColors.Charge },
                new TextWidget(waiting ? "Wartet auf Zustimmung …" : $"{FormatSize(progress.Done)} von {FormatSize(progress.Total)}") { Style = TextStyle.Secondary })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 10,
                Align = CrossAlign.Stretch,
            },
        });
    }

    private void Hud(string color, string text) =>
        _context?.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "result",
            Priority = ActivityPriority.Transient,
            Duration = TimeSpan.FromSeconds(3),
            Announcement = text,
            Content = new StackWidget(
                new IconWidget(color == BrimColors.Charge ? Glyphs.CheckMark : Glyphs.Download) { Size = 14, Color = color },
                new TextWidget(text) { Style = TextStyle.Body, Grow = 1 })
            {
                Spacing = 10,
                Padding = (18, 0, 18, 0),
            },
        });

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }

    private sealed class Progress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var answer in _pending.Values)
        {
            answer.TrySetResult(false);
        }
        return ValueTask.CompletedTask;
    }
}
