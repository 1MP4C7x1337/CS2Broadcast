// CS2Broadcast - by 1MP4C7 | ImpactGuard Systems

using System.Text.Json.Serialization;

namespace CS2Broadcast.Models;

/// <summary>
/// Describes a participant referenced inside larger WebSocket payloads (for example killers or bomb planters).
/// </summary>
public sealed class PlayerInfo
{
    /// <summary>
    /// SteamID64 formatted as a decimal string (may be empty if unavailable).
    /// </summary>
    [JsonPropertyName("steamid")]
    public required string SteamId { get; init; }

    /// <summary>
    /// Latest player display name.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Numeric CS team assignment (<see cref="CounterStrikeSharp.API.Modules.Utils.CsTeam"/> raw value).
    /// </summary>
    [JsonPropertyName("team")]
    public int Team { get; init; }

    /// <summary>
    /// Optional IPv4/IPv6 observed on the controller (<see cref="CounterStrikeSharp.API.Core.CCSPlayerController.IpAddress"/>).
    /// </summary>
    [JsonPropertyName("ip")]
    public string? Ip { get; init; }
}
