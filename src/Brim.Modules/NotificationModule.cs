using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules.Sensors;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Brim.Modules;

/// <summary>Eine Benachrichtigung, wie das Modul sie zeigt.</summary>
public sealed record NotificationPreview(string App, string Title, string Body);

/// <summary>
/// Benachrichtigungen spiegeln (A3, Plan 4.3): Neue Windows-Benachrichtigungen erscheinen kurz als Vorschau in der
/// Notch. Braucht Paketidentität und die Zustimmung des Nutzers (Windows fragt beim ersten Start).
/// Apps aus <see cref="NotificationSettings.MutedApps"/> bleiben stumm.
/// </summary>
public sealed class NotificationModule : IBrimModule
{
    public const string ModuleId = "notifications";

    private ModuleContext? _context;
    private UserNotificationListener? _listener;

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
                return;
            }
            _listener.NotificationChanged += OnChanged;
            Log.Info("Benachrichtigungen werden gespiegelt");
        }
        catch (Exception ex)
        {
            Log.Warn("Benachrichtigungen nicht verfügbar", ex);
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
            var notification = sender.GetNotification(args.UserNotificationId);
            if (notification is null)
            {
                return;
            }
            var app = notification.AppInfo?.DisplayInfo?.DisplayName ?? "";
            var texts = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?
                .GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
            if (Preview(app, texts) is { } preview && Route?.Invoke(preview) != true)
            {
                Show(preview);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Benachrichtigung nicht lesbar", ex);
        }
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

    public void Show(NotificationPreview preview) => _context?.Post(new Activity
    {
        ModuleId = ModuleId,
        Id = "preview",
        Priority = ActivityPriority.Transient,
        Duration = TimeSpan.FromSeconds(5),
        Announcement = $"{preview.App}: {preview.Title}. {preview.Body}",
        Content = new StackWidget(
            new TextWidget(preview.App) { Style = TextStyle.Caption, FontSize = 11, FixedWidth = 70 },
            new TextWidget(preview.Title) { FontSize = 12 },
            new TextWidget(preview.Body) { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 })
        {
            Spacing = 8,
            Padding = (18, 0, 18, 0),
        },
    });

    public ValueTask DisposeAsync()
    {
        if (_listener is not null)
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
