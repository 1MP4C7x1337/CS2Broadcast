// CS2Broadcast - by 1MP4C7 | ImpactGuard Systems

using System.Text.Json.Serialization;

namespace CS2Broadcast.Models;

/// <summary>
/// Canonical envelope broadcast on the WebSocket as JSON.
/// </summary>
public sealed class EventEnvelope
{
    /// <summary>
    /// Logical event name such as <c>player_death</c>.
    /// </summary>
    [JsonPropertyName("event")]
    public required string Event { get; init; }

    /// <summary>
    /// UTC ISO-8601 timestamp string (compact Zulu form).
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    /// <summary>
    /// Hostname advertised by the game server (<c>hostname</c> ConVar).
    /// </summary>
    [JsonPropertyName("server")]
    public required string Server { get; init; }

    /// <summary>
    /// Active map identifier returned by the engine map runtime token.
    /// </summary>
    [JsonPropertyName("map")]
    public required string Map { get; init; }

    /// <summary>
    /// Event-specific structured payload (serialized as a nested JSON object).
    /// </summary>
    [JsonPropertyName("data")]
    public required object Data { get; init; }
}
