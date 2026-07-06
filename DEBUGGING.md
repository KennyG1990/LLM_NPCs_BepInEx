# Debugging Guide - Going Medieval LLM NPCs

This guide reflects the current Player2 + dashboard architecture. The C# plugin does not use OpenRouter for live decisions and does not deploy native SQLite DLLs into BepInEx.

## Fast Health Checks

```powershell
Invoke-RestMethod http://127.0.0.1:8714/health
Invoke-RestMethod http://127.0.0.1:8714/api/memory/saves
```

Expected:

```text
ok=true
database_exists=true
player2.online=true
```

Valid save discovery route:

```text
/api/memory/saves
```

`/api/saves` is not a valid route.

## Build Check

```batch
cd /d F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx
dotnet build --configuration Release -t:Rebuild
```

Expected:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

## Deployment Check

If copying `LLM_NPCs.dll` fails with:

```text
The requested operation cannot be performed on a file with a user-mapped section open.
```

Going Medieval is running and has loaded the plugin. Close or restart the game, then run:

```batch
deploy.bat
```

## Player2 Check

The dashboard discovers Player2 from:

```text
%APPDATA%\game.player2.client\api.port
```

If `/health` reports `player2.online=false`:

```powershell
Get-Content "$env:APPDATA\game.player2.client\api.port"
Invoke-RestMethod "http://127.0.0.1:<port>/v1/health"
```

## Game Stream And Control

Screen capture:

```powershell
Invoke-RestMethod http://127.0.0.1:8714/api/game/screen -OutFile validation\screen.jpg
```

If the screenshot is wrong or black:

- Confirm Going Medieval has a visible top-level window.
- Disable the BepInEx console window if it steals the Going Medieval title match.
- Check that `BepInEx\config\BepInEx.cfg` has `[Logging.Console] Enabled = false`.

## Live Decision Evidence

Current proof should come from the newest file under:

```text
%USERPROFILE%\AppData\LocalLow\Foxy Voxel\Going Medieval\LLM_NPCs\logs\
```

Useful grep:

```powershell
Select-String -Path "$env:USERPROFILE\AppData\LocalLow\Foxy Voxel\Going Medieval\LLM_NPCs\logs\mod_*.log" `
  -Pattern "Found .* validated settlers|Stage 2 validated action|Player2 returned no command|Triggering Stage 1 deterministic fallback"
```

Good live evidence:

```text
Found 3 validated settlers
Stage 2 validated action: drink
Decision received for <settler_id>: drink
Recorded drink for <settler_id>
```

Fallback evidence to investigate:

```text
Player2 returned no command
Triggering Stage 1 deterministic fallback
Stage 2 action '<action>' failed safety validation
```

## Incident DB Check

```powershell
@'
import sqlite3
path=r'C:\Users\Moshi\AppData\Roaming\Going Medieval\LLM_NPCs\memory\npc_memory.sqlite3'
conn=sqlite3.connect(path)
conn.row_factory=sqlite3.Row
for r in conn.execute("select id, save_id, settler_id, action, reasoning, success, created_at from incidents where save_id='Wolferlow' order by id desc limit 12"):
    print(dict(r))
'@ | python -
```

Successful Player2-backed rows have `success=1` and non-fallback reasoning. Deterministic fallback rows have `success=0` and should be treated as recovery evidence, not proof of Player2 ownership.

## Backend Watcher

The dashboard backend restarts itself after watched source edits. `/health.server_boot_id` changes after a successful watcher restart.

Disable watcher:

```powershell
$env:GM_DASHBOARD_WATCH = "0"
```

