# SwyxIt!-Plugin für Brim

Zeigt Anrufe und Chat-Nachrichten aus [SwyxIt!](https://www.enreach.de) in der Notch:

- **Eingehender Anruf:** Die Notch klappt auf und zeigt Name, Nummer und angerufene Gruppe. Daneben stehen **Ablehnen** und **Annehmen**. Klopft während eines Gesprächs ein zweiter Anruf an, erscheint er genauso; Annehmen hält das laufende Gespräch.
- **Laufendes Gespräch:** In der kompakten Notch steht ein grünes Telefon mit der Dauer. Aufgeklappt gibt es **Halten/Fortsetzen** und **Auflegen**.
- **Chat:** Neue Nachrichten erscheinen kurz als HUD. Die letzten fünf stehen im Tab „Plugins“, mit **Chat öffnen**.

Das Plugin spricht die offizielle COM-Schnittstelle von SwyxIt! (Client Line Manager, NuGet `Swyx.Client.ClmgrAPI`). SwyxIt! muss dazu laufen; das Plugin startet es nicht selbst und verbindet sich, sobald es da ist.

## Installieren

```powershell
dotnet publish plugins/SwyxIt -c Release -o "$env:LOCALAPPDATA\Brim\Plugins\swyxit"
```

Danach Brim neu starten, die Notch aufklappen und im Tab **Plugins** „SwyxIt!“ einschalten. Das Plugin fragt nach dem Recht **„Kritische Hinweise“**: Nur damit darf es die Notch bei einem Anruf aufklappen. Ohne dieses Recht erscheint der Anruf nur als kurzer Hinweis, ohne Knöpfe.

Chat-Hinweise abschalten: in `settings.json` unter `Plugins` → `de.brim.swyxit` → `Values` den Wert `"chat": false` setzen.

## Wenn etwas nicht klappt

Das Protokoll liegt in `%LocalAppData%\Brim\logs\plugins\de.brim.swyxit.log`. Dort steht zum Beispiel „Mit SwyxIt! verbunden (Benutzer)“ oder warum SwyxIt! nicht erreichbar ist.

- **Bitness:** Das Plugin ist für x64 gebaut. Ein 32-Bit-SwyxIt! braucht die x86-Fassung (`-p:Platform=x86`).
- **Chat:** Die Schnittstelle liefert die Nachrichten des klassischen SwyxIt!-Chats. Ob der neue Swyx-Messenger seine Nachrichten auch darüber meldet, ist noch nicht geprüft.
