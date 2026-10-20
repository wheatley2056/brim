"""
Erzeugt die Lottie-Animationen der Kurzanleitung (src/Brim/Assets/Onboarding/*.json).

Bewusst als Code statt aus After Effects: Die Animationen sind einfache Formen in Brims Farben, so lassen sie
sich nachvollziehbar ändern. Nur Rechtecke, Ellipsen, Pfade, Füllung, Kontur und Transformation – das kann
Lottie-Windows (CommunityToolkit.WinUI.Lottie) vollständig.

    python build/onboarding/make_lottie.py
"""
import json
import pathlib

W, H, FPS, LENGTH = 240, 140, 30, 120
OUT = pathlib.Path(__file__).resolve().parents[2] / "src" / "Brim" / "Assets" / "Onboarding"

BLACK = [0, 0, 0, 1]
EDGE = [0x3A / 255, 0x3B / 255, 0x40 / 255, 1]
GRAPHITE = [0x1C / 255, 0x1D / 255, 0x21 / 255, 1]
SLATE = [0x9A / 255, 0x9E / 255, 0xA8 / 255, 1]
FOG = [0xF2 / 255, 0xF3 / 255, 0xF5 / 255, 1]
ACCENT = [0xFF / 255, 0x8A / 255, 0x66 / 255, 1]
GREEN = [0x5B / 255, 0xD9 / 255, 0x8A / 255, 1]

EASE_OUT = {"x": [0.2], "y": [1]}
EASE_IN = {"x": [0.6], "y": [0]}


def static(value):
    return {"a": 0, "k": value}


def animated(*keys):
    """keys: (frame, value); weich zwischen allen Stellen."""
    frames = []
    for i, (t, v) in enumerate(keys):
        key = {"t": t, "s": v if isinstance(v, list) else [v]}
        if i < len(keys) - 1:
            key["i"] = EASE_OUT
            key["o"] = EASE_IN
        frames.append(key)
    return {"a": 1, "k": frames}


def transform(position=None, opacity=None, scale=None):
    return {
        "o": opacity or static(100),
        "r": static(0),
        "p": position or static([0, 0, 0]),
        "a": static([0, 0, 0]),
        "s": scale or static([100, 100, 100]),
    }


def group(*items):
    return {"ty": "gr", "it": [*items, {"ty": "tr", "p": static([0, 0]), "a": static([0, 0]), "s": static([100, 100]), "r": static(0), "o": static(100)}]}


def rect(size, position, radius):
    return {"ty": "rc", "d": 1, "s": size, "p": position, "r": radius}


def ellipse(size, position):
    return {"ty": "el", "d": 1, "s": size, "p": position}


def path(points, closed=True):
    return {"ty": "sh", "ks": static({"c": closed, "v": points, "i": [[0, 0]] * len(points), "o": [[0, 0]] * len(points)})}


def fill(color, opacity=None):
    return {"ty": "fl", "c": static(color), "o": opacity or static(100), "r": 1}


def stroke(color, width):
    return {"ty": "st", "c": static(color), "o": static(100), "w": static(width), "lc": 2, "lj": 2}


def layer(index, name, shapes, ks=None):
    return {"ddd": 0, "ind": index, "ty": 4, "nm": name, "sr": 1, "ks": ks or transform(), "ao": 0,
            "shapes": shapes, "ip": 0, "op": LENGTH, "st": 0, "bm": 0}


def notch(keys):
    """Notch oben in der Mitte; keys: (frame, Breite, Höhe, Radius). Oben 12 px über die Kante, damit nur unten rund."""
    size = animated(*[(t, [w, h + 12]) for t, w, h, r in keys])
    position = animated(*[(t, [W / 2, (h + 12) / 2 - 12]) for t, w, h, r in keys])
    radius = animated(*[(t, r) for t, w, h, r in keys])
    return group(rect(size, position, radius), fill(BLACK), stroke(EDGE, 1.5))


def cursor(keys):
    """Mauspfeil; keys: (frame, [x, y]) der Spitze."""
    arrow = [[0, 0], [0, 17], [4.5, 13], [7.5, 20], [10.5, 18.5], [7.5, 12], [13, 12]]
    ks = transform(position=animated(*[(t, [x, y, 0]) for t, (x, y) in keys]))
    return group(path(arrow), fill(FOG), stroke(BLACK, 1.2)), ks


def document(lambda_scale=None):
    sheet = [[-11, -14], [5, -14], [11, -8], [11, 14], [-11, 14]]
    fold = [[5, -14], [5, -8], [11, -8]]
    return group(path(sheet), fill(FOG), group(path(fold), fill(ACCENT)))


def save(name, layers):
    doc = {"v": "5.7.4", "fr": FPS, "ip": 0, "op": LENGTH, "w": W, "h": H, "nm": name, "ddd": 0, "assets": [],
           "layers": layers}
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / f"{name}.json").write_text(json.dumps(doc, separators=(",", ":")), encoding="utf-8")


def hover():
    # Maus kommt von unten rechts, die Notch klappt auf, die Maus geht, sie klappt zu.
    small, big = (0, 84, 20, 10), (0, 168, 76, 22)
    shape = notch([(0, 84, 20, 10), (34, 84, 20, 10), (40, 92, 24, 12), (52, 168, 76, 22), (84, 168, 76, 22), (100, 84, 20, 10), (120, 84, 20, 10)])
    arrow, ks = cursor([(0, [200, 130]), (32, [124, 14]), (84, [130, 30]), (104, [205, 128]), (120, [200, 130])])
    # Inhalt der offenen Notch: drei Zeilen, die mit ihr erscheinen.
    rows = []
    for i, width in enumerate([90, 120, 70]):
        rows.append(group(rect(static([width, 7]), static([W / 2 - 60 + width / 2, 18 + i * 14]), static(3.5)),
                          fill(SLATE if i else FOG)))
    content = layer(2, "inhalt", rows, transform(opacity=animated((0, 0), (50, 0), (58, 100), (84, 100), (90, 0), (120, 0))))
    save("hover", [layer(1, "maus", [arrow], ks), content, layer(3, "notch", [shape])])


def shelf():
    # Eine Datei wird von unten links zur Notch gezogen; die Notch wird breiter, schluckt sie und zeigt einen Haken.
    shape = notch([(0, 84, 20, 10), (30, 84, 20, 10), (44, 140, 40, 18), (70, 140, 40, 18), (82, 84, 20, 10), (120, 84, 20, 10)])
    file_ks = transform(
        position=animated((0, [40, 112, 0]), (44, [120, 46, 0]), (58, [120, 18, 0]), (120, [120, 18, 0])),
        opacity=animated((0, 0), (8, 100), (54, 100), (62, 0), (120, 0)),
        scale=animated((0, [100, 100, 100]), (44, [100, 100, 100]), (60, [40, 40, 100]), (120, [40, 40, 100])))
    arrow, cursor_ks = cursor([(0, [52, 124]), (44, [132, 58]), (60, [150, 70]), (90, [205, 125]), (120, [205, 125])])
    tick = group(path([[-6, 0], [-1.5, 4.5], [7, -4.5]], closed=False), stroke(GREEN, 2.4))
    tick_ks = transform(position=static([W / 2, 10, 0]), opacity=animated((0, 0), (62, 0), (68, 100), (78, 100), (84, 0), (120, 0)))
    save("shelf", [layer(1, "maus", [arrow], cursor_ks), layer(2, "datei", [document()], file_ks),
                   layer(3, "haken", [tick], tick_ks), layer(4, "notch", [shape])])


def customize():
    # Raster aus Kacheln: Die erste wandert nach rechts, die anderen rücken nach (Jetzt bearbeiten).
    left, top, cell_w, cell_h, gap = 36, 28, 38, 34, 6
    panel = group(rect(static([4 * cell_w + 3 * gap + 16, 2 * cell_h + gap + 16]), static([W / 2, top + cell_h + gap / 2]), static(14)),
                  fill(BLACK), stroke(EDGE, 1.5))

    def x(column):
        return left + column * (cell_w + gap) + cell_w / 2

    layers = []
    y0, y1 = top + cell_h / 2, top + cell_h + gap + cell_h / 2
    # Kachel A (Akzent) wandert von Spalte 0 nach 2, B und C rücken nach links.
    moves = {
        "A": (ACCENT, [(0, x(0)), (30, x(0)), (60, x(2)), (90, x(2)), (110, x(0)), (120, x(0))], y0),
        "B": (GRAPHITE, [(0, x(1)), (36, x(1)), (60, x(0)), (90, x(0)), (110, x(1)), (120, x(1))], y0),
        "C": (GRAPHITE, [(0, x(2)), (42, x(2)), (62, x(1)), (90, x(1)), (110, x(2)), (120, x(2))], y0),
    }
    index = 1
    for name, (color, keys, y) in moves.items():
        ks = transform(position=animated(*[(t, [px, y, 0]) for t, px in keys]),
                       scale=animated((0, [100, 100, 100]), (30, [100, 100, 100]), (36, [110, 110, 100]), (56, [110, 110, 100]), (62, [100, 100, 100]), (120, [100, 100, 100])) if name == "A" else None)
        layers.append(layer(index, name, [group(rect(static([cell_w, cell_h]), static([0, 0]), static(8)), fill(color), stroke(SLATE, 1))], ks))
        index += 1
    fixed = [group(rect(static([cell_w, cell_h]), static([x(3), y0]), static(8)), fill(GRAPHITE), stroke(SLATE, 1)),
             group(rect(static([2 * cell_w + gap, cell_h]), static([(x(0) + x(1)) / 2, y1]), static(8)), fill(GRAPHITE), stroke(SLATE, 1)),
             group(rect(static([2 * cell_w + gap, cell_h]), static([(x(2) + x(3)) / 2, y1]), static(8)), fill(GRAPHITE), stroke(SLATE, 1))]
    layers.append(layer(index, "feste", fixed))
    layers.append(layer(index + 1, "rahmen", [panel]))
    save("customize", layers)


if __name__ == "__main__":
    hover()
    shelf()
    customize()
    print("Lottie-Animationen in", OUT)
