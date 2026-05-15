// CS2Broadcast - by 1MP4C7 | ImpactGuard Systems

using System.Text.Json.Serialization;

namespace CS2Broadcast.Models;

/// <summary>
/// Aggregate snapshot returned by <c>GET /state</c>.
/// </summary>
public sealed class ServerStateResponse
{
    /// <summary>
    /// Active map identifier.
    /// </summary>
    [JsonPropertyName("map")]
    public required string Map { get; init; }

    /// <summary>
    /// Friendly hostname (<c>hostname</c> ConVar).
    /// </summary>
    [JsonPropertyName("hostname")]
    public required string Hostname { get; init; }

    /// <summary>
    /// Number of connected human controllers tracked by CounterStrikeSharp utilities.
    /// </summary>
    [JsonPropertyName("player_count")]
    public int PlayerCount { get; init; }

    /// <summary>
    /// Latest captured counter-terrorist score.
    /// </summary>
    [JsonPropertyName("ct_score")]
    public int CtScore { get; init; }

    /// <summary>
    /// Latest captured terrorist score.
    /// </summary>
    [JsonPropertyName("t_score")]
    public int TScore { get; init; }

    /// <summary>
    /// Approximate round counter sourced from gamerules (<c>m_iTotalRoundsPlayed</c>).
    /// </summary>
    [JsonPropertyName("round_number")]
    public int RoundNumber { get; init; }
}

/// <summary>
/// Describes an entry returned by <c>GET /players</c>.
/// </summary>
public sealed class PlayersApiEntry
{
    /// <summary>
    /// SteamID64 decimal string when authenticated.
    /// </summary>
    [JsonPropertyName("steamid")]
    public required string SteamId { get; init; }

    /// <summary>
    /// Player display name.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Latency reported by the engine for UI (<see cref="CounterStrikeSharp.API.Core.CCSPlayerController.Ping"/>).
    /// </summary>
    [JsonPropertyName("ping")]
    public uint Ping { get; init; }

    /// <summary>
    /// Scoreboard score (<see cref="CounterStrikeSharp.API.Core.CCSPlayerController.Score"/>).
    /// </summary>
    [JsonPropertyName("score")]
    public int Score { get; init; }

    /// <summary>
    /// Numeric CS team assignment.
    /// </summary>
    [JsonPropertyName("team")]
    public int Team { get; init; }

    /// <summary>
    /// Whether the pawn appears alive at sampling time (HP &gt; 0 heuristic).
    /// </summary>
    [JsonPropertyName("alive")]
    public bool Alive { get; init; }
}

/// <summary>
/// Compact score payload returned by <c>GET /scores</c>.
/// </summary>
public sealed class ScoresResponse
{
    /// <summary>
    /// Counter-Terrorist rounds won (aggregated halves/overtime buckets).
    /// </summary>
    [JsonPropertyName("ct_score")]
    public int CtScore { get; init; }

    /// <summary>
    /// Terrorist rounds won (aggregated halves/overtime buckets).
    /// </summary>
    [JsonPropertyName("t_score")]
    public int TScore { get; init; }
}
