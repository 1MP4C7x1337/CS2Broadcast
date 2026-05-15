# CS2Broadcast

![License](https://img.shields.io/badge/license-GPL--3.0-blue.svg)
![Target](https://img.shields.io/badge/.NET-8.0-purple.svg)
![CounterStrikeSharp](https://img.shields.io/badge/CounterStrikeSharp-API-orange.svg)

Made by [1MP4C7x1337](https://github.com/1MP4C7x1337) @ ImpactGuard Systems

**CS2Broadcast** exposes Counter-Strike 2 gameplay events over a thread-safe WebSocket fan-out plus a compact JSON REST API so companion apps, overlays, bots, and dashboards can integrate without touching SourceMod.

---

## Requirements

- **CounterStrikeSharp** runtime compatible with API **1.0.367** or newer (built/tested against the NuGet package pinned in `CS2Broadcast.csproj`).
- **.NET 8** targeting pack/SDK for compiling from source (matches CounterStrikeSharp managed plugins).
- Network/firewall rules allowing inbound connections to the configured WebSocket (`ws_port`) and REST (`http_port`) ports when accessed remotely.

## Installation

1. Download the latest packaged release (`CS2Broadcast-v1.0.0.zip`) from **[GitHub Releases](https://github.com/1MP4C7x1337/CS2Broadcast/releases/latest)** (direct asset: [`CS2Broadcast-v1.0.0.zip`](https://github.com/1MP4C7x1337/CS2Broadcast/releases/latest/download/CS2Broadcast-v1.0.0.zip)) or compile this repository locally (`dotnet build -c Release`).
2. Copy `CS2Broadcast.dll` into your CS2 server's CounterStrikeSharp plugins folder (typically `game/csgo/addons/counterstrikesharp/plugins/`).
3. Ensure required CounterStrikeSharp dependencies are already installed on the host (standard CSS deployment).
4. Drop the configuration file under `counterstrikesharp/configs/plugins/CS2Broadcast/CS2Broadcast.json` (see `examples/CS2Broadcast.json` for a starter template).
5. Load or reload the plugin (`css_plugins load CS2Broadcast` / hot reload).
6. On Windows hosts you may need an elevated URL reservation so `HttpListener` can bind wildcard prefixes:
   ```powershell
   netsh http add urlacl url=http://*:7070/ user=Everyone
   netsh http add urlacl url=http://*:7071/ user=Everyone
   ```
   Replace the ports if you customize them.

## Configuration (`counterstrikesharp/configs/plugins/CS2Broadcast/CS2Broadcast.json`)

| Field | Description |
| --- | --- |
| `ws_port` | TCP port hosting `GET /ws` upgrades (default **7070**). |
| `http_port` | TCP port serving the REST routes (default **7071**). |
| `auth_token` | Shared secret. Leave empty to disable authentication on **both** transports. |
| `log_connections` | Emits informational logs whenever WebSocket peers connect/disconnect. |
| `broadcast_chat` | Controls whether `player_chat` events are emitted at all. |
| `__meta` | Optional descriptive string so operators remember where the config originated (ignored logically). |

## WebSocket Usage

Endpoint pattern:

```
ws://<your-host>:<ws_port>/ws?token=<optional-shared-secret>
```

When `auth_token` is non-empty the query parameter **`token`** must match exactly.

### Browser / Node Example

```javascript
const ws = new WebSocket('ws://127.0.0.1:7070/ws?token=YOUR_TOKEN');

ws.onopen = () => console.log('connected');
ws.onmessage = (evt) => {
  const payload = JSON.parse(evt.data);
  console.log(payload.event, payload.data);
};
ws.onerror = (err) => console.error('socket error', err);
```

Every frame shares the envelope documented below under **JSON Event Format**.

## HTTP REST API (`http://<host>:<http_port>/`)

Unless `auth_token` is blank, send `Authorization: Bearer <token>` on **every** request.

### `GET /state`

```bash
curl -H "Authorization: Bearer YOUR_TOKEN" http://127.0.0.1:7071/state
```

Returns hostname, active map, cached scores, approximate round counter, and player population snapshot metadata.

### `GET /players`

```bash
curl -H "Authorization: Bearer YOUR_TOKEN" http://127.0.0.1:7071/players
```

Lists cached controller snapshots (`steamid`, `name`, `ping`, `score`, `team`, `alive`).

### `GET /scores`

```bash
curl -H "Authorization: Bearer YOUR_TOKEN" http://127.0.0.1:7071/scores
```

Returns `{ "ct_score": 5, "t_score": 7 }`.

### `POST /say`

```bash
curl -H "Authorization: Bearer YOUR_TOKEN" ^
     -H "Content-Type: application/json" ^
     -d "{\"message\":\"Hello from automation\"}" ^
     http://127.0.0.1:7071/say
```

Delivers the message via `PrintToChat` to each connected controller.

### `POST /kick`

```bash
curl -H "Authorization: Bearer YOUR_TOKEN" ^
     -H "Content-Type: application/json" ^
     -d "{\"steamid\":\"76561198000000000\",\"reason\":\"Reserved slot\"}" ^
     http://127.0.0.1:7071/kick
```

Attempts `Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED)` and falls back to the legacy `kick` console command when necessary.

## JSON Event Format

All WebSocket payloads follow this canonical envelope:

```json
{
  "event": "player_death",
  "timestamp": "2026-05-16T15:42:31Z",
  "server": "My CS2 Server",
  "map": "de_dust2",
  "data": {}
}
```

Timestamps are UTC ISO-8601 strings ending with `Z`.

### Supported Events & Sample Payloads

#### `player_death`

```json
{
  "event": "player_death",
  "timestamp": "2026-05-16T15:42:31Z",
  "server": "ImpactGuard Public",
  "map": "de_inferno",
  "data": {
    "killer": {
      "steamid": "76561198000000000",
      "name": "s1mple",
      "team": 3,
      "ip": "203.0.113.48"
    },
    "victim": {
      "steamid": "76561198111111111",
      "name": "botched",
      "team": 2,
      "ip": "198.51.100.10"
    },
    "weapon": "weapon_ak47",
    "headshot": true,
    "assisters": [
      {
        "steamid": "76561198222222222",
        "name": "helper",
        "team": 3,
        "ip": null
      }
    ]
  }
}
```

#### `player_connect`

```json
{
  "event": "player_connect",
  "timestamp": "2026-05-16T15:43:05Z",
  "server": "ImpactGuard Public",
  "map": "de_inferno",
  "data": {
    "steamid": "76561198000000000",
    "name": "friend",
    "ip": "192.0.2.33"
  }
}
```

*(Bots / unauthorized controllers are intentionally skipped.)*

#### `player_disconnect`

```json
{
  "event": "player_disconnect",
  "timestamp": "2026-05-16T15:43:57Z",
  "server": "ImpactGuard Public",
  "map": "de_inferno",
  "data": {
    "steamid": "76561198000000000",
    "name": "friend",
    "reason": 2
  }
}
```

#### `round_start`

```json
{
  "event": "round_start",
  "timestamp": "2026-05-16T15:44:10Z",
  "server": "ImpactGuard Public",
  "map": "de_inferno",
  "data": {
    "round_number": 12,
    "map": "de_inferno"
  }
}
```

#### `round_end`

```json
{
  "event": "round_end",
  "timestamp": "2026-05-16T15:45:02Z",
  "server": "ImpactGuard Public",
  "map": "de_inferno",
  "data": {
    "winner_team": "CT",
    "reason": 8,
    "ct_score": 8,
    "t_score": 5
  }
}
```

#### `bomb_planted`

```json
{
  "event": "bomb_planted",
  "timestamp": "2026-05-16T15:45:33Z",
  "server": "ImpactGuard Public",
  "map": "de_inferno",
  "data": {
    "player": {
      "steamid": "76561198222222222",
      "name": "entry",
      "team": 2,
      "ip": "198.51.100.77"
    },
    "site": "B"
  }
}
```

#### `bomb_defused`

```json
{
  "event": "bomb_defused",
  "timestamp": "2026-05-16T15:46:05Z",
  "server": "ImpactGuard Public",
  "map": "de_inferno",
  "data": {
    "player": {
      "steamid": "76561198333333333",
      "name": "anchor",
      "team": 3,
      "ip": null
    },
    "site": "B"
  }
}
```

#### `bomb_exploded`

```json
{
  "event": "bomb_exploded",
  "timestamp": "2026-05-16T15:46:40Z",
  "server": "ImpactGuard Public",
  "map": "de_inferno",
  "data": {
    "site": "A"
  }
}
```

#### `player_chat`

```json
{
  "event": "player_chat",
  "timestamp": "2026-05-16T15:47:11Z",
  "server": "ImpactGuard Public",
  "map": "de_inferno",
  "data": {
    "steamid": "76561198000000000",
    "name": "friend",
    "message": "!extend",
    "team_only": false
  }
}
```

#### `map_change`

```json
{
  "event": "map_change",
  "timestamp": "2026-05-16T15:48:00Z",
  "server": "ImpactGuard Public",
  "map": "de_mirage",
  "data": {
    "old_map": "de_inferno",
    "new_map": "de_mirage",
    "transition": true
  }
}
```

#### `server_shutdown`

```json
{
  "event": "server_shutdown",
  "timestamp": "2026-05-16T15:49:59Z",
  "server": "ImpactGuard Public",
  "map": "de_mirage",
  "data": {
    "graceful": true
  }
}
```

## Contributing

Issues & PRs are welcome on **[github.com/1MP4C7x1337/CS2Broadcast](https://github.com/1MP4C7x1337/CS2Broadcast)**. Please keep discussions constructive and include reproduction steps plus relevant logs (`counterstrikesharp/logs`). When hacking locally:

1. Fork / clone.
2. `dotnet build -c Release`.
3. Deploy the produced `CS2Broadcast.dll`.
4. Validate WebSocket & REST connectivity against your staging server before opening a PR.

## License

Distributed under the **GNU General Public License v3.0**. See [https://www.gnu.org/licenses/gpl-3.0.html](https://www.gnu.org/licenses/gpl-3.0.html).

---

CS2Broadcast is a project by 1MP4C7x1337 — ImpactGuard Systems
