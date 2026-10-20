using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules.Audio;
using Windows.System;

namespace Brim.Modules;

/// <summary>
/// Audio: Lautstärke-HUD (A3) und die Menüs „Audioausgabe“ und „Lautstärke“ (Gesamt und pro App).
/// Andere Module öffnen die Menüs über <see cref="ShowOutput"/> und <see cref="ShowMixer"/>.
/// Ein offenes Menü ist eine Hintergrund-Aktivität mit hohem Rang: Es belegt die kompakte Notch
/// nicht, steht aufgeklappt aber im Tab „Jetzt“ vorn.
/// </summary>
public sealed class AudioModule : IBrimModule
{
    public const string ModuleId = "audio";

    /// <summary>Aktion für andere Module: Geräteliste öffnen bzw. wieder schließen.</summary>
    public const string ShowOutput = "show-output";

    /// <summary>Aktion für andere Module: Mixer öffnen bzw. wieder schließen.</summary>
    public const string ShowMixer = "show-mixer";

    private const string PanelId = "panel";

    private enum Panel { None, Output, Mixer }

    private readonly AudioService _audio;
    private ModuleContext? _context;
    private ITimer? _ticker;
    private Panel _panel;

    public AudioModule(AudioService audio)
    {
        _audio = audio;
    }

    public string Id => ModuleId;
    public string DisplayName => "Audio";

    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _audio.MasterChanged += OnMasterChanged;
        _audio.DevicesChanged += PublishPanel;
        // Im Mixer kommen und gehen Apps; einmal pro Sekunde nachsehen, aber nur, solange er offen ist.
        _ticker = context.Time.CreateTimer(_ =>
        {
            if (_panel == Panel.Mixer)
            {
                PublishPanel();
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    /// <summary>Symbol passend zur Lautstärke.</summary>
    public static string GlyphFor(float volume, bool muted)
    {
        var level = (int)Math.Round(volume * 100);
        return muted || level == 0 ? Glyphs.Mute : level switch
        {
            < 34 => Glyphs.Volume1,
            < 67 => Glyphs.Volume2,
            _ => Glyphs.Volume3,
        };
    }

    // ───────── HUD ─────────

    private void OnMasterChanged(float volume, bool muted)
    {
        PublishPanel();

        var level = (int)Math.Round(volume * 100);
        muted |= level == 0;
        _context?.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "hud",
            Priority = ActivityPriority.Transient,
            Announcement = muted ? "Ton aus" : $"Lautstärke {level}",
            Content = new StackWidget(
                new IconWidget(GlyphFor(volume, muted)) { Size = 16 },
                new ProgressWidget(muted ? 0 : volume) { Grow = 1, AccessibleName = "Lautstärke" },
                new TextWidget(muted ? "–" : level.ToString()) { Style = TextStyle.MonoCaption, FontSize = 12 })
            {
                Spacing = 12,
                Padding = (18, 0, 18, 0),
            },
        });
    }

    // ───────── Menüs ─────────

    private void PublishPanel()
    {
        var context = _context;
        if (context is null || _panel == Panel.None)
        {
            return;
        }
        context.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = PanelId,
            Priority = ActivityPriority.Ongoing,
            IsBackground = true,
            Rank = 1000,
            Expanded = _panel == Panel.Output ? OutputView() : MixerView(),
        });
    }

    private void ShowPanel(Panel panel)
    {
        _panel = panel;
        if (panel == Panel.None)
        {
            _context?.End(PanelId);
        }
        else
        {
            PublishPanel();
        }
    }

    private static Widget PanelHeader(string title) =>
        new StackWidget(
            new ButtonWidget("back") { Glyph = Glyphs.Back, Size = 28, GlyphSize = 12, AccessibleName = "Zurück" },
            new TextWidget(title) { Style = TextStyle.Title, FontSize = 15, Grow = 1 })
        {
            Spacing = 6,
        };

    /// <summary>Ausgabegeräte; ein Klick macht das Gerät zum Standard.</summary>
    private Widget OutputView()
    {
        var rows = _audio.Devices().Select(d => (Widget)new ButtonWidget("device:" + d.Id)
        {
            Kind = ButtonKind.Row,
            Label = d.Name,
            Glyph = LooksLikeHeadphones(d.Name) ? Glyphs.Headphones : Glyphs.Speakers,
            Selected = d.IsDefault,
            AccessibleName = d.IsDefault ? $"{d.Name}, ausgewählt" : d.Name,
        }).ToList();
        if (rows.Count == 0)
        {
            rows.Add(new TextWidget("Kein Ausgabegerät gefunden.") { Style = TextStyle.Secondary });
        }
        return new StackWidget(
            PanelHeader("Audioausgabe"),
            new ListWidget(rows) { Spacing = 2, Scrollable = true, Grow = 1 })
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 8,
            Align = CrossAlign.Stretch,
        };
    }

    /// <summary>Gesamtlautstärke und darunter jede App, die gerade Ton ausgibt.</summary>
    private Widget MixerView()
    {
        var (volume, muted) = _audio.Master;
        var rows = new List<Widget> { VolumeRow("master", "Gesamt", volume, muted, emphasized: true) };
        rows.AddRange(_audio.Apps().Select(a => VolumeRow("app:" + a.Key, a.Name, a.Volume, a.Muted, emphasized: false)));
        return new StackWidget(
            PanelHeader("Lautstärke"),
            new ListWidget(rows) { Spacing = 4, Scrollable = true, Grow = 1 })
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 8,
            Align = CrossAlign.Stretch,
        };
    }

    private static Widget VolumeRow(string action, string name, float volume, bool muted, bool emphasized) =>
        new StackWidget(
            new ButtonWidget(action + ":mute")
            {
                Glyph = GlyphFor(volume, muted),
                Size = 32,
                GlyphSize = 15,
                GlyphColor = muted ? BrimColors.Slate : BrimColors.Fog,
                AccessibleName = muted ? $"{name}: Ton einschalten" : $"{name}: stummschalten",
            },
            new TextWidget(name) { Style = emphasized ? TextStyle.Body : TextStyle.Secondary, FixedWidth = 120 },
            new ProgressWidget(muted ? 0 : volume)
            {
                SeekActionId = action,
                Thickness = emphasized ? 6 : 4,
                Color = muted ? BrimColors.Slate : BrimColors.Fog,
                Grow = 1,
                AccessibleName = $"Lautstärke {name}",
            },
            new TextWidget(muted ? "–" : ((int)Math.Round(volume * 100)).ToString()) { Style = TextStyle.MonoCaption, FontSize = 12, FixedWidth = 28 })
        {
            Spacing = 10,
        };

    private static bool LooksLikeHeadphones(string name) =>
        new[] { "kopfh", "headphone", "headset", "airpods", "buds", "ohrh" }.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    public void OnAction(ActionInvocation invocation)
    {
        var id = invocation.ActionId;
        switch (id)
        {
            case ShowOutput:
                ShowPanel(_panel == Panel.Output ? Panel.None : Panel.Output);
                return;
            case ShowMixer:
                ShowPanel(_panel == Panel.Mixer ? Panel.None : Panel.Mixer);
                return;
            case "back":
                ShowPanel(Panel.None);
                return;
            case "master:mute":
                _audio.ToggleMasterMute();
                break;
            case "master" when invocation.Value is { } level:
                _audio.SetMasterVolume((float)level);
                break;
            default:
                if (id.StartsWith("device:", StringComparison.Ordinal))
                {
                    // Nicht auf dem UI-Thread warten: Die Liste aktualisiert sich über DevicesChanged.
                    _ = SwitchDeviceAsync(id["device:".Length..]);
                    return;
                }
                else if (id.StartsWith("app:", StringComparison.Ordinal) && id.EndsWith(":mute", StringComparison.Ordinal))
                {
                    _audio.ToggleAppMute(id["app:".Length..^":mute".Length]);
                }
                else if (id.StartsWith("app:", StringComparison.Ordinal) && invocation.Value is { } appLevel)
                {
                    _audio.SetAppVolume(id["app:".Length..], (float)appLevel);
                }
                break;
        }
        // Änderungen sofort zeigen (Gesamtlautstärke kommt zusätzlich über das Ereignis).
        PublishPanel();
    }

    private async Task SwitchDeviceAsync(string deviceId)
    {
        if (!await _audio.SetDefaultDeviceAsync(deviceId))
        {
            // Notlösung, falls Windows das Umschalten ablehnt.
            _ = Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));
        }
    }

    public void OnExpandedChanged(bool expanded)
    {
        // Beim Zuklappen schließen, damit die Notch beim nächsten Mal wieder beim Player startet.
        if (!expanded && _panel != Panel.None)
        {
            ShowPanel(Panel.None);
        }
    }

    public ValueTask DisposeAsync()
    {
        _ticker?.Dispose();
        _audio.MasterChanged -= OnMasterChanged;
        _audio.DevicesChanged -= PublishPanel;
        return ValueTask.CompletedTask;
    }
}
