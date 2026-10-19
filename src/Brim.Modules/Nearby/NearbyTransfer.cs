using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Brim.Core;

namespace Brim.Modules.Nearby;

/// <summary>Fortschritt einer Übertragung in Bytes.</summary>
public readonly record struct TransferProgress(long Done, long Total)
{
    public double Fraction => Total <= 0 ? 1 : Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>Eine eingehende Anfrage, über die der Nutzer entscheidet.</summary>
public sealed record IncomingOffer(Offer Offer, string Fingerprint, bool Verified);

/// <summary>
/// Empfängt Dateien: lauscht auf einem freien TCP-Port (IPv4 und IPv6), verlangt gegenseitiges TLS
/// und nimmt nur an, was der Nutzer bestätigt.
/// </summary>
public sealed class NearbyServer : IDisposable
{
    private readonly NearbyIdentity _identity;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public NearbyServer(NearbyIdentity identity)
    {
        _identity = identity;
        _listener = new TcpListener(IPAddress.IPv6Any, 0);
        _listener.Server.DualMode = true;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Fragt den Nutzer. Muss innerhalb der Wartezeit des Absenders antworten.</summary>
    public required Func<IncomingOffer, CancellationToken, Task<bool>> ConfirmAsync { get; init; }

    /// <summary>Liefert für einen Eintrag der obersten Ebene den Zielpfad; wird nach Erfolg mit <see cref="Completed"/> gemeldet.</summary>
    public required Func<string, string> PrepareTarget { get; init; }

    /// <summary>Prüft den Fingerabdruck gegen die Suche: null = unbekannt, sonst erwarteter Wert.</summary>
    public Func<string, string?> ExpectedFingerprint { get; init; } = _ => null;

    public event Action<Offer, TransferProgress>? Progress;
    public event Action<Offer, IReadOnlyList<string>>? Completed;
    public event Action<Offer?, string>? Failed;

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                Log.Warn("Teilen: Annehmen der Verbindung fehlgeschlagen", ex);
                continue;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        Offer? offer = null;
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 90_000;
            client.SendTimeout = 30_000;
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _identity.Certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, _cts.Token);
            if (ssl.RemoteCertificate is not { } remote)
            {
                throw new AuthenticationException("Absender ohne Zertifikat");
            }
            var fingerprint = NearbyIdentity.FingerprintOf(remote);

            offer = await NearbyProtocol.ReadAsync<Offer>(ssl, _cts.Token);
            if (offer.Entries.Count is 0 or > NearbyProtocol.MaxEntries)
            {
                throw new InvalidDataException("Leeres oder zu großes Angebot");
            }
            // Kennt die Suche diesen Absender, muss sein Zertifikat passen.
            var expected = ExpectedFingerprint(offer.SenderId);
            if (expected is not null && expected != fingerprint)
            {
                await NearbyProtocol.WriteAsync(ssl, new Answer(false, "Zertifikat passt nicht"), _cts.Token);
                throw new AuthenticationException($"Fingerabdruck von {offer.SenderName} stimmt nicht");
            }

            using var decision = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            decision.CancelAfter(TimeSpan.FromSeconds(60));
            bool accepted;
            try
            {
                accepted = await ConfirmAsync(new IncomingOffer(offer, fingerprint, expected is not null), decision.Token);
            }
            catch (OperationCanceledException)
            {
                accepted = false;
            }
            await NearbyProtocol.WriteAsync(ssl, new Answer(accepted, accepted ? null : "Abgelehnt"), _cts.Token);
            if (!accepted)
            {
                return;
            }

            // Vorab prüfen: jeder Pfad muss gültig sein, bevor irgendetwas angelegt oder geschrieben wird.
            foreach (var entry in offer.Entries)
            {
                NearbyProtocol.SafeCombine(Path.GetTempPath(), entry.Path);
            }

            var roots = offer.TopLevel.ToDictionary(t => t, PrepareTarget, StringComparer.Ordinal);
            string Target(string relative)
            {
                var top = relative.Split('/')[0];
                var rest = relative.Length > top.Length ? relative[(top.Length + 1)..] : "";
                var root = roots[top];
                return rest.Length == 0 ? root : NearbyProtocol.SafeCombine(root, rest);
            }

            foreach (var dir in offer.Entries.Where(e => e.Directory))
            {
                Directory.CreateDirectory(Target(dir.Path));
            }

            var total = offer.TotalBytes;
            long done = 0;
            var files = offer.Entries.Where(e => !e.Directory).ToDictionary(e => e.Path, StringComparer.Ordinal);
            var buffer = new byte[256 * 1024];
            var received = 0;
            while (true)
            {
                var header = await NearbyProtocol.ReadAsync<FileHeader>(ssl, _cts.Token);
                if (header.Path.Length == 0)
                {
                    break; // Ende-Markierung
                }
                if (!files.Remove(header.Path, out var announced) || announced.Size != header.Size)
                {
                    throw new InvalidDataException($"Unerwartete Datei: {header.Path}");
                }
                var target = Target(header.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
                {
                    var remaining = header.Size;
                    while (remaining > 0)
                    {
                        var read = await ssl.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), _cts.Token);
                        if (read == 0)
                        {
                            throw new EndOfStreamException("Verbindung während der Übertragung beendet");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                        remaining -= read;
                        done += read;
                        Progress?.Invoke(offer, new TransferProgress(done, total));
                    }
                }
                received++;
            }
            if (files.Count > 0)
            {
                throw new InvalidDataException($"{files.Count} angekündigte Dateien fehlen");
            }
            await NearbyProtocol.WriteAsync(ssl, new Done(received), _cts.Token);
            Completed?.Invoke(offer, roots.Values.ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            Log.Warn("Teilen: Empfang fehlgeschlagen", ex);
            Failed?.Invoke(offer, ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}

/// <summary>Sendet Dateien an ein anderes Brim.</summary>
public static class NearbyClient
{
    public static async Task SendAsync(
        NearbyIdentity identity,
        NearbyPeer peer,
        IReadOnlyList<string> paths,
        IProgress<TransferProgress>? progress,
        CancellationToken ct)
    {
        var collected = NearbyProtocol.Collect(paths);
        if (collected.Count == 0)
        {
            throw new InvalidOperationException("Nichts zu senden");
        }
        var offer = new Offer(identity.Id, identity.Name, collected.Select(c => c.Entry).ToList());

        using var tcp = new TcpClient(AddressFamily.InterNetworkV6) { Client = { DualMode = true } };
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(peer.Addresses, peer.Port, connect.Token);
        }
        tcp.ReceiveTimeout = 90_000;
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
            (_, certificate, _, _) => certificate is not null && NearbyIdentity.FingerprintOf(certificate) == peer.Fingerprint);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = peer.Id,
            ClientCertificates = [identity.Certificate],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, ct);

        await NearbyProtocol.WriteAsync(ssl, offer, ct);
        var answer = await NearbyProtocol.ReadAsync<Answer>(ssl, ct);
        if (!answer.Accept)
        {
            throw new NearbyDeclinedException(answer.Reason ?? "Abgelehnt");
        }

        var total = offer.TotalBytes;
        long done = 0;
        var buffer = new byte[256 * 1024];
        foreach (var (entry, source) in collected.Where(c => !c.Entry.Directory))
        {
            await NearbyProtocol.WriteAsync(ssl, new FileHeader(entry.Path, entry.Size), ct);
            await using var input = new FileStream(source!, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
            var remaining = entry.Size;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read == 0)
                {
                    throw new IOException($"{entry.Path} wurde während des Sendens kürzer");
                }
                await ssl.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
                done += read;
                progress?.Report(new TransferProgress(done, total));
            }
        }
        await NearbyProtocol.WriteAsync(ssl, new FileHeader("", 0), ct);
        await NearbyProtocol.ReadAsync<Done>(ssl, ct);
        progress?.Report(new TransferProgress(total, total));
    }
}

public sealed class NearbyDeclinedException(string reason) : Exception(reason);
