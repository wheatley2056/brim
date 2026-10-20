using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Brim.Shell;

/// <summary>Einstellungen als eigenes Fenster (Tray-Menü), mit Mica; der Inhalt ist die <see cref="SettingsPage"/>.</summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    /// <summary>Öffnet das Fenster oder holt das offene nach vorn.</summary>
    public static void Show()
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        if (SettingsPage.Create is not { } create)
        {
            return;
        }
        _open = new SettingsWindow(create);
        _open.Closed += (_, _) => _open = null;
        _open.Activate();
    }

    private SettingsWindow(Func<Func<nint>, SettingsPage> create)
    {
        Title = "Brim – Einstellungen";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(980, 720));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Brim.ico"));
        Content = create(() => WinRT.Interop.WindowNative.GetWindowHandle(this));
    }
}
