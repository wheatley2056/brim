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
| 2 · System | Akku, Ressourcen, Helligkeit, Kamera/Mikro | A5–A7 fertig |
| 3 · Ausblenden | Regelwerk, App-Ausnahmen, Animationseinstellungen, Rand-Reveal | A9 fertig |
| 4 · Plugins | Protokoll, Host, SDK, zwei Beispiel-Plugins | A2 fertig |
| 5 · Spiele | Telemetrie-Dienst, FPS/Latenz, Spielmodus | A8 fertig |
| 6 · Mac-Paket | Timer, Kalender, Benachrichtigungen, Bluetooth, Teilen | A3 vollständig |
| 7 · Auslieferung | MSIX, Signatur, winget, Auto-Update, Einstellungsfenster | Version 1.0 |

Spiele kommen bewusst spät: Sie brauchen den Hilfsprozess mit Rechten und das fertige Ausblende-System.

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

1. **Notch auf allen Monitoren** oder nur Hauptmonitor als Standard?
2. **Ablage-Standard:** Verweis oder Kopie?
3. **Spielmodus-Standard:** Aus, Mini oder Bei Bedarf?
4. **Plugins nur deklarativ** oder später zusätzlich eigene XAML-Inhalte für vertrauenswürdige Plugins?
5. **Verteilung:** nur GitHub/winget oder auch Microsoft Store?
