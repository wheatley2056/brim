using System.Diagnostics;
using System.Globalization;
using Brim.Core;
using Brim.Modules;
using Brim.Core.Widgets;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;

namespace Brim.Shell;

/// <summary>
/// Tab „Ablage“ (Plan 4.4, design/boards/4-shelf-system.html): Kacheln mit Vorschaubild, Name und Größe.
/// Herausziehen nach Explorer, Mail, Discord oder Browser über WinUI-Drag-and-drop
/// (<see cref="DataPackage.SetStorageItems(IEnumerable{IStorageItem})"/>).
/// Die Ablage ist bewusst Teil der Shell und kein deklaratives Modul: Ziehen ist eine Fähigkeit
/// der Oberfläche, die sich nicht als Datenstruktur beschreiben lässt.
/// </summary>
internal sealed class ShelfView : Grid
{
    private const double TileWidth = 104;

    private readonly ShelfStore _store;
    private readonly SettingsStore _settings;
    private readonly Func<nint> _window;
    private readonly NearbyModule _nearby;
    private readonly StackPanel _tiles = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _empty;
    private readonly TextBlock _summary = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _share;
    private readonly Button _clear;
    private readonly HashSet<string> _selected = [];
    private readonly Dictionary<string, ImageSource> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _details = new(StringComparer.OrdinalIgnoreCase);

    public ShelfView(ShelfStore store, SettingsStore settings, NearbyModule nearby, Func<nint> window)
    {
        _store = store;
        _settings = settings;
        _nearby = nearby;
        _window = window;
        RowSpacing = 14;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _scroller = new ScrollViewer
        {
            Content = _tiles,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetName(_tiles, "Einträge der Ablage");
        Children.Add(_scroller);

        _empty = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _empty.Children.Add(Icon(Glyphs.Folder, 22, BrimColors.Slate));
        _empty.Children.Add(new TextBlock { Text = "Ablage ist leer", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Get(BrimColors.Fog), HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock { Text = "Dateien oder Ordner auf die Notch ziehen – sie bleiben, bis du sie entfernst.", FontSize = 13, Foreground = Brushes.Get(BrimColors.Slate), HorizontalAlignment = HorizontalAlignment.Center });
        Children.Add(_empty);

        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _summary.Foreground = Brushes.Get(BrimColors.Slate);
        footer.Children.Add(_summary);
        var receive = FooterButton("Empfangen …", filled: false, (sender, _) => ShowReceiveFlyout((FrameworkElement)sender));
        AutomationProperties.SetName(receive, "Per Code empfangen");
        _share = FooterButton("Teilen", filled: true, (sender, _) => ShowShareMenu((FrameworkElement)sender, SelectionOrAll()));
        _clear = FooterButton("Leeren", filled: false, (_, _) => _store.Clear());
        Grid.SetColumn(receive, 1);
        Grid.SetColumn(_share, 2);
        Grid.SetColumn(_clear, 3);
        footer.Children.Add(receive);
        footer.Children.Add(_share);
        footer.Children.Add(_clear);
        Grid.SetRow(footer, 1);
        Children.Add(footer);

        _store.Changed += () => DispatcherQueue.TryEnqueue(Refresh);
        Refresh();
    }

    /// <summary>Beginnt oder endet das Herausziehen; solange bleibt die Notch offen.</summary>
    public event Action<bool>? DraggingOut;

    /// <summary>Ein Menü oder Eingabefeld ist offen; solange bleibt die Notch offen.</summary>
    public event Action<bool>? PopupChanged;

    /// <summary>
    /// Menüs als eigene Fenster über der Notch: Sonst würde die Fensterregion sie abschneiden.
    /// Solange sie offen sind, klappt die Notch nicht zu.
    /// </summary>
    private T Popup<T>(T flyout) where T : Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase
    {
        flyout.ShouldConstrainToRootBounds = false;
        flyout.Opened += (_, _) => PopupChanged?.Invoke(true);
        flyout.Closed += (_, _) => PopupChanged?.Invoke(false);
        return flyout;
    }

    // ───────── Per Code empfangen ─────────

    private void ShowReceiveFlyout(FrameworkElement anchor)
    {
        var box = new TextBox
        {
            PlaceholderText = "z. B. 7-guitarist-revenge",
            Width = 240,
            IsSpellCheckEnabled = false,
        };
        AutomationProperties.SetName(box, "Code");
        var go = new Button { Content = "Empfangen", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetName(go, "Mit Code empfangen");
        var panel = new StackPanel { Spacing = 10, Width = 260 };
        panel.Children.Add(new TextBlock { Text = "Code vom Absender eingeben", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(box);
        panel.Children.Add(go);
        var flyout = Popup(new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom });

        void Start()
        {
            var code = box.Text.Trim();
            if (code.Length == 0)
            {
                return;
            }
            flyout.Hide();
            _ = _nearby.ReceiveByCodeAsync(code);
        }
        go.Click += (_, _) => Start();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Start();
                e.Handled = true;
            }
        };
        flyout.Opened += (_, _) =>
        {
            // Die Notch nimmt sonst keinen Tastaturfokus an (WS_EX_NOACTIVATE); fürs Tippen kurz aktivieren.
            Win32.Activate(_window());
            box.Focus(FocusState.Programmatic);
        };
        flyout.ShowAt(anchor);
    }

    /// <summary>Kacheln neu aufbauen. Fehlende Dateien werden markiert, nicht versteckt.</summary>
    public void Refresh()
    {
        var items = _store.Items;
        _selected.IntersectWith(items.Select(i => i.Id));
        _tiles.Children.Clear();
        foreach (var item in items)
        {
            _tiles.Children.Add(CreateTile(item));
        }

        var empty = items.Count == 0;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _scroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _share.IsEnabled = items.Any(i => i.Exists);
        _clear.IsEnabled = !empty;
        _summary.Text = empty
            ? ""
            : $"{items.Count} {(items.Count == 1 ? "Eintrag" : "Einträge")} · bleiben, bis du sie entfernst · zum Verwenden herausziehen";
    }

    // ───────── Kacheln ─────────

    private Grid CreateTile(ShelfItem item)
    {
        var exists = item.Exists;
        var selected = _selected.Contains(item.Id);
        var tile = new Grid
        {
            Width = TileWidth,
            Padding = new Thickness(6, 10, 6, 8),
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Get(exists ? BrimColors.Graphite : BrimColors.TabTrack),
            BorderBrush = Brushes.Get(selected ? BrimColors.Fog : "#00000000"),
            BorderThickness = new Thickness(1.5),
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            CanDrag = exists,
            Tag = item,
        };
        AutomationProperties.SetName(tile, exists ? item.Name : $"{item.Name}, nicht gefunden");

        var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        stack.Children.Add(Preview(item, exists));
        stack.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = Brushes.Get(exists ? BrimColors.Fog : BrimColors.Slate),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        var detail = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current.Resources[exists ? "NotchMonoFont" : "NotchTextFont"],
            FontSize = 11,
            Foreground = Brushes.Get(exists ? BrimColors.Slate : BrimColors.Warning),
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = exists ? _details.GetValueOrDefault(item.Path, "") : "Nicht gefunden",
        };
        stack.Children.Add(detail);
        tile.Children.Add(stack);
        if (exists && !_details.ContainsKey(item.Path))
        {
            _ = LoadDetailAsync(item, detail);
        }

        tile.Tapped += (_, e) => Select(item, additive: IsCtrlDown());
        tile.DoubleTapped += (_, _) => Open(item);
        tile.RightTapped += (_, e) => ShowMenu(tile, item, e);
        tile.KeyDown += (_, e) => OnTileKey(item, e);
        tile.DragStarting += OnDragStarting;
        tile.DropCompleted += OnDropCompleted;
        return tile;
    }

    private FrameworkElement Preview(ShelfItem item, bool exists)
    {
        if (!exists)
        {
            return new Rectangle
            {
                Width = 34,
                Height = 40,
                RadiusX = 5,
                RadiusY = 5,
                Stroke = Brushes.Get("#5A5D66"),
                StrokeThickness = 1.5,
                StrokeDashArray = [3, 2],
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }
        var image = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        if (_thumbnails.TryGetValue(item.Path, out var cached))
        {
            image.Source = cached;
        }
        else
        {
            _ = LoadThumbnailAsync(item, image);
        }
        return image;
    }

    /// <summary>Vorschaubild wie im Explorer (Windows-Shell über <c>GetThumbnailAsync</c>).</summary>
    private async Task LoadThumbnailAsync(ShelfItem item, Image target)
    {
        try
        {
            StorageItemThumbnail? thumbnail = item.IsFolder
                ? await (await StorageFolder.GetFolderFromPathAsync(item.Path)).GetThumbnailAsync(ThumbnailMode.SingleItem, 64, ThumbnailOptions.UseCurrentScale)
                : await (await StorageFile.GetFileFromPathAsync(item.Path)).GetThumbnailAsync(ThumbnailMode.SingleItem, 64, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null)
            {
                return;
            }
            using (thumbnail)
            {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail);
                _thumbnails[item.Path] = bitmap;
                target.Source = bitmap;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Kein Vorschaubild für {item.Path}", ex);
        }
    }

    private async Task LoadDetailAsync(ShelfItem item, TextBlock target)
    {
        var path = item.Path;
        var text = await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var count = Directory.EnumerateFileSystemEntries(path).Take(1000).Count();
                    return count >= 1000 ? "999+ Elemente" : $"{count} {(count == 1 ? "Element" : "Elemente")}";
                }
                return FormatSize(new FileInfo(path).Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "";
            }
        });
        _details[path] = text;
        target.Text = text;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        var format = unit == 0 || size >= 100 ? "0" : "0.#";
        return size.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }

    // ───────── Auswahl, Öffnen, Menü ─────────

    private static bool IsCtrlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void Select(ShelfItem item, bool additive)
    {
        if (!additive)
        {
            var only = _selected.Count == 1 && _selected.Contains(item.Id);
            _selected.Clear();
            if (!only)
            {
                _selected.Add(item.Id);
            }
        }
        else if (!_selected.Remove(item.Id))
        {
            _selected.Add(item.Id);
        }
        foreach (var tile in _tiles.Children.OfType<Grid>())
        {
            tile.BorderBrush = Brushes.Get(tile.Tag is ShelfItem i && _selected.Contains(i.Id) ? BrimColors.Fog : "#00000000");
        }
    }

    /// <summary>Ausgewählte Einträge, oder nur der angeklickte, wenn er nicht zur Auswahl gehört.</summary>
    private List<ShelfItem> SelectionFor(ShelfItem item)
    {
        var items = _store.Items;
        return _selected.Contains(item.Id) ? items.Where(i => _selected.Contains(i.Id)).ToList() : [item];
    }

    private List<ShelfItem> SelectionOrAll()
    {
        var items = _store.Items;
        var selected = items.Where(i => _selected.Contains(i.Id)).ToList();
        return selected.Count > 0 ? selected : items.ToList();
    }

    private static void Open(ShelfItem item)
    {
        if (!item.Exists)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Konnte {item.Path} nicht öffnen", ex);
        }
    }

    private static void Reveal(ShelfItem item)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
        }
        catch (Exception ex)
        {
            Log.Warn($"Konnte {item.Path} nicht im Explorer zeigen", ex);
        }
    }

    private void ShowMenu(FrameworkElement tile, ShelfItem item, RightTappedRoutedEventArgs e)
    {
        var targets = SelectionFor(item);
        var menu = Popup(new MenuFlyout());
        var open = new MenuFlyoutItem { Text = "Öffnen", IsEnabled = item.Exists };
        open.Click += (_, _) => Open(item);
        var reveal = new MenuFlyoutItem { Text = "Im Explorer anzeigen", IsEnabled = item.Exists };
        reveal.Click += (_, _) => Reveal(item);
        var share = new MenuFlyoutItem { Text = "Teilen …", IsEnabled = targets.Any(t => t.Exists) };
        share.Click += (_, _) => ShowShareMenu(tile, targets);
        var remove = new MenuFlyoutItem { Text = targets.Count > 1 ? $"{targets.Count} Einträge entfernen" : "Aus der Ablage entfernen" };
        remove.Click += (_, _) => _store.Remove(targets.Select(t => t.Id));
        menu.Items.Add(open);
        menu.Items.Add(reveal);
        menu.Items.Add(share);

        // Aktionen von Plugins mit dem Recht „shelf“, z. B. „Hochladen“.
        var paths = targets.Where(t => t.Exists).Select(t => t.Path).ToList();
        var actions = ShelfPluginActions.List();
        if (actions.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var (pluginId, pluginName, actionId, label) in actions)
            {
                var entry = new MenuFlyoutItem { Text = label, IsEnabled = paths.Count > 0 };
                ToolTipService.SetToolTip(entry, pluginName);
                entry.Click += (_, _) => ShelfPluginActions.Invoke(pluginId, actionId, paths);
                menu.Items.Add(entry);
            }
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
        menu.ShowAt(tile, e.GetPosition(tile));
        e.Handled = true;
    }

    private void OnTileKey(ShelfItem item, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Delete:
                _store.Remove(SelectionFor(item).Select(i => i.Id));
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                Open(item);
                e.Handled = true;
                break;
            case VirtualKey.Space:
                Select(item, additive: IsCtrlDown());
                e.Handled = true;
                break;
        }
    }

    // ───────── Herausziehen ─────────

    private async void OnDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ShelfItem item })
        {
            return;
        }
        var deferral = args.GetDeferral();
        try
        {
            var storageItems = await ToStorageItemsAsync(SelectionFor(item));
            if (storageItems.Count == 0)
            {
                args.Cancel = true;
                return;
            }
            args.Data.SetStorageItems(storageItems, readOnly: true);
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.AllowedOperations = DataPackageOperation.Copy;
            args.Data.Properties.Title = storageItems.Count == 1 ? storageItems[0].Name : $"{storageItems.Count} Einträge";
            DraggingOut?.Invoke(true);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        DraggingOut?.Invoke(false);
        if (args.DropResult != DataPackageOperation.None
            && _settings.Current.Shelf.RemoveAfterDragOut
            && sender is FrameworkElement { Tag: ShelfItem item })
        {
            _store.Remove(SelectionFor(item).Select(i => i.Id));
        }
    }

    private static async Task<List<IStorageItem>> ToStorageItemsAsync(IEnumerable<ShelfItem> items)
    {
        var result = new List<IStorageItem>();
        foreach (var item in items.Where(i => i.Exists))
        {
            try
            {
                result.Add(item.IsFolder
                    ? await StorageFolder.GetFolderFromPathAsync(item.Path)
                    : await StorageFile.GetFileFromPathAsync(item.Path));
            }
            catch (Exception ex)
            {
                Log.Warn($"{item.Path} lässt sich nicht übergeben", ex);
            }
        }
        return result;
    }

    // ───────── Teilen ─────────

    /// <summary>
    /// Teilen-Menü (Plan 4.4a): Brims in der Nähe, danach der Windows-Dialog für Mail, Teams usw.
    /// </summary>
    private void ShowShareMenu(FrameworkElement anchor, List<ShelfItem> items)
    {
        var paths = items.Where(i => i.Exists).Select(i => i.Path).ToList();
        var menu = Popup(new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom });
        menu.Items.Add(new MenuFlyoutItem { Text = "Brims in der Nähe", IsEnabled = false });
        var peers = _nearby.Service.Peers;
        if (peers.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "Noch keins gefunden", IsEnabled = false });
        }
        foreach (var peer in peers)
        {
            var item = new MenuFlyoutItem
            {
                Text = peer.Name,
                Icon = new FontIcon { Glyph = Glyphs.Computer, FontFamily = (FontFamily)Application.Current.Resources["NotchIconFont"] },
                IsEnabled = paths.Count > 0,
            };
            item.Click += (_, _) => _ = _nearby.SendAsync(peer, paths);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var byCode = new MenuFlyoutItem
        {
            Text = "Per Code senden …",
            Icon = new FontIcon { Glyph = Glyphs.Link, FontFamily = (FontFamily)Application.Current.Resources["NotchIconFont"] },
            IsEnabled = paths.Count > 0,
        };
        byCode.Click += (_, _) => _ = _nearby.SendByCodeAsync(paths);
        menu.Items.Add(byCode);
        var windows = new MenuFlyoutItem { Text = "Windows-Teilen …", IsEnabled = paths.Count > 0 };
        windows.Click += (_, _) => Share(items);
        menu.Items.Add(windows);
        menu.ShowAt(anchor);
    }

    private async void Share(List<ShelfItem> items)
    {
        var storageItems = await ToStorageItemsAsync(items);
        if (storageItems.Count > 0)
        {
            ShareSheet.Show(_window(), storageItems, storageItems.Count == 1 ? storageItems[0].Name : $"{storageItems.Count} Einträge aus der Ablage");
        }
    }

    // ───────── Bausteine ─────────

    private static FontIcon Icon(string glyph, double size, string color) => new()
    {
        Glyph = glyph,
        FontSize = size,
        FontFamily = (FontFamily)Application.Current.Resources["NotchIconFont"],
        Foreground = Brushes.Get(color),
    };

    private static Button FooterButton(string text, bool filled, RoutedEventHandler click)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["NotchButtonStyle"],
            Height = 32,
            Padding = new Thickness(14, 0, 14, 1),
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Get(filled ? BrimColors.Track : "#00000000"),
            BorderBrush = Brushes.Get("#2E3036"),
            BorderThickness = new Thickness(filled ? 0 : 1),
            Content = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Brushes.Get(filled ? BrimColors.Fog : BrimColors.Slate) },
        };
        button.Click += click;
        return button;
    }
}
