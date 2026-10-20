using Brim.Core;
using Brim.Core.Widgets;
using Brim.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Brim.Shell;

/// <summary>
/// Seite „Jetzt“ als Raster aus Kacheln (Personalisierung, Plan 7i, Entwurf „Jetzt neu“): 4 × 2 Felder pro Seite,
/// Kacheln aus Modulen und Plugins. Bearbeiten direkt in der Notch – lange drücken oder Rechtsklick: Kacheln werden
/// gedämpft und umrandet, ziehen sortiert um, „−“ entfernt, unter der Kachel unter dem Zeiger steht die Größenwahl,
/// freie Felder sind gestrichelt und öffnen die Galerie. Die Galerie füllt die hohe Notch: Gruppen links, Vorschauen
/// rechts, nach der Wahl eine Vorschau in echter Größe mit den Größen und „Hinzufügen“.
/// </summary>
internal sealed class NowBoard : Grid
{
    private static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(550);

    /// <summary>Gruppen der Galerie in dieser Reihenfolge; alles andere (Plugins) danach.</summary>
    private static readonly string[] BuiltInCategories = ["Brim", "Uhren", "Kalender", "Medien", "System"];

    private readonly ActivityHub _hub;
    private readonly SettingsStore _settings;
    private readonly FrameworkElement _activity;
    private readonly Grid _page = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Dictionary<string, (Border Tile, WidgetRenderer Renderer)> _tiles = new();
    private readonly Dictionary<Border, Grid> _overlays = new();
    private readonly Dictionary<(int Column, int Row), Button> _placeholders = new();

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

    /// <summary>Galerie auf oder zu: Die Notch wird dafür höher und nimmt Tastatur an (Suche).</summary>
    public event Action<bool>? GalleryChanged;

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
            if (_gallery is null)
            {
                SetEditing(true);
            }
            e.Handled = true;
        };
    }

    public bool IsEditing => _editing;

    public bool IsGalleryOpen => _gallery is not null;

    private List<TilePlacement> Tiles => _settings.Current.Now.Tiles;

    /// <summary>Größe der Kachel „Laufende Aktivität“, falls sie liegt (halbe Seite: Inhalt <see cref="Brim.Core.Activity.Tile"/>).</summary>
    public TileSize? ActivitySize => Tiles.FirstOrDefault(t => t.Key == NowSettings.ActivityTile)?.Size;

    /// <summary>Kacheln neu zeichnen (bei jeder Änderung im Hub, solange „Jetzt“ offen ist).</summary>
    public void Update()
    {
        PublishPlacements();
        // Mit dem Index in den Einstellungen (nicht in der gefilterten Liste), damit Entfernen und Ziehen stimmen.
        var shown = Tiles.Select((placement, index) => (Placement: placement, Index: index))
            .Where(t => t.Placement.Key == NowSettings.ActivityTile || _hub.Tile(t.Placement.Key) is not null)
            .ToList();
        var slots = Dashboard.Pack(shown.Select(t => t.Placement).ToList());
        var pages = Dashboard.PageCount(slots);
        // Beim Bearbeiten gibt es nach einer vollen letzten Seite eine leere, damit freie Felder da sind.
        if (_editing && Dashboard.FreeCells(slots, pages - 1).Count == 0)
        {
            pages++;
        }
        _pageIndex = Math.Clamp(_pageIndex, 0, pages - 1);

        var used = new HashSet<string>();
        var placed = new List<FrameworkElement>();
        var overlays = new List<FrameworkElement>();
        foreach (var slot in slots.Where(s => s.Page == _pageIndex))
        {
            var (placement, index) = shown[slot.Index];
            var id = $"{placement.Key}#{index}";
            used.Add(id);
            var tile = Tile(id, placement, index);
            Place(tile, slot.Column, slot.Row, slot.Width, slot.Height);
            placed.Add(tile);
            if (_editing)
            {
                overlays.Add(Overlay(tile, placement, index));
            }
        }
        if (_editing)
        {
            foreach (var cell in Dashboard.FreeCells(slots, _pageIndex))
            {
                placed.Add(Placeholder(cell));
            }
        }
        placed.AddRange(overlays);
        // Nur neu einhängen, wenn sich die Anordnung geändert hat; sonst nur Inhalte (z. B. jede Sekunde die Uhr).
        if (!placed.SequenceEqual(_page.Children.OfType<FrameworkElement>()))
        {
            _page.Children.Clear();
            foreach (var element in placed)
            {
                _page.Children.Add(element);
            }
        }
        foreach (var stale in _tiles.Keys.Where(k => !used.Contains(k)).ToList())
        {
            var old = _tiles[stale].Tile;
            _tiles.Remove(stale);
            _overlays.Remove(old);
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
                var dot = new Border
                {
                    Width = 5,
                    Height = 5,
                    CornerRadius = new CornerRadius(2.5),
                    Background = Brushes.Get(i == _pageIndex ? BrimColors.Fog : BrimColors.Track),
                };
                // Die leere Seite beim Bearbeiten ist gestrichelt angedeutet.
                if (_editing && i == pages - 1 && slots.All(s => s.Page < i))
                {
                    dot.Background = null;
                    dot.BorderBrush = Brushes.Get(BrimColors.Slate);
                    dot.BorderThickness = new Thickness(1);
                }
                _dots.Children.Add(dot);
            }
        }
        _gallery?.Update();
    }

    private static void Place(FrameworkElement element, int column, int row, int width, int height)
    {
        element.Width = width * Dashboard.CellWidth + (width - 1) * Dashboard.Gap;
        element.Height = height * Dashboard.CellHeight + (height - 1) * Dashboard.Gap;
        element.Margin = new Thickness(column * (Dashboard.CellWidth + Dashboard.Gap), row * (Dashboard.CellHeight + Dashboard.Gap), 0, 0);
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.VerticalAlignment = VerticalAlignment.Top;
    }

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
        // Über die ganze Seite bringt die Aktivität ihre eigene Fläche mit (Player, Anruf); sonst sitzt alles auf einer Karte.
        var bare = activity && placement.Size == TileSize.Full && !_editing;
        tile.Background = bare ? null : Brushes.Get(BrimColors.Graphite);
        tile.CornerRadius = new CornerRadius(16);
        tile.BorderBrush = Brushes.Get("#FF3A3B42");
        tile.BorderThickness = new Thickness(_editing ? 1 : 0);
        tile.Padding = bare ? new Thickness(0) : activity ? new Thickness(14) : new Thickness(Dashboard.TilePadding);

        FrameworkElement content;
        if (activity)
        {
            content = _activity;
        }
        else if (_hub.TileContent(placement.Key, placement.Size) is { } widget)
        {
            content = renderer.Render(widget, tile.Child as FrameworkElement is { } existing && existing is not ProgressRing ? existing : null);
        }
        else
        {
            content = new ProgressRing { Width = 18, Height = 18, IsActive = true };
        }

        // Beim Bearbeiten: Inhalt gedämpft und nicht bedienbar (Knöpfe sollen beim Ziehen nicht auslösen).
        content.IsHitTestVisible = !_editing;
        content.Opacity = _editing ? 0.55 : 1;
        if (!ReferenceEquals(tile.Child, content))
        {
            Detach(content);
            tile.Child = content;
            _owners[content] = tile;
        }
        AutomationProperties.SetName(tile, activity ? "Laufende Aktivität" : _hub.Tile(placement.Key)?.Name ?? placement.Key);
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

    /// <summary>
    /// Über jeder Kachel beim Bearbeiten: „−“ oben links, unten mittig die Größenwahl (nur unter dem Zeiger).
    /// Eigene Ebene, weil eine Border mit runden Ecken ihren Inhalt beschneidet.
    /// </summary>
    private Grid Overlay(Border tile, TilePlacement placement, int index)
    {
        // Wiederverwenden, sonst hinge jede Sekunde (Uhr) eine neue Ebene ein und die Größenwahl verschwände.
        if (_overlays.TryGetValue(tile, out var existing))
        {
            existing.Margin = tile.Margin;
            return existing;
        }
        var overlay = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        overlay.Width = tile.Width;
        overlay.Height = tile.Height;
        overlay.Margin = tile.Margin;
        overlay.HorizontalAlignment = HorizontalAlignment.Left;
        overlay.VerticalAlignment = VerticalAlignment.Top;
        _overlays[tile] = overlay;

        var name = placement.Key == NowSettings.ActivityTile ? "Laufende Aktivität" : _hub.Tile(placement.Key)?.Name ?? "Widget";
        var remove = new Button
        {
            Style = ButtonStyle,
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-7, -7, 0, 0),
            Background = Brushes.Get("#FF3A3B42"),
            Content = Icon("", 10),
        };
        AutomationProperties.SetName(remove, $"{name} entfernen");
        remove.Click += (_, _) =>
        {
            Tiles.RemoveAt(index);
            Save();
        };
        overlay.Children.Add(remove);

        var sizes = placement.Key == NowSettings.ActivityTile
            ? _hub.Tile(NowSettings.ActivityTile)?.Sizes ?? [TileSize.Large, TileSize.Full]
            : _hub.Tile(placement.Key)?.Sizes ?? [];
        if (sizes.Count > 1)
        {
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Padding = new Thickness(2),
                CornerRadius = new CornerRadius(14),
                Background = Brushes.Get(BrimColors.Track),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, -12),
                Visibility = Visibility.Collapsed,
            };
            AutomationProperties.SetName(bar, $"Größe von {name}");
            foreach (var size in sizes)
            {
                var selected = size == placement.Size;
                var button = new Button
                {
                    Style = ButtonStyle,
                    Width = 30,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = selected ? Brushes.Get(BrimColors.Fog) : null,
                    Content = SizeIcon(size, 3.5, 1.5, selected ? BrimColors.Obsidian : BrimColors.Fog, selected ? "#33000000" : "#40F2F3F5"),
                };
                AutomationProperties.SetName(button, SizeName(size));
                ToolTipService.SetToolTip(button, SizeName(size));
                button.Click += (_, _) =>
                {
                    if (Tiles[index].Size != size)
                    {
                        Tiles[index] = placement with { Size = size };
                        Save();
                    }
                };
                bar.Children.Add(button);
            }
            overlay.Children.Add(bar);
            overlay.PointerEntered += (_, _) =>
            {
                bar.Visibility = Visibility.Visible;
                tile.BorderBrush = Brushes.Get(BrimColors.DefaultAccent);
            };
            overlay.PointerExited += (_, _) =>
            {
                bar.Visibility = Visibility.Collapsed;
                tile.BorderBrush = Brushes.Get("#FF3A3B42");
            };
        }
        return overlay;
    }

    /// <summary>Freies Feld beim Bearbeiten: gestrichelt mit „+“, öffnet die Galerie.</summary>
    private Button Placeholder((int Column, int Row) cell)
    {
        if (!_placeholders.TryGetValue(cell, out var button))
        {
            var outline = new Rectangle
            {
                RadiusX = 16,
                RadiusY = 16,
                Stroke = Brushes.Get("#FF4A4D56"),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            var content = new Grid();
            content.Children.Add(outline);
            content.Children.Add(Icon("", 16, BrimColors.Slate));
            button = new Button
            {
                Style = ButtonStyle,
                CornerRadius = new CornerRadius(16),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = content,
            };
            AutomationProperties.SetName(button, "Widget hinzufügen");
            button.Click += (_, _) => OpenGallery();
            _placeholders[cell] = button;
        }
        Place(button, cell.Column, cell.Row, 1, 1);
        return button;
    }

    private static Style ButtonStyle => (Style)Application.Current.Resources["NotchButtonStyle"];

    private static FontIcon Icon(string glyph, double size, string color = BrimColors.Fog) => new()
    {
        Glyph = glyph,
        FontSize = size,
        FontFamily = (FontFamily)Application.Current.Resources["NotchIconFont"],
        Foreground = Brushes.Get(color),
    };

    /// <summary>Kleines Bild des Rasters (4 × 2) mit der Fläche, die eine Größe belegt – statt Wörtern wie „ganze Seite“.</summary>
    public static Canvas SizeIcon(TileSize size, double cell, double gap, string on, string off)
    {
        var (w, h) = Dashboard.Cells(size);
        var canvas = new Canvas
        {
            Width = Dashboard.Columns * cell + (Dashboard.Columns - 1) * gap,
            Height = Dashboard.Rows * cell + (Dashboard.Rows - 1) * gap,
        };
        for (var row = 0; row < Dashboard.Rows; row++)
        {
            for (var column = 0; column < Dashboard.Columns; column++)
            {
                if (column < w && row < h)
                {
                    continue;
                }
                var rect = new Rectangle { Width = cell, Height = cell, RadiusX = cell / 4, RadiusY = cell / 4, Fill = Brushes.Get(off) };
                Canvas.SetLeft(rect, column * (cell + gap));
                Canvas.SetTop(rect, row * (cell + gap));
                canvas.Children.Add(rect);
            }
        }
        canvas.Children.Add(new Rectangle
        {
            Width = w * cell + (w - 1) * gap,
            Height = h * cell + (h - 1) * gap,
            RadiusX = cell / 4,
            RadiusY = cell / 4,
            Fill = Brushes.Get(on),
        });
        return canvas;
    }

    public static string SizeName(TileSize size) => size switch
    {
        TileSize.Small => "Klein",
        TileSize.Medium => "Mittel",
        TileSize.Wide => "Breit",
        TileSize.Large => "Groß",
        _ => "Ganze Seite",
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
        _overlays.Clear();
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
        _overlays.Clear();
        Update();
    }

    /// <summary>Meldet dem Hub, welche Kacheln gebraucht werden (und in der Galerie die Vorschauen).</summary>
    private void PublishPlacements()
    {
        var placements = Tiles.Select(t => (t.Key, t.Size)).ToList();
        if (_gallery is not null)
        {
            placements.AddRange(_hub.TileCatalog().Select(d => (d.Key, d.DefaultSize)));
            if (_gallery.Selected is { } selected)
            {
                placements.AddRange(selected.Sizes.Select(s => (selected.Key, s)));
            }
        }
        _hub.SetTilePlacements(placements);
    }

    // ───────── Galerie ─────────

    /// <summary>Öffnet die Galerie (auch über „+ Widget“ in der Kopfzeile); schaltet das Bearbeiten mit ein.</summary>
    public void OpenGallery()
    {
        if (_gallery is not null)
        {
            return;
        }
        if (!_editing)
        {
            SetEditing(true);
        }
        _gallery = new Gallery(this);
        Children.Add(_gallery);
        PublishPlacements();
        _gallery.Update();
        GalleryChanged?.Invoke(true);
    }

    public void CloseGallery()
    {
        if (_gallery is null)
        {
            return;
        }
        Children.Remove(_gallery);
        _gallery = null;
        PublishPlacements();
        GalleryChanged?.Invoke(false);
    }

    /// <summary>Suchtext aus der Kopfzeile.</summary>
    public void Filter(string text) => _gallery?.Filter(text);

    private void Add(TileDefinition definition, TileSize size)
    {
        Tiles.Add(new TilePlacement(definition.Key, size));
        CloseGallery();
        // Zur Seite der neuen Kachel blättern.
        _pageIndex = Dashboard.Pack(Tiles)[Tiles.Count - 1].Page;
        Save();
    }

    /// <summary>
    /// Gruppen links, rechts Vorschau-Kacheln in der Standardgröße. Nach einem Klick: Vorschau in echter Größe,
    /// Größen als Rasterbildchen und „Hinzufügen“.
    /// </summary>
    private sealed class Gallery : Grid
    {
        private readonly NowBoard _board;
        private readonly StackPanel _nav = new() { Spacing = 2 };
        private readonly Grid _cards = new() { ColumnSpacing = 10, RowSpacing = 10 };
        private readonly ScrollViewer _scroll;
        private readonly Grid _detail = new() { Visibility = Visibility.Collapsed };
        private readonly Dictionary<string, WidgetRenderer> _previews = new();
        private readonly Dictionary<string, (TileDefinition Definition, Border Preview, Button Card)> _items = new();
        private string? _category;
        private string _query = "";
        private TileSize _size;
        private Border? _detailPreview;
        private StackPanel? _detailSizes;

        public Gallery(NowBoard board)
        {
            _board = board;
            Background = Brushes.Get(BrimColors.Obsidian);
            ColumnSpacing = 12;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
            ColumnDefinitions.Add(new ColumnDefinition());
            Children.Add(_nav);
            _cards.ColumnDefinitions.Add(new ColumnDefinition());
            _cards.ColumnDefinitions.Add(new ColumnDefinition());
            _scroll = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            SetColumn(_scroll, 1);
            Children.Add(_scroll);
            SetColumnSpan(_detail, 2);
            Children.Add(_detail);
        }

        public TileDefinition? Selected { get; private set; }

        /// <summary>Was die Galerie zeigt: alles außer der Aktivität, wenn sie schon liegt (es gibt nur eine).</summary>
        private List<TileDefinition> Catalog() => _board._hub.TileCatalog()
            .Where(d => d.Key != NowSettings.ActivityTile || !_board.Tiles.Any(t => t.Key == NowSettings.ActivityTile))
            .OrderBy(d => Array.IndexOf(BuiltInCategories, d.Category) is var i && i >= 0 ? i : BuiltInCategories.Length)
            .ThenBy(d => d.Category, StringComparer.CurrentCulture)
            .ThenBy(d => d.Name, StringComparer.CurrentCulture)
            .ToList();

        public void Filter(string text)
        {
            _query = text.Trim();
            Build();
        }

        /// <summary>Karten nur bauen, wenn sich der Katalog ändert (Scrollposition bleibt), Vorschauen bei jeder Aktualisierung.</summary>
        public void Update()
        {
            var catalog = Catalog();
            if (!catalog.Select(d => d.Key).SequenceEqual(_items.Keys))
            {
                _items.Clear();
                foreach (var definition in catalog)
                {
                    var (card, preview) = Card(definition);
                    _items[definition.Key] = (definition, preview, card);
                }
                BuildNav(catalog);
                Build();
            }
            foreach (var (definition, preview, _) in _items.Values)
            {
                RenderInto(preview, definition, definition.DefaultSize);
            }
            if (Selected is { } selected && _detailPreview is not null)
            {
                RenderInto(_detailPreview, selected, _size, "detail");
            }
        }

        private void BuildNav(List<TileDefinition> catalog)
        {
            _nav.Children.Clear();
            _nav.Children.Add(NavButton("Alle", null));
            var plugins = false;
            foreach (var category in catalog.Select(d => d.Category).Distinct())
            {
                if (!plugins && catalog.Any(d => d.Category == category && d.ModuleId == PluginHostModule.ModuleId))
                {
                    plugins = true;
                    _nav.Children.Add(new Border { Height = 1, Background = Brushes.Get(BrimColors.Track), Margin = new Thickness(4, 6, 4, 2) });
                    _nav.Children.Add(new TextBlock { Text = "Plugins", FontSize = 10.5, Foreground = Brushes.Get(BrimColors.Slate), Margin = new Thickness(10, 2, 0, 2) });
                }
                _nav.Children.Add(NavButton(category, category));
            }
        }

        private Button NavButton(string label, string? category)
        {
            var selected = _category == category;
            var button = new Button
            {
                Style = ButtonStyle,
                Height = 30,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 0, 10, 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = selected ? Brushes.Get(BrimColors.Track) : null,
                Content = new TextBlock { Text = label, FontSize = 12.5, Foreground = Brushes.Get(selected ? BrimColors.Fog : "#FFC9CCD3") },
            };
            button.Click += (_, _) =>
            {
                _category = category;
                BuildNav(Catalog());
                Build();
            };
            return button;
        }

        /// <summary>Karten der gewählten Gruppe und Suche, zwei Spalten.</summary>
        private void Build()
        {
            _cards.Children.Clear();
            _cards.RowDefinitions.Clear();
            var shown = _items.Values
                .Where(i => _category is null || i.Definition.Category == _category)
                .Where(i => _query.Length == 0 || Matches(i.Definition, _query))
                .ToList();
            for (var i = 0; i < shown.Count; i++)
            {
                if (i % 2 == 0)
                {
                    _cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }
                var card = shown[i].Card;
                SetRow(card, i / 2);
                SetColumn(card, i % 2);
                _cards.Children.Add(card);
            }
            if (shown.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = _query.Length > 0 ? $"Kein Widget für „{_query}“" : "Keine Widgets in dieser Gruppe",
                    FontSize = 12.5,
                    Foreground = Brushes.Get(BrimColors.Slate),
                    Margin = new Thickness(4, 8, 0, 0),
                };
                SetColumnSpan(empty, 2);
                _cards.Children.Add(empty);
            }
        }

        private static bool Matches(TileDefinition d, string query) =>
            d.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || d.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (d.Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);

        private (Button Card, Border Preview) Card(TileDefinition definition)
        {
            // Vorschau verkleinert in einer dunklen Mulde; bis Inhalt da ist, das Symbol.
            var preview = new Border { Child = definition.Glyph is { } glyph ? Icon(glyph, 18, BrimColors.Slate) : null };
            var well = new Border
            {
                Height = 86,
                CornerRadius = new CornerRadius(10),
                Background = Brushes.Get("#FF0E0F11"),
                Child = new Viewbox { MaxHeight = 66, MaxWidth = 180, Stretch = Stretch.Uniform, Child = preview },
            };
            var name = new Grid();
            name.Children.Add(new TextBlock { Text = definition.Name, FontSize = 12.5, Foreground = Brushes.Get(BrimColors.Fog), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 52, 0) });
            name.Children.Add(new TextBlock
            {
                Text = definition.Sizes.Count == 1 ? SizeName(definition.Sizes[0]) : $"{definition.Sizes.Count} Größen",
                FontSize = 10.5,
                Foreground = Brushes.Get(BrimColors.Slate),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(well);
            stack.Children.Add(name);
            var card = new Button
            {
                Style = ButtonStyle,
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(14),
                Background = Brushes.Get(BrimColors.Graphite),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = stack,
            };
            AutomationProperties.SetName(card, $"{definition.Name}, {definition.Description}");
            card.Click += (_, _) => ShowDetail(definition);
            return (card, preview);
        }

        /// <summary>Inhalt eines Widgets in einer Größe, als Kachel mit Karte (Vorschau oder echte Größe).</summary>
        private void RenderInto(Border host, TileDefinition definition, TileSize size, string scope = "card")
        {
            var (width, height) = Dashboard.PixelSize(size);
            FrameworkElement? element;
            if (definition.Key == NowSettings.ActivityTile)
            {
                element = ActivityPreview();
            }
            else if (_board._hub.TileContent(definition.Key, size) is { } widget)
            {
                var id = $"{scope}:{definition.Key}#{size}";
                if (!_previews.TryGetValue(id, out var renderer))
                {
                    renderer = new WidgetRenderer((_, _) => { });
                    _previews[id] = renderer;
                }
                var existing = (host.Child as Border)?.Child as FrameworkElement;
                element = renderer.Render(widget, existing is FontIcon or StackPanel ? null : existing);
            }
            else
            {
                return;
            }
            element.IsHitTestVisible = false;
            if (host.Child is Border { Tag: "card" } card && ReferenceEquals(card.Child, element) && card.Width == width && card.Height == height)
            {
                return;
            }
            if (host.Child is Border { Tag: "card" } old)
            {
                old.Child = null;
            }
            host.Child = new Border
            {
                Tag = "card",
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(16),
                Background = Brushes.Get(BrimColors.Graphite),
                Padding = new Thickness(Dashboard.TilePadding),
                Child = element,
            };
        }

        /// <summary>Die Aktivität hat keinen eigenen Inhalt in der Galerie: ein Hinweis, was dort erscheint.</summary>
        private static StackPanel ActivityPreview()
        {
            var stack = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(Icon(Glyphs.Play, 22, BrimColors.DefaultAccent));
            stack.Children.Add(new TextBlock { Text = "Musik, Anruf, Timer", FontSize = 13, Foreground = Brushes.Get(BrimColors.Fog), HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = "Läuft nichts: Heute", FontSize = 11, Foreground = Brushes.Get(BrimColors.Slate), HorizontalAlignment = HorizontalAlignment.Center });
            return stack;
        }

        // ───────── Größe wählen ─────────

        private void ShowDetail(TileDefinition definition)
        {
            Selected = definition;
            _size = definition.DefaultSize;
            _detail.Children.Clear();
            _detail.RowDefinitions.Clear();
            _detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _detail.RowDefinitions.Add(new RowDefinition());
            _detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _detail.RowSpacing = 14;

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var back = new Button
            {
                Style = ButtonStyle,
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = Brushes.Get(BrimColors.Track),
                Content = Icon(Glyphs.Back, 11),
            };
            AutomationProperties.SetName(back, "Zurück zur Galerie");
            back.Click += (_, _) => HideDetail();
            header.Children.Add(back);
            header.Children.Add(new TextBlock { Text = definition.Name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brushes.Get(BrimColors.Fog), VerticalAlignment = VerticalAlignment.Center });
            if (definition.Description is { } description)
            {
                header.Children.Add(new TextBlock { Text = $"{definition.Category} · {description}", FontSize = 11.5, Foreground = Brushes.Get(BrimColors.Slate), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 });
            }
            _detail.Children.Add(header);

            _detailPreview = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            SetRow(_detailPreview, 1);
            _detail.Children.Add(_detailPreview);

            var footer = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
            _detailSizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(_detailSizes, "Größe");
            footer.Children.Add(_detailSizes);
            var add = new Button
            {
                Style = ButtonStyle,
                Height = 34,
                Padding = new Thickness(22, 0, 22, 1),
                CornerRadius = new CornerRadius(17),
                Background = Brushes.Get(BrimColors.DefaultAccent),
                HorizontalAlignment = HorizontalAlignment.Center,
                Content = new TextBlock { Text = "Hinzufügen", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brushes.Get(BrimColors.Obsidian) },
            };
            AutomationProperties.SetName(add, $"{definition.Name} hinzufügen");
            add.Click += (_, _) => _board.Add(definition, _size);
            footer.Children.Add(add);
            SetRow(footer, 2);
            _detail.Children.Add(footer);

            _nav.Visibility = Visibility.Collapsed;
            _scroll.Visibility = Visibility.Collapsed;
            _detail.Visibility = Visibility.Visible;
            BuildSizes();
            // Inhalt für alle Größen anfordern; er kommt mit der nächsten Aktualisierung.
            _board.PublishPlacements();
            RenderInto(_detailPreview, definition, _size, "detail");
        }

        private void BuildSizes()
        {
            if (Selected is not { } definition || _detailSizes is null)
            {
                return;
            }
            _detailSizes.Children.Clear();
            foreach (var size in definition.Sizes)
            {
                var selected = size == _size;
                var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
                var icon = SizeIcon(size, 8, 1.5, BrimColors.Fog, "#FF3A3B42");
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                content.Children.Add(icon);
                content.Children.Add(new TextBlock { Text = SizeName(size), FontSize = 12, Foreground = Brushes.Get(selected ? BrimColors.Fog : "#FFC9CCD3"), HorizontalAlignment = HorizontalAlignment.Center });
                var button = new Button
                {
                    Style = ButtonStyle,
                    Width = 88,
                    Padding = new Thickness(8),
                    CornerRadius = new CornerRadius(12),
                    Background = Brushes.Get(selected ? BrimColors.Track : BrimColors.Graphite),
                    BorderBrush = Brushes.Get(BrimColors.DefaultAccent),
                    BorderThickness = new Thickness(selected ? 1.5 : 0),
                    Content = content,
                };
                AutomationProperties.SetName(button, SizeName(size));
                button.Click += (_, _) =>
                {
                    _size = size;
                    BuildSizes();
                    if (_detailPreview is not null)
                    {
                        RenderInto(_detailPreview, definition, size, "detail");
                    }
                };
                _detailSizes.Children.Add(button);
            }
        }

        private void HideDetail()
        {
            Selected = null;
            _detailPreview = null;
            _detailSizes = null;
            _detail.Visibility = Visibility.Collapsed;
            _nav.Visibility = Visibility.Visible;
            _scroll.Visibility = Visibility.Visible;
            _board.PublishPlacements();
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
                if (_overlays.TryGetValue(tile, out var overlay))
                {
                    overlay.Opacity = 0;
                }
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
        if (_overlays.TryGetValue(drag.Tile, out var overlay))
        {
            overlay.Opacity = 1;
        }
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
        var slots = Dashboard.Pack(Tiles);
        var pages = Dashboard.PageCount(slots);
        if (_editing && Dashboard.FreeCells(slots, pages - 1).Count == 0)
        {
            pages++;
        }
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
