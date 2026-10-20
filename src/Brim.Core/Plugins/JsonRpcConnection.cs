using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Brim.Core.Plugins;

/// <summary>Fehler mit JSON-RPC-Code, z. B. ungültige Parameter (-32602).</summary>
public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public int Code { get; } = code;
}

/// <summary>
/// JSON-RPC 2.0 über einen Datenstrom, eine Nachricht pro Zeile (UTF-8, <c>\n</c>). Wird von Brim und vom
/// SDK genutzt. Lesen läuft im Hintergrund; überlange Zeilen beenden die Verbindung, damit ein Plugin
/// Brim nicht mit Speicher fluten kann.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly int _maxMessageBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private Task? _reader;

    public JsonRpcConnection(Stream stream, int maxMessageBytes = 1 << 20)
    {
        _stream = stream;
        _maxMessageBytes = maxMessageBytes;
    }

    /// <summary>Beantwortet Anfragen der Gegenseite; das Ergebnis wird als <c>result</c> zurückgeschickt.</summary>
    public Func<string, JsonElement?, CancellationToken, Task<object?>>? RequestHandler { get; set; }

    /// <summary>Benachrichtigungen der Gegenseite (ohne Antwort).</summary>
    public Action<string, JsonElement?>? NotificationHandler { get; set; }

    /// <summary>Endet, wenn die Verbindung zu ist; mit Ausnahme, wenn sie wegen eines Protokollfehlers abbrach.</summary>
    public Task Completion => _closed.Task;

    public void Start() => _reader ??= Task.Run(ReadLoopAsync);

    public async Task<T?> InvokeAsync<T>(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters is null ? null : JsonSerializer.SerializeToNode(parameters, PluginJson.Options),
            }, cancellationToken);
            var result = await tcs.Task.WaitAsync(timeout, cancellationToken);
            return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? default : result.Deserialize<T>(PluginJson.Options);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken = default) =>
        WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters is null ? null : JsonSerializer.SerializeToNode(parameters, PluginJson.Options),
        }, cancellationToken);

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(PluginJson.Options) + "\n");
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var line = new MemoryStream();
        Exception? failure = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, _cts.Token);
                if (read == 0)
                {
                    break;
                }
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }
                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    await HandleLineAsync(line.ToArray());
                    line.SetLength(0);
                }
                line.Write(buffer, start, read - start);
                if (line.Length > _maxMessageBytes)
                {
                    throw new JsonRpcException(JsonRpcException.InvalidRequest, $"Nachricht länger als {_maxMessageBytes} Bytes");
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Gegenseite weg: normales Ende.
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new IOException("Verbindung geschlossen"));
        }
        if (failure is null)
        {
            _closed.TrySetResult();
        }
        else
        {
            _closed.TrySetException(failure);
        }
    }

    private async Task HandleLineAsync(byte[] bytes)
    {
        if (bytes.Length == 0 || (bytes.Length == 1 && bytes[0] == (byte)'\r'))
        {
            return;
        }
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = PluginJson.Options.MaxDepth });
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            await SendErrorAsync(null, JsonRpcException.ParseError, "Kein gültiges JSON");
            return;
        }
        if (message.ValueKind != JsonValueKind.Object)
        {
            await SendErrorAsync(null, JsonRpcException.InvalidRequest, "Nachricht muss ein Objekt sein");
            return;
        }

        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        if (message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
        {
            var method = methodElement.GetString()!;
            JsonElement? parameters = message.TryGetProperty("params", out var p) && p.ValueKind != JsonValueKind.Null ? p : null;
            if (!hasId)
            {
                try
                {
                    NotificationHandler?.Invoke(method, parameters);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Benachrichtigung {method} fehlgeschlagen", ex);
                }
                return;
            }
            // Anfragen nebenläufig beantworten; die Reihenfolge der Antworten ist bei JSON-RPC egal.
            _ = Task.Run(() => AnswerAsync(id, method, parameters));
            return;
        }

        // Antwort auf eine eigene Anfrage
        if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && _pending.TryGetValue(number, out var tcs))
        {
            if (message.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : JsonRpcException.InternalError;
                var text = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                tcs.TrySetException(new JsonRpcException(code, text));
            }
            else
            {
                tcs.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
            }
        }
    }

    private async Task AnswerAsync(JsonElement id, string method, JsonElement? parameters)
    {
        try
        {
            if (RequestHandler is not { } handler)
            {
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Unbekannte Methode {method}");
            }
            var result = await handler(method, parameters, _cts.Token);
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["result"] = result is null ? null : JsonSerializer.SerializeToNode(result, result.GetType(), PluginJson.Options),
            }, _cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            var (code, text) = ex switch
            {
                JsonRpcException rpc => (rpc.Code, rpc.Message),
                JsonException json => (JsonRpcException.InvalidParams, json.Message),
                _ => (JsonRpcException.InternalError, ex.Message),
            };
            try
            {
                await SendErrorAsync(id, code, text);
            }
            catch (Exception sendFailure) when (sendFailure is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    private Task SendErrorAsync(JsonElement? id, int code, string text) =>
        WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is { } value ? JsonNode.Parse(value.GetRawText()) : null,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = text },
        }, _cts.Token);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _stream.DisposeAsync();
        if (_reader is not null)
        {
            try
            {
                await _reader;
            }
            catch (Exception)
            {
            }
        }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
