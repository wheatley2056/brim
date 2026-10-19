using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brim.Modules.Nearby;

/// <summary>Ein Eintrag im Angebot: relativer Pfad mit „/“, Größe, Ordner oder Datei.</summary>
public sealed record OfferEntry(string Path, long Size, bool Directory);

/// <summary>Was der Absender schicken möchte.</summary>
public sealed record Offer(string SenderId, string SenderName, IReadOnlyList<OfferEntry> Entries)
{
    [JsonIgnore]
    public long TotalBytes => Entries.Sum(e => e.Size);

    /// <summary>Oberste Ebene (Dateien und Ordner, wie sie in der Ablage erscheinen).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> TopLevel => Entries.Select(e => e.Path.Split('/')[0]).Distinct().ToList();
}

internal sealed record Answer(bool Accept, string? Reason = null);

internal sealed record FileHeader(string Path, long Size);

internal sealed record Done(int Count);

/// <summary>
/// Leitungsformat: jede Nachricht ist <c>[uint32 Länge, Big Endian][UTF-8-JSON]</c>; nach einem
/// <see cref="FileHeader"/> folgen genau <c>Size</c> Bytes Dateiinhalt.
/// Ablauf: Angebot → Antwort → (Dateikopf + Daten)* → Fertig → Bestätigung.
/// </summary>
internal static class NearbyProtocol
{
    public const string ServiceType = "_brim._tcp";
    public const int Version = 1;

    /// <summary>Obergrenzen gegen Missbrauch.</summary>
    public const int MaxMessageBytes = 1 << 20;
    public const int MaxEntries = 10_000;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)json.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(json, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Nachricht zu groß ({length} Bytes)");
        }
        var json = new byte[length];
        await stream.ReadExactlyAsync(json, ct);
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Leere Nachricht");
    }

    /// <summary>
    /// Wandelt einen relativen Pfad aus einem Angebot in einen Pfad unterhalb von <paramref name="root"/> um.
    /// Lehnt alles ab, was aus dem Zielordner herausführen könnte.
    /// </summary>
    public static string SafeCombine(string root, string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length == 0 || parts.Length > 64)
        {
            throw new InvalidDataException("Ungültiger Pfad");
        }
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.IndexOfAny(invalid) >= 0 || part.EndsWith('.') || part.EndsWith(' '))
            {
                throw new InvalidDataException($"Ungültiger Pfad: {relative}");
            }
        }
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine([root, .. parts]));
        var rootFull = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Pfad außerhalb des Ziels: {relative}");
        }
        return full;
    }

    /// <summary>Baut das Angebot aus lokalen Dateien und Ordnern (Ordner rekursiv).</summary>
    public static List<(OfferEntry Entry, string? Source)> Collect(IEnumerable<string> paths)
    {
        var result = new List<(OfferEntry, string?)>();
        foreach (var path in paths)
        {
            var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            if (File.Exists(path))
            {
                result.Add((new OfferEntry(name, new FileInfo(path).Length, false), path));
            }
            else if (System.IO.Directory.Exists(path))
            {
                AddFolder(path, name, result);
            }
        }
        if (result.Count > MaxEntries)
        {
            throw new InvalidOperationException($"Zu viele Dateien (höchstens {MaxEntries})");
        }
        return result;
    }

    private static void AddFolder(string folder, string relative, List<(OfferEntry, string?)> result)
    {
        result.Add((new OfferEntry(relative, 0, true), null));
        foreach (var file in System.IO.Directory.EnumerateFiles(folder))
        {
            result.Add((new OfferEntry(relative + "/" + System.IO.Path.GetFileName(file), new FileInfo(file).Length, false), file));
        }
        foreach (var sub in System.IO.Directory.EnumerateDirectories(folder))
        {
            AddFolder(sub, relative + "/" + System.IO.Path.GetFileName(sub), result);
        }
    }
}
