using System.Numerics;
using Brim.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Brim.Shell;

/// <summary>
/// Segmentierte Tabs: Die Auswahl ist eine Pille im Compositor, die mit einer Feder
/// zum gewählten Tab gleitet und dabei ihre Breite anpasst – ohne verzerrte Ecken,
/// weil Position und Größe der Geometrie animiert werden, nicht ihre Skalierung.
/// </summary>
public sealed partial class NotchView
{
    private static readonly Spring TabSpring = new(0.34, 0.78);
    private const float TabHeight = 24;

    private CompositionRoundedRectangleGeometry? _tabGeometry;
    private readonly SpringChannel _tabX = new(0);
    private readonly SpringChannel _tabWidth = new(0);
    private NotchTab? _indicatorTab;

    private void InitializeTabs(Compositor compositor)
    {
        var visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(600, TabHeight);
        _tabGeometry = compositor.CreateRoundedRectangleGeometry();
        _tabGeometry.CornerRadius = new Vector2(TabHeight / 2);
        _tabGeometry.Size = new Vector2(0, TabHeight);
        var shape = compositor.CreateSpriteShape(_tabGeometry);
        shape.FillBrush = compositor.CreateColorBrush(Brushes.Parse(BrimColors.Track));
        visual.Shapes.Add(shape);
        ElementCompositionPreview.SetElementChildVisual(TabIndicatorHost, visual);

        // Erst nach dem Layout kennt man die Breiten; dann ohne Animation an die richtige Stelle.
        TabStrip.SizeChanged += (_, _) => PlaceTabIndicator(animate: false);
    }

    private Button TabButton(NotchTab tab) => tab switch
    {
        NotchTab.Shelf => TabShelf,
        NotchTab.System => TabSystem,
        NotchTab.Plugins => TabPlugins,
        _ => TabNow,
    };

    private void UpdateTabs(NotchTab selected)
    {
        foreach (var tab in Enum.GetValues<NotchTab>())
        {
            var button = TabButton(tab);
            var isSelected = tab == selected;
            button.Foreground = Brushes.Get(isSelected ? BrimColors.Fog : BrimColors.Slate);
            AutomationProperties.SetItemStatus(button, isSelected ? "ausgewählt" : "");
        }
        if (_indicatorTab != selected)
        {
            // Beim Wechsel im offenen Zustand gleiten, beim Aufklappen einfach dort sein.
            var animate = _indicatorTab is not null && ExpandedLayer.Visibility == Visibility.Visible && !_reducedMotion;
            _indicatorTab = selected;
            PlaceTabIndicator(animate);
        }
    }

    private void PlaceTabIndicator(bool animate)
    {
        if (_tabGeometry is null || _indicatorTab is not { } tab)
        {
            return;
        }
        var button = TabButton(tab);
        if (button.ActualWidth <= 0)
        {
            return;
        }
        var x = button.TransformToVisual(TabStrip).TransformPoint(default).X;
        var width = button.ActualWidth;
        var now = NotchSurface.Now;

        if (!animate)
        {
            _tabX.Set(x, now);
            _tabWidth.Set(width, now);
            _tabGeometry.StopAnimation("Offset");
            _tabGeometry.StopAnimation("Size");
            _tabGeometry.Offset = new Vector2((float)x, 0);
            _tabGeometry.Size = new Vector2((float)width, TabHeight);
            return;
        }

        var (fromX, vx) = _tabX.Retarget(x, TabSpring, now);
        var (fromW, vw) = _tabWidth.Retarget(width, TabSpring, now);
        var length = Math.Max(_tabX.Duration, _tabWidth.Duration);
        var compositor = _tabGeometry.Compositor;
        _tabGeometry.StartAnimation("Offset", NotchSurface.CreateCurve2(compositor, length,
            t => new Vector2((float)(t >= _tabX.Duration ? x : TabSpring.Evaluate(fromX, x, vx, t).Value), 0)));
        _tabGeometry.StartAnimation("Size", NotchSurface.CreateCurve2(compositor, length,
            t => new Vector2((float)(t >= _tabWidth.Duration ? width : TabSpring.Evaluate(fromW, width, vw, t).Value), TabHeight)));
    }
}
