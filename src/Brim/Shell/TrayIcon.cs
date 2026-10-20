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
        _icon.Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "Brim.ico"));
        _icon.ForceCreate(enablesEfficiencyMode: false);
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
