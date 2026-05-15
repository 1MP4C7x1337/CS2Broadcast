// CS2Broadcast - by 1MP4C7 | ImpactGuard Systems

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2Broadcast.Config;
using CS2Broadcast.Models;
using Microsoft.Extensions.Logging;

namespace CS2Broadcast;

/// <summary>
/// Lightweight JSON REST host implemented with <see cref="HttpListener"/>.
/// </summary>
public sealed class HttpApiServer : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly BroadcastConfigAccessor _config;
    private readonly Func<ServerStateResponse> _getState;
    private readonly Func<IReadOnlyList<PlayersApiEntry>> _getPlayers;
    private readonly Func<ScoresResponse> _getScores;
    private readonly Func<string, string?, KickApiResult> _kick;
    private readonly Func<string, SayApiResult> _say;

    private readonly object _lifecycleLock = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>
    /// Initializes the REST gateway with snapshot providers and privileged operations executed by the plugin host.
    /// </summary>
    /// <param name="logger">Structured logger.</param>
    /// <param name="config">Latest configuration accessor.</param>
    /// <param name="getState">Factory returning the cached <see cref="ServerStateResponse"/> snapshot.</param>
    /// <param name="getPlayers">Factory returning connected players.</param>
    /// <param name="getScores">Factory returning team scores.</param>
    /// <param name="kick">Executor that disconnects the requested SteamID64 on the game thread.</param>
    /// <param name="say">Executor that prints a chat line on the game thread.</param>
    public HttpApiServer(
        ILogger logger,
        BroadcastConfigAccessor config,
        Func<ServerStateResponse> getState,
        Func<IReadOnlyList<PlayersApiEntry>> getPlayers,
        Func<ScoresResponse> getScores,
        Func<string, string?, KickApiResult> kick,
        Func<string, SayApiResult> say)
    {
        _logger = logger;
        _config = config;
        _getState = getState;
        _getPlayers = getPlayers;
        _getScores = getScores;
        _kick = kick;
        _say = say;
    }

    /// <summary>
    /// Starts accepting HTTP requests on <see cref="BroadcastConfig.HttpPort"/>.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            StopInternalAsync(waitForCompletion: true).GetAwaiter().GetResult();

            var cfg = _config.Current;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://*:{cfg.HttpPort}/");

            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start REST listener on port {Port}", cfg.HttpPort);
                listener.Close();
                return;
            }

            _listener = listener;
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _logger.LogInformation("CS2Broadcast REST API listening on port {Port}", cfg.HttpPort);
        }
    }

    /// <summary>
    /// Stops the REST listener as cleanly as possible.
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

        lock (_lifecycleLock)
        {
            listener = _listener;
            cts = _cts;
            loop = _acceptLoop;
            _listener = null;
            _cts = null;
            _acceptLoop = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to cancel REST CTS");
        }

        try
        {
            listener?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "REST HttpListener.Stop threw");
        }

        listener?.Close();

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
                _logger.LogDebug(ex, "REST accept loop terminated unexpectedly");
            }
        }

        try
        {
            cts?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "REST CTS dispose failed");
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
                _logger.LogDebug(ex, "REST accept loop error");
                await Task.Delay(50, token).ConfigureAwait(false);
                continue;
            }

            if (context == null)
                continue;

            try
            {
                await HandleRequestAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "REST handler fault");
                TryClose(context.Response, HttpStatusCode.InternalServerError);
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (!IsAuthorized(context.Request))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Unauthorized, new ErrorBody("unauthorized")).ConfigureAwait(false);
                return;
            }

            var verb = context.Request.HttpMethod.ToUpperInvariant();
            var path = context.Request.Url?.AbsolutePath ?? "";

            switch (verb)
            {
                case "GET" when path.Equals("/state", StringComparison.OrdinalIgnoreCase):
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK, _getState()).ConfigureAwait(false);
                    return;

                case "GET" when path.Equals("/players", StringComparison.OrdinalIgnoreCase):
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK, _getPlayers()).ConfigureAwait(false);
                    return;

                case "GET" when path.Equals("/scores", StringComparison.OrdinalIgnoreCase):
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK, _getScores()).ConfigureAwait(false);
                    return;

                case "POST" when path.Equals("/say", StringComparison.OrdinalIgnoreCase):
                    await HandleSayAsync(context).ConfigureAwait(false);
                    return;

                case "POST" when path.Equals("/kick", StringComparison.OrdinalIgnoreCase):
                    await HandleKickAsync(context).ConfigureAwait(false);
                    return;

                default:
                    await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new ErrorBody("not_found")).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "REST request pipeline failed");
            await WriteJsonAsync(context.Response, HttpStatusCode.InternalServerError, new ErrorBody("internal_error")).ConfigureAwait(false);
        }
    }

    private async Task HandleSayAsync(HttpListenerContext context)
    {
        string body;
        using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        SayRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SayRequest>(body, BroadcastSerialization.Options);
        }
        catch (Exception)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new ErrorBody("invalid_json")).ConfigureAwait(false);
            return;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.Message))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new ErrorBody("message_required")).ConfigureAwait(false);
            return;
        }

        var result = _say.Invoke(payload.Message);
        if (!result.Ok)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new ErrorBody(result.Error)).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new StatusBody("ok")).ConfigureAwait(false);
    }

    private async Task HandleKickAsync(HttpListenerContext context)
    {
        string body;
        using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        KickRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<KickRequest>(body, BroadcastSerialization.Options);
        }
        catch (Exception)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new ErrorBody("invalid_json")).ConfigureAwait(false);
            return;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.SteamId))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new ErrorBody("steamid_required")).ConfigureAwait(false);
            return;
        }

        var result = _kick.Invoke(payload.SteamId, payload.Reason);
        if (!result.Ok)
        {
            var status = result.Error switch
            {
                "player_not_found" => HttpStatusCode.NotFound,
                "timeout" => HttpStatusCode.GatewayTimeout,
                _ => HttpStatusCode.BadRequest
            };

            await WriteJsonAsync(context.Response, status, new ErrorBody(result.Error)).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new StatusBody("ok")).ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var token = _config.Current.AuthToken;
        if (string.IsNullOrEmpty(token))
            return true;

        var header = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header))
            return false;

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var presented = header[prefix.Length..].Trim();
        return string.Equals(presented, token, StringComparison.Ordinal);
    }

    private static async Task WriteJsonAsync<T>(HttpListenerResponse response, HttpStatusCode code, T payload)
    {
        response.StatusCode = (int)code;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, BroadcastSerialization.Options);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.OutputStream.Close();
    }

    private static void TryClose(HttpListenerResponse response, HttpStatusCode code)
    {
        try
        {
            response.StatusCode = (int)code;
            response.Close();
        }
        catch
        {
            // Ignored.
        }
    }

    private sealed record ErrorBody(string error);

    private sealed record StatusBody(string status);

    private sealed class SayRequest
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class KickRequest
    {
        [JsonPropertyName("steamid")]
        public string? SteamId { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}

/// <summary>
/// Lightweight status object for privileged <c>/kick</c> operations executed through the game thread.
/// </summary>
public readonly record struct KickApiResult(bool Ok, string Error);

/// <summary>
/// Lightweight status object for <c>/say</c> broadcasts executed through the game thread.
/// </summary>
public readonly record struct SayApiResult(bool Ok, string Error);
