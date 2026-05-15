// CS2Broadcast - by 1MP4C7 | ImpactGuard Systems

using System.Globalization;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using CS2Broadcast.Models;
using Microsoft.Extensions.Logging;

namespace CS2Broadcast;

/// <summary>
/// Hooks Counter-Strike 2 gameplay events and forwards normalized JSON payloads to <see cref="WebSocketServer"/>.
/// </summary>
public sealed class EventBroadcaster : IDisposable
{
    private readonly CS2BroadcastPlugin _plugin;
    private readonly WebSocketServer _webSockets;
    private readonly ILogger _logger;
    private string _previousMapName = "";
    private int _shutdownBroadcast;

    /// <summary>
    /// Initializes the broadcaster and registers all configured CounterStrikeSharp hooks.
    /// </summary>
    /// <param name="plugin">Owning plugin instance (used for HostFrame utilities).</param>
    /// <param name="webSockets">Active WebSocket gateway.</param>
    /// <param name="logger">Structured logger.</param>
    public EventBroadcaster(CS2BroadcastPlugin plugin, WebSocketServer webSockets, ILogger logger)
    {
        _plugin = plugin;
        _webSockets = webSockets;
        _logger = logger;

        RegisterHandlers();
        TryCaptureInitialMap();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // CounterStrikeSharp tears down hooks automatically when the plugin unloads.
        GC.SuppressFinalize(this);
    }

    private void RegisterHandlers()
    {
        _plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        _plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        _plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        _plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        _plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        _plugin.RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        _plugin.RegisterEventHandler<EventBombDefused>(OnBombDefused);
        _plugin.RegisterEventHandler<EventBombExploded>(OnBombExploded);
        _plugin.RegisterEventHandler<EventGameNewmap>(OnGameNewmap);
        _plugin.RegisterEventHandler<EventServerPreShutdown>(OnServerPreShutdown);

        _plugin.RegisterEventHandler<EventPlayerChat>(OnPlayerChat);
    }

    private void TryCaptureInitialMap()
    {
        try
        {
            _previousMapName = NativeAPI.GetMapName();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to capture initial map name");
            _previousMapName = "";
        }
    }

    private HookResult OnPlayerDeath(EventPlayerDeath e, GameEventInfo _)
    {
        try
        {
            var killer = PlayerSnapshots.FromController(e.Attacker);
            var victim = PlayerSnapshots.FromController(e.Userid);

            var assists = new List<PlayerInfo>();
            var assistController = PlayerSnapshots.TryGetController(e.Assister);
            if (assistController != null)
                assists.Add(PlayerSnapshots.FromRequiredController(assistController));

            Broadcast("player_death", new
            {
                killer,
                victim,
                weapon = e.Weapon ?? "",
                headshot = e.Headshot,
                assisters = assists
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast player_death");
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull e, GameEventInfo _)
    {
        try
        {
            var controller = PlayerSnapshots.TryGetController(e.Userid);
            if (controller == null || !controller.IsValid || controller.AuthorizedSteamID == null)
                return HookResult.Continue;

            Broadcast("player_connect", new
            {
                steamid = controller.AuthorizedSteamID.SteamId64.ToString(CultureInfo.InvariantCulture),
                name = controller.PlayerName,
                ip = controller.IpAddress ?? ""
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast player_connect");
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect e, GameEventInfo _)
    {
        try
        {
            var steamId = FormatSteamId(e.Xuid, e.Networkid);
            Broadcast("player_disconnect", new
            {
                steamid = steamId,
                name = e.Name ?? "",
                reason = e.Reason
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast player_disconnect");
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart e, GameEventInfo _)
    {
        try
        {
            var round = GameQueries.TryReadRoundNumber(out var rn) ? rn : 0;
            Broadcast("round_start", new
            {
                round_number = round,
                map = SafeMap()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast round_start");
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd e, GameEventInfo _)
    {
        try
        {
            GameQueries.TryReadScores(out var ct, out var t);

            Broadcast("round_end", new
            {
                winner_team = FormatWinner(e.Winner),
                reason = e.Reason,
                ct_score = ct,
                t_score = t
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast round_end");
        }

        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted e, GameEventInfo _)
    {
        try
        {
            var planter = PlayerSnapshots.FromController(e.Userid);
            Broadcast("bomb_planted", new
            {
                player = planter,
                site = BombSiteFormatter.Format(e.Site)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast bomb_planted");
        }

        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused e, GameEventInfo _)
    {
        try
        {
            var defuser = PlayerSnapshots.FromController(e.Userid);
            Broadcast("bomb_defused", new
            {
                player = defuser,
                site = BombSiteFormatter.Format(e.Site)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast bomb_defused");
        }

        return HookResult.Continue;
    }

    private HookResult OnBombExploded(EventBombExploded e, GameEventInfo _)
    {
        try
        {
            Broadcast("bomb_exploded", new { site = BombSiteFormatter.Format(e.Site) });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast bomb_exploded");
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerChat(EventPlayerChat e, GameEventInfo _)
    {
        if (!_plugin.Config.BroadcastChat)
            return HookResult.Continue;

        try
        {
            var controller = Utilities.GetPlayerFromUserid(e.Userid);
            var steamId = controller?.AuthorizedSteamID?.SteamId64.ToString(CultureInfo.InvariantCulture) ?? "";

            Broadcast("player_chat", new
            {
                steamid = steamId,
                name = controller?.PlayerName ?? "",
                message = e.Text ?? "",
                team_only = e.Teamonly
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast player_chat");
        }

        return HookResult.Continue;
    }

    private HookResult OnGameNewmap(EventGameNewmap e, GameEventInfo _)
    {
        try
        {
            var newMap = e.Mapname ?? SafeMap();
            var oldMap = string.IsNullOrEmpty(_previousMapName) ? "" : _previousMapName;

            Broadcast("map_change", new
            {
                old_map = oldMap,
                new_map = newMap,
                transition = e.Transition
            });

            _previousMapName = newMap;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast map_change");
        }

        return HookResult.Continue;
    }

    private HookResult OnServerPreShutdown(EventServerPreShutdown _, GameEventInfo __)
    {
        EmitShutdownSignal();
        return HookResult.Continue;
    }

    /// <summary>
    /// Broadcasts the synthetic <c>server_shutdown</c> envelope exactly once per process lifetime.
    /// </summary>
    public void EmitShutdownSignal()
    {
        if (Interlocked.Exchange(ref _shutdownBroadcast, 1) != 0)
            return;

        try
        {
            Broadcast("server_shutdown", new { graceful = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast server_shutdown");
        }
    }

    private void Broadcast(string eventName, object data)
    {
        try
        {
            var envelope = new EventEnvelope
            {
                Event = eventName,
                Timestamp = TimestampFormatter.UtcIso8601(),
                Server = GameQueries.ReadHostname(_logger),
                Map = SafeMap(),
                Data = data
            };

            _webSockets.EnqueueBroadcast(envelope);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue broadcast for {Event}", eventName);
        }
    }

    private string SafeMap()
    {
        try
        {
            return NativeAPI.GetMapName();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NativeAPI.GetMapName failed");
            return "";
        }
    }

    private static string FormatWinner(int winner) =>
        winner switch
        {
            (int)CsTeam.CounterTerrorist => "CT",
            (int)CsTeam.Terrorist => "T",
            (int)CsTeam.Spectator => "Spectator",
            _ => winner.ToString(CultureInfo.InvariantCulture)
        };

    private static string FormatSteamId(ulong xuid, string? networkId)
    {
        if (xuid > 0UL)
            return xuid.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(networkId))
            return networkId.Trim('[', ']');

        return "";
    }

    private static class PlayerSnapshots
    {
        public static CCSPlayerController? TryGetController(CCSPlayerController? controller)
        {
            return controller is { IsValid: true } ? controller : null;
        }

        public static PlayerInfo? FromController(CCSPlayerController? controller)
        {
            var resolved = TryGetController(controller);
            if (resolved == null)
                return null;

            var steam = resolved.AuthorizedSteamID?.SteamId64.ToString(CultureInfo.InvariantCulture) ?? "";
            return new PlayerInfo
            {
                SteamId = steam,
                Name = resolved.PlayerName,
                Team = resolved.TeamNum,
                Ip = resolved.IpAddress
            };
        }

        public static PlayerInfo FromRequiredController(CCSPlayerController controller)
        {
            var steam = controller.AuthorizedSteamID?.SteamId64.ToString(CultureInfo.InvariantCulture) ?? "";
            return new PlayerInfo
            {
                SteamId = steam,
                Name = controller.PlayerName,
                Team = controller.TeamNum,
                Ip = controller.IpAddress
            };
        }
    }

    private static class BombSiteFormatter
    {
        public static string Format(int site) =>
            site switch
            {
                0 => "A",
                1 => "B",
                _ => $"site_{site.ToString(CultureInfo.InvariantCulture)}"
            };
    }

    private static class TimestampFormatter
    {
        public static string UtcIso8601() =>
            DateTimeOffset.UtcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Provides defensive helpers for reading lightweight server snapshots from the CounterStrikeSharp main thread.
/// </summary>
public static class GameQueries
{
    /// <summary>
    /// Reads the public hostname (<c>hostname</c> ConVar).
    /// </summary>
    /// <param name="logger">Logger used when ConVar lookup fails.</param>
    /// <returns>A non-empty hostname suitable for outbound JSON payloads.</returns>
    public static string ReadHostname(ILogger logger)
    {
        try
        {
            var cv = ConVar.Find("hostname");
            return string.IsNullOrWhiteSpace(cv?.StringValue) ? "Counter-Strike 2 Server" : cv!.StringValue;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "hostname lookup failed");
            return "Counter-Strike 2 Server";
        }
    }

    /// <summary>
    /// Attempts to aggregate CT/T scores by scanning active <see cref="CCSTeam"/> entities.
    /// </summary>
    public static bool TryReadScores(out int ctScore, out int tScore)
    {
        ctScore = 0;
        tScore = 0;

        try
        {
            var found = false;

            for (var i = 0; i < Utilities.MaxEdicts; i++)
            {
                var team = Utilities.GetEntityFromIndex<CCSTeam>(i);
                if (team == null || !team.IsValid)
                    continue;

                found = true;
                var total = team.ScoreFirstHalf + team.ScoreSecondHalf + team.ScoreOvertime;

                switch ((CsTeam)team.TeamNum)
                {
                    case CsTeam.CounterTerrorist:
                        ctScore = total;
                        break;
                    case CsTeam.Terrorist:
                        tScore = total;
                        break;
                }
            }

            return found;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to read <c>m_iTotalRoundsPlayed</c> from <see cref="CCSGameRules"/>.
    /// </summary>
    public static bool TryReadRoundNumber(out int roundNumber)
    {
        roundNumber = 0;
        try
        {
            var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
            if (proxy == null || !proxy.IsValid)
                return false;

            var rules = proxy.GameRules;
            if (rules == null)
                return false;

            roundNumber = rules.ITotalRoundsPlayed;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
