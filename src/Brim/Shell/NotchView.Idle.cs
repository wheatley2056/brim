using System.Numerics;
using Brim.Core;
using Brim.Core.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Brim.Shell;

/// <summary>
/// Ruhende Notch: die Uhr mittig, links oder rechts, daneben Werte aus den Einstellungen (CPU/RAM, Netzwerk,
/// Akku, Datum) und der blaue Punkt für Benachrichtigungen. Die Notch wird so breit, dass alles hineinpasst
/// (<see cref="IdleLayout"/>). Jeder Wert behält seine Breite, bis er eine Weile schmaler war, damit die Notch
/// nicht bei jeder Messung atmet.
/// </summary>
public sealed partial class NotchView
{
    private readonly StackPanel _idleLeft = new() { Orientation = Orientation.Horizontal, Spacing = IdleLayout.Spacing, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _idleRight = new() { Orientation = Orientation.Horizontal, Spacing = IdleLayout.Spacing, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, (WidgetRenderer Renderer, FrameworkElement? Element)> _statusSlots = new();
    private IdleArrangement _idle = new(NotchGeometry.Idle.Width, 0, [], []);
    private SettingsStore? _displaySettings;
    private readonly StableWidths _statusWidths = new(TimeSpan.FromSeconds(8));

    private void InitializeIdle(SettingsStore settings)
    {
        _displaySettings = settings;
        IdleLayer.Children.Add(_idleLeft);
        IdleLayer.Children.Add(_idleRight);
    }

    /// <summary>Statuswerte zeichnen, messen und verteilen; setzt die Breite der ruhenden Notch.</summary>
    private void ArrangeIdle()
    {
        var display = _displaySettings?.Current.Display ?? new NotchDisplaySettings();
        var now = DateTimeOffset.UtcNow;
        var shown = new List<(string Key, FrameworkElement Element, IdleItem Item)>();
        foreach (var key in display.EnabledStatus())
        {
            if (_hub.Status(key) is not { } widget)
            {
                continue;
            }
            if (!_statusSlots.TryGetValue(key, out var slot))
            {
                var moduleId = key.Split('/', 2)[0];
                slot = (new WidgetRenderer((action, value) => _hub.Invoke(new ActionInvocation(moduleId, action, value))), null);
            }
            var element = slot.Renderer.Render(widget, slot.Element);
            _statusSlots[key] = (slot.Renderer, element);
            // Natürliche Breite messen, dann die gleichbleibende setzen; Zahlen stehen rechtsbündig darin.
            element.Width = double.NaN;
            element.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = _statusWidths.Get(key, element.DesiredSize.Width, now);
            element.Width = width;
            if (element is TextBlock text)
            {
                text.TextAlignment = TextAlignment.Right;
            }
            else
            {
                element.HorizontalAlignment = HorizontalAlignment.Right;
            }
            var (side, priority) = IdleLayout.Slot(key);
            shown.Add((key, element, new IdleItem(width, side, priority)));
        }
        _statusWidths.Retain(shown.Select(s => s.Key));

        SharedClock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var clockWidth = Math.Max(1, SharedClock.DesiredSize.Width);
        var arrangement = IdleLayout.Arrange(display.Clock, clockWidth, shown.Select(s => s.Item).ToList(), display.CenterContent);
        _idle = arrangement;

        Place(_idleLeft, arrangement.Left.Select(i => shown[i].Element).Reverse());
        Place(_idleRight, arrangement.Right.Select(i => shown[i].Element));
        IdleLayer.Width = arrangement.Width;
        _idleLeft.Margin = new Thickness(0, 0, arrangement.Width - arrangement.ClockX + IdleLayout.Spacing, 0);
        _idleRight.Margin = new Thickness(arrangement.ClockX + clockWidth + IdleLayout.Spacing, 0, 0, 0);
        ElementCompositionPreview.GetElementVisual(IdleLayer).CenterPoint = new Vector3((float)arrangement.Width / 2, 0, 0);
    }

    private void Place(StackPanel panel, IEnumerable<FrameworkElement> elements)
    {
        var list = elements.ToList();
        if (list.SequenceEqual(panel.Children.OfType<FrameworkElement>()))
        {
            return;
        }
        panel.Children.Clear();
        foreach (var element in list)
        {
            // Ein Element kann von links nach rechts wandern (Uhr umgestellt); Parent taugt dafür nicht immer.
            _idleLeft.Children.Remove(element);
            _idleRight.Children.Remove(element);
            panel.Children.Add(element);
        }
    }

    /// <summary>Linke Kante der Uhr relativ zur Notch (für NotchView.Clock.cs).</summary>
    private double IdleClockX => _idle.ClockX;

    /// <summary>Fläche für Maus und Fensterregion, auch für die ruhende Notch mit Werten.</summary>
    public NotchShape HitShape(NotchMode mode)
    {
        var shape = BaseShape(mode);
        return NotchGeometry.SwellsOnHover(mode) ? NotchGeometry.Swell(shape) : TargetShape;
    }
}
