using Brim.Core.Widgets;

namespace Brim.Core;

/// <summary>
/// Größe einer Kachel auf der Seite „Jetzt“, in Feldern eines Rasters aus 4 × 2 Feldern pro Seite.
/// </summary>
public enum TileSize
{
    /// <summary>1 × 1: eine Uhr, ein Wert.</summary>
    Small,
    /// <summary>2 × 1: Uhr mit Datum, nächster Termin.</summary>
    Medium,
    /// <summary>4 × 1: ein breiter Streifen.</summary>
    Wide,
    /// <summary>2 × 2: halbe Seite.</summary>
    Large,
    /// <summary>4 × 2: ganze Seite, z. B. die laufende Aktivität.</summary>
    Full,
}

/// <summary>
/// Ein Widget, das man auf „Jetzt“ legen kann, aus einem Modul oder Plugin (Galerie).
/// Der Inhalt kommt getrennt je Größe über <see cref="ActivityHub.SetTile"/>, nur für Größen, die liegen.
/// </summary>
public sealed record TileDefinition
{
    public required string ModuleId { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>Gruppe in der Galerie, z. B. „Uhren“ oder der Name des Plugins.</summary>
    public string Category { get; init; } = "Brim";

    public string? Glyph { get; init; }
    public required IReadOnlyList<TileSize> Sizes { get; init; }

    public TileSize DefaultSize => Sizes.Count > 0 ? Sizes[0] : TileSize.Medium;

    public string Key => ActivityKey.Of(ModuleId, Id);
}

/// <summary>Eine gelegte Kachel: welches Widget in welcher Größe. Die Reihenfolge der Liste ist die Anordnung.</summary>
public sealed record TilePlacement(string Key, TileSize Size);

/// <summary>Wo eine Kachel nach dem Packen liegt.</summary>
public readonly record struct TileSlot(int Index, int Page, int Column, int Row, int Width, int Height);

/// <summary>Aufbau der Seite „Jetzt“ (Einstellungen; Bearbeiten in der Notch).</summary>
public sealed class NowSettings
{
    /// <summary>Die laufende Aktivität (Musik, Anruf, Timer …); läuft nichts, zeigt sie „Heute“.</summary>
    public const string ActivityTile = "brim/activity";

    /// <summary>Stand des Standardaufbaus; ältere Einstellungen mit dem alten Standard bekommen den neuen.</summary>
    public const int CurrentRevision = 1;

    /// <summary>
    /// Standard: links die Aktivität als halbe Seite, rechts Termine, eine Uhr und die Systemlast. Fehlt ein
    /// Widget (z. B. Kalender ohne Paketidentität), zeigt seine Kachel, wie man es einrichtet.
    /// </summary>
    public static List<TilePlacement> DefaultTiles() =>
    [
        new(ActivityTile, TileSize.Large),
        new("calendar/agenda", TileSize.Medium),
        new("clock/analog", TileSize.Small),
        new("resources/load", TileSize.Small),
    ];

    public List<TilePlacement> Tiles { get; set; } = DefaultTiles();

    public int Revision { get; set; }

    /// <summary>
    /// Nach dem Laden: Wer noch den Standard von 2.0 hat (nur die Aktivität über die ganze Seite), bekommt den
    /// neuen Standard – einmal, danach bleibt jeder Aufbau, wie er ist.
    /// </summary>
    public void Upgrade()
    {
        if (Revision < 1 && Tiles is [{ Key: ActivityTile, Size: TileSize.Full }])
        {
            Tiles = DefaultTiles();
        }
        Revision = CurrentRevision;
    }
}

/// <summary>Raster der Seite „Jetzt“: 4 × 2 Felder pro Seite, Kacheln in Lesereihenfolge.</summary>
public static class Dashboard
{
    public const int Columns = 4, Rows = 2;

    /// <summary>
    /// Maße in DIP: Die Seite „Jetzt“ ist 572 × 170 groß (aufgeklappte Notch ohne Kopfzeile), Felder mit 8 Abstand,
    /// jede Kachel hat 12 Innenabstand. Module richten ihre Inhalte nach <see cref="ContentSize"/>.
    /// </summary>
    public const double CellWidth = 137, CellHeight = 81, Gap = 8, TilePadding = 12;

    /// <summary>Außenmaß einer Kachel.</summary>
    public static (double Width, double Height) PixelSize(TileSize size)
    {
        var (w, h) = Cells(size);
        return (w * CellWidth + (w - 1) * Gap, h * CellHeight + (h - 1) * Gap);
    }

    /// <summary>Platz für den Inhalt einer Kachel (ohne Innenabstand).</summary>
    public static (double Width, double Height) ContentSize(TileSize size)
    {
        var (w, h) = PixelSize(size);
        return (w - 2 * TilePadding, h - 2 * TilePadding);
    }

    public static (int Width, int Height) Cells(TileSize size) => size switch
    {
        TileSize.Small => (1, 1),
        TileSize.Medium => (2, 1),
        TileSize.Wide => (4, 1),
        TileSize.Large => (2, 2),
        _ => (4, 2),
    };

    /// <summary>
    /// Legt die Kacheln der Reihe nach an die erste freie Stelle (zeilenweise), wie Widgets auf dem iPhone.
    /// Passt eine nicht mehr auf die Seite, beginnt eine neue; Lücken davor dürfen spätere kleinere füllen.
    /// </summary>
    public static IReadOnlyList<TileSlot> Pack(IReadOnlyList<TilePlacement> tiles)
    {
        var slots = new List<TileSlot>(tiles.Count);
        var pages = new List<bool[,]>();
        for (var index = 0; index < tiles.Count; index++)
        {
            var (w, h) = Cells(tiles[index].Size);
            // Frühestens auf der Seite der vorigen Kachel, damit die Reihenfolge über Seiten erhalten bleibt.
            var page = slots.Count > 0 ? slots[^1].Page : 0;
            while (true)
            {
                while (pages.Count <= page)
                {
                    pages.Add(new bool[Columns, Rows]);
                }
                if (Find(pages[page], w, h) is { } spot)
                {
                    for (var c = spot.Column; c < spot.Column + w; c++)
                    {
                        for (var r = spot.Row; r < spot.Row + h; r++)
                        {
                            pages[page][c, r] = true;
                        }
                    }
                    slots.Add(new TileSlot(index, page, spot.Column, spot.Row, w, h));
                    break;
                }
                page++;
            }
        }
        return slots;
    }

    public static int PageCount(IReadOnlyList<TileSlot> slots) => slots.Count == 0 ? 1 : slots.Max(s => s.Page) + 1;

    /// <summary>Freie Felder einer Seite (Bearbeiten: gestrichelt, Klick öffnet die Galerie), in Lesereihenfolge.</summary>
    public static IReadOnlyList<(int Column, int Row)> FreeCells(IReadOnlyList<TileSlot> slots, int page)
    {
        var used = new bool[Columns, Rows];
        foreach (var slot in slots.Where(s => s.Page == page))
        {
            for (var c = slot.Column; c < slot.Column + slot.Width; c++)
            {
                for (var r = slot.Row; r < slot.Row + slot.Height; r++)
                {
                    used[c, r] = true;
                }
            }
        }
        var free = new List<(int, int)>();
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                if (!used[column, row])
                {
                    free.Add((column, row));
                }
            }
        }
        return free;
    }

    private static (int Column, int Row)? Find(bool[,] used, int w, int h)
    {
        for (var row = 0; row + h <= Rows; row++)
        {
            for (var column = 0; column + w <= Columns; column++)
            {
                var free = true;
                for (var c = column; c < column + w && free; c++)
                {
                    for (var r = row; r < row + h && free; r++)
                    {
                        free = !used[c, r];
                    }
                }
                if (free)
                {
                    return (column, row);
                }
            }
        }
        return null;
    }

    /// <summary>Verschiebt eine Kachel an eine neue Stelle der Reihenfolge (Ziehen im Bearbeiten-Modus).</summary>
    public static List<TilePlacement> Move(IReadOnlyList<TilePlacement> tiles, int from, int to)
    {
        var list = tiles.ToList();
        if (from < 0 || from >= list.Count)
        {
            return list;
        }
        var tile = list[from];
        list.RemoveAt(from);
        list.Insert(Math.Clamp(to, 0, list.Count), tile);
        return list;
    }

    /// <summary>Nächste erlaubte Größe (Tippen auf die Größe im Bearbeiten-Modus).</summary>
    public static TileSize NextSize(TileSize current, IReadOnlyList<TileSize> allowed)
    {
        if (allowed.Count == 0)
        {
            return current;
        }
        var index = -1;
        for (var i = 0; i < allowed.Count; i++)
        {
            if (allowed[i] == current)
            {
                index = i;
            }
        }
        return allowed[(index + 1) % allowed.Count];
    }
}
