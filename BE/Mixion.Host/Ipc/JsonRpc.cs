using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Mixion.Host.Ipc;

/// <summary>
/// Standard JSON-RPC 2.0 error codes plus our reserved server range.
/// </summary>
public static class JsonRpcErrorCode
{
    public const int ParseError     = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams  = -32602;
    public const int InternalError  = -32603;

    /// <summary>Engine isn't running yet (host without audio).</summary>
    public const int EngineUnavailable = -32000;
}

/// <summary>
/// Thrown by handlers to surface a typed JSON-RPC error to the client. The
/// dispatcher converts unknown exceptions to <see cref="JsonRpcErrorCode.InternalError"/>;
/// throw this to control the wire-level code/message.
/// </summary>
public sealed class JsonRpcException : Exception
{
    public int Code { get; }
    public object? RpcData { get; }

    public JsonRpcException(int code, string message, object? data = null) : base(message)
    {
        Code    = code;
        RpcData = data;
    }
}

/// <summary>
/// Per-connection state available to RPC handlers. Owned by the WebSocket
/// pump for the lifetime of one socket.
/// </summary>
public interface IRpcConnection
{
    /// <summary>Has this client opted into the binary VU-meter telemetry stream?</summary>
    bool IsTelemetrySubscribed { get; set; }

    /// <summary>Has this client opted into the binary spectrum stream (one channel at a time, FE-side)?</summary>
    bool IsSpectrumSubscribed { get; set; }
}

/// <summary>
/// Concrete <see cref="IRpcConnection"/> tied to a single open WebSocket.
/// Carries the socket + a single shared send-mutex so the WS pump (text
/// responses) and the telemetry broadcaster (binary frames) cooperate
/// without interleaving frames at the protocol level.
/// </summary>
public sealed class RpcConnection : IRpcConnection
{
    public bool IsTelemetrySubscribed { get; set; }
    public bool IsSpectrumSubscribed  { get; set; }

    /// <summary>The underlying open WebSocket. Null until assigned by the pump.</summary>
    public WebSocket? Socket { get; set; }

    /// <summary>
    /// Shared send mutex. Text replies acquire it with <c>WaitAsync</c>;
    /// binary telemetry frames acquire with <c>Wait(0)</c> and drop the
    /// frame on contention (audio path must never block).
    /// </summary>
    public SemaphoreSlim SendLock { get; } = new(1, 1);
}

/// <summary>
/// Signature for a registered RPC handler. <paramref name="params"/> is
/// whatever arrived in the request's <c>params</c> field (may be null/missing);
/// the return value is serialized as the <c>result</c> of the response.
/// </summary>
public delegate Task<object?> RpcHandler(JsonElement? @params, IRpcConnection conn, CancellationToken ct);

/// <summary>
/// JSON-RPC 2.0 dispatcher. Handlers are registered up front; calls go in as
/// raw JSON text frames and come out as raw JSON text responses (or null for
/// notifications). The dispatcher itself is allocation-light but does parse
/// JSON, so it's never invoked from the audio path.
/// </summary>
public sealed class JsonRpcDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, RpcHandler> _handlers = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public JsonRpcDispatcher(ILogger logger)
    {
        _logger = logger;
    }

    public IReadOnlyCollection<string> Methods => _handlers.Keys;

    public void Register(string method, RpcHandler handler)
    {
        if (string.IsNullOrEmpty(method)) throw new ArgumentException("Method name required.", nameof(method));
        _handlers[method] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>
    /// Parse <paramref name="text"/> as a JSON-RPC 2.0 request, dispatch to
    /// the matching handler, and return the serialized response. Returns
    /// null if the request was a notification (no <c>id</c> field).
    /// </summary>
    public async Task<string?> DispatchAsync(string text, IRpcConnection conn, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return SerializeError(null, JsonRpcErrorCode.ParseError, $"Parse error: {ex.Message}");
        }

        try
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return SerializeError(null, JsonRpcErrorCode.InvalidRequest, "Request must be a JSON object.");

            JsonElement? idEl = null;
            if (root.TryGetProperty("id", out var idTmp) && idTmp.ValueKind != JsonValueKind.Null)
                idEl = idTmp;

            if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
                return SerializeError(idEl, JsonRpcErrorCode.InvalidRequest, "Missing or invalid 'method'.");

            var method = methodEl.GetString()!;

            JsonElement? paramsEl = null;
            if (root.TryGetProperty("params", out var paramsTmp))
                paramsEl = paramsTmp;

            if (!_handlers.TryGetValue(method, out var handler))
                return SerializeError(idEl, JsonRpcErrorCode.MethodNotFound, $"Method not found: {method}");

            object? result;
            try
            {
                result = await handler(paramsEl, conn, ct).ConfigureAwait(false);
            }
            catch (JsonRpcException jex)
            {
                return SerializeError(idEl, jex.Code, jex.Message, jex.RpcData);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled RPC exception in {Method}", method);
                return SerializeError(idEl, JsonRpcErrorCode.InternalError, ex.Message);
            }

            // Notifications: no id → no response.
            if (idEl is null) return null;

            return SerializeResult(idEl.Value, result);
        }
        finally
        {
            doc.Dispose();
        }
    }

    /// <summary>
    /// Serialize a result envelope. Caller-supplied <paramref name="id"/> is
    /// written verbatim (preserving its number/string type).
    /// </summary>
    public static string SerializeResult(JsonElement id, object? result)
    {
        using var ms = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            if (result is null)
                writer.WriteNullValue();
            else
                JsonSerializer.Serialize(writer, result, result.GetType(), JsonOptions);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>
    /// Serialize a server → client notification (no <c>id</c>, no response
    /// expected). The SPA dispatches these by <paramref name="method"/> —
    /// e.g. <c>stateChanged</c> and <c>hostShutdown</c>.
    /// </summary>
    public static string SerializeNotification(string method, object? @params)
    {
        using var ms = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            if (@params is not null)
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, @params, @params.GetType(), JsonOptions);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>
    /// Serialize an error envelope. <paramref name="id"/> may be null for
    /// errors that occurred before the id could be parsed (e.g. parse error).
    /// </summary>
    public static string SerializeError(JsonElement? id, int code, string message, object? data = null)
    {
        using var ms = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id is null) writer.WriteNullValue();
            else id.Value.WriteTo(writer);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            if (data is not null)
            {
                writer.WritePropertyName("data");
                JsonSerializer.Serialize(writer, data, data.GetType(), JsonOptions);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>
    /// Helper for handlers: deserialize <paramref name="params"/> into a typed
    /// DTO with consistent error mapping. Throws <see cref="JsonRpcException"/>
    /// (InvalidParams) on missing/malformed input so the dispatcher converts
    /// it to a wire-level error.
    /// </summary>
    public static T RequireParams<T>(JsonElement? @params)
    {
        if (@params is null || @params.Value.ValueKind == JsonValueKind.Null
                            || @params.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, "Missing 'params'.");
        }

        try
        {
            var value = @params.Value.Deserialize<T>(JsonOptions);
            if (value is null)
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, "Null 'params'.");
            return value;
        }
        catch (JsonException ex)
        {
            throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, $"Invalid params: {ex.Message}");
        }
    }
}
