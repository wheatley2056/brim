using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules.Sensors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace Brim.Shell;

/// <summary>
/// Statussymbole oben rechts in der offenen Notch, links neben der Uhr, wie im Infobereich der Taskleiste:
/// Netzwerk (WLAN oder Kabel), Akku, der Mond bei „Nicht stören“ und das Datum. Gelesen wird beim Aufklappen.
/// </summary>
public sealed partial class NotchView
{
    /// <summary>Abstand zwischen Symbolen und Uhr.</summary>
    private const double HeaderStatusGap = 12;

    private StackPanel? _headerStatus;

    private StackPanel HeaderStatus()
    {
        if (_headerStatus is null)
        {
            _headerStatus = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            AutomationProperties.SetName(_headerStatus, "Status");
            HeaderRow.Children.Add(_headerStatus);
        }
        return _headerStatus;
    }

    /// <param name="clockWidth">Breite der Uhr in der Kopfzeile; null blendet die Symbole aus.</param>
    private void UpdateHeaderStatus(double? clockWidth, bool refresh)
    {
        var panel = HeaderStatus();
        if (clockWidth is not { } width)
        {
            panel.Visibility = Visibility.Collapsed;
            return;
        }
        panel.Margin = new Thickness(0, 0, width + HeaderStatusGap, 0);
        if (refresh || panel.Visibility != Visibility.Visible)
        {
            panel.Children.Clear();
            foreach (var (glyph, name) in ReadHeaderStatus())
            {
                var icon = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 16,
                    FontFamily = (FontFamily)Application.Current.Resources["NotchIconFont"],
                    Foreground = Brushes.Get(BrimColors.Fog),
                };
                AutomationProperties.SetName(icon, name);
                panel.Children.Add(icon);
            }
            var date = new TextBlock
            {
                Text = HeaderDate(DateTime.Now),
                FontSize = 12,
                Foreground = Brushes.Get(BrimColors.Slate),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(date, FontNumeralAlignment.Tabular);
            panel.Children.Add(date);
        }
        panel.Visibility = panel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>„Di. 9. Nov“ wie im Design; andere Sprachen in ihrer kurzen Form.</summary>
    private static string HeaderDate(DateTime now)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        return culture.TwoLetterISOLanguageName == "de"
            ? $"{now.ToString("ddd", culture).TrimEnd('.')}. {now.Day}. {now.ToString("MMM", culture).TrimEnd('.')}"
            : now.ToString("ddd d MMM", culture);
    }

    private static List<(string Glyph, string Name)> ReadHeaderStatus()
    {
        var items = new List<(string, string)>();
        try
        {
            if (NetworkInformation.GetInternetConnectionProfile() is { } profile)
            {
                items.Add(profile.IsWlanConnectionProfile
                    ? (Glyphs.Wifi, "WLAN verbunden")
                    : (Glyphs.Ethernet, "Ethernet verbunden"));
            }
            else
            {
                items.Add((Glyphs.NoNetwork, "Kein Internet"));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Kopfzeile: Netzwerk nicht lesbar", ex);
        }
        try
        {
            if (PowerManager.BatteryStatus != BatteryStatus.NotPresent)
            {
                var percent = Math.Clamp(PowerManager.RemainingChargePercent, 0, 100);
                var charging = PowerManager.BatteryStatus == BatteryStatus.Charging;
                var saver = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
                items.Add((Glyphs.Battery(percent, charging, saver), $"Akku {percent} %{(charging ? ", lädt" : "")}"));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Kopfzeile: Akku nicht lesbar", ex);
        }
        if (QuietHours.IsDoNotDisturb())
        {
            items.Add((Glyphs.QuietHours, "Nicht stören ist an"));
        }
        return items;
    }
}
