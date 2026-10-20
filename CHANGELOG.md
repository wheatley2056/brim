# Changelog

Jede Version bekommt einen Abschnitt `## <Version>`. Die Release-Pipeline nimmt genau diesen
Abschnitt als Changelog ins Update-Paket; Brim zeigt ihn nach dem Update in der Notch.

## 0.3.0

- Plugins: Eigene Inhalte für die Notch – kompakt, als HUD oder als Karten im Tab „Plugins“. Plugins laufen als eigene Programme in beliebiger Sprache; abstürzende Plugins starten neu und schalten sich nach drei Abstürzen ab.
- Neue Plugins sind zunächst aus. Im Tab „Plugins“ siehst du, was sie dürfen, und schaltest sie ein.
- Zwei Beispiel-Plugins: Serverstatus (.NET) und ein Zähler (Python).
- Plugin für SwyxIt!: Eingehende Anrufe mit Annehmen und Ablehnen, laufende Gespräche mit echter Wellenform, Schnellmenü (Stumm, Tastatur, Weiterleiten, Halten) und Auflegen, Nachrichten aus dem SwyxIt!-Chat und Swyx Messenger als Hinweis (`plugins/SwyxIt`).
- Plugins können Live-Wellenformen zeigen, erfahren, wann die Notch aufgeklappt ist, und Benachrichtigungen bestimmter Apps übernehmen.
- FPS in Spielen: Bildrate, 1 % Low, Frametime-Verlauf und auf Wunsch der Ping zu einem Server. Im Spiel bleibt nach Wahl eine kleine Pille mit „FPS · ms“ stehen, oder Win+Alt+F blendet sie für 5 Sekunden ein. Einschalten in den Einstellungen unter „Games“; beim ersten Mal richtet Brim nach Rückfrage die nötigen Rechte ein.
- Timer und Stoppuhr: im Tab „Jetzt“ über das Stoppuhr-Symbol neben der Uhr. Läuft kompakt mit Ring und Restzeit; wenn die Zeit um ist, klappt die Notch kurz auf.
- Bluetooth: Der Tab „System“ zeigt alle verbundenen Geräte. Verbindet sich eines neu, zeigt die Notch es kurz an – jeweils mit Akkustand, wenn Windows ihn kennt.
- Einstellungen: eigenes Fenster im Windows-11-Stil (Rechtsklick auf das Brim-Symbol im Infobereich → Einstellungen) für Monitore, Ausblenden mit Regeln pro App, Animation, Module, Ablage, Teilen, Spiele und Updates.
- Kalender und Benachrichtigungen (nach einmaliger Einrichtung unter Einstellungen → Kalender und Benachrichtigungen): Der nächste Termin steht unter der Uhr und erscheint 15 Minuten vorher mit Countdown und „Beitreten“. Neue Windows-Benachrichtigungen zeigt die Notch kurz als Vorschau.
- Brim gibt es auch über winget: `winget install wheatley2056.Brim`.
- Behoben: Bluetooth-Geräte zeigen jetzt ihren Akkustand, auch Mäuse wie die MX Master.
- Behoben: Beim Wechsel des Audio-Ausgabegeräts in der Notch hing Brim sich auf.

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
