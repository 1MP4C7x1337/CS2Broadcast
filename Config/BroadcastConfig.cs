// CS2Broadcast - by 1MP4C7 | ImpactGuard Systems

using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace CS2Broadcast.Config;

/// <summary>
/// Serializable plugin configuration loaded from <c>counterstrikesharp/configs/plugins/CS2Broadcast/CS2Broadcast.json</c>.
/// </summary>
public sealed class BroadcastConfig : BasePluginConfig
{
    /// <summary>
    /// TCP port for the WebSocket endpoint (<c>/ws</c>). Default <c>7070</c>.
    /// </summary>
    [JsonPropertyName("ws_port")]
    public int WsPort { get; set; } = 7070;

    /// <summary>
    /// TCP port for the HTTP REST API. Default <c>7071</c>.
    /// </summary>
    [JsonPropertyName("http_port")]
    public int HttpPort { get; set; } = 7071;

    /// <summary>
    /// Shared secret for optional authentication. Leave empty to disable auth checks on WebSocket and REST.
    /// </summary>
    [JsonPropertyName("auth_token")]
    public string AuthToken { get; set; } = "";

    /// <summary>
    /// When <see langword="true"/>, successful WebSocket connections and disconnections are written to the plugin log.
    /// </summary>
    [JsonPropertyName("log_connections")]
    public bool LogConnections { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, public chat messages are forwarded as WebSocket events.
    /// </summary>
    [JsonPropertyName("broadcast_chat")]
    public bool BroadcastChat { get; set; } = true;

    /// <summary>
    /// Optional descriptive marker ignored by the runtime parser (human-readable attribution).
    /// </summary>
    [JsonPropertyName("__meta")]
    public string? Meta { get; set; }
}
