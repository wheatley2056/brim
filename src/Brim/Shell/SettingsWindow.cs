using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace Brim.Shell;

/// <summary>
/// Einstellungen als eigenes Fenster (Tray-Menü): Mica bis in die Titelleiste wie die Windows-Einstellungen, hell
/// oder dunkel wie das System. Der Inhalt ist die <see cref="SettingsPage"/>.
/// </summary>
internal sealed class SettingsWindow : Window
{
    /// <summary>Größe und Mindestgröße in DIP; auf dem Monitor mit seiner Skalierung multipliziert.</summary>
    private const int DefaultWidth = 1000, DefaultHeight = 720, MinWidth = 640, MinHeight = 480;

    private static SettingsWindow? _open;

    private SettingsPage? _page;

    /// <summary>Öffnet das Fenster oder holt das offene nach vorn, auf Wunsch gleich mit einem Bereich (z. B. „plugins“).</summary>
    public static void Show(string? section = null)
    {
        if (_open is null)
        {
            if (SettingsPage.Create is not { } create)
            {
                return;
            }
            _open = new SettingsWindow(create);
            _open.Closed += (_, _) => _open = null;
        }
        if (section is not null)
        {
            _open._page?.Select(section);
        }
        _open.Activate();
    }

    private SettingsWindow(Func<Func<nint>, SettingsPage> create)
    {
        Title = "Brim – Einstellungen";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Brim.ico"));

        // Eigene Titelleiste im Inhalt: Mica läuft durch, statt dass oben ein heller Balken auf dunklem Inhalt sitzt.
        ExtendsContentIntoTitleBar = true;
        var titleBar = new Grid { Height = 40, Padding = new Thickness(16, 0, 0, 0) };
        titleBar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Image
                {
                    Width = 16,
                    Height = 16,
                    Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Brim.png"))),
                },
                new TextBlock { Text = Title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        var page = create(() => WinRT.Interop.WindowNative.GetWindowHandle(this));
        _page = page;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(page, 1);
        root.Children.Add(titleBar);
        root.Children.Add(page);
        Content = root;
        SetTitleBar(titleBar);

        // Schaltflächen oben rechts zeichnet Windows; ihre Farben folgen hier dem Design des Inhalts.
        root.Loaded += (_, _) => ApplyCaptionColors(root.ActualTheme);
        root.ActualThemeChanged += (sender, _) => ApplyCaptionColors(sender.ActualTheme);

        PlaceOnScreen();
    }

    /// <summary>Größe in DIP auf die Skalierung des Monitors umrechnen (AppWindow rechnet in Pixeln) und mittig setzen.</summary>
    private void PlaceOnScreen()
    {
        var scale = Win32.GetDpi(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinHeight * scale);
        }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(DefaultWidth * scale), area.Width);
        var height = Math.Min((int)(DefaultHeight * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    private void ApplyCaptionColors(ElementTheme theme)
    {
        var bar = AppWindow.TitleBar;
        var dark = theme == ElementTheme.Dark;
        var foreground = dark ? Colors.White : Colors.Black;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Windows.UI.Color.FromArgb(0xFF, 0x70, 0x70, 0x70);
        bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x18, foreground.R, foreground.G, foreground.B);
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x10, foreground.R, foreground.G, foreground.B);
        bar.ButtonPressedForegroundColor = foreground;
    }
}
