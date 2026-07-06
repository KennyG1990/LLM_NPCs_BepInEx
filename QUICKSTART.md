# Quick Start - Going Medieval LLM NPCs

Game root: `E:\SteamLibrary\steamapps\common\Going Medieval`

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

