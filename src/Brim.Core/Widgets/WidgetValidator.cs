namespace Brim.Core.Widgets;

/// <summary>
/// Prüft Bausteine aus fremder Hand (Plugins), bevor die Shell sie zeichnet: Farben, Maße, Textlängen,
/// Größe und Tiefe des Baums. Ungültiges wird abgelehnt statt still korrigiert, damit Entwickler es merken.
/// </summary>
public static class WidgetValidator
{
    public const int MaxWidgets = 400;
    public const int MaxDepth = 16;
    public const int MaxTextLength = 2000;
    public const int MaxImageBytes = 1024 * 1024;
    private const double MaxSize = 2000;

    /// <returns>Fehlerbeschreibung oder <c>null</c>, wenn alles in Ordnung ist.</returns>
    public static string? Validate(Widget widget)
    {
        var all = WidgetTree.All(widget).Take(MaxWidgets + 1).ToList();
        if (all.Count > MaxWidgets)
        {
            return $"mehr als {MaxWidgets} Bausteine";
        }
        if (WidgetTree.Depth(widget) > MaxDepth)
        {
            return $"tiefer als {MaxDepth} Ebenen";
        }
        var imageBytes = 0L;
        foreach (var w in all)
        {
            var error = Check(w);
            if (error is not null)
            {
                return $"{w.GetType().Name.Replace("Widget", "").ToLowerInvariant()}: {error}";
            }
            if (w is ImageWidget { Image: { } image })
            {
                imageBytes += image.Bytes.Length;
            }
        }
        return imageBytes > MaxImageBytes ? $"Bilder größer als {MaxImageBytes / 1024} KB" : null;
    }

    private static string? Check(Widget w)
    {
        var common = Size(w.FixedWidth, "fixedWidth") ?? Range(w.Grow, 0, 100, "grow") ?? Text(w.AccessibleName, "accessibleName") ?? Text(w.Key, "key");
        if (common is not null)
        {
            return common;
        }
        return w switch
        {
            TextWidget t => (t.Text is null ? "text fehlt" : null) ?? Text(t.Text, "text") ?? Color(t.Color) ?? Range(t.FontSize, 6, 120, "fontSize"),
            IconWidget i => Text(i.Glyph, "glyph") ?? Color(i.Color) ?? Range(i.Size, 4, 200, "size"),
            ImageWidget i => Size(i.Width, "width") ?? Size(i.Height, "height") ?? Size(i.CornerRadius, "cornerRadius")
                ?? (i.Placeholder is { } p ? Color(p.From) ?? Color(p.To) : null)
                ?? (i.Image is { } d && (d.Id is null || d.Bytes is null) ? "image braucht id und bytes" : null),
            ProgressWidget p => Range(p.Value, 0, 1, "value") ?? Color(p.Color) ?? Range(p.Thickness, 1, 40, "thickness") ?? Text(p.SeekActionId, "seekActionId"),
            RingWidget r => Range(r.Value, 0, 1, "value") ?? Range(r.Size, 4, 200, "size") ?? Range(r.Thickness, 0.5, 100, "thickness") ?? Color(r.Color) ?? Color(r.TrackColor),
            ChartWidget c => (c.Values is null ? "values fehlen" : null)
                ?? (c.Values!.Count > 1000 ? "mehr als 1000 Werte" : null)
                ?? (c.Values.Any(v => !double.IsFinite(v)) ? "values müssen endliche Zahlen sein" : null)
                ?? Range(c.Max, 1e-9, double.MaxValue, "max") ?? Range(c.Capacity, 2, 1000, "capacity")
                ?? Range(c.Height, 4, 400, "height") ?? Range(c.Thickness, 0.5, 20, "thickness") ?? Color(c.Color),
            LevelWidget l => Color(l.Color) ?? Range(l.Height, 4, 200, "height"),
            WaveWidget v => (v.Values is null ? "values fehlen" : null)
                ?? (v.Values!.Count > 200 ? "mehr als 200 Werte" : null)
                ?? (v.Values.Any(x => !double.IsFinite(x) || x < 0 || x > 1) ? "values müssen zwischen 0 und 1 liegen" : null)
                ?? Range(v.Height, 4, 200, "height") ?? Range(v.BarWidth, 1, 20, "barWidth") ?? Color(v.Color),
            ButtonWidget b => (string.IsNullOrEmpty(b.ActionId) ? "actionId fehlt" : null) ?? Text(b.ActionId, "actionId") ?? Text(b.Glyph, "glyph")
                ?? Text(b.Label, "label") ?? Range(b.Size, 12, 200, "size") ?? Range(b.GlyphSize, 4, 100, "glyphSize") ?? Color(b.GlyphColor),
            SwitchWidget s => (string.IsNullOrEmpty(s.ActionId) ? "actionId fehlt" : null) ?? Text(s.Label, "label"),
            StackWidget s => (s.Children is null || s.Children.Any(c => c is null) ? "children enthält null" : null)
                ?? Size(s.Spacing, "spacing") ?? Size(s.CornerRadius, "cornerRadius") ?? Color(s.Background) ?? Text(s.ActionId, "actionId")
                ?? Size(s.Padding.Left, "padding") ?? Size(s.Padding.Top, "padding") ?? Size(s.Padding.Right, "padding") ?? Size(s.Padding.Bottom, "padding"),
            ListWidget l => (l.Items is null || l.Items.Any(c => c is null) ? "items enthält null" : null) ?? Size(l.Spacing, "spacing"),
            SpacerWidget s => Size(s.Size, "size"),
            ClockFaceWidget c => Range(c.Hour, 0, 24, "hour") ?? Range(c.Minute, 0, 60, "minute") ?? Range(c.Second, 0, 60, "second")
                ?? Range(c.Size, 16, 400, "size") ?? Color(c.Color) ?? Color(c.AccentColor) ?? Color(c.Background),
            _ => "unbekannter Baustein",
        };
    }

    private static string? Color(string? hex) => hex is null || AccentColor.IsValid(hex) ? null : $"„{hex}“ ist keine Farbe (#RRGGBB)";

    private static string? Text(string? text, string name) => text is { Length: > MaxTextLength } ? $"{name} länger als {MaxTextLength} Zeichen" : null;

    private static string? Size(double? value, string name) => Range(value, 0, MaxSize, name);

    private static string? Range(double? value, double min, double max, string name) =>
        value is { } v && (!double.IsFinite(v) || v < min || v > max) ? $"{name} muss zwischen {min} und {max} liegen" : null;
}
