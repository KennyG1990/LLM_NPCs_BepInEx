# Going Medieval AI Influence Mod State Gap Spec

Last verified: 2026-07-05 11:47 ET

## Intended Goal State

The mod should behave like a Going Medieval version of an AI Influence layer:

- Vanilla Going Medieval keeps authority over pathfinding, jobs, needs, and simulation performance.
- The C# BepInEx plugin acts as the in-game sensor and actuator layer.
- Settlers run a 3-stage loop: pressure scoring, Player2 bounded command selection, validation/execution.
- Player2 owns live settler reasoning through its local NPC API.
- The Python dashboard at `http://127.0.0.1:8714` owns SQLite persistence, RoleRAG context, incidents, pressure/relationship APIs, dashboard visualization, and remote game stream/control.
- The dashboard is a development harness that can validate live game state without manual UI guidance.

## Verified Current State

- `dashboard/dashboard_server.py` passes `python -m py_compile`.
- Dashboard backend is live at `127.0.0.1:8714`.
- `/health` returns `ok=true`, `database_exists=true`, `player2.online=true`, Player2 port `4315`, Player2 version `0.10.66`, and backend `server_boot_id`.
- The backend file watcher is live. Watched edits changed `/health.server_boot_id` from `65caedc703f04a73be4f303dfaf1fd5f` to `263cf62a25c742e1b03b8ad98caeffe9`, then to `0ac7bb354ee744138764dc0044f23d32`, while `/health` stayed reachable.
- `/api/memory/saves` is the valid save discovery route and returns `Wolferlow` and `codex_validation`.
- `/api/saves` returns `404`; it is not the current route.
- `dotnet build --configuration Release -t:Rebuild` succeeds with `0` warnings and `0` errors.
- The built DLL at `bin\Release\net472\LLM_NPCs.dll` includes the latest command-repair, colony-adviser wiring, and complain identity fix and has size `311296` bytes.
- After the final verification rebuild, the source build output has `LastWriteTimeUtc=2026-07-05 15:41:59`; the deployed DLL remains locked by the running game with `LastWriteTimeUtc=2026-07-05 15:34:37`.
- The rebuilt source DLL and deployed game DLL are byte-equivalent by SHA-256: `EFDD96F53C9BFA968AE8788247F5C01A25165FC1F6C1E7D8C8ED8499AA11EDA2`.
- BepInEx loaded the deployed DLL with fingerprint `fileLastWriteUtc='2026-07-05T15:34:37.6418000Z'`.
- The running Going Medieval process is PID `15568`, window title `Going Medieval`.
- Dashboard stream capture works after redeploy; latest screenshot artifacts include:
  - `validation\game-screen-after-relaunch.jpg`
  - `validation\game-screen-live-after-redeploy.jpg`
  - `validation\game-screen-live-player2-repair-watch.jpg`
  - `validation\game-screen-adviser-live-map.jpg`
  - `validation\game-screen-final-live-validation.jpg`
- Latest live mod log is `mod_20260705_113501.log`.
- Live loaded save repeatedly reports `Found 3 validated settlers`.
- Post-adviser-build live loaded save had successful Player2-backed decisions for all 3 active settlers:
  - Estrild Thorne `-1019062`: command repair triggered after Player2 returned no command, then recovered to validated `eat`.
  - Alison Ridge `-1019874`: validated `eat`.
  - Godstan Neot `-1018142`: validated `eat`.
- Post-adviser-build incident DB rows for `Wolferlow` were successful and nonfallback:
  - `id=108`, settler `-1018142`, action `eat`, reasoning `My belly growls fierce; I must hunt or gather a meal.`, `success=1`.
  - `id=107`, settler `-1019874`, action `eat`, reasoning `My gut growls, I must hunt for a morsel or find the pantry.`, `success=1`.
  - `id=106`, settler `-1019062`, action `eat`, reasoning `My stomach growls, I must eat.`, `success=1`.
- Post-complain-fix live loaded save has fresh successful Player2-backed decisions with canonical settler IDs only:
  - `id=116`, settler `-1019894`, action `complain`, reasoning `Chose command complain`, `success=1`.
  - `id=115`, settler `-1018162`, action `eat`, reasoning `My stomach still growls; I must seek food.`, `success=1`.
  - `id=114`, settler `-1019082`, action `continue_job`, reasoning `Back to my duties, must keep busy.`, `success=1`.
  - `id=113`, settler `-1018162`, action `eat`, reasoning `I must find some food quickly.`, `success=1`.
  - `id=112`, settler `-1019894`, action `eat`, reasoning `I must find some food quickly; my belly growls.`, `success=1`.
  - `id=111`, settler `-1019082`, action `eat`, reasoning `I must eat at once, my belly growls like a wolf.`, `success=1`.
- The earlier duplicate complain incident bug is fixed:
  - Old bad row before the fix: `id=110`, settler `worker_female(Clone)_Estrild_Thorne`, action `complain`.
  - Current assertion after the fix: `select ... where id > 110 and settler_id not glob '-[0-9]*'` returns `[]`.
- The bounded command-repair path is proven live:
  - log: `Player2 returned no command for -1019062; requesting bounded command repair.`
  - followed by: `Stage 2 validated action: eat`.
- The colony adviser path is proven live after the final deploy:
  - log: `Recorded colony recommendation: seek_medic | Average settler health is low. Recommend medical care and resting.`
  - DB `colony_events` rows `4-9` for `Wolferlow` contain `recommendation_type='seek_medic'` and Player2-style narrative text.
- RoleRAG, relationship, save, and pressure APIs are proven live:
  - `/api/memory/context?npc_id=-1019082&save_id=Wolferlow&role=builder&query=construction%20stability&max_tokens=600` returns recent memories plus `=== ENCYCLOPEDIA CONTEXT (RoleRAG) ===` with construction stability/support beam guidance.
  - `/api/relationships?save_id=Wolferlow` returns `36` relationship rows with trust, friendship, rivalry, fear, resentment, standing, relationship type, interaction counters, and timestamps.
  - `/api/memory/npcs?save_id=Wolferlow` returns NPC rows with current pressure values.
  - `/api/memory/saves` returns `Wolferlow` and `codex_validation`.

## Differences From Intended Goal

| Area | Current State | Intended State | Gap |
| --- | --- | --- | --- |
| Player2 ownership | All 3 active settlers received Player2-backed live decisions after redeploy; one null-command response was repaired into a validated command without deterministic fallback. | Healthy Player2 should usually produce bounded commands; fallback should be exceptional and logged as recovery. | Satisfied for the tested save/session. |
| C# deployment | Latest built DLL is deployed and BepInEx log fingerprint matches it. | Latest built DLL deployed and loaded by BepInEx. | No current deployment gap. |
| Dashboard backend | Live, health-checked, Player2-connected, and watcher-validated. | Same. | No major backend blocker currently known. |
| Save discovery | Valid route is `/api/memory/saves`; `/api/saves` is absent. | Dashboard/client docs should use the valid route. | README, QUICKSTART, and DEPLOYMENT now updated; any older debug docs may still mention obsolete routes or OpenRouter-era setup. |
| RoleRAG/memory | Dashboard DB, RoleRAG, incidents, relationships, pressure APIs, adviser events, and incident persistence are functional. | Durable schema with regression coverage. | Runtime proof exists; full migration test coverage remains future hardening, not a current live blocker. |
| Remote stream/control | `/api/game/screen` returns usable game JPEGs; `/api/game/input` clicked Resume, story continue, menu/save UI, and live map controls. | Browser-visible stream and OS-level input as a reliable dev harness. | Functional in this environment; window targeting remains environment-sensitive by nature of OS-level capture. |
| Build hygiene | Full rebuild is `0` warnings, `0` errors. | Same. | No current build hygiene gap. |
| Documentation | README, QUICKSTART, DEPLOYMENT, and DEBUGGING now describe Player2/dashboard architecture. | Same for top-level docs. | No stale OpenRouter/native-SQLite setup claims found in these top-level docs, except deliberate negations and route clarifications. |

## Completed In Latest Pass

- Added a hardened dashboard/backend file watcher:
  - Watches `.py`, `.js`, `.css`, `.html`, `.json`, `.csproj`.
  - Excludes `.git`, `.vs`, `__pycache__`, `bin`, `obj`, `logs`, and `validation`.
  - Debounces editor saves.
  - Restarts the Python backend through `os.execv`.
  - Adds `server_boot_id` and `server_boot_utc` to `/health`.
- Expanded Player2 spawn command definitions to match the C# action whitelist:
  - Added `drink`, `seek_medic`, `complain`, `prioritize_construction`, and `change_clothing`.
- Added bounded Player2 command repair in `DecisionEngine`:
  - If Player2 returns `command=null`, the engine asks once for a mandatory command.
  - If that fails, it clears the Player2 NPC binding, respawns the NPC once, and retries before deterministic fallback.
- Reduced full rebuild warnings from `6` to `0` by removing trivial `async`-without-`await` compatibility stubs and dead OpenRouter-era/state fields.
- Replaced stale `README.md`, `QUICKSTART.md`, `DEPLOYMENT.md`, and `DEBUGGING.md` content that still described OpenRouter keys and native SQLite plugin DLL deployment.
- Added `tools\player2_command_parser_selftest.py` covering Player2 command string, object, array, `arguments` JSON, extra-field parameter merge, null command, and empty-array failure behavior.
- Closed/restarted Going Medieval after confirming a fresh `Wolferlow\Autosave-4.sav`, deployed the latest DLL, relaunched the game, clicked Resume/story continue through the dashboard, and validated live Player2 decisions in the loaded save.
- Wired `ColonyInfluenceEngine` into the runtime as a periodic adviser pass and validated persisted `colony_events` rows with narrative recommendations.
- Fixed duplicate complain persistence so `ExecuteComplain` no longer writes a second incident under a noncanonical GameObject-style settler ID.
- Redeployed the final DLL, relaunched Going Medieval, loaded `Wolferlow`, validated fresh decisions and adviser events after the complain identity fix, and confirmed no new noncanonical incident IDs after `id=110`.

## Current Validation Evidence

```text
python -m py_compile dashboard\dashboard_server.py
success

GET http://127.0.0.1:8714/health
ok=true
server_boot_id=0ac7bb354ee744138764dc0044f23d32
database_exists=true
player2.online=true
player2.port=4315
player2.version=0.10.66

GET http://127.0.0.1:8714/api/memory/saves
saves=["Wolferlow","codex_validation"]

dotnet build --configuration Release -t:Rebuild
Build succeeded. 0 Warning(s), 0 Error(s)

python tools\player2_command_parser_selftest.py
player2_command_parser_selftest: PASS

Deployed DLL
Source and deployed LLM_NPCs.dll both size=311296
Source LastWriteTimeUtc=2026-07-05 15:41:59 after final rebuild
Deployed LastWriteTimeUtc=2026-07-05 15:34:37 because the running game keeps the loaded DLL mapped
Source/deployed SHA256=EFDD96F53C9BFA968AE8788247F5C01A25165FC1F6C1E7D8C8ED8499AA11EDA2

BepInEx LogOutput.log
Assembly fingerprint fileLastWriteUtc='2026-07-05T15:34:37.6418000Z'

Live mod log
Found 3 validated settlers
Player2 returned no command for -1019062; requesting bounded command repair.
Recorded colony recommendation: seek_medic | Average settler health is low. Recommend medical care and resting.
Stage 2 validated action: eat
Decision received for -1019062: eat
Recorded eat for -1019062
Stage 2 validated action: eat
Decision received for -1019874: eat
Recorded eat for -1019874
Stage 2 validated action: eat
Decision received for -1018142: eat
Recorded eat for -1018142
Recorded colony recommendation: seek_medic | Average settler health is low. Recommend medical care and resting.
Player2 returned no command for -1019082; requesting bounded command repair.
Stage 2 validated action: eat
Stage 2 validated action: continue_job
Stage 2 validated action: complain

SQLite incident DB, save_id=Wolferlow
post-adviser rows id 106-108 are success=1 with nonfallback reasoning
post-complain-fix rows id 111-116 are success=1 and use canonical numeric settler IDs
new_bad_ids_after_fix=[]

SQLite colony_events DB, save_id=Wolferlow
rows id 4-9 contain seek_medic recommendations and narrative adviser text

Dashboard API probes
/api/memory/context role=builder returns RoleRAG construction/support-beam context
/api/relationships?save_id=Wolferlow returns 36 relationship rows
/api/memory/npcs?save_id=Wolferlow returns NPC rows with pressure values
/api/memory/saves returns ["Wolferlow","codex_validation"]

Dashboard/game screenshot evidence
validation\game-screen-final-live-validation.jpg
```

## Remaining Completion Gates

- Completion audit status: the requested prototype end state is satisfied for the tested save/session. Remaining items are hardening and breadth gaps, not blockers for the scoped objective:
  - More migration/regression coverage for SQLite schema evolution.
  - Broader RoleRAG/relationship in-game behavioral proof beyond API/runtime availability.
  - More action executors beyond the current bounded whitelist.
  - More robust OS/window-targeting diagnostics for dashboard stream/control on unusual desktop layouts.
