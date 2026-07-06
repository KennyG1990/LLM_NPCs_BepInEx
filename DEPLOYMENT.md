# Deployment Guide - Going Medieval LLM NPCs

This mod uses a split architecture:

- Going Medieval + BepInEx loads `LLM_NPCs.dll`.
- The C# plugin acts as a sensor/actuator layer.
- The Python dashboard owns SQLite persistence, RoleRAG context, incident APIs, browser dashboard, and remote game stream/control.
- Player2 owns bounded settler reasoning through the local NPC API.

No OpenRouter API key is required. No `System.Data.SQLite.dll` or `SQLite.Interop.dll` should be deployed into `BepInEx\plugins`.

## Prerequisites

- Going Medieval Steam install.
- BepInEx 5.x for Unity Mono.
- .NET SDK capable of building `net472`.
- Python 3.11+ or 3.12 with the dashboard dependencies installed.
- Player2 running locally.

## Build

```batch
cd /d F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx
dotnet build --configuration Release
```

Expected output:

```text
bin\Release\net472\LLM_NPCs.dll
```

## Deploy DLL

Close Going Medieval first. If the game is running, copying over `BepInEx\plugins\LLM_NPCs.dll` can fail because the file is memory-mapped.

```batch
deploy.bat
```

The deploy script copies:

```text
LLM_NPCs.dll
Newtonsoft.Json.dll
```

It also removes obsolete SQLite plugin dependencies if present.

## Start Dashboard Backend

```batch
cd /d F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx
python dashboard\dashboard_server.py
```

Dashboard:

```text
http://127.0.0.1:8714
```

Health check:

```powershell
Invoke-RestMethod http://127.0.0.1:8714/health
```

Expected fields:

```text
ok=true
database_exists=true
player2.online=true
server_boot_id=<changes after backend watcher restarts>
```

## Development Watcher

`dashboard_server.py` starts a file watcher by default. Edits to backend/dashboard project files restart only the Python server, not the game.

Watched extensions:

```text
.py, .js, .css, .html, .json, .csproj
```

Excluded directories include `bin`, `obj`, `.git`, `.vs`, `__pycache__`, `logs`, and `validation`.

Disable:

```powershell
$env:GM_DASHBOARD_WATCH = "0"
```

## Verify In Game

1. Start Player2.
2. Start `dashboard_server.py`.
3. Start Going Medieval.
4. Open `http://127.0.0.1:8714`.
5. Use `/api/game/screen` or the dashboard stream button to confirm the game viewport is captured.
6. Check the latest mod log under `%USERPROFILE%\AppData\LocalLow\Foxy Voxel\Going Medieval\LLM_NPCs\logs\`.

Live Player2 decision evidence looks like:

```text
[DecisionEngine] Stage 2 validated action: eat
[Plugin:ProcessSettler] Decision received for <settler_id>: eat
[NPCRegistry:RecordDecision] Recorded eat for <settler_id>
```

## Troubleshooting

| Symptom | Likely Cause | Check |
| --- | --- | --- |
| DLL copy fails with `user-mapped section open` | Going Medieval is running and has loaded the plugin | Close/restart the game before deploying |
| `/health` reports `player2.online=false` | Player2 daemon is not running or port file is stale | Check `%APPDATA%\game.player2.client\api.port` |
| `/api/game/screen` captures the wrong window | Another Going Medieval-titled or BepInEx console window is targeted | Disable BepInEx console or focus the Unity game window |
| No live decisions | Plugin not loaded, mod disabled, or no valid settlers found | Check BepInEx log and latest mod log |
| Deterministic fallback rows appear | Player2 unavailable, command response invalid, or safety validation rejected the action | Check mod log around `Player2 returned no command`, `Stage 2 action ... failed`, and `/api/incidents` |

