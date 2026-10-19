using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Brim.Core;
using Brim.Modules.Nearby;

namespace Brim.Modules.Wormhole;

/// <summary>Was per Code angeboten wird.</summary>
public sealed record WormholeOffer(string Name, long Size, bool IsDirectory, int FileCount, string? Text);

/// <summary>
/// Dateien und Ordner per Code übertragen, kompatibel mit „wormhole send/receive“ (Dateiprotokoll v1):
/// Angebot über die Mailbox, Daten über Transit, am Ende eine SHA-256-Quittung.
/// </summary>
public sealed class WormholeTransfer
{
    private const int ChunkSize = 256 * 1024;

    private readonly ShareSettings _settings;

    public WormholeTransfer(ShareSettings settings)
    {
        _settings = settings;
    }

    private NetConnector Net => NetConnector.From(_settings);

    /// <summary>Direkte Wege anbieten und nutzen (für Tests abschaltbar, um das Relay zu prüfen).</summary>
    internal bool UseDirect { get; init; } = true;

    // ───────── Senden ─────────

    /// <summary>
    /// Holt einen Code (über <paramref name="onCode"/> gemeldet), wartet auf den Empfänger und sendet.
    /// Eine Datei geht direkt, mehrere Einträge oder Ordner als ZIP wie beim Original.
    /// </summary>
    public async Task SendAsync(IReadOnlyList<string> paths, Action<string> onCode, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var (offer, payload, size, temporary) = await Task.Run(() => BuildOffer(paths), ct);
        try
        {
            await using var session = await WormholeSession.CreateAsync(Net, _settings.MailboxUrl, 2, ct);
            onCode(session.Code);
            await session.EstablishAsync(ct);

            await using var transit = new Transit(isSender: true, session.DeriveKey(WormholeSession.AppId + "/transit-key"), Net, _settings.TransitRelay, UseDirect);
            await session.SendAsync(new JsonObject { ["transit"] = transit.Describe() }, ct);
            await session.SendAsync(new JsonObject { ["offer"] = offer }, ct);

            List<TransitHint> hints = [];
            while (true)
            {
                var message = await session.ReceiveAsync(ct);
                if (message["error"] is { } error)
                {
                    throw new WormholeRemoteException(error.GetValue<string>());
                }
                if (message["transit"] is { } theirs)
                {
                    hints = Transit.Parse(theirs);
                }
                if (message["answer"] is { } answer)
                {
                    if (answer["file_ack"]?.GetValue<string>() != "ok")
                    {
                        throw new WormholeRemoteException("Empfänger hat abgelehnt");
                    }
                    break;
                }
            }

            await using var pipe = await transit.ConnectAsync(hints, ct);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true))
            {
                var buffer = new byte[ChunkSize];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await pipe.SendAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(new TransferProgress(done, size));
                }
            }

            var ack = JsonNode.Parse(await pipe.ReceiveAsync(ct));
            if (ack?["ack"]?.GetValue<string>() != "ok")
            {
                throw new WormholeRemoteException("Empfänger hat den Empfang nicht bestätigt");
            }
            if (ack["sha256"]?.GetValue<string>() is { } theirHash && !string.Equals(theirHash, Convert.ToHexStringLower(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase))
            {
                throw new WormholeRemoteException("Prüfsumme beim Empfänger stimmt nicht");
            }
        }
        finally
        {
            if (temporary)
            {
                TryDelete(payload);
            }
        }
    }

    private static (JsonObject Offer, string Payload, long Size, bool Temporary) BuildOffer(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1 && File.Exists(paths[0]))
        {
            var info = new FileInfo(paths[0]);
            return (new JsonObject { ["file"] = new JsonObject { ["filename"] = info.Name, ["filesize"] = info.Length } }, info.FullName, info.Length, false);
        }

        // Ein Ordner: Inhalt relativ zum Ordner (wie das Original). Mehrere Einträge: gemeinsamer Ordner „Ablage“.
        var single = paths.Count == 1 && Directory.Exists(paths[0]);
        var dirname = single ? Path.GetFileName(paths[0].TrimEnd('\\', '/')) : "Ablage";
        var zip = Path.Combine(Path.GetTempPath(), $"brim-wormhole-{Guid.NewGuid():N}.zip");
        long numBytes = 0;
        var numFiles = 0;
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var path in paths)
            {
                var prefix = single ? "" : Path.GetFileName(path.TrimEnd('\\', '/')) + "/";
                if (File.Exists(path))
                {
                    AddFile(archive, path, prefix.TrimEnd('/'));
                    numBytes += new FileInfo(path).Length;
                    numFiles++;
                }
                else if (Directory.Exists(path))
                {
                    AddFolder(archive, path, prefix, ref numBytes, ref numFiles);
                }
            }
        }
        var zipSize = new FileInfo(zip).Length;
        var offer = new JsonObject
        {
            ["directory"] = new JsonObject
            {
                ["mode"] = "zipfile/deflated",
                ["dirname"] = dirname,
                ["zipsize"] = zipSize,
                ["numbytes"] = numBytes,
                ["numfiles"] = numFiles,
            },
        };
        return (offer, zip, zipSize, true);
    }

    // Unix-Rechte im oberen Wort der externen Attribute, wie zip unter Unix: Das Original setzt sie beim
    // Entpacken (os.chmod). Ohne sie wären empfangene Dateien schreibgeschützt bzw. unter Linux unlesbar.
    private const int FileAttributes = unchecked((int)(0x81A4u << 16));           // 0o100644, normale Datei
    private const int DirectoryAttributes = unchecked((int)((0x41EDu << 16) | 0x10)); // 0o040755 + MS-DOS-Ordnerbit

    private static void AddFile(ZipArchive archive, string path, string name) =>
        archive.CreateEntryFromFile(path, name, CompressionLevel.Optimal).ExternalAttributes = FileAttributes;

    private static void AddFolder(ZipArchive archive, string folder, string prefix, ref long numBytes, ref int numFiles)
    {
        var entries = Directory.EnumerateFileSystemEntries(folder).ToList();
        if (entries.Count == 0 && prefix.Length > 0)
        {
            archive.CreateEntry(prefix).ExternalAttributes = DirectoryAttributes; // leerer Ordner bleibt erhalten
        }
        foreach (var entry in entries)
        {
            var name = prefix + Path.GetFileName(entry);
            if (File.Exists(entry))
            {
                AddFile(archive, entry, name);
                numBytes += new FileInfo(entry).Length;
                numFiles++;
            }
            else
            {
                AddFolder(archive, entry, name + "/", ref numBytes, ref numFiles);
            }
        }
    }

    // ───────── Empfangen ─────────

    /// <summary>
    /// Tritt mit dem Code bei, fragt über <paramref name="confirm"/> und empfängt. Liefert die angelegten
    /// Pfade (Datei, Ordner oder Textdatei); bei Ablehnung eine leere Liste.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReceiveAsync(
        string code,
        Func<WormholeOffer, CancellationToken, Task<bool>> confirm,
        Func<string, string> prepareTarget,
        IProgress<TransferProgress>? progress,
        CancellationToken ct)
    {
        await using var session = await WormholeSession.JoinAsync(Net, _settings.MailboxUrl, code, ct);
        await session.EstablishAsync(ct);

        Transit? transit = null;
        List<TransitHint> hints = [];
        try
        {
            while (true)
            {
                var message = await session.ReceiveAsync(ct);
                if (message["error"] is { } error)
                {
                    throw new WormholeRemoteException(error.GetValue<string>());
                }
                if (message["transit"] is { } theirs && transit is null)
                {
                    hints = Transit.Parse(theirs);
                    transit = new Transit(isSender: false, session.DeriveKey(WormholeSession.AppId + "/transit-key"), Net, _settings.TransitRelay, UseDirect);
                    await session.SendAsync(new JsonObject { ["transit"] = transit.Describe() }, ct);
                }
                if (message["offer"] is not JsonObject offer)
                {
                    continue;
                }

                if (offer["message"]?.GetValue<string>() is { } text)
                {
                    var target = prepareTarget("Nachricht.txt");
                    await File.WriteAllTextAsync(target, text, Encoding.UTF8, ct);
                    await session.SendAsync(new JsonObject { ["answer"] = new JsonObject { ["message_ack"] = "ok" } }, ct);
                    return [target];
                }

                var (info, size) = ReadOffer(offer);
                if (!await confirm(info, ct))
                {
                    await session.SendAsync(new JsonObject { ["error"] = "transfer rejected" }, ct);
                    return [];
                }
                if (transit is null)
                {
                    throw new InvalidDataException("Absender hat keine Verbindungswege genannt");
                }
                await session.SendAsync(new JsonObject { ["answer"] = new JsonObject { ["file_ack"] = "ok" } }, ct);
                return [await ReceiveDataAsync(transit, hints, info, size, prepareTarget, progress, ct)];
            }
        }
        finally
        {
            if (transit is not null)
            {
                await transit.DisposeAsync();
            }
        }
    }

    private static (WormholeOffer Info, long Size) ReadOffer(JsonObject offer)
    {
        if (offer["file"] is { } file)
        {
            var name = SafeName(file["filename"]?.GetValue<string>());
            var size = file["filesize"]?.GetValue<long>() ?? -1;
            return (new WormholeOffer(name, size, false, 1, null), size);
        }
        if (offer["directory"] is { } dir)
        {
            if (dir["mode"]?.GetValue<string>()?.StartsWith("zipfile", StringComparison.Ordinal) != true)
            {
                throw new InvalidDataException("Unbekanntes Ordnerformat");
            }
            var name = SafeName(dir["dirname"]?.GetValue<string>());
            return (new WormholeOffer(name, dir["numbytes"]?.GetValue<long>() ?? 0, true, dir["numfiles"]?.GetValue<int>() ?? 0, null),
                dir["zipsize"]?.GetValue<long>() ?? -1);
        }
        throw new InvalidDataException("Unbekanntes Angebot");
    }

    private static string SafeName(string? name)
    {
        var safe = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "");
        safe = string.Concat(safe.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return safe is "" or "." or ".." ? "Empfangen" : safe;
    }

    private static async Task<string> ReceiveDataAsync(
        Transit transit, List<TransitHint> hints, WormholeOffer info, long size,
        Func<string, string> prepareTarget, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (size < 0)
        {
            throw new InvalidDataException("Größe fehlt im Angebot");
        }
        await using var pipe = await transit.ConnectAsync(hints, ct);
        var target = prepareTarget(info.Name);
        var file = info.IsDirectory ? Path.Combine(Path.GetTempPath(), $"brim-wormhole-{Guid.NewGuid():N}.zip") : target;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, useAsync: true))
            {
                long done = 0;
                while (done < size)
                {
                    var record = await pipe.ReceiveAsync(ct);
                    if (done + record.Length > size)
                    {
                        throw new InvalidDataException("Absender schickt mehr als angekündigt");
                    }
                    hash.AppendData(record);
                    await output.WriteAsync(record, ct);
                    done += record.Length;
                    progress?.Report(new TransferProgress(done, size));
                }
            }
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            await pipe.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["ack"] = "ok", ["sha256"] = digest }), ct);

            if (info.IsDirectory)
            {
                Extract(file, target, info.Size);
            }
            return target;
        }
        finally
        {
            if (info.IsDirectory)
            {
                TryDelete(file);
            }
        }
    }

    /// <summary>Entpackt sicher: keine Pfade außerhalb des Ziels, nicht mehr als angekündigt (plus Spielraum).</summary>
    private static void Extract(string zip, string target, long announced)
    {
        var root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
        Directory.CreateDirectory(root);
        var limit = Math.Max(announced, 0) + 64L * 1024 * 1024;
        long written = 0;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', '\\')));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"ZIP enthält einen Pfad außerhalb des Ziels: {entry.FullName}");
            }
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            written += entry.Length;
            if (written > limit)
            {
                throw new InvalidDataException("ZIP entpackt größer als angekündigt");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
