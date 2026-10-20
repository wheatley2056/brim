# Changelog

Jede Version bekommt einen Abschnitt `## <Version>`. Die Release-Pipeline nimmt genau diesen
Abschnitt als Changelog ins Update-Paket; Brim zeigt ihn nach dem Update in der Notch.

## 2.2.0

„Jetzt“ neu gestaltet: neue Anordnung, einheitliche Widgets und ein Editor, der sich wie Windows anfühlt.

- Neue Anordnung: Links liegt die laufende Aktivität als halbe Seite, rechts Termine, eine Uhr und die Systemlast. Wer noch den alten Standard hatte (nur die Aktivität über die ganze Seite), bekommt einmalig den neuen; eigene Anordnungen bleiben.
- Läuft nichts, zeigt die Aktivität „Heute“: Wochentag, Datum, Kalenderwoche und den nächsten Termin, darunter runde Knöpfe für Timer, Audioausgabe und Lautstärke. Musik erscheint dort als kompakter Player.
- Einheitliche Widgets: Jede Karte hat oben eine Zeile mit farbigem Symbol und Namen. Neu sind „Schnellzugriff“, „System“ (CPU, RAM, GPU) und „Akku“ (Ring mit Restzeit).
- Bearbeiten: Oben stehen „Jetzt anpassen“, „+ Widget“ und „Fertig“. Kacheln werden gedämpft und umrandet, freie Felder sind gestrichelt und lassen sich anklicken. Die Größe wählst du unter der Kachel über kleine Rasterbilder.
- Galerie: Sie öffnet sich in der hohen Notch, mit Gruppen links, Suche oben und Vorschau-Kacheln. Nach der Wahl siehst du das Widget in echter Größe, wählst die Größe und tippst auf „Hinzufügen“.
- Plugins: neuer Knopfstil `round` für runde Symbolknöpfe.

## 2.1.0

Feinschliff nach dem Review: ruhigere, aufgeräumte Notch und Einstellungen, die zu Windows passen.

- Ruhende Notch: Sie „atmet“ nicht mehr bei jeder Messung. Jeder Wert behält seine Breite und schrumpft erst, wenn er eine Weile kleiner war; Ziffern sind gleich breit. Die Werte haben feste Seiten wie im Infobereich (CPU/RAM und Netzwerk links, Datum und Akku rechts). Wird es zu breit, fällt erst das Netzwerk weg, dann CPU und RAM, statt über den Rand zu laufen. Neu: „Ganzen Inhalt zentrieren“.
- Alle Werte neben der Uhr in einer Schrift und Größe (die der Uhr) mit gemeinsamer Grundlinie. Der Akku ist nur noch farbig, wenn er lädt, fast leer ist oder Energie spart.
- Datum: Format wählbar (Di 20., Di 20. Okt., 20.10.); die Beispiele in den Einstellungen zeigen das echte Format.
- Netzwerk: wahlweise in Bit/s wie der Task-Manager (Standard) oder in Byte/s.
- Benachrichtigungen: breitere Vorschau in zwei Zeilen mit App-Symbol, Titel und Text, beides mit Auslassungspunkten. Ein Klick öffnet die App. In Präsentationen, bei Vollbild und bei „Nicht stören“ erscheint nur der blaue Punkt (abschaltbar).
- Einstellungsfenster: Titelleiste im Fenster mit durchgehendem Mica, hell oder dunkel wie Windows; die Notch bleibt dunkel. Größe passt zur Anzeigeskalierung, mit Mindestgröße. Navigation breiter und einheitlich benannt („Kalender und Benachrichtigungen“); Schriften stehen jetzt direkt unter Aussehen.
- Uhr: „Sekunden anzeigen“ liegt unter Aussehen → Uhr und wirkt sofort. Für Einstellungen, die einen Neustart brauchen, gibt es „Jetzt neu starten“.
- Monitore: neue Option „Monitor mit der Maus“ – die Notch folgt dem Mauszeiger.
- Weniger Last im Leerlauf: CPU, RAM und Netzwerk neben der Uhr werden nicht gemessen, solange die Notch versteckt oder aufgeklappt, der PC gesperrt oder der Bildschirm aus ist.
- Schärfere Rundungen: Die Ohren der Notch sind jetzt Vektorformen, sauber geglättet und ohne Saum an der Naht.
- Neues App-Symbol in Pfirsich mit schwarzer Notch, gut sichtbar auf heller und dunkler Taskleiste, mit eigenen 16- und 24-px-Fassungen.

## 2.0.0

Brim 2.0 – „Jetzt“ zum Selbstgestalten, Einstellungen in der Notch und eine Einführung beim ersten Start.

- Einführung: Beim ersten Start öffnet sich eine Naht an der Bildschirmkante, die Notch tropft heraus, blüht zur Begrüßung auf (mit Farbschimmer am unteren Rand) und setzt sich dann als ruhende Notch; ein Hinweis zeigt, wo Brim wohnt. Ein Klick überspringt die Animation, bei reduzierten Animationen wird nur überblendet. Beim ersten Aufklappen folgen Kurzanleitung und Einrichtung von Kalender und Benachrichtigungen. Erneut ansehen: Einstellungen → Info.
- „Jetzt“ selbst gestalten: Die Seite besteht jetzt aus Kacheln. „Jetzt“ lange drücken oder rechts anklicken – die Kacheln bekommen einen Rahmen, du ziehst sie an einen anderen Platz, änderst die Größe, entfernst sie mit „−“ oder fügst mit „+“ neue aus der Galerie hinzu. Bei mehreren Seiten blätterst du mit dem Mausrad.
- Neue Widgets: acht Uhren (digital, mit Sekunden, mit Datum; analog klassisch, minimal, mit Ziffern, mit Datum) und ein Kalenderblatt, dazu Musik, Termine und die laufende Aktivität. Ohne Änderung sieht „Jetzt“ aus wie bisher.
- Plugins können eigene Widgets anbieten, zum Beispiel SwyxIt! (Telefon, Chats) und Outlook (nächster Termin, Posteingang, Aufgaben).
- Einstellungen in der Notch: Das Zahnrad neben den Tabs vergrößert die Notch auf die Größe des Einstellungsfensters (auf kleinen Bildschirmen so weit es geht).
- In der Notch: Die Uhr steht mittig, links oder rechts, und daneben auf Wunsch Datum, CPU und RAM, Netzwerk und Akku. Benachrichtigungen können statt der Vorschau nur einen blauen Punkt zeigen – die Uhr bleibt stehen. Einstellen unter Aussehen → In der Notch.
- System: Ein Klick auf CPU, RAM, GPU, Netzwerk oder Disk öffnet eine Detailansicht mit Verlauf, mehr Werten und den größten Prozessen; oben rechts geht es in den Task-Manager.
- Akku: lädt blau mit Blitz, fast leer rot, Energiesparmodus orange, sonst grün.

## 1.1.0

- Aussehen (neu in den Einstellungen): Die Notch gibt es jetzt in vier Formen – klassische Notch, schwebende Insel, runde Kante ohne Ohren und eckiger Tab. Die Größe lässt sich von 80 bis 130 % einstellen. Wechsel siehst du sofort.
- Material: Statt Schwarz auf Wunsch die Windows-Materialien Acryl (Desktop weichgezeichnet), Mica oder Mica Alt.
- Kalender und Benachrichtigungen: Eine Diagnose in den Einstellungen zeigt, woran es hängt, und lässt sich kopieren. Ist die Einrichtung für einen anderen Brim-Ordner registriert, richtet „Neu einrichten“ sie für diesen ein.
- Schriften (Aussehen → Schriften): Für Uhr, Menü, Überschriften, Texte und Zahlen je eine eigene Schrift – Segoe UI, Bahnschrift, Cascadia Mono oder eigene Schriftdateien (.ttf, .otf), ohne sie installieren zu müssen.

## 1.0.1

- Benachrichtigungen: Neue Windows-Benachrichtigungen erscheinen jetzt zuverlässig in der Notch. Brim fragt das Info-Center zusätzlich alle 2 Sekunden ab, weil Windows das Ereignis an Desktop-Apps oft nicht zustellt. Auch Apps mit älteren Benachrichtigungsvorlagen werden angezeigt.
- Plugins können den Lichtschein am unteren Rand mit einem Pegel pulsieren lassen (`glow`), etwa mit der Stimme im Telefonat.

## 1.0.0

Brim 1.0 – alles aus dem Plan ist da.

- Plugins: Eigene Inhalte für die Notch – kompakt, als HUD oder als Karten im Tab „Plugins“. Plugins laufen als eigene Programme in beliebiger Sprache und enden mit Brim. Sie können Live-Wellenformen zeigen und Benachrichtigungen bestimmter Apps übernehmen. Abstürzende Plugins starten neu und schalten sich nach drei Abstürzen ab. Neue Plugins sind zunächst aus; im Tab „Plugins“ siehst du, was sie dürfen, und schaltest sie ein. Ihre Einstellungen findest du im Einstellungsfenster, ihre Aktionen per Rechtsklick in der Ablage.
- FPS in Spielen: Bildrate, 1 % Low, Frametime und auf Wunsch der Ping. Im Spiel bleibt nach Wahl eine kleine Pille mit „FPS · ms“ stehen, oder Win+Alt+F zeigt sie für 5 Sekunden. Einschalten unter Einstellungen → Spiele.
- Timer und Stoppuhr: über das Stoppuhr-Symbol im Tab „Jetzt“, auch im Player. Läuft kompakt mit Ring und Restzeit; ist die Zeit um, klappt die Notch kurz auf.
- Kalender und Benachrichtigungen (einmal einrichten unter Einstellungen → Kalender und Benachrichtigungen): Der nächste Termin steht unter der Uhr und erscheint 15 Minuten vorher mit Countdown und „Beitreten“. Neue Windows-Benachrichtigungen zeigt die Notch kurz als Vorschau.
- Bluetooth: Der Tab „System“ zeigt alle verbundenen Geräte mit Akkustand; verbindet sich eines neu, zeigt die Notch es kurz an.
- Tab „System“ zeigt jetzt auch die Auslastung der Datenträger.
- Einstellungen: eigenes Fenster im Windows-11-Stil (Rechtsklick auf das Brim-Symbol im Infobereich → Einstellungen).
- Behoben: Beim Wechsel des Audio-Ausgabegeräts hing Brim sich auf.
- Behoben: Bluetooth-Geräte wie die MX Master zeigten ihren Akkustand nicht.

## 0.2.1

- Neuer Tab „System“: CPU, RAM, GPU und Netzwerk mit Verlauf der letzten Minute und die drei Prozesse mit der meisten Last – mit denselben Werten wie im Task-Manager. Gemessen wird nur, solange der Tab offen ist.
- Akku auf Laptops: Stand und Restzeit, kurzes HUD beim Ein- und Ausstecken, eine Warnung bei 20 % und ein Hinweis bei 10 %.
- Helligkeit am Laptop-Bildschirm bekommt ein eigenes HUD.
- Benutzt eine App Kamera oder Mikrofon, zeigt Brim das mit einem grünen bzw. orangen Punkt.
- Ausblenden nach Regeln: Vollbild, maximierte Fenster und Fenster im Bereich lassen sich einzeln abschalten, dazu „nur Desktop“, Leerlauf und eine Verzögerung. Pro App lässt sich festlegen, dass die Notch immer bleibt oder immer verschwindet.
- Teilst du deinen Bildschirm in Teams, Zoom, Meet oder Webex, zieht sich die Notch zurück.
- Win+Alt+N holt die versteckte Notch zurück und klappt sie auf.

## 0.1.1

- Laufen zwei Dinge gleichzeitig, zum Beispiel Teilen und Musik, bleibt die Perle neben der Notch beim Auf- und Zuklappen jetzt ein Stück. Das Cover wandert nicht mehr getrennt von ihr.

## 0.1.0

- Erste Fassung: Notch mit Uhr, Musik, Lautstärke und Feder-Animationen
- Ablage: Dateien und Ordner auf die Notch ziehen, behalten und wieder herausziehen
- Zieht sich zurück, wenn ein Fenster darunter liegt; Maus an die Oberkante holt sie zurück
- Teilen im LAN mit anderen Brims und per Code übers Internet (kompatibel mit Magic Wormhole)
- Aktualisiert sich ab jetzt selbst – unbemerkt, wenn du gerade nichts tust
