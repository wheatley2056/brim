# Changelog

Jede Version bekommt einen Abschnitt `## <Version>`. Die Release-Pipeline nimmt genau diesen
Abschnitt als Changelog ins Update-Paket; Brim zeigt ihn nach dem Update in der Notch.

## 1.1.0

- Aussehen (neu in den Einstellungen): Die Notch gibt es jetzt in vier Formen – klassische Notch, schwebende Insel, flache Kante und eckiger Tab. Die Größe lässt sich von 80 bis 130 % einstellen. Wechsel siehst du sofort.
- Material: Statt Schwarz auf Wunsch die Windows-Materialien Acryl (Desktop weichgezeichnet), Mica oder Mica Alt.
- Schrift: Segoe UI, Bahnschrift oder Cascadia Mono – oder eine eigene Schriftdatei (.ttf, .otf), ohne sie installieren zu müssen.

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
