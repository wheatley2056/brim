using Brim.Core;
using Brim.Core.Widgets;
using Brim.Modules.Sensors;
using Windows.System;

namespace Brim.Modules;

/// <summary>
/// Kamera und Mikrofon in Benutzung (A3): grüner bzw. oranger Punkt wie am Mac. Allein in der
/// kompakten Notch mit Symbol und App, neben anderem als Perle; aufgeklappt mit allen Apps.
/// </summary>
public sealed class PrivacyModule : IBrimModule
{
    public const string ModuleId = "privacy";

    /// <summary>Kurz warten, bis Windows alle Einträge geschrieben hat (Start und Stopp kommen in Schüben).</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly Func<ICapabilitySource> _createSource;
    private ICapabilitySource? _source;
    private ModuleContext? _context;
    private ITimer? _debounce;

    public PrivacyModule() : this(() => new ConsentStoreSource())
    {
    }

    public PrivacyModule(Func<ICapabilitySource> createSource)
    {
        _createSource = createSource;
    }

    public string Id => ModuleId;
    public string DisplayName => "Kamera und Mikrofon";

    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _debounce = context.Time.CreateTimer(_ => Update(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _source = _createSource();
        _source.Changed += OnChanged;
        Update();
        return Task.CompletedTask;
    }

    private void OnChanged() => _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan);

    private void Update()
    {
        if (_context is not { } context || _source is not { } source)
        {
            return;
        }
        var uses = source.Read();
        var camera = Apps(uses, Capability.Camera);
        var microphone = Apps(uses, Capability.Microphone);
        if (camera.Count == 0 && microphone.Count == 0)
        {
            context.End("indicator");
            return;
        }

        // Kamera hat Vorrang (grün), wie bei iPhone und Mac.
        var color = camera.Count > 0 ? BrimColors.Camera : BrimColors.Microphone;
        var icons = new List<Widget>();
        if (camera.Count > 0)
        {
            icons.Add(new IconWidget(Glyphs.Camera) { Size = 15, Color = BrimColors.Camera });
        }
        if (microphone.Count > 0)
        {
            icons.Add(new IconWidget(Glyphs.Microphone) { Size = 15, Color = BrimColors.Microphone });
        }
        var all = camera.Concat(microphone).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var summary = Summary(camera, microphone);

        context.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "indicator",
            Priority = ActivityPriority.Ongoing,
            // Unter laufender Musik (die bleibt groß), über Akku und pausierter Musik.
            Rank = 8,
            Leading = new StackWidget(icons) { Spacing = 6, AccessibleName = summary },
            Trailing = new TextWidget(all.Count > 1 ? $"{all[0]} +{all.Count - 1}" : all[0]) { Style = TextStyle.Caption, FontSize = 12, FixedWidth = 120 },
            Bead = new IconWidget(Glyphs.Dot) { Size = 8, Color = color, AccessibleName = summary },
            Expanded = ExpandedView(camera, microphone),
            Announcement = summary,
        });
    }

    private static List<string> Apps(IEnumerable<CapabilityUse> uses, Capability capability) =>
        uses.Where(u => u.Capability == capability).Select(u => u.App).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Für die Sprachausgabe: „Kamera: Teams. Mikrofon: Teams, Discord.“</summary>
    public static string Summary(IReadOnlyList<string> camera, IReadOnlyList<string> microphone)
    {
        var parts = new List<string>();
        if (camera.Count > 0)
        {
            parts.Add("Kamera: " + string.Join(", ", camera));
        }
        if (microphone.Count > 0)
        {
            parts.Add("Mikrofon: " + string.Join(", ", microphone));
        }
        return string.Join(". ", parts) + ".";
    }

    private static Widget ExpandedView(IReadOnlyList<string> camera, IReadOnlyList<string> microphone)
    {
        var rows = new List<Widget>();
        if (camera.Count > 0)
        {
            rows.Add(Row(Glyphs.Camera, BrimColors.Camera, "Kamera", camera, "settings:camera"));
        }
        if (microphone.Count > 0)
        {
            rows.Add(Row(Glyphs.Microphone, BrimColors.Microphone, "Mikrofon", microphone, "settings:microphone"));
        }
        return new StackWidget(
            new TextWidget("In Benutzung") { Style = TextStyle.Title, FontSize = 15 },
            new ListWidget(rows) { Spacing = 8, Scrollable = true, Grow = 1 })
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 10,
            Align = CrossAlign.Stretch,
        };
    }

    private static Widget Row(string glyph, string color, string label, IReadOnlyList<string> apps, string action) =>
        new StackWidget(
            new IconWidget(glyph) { Size = 16, Color = color },
            new TextWidget(label) { FixedWidth = 80 },
            new TextWidget(string.Join(", ", apps)) { Style = TextStyle.Secondary, Grow = 1 },
            new ButtonWidget(action)
            {
                Glyph = Glyphs.Settings,
                Size = 28,
                GlyphSize = 13,
                GlyphColor = BrimColors.Slate,
                AccessibleName = $"Datenschutzeinstellungen für {label}",
            })
        {
            Spacing = 12,
            Background = BrimColors.Graphite,
            CornerRadius = 12,
            Padding = (12, 4, 6, 4),
        };

    public void OnAction(ActionInvocation invocation)
    {
        var uri = invocation.ActionId switch
        {
            "settings:camera" => "ms-settings:privacy-webcam",
            "settings:microphone" => "ms-settings:privacy-microphone",
            _ => null,
        };
        if (uri is not null)
        {
            _ = Launcher.LaunchUriAsync(new Uri(uri));
        }
    }

    public ValueTask DisposeAsync()
    {
        _debounce?.Dispose();
        if (_source is { } source)
        {
            source.Changed -= OnChanged;
            source.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
