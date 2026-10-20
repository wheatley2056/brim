using System.Text.Json;
using System.Text.RegularExpressions;
using Brim.Core.Plugins;

namespace Brim.Plugins;

/// <summary>Eine Einstellung, die das Plugin anbietet (Plan 4.2, <c>settings</c> im Manifest).</summary>
public sealed record PluginSettingDefinition
{
    public required string Key { get; init; }
    public string Type { get; init; } = "string";
    public string? Label { get; init; }
    public JsonElement? Default { get; init; }
}

/// <summary><c>plugin.json</c> im Ordner des Plugins.</summary>
public sealed partial record PluginManifest
{
    public const string FileName = "plugin.json";

    /// <summary>Rechte, die Brim kennt. Netzwerk und Medien sind Erklärungen für den Nutzer; „alerts“ und „shelf“ prüft Brim selbst.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownPermissions = new Dictionary<string, string>
    {
        ["network"] = "Netzwerk",
        ["shelf"] = "Dateien der Ablage",
        ["media"] = "Mediensteuerung",
        ["alerts"] = "Kritische Hinweise (klappt die Notch auf)",
        ["notifications"] = "Benachrichtigungen bestimmter Apps lesen",
    };

    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "0.0.0";

    /// <summary>Programm im Ordner des Plugins (<c>ServerStatus.exe</c>) oder ein Befehl im Suchpfad (<c>py</c>, <c>node</c>).</summary>
    public required string Entry { get; init; }

    public List<string> Args { get; init; } = [];
    public string Api { get; init; } = PluginProtocol.ApiVersion;
    public List<string> Slots { get; init; } = [];
    public List<string> Permissions { get; init; } = [];
    public List<PluginSettingDefinition> Settings { get; init; } = [];

    /// <summary>
    /// Apps, deren Windows-Benachrichtigungen dieses Plugin statt Brim zeigt (Recht „notifications“), z. B.
    /// <c>["Swyx Messenger"]</c>. Verglichen wird mit dem Anzeigenamen, ohne Groß-/Kleinschreibung, als Teilwort.
    /// </summary>
    public List<string> NotificationApps { get; init; } = [];

    /// <summary>Ordner, aus dem das Manifest stammt (nicht Teil der Datei).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Directory { get; init; } = "";

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9-]+)+$")]
    private static partial Regex IdPattern();

    /// <summary>Liest und prüft das Manifest. Fehler kommen als <see cref="InvalidDataException"/> mit lesbarem Grund.</summary>
    public static PluginManifest Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), PluginJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} ist kein gültiges JSON: {ex.Message}");
        }
        if (manifest is null)
        {
            throw new InvalidDataException($"{FileName} ist leer");
        }
        manifest = manifest with { Directory = Path.GetFullPath(directory) };
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (!IdPattern().IsMatch(Id) || Id.Length > 100)
        {
            throw new InvalidDataException($"Ungültige Kennung „{Id}“ (erwartet z. B. de.beispiel.serverstatus)");
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException("Name fehlt");
        }
        if (Api != PluginProtocol.ApiVersion)
        {
            throw new InvalidDataException($"API-Version {Api} wird nicht unterstützt (Brim spricht {PluginProtocol.ApiVersion})");
        }
        if (string.IsNullOrWhiteSpace(Entry))
        {
            throw new InvalidDataException("entry fehlt");
        }
        if (LooksLikePath(Entry) && ResolveEntry() is null)
        {
            throw new InvalidDataException($"entry „{Entry}“ liegt nicht im Ordner des Plugins");
        }
        if (NotificationApps.Count > 0 && !Permissions.Contains("notifications"))
        {
            throw new InvalidDataException("notificationApps braucht das Recht „notifications“");
        }
        foreach (var permission in Permissions)
        {
            if (!KnownPermissions.ContainsKey(permission))
            {
                throw new InvalidDataException($"Unbekanntes Recht „{permission}“");
            }
        }
    }

    private static bool LooksLikePath(string entry) =>
        entry.Contains('/') || entry.Contains('\\') || entry.Contains('.') || Path.IsPathRooted(entry);

    /// <summary>
    /// Vollständiger Pfad, wenn <see cref="Entry"/> eine Datei im Ordner ist; <c>null</c>, wenn sie fehlt oder
    /// aus dem Ordner herausführt. Ein bloßer Befehlsname wie <c>py</c> bleibt unverändert.
    /// </summary>
    public string? ResolveEntry()
    {
        if (!LooksLikePath(Entry))
        {
            return Entry;
        }
        if (Path.IsPathRooted(Entry))
        {
            return null;
        }
        var root = Path.GetFullPath(Directory);
        var full = Path.GetFullPath(Path.Combine(root, Entry));
        var inside = full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return inside && File.Exists(full) ? full : null;
    }

    /// <summary>Standardwerte aus dem Manifest, überschrieben von den Werten des Nutzers.</summary>
    public Dictionary<string, JsonElement> EffectiveSettings(IReadOnlyDictionary<string, JsonElement>? values)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var definition in Settings)
        {
            if (values is not null && values.TryGetValue(definition.Key, out var value))
            {
                result[definition.Key] = value;
            }
            else if (definition.Default is { } fallback)
            {
                result[definition.Key] = fallback;
            }
        }
        return result;
    }
}
