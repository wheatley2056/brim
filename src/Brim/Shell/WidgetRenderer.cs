using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Brim.Core;
using Brim.Core.Widgets;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Streams;
using Windows.UI;

namespace Brim.Shell;

/// <summary>
/// Zeichnet deklarative Widgets (Plan 4.2) als XAML. Bei Aktualisierungen werden vorhandene
/// Elemente gleicher Art wiederverwendet, damit z. B. der Fortschritt jede Sekunde
/// ohne Neuaufbau und ohne erneutes Dekodieren des Covers weiterläuft.
/// </summary>
internal sealed class WidgetRenderer
{
    private readonly Action<string, double?> _invoke;

    /// <param name="invoke">Aktion an das Modul: Aktions-ID und optional ein Wert (0..1).</param>
    public WidgetRenderer(Action<string, double?> invoke)
    {
        _invoke = invoke;
    }

    public FrameworkElement Render(Widget widget, FrameworkElement? existing)
    {
        var element = widget switch
        {
            TextWidget w => (FrameworkElement)RenderText(w, existing as TextBlock),
            IconWidget w => RenderIcon(w, existing as FontIcon),
            ImageWidget w => RenderImage(w, existing as ImageView),
            ProgressWidget w => RenderProgress(w, existing as ProgressView),
            RingWidget w => RenderRing(w, existing as RingView),
            LevelWidget w => RenderLevel(w, existing as LevelView),
            ButtonWidget w => RenderButton(w, existing as WidgetButton),
            SwitchWidget w => RenderSwitch(w, existing as ToggleSwitch),
            StackWidget w => RenderStack(w, existing as StackView),
            ListWidget w => RenderList(w, existing as ListView2),
            SpacerWidget w => RenderSpacer(w, existing as SpacerView),
            _ => new TextBlock { Text = $"[{widget.GetType().Name}]" },
        };
        if (widget.AccessibleName is { } name)
        {
            AutomationProperties.SetName(element, name);
        }
        if (widget.FixedWidth is { } width)
        {
            element.Width = width;
        }
        return element;
    }

    // ───────── Text, Symbol ─────────

    private static TextBlock RenderText(TextWidget w, TextBlock? tb)
    {
        tb ??= new TextBlock { TextLineBounds = TextLineBounds.Full };
        tb.Text = w.Text;
        tb.TextTrimming = w.Ellipsis && !w.Wrap ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        tb.TextWrapping = w.Wrap ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap;
        tb.MaxLines = w.Wrap ? 0 : 1;

        var (font, size, weight, color) = w.Style switch
        {
            TextStyle.Title => ("NotchTextFont", 18d, FontWeights.SemiBold, BrimColors.Fog),
            TextStyle.Secondary => ("NotchTextFont", 13d, FontWeights.Normal, BrimColors.Slate),
            TextStyle.Caption => ("NotchTextFont", 11d, FontWeights.Normal, BrimColors.Slate),
            TextStyle.Mono => ("NotchMonoFont", 13d, FontWeights.Normal, BrimColors.Fog),
            TextStyle.MonoCaption => ("NotchMonoFont", 11d, FontWeights.Normal, BrimColors.Slate),
            TextStyle.Display => ("NotchDisplayFont", 44d, FontWeights.SemiBold, BrimColors.Fog),
            _ => ("NotchTextFont", 13d, FontWeights.Normal, BrimColors.Fog),
        };
        tb.FontFamily = (FontFamily)Application.Current.Resources[font];
        tb.FontSize = w.FontSize ?? size;
        tb.FontWeight = weight;
        tb.CharacterSpacing = w.Style is TextStyle.Title or TextStyle.Display ? -20 : 0;
        tb.Foreground = Brushes.Get(w.Color ?? color);
        return tb;
    }

    private static FontIcon RenderIcon(IconWidget w, FontIcon? icon)
    {
        icon ??= new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["NotchIconFont"] };
        icon.Glyph = w.Glyph;
        icon.FontSize = w.Size;
        icon.Foreground = Brushes.Get(w.Color ?? BrimColors.Fog);
        return icon;
    }

    // ───────── Bild ─────────

    private static ImageView RenderImage(ImageWidget w, ImageView? view)
    {
        view ??= new ImageView();
        view.Update(w);
        return view;
    }

    private sealed class ImageView : Grid
    {
        private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
        private string? _imageId;

        public ImageView()
        {
            Children.Add(_image);
        }

        public void Update(ImageWidget w)
        {
            Width = w.Width;
            Height = w.Height;
            CornerRadius = new CornerRadius(w.CornerRadius);
            if (w.Placeholder is { } p)
            {
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops = { new GradientStop { Color = Brushes.Parse(p.From), Offset = 0 }, new GradientStop { Color = Brushes.Parse(p.To), Offset = 1 } },
                };
            }
            if (w.Image?.Id == _imageId)
            {
                return;
            }
            _imageId = w.Image?.Id;
            if (w.Image is null)
            {
                _image.Source = null;
                return;
            }
            _ = LoadAsync(w.Image, w.Width, w.Height);
        }

        private async Task LoadAsync(ImageData data, double width, double height)
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(data.Bytes.AsBuffer());
                stream.Seek(0);
                var scale = XamlRoot?.RasterizationScale ?? 1.5;
                var bitmap = new BitmapImage { DecodePixelWidth = (int)Math.Ceiling(Math.Max(width, height) * scale), DecodePixelType = DecodePixelType.Physical };
                await bitmap.SetSourceAsync(stream);
                if (_imageId == data.Id)
                {
                    _image.Source = bitmap;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Bild konnte nicht dekodiert werden", ex);
            }
        }
    }

    // ───────── Fortschritt ─────────

    private ProgressView RenderProgress(ProgressWidget w, ProgressView? view)
    {
        view ??= new ProgressView(_invoke);
        view.Update(w);
        return view;
    }

    private sealed class ProgressView : Grid
    {
        private readonly Border _track = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _fill = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly Action<string, double?> _invoke;
        private string? _seekAction;
        private double _value;

        private bool _dragging;
        private bool _live = true;

        /// <summary>
        /// Mit <see cref="ProgressWidget.SeekActionId"/> wird die Leiste zum Regler: klicken, ziehen
        /// oder mit den Pfeiltasten bewegen. Während des Ziehens folgt die Füllung sofort dem Zeiger,
        /// unabhängig davon, wann das Modul den neuen Wert bestätigt.
        /// </summary>
        public ProgressView(Action<string, double?> invoke)
        {
            _invoke = invoke;
            Background = new SolidColorBrush(Colors.Transparent);
            Children.Add(_track);
            Children.Add(_fill);
            SizeChanged += (_, _) => Layout();
            PointerPressed += (_, e) =>
            {
                if (_seekAction is null || ActualWidth <= 0)
                {
                    return;
                }
                _dragging = CapturePointer(e.Pointer);
                Seek(e.GetCurrentPoint(this).Position.X, final: !_dragging);
                e.Handled = true;
            };
            PointerMoved += (_, e) =>
            {
                if (_dragging)
                {
                    Seek(e.GetCurrentPoint(this).Position.X, final: false);
                }
            };
            PointerReleased += (_, e) =>
            {
                if (_dragging)
                {
                    Seek(e.GetCurrentPoint(this).Position.X, final: true);
                }
                _dragging = false;
                ReleasePointerCapture(e.Pointer);
            };
            PointerCaptureLost += (_, _) => _dragging = false;
            KeyDown += (_, e) =>
            {
                if (_seekAction is null)
                {
                    return;
                }
                var step = e.Key switch
                {
                    Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Down => -0.05,
                    Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Up => 0.05,
                    _ => 0,
                };
                if (step != 0)
                {
                    SetLocal(_value + step);
                    _invoke(_seekAction, _value);
                    e.Handled = true;
                }
            };
        }

        private void Seek(double x, bool final)
        {
            SetLocal(x / ActualWidth);
            if (_live || final)
            {
                _invoke(_seekAction!, _value);
            }
        }

        private void SetLocal(double value)
        {
            _value = Math.Clamp(value, 0, 1);
            Layout();
        }

        public void Update(ProgressWidget w)
        {
            // Beim Ziehen gewinnt der Zeiger; sonst springt die Füllung zwischen Zeiger und Rückmeldung.
            if (!_dragging)
            {
                _value = Math.Clamp(w.Value, 0, 1);
            }
            _seekAction = w.SeekActionId;
            _live = w.Live;
            IsTabStop = w.SeekActionId is not null;
            Height = Math.Max(w.Thickness, w.SeekActionId is null ? w.Thickness : 16);
            _track.Height = _fill.Height = w.Thickness;
            _track.CornerRadius = _fill.CornerRadius = new CornerRadius(w.Thickness / 2);
            _track.Background = Brushes.Get(BrimColors.Track);
            _fill.Background = Brushes.Get(w.Color ?? BrimColors.Fog);
            Layout();
        }

        private void Layout()
        {
            _fill.Width = ActualWidth * _value;
        }
    }

    // ───────── Ring ─────────

    private static RingView RenderRing(RingWidget w, RingView? view)
    {
        view ??= new RingView();
        view.Update(w);
        return view;
    }

    private sealed class RingView : Grid
    {
        private readonly Ellipse _track = new();
        private readonly Microsoft.UI.Xaml.Shapes.Path _arc = new() { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };

        public RingView()
        {
            Children.Add(_track);
            Children.Add(_arc);
        }

        public void Update(RingWidget w)
        {
            Width = Height = w.Size;
            var color = w.Color ?? BrimColors.Warning;
            _track.Stroke = Brushes.Get(w.TrackColor ?? "#3A3226");
            _track.StrokeThickness = _arc.StrokeThickness = w.Thickness;
            _track.Margin = new Thickness(w.Thickness / 2);
            _arc.Stroke = Brushes.Get(color);

            var value = Math.Clamp(w.Value, 0, 0.9999);
            var r = (w.Size - w.Thickness) / 2;
            var c = w.Size / 2;
            var angle = value * 2 * Math.PI;
            var end = new Windows.Foundation.Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
            var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(c, c - r), IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Windows.Foundation.Size(r, r),
                IsLargeArc = value > 0.5,
                SweepDirection = SweepDirection.Clockwise,
            });
            _arc.Data = new PathGeometry { Figures = { figure } };
        }
    }

    // ───────── Pegel ─────────

    private static LevelView RenderLevel(LevelWidget w, LevelView? view)
    {
        view ??= new LevelView();
        view.Update(w);
        return view;
    }

    /// <summary>Fünf Balken, die im Compositor auf und ab laufen, solange Musik spielt.</summary>
    private sealed class LevelView : StackPanel
    {
        private static readonly double[] BaseHeights = [8, 16, 11, 18, 6];
        private static readonly float[] Periods = [0.62f, 0.48f, 0.71f, 0.53f, 0.66f];
        private readonly Rectangle[] _bars = new Rectangle[5];
        private bool? _active;
        private double _height;

        public LevelView()
        {
            Orientation = Orientation.Horizontal;
            Spacing = 3;
            VerticalAlignment = VerticalAlignment.Center;
            for (var i = 0; i < _bars.Length; i++)
            {
                _bars[i] = new Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5, VerticalAlignment = VerticalAlignment.Center };
                Children.Add(_bars[i]);
            }
        }

        public void Update(LevelWidget w)
        {
            var brush = Brushes.Get(w.Color ?? BrimColors.DefaultAccent);
            var factor = w.Height / 18;
            Height = w.Height;
            for (var i = 0; i < _bars.Length; i++)
            {
                _bars[i].Fill = brush;
                _bars[i].Height = BaseHeights[i] * factor;
            }
            if (_active == w.Active && _height == w.Height)
            {
                return;
            }
            _active = w.Active;
            _height = w.Height;
            for (var i = 0; i < _bars.Length; i++)
            {
                var visual = ElementCompositionPreview.GetElementVisual(_bars[i]);
                var h = (float)(BaseHeights[i] * factor);
                visual.CenterPoint = new Vector3(1.5f, h / 2, 0);
                var compositor = visual.Compositor;
                if (w.Active)
                {
                    var bounce = compositor.CreateVector3KeyFrameAnimation();
                    var low = new Vector3(1, 0.35f, 1);
                    var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.55f, 1));
                    bounce.InsertKeyFrame(0, low);
                    bounce.InsertKeyFrame(0.5f, Vector3.One, ease);
                    bounce.InsertKeyFrame(1, low, ease);
                    bounce.Duration = TimeSpan.FromSeconds(Periods[i] * 2);
                    bounce.IterationBehavior = AnimationIterationBehavior.Forever;
                    bounce.DelayTime = TimeSpan.FromMilliseconds(i * 70);
                    visual.StartAnimation("Scale", bounce);
                }
                else
                {
                    var settle = compositor.CreateVector3KeyFrameAnimation();
                    settle.InsertKeyFrame(1, new Vector3(1, 0.3f, 1));
                    settle.Duration = TimeSpan.FromMilliseconds(300);
                    visual.StartAnimation("Scale", settle);
                }
            }
        }
    }

    // ───────── Knöpfe ─────────

    private sealed class WidgetButton : Button
    {
        public string ActionId { get; set; } = "";
    }

    private WidgetButton RenderButton(ButtonWidget w, WidgetButton? button)
    {
        if (button is null)
        {
            button = new WidgetButton { Style = (Style)Application.Current.Resources["NotchButtonStyle"] };
            button.Click += (s, _) => _invoke(((WidgetButton)s).ActionId, null);
        }
        button.ActionId = w.ActionId;
        switch (w.Kind)
        {
            case ButtonKind.Primary:
                button.Width = button.Height = w.Size;
                button.CornerRadius = new CornerRadius(w.Size / 2);
                button.Background = Brushes.Get(BrimColors.Fog);
                button.Content = Glyph(w.Glyph, w.GlyphSize, w.GlyphColor ?? BrimColors.Obsidian);
                break;
            case ButtonKind.Row:
                button.Width = double.NaN;
                button.Height = 36;
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                button.Padding = new Thickness(12, 0, 12, 0);
                button.CornerRadius = new CornerRadius(10);
                button.Background = Brushes.Get(w.Selected ? BrimColors.Graphite : "#00000000");
                button.Content = RowContent(w);
                break;
            case ButtonKind.Secondary:
                button.Width = double.NaN;
                button.Height = 32;
                button.Padding = new Thickness(14, 0, 14, 0);
                button.CornerRadius = new CornerRadius(16);
                button.Background = Brushes.Get(BrimColors.Track);
                button.Content = new TextBlock { Text = w.Label ?? "", FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Brushes.Get(BrimColors.Fog) };
                break;
            default:
                button.Width = button.Height = w.Size;
                button.CornerRadius = new CornerRadius(w.Size / 2);
                button.Background = Brushes.Get("#00000000");
                button.Content = Glyph(w.Glyph, w.GlyphSize, w.GlyphColor ?? BrimColors.Fog);
                break;
        }
        AutomationProperties.SetName(button, w.AccessibleName ?? w.Label ?? w.ActionId);
        ToolTipService.SetToolTip(button, w.AccessibleName ?? w.Label);
        return button;
    }

    /// <summary>Symbol, Text, Haken – wie die Geräteliste in den Windows-Schnelleinstellungen.</summary>
    private static Grid RowContent(ButtonWidget w)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var color = w.Selected ? BrimColors.Fog : BrimColors.Slate;
        if (Glyph(w.Glyph, 16, color) is { } icon)
        {
            grid.Children.Add(icon);
        }
        var label = new TextBlock
        {
            Text = w.Label ?? "",
            FontSize = 13,
            FontWeight = w.Selected ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Brushes.Get(BrimColors.Fog),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (w.Selected && Glyph(Glyphs.CheckMark, 12, BrimColors.Fog) is { } check)
        {
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }
        return grid;
    }

    private static FontIcon? Glyph(string? glyph, double size, string color) => glyph is null
        ? null
        : new FontIcon
        {
            Glyph = glyph,
            FontSize = size,
            FontFamily = (FontFamily)Application.Current.Resources["NotchIconFont"],
            Foreground = Brushes.Get(color),
        };

    private ToggleSwitch RenderSwitch(SwitchWidget w, ToggleSwitch? toggle)
    {
        if (toggle is null)
        {
            toggle = new ToggleSwitch { OnContent = null, OffContent = null };
            toggle.Toggled += (s, _) =>
            {
                var t = (ToggleSwitch)s;
                _invoke((string)t.Tag, t.IsOn ? 1 : 0);
            };
        }
        toggle.Tag = w.ActionId;
        toggle.Header = w.Label;
        if (toggle.IsOn != w.IsOn)
        {
            toggle.IsOn = w.IsOn;
        }
        return toggle;
    }

    // ───────── Stapel, Liste, Abstand ─────────

    private sealed class StackView : Grid
    {
    }

    private StackView RenderStack(StackWidget w, StackView? grid)
    {
        grid ??= new StackView();
        var horizontal = w.Orientation == StackOrientation.Horizontal;
        grid.Padding = new Thickness(w.Padding.Left, w.Padding.Top, w.Padding.Right, w.Padding.Bottom);
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnSpacing = horizontal ? w.Spacing : 0;
        grid.RowSpacing = horizontal ? 0 : w.Spacing;

        // Spalten bzw. Zeilen: Kinder mit Grow teilen sich den Platz, Justify fügt Füllspalten ein.
        var anyGrow = w.Children.Any(c => c.Grow > 0);
        var tracks = new List<GridLength>();
        var childTrack = new int[w.Children.Count];
        void Filler() => tracks.Add(new GridLength(1, GridUnitType.Star));

        if (!anyGrow && w.Justify is Justify.Center or Justify.End)
        {
            Filler();
        }
        for (var i = 0; i < w.Children.Count; i++)
        {
            if (i > 0 && !anyGrow && w.Justify == Justify.SpaceBetween)
            {
                Filler();
            }
            childTrack[i] = tracks.Count;
            var grow = w.Children[i].Grow;
            tracks.Add(grow > 0 ? new GridLength(grow, GridUnitType.Star) : GridLength.Auto);
        }
        if (!anyGrow && w.Justify is Justify.Center or Justify.Start)
        {
            Filler();
        }

        foreach (var t in tracks)
        {
            if (horizontal)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = t });
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = t });
            }
        }

        // Kinder abgleichen: gleicher Platz, gleiche Art → wiederverwenden.
        for (var i = 0; i < w.Children.Count; i++)
        {
            var existing = i < grid.Children.Count ? grid.Children[i] as FrameworkElement : null;
            var element = Render(w.Children[i], existing);
            if (!ReferenceEquals(element, existing))
            {
                if (existing is not null)
                {
                    grid.Children[i] = element;
                }
                else
                {
                    grid.Children.Add(element);
                }
            }
            if (horizontal)
            {
                Grid.SetColumn(element, childTrack[i]);
                Grid.SetRow(element, 0);
                element.VerticalAlignment = Cross(w.Align);
                if (w.Children[i].Grow > 0)
                {
                    element.HorizontalAlignment = HorizontalAlignment.Stretch;
                }
            }
            else
            {
                Grid.SetRow(element, childTrack[i]);
                Grid.SetColumn(element, 0);
                element.HorizontalAlignment = CrossH(w.Align);
                if (w.Children[i].Grow > 0)
                {
                    element.VerticalAlignment = VerticalAlignment.Stretch;
                }
            }
        }
        while (grid.Children.Count > w.Children.Count)
        {
            grid.Children.RemoveAt(grid.Children.Count - 1);
        }
        return grid;
    }

    private static VerticalAlignment Cross(CrossAlign a) => a switch
    {
        CrossAlign.Start => VerticalAlignment.Top,
        CrossAlign.End => VerticalAlignment.Bottom,
        CrossAlign.Stretch => VerticalAlignment.Stretch,
        _ => VerticalAlignment.Center,
    };

    private static HorizontalAlignment CrossH(CrossAlign a) => a switch
    {
        CrossAlign.Start => HorizontalAlignment.Left,
        CrossAlign.End => HorizontalAlignment.Right,
        CrossAlign.Stretch => HorizontalAlignment.Stretch,
        _ => HorizontalAlignment.Center,
    };

    /// <summary>Liste; mit <see cref="ListWidget.Scrollable"/> scrollt sie, statt aus der Notch zu wachsen.</summary>
    private sealed class ListView2 : Grid
    {
        public ListView2()
        {
            Scroller = new ScrollViewer
            {
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = Items,
            };
            Children.Add(Scroller);
        }

        public ScrollViewer Scroller { get; }
        public StackPanel Items { get; } = new();
    }

    private ListView2 RenderList(ListWidget w, ListView2? list)
    {
        list ??= new ListView2();
        var panel = list.Items;
        panel.Spacing = w.Spacing;
        list.Scroller.VerticalScrollMode = w.Scrollable ? ScrollMode.Auto : ScrollMode.Disabled;
        list.Scroller.VerticalScrollBarVisibility = w.Scrollable ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        for (var i = 0; i < w.Items.Count; i++)
        {
            var existing = i < panel.Children.Count ? panel.Children[i] as FrameworkElement : null;
            var element = Render(w.Items[i], existing);
            if (existing is null)
            {
                panel.Children.Add(element);
            }
            else if (!ReferenceEquals(existing, element))
            {
                panel.Children[i] = element;
            }
        }
        while (panel.Children.Count > w.Items.Count)
        {
            panel.Children.RemoveAt(panel.Children.Count - 1);
        }
        return list;
    }

    private sealed class SpacerView : Grid
    {
    }

    private static SpacerView RenderSpacer(SpacerWidget w, SpacerView? spacer)
    {
        spacer ??= new SpacerView();
        spacer.MinWidth = spacer.MinHeight = w.Size;
        return spacer;
    }
}

/// <summary>Zwischenspeicher für Pinsel aus Hex-Farben.</summary>
internal static class Brushes
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SolidColorBrush Get(string hex)
    {
        if (!Cache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush(Parse(hex));
            Cache[hex] = brush;
        }
        return brush;
    }

    public static Color Parse(string hex)
    {
        var (a, r, g, b) = AccentColor.Parse(hex);
        return Color.FromArgb(a, r, g, b);
    }
}
