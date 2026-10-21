using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Brim.Plugins;
using Brim.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Graphics;

namespace Brim.Shell;

/// <summary>
/// Einstellungen im Windows-11-Stil (Plan 5): Navigation links. Jede Änderung wird sofort gespeichert; was nur
/// beim Start gelesen wird, ist als solches gekennzeichnet und bietet „Jetzt neu starten“ an. Komplett in Code statt
/// XAML, weil es nur aus Standard-Steuerelementen besteht. Steckt im <see cref="SettingsWindow"/> (folgt Hell/Dunkel
/// des Systems) und, über das Zahnrad, in der Notch (immer dunkel).
/// </summary>
internal sealed class SettingsPage : UserControl
{
    /// <summary>Baut eine Seite mit allem, was die App dafür kennt (von <see cref="App"/> gesetzt).</summary>
    public static Func<Func<nint>, SettingsPage>? Create { get; set; }

    /// <summary>Zeigt die Einführung noch einmal (Info → „Einführung erneut zeigen“).</summary>
    public static Action? ShowOnboarding { get; set; }

    private readonly Func<nint> _window;

    private readonly SettingsStore _store;
    private readonly IReadOnlyList<(string Id, string Name)> _modules;
    private readonly string? _version;
    private readonly Action _preview;
    private readonly Action _restart;
    private readonly Func<IReadOnlyList<PluginManifest>> _plugins;
    private readonly PluginLibrary? _library;
    private readonly NavigationView _navigation = new()
    {
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsSettingsVisible = false,
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        IsPaneToggleButtonVisible = false,
        // Breit genug für „Kalender und Benachrichtigungen“; mit breiterer Schrift bricht der Eintrag um statt abzuschneiden.
        OpenPaneLength = 320,
    };
    private readonly ScrollViewer _page = new() { Padding = new Thickness(32, 24, 32, 32) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>Erscheint, sobald etwas geändert wurde, das erst nach einem Neustart wirkt.</summary>
    private readonly InfoBar _restartBar = new()
    {
        Severity = InfoBarSeverity.Informational,
        Title = "Neustart nötig",
        Message = "Einige Änderungen wirken erst, wenn Brim neu startet.",
        IsClosable = true,
        IsOpen = false,
        Margin = new Thickness(32, 16, 32, 0),
    };

    /// <param name="window">Fenster, in dem die Seite steckt (für die Dateiauswahl).</param>
    public SettingsPage(SettingsStore store, IReadOnlyList<(string Id, string Name)> modules, string? version, Action preview, Action restart,
        Func<IReadOnlyList<PluginManifest>> plugins, PluginLibrary? library, Func<nint> window)
    {
        _window = window;
        _plugins = plugins;
        _library = library;
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
        if (library is not null)
        {
            // Nur abonniert, solange die Seite steht: Die Bibliothek lebt länger als jedes Einstellungsfenster.
            Loaded += (_, _) => library.Changed += OnLibraryChanged;
            Unloaded += (_, _) => library.Changed -= OnLibraryChanged;
        }
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
            _navigation.MenuItems.Add(new NavigationViewItem
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                Tag = tag,
                Icon = new FontIcon { Glyph = glyph },
            });
        }
        _navigation.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem { Tag: string tag })
            {
                ShowSection(tag);
            }
        };
        var restartNow = new Button { Content = "Jetzt neu starten" };
        restartNow.Click += (_, _) =>
        {
            Save();
            _restart();
        };
        _restartBar.ActionButton = restartNow;
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_page, 1);
        content.Children.Add(_restartBar);
        content.Children.Add(_page);
        _navigation.Content = content;
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
        ("identity", "Kalender und Benachrichtigungen", ""),
        ("shelf", "Ablage und Teilen", ""),
        ("games", "Spiele", ""),
        ("plugins", "Plugins", ""),
        ("about", "Info", ""),
    ];

    /// <summary>Springt zu einem Bereich (Tag aus <see cref="Sections"/>, z. B. „plugins“ für die Bibliothek).</summary>
    public void Select(string tag)
    {
        if (_navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag) is { } item)
        {
            _navigation.SelectedItem = item;
        }
    }

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
        _libraryList = null;
        _libraryState = null;
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
            case "appearance":
                Appearance(panel);
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
            [("Nur Hauptmonitor", MonitorMode.Primary), ("Auf allen Monitoren", MonitorMode.All), ("Monitor mit der Maus", MonitorMode.Mouse)],
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

        p.Children.Add(Header("Form und Material"));
        p.Children.Add(Choice("Form", "Die Insel schwebt mit etwas Abstand unter der Kante.",
            [("Notch – wächst aus der Kante", NotchForm.Notch), ("Insel – schwebende Pille", NotchForm.Island),
             ("Kante – rund, ohne Ohren", NotchForm.Edge), ("Tab – eckig wie ein Reiter", NotchForm.Tab)],
            a.Form, v => a.Form = v));
        p.Children.Add(Choice("Material", "Acryl zeigt den Desktop weichgezeichnet, Mica und Mica Alt sind aus dem Hintergrundbild getönt (Windows 11). Braucht etwas mehr Grafikleistung.",
            [("Schwarz", NotchMaterial.Black), ("Acryl", NotchMaterial.Acrylic), ("Mica", NotchMaterial.Mica), ("Mica Alt", NotchMaterial.MicaAlt)],
            a.Material, v => a.Material = v));
        p.Children.Add(Slider("Größe (%)", NotchGeometry.MinSize * 100, NotchGeometry.MaxSize * 100, 5, a.Size * 100,
            v => a.Size = Math.Round(v) / 100));
        var preview = new Button { Content = "Aufklappen" };
        preview.Click += (_, _) =>
        {
            Save();
            _preview();
        };
        p.Children.Add(Card("Vorschau", "Klappt die Notch einmal auf.", preview));

        var d = S.Display;
        p.Children.Add(Header("Uhr"));
        p.Children.Add(Choice("Position", "Wo die Uhrzeit in der ruhenden Notch steht.",
            [("Mittig", ClockAlignment.Center), ("Links", ClockAlignment.Left), ("Rechts", ClockAlignment.Right)],
            d.Clock, v => d.Clock = v));
        p.Children.Add(Toggle("Ganzen Inhalt zentrieren", "Bei mittiger Uhr: Ist eine Seite länger, steht alles zusammen in der Mitte statt nur die Uhr.",
            d.CenterContent, v => d.CenterContent = v));
        p.Children.Add(Toggle("Sekunden anzeigen", null, S.Clock.ShowSeconds, v => S.Clock.ShowSeconds = v));
        p.Children.Add(Slider("Größe der Uhr in der Pille", ClockSettings.MinIdleSize, ClockSettings.MaxIdleSize, 1, S.Clock.ClampedIdleSize,
            v => S.Clock.IdleSize = Math.Round(v)));
        var today = DateTime.Now;
        string Example(DateFormat format) => today.ToString(DateFormats.Pattern(format), CultureInfo.CurrentCulture);
        p.Children.Add(Toggle("Datum", "Neben der Uhr, rechts.", d.Date, v => d.Date = v));
        p.Children.Add(Choice("Datumsformat", null,
            [(Example(DateFormat.WeekdayDay), DateFormat.WeekdayDay), (Example(DateFormat.WeekdayDayMonth), DateFormat.WeekdayDayMonth),
             (Example(DateFormat.DayMonth), DateFormat.DayMonth)],
            d.DateFormat, v => d.DateFormat = v));

        p.Children.Add(Header("Werte neben der Uhr"));
        p.Children.Add(Hint("Systemwerte stehen links der Uhr, Datum und Akku rechts. Wird es zu breit, fällt zuerst das Netzwerk weg, dann CPU und RAM."));
        p.Children.Add(Toggle("CPU und RAM", "Auslastung in Prozent, alle 2 Sekunden, solange die Notch ruht.", d.Load, v => d.Load = v));
        p.Children.Add(Toggle("Netzwerk", "Aktuelle Download- und Uploadrate.", d.Network, v => d.Network = v));
        p.Children.Add(Choice("Einheit", "Bit pro Sekunde zeigt dieselben Werte wie der Task-Manager.",
            [("Bit/s (wie Task-Manager)", NetworkUnit.Bits), ("Byte/s (wie Downloads)", NetworkUnit.Bytes)],
            d.NetworkUnit, v => d.NetworkUnit = v));
        p.Children.Add(Toggle("Akku", "Ladestand (nur mit Akku); farbig, wenn er lädt, fast leer ist oder Energie spart.", d.Battery, v => d.Battery = v));
        p.Children.Add(Choice("Benachrichtigungen", "„Nur Punkt“: Die Uhr bleibt stehen, ein blauer Punkt erscheint, bis du die Notch aufklappst.",
            [("Vorschau zeigen", NotificationStyle.Preview), ("Nur blauer Punkt", NotificationStyle.Dot)],
            S.Notifications.Style, v => S.Notifications.Style = v));

        Fonts(p);
    }

    private static readonly (FontRole Role, string Label, string Description)[] FontRoles =
    [
        (FontRole.Clock, "Uhr", "Uhrzeit in Ruhe und oben rechts, wenn die Notch offen ist."),
        (FontRole.Menu, "Menü", "Die Tabs „Jetzt · Ablage · System · Plugins“."),
        (FontRole.Headings, "Überschriften", "Titel und große Anzeigen wie Datum, Songtitel oder Gesprächsdauer."),
        (FontRole.Text, "Texte", "Untertitel, Hinweise und kleine Beschriftungen."),
        (FontRole.Numbers, "Zahlen", "Messwerte wie FPS, Ping oder Prozent. Eine Monospace-Schrift verhindert springende Ziffern."),
    ];

    /// <summary>Abschnitt in Aussehen: Schrift je Bereich und eigene Schriftdateien.</summary>
    private void Fonts(StackPanel p)
    {
        var fonts = S.Appearance.Fonts;
        var available = FontCatalog.BuiltIn.Concat(fonts.Custom.Select(c => c.Family)).Distinct().ToList();
        (string, string?)[] Options(FontRole role) =>
            [($"Standard ({FontCatalog.DefaultName(role)})", null), .. available.Select(f => (f, (string?)f))];

        p.Children.Add(Header("Schriften"));
        p.Children.Add(Hint("Für Uhr, Menü, Überschriften, Texte und Zahlen je eine eigene Schrift, auch aus eigenen Dateien. Bahnschrift und Cascadia Mono gehören zu Windows 11."));
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
                ShowSection("appearance");
            }
        };
        p.Children.Add(Card("Alle Bereiche", "Setzt eine Schrift überall; danach kannst du einzelne Bereiche ändern.", all));

        foreach (var (role, label, description) in FontRoles)
        {
            p.Children.Add(Choice(label, description, Options(role), fonts.Get(role), v => fonts.Set(role, v)));
        }

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
                ShowSection("appearance");
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
                RequestedTheme = ActualTheme,
            }.ShowAsync();
            return;
        }
        // Neu aufbauen, damit die neue Schrift in jeder Auswahl steht.
        ShowSection("appearance");
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
        p.Children.Add(Toggle("In Präsentationen und bei „Nicht stören“ nur Punkt", "Auch bei Vollbild-Spielen und -Videos erscheint keine Vorschau, nur der blaue Punkt.",
            S.Notifications.QuietWhenBusy, v => S.Notifications.QuietWhenBusy = v));
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
        if (_library is not null)
        {
            Library(p, _library);
            p.Children.Add(Header("Installiert"));
        }
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

    // ───────── Bibliothek ─────────

    private StackPanel? _libraryList;
    private TextBlock? _libraryState;
    private string _libraryQuery = "";
    private bool _libraryPending;

    private void Library(StackPanel p, PluginLibrary library)
    {
        p.Children.Add(Header("Bibliothek"));
        p.Children.Add(Hint("Plugins aus dem Brim-Katalog. Brim prüft jedes Zip gegen die Prüfsumme im Katalog, bevor es installiert wird."));
        var search = new TextBox { PlaceholderText = "Plugins durchsuchen", Text = _libraryQuery, MinWidth = 280 };
        search.TextChanged += (_, _) =>
        {
            _libraryQuery = search.Text.Trim();
            FillLibrary(library);
        };
        var refresh = new Button { Content = new FontIcon { Glyph = "\uE72C", FontSize = 14 } };
        ToolTipService.SetToolTip(refresh, "Katalog neu laden");
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            await library.RefreshAsync();
            refresh.IsEnabled = true;
        };
        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(refresh, 1);
        bar.Children.Add(search);
        bar.Children.Add(refresh);
        p.Children.Add(bar);
        _libraryState = Secondary("");
        _libraryState.FontSize = 12;
        p.Children.Add(_libraryState);
        _libraryList = new StackPanel { Spacing = 6 };
        p.Children.Add(_libraryList);
        p.Children.Add(Toggle("Plugins automatisch aktualisieren", "Im Hintergrund, wenn eine neue Version keine neuen Rechte verlangt.",
            S.Library.AutoUpdate, v => S.Library.AutoUpdate = v));
        FillLibrary(library);
        if (!library.HasCatalog || library.CatalogLoaded < DateTimeOffset.UtcNow.AddMinutes(-10))
        {
            _ = library.RefreshAsync();
        }
    }

    /// <summary>Fortschritt kommt oft und aus anderen Threads: höchstens ein Neuaufbau in der Warteschlange.</summary>
    private void OnLibraryChanged()
    {
        if (_libraryPending || _library is not { } library)
        {
            return;
        }
        _libraryPending = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _libraryPending = false;
                FillLibrary(library);
            }))
        {
            _libraryPending = false;
        }
    }

    private void FillLibrary(PluginLibrary library)
    {
        if (_libraryList is null || _libraryState is null)
        {
            return;
        }
        _libraryState.Text = library.CatalogError is { } error
            ? error + (library.HasCatalog ? " – zeige den zuletzt geladenen Katalog." : ".")
            : library.CatalogLoaded is { } loaded ? $"Katalog geladen {loaded.ToLocalTime():g}" : "Lade Katalog …";
        _libraryList.Children.Clear();
        var items = library.Items()
            .Where(i => _libraryQuery.Length == 0
                || i.Name.Contains(_libraryQuery, StringComparison.CurrentCultureIgnoreCase)
                || i.Entry?.Summary.Contains(_libraryQuery, StringComparison.CurrentCultureIgnoreCase) == true
                || i.Entry?.Categories.Any(c => c.Contains(_libraryQuery, StringComparison.CurrentCultureIgnoreCase)) == true)
            .OrderBy(i => i.Status == LibraryStatus.Local)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var item in items)
        {
            _libraryList.Children.Add(LibraryCard(library, item));
        }
        if (items.Count == 0 && library.HasCatalog)
        {
            _libraryList.Children.Add(Secondary(_libraryQuery.Length > 0 ? "Nichts gefunden." : "Der Katalog ist leer."));
        }
    }

    private static string PermissionText(IEnumerable<string> permissions)
    {
        var names = permissions.Select(p => PluginManifest.KnownPermissions.GetValueOrDefault(p, p)).ToList();
        return names.Count == 0 ? "Braucht keine Rechte" : "Darf: " + string.Join(", ", names);
    }

    private Border LibraryCard(PluginLibrary library, LibraryItem item)
    {
        var entry = item.Entry;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (entry is { Official: true })
        {
            var badge = (Border)XamlReader.Load($"<Border {XamlNamespace} CornerRadius='4' Padding='6,1,6,1' VerticalAlignment='Center' "
                + "Background='{ThemeResource AccentFillColorDefaultBrush}' />");
            var label = (TextBlock)XamlReader.Load($"<TextBlock {XamlNamespace} FontSize='11' Foreground='{{ThemeResource TextOnAccentFillColorPrimaryBrush}}' />");
            label.Text = "Von Brim";
            badge.Child = label;
            title.Children.Add(badge);
        }
        text.Children.Add(title);

        void Line(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var line = Secondary(value);
                line.FontSize = 12;
                text.Children.Add(line);
            }
        }
        Line(entry?.Summary);
        var installed = item.Installed?.Version;
        Line(item.Status switch
        {
            LibraryStatus.Available => $"Version {entry!.Version}" + (entry.Author.Length > 0 ? $" · {entry.Author}" : "") + (entry.Size > 0 ? $" · {entry.Size / (1024.0 * 1024):0.#} MB" : ""),
            LibraryStatus.Installed => $"Installiert · Version {installed}",
            LibraryStatus.UpdateAvailable => $"Installiert {installed} · Update auf {entry!.Version}",
            LibraryStatus.UpdateNeedsConsent => $"Installiert {installed} · Version {entry!.Version} darf zusätzlich: "
                + string.Join(", ", item.NewPermissions.Select(p => PluginManifest.KnownPermissions.GetValueOrDefault(p, p))),
            LibraryStatus.NeedsNewerBrim => $"Version {entry!.Version} braucht Brim {entry.MinBrim}",
            _ => installed is null ? null : $"Version {installed} · " + (library.CanRemove(item) ? "nicht aus der Bibliothek" : "eigener Ordner"),
        });
        Line(PermissionText(entry?.Permissions ?? item.Installed?.Permissions ?? []));
        if (library.Error(item.Id) is { } error)
        {
            var failed = (TextBlock)XamlReader.Load($"<TextBlock {XamlNamespace} TextWrapping='Wrap' FontSize='12' Foreground='{{ThemeResource SystemFillColorCriticalBrush}}' />");
            failed.Text = error;
            text.Children.Add(failed);
        }

        FrameworkElement control;
        if (library.Progress(item.Id) is { } progress)
        {
            control = new ProgressBar { Width = 140, Minimum = 0, Maximum = 1, Value = progress, IsIndeterminate = progress <= 0 };
        }
        else
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (item.Status is LibraryStatus.Available or LibraryStatus.UpdateAvailable or LibraryStatus.UpdateNeedsConsent)
            {
                var install = new Button
                {
                    Content = item.Status == LibraryStatus.Available ? "Installieren" : "Aktualisieren",
                    Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                };
                // Klick = Zustimmung zu den Rechten auf der Karte; das Plugin ist danach eingeschaltet.
                ToolTipService.SetToolTip(install, PermissionText(entry!.Permissions));
                install.Click += (_, _) => _ = library.InstallAsync(item.Id);
                buttons.Children.Add(install);
            }
            if (library.CanRemove(item))
            {
                buttons.Children.Add(RemoveButton(library, item));
            }
            control = buttons;
        }

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        var card = CardBorder();
        card.Child = grid;
        return card;
    }

    /// <summary>„Entfernen“ mit Rückfrage im Flyout (klappt im Fenster und in der Notch).</summary>
    private static Button RemoveButton(PluginLibrary library, LibraryItem item)
    {
        var remove = new Button { Content = "Entfernen" };
        var confirm = new Button { Content = "Entfernen", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var question = new TextBlock { Text = $"{item.Name} und seine Einstellungen entfernen?", TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 12, Children = { question, confirm } } };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            _ = library.RemoveAsync(item.Id);
        };
        remove.Flyout = flyout;
        return remove;
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
        var intro = new Button { Content = "Einführung erneut zeigen" };
        intro.Click += (_, _) => ShowOnboarding?.Invoke();
        p.Children.Add(Card("Einführung", "Intro, Kurzanleitung und Einrichtung wie beim ersten Start.", intro));
    }

    // ───────── Bausteine ─────────

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 18, 0, 4),
    };

    private static TextBlock Hint(string text)
    {
        var hint = Secondary(text);
        hint.Margin = new Thickness(0, 0, 0, 8);
        return hint;
    }

    // Farben über {ThemeResource}: So folgen sie dem Design des Elements (Fenster hell oder dunkel, Notch dunkel).
    // Application.Current.Resources liefert dagegen immer die Farben des App-Designs.
    private const string XamlNamespace = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

    private static TextBlock Secondary(string text)
    {
        var block = (TextBlock)XamlReader.Load($"<TextBlock {XamlNamespace} TextWrapping='Wrap' Foreground='{{ThemeResource TextFillColorSecondaryBrush}}' />");
        block.Text = text;
        return block;
    }

    private static Border CardBorder() => (Border)XamlReader.Load(
        $"<Border {XamlNamespace} Padding='16,12,16,12' CornerRadius='6' BorderThickness='1' MinHeight='64' "
        + "Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' />");

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
            var secondary = Secondary(description);
            secondary.FontSize = 12;
            text.Children.Add(secondary);
        }
        grid.Children.Add(text);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        var card = CardBorder();
        card.Child = grid;
        return card;
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
            if (restart)
            {
                _restartBar.IsOpen = true;
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
