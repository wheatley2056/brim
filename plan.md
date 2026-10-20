# Kerbe – Plan

Stand: 19.10.2026 · Basis: bestehender WinUI-3-Prototyp (C#, Windows App SDK)

## 1. Ziel

Eine Notch für Windows, die sich anfühlt wie ein Teil des Systems: Sie nutzt ausschließlich offizielle Windows-Schnittstellen, bietet alles, was die Notch am Mac kann, und lässt sich über Plugins erweitern. Sie stört nie. Bei Vollbild oder nach eigenen Regeln morpht sie flüssig aus dem Bild.

## 2. Anforderungen

| # | Anforderung | Kurz |
|---|---|---|
| A1 | Volle Windows-Kompatibilität | Nur offizielle APIs, Windows 10 22H2 und 11, DPI, Multi-Monitor, Barrierefreiheit |
| A2 | Eigene Plugin-API | Dritte können Inhalte, HUDs und Ablagen beisteuern |
| A3 | Standardfunktionen der Mac-Notch | Medien, HUDs, Laden, Timer, Kalender, Benachrichtigungen, Kamera/Mikro |
| A4 | Ablage (Shelf) | Dateien und Ordner reinziehen, bleiben drin, wieder rausziehen |
| A5 | Uhrzeit | Immer verfügbar, im Ruhezustand Standardinhalt |
| A6 | Akku | Nur auf Laptops: Stand, Laden, Restzeit, Energiesparmodus |
| A7 | Ressourcen | Werte wie im Task-Manager: CPU, RAM, GPU, Datenträger, Netzwerk |
| A8 | FPS und Latenz in Spielen | Bildrate, Frametime, Latenz |
| A9 | Automatisches Ausblenden | Einstellbare Regeln, morphende, flüssige Animationen |
| A10 | Teilen zwischen Brims | Dateien aus der Ablage an andere Brim-Nutzer: im LAN ohne Backend, übers Internet per Code (mit Proxy) |

## 3. Architektur

```
┌──────────────────────────── Kerbe.exe (WinUI 3) ────────────────────────────┐
│  Shell          Fenster pro Monitor · Morph-Engine · Zustandsmaschine · Hover │
│  Core-Module    Medien · Uhr · Akku · Ressourcen · FPS · Ablage · HUDs · …     │
│  Plugin-Host    lädt Plugins, verteilt Ereignisse, rendert deklarative UI     │
│  Einstellungen  Regeln, Animation, Module an/aus, Plugin-Rechte               │
└──────────────────────────────┬───────────────────────────────────────────────┘
                               │ Named Pipe · JSON-RPC
         ┌─────────────────────┼─────────────────────┐
   Kerbe.Telemetry.exe    Plugin A.exe          Plugin B.exe
   (optional, erhöht:     (beliebige Sprache)   (.NET mit SDK)
   ETW für FPS)
```

Grundsätze:
- **Eingebaute Funktionen sind selbst Module** und nutzen intern dieselbe Schnittstelle wie Plugins. So bleibt die API vollständig und getestet.
- **Plugins laufen in eigenen Prozessen.** Ein abstürzendes oder hängendes Plugin kann die Notch nicht einfrieren.
- **Alles, was Rechte braucht, liegt in einem separaten Hilfsprozess.** Die Notch selbst läuft immer ohne Adminrechte.

### Zustandsmaschine

Jedes Modul meldet Aktivitäten mit Priorität. Die Shell entscheidet, was gezeigt wird:

| Priorität | Beispiel | Darstellung |
|---|---|---|
| Kritisch | Akku unter 10 %, Timer abgelaufen | erweitert sich kurz von selbst |
| Vorübergehend | Lautstärke, Helligkeit, Ladekabel ein | HUD für 1,6 s |
| Laufend | Musik, Timer, Bildschirmaufnahme, FPS | kompakt, links und rechts je ein Slot |
| Ruhe | Uhrzeit | Minimalform |

Laufen mehrere Aktivitäten gleichzeitig, zeigt die kompakte Ansicht die wichtigste. Weitere erscheinen als kleine Punkte daneben, wie bei mehreren Live-Aktivitäten auf dem iPhone. Im erweiterten Zustand gibt es Tabs: **Jetzt · Ablage · System · Plugins**.

## 4. Module im Detail

### 4.1 Windows-Kompatibilität (A1)

| Thema | Umsetzung |
|---|---|
| Versionen | Windows 10 22H2 und Windows 11 23H2 aufwärts, x64 und ARM64 |
| Identität | MSIX-Paket (oder Sparse Package bei unverpackter Installation). Nötig für Benachrichtigungszugriff, Autostart über `StartupTask` und saubere Deinstallation |
| DPI | Per-Monitor V2, Neuberechnung bei DPI-Wechsel |
| Multi-Monitor | Ein Notch-Fenster pro `DisplayArea`; einstellbar: nur Hauptmonitor, alle, oder Monitor mit Maus |
| Taskleiste oben | Erkennen über `SHAppBarMessage` und Notch darunter setzen oder Taskleiste meiden |
| Design | Hell/Dunkel folgt dem System, Kontrastdesigns: Notch bekommt Systemrahmenfarbe |
| Barrierefreiheit | UI-Automation-Namen für alles, Sprachausgabe-Ankündigungen für HUDs, Tastatur über globales Tastenkürzel (z. B. Win+Alt+N) |
| Bewegung | Respektiert „Animationseffekte“; dann Überblenden statt Morph |
| Nicht stören | Respektiert Benachrichtigungsmodus „Nicht stören“; kritische Hinweise trotzdem |
| Leistung | Leerlauf unter 0,5 % CPU und unter 80 MB RAM; Abfragen pausieren, wenn Notch versteckt oder Bildschirm aus |

### 4.2 Plugin-API (A2)

**Modell: deklarative Widgets, eigene Prozesse.** Das Plugin beschreibt seine Oberfläche als Datenstruktur, die Notch zeichnet sie. Plugins können damit das Aussehen nicht kaputt machen und laufen in jeder Sprache.

**Manifest** `plugin.json`:
```json
{
  "id": "de.twogg.serverstatus",
  "name": "TwoGG Serverstatus",
  "version": "1.0.0",
  "entry": "ServerStatus.exe",
  "api": "1",
  "slots": ["compact.trailing", "expanded.tab"],
  "permissions": ["network"],
  "settings": [{ "key": "server", "type": "string", "label": "Server-Adresse" }]
}
```

**Slots**, in die Plugins Inhalte liefern:

| Slot | Inhalt |
|---|---|
| `compact.leading` / `compact.trailing` | Icon, kurzer Text, Pegel, Ring |
| `hud` | vorübergehende Meldung mit Wert und Icon |
| `expanded.tab` | eigener Tab mit Karten, Listen, Buttons, Fortschritt |
| `shelf.action` | Aktion für Dateien in der Ablage, z. B. „Hochladen“ |

**Bausteine der deklarativen UI:** Text, Icon (Segoe Fluent Icons oder eigenes PNG/SVG), Bild, Fortschritt, Ring, Pegel, Button, Liste, Stack, Schalter. Mehr nicht, bewusst.

**Protokoll:** JSON-RPC 2.0 über Named Pipe `\\.\pipe\kerbe-<id>`

| Richtung | Methode | Zweck |
|---|---|---|
| Plugin → Kerbe | `activity.start / update / end` | laufende Aktivität mit Priorität |
| Plugin → Kerbe | `hud.show` | kurze Meldung |
| Plugin → Kerbe | `view.set` | Inhalt eines Slots setzen |
| Kerbe → Plugin | `action.invoked` | Button gedrückt |
| Kerbe → Plugin | `shelf.items` | Dateien an eine Ablage-Aktion übergeben |
| Kerbe → Plugin | `settings.changed`, `lifecycle.suspend/resume` | Konfiguration, Energiesparen |

**SDK:** NuGet-Paket `Kerbe.Sdk` für .NET mit typisierten Klassen; das Protokoll ist dokumentiert, damit auch Python, Node oder Rust gehen.

**Sicherheit:** Plugins bekommen nur, was im Manifest steht und der Nutzer beim Installieren bestätigt hat (Netzwerk, Dateien der Ablage, Mediensteuerung). Pro Plugin Zeitlimits für Antworten, Neustart nach Absturz mit Backoff, Abschalten nach drei Abstürzen.

**Verwaltung:** Plugins liegen in `%LocalAppData%\Kerbe\Plugins\<id>\`. Einstellungsseite listet sie mit Schalter, Rechten und Logs.

### 4.3 Standardfunktionen der Mac-Notch (A3)

| Funktion | Windows-Schnittstelle | Hinweis |
|---|---|---|
| Medien mit Cover und Steuerung | `GlobalSystemMediaTransportControlsSessionManager` | vorhanden; Ergänzung: mehrere Quellen umschaltbar, Spulen über Fortschrittsleiste |
| Lautstärke-HUD | Core Audio `IAudioEndpointVolume` | vorhanden; plus Ausgabegerät wählen im erweiterten Zustand |
| Helligkeits-HUD | WMI `WmiMonitorBrightnessEvent` (Laptop), DDC/CI über `dxva2` (externe Monitore) | |
| Lade-HUD | siehe Akku | Animation beim Einstecken |
| Timer und Stoppuhr | eigenes Modul | läuft als Aktivität im kompakten Zustand |
| Kalender: nächster Termin | Outlook/Exchange über Plugin, Windows-Kalender über `AppointmentStore` | `AppointmentStore` braucht Paketidentität |
| Benachrichtigungen spiegeln | `UserNotificationListener` | braucht Paketidentität und Nutzerzustimmung; nur Vorschau, Klick öffnet die App |
| Kamera/Mikrofon in Benutzung | Registry `CapabilityAccessManager\ConsentStore` (`LastUsedTimeStop = 0`) | grüner/oranger Punkt wie am Mac |
| Bildschirmaufnahme aktiv | Prüfung auf Game Bar/Snipping-Aufnahme | später, niedrige Priorität |
| Bluetooth-Geräte verbunden | `Windows.Devices.Bluetooth` + Akkustand über GATT | z. B. Kopfhörer-Akku als HUD |
| Teilen | Windows-Teilen-Dialog (`DataTransferManager`) auf Ablage-Dateien | Ersatz für AirDrop |

### 4.4 Ablage / Shelf (A4)

**Verhalten**
- Datei oder Ordner aus dem Explorer zur Notch ziehen → Notch erweitert sich beim Drüberziehen und öffnet den Tab Ablage.
- Loslassen → Eintrag erscheint mit Symbol, Name und Größe.
- Einträge bleiben nach Neustart erhalten.
- Aus der Notch herausziehen → in Explorer, Desktop, Mail, Discord, Browser-Upload usw.
- Mehrfachauswahl, Entfernen per Rechtsklick oder Ziehen nach außen mit „Nach dem Herausziehen entfernen“ (einstellbar).

**Speicherung, Nutzer wählt:**

| Modus | Verhalten | Standard |
|---|---|---|
| Verweis | merkt sich den Pfad, verschiebt nichts | ja |
| Kopie | kopiert nach `%LocalAppData%\Kerbe\Shelf\` | für Dateien aus temporären Orten (Browser-Downloads, Mail-Anhänge) automatisch |

Fehlt eine verwiesene Datei später, wird der Eintrag ausgegraut mit „Nicht mehr gefunden“.

**Umsetzung**
- Hineinziehen: `AllowDrop`, `DragEnter` als Hover-Auslöser, `DataPackageView.GetStorageItemsAsync()`.
- Herausziehen: `CanDrag` + `DragStarting` mit `DataPackage.SetStorageItems()`.
- Speicher: `shelf.json` mit Pfad, Modus, Zeitpunkt, Vorschaubild-Cache über `StorageFile.GetThumbnailAsync`.
- Zusätzlich optional: Text, Links und Bilder aus der Windows-Zwischenablage (`Clipboard.ContentChanged`) mit einem Klick in die Ablage übernehmen.

**Technisches Risiko:** Ziehen über ein Fenster mit `WS_EX_NOACTIVATE` und Fensterregion muss früh getestet werden (siehe Risiken).

### 4.4a Teilen zwischen Brims (A10)

Ergänzt am 19.10.2026. Der Knopf „Teilen“ in der Ablage öffnet ein Menü: Brims in der Nähe · „Per Code senden …“ · „Windows-Teilen …“ (bisheriger Dialog für Mail, Teams usw.).

**Stufe 1 – lokales Netz, ganz ohne Backend**

| Teil | Umsetzung |
|---|---|
| Identität | Pro Installation ein selbstsigniertes Zertifikat (ECDSA P-256), mit DPAPI geschützt in `%LocalAppData%\Brim\identity.bin`; Gerätename „Brim von *Nutzer*“ |
| Finden | DNS-SD/mDNS, Dienst `_brim._tcp.local`: Anmelden über `DnsServiceRegister` (Win32), Suchen über `DeviceWatcher` (WinRT). TXT-Einträge: Kennung, Name, Fingerabdruck des Zertifikats |
| Übertragen | Direkte TCP-Verbindung mit gegenseitigem TLS. Der Fingerabdruck muss zum Eintrag aus der Suche passen (kein Mithören, kein Unterschieben im LAN) |
| Ablauf | Angebot (Namen, Größen) → Annahme-Frage in der Notch beim Empfänger (kritischer Hinweis, 60 s) → Dateien → landen in der Ablage des Empfängers. Fortschritt auf beiden Seiten als Aktivität |
| Sicherheit | Nichts wird ohne Zustimmung angenommen; Pfade aus dem Angebot werden bereinigt (kein `..`, keine absoluten Pfade); Grenzen für Anzahl und Kopfgröße |
| Firewall | Beim ersten Empfang fragt Windows einmal nach (privates Netz) |

**Stufe 2 – Internet per Code (Magic Wormhole)**

Ein ganz backendloser Weg übers Internet ist wegen NAT nicht möglich; statt eines eigenen Servers nutzt Brim das offene Magic-Wormhole-Protokoll mit den öffentlichen Community-Servern (Mailbox und Transit-Relay). Der Absender bekommt einen Code wie `7-kaktus-orbit`, der Empfänger tippt ihn ein. Ende-zu-Ende-verschlüsselt (SPAKE2), Verbindung über WebSocket/HTTPS, dadurch mit System-Proxy und eingestelltem HTTP-/SOCKS-Proxy nutzbar. Das Protokoll wird in C# umgesetzt (keine ausgereifte .NET-Bibliothek vorhanden).

### 4.5 Uhrzeit (A5)

- Ruhezustand zeigt `HH:mm`, optional Sekunden, Datum oder zweite Zeitzone.
- Format folgt den regionalen Windows-Einstellungen.
- Erweitert: Datum, Kalenderwoche, nächster Termin, Wecker/Timer.

### 4.6 Akku (A6)

- Nur aktiv, wenn ein Akku existiert (`Windows.Devices.Power.Battery.AggregateBattery` liefert Werte) → auf Desktop-PCs ausgeblendet.
- Daten: `PowerManager` (Stand, Restzeit, Netzteil, Energiesparmodus) mit Ereignissen statt Abfragen.
- Kompakt: Akkusymbol rechts, wenn unter 20 % oder beim Laden.
- HUD: Ladekabel ein/aus mit kurzer Ladeanimation, Warnung bei 20 % und 10 %.
- Erweitert (Tab System): Prozent, Restzeit, Energiemodus, Link zu den Energieeinstellungen.

### 4.7 Ressourcen (A7)

Die Notch liest **dieselben Quellen wie der Task-Manager**, damit die Werte übereinstimmen. Der Task-Manager selbst bietet keine Schnittstelle zum Anbinden.

| Wert | Quelle |
|---|---|
| CPU gesamt und pro Kern | PDH `\Processor Information(_Total)\% Processor Utility` (entspricht Task-Manager seit Windows 8) |
| RAM | `GlobalMemoryStatusEx` |
| GPU | PDH `\GPU Engine(*)\Utilization Percentage`, gruppiert nach Engine-Typ wie im Task-Manager |
| VRAM | PDH `\GPU Adapter Memory(*)\Dedicated Usage` |
| Datenträger | PDH `\PhysicalDisk(*)\% Idle Time`, Lese-/Schreibrate |
| Netzwerk | PDH `\Network Interface(*)\Bytes Received/sec` und `Sent/sec` |
| Temperaturen | nicht offiziell verfügbar → nur über Plugin (z. B. LibreHardwareMonitor) |
| Top-Prozesse | `NtQuerySystemInformation` bzw. PDH `\Process(*)` |

- Kompakt (optional als Aktivität): kleiner CPU/RAM-Pegel.
- Erweitert, Tab System: Mini-Verläufe der letzten 60 s, Top-3-Prozesse, Klick öffnet `taskmgr.exe` direkt auf der Leistungsseite.
- Abfrage nur alle 1 s und nur, wenn der Tab sichtbar oder die Aktivität aktiv ist.

### 4.8 FPS und Latenz in Spielen (A8)

**Messung** wie PresentMon: ETW-Ereignisse von `Microsoft-Windows-DxgKrnl` und `Microsoft-Windows-DXGI` für das Spiel im Vordergrund.

| Wert | Bedeutung |
|---|---|
| FPS | Bilder pro Sekunde, Mittel über 1 s |
| 1 % Low | Ruckler sichtbar machen |
| Frametime | ms pro Bild, als Mini-Verlauf |
| Renderlatenz | Zeit von Present bis Anzeige (`MsUntilDisplayed`) |
| Netzwerklatenz (optional) | Ping zu einem eingestellten Server oder per Plugin aus dem Spiel |

**Rechte:** ETW-Echtzeitsitzungen brauchen Admin oder Mitgliedschaft in „Leistungsprotokollbenutzer“. Deshalb läuft die Messung in `Kerbe.Telemetry.exe` als optionaler Dienst. Der Nutzer aktiviert ihn einmal in den Einstellungen. Alternative: Intel PresentMon-Dienst nutzen, falls installiert.

**Konflikt mit dem Ausblenden:** Spiele laufen im Vollbild, dort zieht sich die Notch zurück. Lösung: **Spielmodus** als eigene Ausblende-Regel:

| Option | Verhalten |
|---|---|
| Aus | Notch verschwindet komplett (Standard) |
| Mini | Schmale Pille (ca. 120 × 22) bleibt mit FPS · ms, halbtransparent |
| Bei Bedarf | Unsichtbar; Tastenkürzel blendet FPS für 5 s ein |

Funktioniert in randlosem Vollbild. **Exklusives Vollbild** (ältere DirectX-9/11-Spiele) erlaubt keine Fenster darüber, dort ist die Anzeige technisch nicht möglich. Das wird in den Einstellungen klar gesagt.

### 4.9 Automatisches Ausblenden (A9)

**Regeln** (Einstellungen, auch pro App):

| Auslöser | Standard |
|---|---|
| Vollbild (Spiel, Video, F11, Präsentation) | ausblenden |
| Maximiertes Fenster | ausblenden |
| Beliebiges Fenster im Vordergrund („nur Desktop“) | aus |
| Bestimmte Apps (Liste, z. B. OBS, Premiere) | frei wählbar: immer zeigen / immer ausblenden |
| Bildschirmfreigabe in Teams/Zoom/Discord | ausblenden (Privatsphäre) |
| Leerlauf nach X Sekunden | aus |

**Zurückholen:** Maus an die Bildschirmoberkante (Verweilzeit einstellbar, Standard 300 ms) oder Tastenkürzel. Dann morpht die Notch heraus, bis die Maus den Bereich verlässt.

**Animation**

| Einstellung | Standard | Bereich |
|---|---|---|
| Stil | Feder | Feder · Ease-Out · Überblenden · Keine |
| Dauer Ausdehnen | 380 ms | 150–800 ms |
| Dauer Zurückziehen | 320 ms | 120–600 ms |
| Federhärte / Dämpfung | 0,8 | 0,5–1,0 |
| Verzögerung vor Ausblenden | 0 ms | 0–2000 ms |

**Flüssigkeit, technisch:**
- Morph-Engine von der UI-Thread-Schleife auf **Composition-Animationen** umstellen (`Microsoft.UI.Composition`): Größe, Eckenradius und Ohren laufen dann im Compositor, unabhängig vom UI-Thread, mit Bildwiederholrate des Monitors (auch 144/240 Hz).
- Form als `CompositionPathGeometry` mit animierbaren Kontrollpunkten, damit Körper und Ohren eine durchgehende Kontur sind.
- Federanimationen über `NaturalMotionAnimation` (`SpringVector2NaturalMotionAnimation`).
- Unterbrechbar: Neue Zustände starten aus der aktuellen Form, nie mit Sprung.
- Ziel: keine verlorenen Frames im Ablauf, gemessen mit dem eingebauten Debug-Overlay.

## 5. Einstellungen

Eigenes Fenster im Windows-11-Stil (`NavigationView`, Mica), Bereiche:

1. Allgemein: Monitor, Autostart, Tastenkürzel, Sprache
2. Aussehen: Größe, Uhrformat, Akzent aus Cover ja/nein
3. Ausblenden: Regeln aus 4.9, Ausnahmen pro App
4. Animation: Stil, Dauer, Feder, Vorschau-Knopf
5. Module: Medien, Ablage, Akku, Ressourcen, Spiele … einzeln an/aus
6. Spiele: Spielmodus, Telemetrie-Dienst aktivieren
7. Plugins: installieren, Rechte, Logs
8. Info: Version, Updates

Speicherung in `%LocalAppData%\Kerbe\settings.json`, Export/Import möglich.

## 6. Projektstruktur

```
Kerbe.sln
├─ src/Kerbe              WinUI-3-App: Shell, Morph, Einstellungen
├─ src/Kerbe.Core         Zustandsmaschine, Aktivitäten, Modul-Schnittstelle
├─ src/Kerbe.Modules      Medien, Uhr, Akku, Ressourcen, Ablage, HUDs, …
├─ src/Kerbe.Plugins      Plugin-Host, JSON-RPC, Rechte, Rendering der deklarativen UI
├─ src/Kerbe.Telemetry    optionaler Hilfsprozess: ETW/FPS
├─ src/Kerbe.Sdk          NuGet für Plugin-Entwickler
├─ samples/               Beispiel-Plugins (.NET, Python)
├─ tests/                 Unit-Tests Core, Protokoll-Tests
└─ packaging/             MSIX, winget-Manifest
```

## 7. Phasen

| Phase | Inhalt | Ergebnis |
|---|---|---|
| 0 · Fundament | Prototyp lauffähig machen, Composition-Morph, Zustandsmaschine mit Prioritäten, Multi-Monitor | stabile Notch mit Uhr, Medien, Lautstärke |
| 1 · Ablage | Shelf komplett inkl. Persistenz und Herausziehen | A4 fertig |
| 1b · Teilen | Brim-zu-Brim im LAN, danach per Code übers Internet | A10 fertig |
| 2 · System | Akku, Ressourcen, Helligkeit, Kamera/Mikro | A5–A7 fertig (siehe 7a) |
| 3 · Ausblenden | Regelwerk, App-Ausnahmen, Animationseinstellungen, Rand-Reveal | A9 fertig (siehe 7b) |
| 4 · Plugins | Protokoll, Host, SDK, zwei Beispiel-Plugins | A2 fertig (siehe 7c) |
| 5 · Spiele | Telemetrie-Dienst, FPS/Latenz, Spielmodus | A8 fertig (siehe 7d) |
| 6 · Mac-Paket | Timer, Kalender, Benachrichtigungen, Bluetooth, Teilen | A3 vollständig (siehe 7e) |
| 7 · Auslieferung | MSIX, Signatur, winget, Auto-Update, Einstellungsfenster | Version 1.0 (siehe 7f, 7g) |

### 7a. Stand Phase 2 (20.10.2026)

Umgesetzt:
- **Tab-Inhalte** als neue Modul-Schnittstelle (`ModuleContext.SetView`, Vorbild für `view.set` in 4.2): Module legen Karten in einen Tab, Spalte „Main“ oder „Side“. Die Shell meldet, welcher Tab irgendwo offen ist (`OnTabVisibilityChanged`), damit teure Abfragen nur dann laufen.
- **Neue Bausteine:** `ChartWidget` (Verlauf) sowie Hintergrund und Eckenradius für `StackWidget` (Karten).
- **Ressourcen** (`ResourcesModule`): PDH wie in 4.7 für CPU, GPU (Summe pro Engine, davon das Maximum), Netzwerk und Top-3-Prozesse; RAM über `GlobalMemoryStatusEx`. Takt 1 s, nur bei offenem Tab. Knopf öffnet den Task-Manager.
- **Akku** (`BatteryModule`): `PowerManager`-Ereignisse, Ladezeit bis voll aus dem Akkubericht, HUD beim Ein-/Ausstecken, Warnung bei 20 % (HUD) und 10 % (kritisch), kompakt beim Laden und unter 20 %, Karte im Tab System. Schwellen und „kompakt beim Laden“ in `settings.json` (`Battery`).
- **Helligkeit** (`BrightnessModule`): HUD über WMI `WmiMonitorBrightnessEvent`.
- **Kamera/Mikrofon** (`PrivacyModule`): `ConsentStore` mit `RegNotifyChangeKeyValue` statt Abfragen; grüner/oranger Punkt als Perle, kompakt mit App-Namen, aufgeklappt mit Link zu den Datenschutzeinstellungen.

Noch offen:
- Datenträger (`\PhysicalDisk(*)`) und VRAM im Tab System; dafür fehlt im 620 × 248-Layout noch der Platz.
- Helligkeit externer Monitore per DDC/CI (`dxva2`): Monitore melden Änderungen nicht, braucht Abfragen oder einen Regler im Tab System.
- Optionaler kompakter CPU/RAM-Pegel als Aktivität (4.7).
- „Task-Manager direkt auf der Leistungsseite“: dafür gibt es keinen dokumentierten Schalter, Brim öffnet ihn normal.
- Automatische Helligkeit (Umgebungslicht) löst das HUD ebenfalls aus; ggf. abschaltbar machen.

### 7b. Stand Phase 3 (20.10.2026)

Umgesetzt:
- **Regelwerk** als reine Funktion `AutoHideRules.Evaluate` in Brim.Core, voll getestet. Reihenfolge: Fenster Richtung Snap-Leiste ziehen → Bildschirmfreigabe → Regel der App im Vordergrund → Vollbild → maximiert → Fenster im Bereich → „nur Desktop“ → Leerlauf.
- **Einstellungen** (`AutoHide` in `settings.json`): `WhenFullscreen`, `WhenMaximized`, `WhenOverlapped`, `WhenAnyForeground`, `WhenScreenSharing`, `IdleSeconds`, `HideDelayMs`, `Apps` (Prozessname + `AlwaysShow`/`AlwaysHide`), `ScreenShareMarkers`. Fenster von „immer zeigen“-Apps verdecken die Notch nie.
- **Fensterlage** pro Monitor über die bestehenden WinEvent-Hooks: Fenster im Bereich und Vordergrund mit Prozess, Vollbild (Rahmen deckt den Monitor) und maximiert (`IsZoomed`).
- **Bildschirmfreigabe**: Windows hat dafür keine Schnittstelle. Brim erkennt die Freigabeleisten von Zoom, Teams, Chrome/Edge und Webex an Fensterklasse oder Titel. Die Merkmale stehen in den Einstellungen und lassen sich ergänzen.
- **Leerlauf** über `GetLastInputInfo`, nur bei eingeschalteter Regel; **Verzögerung** vor dem Zurückziehen (Zurückkommen sofort, beim Ziehen nie verzögert).
- **Zurückholen per Tastenkürzel:** Win+Alt+N holt die versteckte Notch und klappt sie auf; nach dem Zuklappen zieht sie sich wieder zurück.

Noch offen:
- Oberfläche für diese Regeln kommt mit dem Einstellungsfenster (Phase 7); bis dahin `settings.json`.
- Merkmale für die Freigabe sind aus der Erinnerung zusammengestellt und unter Windows zu prüfen, vor allem für das neue Teams und Discord (Discord zeigt keine eigene Leiste).
- Animationsstil und -dauer sind schon einstellbar (`Animation`), ein Vorschau-Knopf fehlt bis zum Einstellungsfenster.

### 7c. Stand Phase 4 (20.10.2026)

Umgesetzt (Protokoll für Entwickler: `docs/plugins.md`):
- **Protokoll** in `Brim.Core/Plugins`: JSON-RPC 2.0, eine Nachricht pro Zeile, höchstens 1 MB. Widgets als JSON mit `"type"`. Methoden wie in 4.2, dazu `view.clear`, `log`, `tab.visibility` und `shutdown`.
- **Pipe** mit zufälligem Namen pro Start (`BRIM_PIPE`), nur für den eigenen Benutzer (`CurrentUserOnly`), genau eine Verbindung. Der Name `\\.\pipe\kerbe-<id>` aus 4.2 wäre erratbar.
- **Host** (`Brim.Plugins`): findet Plugins in `%LocalAppData%\Brim\Plugins` und `PluginDirectories`, prüft Manifeste (Kennung, API, `entry` nur im eigenen Ordner oder als Befehl im Suchpfad, bekannte Rechte). Startet eingeschaltete Plugins ohne Fenster, Ausgaben in `logs\plugins\<id>.log`.
- **Prüfung aller Inhalte** (`WidgetValidator`): Farben, Maße, Textlängen, höchstens 400 Bausteine, 16 Ebenen, 1 MB Bilder. Aktionen werden pro Plugin zugeordnet. Rang 0–9, `idle` ist der Uhr vorbehalten, `critical` nur mit dem Recht „alerts“, höchstens 120 Nachrichten pro Sekunde.
- **Abstürze:** 10 s Zeit für `hello`, Neustart nach 1 s und 5 s, abgeschaltet nach 3 Abstürzen in 10 Minuten. Beim Beenden erst `shutdown`, nach 2 s wird der Prozessbaum beendet. Alles, was das Plugin zeigte, verschwindet.
- **Zustimmung und Verwaltung** im Tab „Plugins“ (rechte Spalte): jedes Plugin mit Zustand, Rechten und Schalter. Einschalten heißt Zustimmung zu genau diesen Rechten; kommen mit einem Update neue hinzu, bleibt es aus, bis neu zugestimmt wird.
- **SDK** `Brim.Sdk` (`BrimPlugin`) und Beispiele `samples/ServerStatus` (.NET) und `samples/python-counter` (Python ohne Bibliotheken).
- Die Tabs „System“ und „Plugins“ teilen sich jetzt dieselbe Kartenansicht in der Shell.

Noch offen:
- **Slot `shelf.action`:** Host und Protokoll sind fertig (`shelf.items`, `SendShelfItems`), der Knopf in der Ablage fehlt noch.
- **Einstellungen der Plugins** gibt es bisher nur in `settings.json` (`Plugins.<id>.Values`), eine Oberfläche kommt mit dem Einstellungsfenster.
- **Abhängigkeit:** Das SDK hängt an Brim.Core; für ein eigenes NuGet-Paket ggf. Protokoll und Widgets in ein schlankes Paket auslagern.
- **Job-Objekt**, damit Plugins sicher mit Brim enden, auch wenn Brim abstürzt.
- `lifecycle.suspend/resume` (Bildschirm aus, Energiesparen) noch nicht verschickt; `tab.visibility` deckt den häufigsten Fall ab.
- Offene Entscheidung 4 (nur deklarativ oder später XAML) bleibt offen; bisher nur deklarativ.

### 7c+. SwyxIt!-Plugin (20.10.2026)

Erstes echtes Plugin für SwyxIt! (Anrufe, Gespräche mit Wellenform und Schnellmenü, Chats aus SwyxIt! und Swyx Messenger). Es liegt seit 1.0 **nicht mehr in diesem Repository**, sondern im eigenen Repository `brim-swyxit`, und baut dort gegen `Brim.Sdk`. Für es neu in Brim und für alle Plugins nutzbar: Baustein `wave`, `notch.expanded`, `notification.posted` mit Recht `notifications` und Manifestfeld `notificationApps`.

### 7d. Stand Phase 5 (20.10.2026)

Umgesetzt:
- **Messdienst** (`Brim.Telemetry`) als zweiter Prozess über `Brim.exe --telemetry` statt eigener EXE: Das Release-Paket bleibt unverändert, und ETW-Rechte oder ein Absturz berühren die Notch nicht. Er verbindet sich über eine Pipe mit zufälligem Namen und spricht dasselbe JSON-RPC wie Plugins (`watch`, `frames`, `status`).
- **ETW wie PresentMon:** Echtzeitsitzung auf Microsoft-Windows-DXGI (Present_Start 42, PresentMultiplaneOverlay_Start 55) und Microsoft-Windows-D3D9 (Present_Start 1), gezählt nur für den Prozess im Vordergrund. FPS (1 s), 1 % Low (10 s), Frametime und ihr Verlauf rechnet `FrameCounter` in Brim.Core (getestet).
- **Rechte:** Statt eines Dienstes mit Adminrechten genügt die Gruppe „Leistungsprotokollbenutzer“ (SID S-1-5-32-559). Fehlt sie, bietet der Tab „Jetzt“ „Einrichten“ an. Brim startet sich dann einmal erhöht mit `--telemetry-setup <Nutzer>` und ruft `net localgroup` auf; wirksam nach dem nächsten Anmelden.
- **Spiel erkannt**, wenn das Vordergrundfenster seinen Monitor füllt und Bilder ausgibt. Die Werte stehen im Tab „Jetzt“ (Hintergrund-Aktivität), auf Wunsch mit Ping (`Games.PingHost`) als Netzwerklatenz.
- **Spielmodus** (`Games.Overlay`): `Off` (Standard, entscheidet offene Frage 3), `Mini` (neuer Zustand `NotchMode.Mini`, Pille 124 × 22, 75 % Deckkraft, ignoriert die Maus) und `OnDemand` (Win+Alt+F zeigt die Pille 5 s). Kritische Hinweise gehen auch im Spiel vor.

Noch offen:
- **Anzeige-Latenz** (`MsUntilDisplayed`) braucht die DxgKrnl-Ereignisse und die Flip-/VSync-Verfolgung von PresentMon; bisher zeigt Brim Frametime und Ping.
- **Vulkan und OpenGL** laufen nicht immer über DXGI. Dafür müsste die DxgKrnl-Auswertung von PresentMon nachgebaut werden.
- Alternative Quelle Intel-PresentMon-Dienst, falls installiert.
- Die Mini-Pille nimmt Klicks in ihrem kleinen Bereich an (Fensterregion), ohne darauf zu reagieren.
- Einschalten bisher nur über `settings.json` (`Games.Enabled`), mit Neustart; das Einstellungsfenster folgt in Phase 7.

### 7e. Stand Phase 6 (20.10.2026)

Umgesetzt:
- **Timer und Stoppuhr** (`TimerModule`): Auswahl über das Stoppuhr-Symbol im Tab „Jetzt“ (1, 5, 10, 25 min oder Stoppuhr). Der Timer läuft kompakt mit Ring und Restzeit (Rang 30, vor Musik), lässt sich anhalten, um +1 min verlängern und beenden. Abgelaufen kommt ein kritischer Hinweis mit „Noch mal“. Die Stoppuhr läuft kompakt; angehalten ist sie nur noch im Tab „Jetzt“.
- **Bluetooth** (`BluetoothModule`): `DeviceWatcher` auf verbundene klassische und LE-Geräte; HUD beim Verbinden mit Akkustand aus der Geräteeigenschaft `{104EA319-…} 2`, ohne eigene GATT-Verbindung. Schon beim Start verbundene Geräte zeigt Brim nicht.
- **Teilen** ist seit 4.4a fertig (LAN, Magic Wormhole, Windows-Teilen).

Noch offen:
- **Kalender** (`AppointmentStore`) und **Benachrichtigungen** (`UserNotificationListener`) brauchen Paketidentität. Sie kommen mit dem MSIX bzw. Sparse Package aus Phase 7. Bis dahin wäre nur ein Outlook-Plugin möglich.
- Ob die Akku-Eigenschaft bei allen Geräten ankommt, ist unter Windows zu prüfen; manche melden sie nur am Unterknoten (HFP).
- Bildschirmaufnahme aktiv (niedrige Priorität, 4.3).
- Der Timer ist bei laufender Musik nur über die kompakte Notch erreichbar, solange er läuft; ein Knopf im Player fehlt noch.

### 7f. Stand Phase 7 (20.10.2026)

Umgesetzt:
- **Einstellungsfenster** (`SettingsWindow`, in Code statt XAML): `NavigationView` mit Mica, Bereiche Allgemein, Ausblenden (mit Regeln pro App), Animation (mit Vorschau), Module (inkl. Akku-Schwellen), Ablage und Teilen, Spiele, Plugins, Info (Updates, Protokolle). Geöffnet wird es aus dem Tray-Menü. Jede Änderung wird nach 400 ms gespeichert; was nur beim Start gelesen wird, ist gekennzeichnet.
- **Auto-Update** ist seit 0.1.0 da (Velopack).

Entschieden (20.10.2026): **keine gekaufte Signatur**, **Sparse Package** für die Identität, Verteilung **GitHub und winget** (Store vorerst nicht).
- **Identitätspaket** (`build/identity`): Sparse Package `Brim.Notch`, `CN=Brim`, Version fest 1.0.0.0, externer Ort ist die Velopack-Installation (`…\BrimNotch\current`). Die Pipeline signiert es pro Release mit einem frisch erzeugten, selbst ausgestellten Zertifikat; der private Schlüssel verlässt den Build nie. Schlägt der Bau fehl, erscheint das Release trotzdem.
- **Einrichtung** (Einstellungen → Kalender und Benachrichtigungen): Brim startet sich einmal erhöht (`--trust-identity`) und legt das Zertifikat unter „Vertrauenswürdige Personen“ des Computers ab. Dann registriert es das Paket für den Nutzer (`AddPackageByUriAsync` mit `ExternalLocationUri`) und startet neu (`--wait-for`). Beim Deinstallieren wird das Paket wieder entfernt.
- `app.manifest` trägt das passende `<msix>`-Element; ohne registriertes Paket hat es keine Wirkung. Für WinUI mit Identität liegt `Brim.pri` zusätzlich als `resources.pri` bei.
- **Kalender** (`CalendarModule`, `AppointmentStore`, nur Konten aus der Windows-Kalender-App, nicht das klassische Outlook): nächster Termin unter der Uhr, 15 min vorher kompakt mit Countdown, zu Beginn ein HUD, „Beitreten“ bei Online-Besprechungen.
- **Benachrichtigungen** (`NotificationModule`, `UserNotificationListener`): Vorschau neuer Benachrichtigungen als HUD (App, Titel, Text), stumm schaltbare Apps.
- **winget** (`build/winget`): Manifeste 1.6 aus dem Velopack-Setup, pro Release als Zip am GitHub-Release. Das erste Einreichen bei microsoft/winget-pkgs geht von Hand; danach automatisch, wenn das Secret `WINGET_TOKEN` gesetzt ist.

Noch offen:
- Alles in 7f ist nicht unter Windows erprobt, vor allem der Bau des Identitätspakets in der Pipeline und WinUI mit Paketidentität.
- „Nicht stören“ von Windows wird bei Benachrichtigungen noch nicht beachtet (die Schnittstelle dafür braucht ein neueres SDK).
- Klick auf eine Benachrichtigung öffnet die App noch nicht.

### 7g. Version 1.0.0 (20.10.2026)

Zusätzlich für 1.0:
- **Timer-Knopf im Player**, damit der Timer auch bei laufender Musik erreichbar ist.
- **Ablage-Aktionen** (Slot `shelf.action`): Plugins mit dem Recht „shelf“ nennen im Manifest `shelfActions`; sie erscheinen im Kontextmenü der Ablage und schicken die Dateien als `shelf.items`.
- **Plugin-Einstellungen** im Einstellungsfenster (Bereich Plugins), aus den `settings` der Manifeste; Änderungen gehen sofort als `settings.changed` an das Plugin.
- **Job-Objekt** (`ChildProcessJob`): Plugins und Messdienst enden mit Brim, auch nach einem Absturz.
- **Datenträger** (aktive Zeit, `\PhysicalDisk(_Total)\% Idle Time`) im Tab System.
- Version 1.0.0 in `Directory.Build.props` und `CHANGELOG.md`.

Nach 1.0 (bewusst verschoben): Personalisierung (siehe 7h), englische Oberfläche, Anzeige-Latenz und Vulkan/OpenGL wie PresentMon, Helligkeit externer Monitore per DDC/CI, Klick auf Benachrichtigungen, „Nicht stören“, `lifecycle.suspend/resume`, VRAM.

Spiele kommen bewusst spät: Sie brauchen den Hilfsprozess mit Rechten und das fertige Ausblende-System.

### 7h. Personalisierung (20.10.2026)

Entscheidungen: alle vier Formen, Glas ja, Position nur Mitte.

Schritte:
1. **Formen und Größe** (fertig): `NotchForm` Notch, Insel, Kante, Tab. `NotchGeometry.Styled` ändert nur Ecken, Ohren und den Morph-Wert `Round` (obere Ecken rund); Breiten und Höhen bleiben, damit alle Inhalte passen. Die Insel schwebt 6 DIP unter der Kante (`CanvasRoot.Padding`), der Abstand zählt zur Eingabefläche. Die Größe (80–130 %) ist ein `ScaleTransform` auf der Zeichenfläche; das Fenster rechnet mit DPI × Größe.
2. **Glas** (fertig, auf Windows zu prüfen): `GlassBackdrop` (Acryl, bleibt auch ohne Fokus an), Fensterregion genau im Umriss (`NotchGeometry.Outline`, `CreateRoundRectRgn`, Ohren mit ausgespartem Kreis). Während einer Formänderung folgt die Region Bild für Bild (`CompositionTarget.Rendering`). Der Körper ist dann eine Tönung (Schwarz, 44 %). Grenze: Regionen sind nicht geglättet.
2b. **Schrift** (fertig): Standard (Segoe UI, Uhr Cascadia Mono), Bahnschrift, Cascadia Mono oder eigene Datei. `FontFile` liest den Familiennamen aus der Tabelle „name“; `NotchFonts` kopiert die Datei nach `%LocalAppData%\Brim\Fonts` und setzt die Schrift-Ressourcen als „Pfad#Familie“ (auf Windows zu prüfen).
3. Farbschema (Farbnamen → Farben des Designs), eigene Farbe, Kontur, Deckkraft in Ruhe.
4. Ruhezustand (Uhr, Datum, leer), Uhrschrift, Perlen-Stil.
5. Fertige Kombinationen, Designs exportieren und importieren.

## 8. Risiken

| Risiko | Auswirkung | Gegenmaßnahme |
|---|---|---|
| Transparentes WinUI-Fenster rendert nicht sauber | Notch hat schwarze Ecken | in Phase 0 zuerst prüfen; Ausweg: Win32-Fenster mit DirectComposition nur für die Form |
| Drag-and-drop auf Fenster ohne Aktivierung | Ablage nimmt nichts an | Spike in Phase 0; Ausweg: Fenster beim Ziehen kurz aktivierbar machen |
| ETW braucht Rechte | FPS ohne Einrichtung nicht verfügbar | optionaler Dienst, klar erklärt |
| Exklusives Vollbild | kein Overlay möglich | in UI ehrlich kommunizieren |
| Windows-Lautstärke-Flyout | doppelte Anzeige | kein offizieller Weg; dokumentieren |
| Standard-Ausgabegerät umschalten | kein offizieller Weg | **Bewusste Ausnahme von A1** (entschieden 19.10.2026): undokumentiertes `IPolicyConfig` wie EarTrumpet/SoundSwitch, gekapselt in `Brim.Modules/Audio/PolicyConfig.cs`; schlägt es fehl, öffnen sich die Windows-Soundeinstellungen |
| Paketidentität für Benachrichtigungen/Kalender | ohne MSIX nicht nutzbar | von Anfang an MSIX bzw. Sparse Package |
| Plugins als Sicherheitslücke | Datenabfluss | eigene Prozesse, Rechte im Manifest, Nutzerbestätigung |

## 9. Offene Entscheidungen

*Stand 1.0:* 1. Hauptmonitor (umschaltbar) · 2. Verweis · 3. Aus · 4. vorerst nur deklarativ · 5. GitHub und winget.

1. **Notch auf allen Monitoren** oder nur Hauptmonitor als Standard?
2. **Ablage-Standard:** Verweis oder Kopie?
3. **Spielmodus-Standard:** Aus, Mini oder Bei Bedarf?
4. **Plugins nur deklarativ** oder später zusätzlich eigene XAML-Inhalte für vertrauenswürdige Plugins?
5. **Verteilung:** nur GitHub/winget oder auch Microsoft Store?
