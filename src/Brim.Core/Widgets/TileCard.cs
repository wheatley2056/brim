namespace Brim.Core.Widgets;

/// <summary>
/// Gemeinsame Form der Kacheln auf „Jetzt“ (Entwurf „Jetzt neu“, 20.10.2026): oben eine Zeile aus farbigem Symbol
/// und Name, darunter der Inhalt. Die Karte selbst (Fläche, Ecken 16, Innenabstand 12) zeichnet die Shell.
/// </summary>
public static class TileCard
{
    /// <summary>Kopfzeile: Symbol in der Farbe der Gruppe, Name in Slate, rechts optional ein Wert.</summary>
    public static StackWidget Header(string glyph, string color, string title, string? trailing = null)
    {
        var children = new List<Widget>
        {
            new IconWidget(glyph) { Size = 12, Color = color },
            new TextWidget(title) { Style = TextStyle.Caption, Grow = 1 },
        };
        if (trailing is not null)
        {
            children.Add(new TextWidget(trailing) { Style = TextStyle.Caption });
        }
        return new StackWidget(children) { Spacing = 6 };
    }

    /// <summary>Kopfzeile oben, Inhalt füllt den Rest.</summary>
    public static StackWidget Card(StackWidget header, Widget body) =>
        new(header, new SpacerWidget { Grow = 1 }, body, new SpacerWidget { Grow = 1 })
        {
            Orientation = StackOrientation.Vertical,
            Align = CrossAlign.Stretch,
            Spacing = 6,
            Grow = 1,
        };

    /// <summary>Leerer Zustand (keine Termine, keine Musik): Symbol und ein kurzer Satz, mittig.</summary>
    public static StackWidget Empty(string glyph, string text) =>
        new(new IconWidget(glyph) { Size = 16, Color = BrimColors.Slate },
            new TextWidget(text) { Style = TextStyle.Secondary, FontSize = 12 })
        {
            Spacing = 8,
            Justify = Justify.Center,
            Grow = 1,
        };
}
