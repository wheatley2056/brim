using Brim.Core;
using CommunityToolkit.WinUI.Lottie;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace Brim.Shell;

/// <summary>
/// Einführung beim ersten Start (Plan 8a, Design „First launch · Birth“): Eine Naht aus Licht an der Kante, die
/// Notch tropft heraus, blüht zur Begrüßung auf (mit Schimmer durch die Akzentfarben) und zieht sich in die Ruhe
/// zurück; darunter erscheint kurz ein Hinweis. Nimmt nie den Fokus, ein Klick auf die Notch springt ans Ende.
/// Beim ersten Aufklappen danach folgen Kurzanleitung und Einrichtung in der Notch.
/// </summary>
public sealed partial class NotchView
{
    // Formen aus dem Design: unsichtbar in der Kante, Tropfen, Begrüßung. Ohren wie dort: 45 % der Höhe, höchstens 11.
    private static readonly NotchShape BirthSeam = new(64, 0.5, 0, 0);
    private static readonly NotchShape BirthDrop = new(112, 26, 13, 11);
    private static readonly NotchShape BirthHello = new(440, 136, 36, 11);

    private Grid? _onboardingLayer;
    private ContentPresenter? _onboardingStep;
    private StackPanel? _onboardingDots;
    private Grid? _helloLayer;
    private FrameworkElement? _hint;
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _birthTimers = [];
    private int _onboardingIndex;
    private bool _birth;
    private bool _onboarding;
    private bool _onboardingPending;

    /// <summary>Die Geburt ist vorbei, die Notch ruht; der Hinweis steht darunter (Fensterregion mit einschließen).</summary>
    public event Action<bool>? HintChanged;

    /// <summary>Die Geburt ist vorbei: Notch ruht wieder; die Schritte kommen beim ersten Aufklappen.</summary>
    public event Action? BirthFinished;

    /// <summary>Die Schritte beginnen (erstes Aufklappen nach der Geburt): offen halten, Tastatur annehmen.</summary>
    public event Action? OnboardingStarted;

    /// <summary>Einführung beendet; <c>true</c>, wenn Brim für Kalender und Benachrichtigungen neu starten soll.</summary>
    public event Action<bool>? OnboardingFinished;

    public bool IsOnboarding => _onboarding || _birth;

    /// <summary>Bereich des Hinweises in DIP der Zeichenfläche (für die Fensterregion).</summary>
    public (double X, double Y, double Width, double Height) HintArea =>
        (NotchGeometry.Canvas.Width / 2 - 230, 30 + NotchGeometry.Gap(_form), 460, 60);

    /// <summary>Spielt die Geburt; danach ruht die Notch, und die Schritte warten aufs erste Aufklappen.</summary>
    public void StartOnboarding(AnimationSettings animation, bool reducedMotion)
    {
        SetSettingsOpen(false);
        _board?.SetEditing(false);
        EnsureOnboardingLayer();
        EnsureHello();
        _onboardingIndex = 0;
        ShowOnboardingStep();
        StopBirthTimers();
        _birth = true;
        _onboardingPending = true;
        FadeClock(false);
        SwitchLayer(null, animation, reducedMotion, growing: true);

        var calm = reducedMotion || animation.Style is AnimationStyle.None or AnimationStyle.Fade;
        if (calm)
        {
            // Bewegung reduziert: keine Feder, nur Überblenden – Begrüßung, dann Ruhe.
            _surface.MorphTo(NotchGeometry.Styled(BirthHello, _form), new AnimationSettings { Style = AnimationStyle.None }, reducedMotion: true);
            _surface.SetVisible(true, TimeSpan.Zero);
            ShowHello(true, animate: false);
            At(2600, () => Settle(new AnimationSettings { Style = AnimationStyle.Fade }));
            return;
        }

        _surface.MorphTo(NotchGeometry.Styled(BirthSeam, _form), new AnimationSettings { Style = AnimationStyle.None }, reducedMotion: false);
        _surface.SetVisible(true, TimeSpan.Zero);
        _surface.PlaySeam();
        _surface.PlaySheen(NotchGeometry.Styled(BirthHello, _form), TimeSpan.FromMilliseconds(1000));
        // Tropfen: aus der Kante heraus, kaum Überschwingen (Dämpfung 0,85).
        At(260, () => LastMorphDuration = _surface.MorphTo(NotchGeometry.Styled(BirthDrop, _form), animation, reducedMotion: false, new Spring(0.36, 0.85)));
        // Aufblühen: weicher, etwa 4 % Überschwingen (Dämpfung 0,7).
        At(620, () => LastMorphDuration = _surface.MorphTo(NotchGeometry.Styled(BirthHello, _form), animation, reducedMotion: false, new Spring(0.62, 0.7)));
        At(950, () => ShowHello(true, animate: true));
        At(2900, () => Settle(new AnimationSettings { Style = AnimationStyle.EaseOut, RetractMs = 360, ExpandMs = 360 }));
    }

    /// <summary>Zurückziehen in die Ruhe (schneller als das Ausdehnen), dann übernimmt wieder Present.</summary>
    private void Settle(AnimationSettings animation)
    {
        StopBirthTimers();
        ShowHello(false, animate: animation.Style != AnimationStyle.Fade);
        var idle = ShapeFor(NotchMode.Idle);
        LastMorphDuration = _surface.MorphTo(idle, animation, reducedMotion: false);
        At(360, () =>
        {
            _birth = false;
            BirthFinished?.Invoke();
            // Hinweis an Ort und Stelle, kurz darauf.
            At(140, () => ShowHint(true));
            At(140 + 2200, () => ShowHint(false));
        });
    }

    /// <summary>Klick auf die Notch während der Geburt: sofort ans Ende (Zurückziehen).</summary>
    private void SkipBirth()
    {
        if (_birth)
        {
            Settle(new AnimationSettings { Style = AnimationStyle.EaseOut, RetractMs = 360, ExpandMs = 360 });
        }
    }

    private void At(double ms, Action action)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(ms);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _birthTimers.Remove(timer);
            action();
        };
        _birthTimers.Add(timer);
        timer.Start();
    }

    private void StopBirthTimers()
    {
        foreach (var timer in _birthTimers)
        {
            timer.Stop();
        }
        _birthTimers.Clear();
    }

    /// <summary>„Brim“ und die Zeile darunter, mitten in der aufgeblühten Notch.</summary>
    private void EnsureHello()
    {
        if (_helloLayer is not null)
        {
            return;
        }
        _helloLayer = new Grid
        {
            Width = BirthHello.Width,
            Height = BirthHello.Height,
            Padding = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        var text = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = "Brim",
            FontSize = 46,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = -45,
            LineHeight = 46,
            Foreground = Brushes.Get(BrimColors.Fog),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        text.Children.Add(new TextBlock { Text = "Alles an der Kante.", FontSize = 14, Foreground = Brushes.Get(BrimColors.Slate), HorizontalAlignment = HorizontalAlignment.Center });
        _helloLayer.Children.Add(text);
        ContentHost.Children.Add(_helloLayer);

        // Hinweis unter der Notch, außerhalb ihres Umrisses (nicht beschnitten).
        var bubble = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Opacity = 0 };
        bubble.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), "M0 8 8 0l8 8z"),
            Fill = Brushes.Get("#141519"),
            Width = 16,
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        bubble.Children.Add(new Border
        {
            Padding = new Thickness(16, 11, 16, 11),
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Get("#141519"),
            Child = new TextBlock { Text = "Brim wohnt hier oben. Zeig jederzeit auf die obere Kante.", FontSize = 14, Foreground = Brushes.Get(BrimColors.Fog) },
        });
        bubble.Margin = new Thickness(0, 40, 0, 0);
        _hint = bubble;
        CanvasRoot.Children.Add(bubble);

        CanvasRoot.Tapped += (_, e) =>
        {
            if (_birth)
            {
                SkipBirth();
                e.Handled = true;
            }
        };
    }

    /// <summary>Begrüßung: 450 ms ein (8 px von unten, wie aus der Unschärfe), 120 ms aus.</summary>
    private void ShowHello(bool show, bool animate)
    {
        if (_helloLayer is null)
        {
            return;
        }
        var visual = ElementCompositionPreview.GetElementVisual(_helloLayer);
        ElementCompositionPreview.SetIsTranslationEnabled(_helloLayer, true);
        _helloLayer.Opacity = 1;
        if (!animate)
        {
            visual.Opacity = show ? 1 : 0;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1), new Vector2(0.68f, 1));
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, show ? 0 : 1);
        opacity.InsertKeyFrame(1, show ? 1 : 0, ease);
        opacity.Duration = TimeSpan.FromMilliseconds(show ? 450 : 120);
        visual.StartAnimation("Opacity", opacity);
        if (show)
        {
            var rise = compositor.CreateVector3KeyFrameAnimation();
            rise.InsertKeyFrame(0, new Vector3(0, 8, 0));
            rise.InsertKeyFrame(1, Vector3.Zero, ease);
            rise.Duration = TimeSpan.FromMilliseconds(450);
            visual.StartAnimation("Translation", rise);
            BlurIn(6, TimeSpan.FromMilliseconds(450));
        }
    }

    /// <summary>Hinweis: 300 ms ein (6 px von oben), 300 ms aus.</summary>
    private void ShowHint(bool show)
    {
        if (_hint is null)
        {
            return;
        }
        HintChanged?.Invoke(show);
        _hint.Margin = new Thickness(0, 40 + NotchGeometry.Gap(_form), 0, 0);
        var visual = ElementCompositionPreview.GetElementVisual(_hint);
        ElementCompositionPreview.SetIsTranslationEnabled(_hint, true);
        _hint.Opacity = 1;
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1), new Vector2(0.68f, 1));
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, show ? 0 : 1);
        opacity.InsertKeyFrame(1, show ? 1 : 0, ease);
        opacity.Duration = TimeSpan.FromMilliseconds(300);
        visual.StartAnimation("Opacity", opacity);
        if (show)
        {
            var drop = compositor.CreateVector3KeyFrameAnimation();
            drop.InsertKeyFrame(0, new Vector3(0, -6, 0));
            drop.InsertKeyFrame(1, Vector3.Zero, ease);
            drop.Duration = TimeSpan.FromMilliseconds(300);
            visual.StartAnimation("Translation", drop);
        }
    }

    /// <summary>Beim ersten Aufklappen nach der Geburt: Kurzanleitung und Einrichtung statt „Jetzt“.</summary>
    private void BeginStepsIfPending(NotchPresentation p)
    {
        if (_onboardingPending && !_birth && p.Mode == NotchMode.Expanded && !_onboarding)
        {
            _onboardingPending = false;
            _onboarding = true;
            _onboardingIndex = 0;
            ShowOnboardingStep();
            OnboardingStarted?.Invoke();
        }
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
            Width = LargeShape.Width,
            Height = LargeShape.Height,
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

    private const int OnboardingSteps = 3;

    private void ShowOnboardingStep()
    {
        if (_onboardingStep is null || _onboardingDots is null)
        {
            return;
        }
        _onboardingStep.Content = _onboardingIndex switch
        {
            0 => GuideStep(),
            1 => IdentityStep(),
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

        var buttons = Buttons(("Später", FinishOnboarding, false), ("Weiter", () => Go(1), true));
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
        panel.Children.Add(Buttons(("Zurück", () => Go(0), false), ("Weiter", () => Go(2), true)));
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
        panel.Children.Add(Buttons(("Zurück", () => Go(1), false), (restart ? "Brim neu starten" : "Loslegen", FinishOnboarding, true)));
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
