# Plugin-Katalog

`plugins.json` ist der Katalog der Plugin-Bibliothek. Brim lädt ihn von `main` und zeigt ihn unter Einstellungen → Plugins → Bibliothek.

Eintragen oder aktualisieren:

```powershell
./build/catalog/Add-Plugin.ps1 -Repository wheatley2056/brim-swyxit -Tag v1.3.0
```

Das Skript lädt das Zip aus dem Release, rechnet den SHA-256 nach und übernimmt Kennung, Name, Version und Rechte aus dem `plugin.json` im Zip. Danach Pull Request öffnen; der Test `Shipped_catalog_is_valid` prüft das Format. Format und Regeln: [docs/plugins.md](../docs/plugins.md#publish-in-the-library).
