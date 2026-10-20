using Brim.Core;
using CommunityToolkit.WinUI.Lottie;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Brim.Shell;

/// <summary>
/// Einführung beim ersten Start (Plan 8a): Die Notch wird aus der Bildschirmkante „geboren“ (Lichtlinie,
/// Glühen, Funken, Tropfen mit Nachfedern), wächst dann auf und zeigt vier Schritte – Willkommen, Kurzanleitung
/// mit Lottie-Animationen, Kalender und Benachrichtigungen einrichten, Fertig.
/// </summary>
public sealed partial class NotchView
{
    private static readonly TimeSpan DropAt = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan GrowAt = TimeSpan.FromMilliseconds(1700);

    private Grid? _onboardingLayer;
    private ContentPresenter? _onboardingStep;
    private StackPanel? _onboardingDots;
    private int _onboardingIndex;
    private bool _birth;
    private bool _onboarding;

    /// <summary>Die Geburt ist vorbei: Das Fenster klappt auf, die Schritte erscheinen.</summary>
    public event Action? BirthFinished;

    /// <summary>Einführung beendet; <c>true</c>, wenn Brim für Kalender und Benachrichtigungen neu starten soll.</summary>
    public event Action<bool>? OnboardingFinished;

    public bool IsOnboarding => _onboarding || _birth;

    /// <summary>Spielt die Geburt und zeigt danach die Einführung.</summary>
    public void StartOnboarding(AnimationSettings animation, bool reducedMotion)
    {
        SetSettingsOpen(false);
        _board?.SetEditing(false);
        EnsureOnboardingLayer();
        _onboardingIndex = 0;
        ShowOnboardingStep();

        if (reducedMotion || animation.Style is AnimationStyle.None or AnimationStyle.Fade)
        {
            _onboarding = true;
            BirthFinished?.Invoke();
            return;
        }

        // Aus dem Nichts: zur feinen Linie an der Kante, ohne Inhalte und ohne Uhr.
        _birth = true;
        FadeClock(false);
        _surface.MorphTo(NotchGeometry.For(NotchMode.Hidden, _form), new AnimationSettings { Style = AnimationStyle.None }, reducedMotion: false);
        _surface.SetVisible(true, TimeSpan.Zero);
        SwitchLayer(null, animation, reducedMotion, growing: true);
        _surface.SetAccent(BrimColors.DefaultAccent, visible: true);
        _surface.PlayBirth(BrimColors.DefaultAccent, DropAt);

        After(DropAt, () =>
        {
            // Der Tropfen: aus der Kante heraus mit deutlichem Nachfedern.
            LastMorphDuration = _surface.MorphTo(NotchGeometry.For(NotchMode.Compact, _form), animation, reducedMotion: false, new Spring(0.55, 0.42));
        });
        After(GrowAt, () =>
        {
            _birth = false;
            _onboarding = true;
            BirthFinished?.Invoke();
        });
    }

    private void After(TimeSpan delay, Action action)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => action();
        timer.Start();
    }

    private void FinishOnboarding()
    {
        if (!_onboarding)
        {
            return;
        }
        _onboarding = false;
        // Neu starten, wenn die Paketidentität gerade eingerichtet wurde (wirkt erst im neuen Prozess).
        OnboardingFinished?.Invoke(PackageIdentity.IsRegistered && !PackageIdentity.HasIdentity);
    }

    private void EnsureOnboardingLayer()
    {
        if (_onboardingLayer is not null)
        {
            return;
        }
        _onboardingLayer = new Grid
        {
            Width = NotchGeometry.Settings.Width,
            Height = NotchGeometry.Settings.Height,
            Padding = new Thickness(36, 28, 36, 24),
            RowSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            RequestedTheme = ElementTheme.Dark,
        };
        _onboardingLayer.RowDefinitions.Add(new RowDefinition());
        _onboardingLayer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _onboardingStep = new ContentPresenter { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        _onboardingLayer.Children.Add(_onboardingStep);
        _onboardingDots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetRow(_onboardingDots, 1);
        _onboardingLayer.Children.Add(_onboardingDots);
        ContentHost.Children.Add(_onboardingLayer);
        _layers = [.. _layers, _onboardingLayer];
    }

    private const int OnboardingSteps = 4;

    private void ShowOnboardingStep()
    {
        if (_onboardingStep is null || _onboardingDots is null)
        {
            return;
        }
        _onboardingStep.Content = _onboardingIndex switch
        {
            0 => WelcomeStep(),
            1 => GuideStep(),
            2 => IdentityStep(),
            _ => DoneStep(),
        };
        _onboardingDots.Children.Clear();
        for (var i = 0; i < OnboardingSteps; i++)
        {
            _onboardingDots.Children.Add(new Border
            {
                Width = i == _onboardingIndex ? 18 : 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Get(i == _onboardingIndex ? BrimColors.Fog : BrimColors.Track),
            });
        }
    }

    private void Go(int step)
    {
        _onboardingIndex = Math.Clamp(step, 0, OnboardingSteps - 1);
        ShowOnboardingStep();
    }

    // ───────── Schritte ─────────

    private UIElement WelcomeStep()
    {
        var panel = Column(HorizontalAlignment.Center, 14);
        panel.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Brim.png"))),
            Width = 72,
            Height = 72,
        });
        panel.Children.Add(Text("Willkommen bei Brim", 30, FontWeights.SemiBold, BrimColors.Fog, center: true));
        panel.Children.Add(Text("Deine Notch für Windows: Musik, Anrufe, Termine, Dateien und dein System – immer oben in der Mitte, " +
            "und nur, wenn du sie brauchst. Drei kurze Schritte, dann geht es los.", 14, FontWeights.Normal, BrimColors.Slate, center: true));
        panel.Children.Add(Buttons(("Los geht's", () => Go(1), true)));
        return panel;
    }

    private UIElement GuideStep()
    {
        var grid = new Grid { RowSpacing = 14 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Text("So funktioniert's", 22, FontWeights.SemiBold, BrimColors.Fog));

        var columns = new Grid { ColumnSpacing = 16 };
        Grid.SetRow(columns, 1);
        var tips = new[]
        {
            ("hover", "Aufklappen", "Maus auf die Notch – sie öffnet sich. Oder Win + Alt + N."),
            ("shelf", "Ablage", "Dateien auf die Notch ziehen; sie bleiben griffbereit, bis du sie wieder herausziehst."),
            ("customize", "„Jetzt“ gestalten", "Lange drücken oder Rechtsklick: Uhren, Musik und Widgets anordnen."),
        };
        for (var i = 0; i < tips.Length; i++)
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            var (animation, title, text) = tips[i];
            var card = Column(HorizontalAlignment.Stretch, 8);
            card.Padding = new Thickness(10);
            card.CornerRadius = new CornerRadius(16);
            card.Background = Brushes.Get(BrimColors.Graphite);
            card.Children.Add(Lottie(animation));
            card.Children.Add(Text(title, 15, FontWeights.SemiBold, BrimColors.Fog));
            card.Children.Add(Text(text, 12, FontWeights.Normal, BrimColors.Slate));
            Grid.SetColumn(card, i);
            columns.Children.Add(card);
        }
        grid.Children.Add(columns);

        var buttons = Buttons(("Zurück", () => Go(0), false), ("Weiter", () => Go(2), true));
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        return grid;
    }

    private UIElement IdentityStep()
    {
        var panel = Column(HorizontalAlignment.Stretch, 12);
        panel.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(Text("Kalender und Benachrichtigungen", 22, FontWeights.SemiBold, BrimColors.Fog));
        panel.Children.Add(Text("Damit Brim deinen nächsten Termin zeigt und neue Windows-Benachrichtigungen kurz in der Notch erscheinen, " +
            "braucht es eine kleine Paketidentität. Windows fragt dafür einmal nach Adminrechten; Brim selbst bleibt, wie es ist.",
            14, FontWeights.Normal, BrimColors.Slate));

        var status = Text("", 13, FontWeights.Normal, BrimColors.Fog);
        panel.Children.Add(status);
        var setup = new Button { Content = "Jetzt einrichten", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        void Update(string? message = null)
        {
            if (PackageIdentity.HasIdentity)
            {
                status.Text = "✓ Eingerichtet.";
                setup.Visibility = Visibility.Collapsed;
            }
            else if (PackageIdentity.IsRegistered && !PackageIdentity.RegisteredElsewhere)
            {
                status.Text = message ?? "✓ Eingerichtet – wirkt nach dem Neustart am Ende.";
                setup.Visibility = Visibility.Collapsed;
            }
            else if (!PackageIdentity.Available)
            {
                status.Text = "Geht nur in der installierten Fassung (nicht beim Start aus dem Quellcode). Später unter Einstellungen → Kalender & Mitteilungen.";
                setup.Visibility = Visibility.Collapsed;
            }
            else
            {
                status.Text = message ?? "";
                setup.Visibility = Visibility.Visible;
            }
        }
        setup.Click += async (_, _) =>
        {
            setup.IsEnabled = false;
            status.Text = "Wird eingerichtet …";
            var error = PackageIdentity.RegisteredElsewhere ? await PackageIdentity.ReinstallAsync() : await PackageIdentity.SetupAsync();
            setup.IsEnabled = true;
            Update(error is null ? null : "Hat nicht geklappt: " + error);
        };
        Update();
        panel.Children.Add(setup);
        panel.Children.Add(Buttons(("Zurück", () => Go(1), false), ("Weiter", () => Go(3), true)));
        return panel;
    }

    private UIElement DoneStep()
    {
        var restart = PackageIdentity.IsRegistered && !PackageIdentity.HasIdentity;
        var panel = Column(HorizontalAlignment.Center, 14);
        panel.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(Text("Alles bereit", 30, FontWeights.SemiBold, BrimColors.Fog, center: true));
        panel.Children.Add(Text("Das Zahnrad neben den Tabs öffnet die Einstellungen – Form, Material, Schriften, Plugins und mehr. " +
            "Viel Spaß mit Brim!", 14, FontWeights.Normal, BrimColors.Slate, center: true));
        panel.Children.Add(Buttons(("Zurück", () => Go(2), false), (restart ? "Brim neu starten" : "Loslegen", FinishOnboarding, true)));
        return panel;
    }

    // ───────── Bausteine ─────────

    /// <summary>Lottie-Animation aus Assets/Onboarding, in Schleife.</summary>
    private static FrameworkElement Lottie(string name)
    {
        var player = new AnimatedVisualPlayer { Height = 120, AutoPlay = false, Stretch = Stretch.Uniform };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(player, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        player.Loaded += async (_, _) =>
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Onboarding", name + ".json"));
                var source = new LottieVisualSource();
                await source.SetSourceAsync(file);
                player.Source = source;
                _ = player.PlayAsync(0, 1, looped: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Animation {name} nicht geladen", ex);
            }
        };
        return player;
    }

    private static StackPanel Column(HorizontalAlignment align, double spacing) => new() { Spacing = spacing, HorizontalAlignment = align };

    private static TextBlock Text(string text, double size, Windows.UI.Text.FontWeight weight, string color, bool center = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = Brushes.Get(color),
        TextWrapping = TextWrapping.WrapWholeWords,
        TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
        HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
        MaxWidth = 560,
    };

    private static Button Primary(string label, Action click)
    {
        var button = new Button { Content = label, Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 120 };
        button.Click += (_, _) => click();
        return button;
    }

    private static StackPanel Buttons(params (string Label, Action Click, bool Primary)[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (label, click, primary) in buttons)
        {
            if (primary)
            {
                row.Children.Add(Primary(label, click));
                continue;
            }
            var button = new Button { Content = label, MinWidth = 100 };
            button.Click += (_, _) => click();
            row.Children.Add(button);
        }
        return row;
    }
}
