using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules.Sensors;
using Windows.Devices.Radios;
using Windows.System;
using Windows.System.Power;

namespace Brim.Modules;

/// <summary>Zustand einer Kachel der Schnelleinstellungen.</summary>
public sealed record QuickTile(string Id, string Glyph, string Label, bool On, bool Available = true);

/// <summary>Woher die Schnelleinstellungen ihre Zustände haben (austauschbar für Tests).</summary>
public interface IQuickSettingsSource : IDisposable
{
    event Action? Changed;

    Task StartAsync();

    IReadOnlyList<QuickTile> Read();

    /// <summary>Schaltet um, wenn Windows das erlaubt; sonst öffnet es die passende Seite der Windows-Einstellungen.</summary>
    Task ToggleAsync(string id);

    /// <summary>Öffnet die Seite der Windows-Einstellungen zu dieser Kachel.</summary>
    Task OpenSettingsAsync(string id);
}

/// <summary>
/// Schnelleinstellungen im Tab „System“ (Redesign „Windows-11-nativ“): WLAN, Bluetooth, Nicht stören und
/// Energiesparen als Kacheln wie im Windows-Flyout, in derselben Reihenfolge, „an“ in der Akzentfarbe.
/// WLAN und Bluetooth schaltet Brim selbst (<see cref="Radio"/>). Für Nicht stören und Energiesparen gibt es
/// keine öffentliche Schnittstelle zum Schalten; ein Klick öffnet dort die Seite der Windows-Einstellungen.
/// </summary>
public sealed class QuickSettingsModule : IBrimModule
{
    public const string ModuleId = "quick";

    private readonly Func<IQuickSettingsSource> _createSource;
    private IQuickSettingsSource? _source;
    private ModuleContext? _context;
    private ITimer? _poll;

    public QuickSettingsModule() : this(() => new WindowsQuickSettings())
    {
    }

    public QuickSettingsModule(Func<IQuickSettingsSource> createSource)
    {
        _createSource = createSource;
    }

    public string Id => ModuleId;
    public string DisplayName => "Schnelleinstellungen";

    public async Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _source = _createSource();
        _source.Changed += Publish;
        await _source.StartAsync();
        Publish();
        // „Nicht stören“ meldet keine Änderung; der Mond an der Uhr soll trotzdem bald folgen.
        _poll = context.Time.CreateTimer(_ => Publish(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void OnTabVisibilityChanged(NotchTab tab, bool visible)
    {
        // Nicht stören meldet keine Änderung: beim Öffnen des Tabs frisch lesen.
        if (tab == NotchTab.System && visible)
        {
            Publish();
        }
    }

    public void OnAction(ActionInvocation invocation)
    {
        if (_source is not { } source)
        {
            return;
        }
        var id = invocation.ActionId;
        _ = Run(id.StartsWith("settings:", StringComparison.Ordinal)
            ? source.OpenSettingsAsync(id["settings:".Length..])
            : source.ToggleAsync(id));
    }

    private async Task Run(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Log.Warn("Schnelleinstellungen: Aktion fehlgeschlagen", ex);
        }
        Publish();
    }

    /// <summary>Die Kachelreihe als Widget (öffentlich für Tests).</summary>
    public static Widget Card(IReadOnlyList<QuickTile> tiles) => new StackWidget(
        tiles.Where(t => t.Available).Select(t => (Widget)new StackWidget(
            new ButtonWidget(t.Id)
            {
                Kind = ButtonKind.Round,
                Glyph = t.Glyph,
                Size = 44,
                GlyphSize = 16,
                Selected = t.On,
                AccessibleName = $"{t.Label}: {(t.On ? "an" : "aus")}",
            },
            new TextWidget(t.Label) { Style = TextStyle.Secondary, FontSize = 11 })
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 4,
            Align = CrossAlign.Center,
            Grow = 1,
        }).ToList())
    {
        Spacing = 8,
        Justify = Justify.SpaceBetween,
        Background = BrimColors.Graphite,
        CornerRadius = 8,
        Padding = (12, 10, 12, 8),
        AccessibleName = "Schnelleinstellungen",
    };

    private void Publish()
    {
        if (_context is not { } context || _source is not { } source)
        {
            return;
        }
        var tiles = source.Read();
        var quiet = tiles.Any(t => t.Id == "dnd" && t.On);
        context.SetStatus("dnd", quiet
            ? new IconWidget(Glyphs.QuietHours) { Size = 12, Color = BrimColors.Slate, AccessibleName = "Nicht stören ist an" }
            : null);
        if (tiles.All(t => !t.Available))
        {
            return;
        }
        context.SetView("tiles", Card(tiles), order: -10);
    }

    public ValueTask DisposeAsync()
    {
        _poll?.Dispose();
        if (_source is { } source)
        {
            source.Changed -= Publish;
            source.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Echte Zustände aus Windows.</summary>
internal sealed class WindowsQuickSettings : IQuickSettingsSource
{
    private readonly List<Radio> _radios = [];

    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
            {
                foreach (var radio in await Radio.GetRadiosAsync())
                {
                    if (radio.Kind is RadioKind.WiFi or RadioKind.Bluetooth)
                    {
                        radio.StateChanged += OnRadioChanged;
                        _radios.Add(radio);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Schnelleinstellungen: Funkmodule nicht lesbar", ex);
        }
        PowerManager.EnergySaverStatusChanged += OnPowerChanged;
    }

    private void OnRadioChanged(Radio sender, object args) => Changed?.Invoke();

    private void OnPowerChanged(object? sender, object e) => Changed?.Invoke();

    public IReadOnlyList<QuickTile> Read()
    {
        var wifi = _radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
        var bluetooth = _radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
        var saver = PowerManager.EnergySaverStatus;
        return
        [
            new QuickTile("wifi", Glyphs.Wifi, "WLAN", wifi?.State == RadioState.On, wifi is not null),
            new QuickTile("bluetooth", Glyphs.Bluetooth, "Bluetooth", bluetooth?.State == RadioState.On, bluetooth is not null),
            new QuickTile("dnd", Glyphs.QuietHours, "Nicht stören", QuietHours.IsDoNotDisturb()),
            new QuickTile("saver", Glyphs.Leaf, "Energiesparen", saver == EnergySaverStatus.On, saver != EnergySaverStatus.Disabled),
        ];
    }

    public async Task ToggleAsync(string id)
    {
        var kind = id switch
        {
            "wifi" => RadioKind.WiFi,
            "bluetooth" => RadioKind.Bluetooth,
            _ => (RadioKind?)null,
        };
        if (kind is { } k && _radios.FirstOrDefault(r => r.Kind == k) is { } radio)
        {
            var result = await radio.SetStateAsync(radio.State == RadioState.On ? RadioState.Off : RadioState.On);
            if (result == RadioAccessStatus.Allowed)
            {
                return;
            }
        }
        // Nicht stören und Energiesparen lassen sich nur in Windows schalten.
        await OpenSettingsAsync(id);
    }

    public async Task OpenSettingsAsync(string id)
    {
        var page = id switch
        {
            "wifi" => "ms-settings:network-wifi",
            "bluetooth" => "ms-settings:bluetooth",
            "dnd" => "ms-settings:notifications",
            "saver" => "ms-settings:batterysaver",
            _ => "ms-settings:",
        };
        await Launcher.LaunchUriAsync(new Uri(page));
    }

    public void Dispose()
    {
        PowerManager.EnergySaverStatusChanged -= OnPowerChanged;
        foreach (var radio in _radios)
        {
            radio.StateChanged -= OnRadioChanged;
        }
        _radios.Clear();
    }
}
