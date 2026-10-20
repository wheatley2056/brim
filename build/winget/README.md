# Brim über winget

Jedes Release erzeugt die Manifeste (`build/winget/make.ps1`) und hängt sie als `winget-manifests-<Version>.zip` an das GitHub-Release.

## Erstes Mal (neues Paket)

1. Fork von [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) anlegen.
2. Den Ordner `manifests/w/wheatley2056/Brim/<Version>` aus dem Zip in den Fork kopieren und einen Pull Request stellen.
3. Prüfen lassen, z. B. lokal mit `winget validate --manifest <Ordner>` und `winget install --manifest <Ordner>`.

Brims Setup ist unsigniert (Entscheidung 20.10.2026). winget nimmt das an; Windows SmartScreen kann beim ersten Start trotzdem warnen.

## Danach automatisch

Liegt im Repository das Secret `WINGET_TOKEN` (klassischer GitHub-Token mit `public_repo`), reicht die Release-Pipeline neue Versionen selbst mit [wingetcreate](https://github.com/microsoft/winget-create) ein. Ohne das Secret wird der Schritt übersprungen.

Installieren für Nutzer:

```powershell
winget install wheatley2056.Brim
```
