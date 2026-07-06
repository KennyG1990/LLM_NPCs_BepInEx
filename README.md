# Going Medieval LLM NPCs

This is a BepInEx mod that adds an AI influence layer to Going Medieval settlers without replacing the vanilla simulation. The game keeps authority over pathfinding, jobs, needs, and world state. The mod reads settler context, asks Player2 for a bounded command, validates it, then injects an allowed action back into the game.

## Current Architecture

```text
Going Medieval
  BepInEx plugin: LLM_NPCs.dll
    - settler discovery
    - pressure scoring
    - Player2 decision requests
    - action validation/execution
    - HTTP writes to dashboard backend

Python dashboard backend: http://127.0.0.1:8714
    - SQLite memory DB
    - RoleRAG lore/context
    - incidents, pressures, relationships APIs
    - web dashboard
    - game screen capture and remote input
    - dev file watcher for backend restarts

Player2 local daemon
    - NPC spawn/chat API
    - bounded command selection
```

The C# plugin no longer owns SQLite directly. It also does not use OpenRouter for live decisions.

## Main Runtime Paths

| Component | Path |
| --- | --- |
| Source | `F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx` |
| Game | `E:\SteamLibrary\steamapps\common\Going Medieval` |
| Deployed plugin | `E:\SteamLibrary\steamapps\common\Going Medieval\BepInEx\plugins\LLM_NPCs.dll` |
| Dashboard | `http://127.0.0.1:8714` |
| SQLite DB | `%APPDATA%\Going Medieval\LLM_NPCs\memory\npc_memory.sqlite3` |
| Mod logs | `%USERPROFILE%\AppData\LocalLow\Foxy Voxel\Going Medieval\LLM_NPCs\logs\` |

## Build

```batch
cd /d F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx
dotnet build --configuration Release
```

## Deploy

Close Going Medieval before deploying. Windows keeps loaded plugin DLLs memory-mapped.

```batch
deploy.bat
```

The deploy script copies `LLM_NPCs.dll` and `Newtonsoft.Json.dll`, and removes obsolete native SQLite plugin files.

## Start Dashboard Backend

```batch
python dashboard\dashboard_server.py
```

Health:

```powershell
Invoke-RestMethod http://127.0.0.1:8714/health
```

Save list:

```powershell
Invoke-RestMethod http://127.0.0.1:8714/api/memory/saves
```

## Development Watcher

The dashboard backend restarts itself on edits to watched backend/dashboard files. This avoids manually restarting the Python server while iterating on the dashboard and backend APIs. It does not hot-reload the BepInEx DLL inside the running game.

Disable:

```powershell
$env:GM_DASHBOARD_WATCH = "0"
```

## Validation Targets

The mod is considered live-functional only when current evidence shows:

- `/health` returns `ok=true` and `player2.online=true`.
- The dashboard can capture the Going Medieval viewport through `/api/game/screen`.
- `/api/game/input` can send OS-level input to the game.
- The latest mod log reports valid settlers.
- The latest mod log reports `Stage 2 validated action: ...`.
- `/api/incidents?save_id=<save>` contains successful Player2-backed incident rows.

See `MOD_STATE_GAP_SPEC.md` for the current verified state, gaps, and evidence ledger.

## Roadmap

The AI Influence system roadmap is locked in `PLAN.md`. The next implementation slice starts with `05 - NPC Memory System.md`, followed by `01 - AI Dialogues with NPCs.md`, then `09 - AI Actions System.md`.

Intentional deferrals are tracked in `DEFERRED_BACKLOG.md` so broad systems like diplomacy, plague, settlement combat, multi-backend support, and polished UX are not confused with the next active slice.

P1 memory release-candidate evidence is tracked in `P1_MEMORY_RELEASE_CANDIDATE.md`.
