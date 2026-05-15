// CS2Broadcast - by 1MP4C7 | ImpactGuard Systems

using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using CS2Broadcast.Config;
using CS2Broadcast.Models;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CS2Broadcast;

/// <summary>
/// Primary CounterStrikeSharp plugin that wires gameplay hooks, networking listeners, and cached REST snapshots together.
/// </summary>
[MinimumApiVersion(247)]
public sealed class CS2BroadcastPlugin : BasePlugin, IPluginConfig<BroadcastConfig>
{
    private readonly object _snapshotLock = new();
    private readonly ConcurrentQueue<Action> _mainThreadJobs = new();

    private WebSocketServer? _webSockets;
    private HttpApiServer? _httpApi;
    private EventBroadcaster? _broadcaster;
    private Timer? _snapshotTimer;
    private BroadcastConfigAccessor? _configAccessor;

    private ServerStateResponse _cachedState = new()
    {
        Map = "",
        Hostname = "Counter-Strike 2 Server",
        PlayerCount = 0,
        CtScore = 0,
        TScore = 0,
        RoundNumber = 0
    };

    private IReadOnlyList<PlayersApiEntry> _cachedPlayers = Array.Empty<PlayersApiEntry>();
    private ScoresResponse _cachedScores = new() { CtScore = 0, TScore = 0 };

    /// <inheritdoc />
    public override string ModuleName => "CS2Broadcast";

    /// <inheritdoc />
    public override string ModuleAuthor => "1MP4C7 @ ImpactGuard Systems";

    /// <inheritdoc />
    public override string ModuleDescription =>
        "Real-time WebSocket + HTTP REST bridge that exposes CS2 server events to external applications.";

    /// <inheritdoc />
    public override string ModuleVersion => "1.0.0";

    /// <inheritdoc />
    public BroadcastConfig Config { get; set; } = new();

    /// <inheritdoc />
    public override void Load(bool hotReload)
    {
        try
        {
            _configAccessor = new BroadcastConfigAccessor(() => Config);

            _webSockets = new WebSocketServer(Logger, _configAccessor);
            _httpApi = BuildHttpApi();

            _webSockets.Start();
            _httpApi.Start();

            _broadcaster = new EventBroadcaster(this, _webSockets, Logger);

            RegisterListener<Listeners.OnTick>(() =>
            {
                while (_mainThreadJobs.TryDequeue(out var job))
                {
                    try
                    {
                        job();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Main-thread CS2Broadcast job faulted");
                    }
                }
            });

            _snapshotTimer = AddTimer(0.25f, RefreshSnapshots, TimerFlags.REPEAT);

            Logger.LogInformation("CS2Broadcast loaded (WS {WsPort}, HTTP {HttpPort})",
                Config.WsPort,
                Config.HttpPort);
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "CS2Broadcast failed during Load");
        }
    }

    /// <inheritdoc />
    public override void Unload(bool hotReload)
    {
        try
        {
            _snapshotTimer?.Kill();
            _snapshotTimer = null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Snapshot timer shutdown failed");
        }

        try
        {
            _broadcaster?.EmitShutdownSignal();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unable to broadcast shutdown envelope");
        }

        try
        {
            _webSockets?.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "WebSocket shutdown failed");
        }

        try
        {
            _httpApi?.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "REST shutdown failed");
        }

        _broadcaster?.Dispose();

        Logger.LogInformation("CS2Broadcast unloaded");
    }

    /// <inheritdoc />
    public void OnConfigParsed(BroadcastConfig config)
    {
        Config = config;

        if (string.IsNullOrWhiteSpace(Config.Meta))
        {
            Config.Meta = "CS2Broadcast - by 1MP4C7 | ImpactGuard Systems";
        }
    }

    private HttpApiServer BuildHttpApi()
    {
        if (_configAccessor == null)
            throw new InvalidOperationException("Config accessor must be initialized first.");

        return new HttpApiServer(
            Logger,
            _configAccessor,
            GetStateSnapshot,
            GetPlayersSnapshot,
            GetScoresSnapshot,
            KickFromApi,
            SayFromApi);
    }

    private void EnqueueMainThread(Action action)
    {
        _mainThreadJobs.Enqueue(action);
    }

    private void RefreshSnapshots()
    {
        try
        {
            var map = SafeMapName();
            var hostname = GameQueries.ReadHostname(Logger);
            GameQueries.TryReadScores(out var ct, out var t);
            var round = GameQueries.TryReadRoundNumber(out var rn) ? rn : 0;

            var players = Utilities.GetPlayers()
                .Where(p => p.IsValid)
                .Select(ToPlayersApiEntry)
                .ToList();

            var state = new ServerStateResponse
            {
                Map = map,
                Hostname = hostname,
                PlayerCount = players.Count,
                CtScore = ct,
                TScore = t,
                RoundNumber = round
            };

            var scores = new ScoresResponse { CtScore = ct, TScore = t };

            lock (_snapshotLock)
            {
                _cachedState = state;
                _cachedPlayers = players;
                _cachedScores = scores;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Snapshot refresh failed");
        }
    }

    private ServerStateResponse GetStateSnapshot()
    {
        lock (_snapshotLock)
            return _cachedState;
    }

    private IReadOnlyList<PlayersApiEntry> GetPlayersSnapshot()
    {
        lock (_snapshotLock)
            return _cachedPlayers.ToArray();
    }

    private ScoresResponse GetScoresSnapshot()
    {
        lock (_snapshotLock)
            return _cachedScores;
    }

    private SayApiResult SayFromApi(string message)
    {
        var trimmed = message.Trim();
        if (trimmed.Length == 0)
            return new SayApiResult(false, "message_empty");

        if (trimmed.Length > 512)
            trimmed = trimmed[..512];

        var tcs = new TaskCompletionSource<SayApiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = trimmed;

        EnqueueMainThread(() =>
        {
            try
            {
                foreach (var player in Utilities.GetPlayers())
                {
                    if (!player.IsValid)
                        continue;

                    player.PrintToChat(payload);
                }

                tcs.TrySetResult(new SayApiResult(true, ""));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to broadcast server say");
                tcs.TrySetResult(new SayApiResult(false, "exception"));
            }
        });

        return tcs.Task.Wait(TimeSpan.FromSeconds(3))
            ? tcs.Task.Result
            : new SayApiResult(false, "timeout");
    }

    private KickApiResult KickFromApi(string steamId, string? reason)
    {
        if (!ulong.TryParse(steamId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var steam64))
            return new KickApiResult(false, "invalid_steamid");

        var tcs = new TaskCompletionSource<KickApiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var kickReason = string.IsNullOrWhiteSpace(reason) ? "Kicked via CS2Broadcast API" : reason.Trim();

        EnqueueMainThread(() =>
        {
            try
            {
                var target = Utilities.GetPlayerFromSteamId64(steam64);
                if (target == null || !target.IsValid)
                {
                    tcs.TrySetResult(new KickApiResult(false, "player_not_found"));
                    return;
                }

                try
                {
                    target.PrintToCenter($"You were removed: {kickReason}");
                }
                catch
                {
                    // Best-effort feedback; ignore failures.
                }

                try
                {
                    target.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
                    tcs.TrySetResult(new KickApiResult(true, ""));
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Disconnect API failed; issuing console kick command");
                    Server.ExecuteCommand($"kick {target.UserId}");
                    tcs.TrySetResult(new KickApiResult(true, ""));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Kick pipeline failed");
                tcs.TrySetResult(new KickApiResult(false, "exception"));
            }
        });

        return tcs.Task.Wait(TimeSpan.FromSeconds(3))
            ? tcs.Task.Result
            : new KickApiResult(false, "timeout");
    }

    private static string SafeMapName()
    {
        try
        {
            return NativeAPI.GetMapName();
        }
        catch
        {
            return "";
        }
    }

    private static PlayersApiEntry ToPlayersApiEntry(CCSPlayerController player)
    {
        var steam = player.AuthorizedSteamID?.SteamId64.ToString(CultureInfo.InvariantCulture) ?? "";
        var ping = ReadPing(player);

        return new PlayersApiEntry
        {
            SteamId = steam,
            Name = player.PlayerName,
            Ping = ping,
            Score = player.Score,
            Team = player.TeamNum,
            Alive = IsAlive(player)
        };
    }

    private static uint ReadPing(CCSPlayerController player)
    {
        try
        {
            return player.Ping;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsAlive(CCSPlayerController player)
    {
        try
        {
            var pawn = player.PlayerPawn.Value;
            return pawn is { IsValid: true, Health: > 0 };
        }
        catch
        {
            return false;
        }
    }
}
