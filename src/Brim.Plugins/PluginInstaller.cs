using System.IO.Compression;
using System.Security.Cryptography;
using Brim.Core;

namespace Brim.Plugins;

/// <summary>
/// Lädt das Zip eines Katalog-Eintrags, prüft den SHA-256 und entpackt es in einen Bereitstellungsordner.
/// Erst wenn das Manifest darin zum Eintrag passt (Kennung, Version, Rechte), kommt es in den Plugin-Ordner.
/// </summary>
public sealed class PluginInstaller
{
    /// <summary>Größtes Zip, das Brim lädt.</summary>
    public const long MaxDownloadBytes = 200L * 1024 * 1024;

    /// <summary>Höchstens so viel entpackt (gegen Zip-Bomben).</summary>
    public const long MaxExtractedBytes = 800L * 1024 * 1024;

    public const int MaxEntries = 5000;

    private readonly HttpClient _http;
    private readonly string _stagingRoot;

    /// <param name="stagingRoot">Arbeitsordner; auf demselben Laufwerk wie der Plugin-Ordner, damit das Verschieben sofort geht.</param>
    public PluginInstaller(HttpClient http, string stagingRoot)
    {
        _http = http;
        _stagingRoot = stagingRoot;
    }

    /// <summary>
    /// Lädt, prüft und entpackt. Gibt den Ordner mit dem geprüften Plugin zurück; er gehört danach dem Aufrufer
    /// (verschieben oder mit <see cref="Discard"/> löschen).
    /// </summary>
    public async Task<string> StageAsync(CatalogEntry entry, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stagingRoot);
        var name = entry.Id + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var zip = Path.Combine(_stagingRoot, name + ".zip");
        var folder = Path.Combine(_stagingRoot, name);
        try
        {
            await DownloadAsync(entry, zip, progress, cancellationToken);
            Extract(zip, folder);
            var root = ManifestRoot(folder);
            var manifest = PluginManifest.Load(root);
            Check(entry, manifest);
            return root;
        }
        catch
        {
            Discard(folder);
            throw;
        }
        finally
        {
            TryDelete(zip);
        }
    }

    private async Task DownloadAsync(CatalogEntry entry, string target, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(entry.Download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"Download fehlgeschlagen ({(int)response.StatusCode} {response.ReasonPhrase})");
        }
        var expected = response.Content.Headers.ContentLength ?? (entry.Size > 0 ? entry.Size : null);
        if (expected > MaxDownloadBytes)
        {
            throw new InvalidDataException("Das Zip ist zu groß");
        }
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var file = File.Create(target))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes)
                {
                    throw new InvalidDataException("Das Zip ist zu groß");
                }
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (expected is > 0)
                {
                    progress?.Report(Math.Min(1, (double)total / expected.Value));
                }
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != entry.Sha256)
            {
                // Anderes Zip als im Katalog geprüft: nicht installieren.
                throw new InvalidDataException($"Prüfsumme stimmt nicht (erwartet {entry.Sha256[..12]}…, erhalten {actual[..12]}…)");
            }
        }
        progress?.Report(1);
    }

    /// <summary>Entpackt mit Obergrenzen; Einträge, die aus dem Ordner herausführen, brechen ab.</summary>
    internal static void Extract(string zip, string folder)
    {
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException("Das Zip enthält zu viele Dateien");
        }
        if (archive.Entries.Sum(e => e.Length) > MaxExtractedBytes)
        {
            throw new InvalidDataException("Das Zip ist entpackt zu groß");
        }
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        foreach (var item in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(root, item.FullName.Replace('\\', '/')));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Das Zip enthält einen ungültigen Pfad ({item.FullName})");
            }
            if (item.FullName.EndsWith('/') || item.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            item.ExtractToFile(path, overwrite: false);
        }
    }

    /// <summary><c>plugin.json</c> liegt oben im Zip oder in genau einem Unterordner.</summary>
    internal static string ManifestRoot(string folder)
    {
        if (File.Exists(Path.Combine(folder, PluginManifest.FileName)))
        {
            return folder;
        }
        var children = Directory.GetDirectories(folder);
        if (children.Length == 1 && Directory.GetFiles(folder).Length == 0 && File.Exists(Path.Combine(children[0], PluginManifest.FileName)))
        {
            return children[0];
        }
        throw new InvalidDataException($"Im Zip fehlt {PluginManifest.FileName}");
    }

    /// <summary>Das Zip muss genau das Plugin enthalten, das der Katalog verspricht.</summary>
    internal static void Check(CatalogEntry entry, PluginManifest manifest)
    {
        if (manifest.Id != entry.Id)
        {
            throw new InvalidDataException($"Das Zip enthält {manifest.Id} statt {entry.Id}");
        }
        if (PluginVersion.TryParse(manifest.Version) != PluginVersion.TryParse(entry.Version))
        {
            throw new InvalidDataException($"Das Zip enthält Version {manifest.Version} statt {entry.Version}");
        }
        var extra = manifest.Permissions.Except(entry.Permissions).ToList();
        if (extra.Count > 0)
        {
            // Sonst stimmte der Nutzer in der Bibliothek anderen Rechten zu, als das Plugin bekommt.
            throw new InvalidDataException($"Das Plugin verlangt Rechte, die der Katalog nicht nennt: {string.Join(", ", extra)}");
        }
    }

    /// <summary>Bereitstellungsordner (oder dessen Elternordner im Arbeitsordner) löschen.</summary>
    public void Discard(string staged)
    {
        var full = Path.GetFullPath(staged);
        var root = Path.GetFullPath(_stagingRoot) + Path.DirectorySeparatorChar;
        // Liegt das Plugin in einem Unterordner des Zips, den ganzen entpackten Ordner löschen.
        while (Path.GetDirectoryName(full) is { } parent && parent + Path.DirectorySeparatorChar != root && parent.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            full = parent;
        }
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteDirectory(full);
        }
    }

    /// <summary>Reste abgebrochener Installationen beim Start entfernen.</summary>
    public void CleanUp()
    {
        if (Directory.Exists(_stagingRoot))
        {
            TryDeleteDirectory(_stagingRoot);
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"{path} ließ sich nicht löschen: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
