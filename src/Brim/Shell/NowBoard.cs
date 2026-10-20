using Brim.Core;
using Brim.Core.Widgets;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Brim.Shell;

/// <summary>
/// Seite „Jetzt“ als Raster aus Kacheln (Personalisierung, Plan 7i): 4 × 2 Felder pro Seite, Kacheln aus
/// Modulen und Plugins. Bearbeiten direkt in der Notch – lange drücken oder Rechtsklick: Kacheln bekommen
/// einen Rahmen, ziehen sortiert um, „−“ entfernt, die Größe wechselt per Tipp, „+“ öffnet die Galerie.
/// </summary>
internal sealed class NowBoard : Grid
{
    private static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(550);

    private readonly ActivityHub _hub;
    private readonly SettingsStore _settings;
    private readonly FrameworkElement _activity;
    private readonly Grid _page = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Dictionary<string, (Border Tile, WidgetRenderer Renderer)> _tiles = new();

    // Wo jeder Inhalt gerade hängt. FrameworkElement.Parent ist vor dem ersten Laden leer und taugt dafür nicht.
    private readonly Dictionary<FrameworkElement, FrameworkElement> _owners = [];
    private readonly DispatcherTimer _pressTimer = new() { Interval = LongPress };
    private Gallery? _gallery;
    private int _pageIndex;
    private bool _editing;
    private (int Index, Point Start, Border Tile)? _drag;
    private Point _pressAt;

    /// <summary>Bearbeiten an oder aus: Die Notch bleibt so lange offen.</summary>
    public event Action<bool>? EditingChanged;

    /// <param name="activity">Inhalt der Kachel „Laufende Aktivität“ (der bisherige Inhalt von „Jetzt“).</param>
    public NowBoard(ActivityHub hub, SettingsStore settings, FrameworkElement activity)
    {
        _hub = hub;
        _settings = settings;
        _activity = activity;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _page.Width = Dashboard.Columns * Dashboard.CellWidth + (Dashboard.Columns - 1) * Dashboard.Gap;
        _page.Height = Dashboard.Rows * Dashboard.CellHeight + (Dashboard.Rows - 1) * Dashboard.Gap;
        _page.HorizontalAlignment = HorizontalAlignment.Left;
        _page.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_page);
        _dots.Margin = new Thickness(0, 0, 0, -13);
        Children.Add(_dots);

        _pressTimer.Tick += (_, _) =>
        {
            _pressTimer.Stop();
            SetEditing(true);
        };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCanceled += (_, _) => _pressTimer.Stop();
        PointerWheelChanged += OnWheel;
        // Rechtsklick bearbeitet direkt (ein Menü würde von der Fensterregion abgeschnitten).
        RightTapped += (_, e) =>
        {
            SetEditing(true);
            e.Handled = true;
        };
    }

    public bool IsEditing => _editing;

    private List<TilePlacement> Tiles => _settings.Current.Now.Tiles;

    /// <summary>Nur die laufende Aktivität über die ganze Seite: „Jetzt“ sieht aus wie vor den Kacheln.</summary>
    public bool IsClassic => !_editing && Tiles is [{ Key: NowSettings.ActivityTile, Size: TileSize.Full }];

    /// <summary>Kacheln neu zeichnen (bei jeder Änderung im Hub, solange „Jetzt“ offen ist).</summary>
    public void Update()
    {
        PublishPlacements();
        var tiles = Tiles.Where(t => t.Key == NowSettings.ActivityTile || _hub.Tile(t.Key) is not null).ToList();
        List<TilePlacement> shown = _editing ? [.. tiles, new TilePlacement(AddKey, TileSize.Small)] : tiles;
        var slots = Dashboard.Pack(shown);
        var pages = Dashboard.PageCount(slots);
        _pageIndex = Math.Clamp(_pageIndex, 0, pages - 1);

        var used = new HashSet<string>();
        var placed = new List<Border>();
        foreach (var slot in slots.Where(s => s.Page == _pageIndex))
        {
            var placement = shown[slot.Index];
            var id = $"{placement.Key}#{slot.Index}";
            used.Add(id);
            var tile = Tile(id, placement, slot.Index);
            tile.Width = slot.Width * Dashboard.CellWidth + (slot.Width - 1) * Dashboard.Gap;
            tile.Height = slot.Height * Dashboard.CellHeight + (slot.Height - 1) * Dashboard.Gap;
            tile.Margin = new Thickness(slot.Column * (Dashboard.CellWidth + Dashboard.Gap), slot.Row * (Dashboard.CellHeight + Dashboard.Gap), 0, 0);
            tile.HorizontalAlignment = HorizontalAlignment.Left;
            tile.VerticalAlignment = VerticalAlignment.Top;
            placed.Add(tile);
        }
        // Nur neu einhängen, wenn sich die Anordnung geändert hat; sonst nur Inhalte (z. B. jede Sekunde die Uhr).
        if (!placed.SequenceEqual(_page.Children.OfType<Border>()))
        {
            _page.Children.Clear();
            foreach (var tile in placed)
            {
                _page.Children.Add(tile);
            }
        }
        foreach (var stale in _tiles.Keys.Where(k => !used.Contains(k)).ToList())
        {
            var old = _tiles[stale].Tile;
            _tiles.Remove(stale);
            // Inhalte, die nur in der alten Kachel hingen, vergessen (nicht die Aktivität, die zieht weiter).
            foreach (var content in _owners.Where(o => ReferenceEquals(o.Value, old) && !ReferenceEquals(o.Key, _activity)).Select(o => o.Key).ToList())
            {
                _owners.Remove(content);
            }
        }

        _dots.Children.Clear();
        if (pages > 1)
        {
            for (var i = 0; i < pages; i++)
            {
                _dots.Children.Add(new Border
                {
                    Width = 5,
                    Height = 5,
                    CornerRadius = new CornerRadius(2.5),
                    Background = Brushes.Get(i == _pageIndex ? BrimColors.Fog : BrimColors.Track),
                });
            }
        }
        _gallery?.Update();
    }

    private const string AddKey = "brim/add";

    private Border Tile(string id, TilePlacement placement, int index)
    {
        if (!_tiles.TryGetValue(id, out var entry))
        {
            var moduleId = placement.Key.Split('/', 2)[0];
            entry = (new Border(), new WidgetRenderer((action, value) => _hub.Invoke(new ActionInvocation(moduleId, action, value))));
            _tiles[id] = entry;
        }
        var (tile, renderer) = entry;
        tile.Tag = index;
        var activity = placement.Key == NowSettings.ActivityTile;
        // Die Aktivität bringt ihre eigene Fläche mit (Player, Anruf); alle anderen sitzen auf einer Karte.
        tile.Background = activity && !_editing ? null : Brushes.Get(BrimColors.Graphite);
        tile.CornerRadius = new CornerRadius(16);
        // Bearbeiten erkennt man an einem feinen Rahmen um jede Kachel.
        tile.BorderBrush = Brushes.Get(BrimColors.Slate);
        tile.BorderThickness = new Thickness(_editing && placement.Key != AddKey ? 1 : 0);
        tile.Padding = activity ? new Thickness(0) : new Thickness(Dashboard.TilePadding);

        FrameworkElement content;
        if (placement.Key == AddKey)
        {
            var add = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 18 },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = null,
                BorderThickness = new Thickness(0),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(add, "Widget hinzufügen");
            add.Click += (_, _) => OpenGallery();
            content = add;
        }
        else if (activity)
        {
            content = _activity;
        }
        else if (_hub.TileContent(placement.Key, placement.Size) is { } widget)
        {
            content = renderer.Render(widget, tile.Child as FrameworkElement is { } existing && existing is not Grid { Tag: "edit" } ? existing : null);
        }
        else
        {
            content = new ProgressRing { Width = 18, Height = 18, IsActive = true };
        }

        if (_editing && placement.Key != AddKey)
        {
            tile.Child = EditChrome(content, placement, index);
        }
        else
        {
            content.IsHitTestVisible = true;
            if (!ReferenceEquals(tile.Child, content))
            {
                Detach(content);
                tile.Child = content;
                _owners[content] = tile;
            }
        }
        return tile;
    }

    private void Detach(FrameworkElement element)
    {
        if (!_owners.Remove(element, out var owner))
        {
            return;
        }
        switch (owner)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Border border when ReferenceEquals(border.Child, element):
                border.Child = null;
                break;
        }
    }

    /// <summary>Im Bearbeiten-Modus: Inhalt nicht bedienbar, oben links „−“, unten rechts die Größe.</summary>
    private Grid EditChrome(FrameworkElement content, TilePlacement placement, int index)
    {
        Detach(content);
        content.IsHitTestVisible = false;
        var grid = new Grid { Tag = "edit" };
        grid.Children.Add(content);
        _owners[content] = grid;

        var remove = Badge("", "Entfernen", HorizontalAlignment.Left);
        remove.Click += (_, _) =>
        {
            Tiles.RemoveAt(index);
            Save();
        };
        grid.Children.Add(remove);

        var sizes = placement.Key == NowSettings.ActivityTile ? [TileSize.Full] : _hub.Tile(placement.Key)?.Sizes ?? [];
        if (sizes.Count > 1)
        {
            var resize = Badge("", $"Größe: {SizeName(placement.Size)}", HorizontalAlignment.Right);
            resize.VerticalAlignment = VerticalAlignment.Bottom;
            resize.Click += (_, _) =>
            {
                Tiles[index] = placement with { Size = Dashboard.NextSize(placement.Size, sizes) };
                Save();
            };
            grid.Children.Add(resize);
        }
        return grid;
    }

    private static Button Badge(string glyph, string name, HorizontalAlignment align)
    {
        var button = new Button
        {
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-6),
            Background = Brushes.Get(BrimColors.Track),
            Content = new FontIcon { Glyph = glyph, FontSize = 10 },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        return button;
    }

    public static string SizeName(TileSize size) => size switch
    {
        TileSize.Small => "klein",
        TileSize.Medium => "mittel",
        TileSize.Wide => "breit",
        TileSize.Large => "groß",
        _ => "ganze Seite",
    };

    public void SetEditing(bool editing)
    {
        if (_editing == editing)
        {
            return;
        }
        _editing = editing;
        if (!editing)
        {
            CloseGallery();
        }
        // Inhalte bedienbar bzw. gesperrt: Kacheln neu aufbauen.
        _tiles.Clear();
        EditingChanged?.Invoke(editing);
        Update();
    }

    private void Save()
    {
        try
        {
            _settings.Save(_settings.Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Aufbau von „Jetzt“ nicht gespeichert", ex);
        }
        _tiles.Clear();
        Update();
    }

    /// <summary>Meldet dem Hub, welche Kacheln gebraucht werden (und in der Galerie die Vorschau).</summary>
    private void PublishPlacements()
    {
        var placements = Tiles.Select(t => (t.Key, t.Size)).ToList();
        if (_gallery is not null)
        {
            placements.AddRange(_hub.TileCatalog().Select(d => (d.Key, d.DefaultSize)));
        }
        _hub.SetTilePlacements(placements);
    }

    // ───────── Galerie ─────────

    private void OpenGallery()
    {
        if (_gallery is not null)
        {
            return;
        }
        _gallery = new Gallery(this);
        Children.Add(_gallery);
        PublishPlacements();
        _gallery.Update();
    }

    private void CloseGallery()
    {
        if (_gallery is null)
        {
            return;
        }
        Children.Remove(_gallery);
        _gallery = null;
        PublishPlacements();
    }

    private void Add(TileDefinition definition, TileSize size)
    {
        Tiles.Add(new TilePlacement(definition.Key, size));
        CloseGallery();
        // Zur Seite der neuen Kachel blättern.
        _pageIndex = Dashboard.Pack([.. Tiles, new TilePlacement(AddKey, TileSize.Small)])[Tiles.Count - 1].Page;
        Save();
    }

    /// <summary>Liste aller Widgets nach Gruppe, mit Vorschau und den möglichen Größen.</summary>
    private sealed class Gallery : Grid
    {
        private readonly NowBoard _board;
        private readonly StackPanel _list = new() { Spacing = 6 };
        private readonly Dictionary<string, WidgetRenderer> _previews = new();
        private readonly Dictionary<string, (TileDefinition Definition, Viewbox Preview)> _rows = new();

        public Gallery(NowBoard board)
        {
            _board = board;
            Background = Brushes.Get(BrimColors.Obsidian);
            var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            header.Children.Add(new TextBlock { Text = "Widget hinzufügen", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brushes.Get(BrimColors.Fog), VerticalAlignment = VerticalAlignment.Center });
            var close = new Button { Content = "Fertig", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(10, 2, 10, 3) };
            close.Click += (_, _) => board.CloseGallery();
            header.Children.Add(close);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());
            Children.Add(header);
            var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            SetRow(scroll, 1);
            Children.Add(scroll);
        }

        /// <summary>Zeilen nur einmal bauen (Scrollposition bleibt), Vorschauen bei jeder Aktualisierung.</summary>
        public void Update()
        {
            var catalog = _board._hub.TileCatalog();
            if (!catalog.Select(d => d.Key).SequenceEqual(_rows.Keys))
            {
                _list.Children.Clear();
                _rows.Clear();
                string? category = null;
                foreach (var definition in catalog)
                {
                    if (definition.Category != category)
                    {
                        category = definition.Category;
                        _list.Children.Add(new TextBlock { Text = category, FontSize = 11, Foreground = Brushes.Get(BrimColors.Slate), Margin = new Thickness(2, 6, 0, 0) });
                    }
                    var (row, preview) = Row(definition);
                    _rows[definition.Key] = (definition, preview);
                    _list.Children.Add(row);
                }
            }
            foreach (var (definition, preview) in _rows.Values)
            {
                if (_board._hub.TileContent(definition.Key, definition.DefaultSize) is not { } widget)
                {
                    continue;
                }
                if (!_previews.TryGetValue(definition.Key, out var renderer))
                {
                    renderer = new WidgetRenderer((_, _) => { });
                    _previews[definition.Key] = renderer;
                }
                var (w, h) = Dashboard.ContentSize(definition.DefaultSize);
                var element = renderer.Render(widget, preview.Child as FrameworkElement);
                element.Width = w;
                element.Height = h;
                element.IsHitTestVisible = false;
                if (!ReferenceEquals(preview.Child, element))
                {
                    preview.Child = element;
                }
            }
        }

        private (Grid Row, Viewbox Preview) Row(TileDefinition definition)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(12), Background = Brushes.Get(BrimColors.Graphite) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Dashboard.CellWidth - 2 * Dashboard.TilePadding) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Vorschau in der Standardgröße, verkleinert auf eine Zeile; bis Inhalt da ist, das Symbol.
            var preview = new Viewbox { Height = 44, HorizontalAlignment = HorizontalAlignment.Left };
            if (definition.Glyph is { } glyph)
            {
                preview.Child = new FontIcon { Glyph = glyph, FontSize = 18 };
            }
            row.Children.Add(preview);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = definition.Name, FontSize = 13, Foreground = Brushes.Get(BrimColors.Fog) });
            if (definition.Description is { } description)
            {
                text.Children.Add(new TextBlock { Text = description, FontSize = 11, Foreground = Brushes.Get(BrimColors.Slate), TextTrimming = TextTrimming.CharacterEllipsis });
            }
            SetColumn(text, 1);
            row.Children.Add(text);

            var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            foreach (var size in definition.Sizes)
            {
                var add = new Button { Content = SizeName(size), FontSize = 11, Padding = new Thickness(8, 2, 8, 3) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(add, $"{definition.Name} {SizeName(size)} hinzufügen");
                add.Click += (_, _) => _board.Add(definition, size);
                sizes.Children.Add(add);
            }
            SetColumn(sizes, 2);
            row.Children.Add(sizes);
            return (row, preview);
        }
    }

    // ───────── Eingaben ─────────

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        _pressAt = point.Position;
        if (_editing)
        {
            if (_gallery is null && TileAt(point.Position) is { } tile && tile.Tag is int index && index < Tiles.Count)
            {
                _drag = (index, point.Position, tile);
                CapturePointer(e.Pointer);
                Canvas.SetZIndex(tile, 10);
            }
            return;
        }
        // Lange drücken öffnet das Bearbeiten (nur mit der linken Taste bzw. Finger).
        if (point.Properties.IsLeftButtonPressed)
        {
            _pressTimer.Start();
        }
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;
        if (_pressTimer.IsEnabled && Distance(position, _pressAt) > 6)
        {
            _pressTimer.Stop();
        }
        if (_drag is { } drag)
        {
            drag.Tile.RenderTransform = new TranslateTransform { X = position.X - drag.Start.X, Y = position.Y - drag.Start.Y };
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _pressTimer.Stop();
        if (_drag is not { } drag)
        {
            return;
        }
        _drag = null;
        ReleasePointerCapture(e.Pointer);
        drag.Tile.RenderTransform = null;
        Canvas.SetZIndex(drag.Tile, 0);
        var position = e.GetCurrentPoint(this).Position;
        if (Distance(position, drag.Start) < 8)
        {
            return;
        }
        // Ziel: die Kachel unter dem Zeiger; sonst ans Ende.
        drag.Tile.IsHitTestVisible = false;
        var target = TileAt(position) is { Tag: int index } ? Math.Min(index, Tiles.Count - 1) : Tiles.Count - 1;
        drag.Tile.IsHitTestVisible = true;
        if (target != drag.Index)
        {
            _settings.Current.Now.Tiles = Dashboard.Move(Tiles, drag.Index, target);
            Save();
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_gallery is not null)
        {
            return;
        }
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        var pages = Dashboard.PageCount(Dashboard.Pack(_editing ? [.. Tiles, new TilePlacement(AddKey, TileSize.Small)] : Tiles));
        var next = Math.Clamp(_pageIndex + (delta < 0 ? 1 : -1), 0, pages - 1);
        if (next != _pageIndex)
        {
            _pageIndex = next;
            Update();
            e.Handled = true;
        }
    }

    private Border? TileAt(Point position)
    {
        foreach (var child in _page.Children.OfType<Border>())
        {
            if (!child.IsHitTestVisible)
            {
                continue;
            }
            var origin = child.TransformToVisual(this).TransformPoint(new Point(0, 0));
            if (position.X >= origin.X && position.X < origin.X + child.ActualWidth && position.Y >= origin.Y && position.Y < origin.Y + child.ActualHeight)
            {
                return child;
            }
        }
        return null;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
