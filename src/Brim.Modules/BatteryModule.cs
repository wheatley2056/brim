using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules.Sensors;
using Windows.System;

namespace Brim.Modules;

/// <summary>
/// Akku (A6), nur auf Geräten mit Akku: HUD beim Ein- und Ausstecken, Warnung bei wenig Ladung,
/// kompakter Akkustand beim Laden und darunter, Karte im Tab „System“.
/// </summary>
public sealed class BatteryModule : IBrimModule
{
    public const string ModuleId = "battery";

    private readonly Func<IBatterySource> _createSource;
    private readonly Lock _gate = new();
    private IBatterySource? _source;
    private ModuleContext? _context;
    private BatteryState? _last;
    private bool _warnedLow;
    private bool _warnedCritical;

    public BatteryModule() : this(() => new PowerManagerBatterySource())
    {
    }

    public BatteryModule(Func<IBatterySource> createSource)
    {
        _createSource = createSource;
    }

    public string Id => ModuleId;
    public string DisplayName => "Akku";

    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _source = _createSource();
        _source.Changed += Update;
        Update();
        return Task.CompletedTask;
    }

    private BatterySettings Settings => _context!.Settings.Battery;

    private void Update()
    {
        var context = _context;
        var source = _source;
        if (context is null || source is null)
        {
            return;
        }
        var state = source.Read();
        BatteryState? previous;
        bool warnLow = false, warnCritical = false;
        lock (_gate)
        {
            previous = _last;
            if (previous == state)
            {
                return;
            }
            _last = state;
            var low = Settings.LowPercent;
            var critical = Settings.CriticalPercent;
            // Jede Warnung einmal pro Entladung; Laden oder wieder genug Ladung setzt sie zurück.
            if (state.PluggedIn || state.Percent > low)
            {
                _warnedLow = false;
            }
            if (state.PluggedIn || state.Percent > critical)
            {
                _warnedCritical = false;
            }
            if (state.Present && !state.PluggedIn)
            {
                if (state.Percent <= critical && !_warnedCritical)
                {
                    _warnedCritical = _warnedLow = warnCritical = true;
                }
                else if (state.Percent <= low && !_warnedLow)
                {
                    _warnedLow = warnLow = true;
                }
            }
        }

        if (!state.Present)
        {
            // Desktop-PC (oder Akku entfernt): nichts zeigen.
            context.End("status");
            context.RemoveView("card");
            return;
        }

        if (previous is { Present: true } && previous.PluggedIn != state.PluggedIn)
        {
            PostPlugHud(context, state);
        }
        if (warnCritical)
        {
            PostCritical(context, state);
        }
        else if (warnLow)
        {
            PostLowHud(context, state);
        }

        if (ShowsCompact(state, Settings))
        {
            context.Post(new Activity
            {
                ModuleId = ModuleId,
                Id = "status",
                Priority = ActivityPriority.Ongoing,
                // Unter laufender Musik, über pausierter: als Perle daneben statt an ihrer Stelle.
                Rank = 5,
                Leading = new IconWidget(Glyph(state)) { Size = 16, Color = ColorFor(state, Settings) },
                Trailing = new TextWidget($"{state.Percent} %") { Style = TextStyle.Mono, AccessibleName = $"Akku {state.Percent} %" },
                Bead = new IconWidget(Glyph(state)) { Size = 14, Color = ColorFor(state, Settings), AccessibleName = $"Akku {state.Percent} %" },
                Expanded = ExpandedView(state),
            });
        }
        else
        {
            context.End("status");
        }

        context.SetView("card", Card(state), column: TabColumn.Side, order: 0);
        context.SetStatus("status", state.Present && context.Settings.Display.Battery ? StatusWidget(state) : null);
    }

    public void OnSettingsChanged()
    {
        if (_context is { } context && _last is { } state)
        {
            context.SetStatus("status", state.Present && context.Settings.Display.Battery ? StatusWidget(state) : null);
        }
    }

    /// <summary>Kompakt nur beim Laden (einstellbar) oder wenn der Akku zur Neige geht (Plan 4.6).</summary>
    public static bool ShowsCompact(BatteryState state, BatterySettings settings) =>
        state.Present && ((state.Charging && settings.CompactWhileCharging) || (!state.PluggedIn && state.Percent <= settings.LowPercent));

    public static string Glyph(BatteryState state) => Glyphs.Battery(state.Percent, state.Charging, state.EnergySaver && !state.Charging);

    /// <summary>Lädt: blau (mit Blitz). Fast leer: rot. Energiesparmodus: orange. Sonst grün.</summary>
    public static string ColorFor(BatteryState state, BatterySettings settings) =>
        state.Charging ? BrimColors.Charging
        : !state.PluggedIn && state.Percent <= settings.LowPercent ? BrimColors.Danger
        : state.EnergySaver ? BrimColors.Warning
        : BrimColors.Charge;

    /// <summary>„voll in 1 h 5 min“, „noch 3 h 10 min“, „Angeschlossen“ – plus Energiesparmodus.</summary>
    public static string StatusText(BatteryState state)
    {
        var text = state switch
        {
            { Charging: true, TimeToFull: { } full } => "voll in " + FormatDuration(full),
            { Charging: true } => "Lädt",
            { PluggedIn: true } => "Angeschlossen",
            { Remaining: { } left } => "noch " + FormatDuration(left),
            _ => "Akkubetrieb",
        };
        return state.EnergySaver ? text + " · Energiesparen" : text;
    }

    public static string FormatDuration(TimeSpan duration)
    {
        var minutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }

    // ───────── Darstellung ─────────

    /// <summary>
    /// Neben der Uhr in der ruhenden Notch: Symbol und Prozent wie die anderen Werte. Farbig nur, wenn es etwas zu
    /// sagen gibt (lädt, fast leer, Energiesparen); ein dauerhaftes Grün würde nur ablenken.
    /// </summary>
    private Widget StatusWidget(BatteryState state)
    {
        var color = StatusColor(state, Settings);
        return new StackWidget(
            new IconWidget(Glyph(state)) { Size = 12, Color = color },
            new TextWidget($"{state.Percent} %") { Style = TextStyle.Status, Color = color })
        {
            Spacing = 4,
            AccessibleName = $"Akku {state.Percent} %, {StatusText(state)}",
        };
    }

    /// <summary>Wie <see cref="ColorFor"/>, aber im Normalfall neutral (Slate) statt grün.</summary>
    public static string StatusColor(BatteryState state, BatterySettings settings)
    {
        var color = ColorFor(state, settings);
        return color == BrimColors.Charge ? BrimColors.Slate : color;
    }

    private Widget Card(BatteryState state) => new StackWidget(
        new IconWidget(Glyph(state)) { Size = 18, Color = ColorFor(state, Settings) },
        new TextWidget($"{state.Percent} %"),
        new TextWidget(StatusText(state)) { Style = TextStyle.Caption, FontSize = 12, Grow = 1 },
        new ButtonWidget("settings")
        {
            Glyph = Glyphs.Settings,
            Size = 22,
            GlyphSize = 11,
            GlyphColor = BrimColors.Slate,
            AccessibleName = "Energieeinstellungen öffnen",
        })
    {
        Spacing = 8,
        Background = BrimColors.Graphite,
        CornerRadius = 14,
        Padding = (12, 6, 6, 6),
        AccessibleName = $"Akku {state.Percent} %, {StatusText(state)}",
    };

    private Widget ExpandedView(BatteryState state) => new StackWidget(
        new StackWidget(
            new IconWidget(Glyph(state)) { Size = 26, Color = ColorFor(state, Settings) },
            new TextWidget($"{state.Percent} %") { Style = TextStyle.Display, FontSize = 30 })
        {
            Spacing = 12,
        },
        new TextWidget(StatusText(state)) { Style = TextStyle.Secondary, FontSize = 14 },
        new SpacerWidget { Grow = 1 },
        new StackWidget(
            new ButtonWidget("settings") { Kind = ButtonKind.Secondary, Label = "Energieeinstellungen" })
        {
            Justify = Justify.End,
        })
    {
        Orientation = StackOrientation.Vertical,
        Spacing = 4,
        Align = CrossAlign.Stretch,
    };

    private void PostPlugHud(ModuleContext context, BatteryState state)
    {
        var text = state.PluggedIn ? "Lädt" : "Akkubetrieb";
        context.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "hud",
            Priority = ActivityPriority.Transient,
            Duration = TimeSpan.FromSeconds(2),
            Announcement = state.PluggedIn ? $"Ladekabel angeschlossen, {state.Percent} %" : $"Ladekabel getrennt, {state.Percent} %",
            Content = HudRow(state, text),
        });
    }

    private void PostLowHud(ModuleContext context, BatteryState state) => context.Post(new Activity
    {
        ModuleId = ModuleId,
        Id = "hud",
        Priority = ActivityPriority.Transient,
        Duration = TimeSpan.FromSeconds(3),
        Announcement = $"Akku schwach, {state.Percent} %",
        Content = HudRow(state, "Akku schwach"),
    });

    private Widget HudRow(BatteryState state, string text) => new StackWidget(
        new IconWidget(Glyph(state)) { Size = 16, Color = ColorFor(state, Settings) },
        new TextWidget(text) { Grow = 1 },
        new TextWidget($"{state.Percent} %") { Style = TextStyle.Mono, Color = ColorFor(state, Settings) })
    {
        Spacing = 10,
        Padding = (18, 0, 18, 0),
    };

    private void PostCritical(ModuleContext context, BatteryState state) => context.Post(new Activity
    {
        ModuleId = ModuleId,
        Id = "critical",
        Priority = ActivityPriority.Critical,
        Duration = TimeSpan.FromSeconds(6),
        Announcement = $"Akku fast leer, {state.Percent} %. Bitte Ladekabel anschließen.",
        Content = new StackWidget(
            new IconWidget(Glyph(state)) { Size = 24, Color = BrimColors.Danger },
            new StackWidget(
                new TextWidget("Akku fast leer") { Style = TextStyle.Title, FontSize = 15 },
                new TextWidget(state.Remaining is { } left
                    ? $"{state.Percent} % · noch {FormatDuration(left)} · Ladekabel anschließen"
                    : $"{state.Percent} % · Ladekabel anschließen") { Style = TextStyle.Secondary })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 2,
                Align = CrossAlign.Start,
                Grow = 1,
            },
            state.EnergySaver
                ? new SpacerWidget()
                : new ButtonWidget("saver") { Kind = ButtonKind.Secondary, Label = "Energiesparen" })
        {
            Spacing = 14,
        },
    });

    public void OnAction(ActionInvocation invocation)
    {
        var uri = invocation.ActionId switch
        {
            "settings" => "ms-settings:powersleep",
            "saver" => "ms-settings:batterysaver",
            _ => null,
        };
        if (uri is not null)
        {
            _context?.End("critical");
            _ = Launcher.LaunchUriAsync(new Uri(uri));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_source is { } source)
        {
            source.Changed -= Update;
            source.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
