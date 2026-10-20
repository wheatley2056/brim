using System.Diagnostics;
using System.Text.Json;
using Brim.Plugins;
using Brim.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Brim.Shell;

/// <summary>
/// Einstellungen im Windows-11-Stil (Plan 5): Navigation links. Jede Änderung wird sofort gespeichert; was nur
/// beim Start gelesen wird, ist als solches gekennzeichnet. Komplett in Code statt XAML, weil es nur aus
/// Standard-Steuerelementen besteht. Steckt im <see cref="SettingsWindow"/> und, über das Zahnrad, in der Notch.
/// </summary>
internal sealed class SettingsPage : UserControl
{
    /// <summary>Baut eine Seite mit allem, was die App dafür kennt (von <see cref="App"/> gesetzt).</summary>
    public static Func<Func<nint>, SettingsPage>? Create { get; set; }

    private readonly Func<nint> _window;

    private readonly SettingsStore _store;
    private readonly IReadOnlyList<(string Id, string Name)> _modules;
    private readonly string? _version;
    private readonly Action _preview;
    private readonly Action _restart;
    private readonly Func<IReadOnlyList<PluginManifest>> _plugins;
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

    /// <param name="window">Fenster, in dem die Seite steckt (für die Dateiauswahl).</param>
    public SettingsPage(SettingsStore store, IReadOnlyList<(string Id, string Name)> modules, string? version, Action preview, Action restart,
        Func<IReadOnlyList<PluginManifest>> plugins, Func<nint> window)
    {
        _window = window;
        _plugins = plugins;
        _store = store;
        _modules = modules;
        _version = version;
        _preview = preview;
        _restart = restart;

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
        Unloaded += (_, _) =>
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
            var item = new NavigationViewItem { Content = label, Tag = tag, Icon = new FontIcon { Glyph = glyph } };
            if (tag == "appearance")
            {
                // Untermenü: Schriften je Bereich.
                item.MenuItems.Add(new NavigationViewItem { Content = "Schriften", Tag = "fonts", Icon = new FontIcon { Glyph = "\uE8D2" } });
            }
            _navigation.MenuItems.Add(item);
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
        ("appearance", "Aussehen", ""),
        ("animation", "Animation", ""),
        ("modules", "Module", ""),
        // Kurz, damit es auch in der schmaleren Navigation der Notch ganz passt; die Seite heißt ausführlich.
        ("identity", "Kalender & Mitteilungen", ""),
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

    /// <summary>Wechselt zu einem Bereich, auch zu einem im Untermenü.</summary>
    private void Select(string tag)
    {
        foreach (var item in _navigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag)
            {
                _navigation.SelectedItem = item;
                return;
            }
            if (item.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(c => c.Tag as string == tag) is { } child)
            {
                item.IsExpanded = true;
                _navigation.SelectedItem = child;
                return;
            }
        }
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
        var title = tag switch
        {
            "fonts" => "Schriften",
            "identity" => "Kalender und Benachrichtigungen",
            _ => Sections.First(s => s.Tag == tag).Label,
        };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        switch (tag)
        {
            case "general":
                General(panel);
                break;
            case "autohide":
                AutoHide(panel);
                break;
            case "appearance":
                Appearance(panel);
                break;
            case "fonts":
                Fonts(panel);
                break;
            case "animation":
                Animation(panel);
                break;
            case "modules":
                Modules(panel);
                break;
            case "identity":
                IdentitySection(panel);
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

    private void Appearance(StackPanel p)
    {
        var a = S.Appearance;
        p.Children.Add(Hint("Änderungen siehst du sofort an der Notch."));
        p.Children.Add(Choice("Form", "Die Insel schwebt mit etwas Abstand unter der Kante.",
            [("Notch – wächst aus der Kante", NotchForm.Notch), ("Insel – schwebende Pille", NotchForm.Island),
             ("Kante – rund, ohne Ohren", NotchForm.Edge), ("Tab – eckig wie ein Reiter", NotchForm.Tab)],
            a.Form, v => a.Form = v));
        p.Children.Add(Choice("Material", "Acryl zeigt den Desktop weichgezeichnet, Mica und Mica Alt sind aus dem Hintergrundbild getönt (Windows 11). Braucht etwas mehr Grafikleistung.",
            [("Schwarz", NotchMaterial.Black), ("Acryl", NotchMaterial.Acrylic), ("Mica", NotchMaterial.Mica), ("Mica Alt", NotchMaterial.MicaAlt)],
            a.Material, v => a.Material = v));
        var fonts = new Button { Content = "Einstellen" };
        fonts.Click += (_, _) => Select("fonts");
        p.Children.Add(Card("Schriften", "Für Uhr, Menü, Überschriften, Texte und Zahlen je eine eigene Schrift, auch aus eigenen Dateien.", fonts));
        p.Children.Add(Slider("Größe (%)", NotchGeometry.MinSize * 100, NotchGeometry.MaxSize * 100, 5, a.Size * 100,
            v => a.Size = Math.Round(v) / 100));
        var preview = new Button { Content = "Aufklappen" };
        preview.Click += (_, _) =>
        {
            Save();
            _preview();
        };
        p.Children.Add(Card("Vorschau", "Klappt die Notch einmal auf.", preview));
    }

    private static readonly (FontRole Role, string Label, string Description)[] FontRoles =
    [
        (FontRole.Clock, "Uhr", "Uhrzeit in Ruhe und oben rechts, wenn die Notch offen ist."),
        (FontRole.Menu, "Menü", "Die Tabs „Jetzt · Ablage · System · Plugins“."),
        (FontRole.Headings, "Überschriften", "Titel und große Anzeigen wie Datum, Songtitel oder Gesprächsdauer."),
        (FontRole.Text, "Texte", "Untertitel, Hinweise und kleine Beschriftungen."),
        (FontRole.Numbers, "Zahlen", "Messwerte wie FPS, Ping oder Prozent. Eine Monospace-Schrift verhindert springende Ziffern."),
    ];

    /// <summary>Untermenü von Aussehen: Schrift je Bereich und eigene Schriftdateien.</summary>
    private void Fonts(StackPanel p)
    {
        var fonts = S.Appearance.Fonts;
        var available = FontCatalog.BuiltIn.Concat(fonts.Custom.Select(c => c.Family)).Distinct().ToList();
        (string, string?)[] Options(FontRole role) =>
            [($"Standard ({FontCatalog.DefaultName(role)})", null), .. available.Select(f => (f, (string?)f))];

        p.Children.Add(Hint("Änderungen siehst du sofort an der Notch. Bahnschrift und Cascadia Mono gehören zu Windows 11."));
        var all = new ComboBox { MinWidth = 220, PlaceholderText = "Schrift wählen" };
        foreach (var font in available)
        {
            all.Items.Add(font);
        }
        all.SelectionChanged += (_, _) =>
        {
            if (all.SelectedItem is string font)
            {
                foreach (var (role, _, _) in FontRoles)
                {
                    fonts.Set(role, font);
                }
                Changed();
                ShowSection("fonts");
            }
        };
        p.Children.Add(Card("Alle Bereiche", "Setzt eine Schrift überall; danach kannst du einzelne Bereiche ändern.", all));

        p.Children.Add(Header("Je Bereich"));
        foreach (var (role, label, description) in FontRoles)
        {
            p.Children.Add(Choice(label, description, Options(role), fonts.Get(role), v => fonts.Set(role, v)));
        }

        p.Children.Add(Header("Eigene Schriften"));
        var upload = new Button { Content = "Schriftdatei hinzufügen …" };
        upload.Click += async (_, _) => await ImportFontAsync();
        p.Children.Add(Card("Hinzufügen", "Eine .ttf-, .otf- oder .ttc-Datei. Brim kopiert sie in seinen Datenordner; installieren musst du nichts.", upload));
        foreach (var custom in fonts.Custom.ToList())
        {
            var remove = new Button { Content = "Entfernen" };
            remove.Click += (_, _) =>
            {
                fonts.Custom.Remove(custom);
                foreach (var (role, _, _) in FontRoles)
                {
                    if (fonts.Get(role) == custom.Family)
                    {
                        fonts.Set(role, null);
                    }
                }
                Changed();
                ShowSection("fonts");
            };
            p.Children.Add(Card(custom.Family, Path.GetFileName(custom.File), remove));
        }
    }

    private async Task ImportFontAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _window());
        foreach (var extension in FontFile.Extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }
        if (await picker.PickSingleFileAsync() is not { } file)
        {
            return;
        }
        try
        {
            var (path, family) = NotchFonts.Import(file.Path);
            var custom = S.Appearance.Fonts.Custom;
            custom.RemoveAll(c => c.Family == family);
            custom.Add(new CustomFont(path, family));
            Changed();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("Schrift nicht übernommen", ex);
            await new ContentDialog
            {
                Title = "Schrift nicht übernommen",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
            }.ShowAsync();
            return;
        }
        // Neu aufbauen, damit die neue Schrift in jeder Auswahl steht.
        ShowSection("fonts");
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

    /// <summary>Einrichtung der Paketidentität (Plan 7f): einmal Adminrechte, dann registrieren, dann neu starten.</summary>
    private void IdentitySection(StackPanel p)
    {
        p.Children.Add(Hint("Für den nächsten Termin aus dem Windows-Kalender und die Vorschau neuer Benachrichtigungen braucht Brim eine Paketidentität. "
            + "Dafür vertraut dein PC einmalig dem Zertifikat, mit dem Brim dieses kleine Paket signiert (Windows fragt nach Adminrechten). Brim selbst bleibt, wie es ist."));

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var action = new Button { Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        void Update(string? message = null)
        {
            if (PackageIdentity.HasIdentity)
            {
                status.Text = message ?? "Eingerichtet.";
                action.Visibility = Visibility.Collapsed;
            }
            else if (PackageIdentity.RegisteredElsewhere && PackageIdentity.Available)
            {
                status.Text = message ?? $"Eingerichtet für einen anderen Brim-Ordner ({PackageIdentity.RegisteredLocation}). Für dieses Brim neu einrichten.";
                action.Content = "Neu einrichten";
                action.Visibility = Visibility.Visible;
            }
            else if (PackageIdentity.IsRegistered && !PackageIdentity.RegisteredElsewhere)
            {
                status.Text = message ?? "Eingerichtet – wirkt nach einem Neustart von Brim.";
                action.Content = "Brim neu starten";
                action.Visibility = Visibility.Visible;
            }
            else if (!PackageIdentity.Available)
            {
                status.Text = "Nur in der installierten Fassung möglich (nicht beim Start aus dem Quellcode).";
                action.Visibility = Visibility.Collapsed;
            }
            else
            {
                status.Text = message ?? "Noch nicht eingerichtet.";
                action.Content = "Einrichten";
                action.Visibility = Visibility.Visible;
            }
        }
        action.Click += async (_, _) =>
        {
            var elsewhere = PackageIdentity.RegisteredElsewhere;
            if (PackageIdentity.IsRegistered && !elsewhere)
            {
                _restart();
                return;
            }
            action.IsEnabled = false;
            status.Text = "Wird eingerichtet …";
            var error = elsewhere ? await PackageIdentity.ReinstallAsync() : await PackageIdentity.SetupAsync();
            action.IsEnabled = true;
            Update(error is null ? null : "Hat nicht geklappt: " + error);
        };
        Update();
        p.Children.Add(Card("Status", null, new StackPanel { Spacing = 8, Children = { status, action } }));

        // Diagnose: zeigt, woran es hängt, und lässt sich für eine Fehlermeldung kopieren.
        var diagnosis = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12, Text = PackageIdentity.Diagnose() };
        var copy = new Button { Content = "Kopieren" };
        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(diagnosis.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        };
        var refresh = new Button { Content = "Aktualisieren" };
        refresh.Click += (_, _) =>
        {
            diagnosis.Text = PackageIdentity.Diagnose();
            Update();
        };
        p.Children.Add(Card("Diagnose", null, new StackPanel
        {
            Spacing = 8,
            Children = { diagnosis, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, refresh } } },
        }));

        p.Children.Add(Header("Benachrichtigungen"));
        p.Children.Add(Hint("Windows fragt beim ersten Start nach dem Zugriff. Zurücknehmen kannst du ihn unter Datenschutz → Benachrichtigungen."));
        p.Children.Add(Text("Stumm schalten", "Apps, deren Benachrichtigungen die Notch nicht zeigt, durch Komma getrennt (Name wie im Info-Center).",
            string.Join(", ", S.Notifications.MutedApps),
            v => S.Notifications.MutedApps = [.. v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]));
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
        // Einstellungen, die Plugins im Manifest anbieten; Änderungen gehen sofort als settings.changed an das Plugin.
        foreach (var manifest in _plugins().Where(m => m.Settings.Count > 0))
        {
            p.Children.Add(Header(manifest.Name));
            if (!S.Plugins.TryGetValue(manifest.Id, out var config))
            {
                config = new PluginConfig();
                S.Plugins[manifest.Id] = config;
            }
            var values = manifest.EffectiveSettings(config.Values);
            foreach (var setting in manifest.Settings)
            {
                var label = setting.Label ?? setting.Key;
                values.TryGetValue(setting.Key, out var current);
                if (setting.Type == "bool")
                {
                    p.Children.Add(Toggle(label, null, current.ValueKind == JsonValueKind.True,
                        on => config.Values[setting.Key] = JsonSerializer.SerializeToElement(on)));
                }
                else
                {
                    var text = current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : current.ValueKind == JsonValueKind.Undefined ? "" : current.ToString();
                    p.Children.Add(Text(label, null, text, v => config.Values[setting.Key] = JsonSerializer.SerializeToElement(v)));
                }
            }
        }

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
