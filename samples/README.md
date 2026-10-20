# Beispiel-Plugins

| Ordner | Sprache | Zeigt |
|---|---|---|
| `ServerStatus` | .NET mit `Brim.Sdk` | Erreichbarkeit eines Servers mit Latenz-Verlauf; meldet sich in der Notch, wenn er ausfällt |
| `python-counter` | Python ohne Bibliotheken | Zähler mit Knöpfen und HUD; zeigt das Protokoll „von Hand“ |

## Ausprobieren

Plugins liegen in `%LocalAppData%\Brim\Plugins\<Ordner>\` (oder in einem Ordner aus `PluginDirectories` in `settings.json`).

```powershell
# .NET: Build-Ausgabe ist der fertige Plugin-Ordner
dotnet publish samples/ServerStatus -c Release -o "$env:LOCALAPPDATA\Brim\Plugins\serverstatus"

# Python (braucht den Python-Launcher „py“)
Copy-Item -Recurse samples/python-counter "$env:LOCALAPPDATA\Brim\Plugins\python-counter"
```

Brim neu starten, Notch aufklappen, Tab **Plugins**: rechts das Plugin einschalten. Damit stimmst du den Rechten zu, die neben dem Schalter stehen. Ausgaben der Plugins landen in `%LocalAppData%\Brim\logs\plugins\<id>.log`.

Das Protokoll ist in [`docs/plugins.md`](../docs/plugins.md) beschrieben.
