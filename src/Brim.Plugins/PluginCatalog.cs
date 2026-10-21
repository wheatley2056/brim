using System.Text.Json;
using System.Text.RegularExpressions;
using Brim.Core.Plugins;

namespace Brim.Plugins;

/// <summary>
/// Ein Plugin im Katalog der Bibliothek: wer es macht, was es darf und woher das Zip der aktuellen Version kommt.
/// Der SHA-256 steht im Katalog, damit Brim nur genau das Zip installiert, das beim Eintragen geprüft wurde.
/// </summary>
public sealed partial record CatalogEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Ein Satz für die Karte in der Bibliothek.</summary>
    public string Summary { get; init; } = "";

    public string Author { get; init; } = "";

    /// <summary>GitHub-Repository des Plugins, z. B. <c>https://github.com/wheatley2056/brim-swyxit</c>.</summary>
    public required string Repository { get; init; }

    public List<string> Categories { get; init; } = [];

    /// <summary>Rechte der aktuellen Version, wie im Manifest. Die Bibliothek zeigt sie vor dem Installieren.</summary>
    public List<string> Permissions { get; init; } = [];

    public required string Version { get; init; }

    /// <summary>Zip aus einem Release des <see cref="Repository"/>.</summary>
    public required string Download { get; init; }

    public required string Sha256 { get; init; }

    /// <summary>Größe des Zips in Bytes (Anzeige und Obergrenze beim Laden).</summary>
    public long Size { get; init; }

    /// <summary>Kleinste Brim-Version, mit der das Plugin läuft; leer = jede.</summary>
    public string? MinBrim { get; init; }

    /// <summary>Plugins aus den Repositorys von Brim selbst bekommen das Abzeichen „Von Brim“.</summary>
    public bool Official => Repository.StartsWith(PluginCatalog.OfficialOwner, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9-]+)+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^https://github\.com/[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    public void Validate()
    {
        if (!IdPattern().IsMatch(Id) || Id.Length > 100)
        {
            throw new InvalidDataException($"Ungültige Kennung „{Id}“");
        }
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 40)
        {
            throw new InvalidDataException($"{Id}: Name fehlt oder ist länger als 40 Zeichen");
        }
        if (Summary.Length > 200)
        {
            throw new InvalidDataException($"{Id}: summary ist länger als 200 Zeichen");
        }
        if (!RepositoryPattern().IsMatch(Repository))
        {
            throw new InvalidDataException($"{Id}: repository muss ein GitHub-Repository sein (https://github.com/<besitzer>/<name>)");
        }
        // Nur Zips aus den Releases des genannten Repositorys: Wer das Plugin prüft, sieht dort, woher es kommt.
        if (!Download.StartsWith(Repository + "/releases/download/", StringComparison.OrdinalIgnoreCase)
            || !Download.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{Id}: download muss ein .zip aus den Releases von {Repository} sein");
        }
        if (!HashPattern().IsMatch(Sha256))
        {
            throw new InvalidDataException($"{Id}: sha256 muss 64 Hex-Zeichen in Kleinbuchstaben haben");
        }
        if (PluginVersion.TryParse(Version) is null)
        {
            throw new InvalidDataException($"{Id}: Version „{Version}“ ist keine Versionsnummer");
        }
        if (MinBrim is not null && PluginVersion.TryParse(MinBrim) is null)
        {
            throw new InvalidDataException($"{Id}: minBrim „{MinBrim}“ ist keine Versionsnummer");
        }
        if (Size is < 0 or > PluginInstaller.MaxDownloadBytes)
        {
            throw new InvalidDataException($"{Id}: size muss zwischen 0 und {PluginInstaller.MaxDownloadBytes / (1024 * 1024)} MB liegen");
        }
        foreach (var permission in Permissions)
        {
            if (!PluginManifest.KnownPermissions.ContainsKey(permission))
            {
                throw new InvalidDataException($"{Id}: unbekanntes Recht „{permission}“");
            }
        }
    }
}

/// <summary>Katalog der Bibliothek (<c>catalog/plugins.json</c> im Brim-Repository).</summary>
public sealed record PluginCatalog
{
    public const int SchemaVersion = 1;

    /// <summary>Repositorys unter diesem Besitzer gelten als „Von Brim“.</summary>
    public const string OfficialOwner = "https://github.com/wheatley2056/";

    public int Schema { get; init; } = SchemaVersion;
    public List<CatalogEntry> Plugins { get; init; } = [];

    /// <summary>
    /// Liest und prüft den Katalog. Fehlerhafte Einträge fallen weg (mit Grund in <paramref name="skipped"/>), damit ein
    /// Tippfehler bei einem Plugin nicht die ganze Bibliothek leert.
    /// </summary>
    public static PluginCatalog Parse(string json, out List<string> skipped)
    {
        skipped = [];
        PluginCatalog? catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<PluginCatalog>(json, PluginJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Katalog ist kein gültiges JSON: {ex.Message}");
        }
        if (catalog is null)
        {
            throw new InvalidDataException("Katalog ist leer");
        }
        if (catalog.Schema != SchemaVersion)
        {
            throw new InvalidDataException($"Katalog hat Format {catalog.Schema}, Brim kennt {SchemaVersion}. Brim aktualisieren.");
        }
        var valid = new List<CatalogEntry>();
        foreach (var entry in catalog.Plugins)
        {
            try
            {
                entry.Validate();
                if (valid.Any(e => e.Id == entry.Id))
                {
                    throw new InvalidDataException($"{entry.Id}: steht doppelt im Katalog");
                }
                valid.Add(entry);
            }
            catch (InvalidDataException ex)
            {
                skipped.Add(ex.Message);
            }
        }
        return catalog with { Plugins = valid };
    }
}

/// <summary>Versionsnummern wie „1.3.0“ oder „v1.3.0-beta“ vergleichen (Zusatz nach „-“ zählt nicht).</summary>
public static class PluginVersion
{
    public static Version? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!System.Version.TryParse(core.Contains('.') ? core : core + ".0", out var version))
        {
            return null;
        }
        // 1.3 und 1.3.0 sind gleich.
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    /// <summary>Ist <paramref name="candidate"/> neuer als <paramref name="installed"/>? Unlesbare Nummern sind nie neuer.</summary>
    public static bool IsNewer(string candidate, string installed) =>
        TryParse(candidate) is { } c && (TryParse(installed) is not { } i || c > i);
}
