// CS2Broadcast - by 1MP4C7 | ImpactGuard Systems

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2Broadcast.Config;
using CS2Broadcast.Models;
using Microsoft.Extensions.Logging;

namespace CS2Broadcast;

/// <summary>
/// Hosts an <see cref="HttpListener"/> that upgrades <c>GET /ws</c> requests to WebSocket sessions and fans out JSON payloads.
/// </summary>
public sealed class WebSocketServer : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly BroadcastConfigAccessor _config;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly object _lifecycleLock = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>
    /// Creates a WebSocket gateway that reads live settings from <paramref name="config"/>.
    /// </summary>
    /// <param name="logger">Structured logger provided by CounterStrikeSharp.</param>
    /// <param name="config">Accessor that always returns the latest <see cref="Config.BroadcastConfig"/> instance.</param>
    public WebSocketServer(ILogger logger, BroadcastConfigAccessor config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Starts accepting connections on the configured WebSocket port (defaults to wildcard bindings).
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            StopInternalAsync(waitForCompletion: true).GetAwaiter().GetResult();

            var cfg = _config.Current;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://*:{cfg.WsPort}/");

            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start WebSocket listener on port {Port}", cfg.WsPort);
                listener.Close();
                return;
            }

            _listener = listener;
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _logger.LogInformation("CS2Broadcast WebSocket listening on port {Port} (/ws)", cfg.WsPort);
        }
    }

    /// <summary>
    /// Enqueues a UTF-8 JSON payload for asynchronous fan-out (never invoked on the game thread synchronously).
    /// </summary>
    /// <param name="json">Serialized event envelope.</param>
    public void EnqueueBroadcast(string json)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await BroadcastCoreAsync(json).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebSocket broadcast failed");
            }
        });
    }

    /// <summary>
    /// Serializes <paramref name="envelope"/> using the shared broadcast options and forwards it to all clients.
    /// </summary>
    public void EnqueueBroadcast(EventEnvelope envelope)
    {
        try
        {
            var json = JsonSerializer.Serialize(envelope, BroadcastSerialization.Options);
            EnqueueBroadcast(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to serialize event {Event}", envelope.Event);
        }
    }

    /// <summary>
    /// Stops the listener, cancels pending accepts, and closes every active socket.
    /// </summary>
    public async Task StopAsync()
    {
        await StopInternalAsync(waitForCompletion: true).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task StopInternalAsync(bool waitForCompletion)
    {
        HttpListener? listener;
        CancellationTokenSource? cts;
        Task? loop;
        IEnumerable<KeyValuePair<Guid, WebSocket>> sockets;

        lock (_lifecycleLock)
        {
            listener = _listener;
            cts = _cts;
            loop = _acceptLoop;
            sockets = _clients.ToArray();
            _listener = null;
            _cts = null;
            _acceptLoop = null;
            _clients.Clear();
        }

        try
        {
            cts?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to cancel WebSocket CTS");
        }

        try
        {
            listener?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HttpListener.Stop threw");
        }

        listener?.Close();

        foreach (var (_, socket) in sockets)
        {
            await SafeCloseAsync(socket).ConfigureAwait(false);
        }

        if (waitForCompletion && loop != null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebSocket accept loop terminated unexpectedly");
            }
        }

        try
        {
            cts?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CTS dispose failed");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            HttpListenerContext? context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebSocket accept loop error");
                await Task.Delay(50, token).ConfigureAwait(false);
                continue;
            }

            if (context == null)
                continue;

            _ = Task.Run(() => HandleContextAsync(context, token), token);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "";
            if (!path.Equals("/ws", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            if (!TryAuthorizeHandshake(context.Request.Url))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                context.Response.Close();
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }

            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            var socket = wsContext.WebSocket;
            var id = Guid.NewGuid();
            _clients[id] = socket;

            if (_config.Current.LogConnections)
                _logger.LogInformation("WebSocket client connected ({ClientCount} active)", _clients.Count);

            await ReceivePumpAsync(id, socket, token).ConfigureAwait(false);

            _clients.TryRemove(id, out _);
            await SafeCloseAsync(socket).ConfigureAwait(false);

            if (_config.Current.LogConnections)
                _logger.LogInformation("WebSocket client disconnected ({ClientCount} active)", _clients.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebSocket handshake failure");
            TryCloseResponse(context.Response);
        }
    }

    private bool TryAuthorizeHandshake(Uri? uri)
    {
        var expected = _config.Current.AuthToken;
        if (string.IsNullOrEmpty(expected))
            return true;

        var provided = QueryTokenParser.Get(uri?.Query, "token");
        return string.Equals(provided, expected, StringComparison.Ordinal);
    }

    private async Task ReceivePumpAsync(Guid id, WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket receive ended for {ClientId}", id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unexpected error in receive pump for {ClientId}", id);
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    private async Task BroadcastCoreAsync(string json)
    {
        if (_clients.Count == 0)
            return;

        var payload = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(payload);

        foreach (var kvp in _clients.ToArray())
        {
            var socket = kvp.Value;
            if (socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(kvp.Key, out _);
                await SafeCloseAsync(socket).ConfigureAwait(false);
                continue;
            }

            try
            {
                await socket.SendAsync(segment, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                _clients.TryRemove(kvp.Key, out _);
                await SafeCloseAsync(socket).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dropping dead WebSocket client {ClientId}", kvp.Key);
                _clients.TryRemove(kvp.Key, out _);
                await SafeCloseAsync(socket).ConfigureAwait(false);
            }
        }
    }

    private static async Task SafeCloseAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort.
        }

        try
        {
            socket.Dispose();
        }
        catch
        {
            // Ignored.
        }
    }

    private static void TryCloseResponse(HttpListenerResponse? response)
    {
        try
        {
            response?.Close();
        }
        catch
        {
            // Ignored.
        }
    }
}

/// <summary>
/// Thread-safe accessor that always returns the latest configuration object.
/// </summary>
/// <remarks>
/// This indirection lets background HTTP/WebSocket threads read fresh tokens/ports without capturing a mutable field.
/// </remarks>
public sealed class BroadcastConfigAccessor
{
    private readonly Func<BroadcastConfig> _factory;

    /// <summary>
    /// Initializes a new accessor bound to <paramref name="factory"/>.
    /// </summary>
    /// <param name="factory">Returns the latest configuration snapshot (may be invoked from background threads).</param>
    public BroadcastConfigAccessor(Func<BroadcastConfig> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Gets the current <see cref="BroadcastConfig"/> instance.
    /// </summary>
    public BroadcastConfig Current => _factory();
}

internal static class QueryTokenParser
{
    /// <summary>
    /// Reads a query-string parameter from a raw <c>?a=1&amp;b=2</c> fragment.
    /// </summary>
    public static string? Get(string? query, string key)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var chunk in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = chunk.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;

            if (!string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
                continue;

            return Uri.UnescapeDataString(parts[1]);
        }

        return null;
    }
}
internal static class BroadcastSerialization
{
    /// <summary>
    /// Shared JSON serializer options for outbound WebSocket envelopes and REST payloads.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
