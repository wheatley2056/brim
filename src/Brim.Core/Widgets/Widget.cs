namespace Brim.Core.Widgets;

// Deklarative Bausteine (Plan 4.2). Module und später Plugins beschreiben ihre Oberfläche
// als Datenstruktur, die Shell zeichnet sie. Bewusst klein gehalten.

/// <summary>Basis aller Bausteine.</summary>
public abstract record Widget
{
    /// <summary>Optionaler Schlüssel; hilft dem Renderer, Elemente zwischen Aktualisierungen wiederzuverwenden.</summary>
    public string? Key { get; init; }

    /// <summary>Name für die Sprachausgabe (UI Automation).</summary>
    public string? AccessibleName { get; init; }

    /// <summary>Anteil am freien Platz in einem <see cref="StackWidget"/> (wie CSS <c>flex: n</c>). 0 = nur so groß wie nötig.</summary>
    public double Grow { get; init; }

    /// <summary>Feste Breite in DIP, z. B. für Spalten mit Namen.</summary>
    public double? FixedWidth { get; init; }
}

public enum TextStyle
{
    /// <summary>13 px, Fog.</summary>
    Body,
    /// <summary>13 px, Slate.</summary>
    Secondary,
    /// <summary>18 px / 600.</summary>
    Title,
    /// <summary>11 px, Slate.</summary>
    Caption,
    /// <summary>Monospace 13 px, Fog.</summary>
    Mono,
    /// <summary>Monospace 11 px, Slate.</summary>
    MonoCaption,
    /// <summary>Große Ziffern, z. B. Uhr im erweiterten Zustand.</summary>
    Display,
}

public sealed record TextWidget(string Text) : Widget
{
    public TextStyle Style { get; init; } = TextStyle.Body;
    public string? Color { get; init; }
    public double? FontSize { get; init; }
    public bool Ellipsis { get; init; } = true;

    /// <summary>Über mehrere Zeilen umbrechen statt abschneiden (z. B. Changelog).</summary>
    public bool Wrap { get; init; }
}

/// <summary>Symbol aus Segoe Fluent Icons (Fallback: Segoe MDL2 Assets).</summary>
public sealed record IconWidget(string Glyph) : Widget
{
    public double Size { get; init; } = 16;
    public string? Color { get; init; }
}

/// <summary>Bilddaten mit stabiler Kennung, damit unveränderte Bilder nicht neu dekodiert werden.</summary>
public sealed record ImageData(string Id, byte[] Bytes);

public sealed record ImageWidget(ImageData? Image) : Widget
{
    public double Width { get; init; } = 22;
    public double Height { get; init; } = 22;
    public double CornerRadius { get; init; } = 6;

    /// <summary>Farbverlauf (zwei Hex-Farben) als Platzhalter, solange kein Bild da ist.</summary>
    public (string From, string To)? Placeholder { get; init; }
}

public sealed record ProgressWidget(double Value) : Widget
{
    public string? Color { get; init; }
    public double Thickness { get; init; } = 4;

    /// <summary>Wenn gesetzt, ist die Leiste klickbar; der Wert (0..1) geht als Aktion an das Modul.</summary>
    public string? SeekActionId { get; init; }

    /// <summary>Beim Ziehen laufend melden (Lautstärke) oder erst beim Loslassen (Spulen).</summary>
    public bool Live { get; init; } = true;
}

public sealed record RingWidget(double Value) : Widget
{
    public double Size { get; init; } = 20;
    public double Thickness { get; init; } = 2.5;
    public string? Color { get; init; }
    public string? TrackColor { get; init; }
}

/// <summary>Pegel aus fünf Balken (Musik läuft). Animiert, solange <see cref="Active"/>.</summary>
public sealed record LevelWidget : Widget
{
    public bool Active { get; init; } = true;
    public string? Color { get; init; }
    public double Height { get; init; } = 18;
}

public enum ButtonKind
{
    /// <summary>Nur Symbol, transparenter Hintergrund.</summary>
    Subtle,
    /// <summary>Heller runder Knopf (Play/Pause).</summary>
    Primary,
    /// <summary>Graphit-Fläche mit Text.</summary>
    Secondary,
    /// <summary>Volle Breite, Symbol und Text links, Haken rechts (Listen wie die Geräteauswahl).</summary>
    Row,
}

public sealed record ButtonWidget(string ActionId) : Widget
{
    public string? Glyph { get; init; }
    public string? Label { get; init; }
    public ButtonKind Kind { get; init; } = ButtonKind.Subtle;
    public double Size { get; init; } = 44;
    public double GlyphSize { get; init; } = 18;
    public string? GlyphColor { get; init; }

    /// <summary>Für <see cref="ButtonKind.Row"/>: ausgewählt (hervorgehoben, mit Haken).</summary>
    public bool Selected { get; init; }
}

public sealed record SwitchWidget(string ActionId, bool IsOn) : Widget
{
    public string? Label { get; init; }
}

public enum StackOrientation { Horizontal, Vertical }

public enum Justify { Start, Center, End, SpaceBetween }

public enum CrossAlign { Start, Center, End, Stretch }

public sealed record StackWidget(IReadOnlyList<Widget> Children) : Widget
{
    public StackWidget(params Widget[] children) : this((IReadOnlyList<Widget>)children) { }

    public StackOrientation Orientation { get; init; } = StackOrientation.Horizontal;
    public double Spacing { get; init; }
    public Justify Justify { get; init; } = Justify.Start;
    public CrossAlign Align { get; init; } = CrossAlign.Center;

    /// <summary>Innenabstand links, oben, rechts, unten.</summary>
    public (double Left, double Top, double Right, double Bottom) Padding { get; init; }
}

public sealed record ListWidget(IReadOnlyList<Widget> Items) : Widget
{
    public double Spacing { get; init; } = 6;

    /// <summary>Senkrecht scrollen, wenn die Einträge nicht in den Platz passen.</summary>
    public bool Scrollable { get; init; }
}

/// <summary>Leerer Platzhalter, nimmt mit <see cref="Widget.Grow"/> freien Platz ein.</summary>
public sealed record SpacerWidget : Widget
{
    public double Size { get; init; }
}
