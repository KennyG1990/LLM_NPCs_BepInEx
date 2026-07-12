# HANDOFF — AI Influence for Going Medieval (LLM_NPCs)
_Last updated: 2026-07-11 ~18:10 by Claude Code. Successor: pick up EXACTLY here._

## ⚡ IN-FLIGHT STATE (do this first)
- **ALL DEPLOYED + LIVE**: EventInteractor v3 (field-aware extraction + honest OnClose apply) and #35 budget lanes are in the running DLL, validated on Dowsby (see BACKLOG 2026-07-11 ~18:05 ledger). Nothing unbuilt on disk.
- **WATCH**: #34 positive path — needs a natural blocking CHOICE event in Dowsby (`ShowDialogPhaseBranching`). Expect telemetry `deciding… → CHOSE n:'…'` and mod-log `apply: ShowDialogPhaseBranching.OnClose(n) invoked`. Info-only/news events (e.g. trader visit, phase `TraderVisitPhase`) correctly end at `NEEDS PLAYER (no dialog phase awaiting an answer)` — that wording is queued for a soften ("no answer needed").
- **LAUNCH FIXED + VALIDATED**: doorstop root cause was env inheritance (game-spawned dashboard → `DOORSTOP_*` in child env → Doorstop skips BepInEx). `gm_devops._launch` now strips them; dashboard restarted; `POST /api/dev/game/launch` INJECTS again (validated 18:24, `mod_20260711_182416.log`). Normal API dev loop restored. STILL verify a fresh `mod_*.log` in `C:\Users\Moshi\AppData\LocalLow\Foxy Voxel\Going Medieval\LLM_NPCs\logs` after every launch (cheap, catches everything).
- **WATCH — WeaponChain (#37 slice, deployed 18:24)**: `weapons:` telemetry line owns the ranged-weapon pipeline (station → order → ProductionState → skill gate via the game's `HasSkillsRequired` → Crafting-prio boost or named unmet skill). Current honest state: fletcher blueprint placed but NOT constructed → "no constructed station hosts a ranged recipe". Expect: fletcher built → `sling@fletchers_table=<state>` → boost/skill report. If the window vanishes (`MainWindowTitle` empty, /api/game/* 404s): restore via Win32 ShowWindow by PID (see BACKLOG 18:30 addendum).
- **LIVE COLONY**: "Dowsby" (A New Life, valley, river; settlers Linyeve Green, Giles Becker, Alric Sollers/Smithing-19). Fresh validation bed. Two previous colonies (Cockhamsted, Llangefni) died — their autopsies produced most of the current systems.
- **ENV NOTE (Claude Code)**: PowerShell reaches localhost:8714 DIRECTLY — no Chrome relay for dashboard APIs (that gotcha is Cowork-specific). Game screenshots still via a browser tab on `/api/game/screen`.

## THE VISION (Ken's words)
LLM-as-player: "the engine asks the LLM WHERE/WHAT/WHY/WHEN/HOW" for immediate + long-term planning. End state: "leave it running for a day, come back to a village arguing about religion and politics, then they split into two villages and go to war." The 12 reference docs (00–11, in the mounted folder `AI Influence - Systems - Going Medieval` and re-uploaded copies) are the finished-mod spec. Lineages (children take father's surname), underground cellars, TTS voices — all planned.

## HARD RULES (Ken-enforced, violations have cost colonies)
1. **WORKFLOW**: PLAN → RECONCILE → DOCUMENT → IMPLEMENT → VALIDATE → REVIEW → DOCUMENT → AAR. Non-skippable. AAR into BACKLOG.md.
2. **Machine checks prove STATE; anything a user sees/feels needs EYES ON SCREEN** (screenshot + judge like a player). Also prove the NEGATIVE path.
3. **NEVER act on guessed ids/names** — enum members via Enum.GetNames substring match, ids from live repository dumps. (HourType bug, zoner v2 no-op, both from guessing.)
4. **"invoked" ≠ "placed" ≠ "built" ≠ "seen"** — count before/after, only persist verified success (roof counter lie closed a roofless house for weeks).
5. **Every alert must have a reaction owner** — warnings that only decorate telemetry kill colonies (Llangefni starved next to its own FOOD IS SCARCE alert).
6. **Survival constraints beat all other constraints** — peacetime bounds (caps, leash, quotas) must yield in crisis.
7. **Coherence test**: "imagine you are the NPC — what would you WANT?" Judge outcomes, not mechanics.

## ARCHITECTURE (two layers + memory)
- **Deterministic strategic layer** (C#, ticks ~13s in ColonyBuilder.OnTick): census → alerts → CRISIS check → home/site/move-in coherence → one build action per tick by priority (storage→cook→beds→house→research table→fletcher→farm...) → module ticks (research/production/farm/jobs/equip/schedule/zoner/events/deaths) → WriteStatus telemetry.
- **LLM layer** (Player2 daemon :4315 or OpenRouter, per-task model routing `LLMClient.TaskModels`): NPC dialogue (real-time), leader site choice (`HouseSitePlanner`, task "planner"), story-event decisions (`EventInteractor`, task "story_event"), death chronicles (`DeathChronicler`, task "death_history"). Budget: `LLMClient.TrySpendBudget`, 8/hr sliding window (needs lanes, #35).
- **Memory**: Python dashboard (`dashboard/dashboard_server.py`, http://127.0.0.1:8714) + SQLite (25+ tables) + RoleRAG (`dashboard/gm_rolerag.py`, graph-guided boundary-aware retrieval, section 0 of /api/memory/context). PLANNED (#33, decided): migrate to mod-local JSON folder-per-save + C# RoleRAG port, dual-write, dashboard demoted to dev mirror. **Release blocker for standalone.**

## DIRECTORIES
- `src/` — all mod C# (BepInEx plugin, net472). Key files: Plugin.cs (Awake/MainLoop/config binds — NEVER read config .Value before Bind), ColonyBuilder.cs (the strategic brain), StockpilePlacer.cs (all placement + reflection helpers RepoInstance/FindTypeByName/MakeVec3Int/BuildingExistsAt), HouseBuilder, HouseSitePlanner+SiteScorer (leash MaxLeash=35), ColonyHome (MoveTo = coherence), FoodGatherer (Crisis flag), JobRouter (skill routing + CrisisRouteAll), EquipManager (honest weapon census v2), StockpileZoner (v3 data-driven pantry/materials/refuse), EventInteractor (#34), DeathChronicler (#27), BuiltState (per-save sidecars `validation/built_state/{save}.txt` + world-truth re-detection — the save-bloat fix), GameBridge (identity, GoapAgent via DeclaredOnly hierarchy-walk).
- `dashboard/` — Python server + modules (gm_rolerag, gm_plans, gm_colony, gm_devops). Start via `start_dashboard.bat` (Plugin auto-spawns it if health probe fails).
- `validation/` — colony_status.txt (THE telemetry, rewritten every tick), decompiled/ (ilspycmd output), built_state/, chronicles/, resource_taxonomy.txt, event_api.txt, filter_groups.txt, production_ids.txt, player2_dev_api.md.
- `BACKLOG.md` — session ledgers, AARs, specs. READ THE LAST ~300 LINES FIRST.
- `ROADMAP.md` — the SCALING ROADMAP (S1 pop-20/8h · S2 multi-village splits · S3 height maps — Ken 2026-07-12) + append-only verified history. Closing a milestone MOVES it here with cited validation.
- Reference docs: `C:\Users\Moshi\Desktop\X4 AI Influence\AI Influence - Systems - Going Medieval\00..11*.md`.
- Game: `E:\SteamLibrary\steamapps\common\Going Medieval\` (BepInEx plugins dir = deploy target).

## DEV LOOP (dashboard API, drive via in-page fetch from Claude-in-Chrome — sandbox can't reach localhost)
1. Build only: `POST /api/dev/build {deploy:false}` → check `ok` + `errors`.
2. **Deploy protocol (stale-DLL gotcha, bit us TWICE; save protocol replaced 2026-07-11)**: (a) SAVE programmatically — write `validation/save_request.txt`, wait until `VillageSaves\<colony>` folder mtime ADVANCES (SaveGuard consumes the flag → game's own AutosaveCurrentVillage; NEVER use ESC-menu clicks — recap screens/open panels eat them silently). (b) kill (`POST /api/dev/game/kill`) → poll `/api/dev/status` until `game_running=false` for 5 consecutive seconds → `POST /api/dev/build {deploy:true}` → verify `dll_in_sync` AND `deployed_dll.mtime` fresh (<120s) → launch → verify fresh `mod_*.log` → RESUME click → wait `census: pop>0` → story-continue click → VERIFY the recap actually dismissed (it intercepts every resume and eats input). If new telemetry lines are missing after relaunch, the RUNNING process has an old DLL in memory — kill and relaunch.
3. Game UI: `POST /api/game/input` {action: click {x,y rel} | keypress {key} | text}. RESUME at main menu ≈ (0.857, 0.330); story-continue ≈ (0.498, 0.955) — **but see #34: STOP blind-clicking story dialogs**; speed-3 = key "3". Screenshot: navigate a Chrome tab to `/api/game/screen?force_focus=true&t=<nonce>` and screenshot the TAB (endpoint returns raw JPEG; don't fetch as JSON).
4. Telemetry: Read `validation/colony_status.txt` (check `time:` freshness — a stale file means the mod isn't ticking!). Logs: `GET /api/dev/log?lines=N` → {logs: {mod, player, bepinex}} (mod log timestamps are UTC).
5. Decompile ground truth: `POST /api/dev/decompile {types:[fullTypeName,...]}` → `validation/decompiled/`. Nested types use `+`.
6. Saves: per-colony folders; sidecars keyed by save NAME. Save in-game: ESC → SAVE (≈0.840,0.482) → Overwrite (≈0.577,0.326) → VERIFY the timestamp changed. Editing a sidecar requires doing it BEFORE the save loads (adoption reads it once).

## ENVIRONMENT GOTCHAS
- **Bash mount is STALE/TRUNCATES** — use Read/Grep/Edit tools (Windows-side) for source files; bash only for the mounted validation/decompiled greps (verify freshness) and sleeps.
- Chrome content filter can [BLOCK] responses — return sanitized counts/booleans from in-page fetch.
- CDP calls time out at 45s — keep in-page scripts under that; they continue running after timeout.
- .NET string literals are UTF-16: `strings -el` to grep DLLs.
- Screenshots may capture Ken's desktop — never force_focus while he's actively using the machine.
- BepInEx injection failed once after a mid-day relaunch (no mod log, no toolbar, no plugin lines in player.log) — resolved when Ken launched via Steam himself. If the LLM toolbar is missing at the main menu, the plugin is NOT loaded; check mod log freshness before debugging "bugs".

## GAME-API GROUND TRUTH (hard-won, do not re-derive)
- **THE API INDEX (built 2026-07-11): `F:\DEV_ENV\projects\Mods\Going Medieval\GameApiIndex\`** — the ENTIRE game decompiled (3,558 files under `src\`) + flat signatures index `api_index.txt` (54,670 lines, `Namespace.Type :: kind :: signature`; field-vs-property is first-class). Grep the index for existence/signatures FIRST, open src\ for behavior; `/api/dev/decompile` only to freshness-check a type. `INDEX_META.txt` has the DLL sha — REGENERATE after every game patch. Never guess type names or byte-scan the DLL again.
- **HOW_THINGS_WORK.md (same folder — Ken's standing directive)**: the intent layer for future mod makers — how the API actually behaves, with preconditions/gotchas and live-validation dates. MAINTENANCE IS PART OF THE WORKFLOW: every newly ground-truthed mechanism gets a dated entry there during the DOCUMENT step, then SYNC TO THE STARFORGE VAULT: `Copy-Item "F:\DEV_ENV\projects\Mods\Going Medieval\GameApiIndex\HOW_THINGS_WORK.md" "F:\StarForge\wiki\going-medieval\how-things-work.md" -Force` (linked in that wiki's _index). Read it BEFORE touching an unfamiliar game system.
- Roofs: place via `BuildingPlacementManager.SpawnRoofAutoTesting` with `Autoconstruct=true` save/restore; `CreateRoofs` collapses the check to gridPos ONLY; `CanPlaceRoof` demands support at y-1 (Wall|Voxel|Beam|Window|Door|BarnDoor), no blocker at cell (`~(Default|Floor|Rug)`); unbuilt blueprints block placement — kick them with `BaseBuildingInstance.AutoConstructSequence()` (see StockpilePlacer.KickRoofRow).
- Events (#34): `GameEventSystem` (MonoSingleton).RunningEvents / IsBlockingEventRunning / StartEvent(id); `GameEventUtil.BuildDialogContent(instance, dialogIndex)` → DialogContent — **its members are public FIELDS, not properties** (WindowTitle, ContentTitle, ContentBodyText, Options[].Text) — reflect with field-or-property (`EventInteractor.Member`). ANSWER path (decompile-verified 2026-07-11): `GameEventSystemController.EventOptionChosen(instance,int)` is a NOTIFICATION RELAY ONLY (int = dialogShowingIndex, NOT the option) — the REAL write is `instance.stateMachine.currentPhase.OnClose(selectedOptionIndex)` (sets `switchPhaseIndexNextTick`; next Tick advances `choiceDestinationPhases[chosen]`). Choice dialogs = phase `ShowDialogPhaseBranching`; phases without `OnClose(int)` (e.g. `TraderVisitPhase`) have nothing to answer. Positive path on a live choice event still pending.
- Filters/zones: `StockpileInstance.ResourcesFilter` — ids are blueprint strings; `RemoveAllowedResource(string)` / `AddAllowedResource(string)` / `IsBlueprintAllowed(string)` for spot-check verification. Resource taxonomy from ResourceRepository: `Category` (Ctg* flags) + `GroupIdentifier` (aggregate ALL repo fields, dedupe — first field is a weapons-only subcache).
- Y-convention: grid LEVEL y for placement APIs; instances often store WORLD y = level×MapBlockHeight(3). Check both (CountVerifiedStockpiles pattern).
- Jobs: `WorkerGoapAgent.ChangeJobPriority(JobType, 1..4)`; JobType flags: Hauling=1 Mining=2 Construction=4 Crafting=8 Harvesting=0x10 Hunting=0x20 PlantCropfields=0x40 Cooking=0x400 Research=0x1000 Animal=0x10000 Art=0x20000 Fishing=0x100000. Skills live on HumanoidInstance model, NOT WorkerView.
- Death: `CreatureBase.HasDied` (:302); DeathChronicler uses roster-diff (3-tick confirm).
- Research: `ResearchController.Activate(node, false)` is the LEGAL path (enforces+allocates resources).
- Nutrition: `ResourcePileTracker.GetTotalStockpilePilesNutrition()` counts STOCKPILED food only (new colony reads 0 until first zone).

## OPEN TASKS (board state; numbers = task tracker)
- **#34 EventInteractor** (in progress): ReadEvent v2 on disk unbuilt; then LIVE positive-path validation (an event answered by the leader, dialog closes). NEVER blind-click story dialogs from the driving loop.
- **#35 Budget lanes** (next): reserve calls for story_event/planner/death_history/siteplan in TrySpendBudget.
- **#37 Crisis reactor** (deployed, positive path unvalidated): expect "⚠CRISIS(food)" census prefix + caps ignored + all-hands food jobs when nutrition < pop*6. Remaining slices: storage scaling by fill-%, roofed stockpiles/pantry indoors, weapons fallback chain when fletcher is blocked (NO-SKILLED-WORKER contributed to both wipes), seasonal farm replant, animal husbandry as food strategy (JobType Animal already prio-1 in crisis).
- **#27 DeathChronicler** (deployed, awaiting a death): verify chronicle file + survivor memories + colony event when it happens.
- **#32 Coherence remainder**: orphan-blueprint cleanup (cancel blueprints beyond WorkRadius; one orphan campfire at (130,100) in Llangefni-world), old-camp goods migration.
- **#17 THE PLANNER** (the big one): LLM writes immediate+long-term plans ("winter is coming"), deterministic validator/executor consumes a plan queue, event-driven replans, gm_plans persistence (already live server-side).
- **#31 PACKER**: LLM room program → multi-room floor plan on SiteScorer pad (~110 interior tiles, corridor spine; design in BACKLOG). Replaces the 4x7 2-room shack.
- **#33 Memory independence**: JSON folder-per-save + C# RoleRAG + dual-write. Standalone release blocker.
- **#36** dead-colony dashboard state; **#20** ScheduleRouter v2 (chronotypes); **#8** cellar dig (needs hilly map or stairs); **#15/#19/#21/#12/#24/#25/#26/#30/#23** as per board; **#28 TTS** (`/v1/tts/speak` on Player2).
- Docs campaign: systematically satisfy reference docs 00–11 (02 world events → after #34; 03 diplomacy needs a faction layer design; 04 disease; 06 romance/lineages; 07 combat direction — Raid delegates already mapped).

## VALIDATION CHECKLIST (per feature)
telemetry line proves state → negative path proven → screenshot proves the player-visible outcome → save → reload → telemetry/census unchanged (idempotent) → AAR in BACKLOG.md → task board updated honestly (✅ only when FULLY validated; ◐ otherwise).

## COST DISCIPLINE
1,792 calls/$222 happened once. Governor 8/hr + per-task models (cheap models for planner/summary tasks, real-time only for NPC chat). Dialogue batching/1-call-conversations still open (#23). Player2 joules were exhausted once — OpenRouter is the configured backup (key in game config, models cached with pricing).
