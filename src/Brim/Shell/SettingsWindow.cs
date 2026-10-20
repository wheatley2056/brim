using System.Diagnostics;
using Brim.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Brim.Shell;

/// <summary>
/// Einstellungsfenster im Windows-11-Stil (Plan 5): Navigation links, Mica. Jede Änderung wird sofort
/// gespeichert; was nur beim Start gelesen wird, ist als solches gekennzeichnet. Komplett in Code statt XAML,
/// weil es nur aus Standard-Steuerelementen besteht.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private readonly SettingsStore _store;
    private readonly IReadOnlyList<(string Id, string Name)> _modules;
    private readonly string? _version;
    private readonly Action _preview;
    private readonly NavigationView _navigation = new()
    {
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsSettingsVisible = false,
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        IsPaneToggleButtonVisible = false,
        OpenPaneLength = 220,
    };
    private readonly ScrollViewer _page = new() { Padding = new Thickness(32, 24, 32, 32) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>Öffnet das Fenster oder holt das offene nach vorn.</summary>
    public static void Show(SettingsStore store, IReadOnlyList<(string Id, string Name)> modules, string? version, Action preview)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new SettingsWindow(store, modules, version, preview);
        _open.Closed += (_, _) => _open = null;
        _open.Activate();
    }

    private SettingsWindow(SettingsStore store, IReadOnlyList<(string Id, string Name)> modules, string? version, Action preview)
    {
        _store = store;
        _modules = modules;
        _version = version;
        _preview = preview;
        Title = "Brim – Einstellungen";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(980, 720));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Brim.ico"));

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
        Closed += (_, _) =>
        {
            // Nichts verlieren, was in den letzten Millisekunden geändert wurde.
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                Save();
            }
        };

        foreach (var (tag, label, glyph) in Sections)
        {
            _navigation.MenuItems.Add(new NavigationViewItem { Content = label, Tag = tag, Icon = new FontIcon { Glyph = glyph } });
        }
        _navigation.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem { Tag: string tag })
            {
                ShowSection(tag);
            }
        };
        _navigation.Content = _page;
        Content = _navigation;
        _navigation.SelectedItem = _navigation.MenuItems[0];
    }

    private static readonly (string Tag, string Label, string Glyph)[] Sections =
    [
        ("general", "Allgemein", ""),
        ("autohide", "Ausblenden", ""),
        ("animation", "Animation", ""),
        ("modules", "Module", ""),
        ("shelf", "Ablage und Teilen", ""),
        ("games", "Spiele", ""),
        ("plugins", "Plugins", ""),
        ("about", "Info", ""),
    ];

    private BrimSettings S => _store.Current;

    private void Changed()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        try
        {
            _store.Save(_store.Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Einstellungen nicht gespeichert", ex);
        }
    }

    // ───────── Seiten ─────────

    private void ShowSection(string tag)
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        var title = Sections.First(s => s.Tag == tag).Label;
        panel.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        switch (tag)
        {
            case "general":
                General(panel);
                break;
            case "autohide":
                AutoHide(panel);
                break;
            case "animation":
                Animation(panel);
                break;
            case "modules":
                Modules(panel);
                break;
            case "shelf":
                ShelfAndShare(panel);
                break;
            case "games":
                Games(panel);
                break;
            case "plugins":
                Plugins(panel);
                break;
            default:
                About(panel);
                break;
        }
        _page.Content = panel;
    }

    private void General(StackPanel p)
    {
        p.Children.Add(Choice("Monitore", "Wo die Notch erscheint.",
            [("Nur Hauptmonitor", MonitorMode.Primary), ("Auf allen Monitoren", MonitorMode.All)],
            S.Monitors, v => S.Monitors = v));
        p.Children.Add(Toggle("Mit Windows starten", "Gilt für die installierte Fassung.", Autostart.IsEnabled, on =>
        {
            if (on)
            {
                Autostart.Enable();
            }
            else
            {
                Autostart.Disable();
            }
        }, save: false));
        p.Children.Add(Toggle("Tastenkürzel", "Win+Alt+N klappt die Notch auf, Win+Alt+F zeigt im Spiel die FPS.",
            S.Interaction.HotkeyEnabled, v => S.Interaction.HotkeyEnabled = v));
        p.Children.Add(Number("Aufklappen nach (ms)", "So lange muss die Maus auf der Notch ruhen.", 0, 2000, 50,
            S.Interaction.HoverExpandDelayMs, v => S.Interaction.HoverExpandDelayMs = v));
        p.Children.Add(Number("Zuklappen nach (ms)", "Verzögerung, nachdem die Maus die Notch verlassen hat.", 0, 2000, 50,
            S.Interaction.HoverCollapseDelayMs, v => S.Interaction.HoverCollapseDelayMs = v));
        p.Children.Add(Toggle("Sekunden anzeigen", "Uhr im Ruhezustand mit Sekunden.", S.Clock.ShowSeconds, v => S.Clock.ShowSeconds = v, restart: true));
    }

    private void AutoHide(StackPanel p)
    {
        var a = S.AutoHide;
        p.Children.Add(Header("Wann sich die Notch zurückzieht"));
        p.Children.Add(Toggle("Vollbild", "Spiele, Videos, F11, Präsentationen.", a.WhenFullscreen, v => a.WhenFullscreen = v));
        p.Children.Add(Toggle("Maximierte Fenster", "Ein maximiertes Fenster liegt unter der Notch.", a.WhenMaximized, v => a.WhenMaximized = v));
        p.Children.Add(Toggle("Andere Fenster im Bereich", "Ein normales Fenster wurde unter die Notch geschoben.", a.WhenOverlapped, v => a.WhenOverlapped = v));
        p.Children.Add(Toggle("Nur auf dem Desktop zeigen", "Sobald irgendein Fenster im Vordergrund ist.", a.WhenAnyForeground, v => a.WhenAnyForeground = v));
        p.Children.Add(Toggle("Bildschirmfreigabe", "Solange du in Teams, Zoom, Meet oder Webex deinen Bildschirm teilst.", a.WhenScreenSharing, v => a.WhenScreenSharing = v));
        p.Children.Add(Toggle("Fenster zur Snap-Leiste ziehen", "Macht oben Platz für die Snap-Layouts.", a.WhileDraggingWindows, v => a.WhileDraggingWindows = v));
        p.Children.Add(Number("Bei Leerlauf nach (s)", "0 = aus.", 0, 3600, 10, a.IdleSeconds, v => a.IdleSeconds = v));
        p.Children.Add(Number("Verzögerung (ms)", "Wartet vor dem Zurückziehen; zurück kommt sie immer sofort.", 0, 2000, 50, a.HideDelayMs, v => a.HideDelayMs = v));

        p.Children.Add(Header("Zurückholen"));
        p.Children.Add(Toggle("Maus an die Oberkante", "Holt die versteckte Notch zurück.", a.PeekFromTopEdge, v => a.PeekFromTopEdge = v));
        p.Children.Add(Number("Verweilzeit (ms)", "So lange muss die Maus oben ruhen.", 0, 2000, 50, a.PeekDelayMs, v => a.PeekDelayMs = v));

        p.Children.Add(Header("Regeln pro App"));
        p.Children.Add(Hint("Prozessname wie im Task-Manager unter „Details“, ohne „.exe“, z. B. obs64 oder Teams."));
        var list = new StackPanel { Spacing = 6 };
        void Refresh()
        {
            list.Children.Clear();
            foreach (var rule in a.Apps.ToList())
            {
                var remove = new Button { Content = "Entfernen" };
                remove.Click += (_, _) =>
                {
                    a.Apps.Remove(rule);
                    Changed();
                    Refresh();
                };
                list.Children.Add(Card(rule.Process, rule.Visibility == AppVisibility.AlwaysShow ? "Notch immer zeigen" : "Notch immer ausblenden", remove));
            }
        }
        Refresh();
        p.Children.Add(list);
        var name = new TextBox { PlaceholderText = "Prozessname", Width = 220 };
        var mode = new ComboBox { Items = { "Immer zeigen", "Immer ausblenden" }, SelectedIndex = 0, Width = 180 };
        var add = new Button { Content = "Hinzufügen", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        add.Click += (_, _) =>
        {
            var process = name.Text.Trim();
            if (process.Length == 0)
            {
                return;
            }
            a.Apps.RemoveAll(r => string.Equals(r.Process, process, StringComparison.OrdinalIgnoreCase));
            a.Apps.Add(new AppRule { Process = process, Visibility = mode.SelectedIndex == 0 ? AppVisibility.AlwaysShow : AppVisibility.AlwaysHide });
            name.Text = "";
            Changed();
            Refresh();
        };
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { name, mode, add } });
    }

    private void Animation(StackPanel p)
    {
        var a = S.Animation;
        p.Children.Add(Choice("Stil", "Bei „Animationseffekte aus“ in Windows blendet Brim immer nur über.",
            [("Feder", AnimationStyle.Spring), ("Ease-Out", AnimationStyle.EaseOut), ("Überblenden", AnimationStyle.Fade), ("Keine", AnimationStyle.None)],
            a.Style, v => a.Style = v));
        p.Children.Add(Slider("Dauer Ausdehnen (ms)", 150, 800, 10, a.ExpandMs, v => a.ExpandMs = (int)v));
        p.Children.Add(Slider("Dauer Zurückziehen (ms)", 120, 600, 10, a.RetractMs, v => a.RetractMs = (int)v));
        p.Children.Add(Slider("Dämpfung", 0.5, 1.0, 0.05, a.Damping, v => a.Damping = Math.Round(v, 2)));
        var preview = new Button { Content = "Vorschau" };
        preview.Click += (_, _) =>
        {
            Save();
            _preview();
        };
        p.Children.Add(Card("Vorschau", "Klappt die Notch einmal auf.", preview));
    }

    private void Modules(StackPanel p)
    {
        p.Children.Add(Hint("Abgeschaltete Module laufen nach dem nächsten Start von Brim nicht mehr."));
        foreach (var (id, name) in _modules)
        {
            p.Children.Add(Toggle(name, null, !S.DisabledModules.Contains(id), on =>
            {
                S.DisabledModules.Remove(id);
                if (!on)
                {
                    S.DisabledModules.Add(id);
                }
            }, restart: true));
        }
        p.Children.Add(Header("Akku"));
        p.Children.Add(Toggle("Beim Laden kompakt zeigen", null, S.Battery.CompactWhileCharging, v => S.Battery.CompactWhileCharging = v));
        p.Children.Add(Number("Warnung ab (%)", "Kurzer Hinweis und kompakte Anzeige.", 5, 50, 1, S.Battery.LowPercent, v => S.Battery.LowPercent = v));
        p.Children.Add(Number("Kritisch ab (%)", "Die Notch klappt kurz auf.", 1, 30, 1, S.Battery.CriticalPercent, v => S.Battery.CriticalPercent = v));
    }

    private void ShelfAndShare(StackPanel p)
    {
        p.Children.Add(Choice("Dateien in der Ablage", "Aus temporären Ordnern (Downloads, Anhänge) kopiert Brim immer.",
            [("Nur merken, wo sie liegen", ShelfStorage.Reference), ("Kopie in der Ablage", ShelfStorage.Copy)],
            S.Shelf.Storage, v => S.Shelf.Storage = v));
        p.Children.Add(Toggle("Nach dem Herausziehen entfernen", null, S.Shelf.RemoveAfterDragOut, v => S.Shelf.RemoveAfterDragOut = v));
        p.Children.Add(Header("Teilen per Code"));
        p.Children.Add(Choice("Proxy", null,
            [("Wie Windows", ProxyMode.System), ("Direkt", ProxyMode.None), ("Eigener", ProxyMode.Manual)],
            S.Share.Proxy, v => S.Share.Proxy = v));
        p.Children.Add(Text("Eigener Proxy", "http://host:port oder socks5://host:port", S.Share.ProxyUrl ?? "", v => S.Share.ProxyUrl = v.Length == 0 ? null : v));
        p.Children.Add(Text("Vermittlungsserver", null, S.Share.MailboxUrl, v => S.Share.MailboxUrl = v));
        p.Children.Add(Text("Weiterleitung", null, S.Share.TransitRelay, v => S.Share.TransitRelay = v));
    }

    private void Games(StackPanel p)
    {
        var g = S.Games;
        p.Children.Add(Toggle("FPS messen", "Startet den Messdienst. Beim ersten Mal fragt Brim einmal nach Adminrechten.", g.Enabled, v => g.Enabled = v, restart: true));
        p.Children.Add(Choice("Im Spiel", "Exklusives Vollbild älterer Spiele lässt keine Anzeige darüber zu.",
            [("Notch verschwindet", GameOverlay.Off), ("Kleine Pille mit FPS · ms", GameOverlay.Mini), ("Bei Bedarf (Win+Alt+F)", GameOverlay.OnDemand)],
            g.Overlay, v => g.Overlay = v));
        p.Children.Add(Text("Ping zu", "Spielserver für die Netzwerklatenz, leer = aus.", g.PingHost, v => g.PingHost = v));
    }

    private void Plugins(StackPanel p)
    {
        p.Children.Add(Hint("Plugins schaltest du im Tab „Plugins“ der Notch ein und aus; dort steht auch, was sie dürfen."));
        var folder = Path.Combine(App.DataDirectory, "Plugins");
        var open = new Button { Content = "Ordner öffnen" };
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(folder);
            Open(folder);
        };
        p.Children.Add(Card("Plugin-Ordner", folder, open));
        var docs = new HyperlinkButton { Content = "Plugins entwickeln", NavigateUri = new Uri(AutoUpdater.RepositoryUrl + "/blob/main/docs/plugins.md") };
        p.Children.Add(docs);
    }

    private void About(StackPanel p)
    {
        p.Children.Add(Card("Brim", _version is null ? "Entwicklungsfassung" : $"Version {_version}"));
        p.Children.Add(Toggle("Automatisch aktualisieren", "Unbemerkt, wenn du gerade nichts tust.", S.Updates.Enabled, v => S.Updates.Enabled = v));
        p.Children.Add(Number("Nach Updates sehen alle (h)", null, 1, 48, 1, S.Updates.CheckHours, v => S.Updates.CheckHours = v));
        var json = new Button { Content = "settings.json öffnen" };
        json.Click += (_, _) => Open(Path.Combine(App.DataDirectory, "settings.json"));
        var logs = new Button { Content = "Protokolle" };
        logs.Click += (_, _) => Open(Path.Combine(App.DataDirectory, "logs"));
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { json, logs } });
        p.Children.Add(new HyperlinkButton { Content = "Brim auf GitHub", NavigateUri = new Uri(AutoUpdater.RepositoryUrl) });
    }

    // ───────── Bausteine ─────────

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 18, 0, 4),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        Margin = new Thickness(0, 0, 0, 8),
    };

    /// <summary>Zeile wie in den Windows-Einstellungen: Titel und Beschreibung links, Steuerelement rechts.</summary>
    private static Border Card(string title, string? description, FrameworkElement? control = null)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description is not null)
        {
            text.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        grid.Children.Add(text);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        return new Border
        {
            Child = grid,
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            MinHeight = 64,
        };
    }

    private const string RestartNote = " Wirkt nach dem nächsten Start von Brim.";

    private Border Toggle(string title, string? description, bool value, Action<bool> set, bool save = true, bool restart = false)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "Ein", OffContent = "Aus", MinWidth = 0 };
        toggle.Toggled += (_, _) =>
        {
            set(toggle.IsOn);
            if (save)
            {
                Changed();
            }
        };
        return Card(title, restart ? (description ?? "").TrimEnd() + RestartNote : description, toggle);
    }

    private Border Number(string title, string? description, int min, int max, int step, int value, Action<int> set)
    {
        var box = new NumberBox
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            Value = value,
            Width = 140,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        box.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue))
            {
                set((int)Math.Clamp(Math.Round(e.NewValue), min, max));
                Changed();
            }
        };
        return Card(title, description, box);
    }

    private Border Slider(string title, double min, double max, double step, double value, Action<double> set)
    {
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value, Width = 260 };
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            Changed();
        };
        return Card(title, null, slider);
    }

    private Border Choice<T>(string title, string? description, (string Label, T Value)[] options, T value, Action<T> set)
    {
        var combo = new ComboBox { MinWidth = 220 };
        foreach (var (label, _) in options)
        {
            combo.Items.Add(label);
        }
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => EqualityComparer<T>.Default.Equals(o.Value, value)));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                set(options[combo.SelectedIndex].Value);
                Changed();
            }
        };
        return Card(title, description, combo);
    }

    private Border Text(string title, string? description, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, Width = 300 };
        box.TextChanged += (_, _) =>
        {
            set(box.Text.Trim());
            Changed();
        };
        return Card(title, description, box);
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Konnte {path} nicht öffnen", ex);
        }
    }
}
