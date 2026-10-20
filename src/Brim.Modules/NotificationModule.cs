using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules.Sensors;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Brim.Modules;

/// <summary>Eine Benachrichtigung, wie das Modul sie zeigt.</summary>
/// <param name="AppId">AppUserModelId der App, damit ein Klick sie öffnet; <c>null</c>, wenn unbekannt.</param>
public sealed record NotificationPreview(string App, string Title, string Body, string? AppId = null);

/// <summary>
/// Benachrichtigungen spiegeln (A3, Plan 4.3): Neue Windows-Benachrichtigungen erscheinen kurz als Vorschau in der
/// Notch. Braucht Paketidentität und die Zustimmung des Nutzers (Windows fragt beim ersten Start).
/// Apps aus <see cref="NotificationSettings.MutedApps"/> bleiben stumm.
/// </summary>
public sealed class NotificationModule : IBrimModule
{
    public const string ModuleId = "notifications";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private ModuleContext? _context;
    private UserNotificationListener? _listener;
    private bool _subscribed;
    private readonly NotificationTracker _tracker = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, ImageData?> _logos = new();
    private readonly Lock _logoGate = new();
    private string? _shownAppId;

    /// <summary>
    /// Gibt eine Benachrichtigung an ein Plugin weiter (z. B. Swyx Messenger an das SwyxIt!-Plugin).
    /// <c>true</c>, wenn es sie übernommen hat; dann zeigt Brim sie nicht noch einmal.
    /// </summary>
    public Func<NotificationPreview, bool>? Route { get; set; }

    public string Id => ModuleId;
    public string DisplayName => "Benachrichtigungen";

    public async Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        if (!Identity.Present)
        {
            Log.Info("Benachrichtigungen brauchen Paketidentität (Einstellungen → Kalender und Benachrichtigungen)");
            FeatureStatus.Notifications = "Aus: Brim läuft ohne Paketidentität.";
            return;
        }
        try
        {
            // Die Rückfrage muss vom UI-Thread kommen; StartAsync läuft dort.
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Log.Info($"Zugriff auf Benachrichtigungen: {access} (Windows-Einstellungen → Datenschutz → Benachrichtigungen)");
                FeatureStatus.Notifications = $"Aus: Windows erlaubt den Zugriff nicht ({access}). Einstellungen → Datenschutz → Benachrichtigungen.";
                return;
            }
            // Was jetzt schon im Info-Center liegt, ist nicht neu.
            _tracker.Seed(await ReadIdsAsync(_listener));
            try
            {
                _listener.NotificationChanged += OnChanged;
                _subscribed = true;
            }
            catch (Exception ex)
            {
                // Für Desktop-Apps wirft das Ereignis oft „Element nicht gefunden“; dann reicht das Abfragen.
                Log.Info($"Benachrichtigungs-Ereignis nicht verfügbar, frage alle {PollInterval.TotalSeconds:0} s ab: {ex.Message}");
            }
            _ = Task.Run(() => PollAsync(_listener, _stop.Token), CancellationToken.None);
            Log.Info("Benachrichtigungen werden gespiegelt");
            FeatureStatus.Notifications = _subscribed ? "Aktiv (Ereignis und Abfrage alle 2 s)." : "Aktiv (Abfrage alle 2 s, das Ereignis gibt es nicht).";
        }
        catch (Exception ex)
        {
            Log.Warn("Benachrichtigungen nicht verfügbar", ex);
            FeatureStatus.Notifications = $"Fehler: {ex.Message}";
        }
    }

    private static async Task<IReadOnlyList<UserNotification>> ReadAsync(UserNotificationListener listener) =>
        [.. await listener.GetNotificationsAsync(NotificationKinds.Toast)];

    private static async Task<IEnumerable<uint>> ReadIdsAsync(UserNotificationListener listener) =>
        (await ReadAsync(listener)).Select(n => n.Id);

    /// <summary>
    /// Auch mit Ereignis: Desktop-Apps bekommen es nicht zuverlässig. Doppelte meldet der <see cref="NotificationTracker"/> nicht.
    /// </summary>
    private async Task PollAsync(UserNotificationListener listener, CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollInterval);
        var failures = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    var current = await ReadAsync(listener);
                    var fresh = _tracker.Update(current.Select(n => n.Id)).ToHashSet();
                    foreach (var notification in current.Where(n => fresh.Contains(n.Id)))
                    {
                        await HandleAsync(notification);
                    }
                    failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (failures++ == 0)
                    {
                        Log.Warn("Benachrichtigungen abfragen fehlgeschlagen", ex);
                        FeatureStatus.Notifications = $"Fehler beim Abfragen: {ex.Message}";
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnChanged(UserNotificationListener sender, Windows.UI.Notifications.UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added)
        {
            return;
        }
        try
        {
            if (_tracker.Add(args.UserNotificationId) && sender.GetNotification(args.UserNotificationId) is { } notification)
            {
                _ = HandleAsync(notification);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Benachrichtigung nicht lesbar", ex);
        }
    }

    private async Task HandleAsync(UserNotification notification)
    {
        try
        {
            var info = notification.AppInfo;
            var app = info?.DisplayInfo?.DisplayName ?? "";
            var visual = notification.Notification?.Visual;
            // Ältere Apps nutzen die alten Vorlagen (ToastText02 …) statt ToastGeneric.
            var binding = visual?.GetBinding(KnownNotificationBindings.ToastGeneric) ?? visual?.Bindings.FirstOrDefault();
            var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
            if (Preview(app, texts) is not { } found)
            {
                return;
            }
            var preview = found with { AppId = info?.AppUserModelId };
            if (Route?.Invoke(preview) == true || _context is not { } context)
            {
                return;
            }
            // Präsentation, Vollbild oder „Nicht stören“: nicht dazwischenfunken, nur der Punkt wie bei „Nur Punkt“.
            var quiet = context.Settings.Notifications.QuietWhenBusy && QuietHours.IsActive();
            if (context.Settings.Notifications.Style == NotificationStyle.Dot || quiet)
            {
                // Die Uhr bleibt; ein blauer Punkt zeigt, dass etwas gekommen ist, bis die Notch aufgeht.
                context.SetStatus("dot", new IconWidget(Glyphs.Dot) { Size = 10, Color = BrimColors.Charging, AccessibleName = $"Neue Benachrichtigung von {preview.App}" });
            }
            else
            {
                Show(preview, info is null ? null : await LogoAsync(info));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Benachrichtigung nicht lesbar", ex);
        }
    }

    /// <summary>Symbol der App in 32 px, je App einmal gelesen; <c>null</c>, wenn sie keins hat.</summary>
    private async Task<ImageData?> LogoAsync(Windows.ApplicationModel.AppInfo info)
    {
        var id = info.AppUserModelId ?? info.DisplayInfo?.DisplayName ?? "";
        lock (_logoGate)
        {
            if (_logos.TryGetValue(id, out var cached))
            {
                return cached;
            }
        }
        ImageData? logo = null;
        try
        {
            if (info.DisplayInfo?.GetLogo(new Windows.Foundation.Size(32, 32)) is { } reference)
            {
                using var stream = await reference.OpenReadAsync();
                using var reader = new Windows.Storage.Streams.DataReader(stream);
                var length = (uint)stream.Size;
                await reader.LoadAsync(length);
                var bytes = new byte[length];
                reader.ReadBytes(bytes);
                logo = bytes.Length > 0 ? new ImageData("app:" + id, bytes) : null;
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Kein Symbol für {id}: {ex.Message}");
        }
        lock (_logoGate)
        {
            _logos[id] = logo;
        }
        return logo;
    }

    /// <summary>Erste Zeile ist der Titel, der Rest der Text. <c>null</c> für Brim selbst und stumme Apps.</summary>
    public NotificationPreview? Preview(string app, IReadOnlyList<string> texts)
    {
        if (texts.Count == 0 || app.Equals("Brim", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var muted = _context?.Settings.Notifications.MutedApps ?? [];
        if (muted.Any(m => app.Equals(m, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return new NotificationPreview(app.Length > 0 ? app : "Benachrichtigung", texts[0].Trim(), string.Join(" ", texts.Skip(1)).Trim());
    }

    /// <summary>
    /// Breites HUD in zwei Zeilen: links das Symbol der App, oben Titel und App, darunter der Text, beides mit
    /// Auslassungspunkten. Ein Klick öffnet die App.
    /// </summary>
    public void Show(NotificationPreview preview, ImageData? logo = null)
    {
        _shownAppId = preview.AppId;
        Widget icon = logo is not null
            ? new ImageWidget(logo) { Width = 28, Height = 28, CornerRadius = 6 }
            : new IconWidget(Glyphs.Bell) { Size = 18, Color = BrimColors.Slate };
        var title = new StackWidget(
            new TextWidget(preview.Title) { FontSize = 13, Grow = 1 },
            new TextWidget(preview.App) { Style = TextStyle.Caption })
        {
            Spacing = 8,
        };
        var lines = preview.Body.Length > 0
            ? new StackWidget(title, new TextWidget(preview.Body) { Style = TextStyle.Secondary, FontSize = 12 })
            : new StackWidget(title);
        _context?.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "preview",
            Priority = ActivityPriority.Transient,
            Duration = TimeSpan.FromSeconds(5),
            Wide = true,
            Announcement = $"{preview.App}: {preview.Title}. {preview.Body}",
            Content = new StackWidget(
                icon,
                lines with
                {
                    Orientation = StackOrientation.Vertical,
                    Spacing = 2,
                    Align = CrossAlign.Stretch,
                    Grow = 1,
                })
            {
                Spacing = 12,
                Padding = (20, 0, 22, 0),
                ActionId = preview.AppId is null ? null : "open",
                AccessibleName = preview.AppId is null ? null : $"{preview.App} öffnen",
            },
        });
    }

    /// <summary>Klick auf die Vorschau: die App öffnen, von der sie kam.</summary>
    public void OnAction(ActionInvocation invocation)
    {
        if (invocation.ActionId != "open" || _shownAppId is not { } appId)
        {
            return;
        }
        _context?.End("preview");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Info($"App {appId} nicht geöffnet: {ex.Message}");
        }
    }

    public void OnExpandedChanged(bool expanded)
    {
        if (expanded)
        {
            _context?.SetStatus("dot", null);
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_listener is not null && _subscribed)
        {
            try
            {
                _listener.NotificationChanged -= OnChanged;
            }
            catch (Exception ex)
            {
                Log.Info($"Benachrichtigungen: {ex.Message}");
            }
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Merkt sich gesehene Benachrichtigungen, damit Ereignis und Abfrage dieselbe nur einmal zeigen.
/// Entfernte vergisst er erst ab <see cref="Capacity"/>: Eine per Ereignis gemeldete kann in der Abfrage noch fehlen.
/// </summary>
public sealed class NotificationTracker
{
    public const int Capacity = 1000;

    private readonly HashSet<uint> _seen = [];
    private readonly Lock _gate = new();

    public void Seed(IEnumerable<uint> ids)
    {
        lock (_gate)
        {
            _seen.UnionWith(ids);
        }
    }

    /// <summary><c>true</c>, wenn die Benachrichtigung neu ist.</summary>
    public bool Add(uint id)
    {
        lock (_gate)
        {
            return _seen.Add(id);
        }
    }

    /// <summary>Gleicht mit dem Info-Center ab und gibt die neuen zurück.</summary>
    public IReadOnlyList<uint> Update(IEnumerable<uint> current)
    {
        var ids = current.ToHashSet();
        lock (_gate)
        {
            var fresh = ids.Where(_seen.Add).ToList();
            if (_seen.Count > Capacity)
            {
                _seen.IntersectWith(ids);
            }
            return fresh;
        }
    }
}
