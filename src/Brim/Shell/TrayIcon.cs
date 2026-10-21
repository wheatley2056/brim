using System.Diagnostics;
using Brim.Core;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;

namespace Brim.Shell;

/// <summary>Symbol im Infobereich mit Menü (H.NotifyIcon).</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private Action? _onNotificationClick;

    public TrayIcon(SettingsStore settings, Action exit, Action toggle, Action openSettings)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Notch aufklappen (Win+Alt+N)", Command = new RelayCommand(toggle) });
        menu.Items.Add(new MenuFlyoutItem { Text = "Einstellungen", Command = new RelayCommand(openSettings) });
        menu.Items.Add(new MenuFlyoutItem { Text = "Protokolle", Command = new RelayCommand(() => Open(Path.Combine(App.DataDirectory, "logs"))) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Brim beenden", Command = new RelayCommand(exit) });

        _icon = new TaskbarIcon
        {
            ToolTipText = "Brim",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(toggle),
            NoLeftClickDelay = true,
        };
        // Die passende Größe aus der .ico wählen: 16 und 24 px sind eigens vereinfacht und bleiben so scharf.
        var size = Math.Max(16, Win32.SmallIconSize);
        _icon.Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "Brim.ico"), size, size);
        _icon.ForceCreate(enablesEfficiencyMode: false);
        // Klick auf die Benachrichtigung; kommt vom Nachrichtenfenster, daher zurück in den UI-Thread.
        _icon.TrayIcon.MessageWindow.MouseEventReceived += (_, e) =>
        {
            if (e.MouseEvent == MouseEvent.BalloonToolTipClicked && _onNotificationClick is { } click)
            {
                _onNotificationClick = null;
                _icon.DispatcherQueue.TryEnqueue(() => click());
            }
        };
    }

    /// <summary>Windows-Benachrichtigung vom Symbol im Infobereich; <paramref name="onClick"/> beim Klick darauf.</summary>
    public void Notify(string title, string message, Action? onClick = null)
    {
        try
        {
            _onNotificationClick = onClick;
            _icon.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            Log.Warn("Benachrichtigung ließ sich nicht zeigen", ex);
        }
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Konnte {path} nicht öffnen", ex);
        }
    }

    public void Dispose() => _icon.Dispose();
}
