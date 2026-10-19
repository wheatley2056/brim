# Brim

Eine Notch für Windows. Plan: [plan.md](plan.md), Design: [design/boards](design/boards) (aus `Brim – Notch für Windows.html` entpackt).

## Bauen und starten

Voraussetzung: .NET 10 SDK. Visual Studio ist nicht nötig.

```bash
dotnet build Brim.slnx
dotnet test tests/Brim.Core.Tests
dotnet run --project src/Brim
```

Einstellungen: `%LocalAppData%\Brim\settings.json`, Protokoll: `%LocalAppData%\Brim\logs\brim.log`.

Netz- und Kompatibilitätstests (öffentliche Magic-Wormhole-Server, Original-Programm) laufen nur mit
gesetztem `BRIM_WORMHOLE_CLI` (Pfad zu `wormhole.exe`, z. B. aus `pip install magic-wormhole`).

## Teilen

- **Im LAN:** Brims finden sich per DNS-SD und senden direkt über TLS; der Empfänger bestätigt in der Notch.
- **Per Code:** kompatibel mit Magic Wormhole (`wormhole send/receive`, Warp u. a.) über die öffentlichen
  Server; Proxy aus Windows oder eigener HTTP-/SOCKS5-Proxy (`Share` in `settings.json`).
Bedienung: Maus auf die Notch → klappt auf; Klick klappt sofort auf; `Win+Alt+N` per Tastatur; Tray-Symbol → Menü.

## Aufbau

| Projekt | Inhalt |
|---|---|
| `src/Brim.Core` | Aktivitäten und `ActivityHub`, Zustandsmaschine, deklarative Widgets, Geometrie der Formen, Einstellungen, Modul-Schnittstelle. Ohne Windows-Abhängigkeit, vollständig getestet. |
| `src/Brim.Modules` | Eingebaute Module: Uhr, Medien (`GlobalSystemMediaTransportControlsSessionManager`), Audio (Lautstärke-HUD, Menüs für Ausgabegerät und Mixer pro App; Core Audio über NAudio, Gerätewechsel über `IPolicyConfig` als dokumentierte Ausnahme). Liefern nur Widgets, kein WinUI. |
| `src/Brim` | WinUI-3-Shell: ein Fenster pro Monitor, Morph-Engine im Compositor (`NotchSurface`), Widget-Renderer, Tray. |
| `tests/Brim.Core.Tests` | Zustandsmaschine, Ablaufzeiten, Geometrie, Akzentfarbe, Einstellungen. |

Eingebaute Funktionen sind Module und sprechen dieselbe Sprache wie spätere Plugins: Sie melden `Activity`-Objekte mit Priorität und beschreiben ihre Oberfläche als Widgets.

## Bibliotheken

| Paket | Wofür |
|---|---|
| Microsoft.WindowsAppSDK 2.5 | WinUI 3, Composition (Federn, Formen) |
| CommunityToolkit.WinUI.Animations | Ein- und Ausblenden der Perlen |
| Microsoft.Graphics.Win2D | Unschärfe beim Inhaltswechsel (GaussianBlur über BackdropBrush) |
| CommunityToolkit.Mvvm | Befehle im Tray-Menü |
| WinUIEx | Transparentes Fenster, Fensternachrichten |
| H.NotifyIcon.WinUI | Tray-Symbol |
| NAudio.Wasapi | Lautstärke-Ereignisse |
| libsodium | Teilen per Code: NaCl SecretBox und Ed25519 für SPAKE2 (Magic-Wormhole-Protokoll) |
| Microsoft.Windows.CsWin32 | Übrige Win32-Aufrufe (Region, Stile, Hotkey, Taskleiste) |

## Stand

Phase 0 (Fundament) läuft: Uhr, Medien kompakt und erweitert, Lautstärke-HUD, Zustandsmaschine mit Prioritäten und Perlen, Feder-Morph im Compositor, Fenster pro Monitor, Taskleiste oben, Sprachausgabe für HUDs, „Animationseffekte aus“ → Überblenden.

Noch offen in Phase 0: Messung verlorener Frames (Debug-Overlay), Leerlauf-RAM (Debug-Build ~100 MB privat, Ziel < 80 MB).
Die Tabs Ablage, System und Plugins zeigen Platzhalter bis zu ihren Phasen.
