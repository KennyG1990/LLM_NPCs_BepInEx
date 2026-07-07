# Quick Start - Going Medieval LLM NPCs

Game root: `E:\SteamLibrary\steamapps\common\Going Medieval`

## Agent iteration loop (no desktop automation needed)

One-time human step: start the dashboard by double-clicking
`start_dashboard.bat` (repo root) or running `python dashboard\dashboard_server.py`.
It binds `127.0.0.1:8714` and hot-restarts itself whenever watched project
files change, so it only ever needs to be started once per boot.

Everything else is HTTP against `http://localhost:8714`:

- `GET  /api/dev/status` — game running? built vs deployed DLL hash sync
- `POST /api/dev/build` — `dotnet build -c Release -t:Rebuild` + deploy DLL to BepInEx plugins (`{"deploy":true}` default; refuses to deploy while game runs)
- `POST /api/dev/game/launch` — start Going Medieval (`{"via":"steam"}` optional)
- `POST /api/dev/game/kill` — stop the game (graceful, then forced)
- `POST /api/dev/game/restart` — kill + relaunch (use after redeploying the DLL)
- `GET  /api/game/screen` — JPEG frame of the game window (`?force_focus=true` to raise it)
- `POST /api/game/input` — `{"action":"click","x":0.5,"y":0.5}` relative coords, `{"action":"text",...}`, `{"action":"keypress","key":"enter"}`

Standard C# iteration: edit → `POST /api/dev/game/kill` → `POST /api/dev/build`
→ `POST /api/dev/game/launch` → drive/verify via `/api/game/screen` + `/api/game/input`.
Python/dashboard iteration: just edit; the file watcher restarts the server.

## Runtime Pieces

- `LLM_NPCs.dll`: BepInEx plugin loaded by Going Medieval.
- `dashboard/dashboard_server.py`: local backend, SQLite owner, RoleRAG provider, web dashboard, and game stream/control server.
- Player2: local reasoning backend discovered from `%APPDATA%\game.player2.client\api.port`, normally `127.0.0.1:4315`.

The C# plugin no longer requires an OpenRouter API key or native SQLite plugin DLLs.

## Build

```batch
cd /d F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx
dotnet restore
dotnet build --configuration Release
```

Output:

```text
bin\Release\net472\LLM_NPCs.dll
```

## Deploy

Close Going Medieval before copying the DLL. Windows keeps loaded BepInEx assemblies memory-mapped while the game is running.

```batch
deploy.bat
```

Manual equivalent:

```batch
copy /Y bin\Release\net472\LLM_NPCs.dll "E:\SteamLibrary\steamapps\common\Going Medieval\BepInEx\plugins\LLM_NPCs.dll"
copy /Y bin\Release\net472\Newtonsoft.Json.dll "E:\SteamLibrary\steamapps\common\Going Medieval\BepInEx\plugins\Newtonsoft.Json.dll"
```

## Start Backend

```batch
cd /d F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx
python dashboard\dashboard_server.py
```

Open:

```text
http://127.0.0.1:8714
```

Useful checks:

```powershell
Invoke-RestMethod http://127.0.0.1:8714/health
Invoke-RestMethod http://127.0.0.1:8714/api/memory/saves
```

The backend has a dev file watcher enabled by default. It restarts `dashboard_server.py` after edits to watched source files. Disable with:

```powershell
$env:GM_DASHBOARD_WATCH = "0"
```

## Expected Working Evidence

- `/health` returns `ok=true`, `database_exists=true`, and `player2.online=true`.
- `/api/game/screen` returns a JPEG of the Going Medieval window.
- `/api/game/input` can send clicks and keypresses to the game.
- BepInEx log contains `Loading [LLM NPCs ...]`.
- Mod log contains `Found ... validated settlers` and `Stage 2 validated action: ...`.

## Important Paths

| Artifact | Path |
| --- | --- |
| Dashboard | `http://127.0.0.1:8714` |
| Source DLL | `F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\bin\Release\net472\LLM_NPCs.dll` |
| Deployed DLL | `E:\SteamLibrary\steamapps\common\Going Medieval\BepInEx\plugins\LLM_NPCs.dll` |
| BepInEx log | `E:\SteamLibrary\steamapps\common\Going Medieval\BepInEx\LogOutput.log` |
| Mod logs | `%USERPROFILE%\AppData\LocalLow\Foxy Voxel\Going Medieval\LLM_NPCs\logs\` |
| SQLite DB | `%APPDATA%\Going Medieval\LLM_NPCs\memory\npc_memory.sqlite3` |

