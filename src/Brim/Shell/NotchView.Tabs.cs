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

    /// <summary>Strich unter dem aktiven Tab in der Akzentfarbe, wie in Windows 11 (Breite und Höhe in DIP).</summary>
    private const float MarkWidth = 16, MarkHeight = 3;

    private CompositionRoundedRectangleGeometry? _tabGeometry;
    private CompositionRoundedRectangleGeometry? _tabMark;
    private CompositionColorBrush? _tabFill;
    private CompositionColorBrush? _tabMarkFill;
    private readonly SpringChannel _tabX = new(0);
    private readonly SpringChannel _tabWidth = new(0);
    private NotchTab? _indicatorTab;

    private void InitializeTabs(Compositor compositor)
    {
        var visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(600, TabHeight);
        _tabGeometry = compositor.CreateRoundedRectangleGeometry();
        // Segmente wie in Windows 11: 4 DIP Radius statt Pille.
        _tabGeometry.CornerRadius = new Vector2(4);
        _tabGeometry.Size = new Vector2(0, TabHeight);
        _tabFill = compositor.CreateColorBrush(Brushes.Parse(Brushes.Resolve(BrimColors.Track)));
        var shape = compositor.CreateSpriteShape(_tabGeometry);
        shape.FillBrush = _tabFill;
        visual.Shapes.Add(shape);
        _tabMark = compositor.CreateRoundedRectangleGeometry();
        _tabMark.CornerRadius = new Vector2(MarkHeight / 2);
        _tabMark.Size = new Vector2(MarkWidth, MarkHeight);
        _tabMarkFill = compositor.CreateColorBrush(Brushes.Parse(Brushes.Resolve(BrimColors.DefaultAccent)));
        var mark = compositor.CreateSpriteShape(_tabMark);
        mark.FillBrush = _tabMarkFill;
        visual.Shapes.Add(mark);
        ElementCompositionPreview.SetElementChildVisual(TabIndicatorHost, visual);

        // Erst nach dem Layout kennt man die Breiten; dann ohne Animation an die richtige Stelle.
        TabStrip.SizeChanged += (_, _) => PlaceTabIndicator(animate: false);
    }

    /// <summary>Hell/dunkel oder Akzentfarbe geändert: Auswahl und Strich neu einfärben.</summary>
    private void ApplyTabColors()
    {
        if (_tabFill is not null)
        {
            // Hell: weißes Segment auf grauer Spur, wie die Segmente in Windows 11.
            _tabFill.Color = Brushes.Parse(Brushes.IsLight ? "#FFFFFF" : "#14FFFFFF");
        }
        // Spur: dunkel 25 % Schwarz, hell 5 % Schwarz (Design „Windows-11-nativ“).
        TabTrack.Background = Brushes.Get("#40000000|#0D000000");
        if (_tabMarkFill is not null)
        {
            _tabMarkFill.Color = Brushes.Parse(Brushes.Resolve(BrimColors.DefaultAccent));
        }
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

    private static Vector2 MarkOffset(double x, double width) => new((float)(x + (width - MarkWidth) / 2), TabHeight - MarkHeight);

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
            if (_tabMark is not null)
            {
                _tabMark.StopAnimation("Offset");
                _tabMark.Offset = MarkOffset(x, width);
            }
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
        _tabMark?.StartAnimation("Offset", NotchSurface.CreateCurve2(compositor, length, t => MarkOffset(
            t >= _tabX.Duration ? x : TabSpring.Evaluate(fromX, x, vx, t).Value,
            t >= _tabWidth.Duration ? width : TabSpring.Evaluate(fromW, width, vw, t).Value)));
    }
}
