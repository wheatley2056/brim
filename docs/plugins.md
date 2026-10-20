# Brim plugins

Plugins add content to the notch: a compact status, a short HUD, or cards in the **Plugins** tab. They run as their own processes, in any language, and describe their UI as data. Brim draws it, so a plugin can't break the look of the notch, and a plugin that crashes or hangs can't freeze it.

- **.NET:** reference `Brim.Sdk` (`src/Brim.Sdk`) and use `BrimPlugin`.
- **Anything else:** speak the protocol below. It's newline-delimited JSON-RPC 2.0 over a named pipe.

Examples: [`samples/ServerStatus`](../samples/ServerStatus) (.NET) and [`samples/python-counter`](../samples/python-counter) (plain Python).

## Install and enable

Each plugin is a folder under `%LocalAppData%\Brim\Plugins\` with a `plugin.json`. For development you can add more folders to `PluginDirectories` in `settings.json`. Brim looks for plugins at startup.

New plugins start switched **off**. In the **Plugins** tab, the card on the right lists every plugin with its permissions, its state and a switch. Switching a plugin on grants exactly the permissions shown. If a later version asks for more, the plugin stays off until you switch it on again.

The plugin's stdout and stderr go to `%LocalAppData%\Brim\logs\plugins\<id>.log`.

## `plugin.json`

```json
{
  "id": "de.example.serverstatus",
  "name": "Server status",
  "version": "1.0.0",
  "entry": "ServerStatus.exe",
  "args": [],
  "api": "1",
  "slots": ["compact.trailing", "expanded.tab"],
  "permissions": ["network"],
  "settings": [{ "key": "server", "type": "string", "label": "Server", "default": "github.com:443" }]
}
```

| Field | Meaning |
|---|---|
| `id` | Reverse-domain, lowercase: `^[a-z0-9]+(\.[a-z0-9-]+)+$` |
| `entry` | A file inside the plugin folder (`ServerStatus.exe`), or a command on `PATH` (`py`, `node`). Paths that leave the folder are rejected. |
| `args` | Arguments for `entry`, e.g. `["-3", "plugin.py"]` |
| `api` | Protocol version, currently `"1"` |
| `permissions` | `network`, `media`: shown to the user. Brim can't sandbox a process, so these are declarations. `alerts`: allows `critical` activities, which pop the notch open; without it they are shown as a HUD. `shelf`: receive files from the shelf. `notifications`: receive the Windows notifications of the apps in `notificationApps`. |
| `shelfActions` | Entries in the shelf's right-click menu, e.g. `[{ "id": "upload", "label": "Upload" }]`. When chosen, the plugin gets `shelf.items` with that `id` as `actionId`. Needs `shelf`. |
| `notificationApps` | App display names, e.g. `["Swyx Messenger"]`. Their Windows notifications go to this plugin instead of Brim's own preview. Matching is case-insensitive on a part of the name. Needs `notifications`, and Brim's identity must be set up (Settings → Calendar and notifications). |
| `settings` | Values the user can set (for now in `settings.json` under `Plugins.<id>.Values`). Defaults are sent in `hello`. |

## Connection

Brim creates a pipe with a random name and starts the plugin with:

| Variable | Value |
|---|---|
| `BRIM_PIPE` | Pipe name. On Windows the path is `\\.\pipe\<name>`. |
| `BRIM_PLUGIN_ID` | The plugin's `id` |

Every message is one JSON object on one line, UTF-8 encoded and ending in `\n` (JSON-RPC 2.0). A message may be at most 1 MB.

1. The plugin connects and sends the request `hello`. Brim accepts nothing else before it.
2. Brim answers with the plugin's id, its settings and the permissions it was granted.
3. The plugin sends requests whenever it likes. Brim sends notifications.
4. On `shutdown` the plugin should exit within 2 seconds, otherwise Brim kills it.

If the plugin doesn't say `hello` within 10 seconds, that counts as a crash. After a crash Brim restarts the plugin after 1 s, then after 5 s. The third crash within 10 minutes switches it off until the user switches it on again. Everything a plugin showed disappears when it stops.

### Plugin → Brim (requests)

| Method | Params | Effect |
|---|---|---|
| `hello` | `{ "api": "1" }` | → `{ "id", "api", "settings", "permissions" }` |
| `activity.start` / `activity.update` | activity (below) | Start or replace an activity |
| `activity.end` | `{ "id" }` | Remove it |
| `hud.show` | `{ "glyph", "text", "value"?, "durationMs"? }` or `{ "content" }` | Short message, like the volume HUD. With `value` (0–1) it shows a bar. |
| `view.set` | `{ "id", "content", "order"? }` | Card in the Plugins tab, `order` 0–99 |
| `view.clear` | `{ "id" }` | Remove the card |
| `log` | `{ "message", "level": "info" \| "warn" \| "error" }` | Write to Brim's log |
| `widget.set` | `{ "id", "size", "content" }` | Content of one of your `widgets` (see [Widgets on the Now page](#widgets-on-the-now-page)) in one size |

Activity:

```json
{
  "id": "status",
  "priority": "ongoing",
  "leading":  { "type": "icon", "glyph": "", "color": "#5BD98A" },
  "trailing": { "type": "text", "text": "23 ms", "style": "mono" },
  "bead":     { "type": "icon", "glyph": "", "size": 8, "color": "#5BD98A" },
  "expanded": { "type": "text", "text": "Large content for the Now tab" },
  "rank": 5,
  "background": false,
  "durationMs": null,
  "accent": "#5BD98A",
  "glow": 0.4,
  "announcement": "Spoken by screen readers when it appears"
}
```

- `priority`: `ongoing` (compact, left and right slot), `transient` (HUD, at most 10 s) or `critical` (pops open, needs `alerts`). `idle` is reserved for the clock.
- `rank`: 0–9. Built-in modules such as playing music rank higher, and the second activity becomes a bead next to the notch.
- `accent`: tints the glow along the bottom edge of the notch.
- `glow`: optional level 0–1 that makes the glow pulse, taller and brighter the louder it is (for example a voice in a call). Send updates about 15 times per second; leave it out for a steady glow. Ignored when the user turned off animations.
- `background`: true keeps the activity out of the compact notch but still reachable in the Now tab.

Every `id` is 1–64 characters, without `/` or `|`. Invalid parameters come back as a JSON-RPC error (`-32602`) with a readable message, for example a color that isn't `#RRGGBB`. More than 120 messages per second are refused (`-32000`).

### Brim → Plugin (notifications)

| Method | Params |
|---|---|
| `action.invoked` | `{ "actionId", "value"? }`: a button, switch (`value` 1/0) or slider (`value` 0–1) in your content |
| `settings.changed` | `{ "settings" }` |
| `tab.visibility` | `{ "visible" }`: the Plugins tab opened or closed. Do expensive work only while it's visible. |
| `notch.expanded` | `{ "expanded" }`: the notch opened or closed. Send live values (e.g. a `wave`) only while it's open. |
| `notification.posted` | `{ "app", "title", "body" }`: a Windows notification from one of your `notificationApps` (needs `notifications`) |
| `shelf.items` | `{ "actionId", "paths" }`: files from the shelf (needs `shelf`) |
| `widget.placed` | `{ "widgets": [{ "id", "sizes" }] }`: which of your widgets the user put on the Now page (or that show in the gallery), and in which sizes. Send `widget.set` for exactly these. An empty list means none. |
| `shutdown` | Exit now |

## Widgets on the Now page

Users build their **Now** page from tiles: press and hold (or right-click) the page, then drag, resize, remove, or add widgets from the gallery. Plugins offer widgets in `plugin.json`:

```json
"widgets": [
  { "id": "calls", "name": "Calls", "description": "Current call and quick actions", "glyph": "\uE717", "sizes": ["medium", "large"] }
]
```

| Size | Cells | Content area (DIP) |
|---|---|---|
| `small` | 1 × 1 | 113 × 57 |
| `medium` | 2 × 1 | 258 × 57 |
| `wide` | 4 × 1 | 548 × 57 |
| `large` | 2 × 2 | 258 × 146 |
| `full` | 4 × 2 | 548 × 146 |

The first size is the default in the gallery. Brim draws the card (background, corners, 12 DIP padding); your content fills the area. After `hello`, Brim sends `widget.placed`; answer with `widget.set` for each size, and again whenever the content changes. Buttons in widgets come back as `action.invoked` like everywhere else. A widget disappears from the gallery while your plugin isn't running; users' layouts keep it and show it again when you're back.

With the SDK:

```csharp
brim.WidgetsPlaced += placed => Refresh(placed);
Refresh(brim.PlacedWidgets); // may already be known when ConnectAsync returns

async void Refresh(IReadOnlyDictionary<string, IReadOnlyList<TileSize>> placed)
{
    foreach (var size in placed.GetValueOrDefault("calls") ?? [])
        await brim.SetWidgetAsync("calls", size, CallsWidget(size));
}
```

For analog clocks there's the `clockface` widget (below).

## Widgets

Content is a tree of widgets, each with a `"type"`. Names are camelCase. Colors are `#RRGGBB` or `#AARRGGBB`. Glyphs are characters from Segoe Fluent Icons, e.g. `""` for *Add*.

| `type` | Main fields |
|---|---|
| `text` | `text`, `style` (`body`, `secondary`, `title`, `caption`, `mono`, `monoCaption`, `display`), `color`, `fontSize`, `wrap` |
| `icon` | `glyph`, `size`, `color` |
| `image` | `image: { "id", "bytes" (Base64) }`, `width`, `height`, `cornerRadius` |
| `progress` | `value` 0–1, `color`, `thickness`, `seekActionId` (makes it a slider), `live` |
| `ring` | `value` 0–1, `size`, `thickness`, `color`, `trackColor` |
| `chart` | `values`, `max`, `capacity`, `height`, `color`: a line, newest value on the right |
| `level` | `active`, `color`, `height`: five animated bars |
| `wave` | `values` (0–1, at most 200, newest on the right), `height`, `color`, `barWidth`: a real waveform. Brim glides between updates, so 10–20 updates per second look fluid. |
| `button` | `actionId`, `glyph`, `label`, `kind` (`subtle`, `primary`, `secondary`, `row`), `size`, `glyphSize`, `glyphColor`, `selected` |
| `switch` | `actionId`, `isOn`, `label` |
| `stack` | `children`, `orientation` (`horizontal`/`vertical`), `spacing`, `justify`, `align`, `padding` (number, `[x, y]` or `[l, t, r, b]`), `background`, `cornerRadius` |
| `list` | `items`, `spacing`, `scrollable` |
| `spacer` | `size`, `grow` |
| `clockface` | `hour`, `minute`, `second`, `size`, `style` (`classic`, `minimal`, `numbers`), `showSeconds`, `color`, `accentColor`, `background`: an analog clock; send it once per second |

All widgets also take `accessibleName` (for screen readers), `grow` (share of free space in a stack) and `fixedWidth`.

Limits per message: 400 widgets, 16 levels deep, 2000 characters per text, 1 MB of images.

A card that looks like Brim's own:

```json
{ "type": "stack", "orientation": "vertical", "spacing": 8, "align": "stretch",
  "background": "#1C1D21", "cornerRadius": 14, "padding": [12, 10, 12, 10],
  "children": [
    { "type": "text", "text": "Server", "style": "secondary", "fontSize": 12 },
    { "type": "chart", "values": [20, 24, 22, 31, 18], "capacity": 30, "color": "#5BD98A" },
    { "type": "button", "actionId": "check", "kind": "secondary", "label": "Check now" } ] }
```
