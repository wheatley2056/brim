using Brim.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Brim.Shell;

/// <summary>
/// Einstellungen in der Notch: Das Zahnrad neben den Tabs vergrößert die Notch, bis die ganze Einstellungsseite
/// hineinpasst. Solange sie offen ist, bleibt die Notch aufgeklappt und nimmt Tastatureingaben an.
/// </summary>
public sealed partial class NotchView
{
    private Grid? _settingsLayer;
    private Border? _settingsHost;
    private Func<nint>? _window;
    private bool _settingsOpen;

    /// <summary>Einstellungen auf oder zu: Das Fenster muss dann Tastatur annehmen und offen bleiben.</summary>
    public event Action<bool>? SettingsOpenChanged;

    public bool IsSettingsOpen => _settingsOpen;

    private (double Width, double Height) _available = (double.MaxValue, double.MaxValue);

    /// <summary>
    /// Einstellungen und Einführung: so groß wie das Einstellungsfenster, auf kleinen Monitoren so groß, wie
    /// der Bildschirm (in DIP, ohne Ränder) zulässt.
    /// </summary>
    private NotchShape LargeShape => NotchGeometry.Settings with
    {
        Width = Math.Min(NotchGeometry.Settings.Width, _available.Width - 48),
        Height = Math.Min(NotchGeometry.Settings.Height, _available.Height - 48),
    };

    /// <summary>Platz auf dem Monitor in DIP der Zeichenfläche (vom Fenster beim Platzieren).</summary>
    public void SetAvailableSize(double width, double height)
    {
        _available = (width, height);
        var shape = LargeShape;
        foreach (var layer in new[] { _settingsLayer, _onboardingLayer })
        {
            if (layer is not null)
            {
                layer.Width = shape.Width;
                layer.Height = shape.Height;
            }
        }
    }

    private void InitializeSettings(Func<nint> window)
    {
        _window = window;

        // Zahnrad rechts neben der Tab-Leiste.
        var track = HeaderRow.Children.OfType<Border>().First();
        HeaderRow.Children.Remove(track);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(track);
        var gear = new Button
        {
            Style = (Style)Application.Current.Resources["NotchTabStyle"],
            Content = new FontIcon { Glyph = Brim.Core.Widgets.Glyphs.Settings, FontSize = 13 },
            Padding = new Thickness(8, 0, 8, 0),
            Background = (Brush)Application.Current.Resources["TabTrackBrush"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gear, "Einstellungen");
        ToolTipService.SetToolTip(gear, "Einstellungen");
        gear.Click += (_, _) => SetSettingsOpen(true);
        row.Children.Add(gear);
        HeaderRow.Children.Insert(0, row);

        var (width, height) = (LargeShape.Width, LargeShape.Height);
        _settingsLayer = new Grid
        {
            Width = width,
            Height = height,
            Padding = new Thickness(20, 12, 20, 18),
            RowSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            // Standard-Steuerelemente in dunkel, passend zur schwarzen Notch.
            RequestedTheme = ElementTheme.Dark,
        };
        _settingsLayer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _settingsLayer.RowDefinitions.Add(new RowDefinition());
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = "Einstellungen", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 11 },
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Einstellungen schließen");
        close.Click += (_, _) => SetSettingsOpen(false);
        header.Children.Add(close);
        _settingsLayer.Children.Add(header);
        _settingsHost = new Border { CornerRadius = new CornerRadius(14), Background = Brushes.Get(BrimColors.Graphite) };
        Grid.SetRow(_settingsHost, 1);
        _settingsLayer.Children.Add(_settingsHost);
        _settingsLayer.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                SetSettingsOpen(false);
                e.Handled = true;
            }
        };
        ContentHost.Children.Add(_settingsLayer);
        _layers = [.. _layers, _settingsLayer];
    }

    public void SetSettingsOpen(bool open)
    {
        if (_settingsOpen == open)
        {
            return;
        }
        _settingsOpen = open;
        if (open && _settingsHost is { Child: null } && SettingsPage.Create is { } create && _window is { } window)
        {
            // Erst beim ersten Öffnen bauen; danach bleibt die Seite (mit ihrer Auswahl) erhalten.
            _settingsHost.Child = create(window);
        }
        _board?.SetEditing(false);
        SettingsOpenChanged?.Invoke(open);
    }
}
