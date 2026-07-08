# Going Medieval — LLM NPCs Mod — Handoff to Fable 5

## 0. TL;DR
A BepInEx/Harmony C# mod (net472) that makes Going Medieval settlers **LLM-driven (Player2)** and gives the colony an **autonomous build-out system**. Overarching goal (Ken's): *the villagers build their own village organically, that makes sense (never a house in the water), driven by LLM reasoning + reliable deterministic execution.*

**Most of the pipeline works and is verified live.** There is **one release-blocking bug**: the mod bloats/duplicates structures across reloads and can render saves unloadable. Fix that first.

---

## 1. Architecture (the agreed 3-layer model)
- **Strategic layer (deterministic "firewall"):** decides *WHERE* things can go — dry/flat/near-home, safe siting — and creates *opportunities* (designates trees/animals/plants, places blueprints). It must NEVER let the LLM put a house in water. (`ColonyBuilder`, `HouseBuilder`, `StockpilePlacer`, `ColonyHome`.)
- **Settler brain (Player2 LLM):** decides *WHAT each settler wants*, reasoned from mood/hunger/needs **+ colony alerts**. (`DecisionEngine`, `LLMClient`, `ColonyAlerts`.)
- **Execution layer (deterministic, reliable):** *FORCES* the LLM's choice to actually happen via the game's real goals — **not stat cheats**. (`GameBridge.ForceGoal` → `WorkerGoapAgent.ForceNextGoalExclusive`.)

Key principle Ken hammered: settlers can be **forced** (a player command reliably makes them act). So: **LLM decides → we force the real action**. Do NOT fake needs (the old `ExecuteEat` cheated the hunger stat — now fixed to force a real eat goal).

---

## 2. Dev workflow & infrastructure (READ THIS FIRST)
- **Dashboard**: `http://127.0.0.1:8714` (Python, in `dashboard/`). Endpoints:
  - `POST /api/dev/build` `{deploy:true}` → dotnet build -c Release + copy DLL to `GAME_DIR/BepInEx/plugins/`.
  - `POST /api/dev/game/kill` / `/launch`; `GET /api/dev/status` (game_running, built vs deployed DLL hash, dll_in_sync).
  - `POST /api/dev/decompile` `{types:["Full.Type.Name"]}` → runs pinned ilspycmd, writes to `validation/decompiled/`.
  - `GET /api/game/screen?force_focus=true` → JPEG of the game (raises the window).
  - `POST /api/game/input` `{action:"click",x,y}` (0..1 rel), `{action:"keypress",key}`, `{action:"text"}`.
- **The sandbox cannot reach 127.0.0.1** — drive the dashboard via **Claude-in-Chrome in-page fetch** (tab on the dashboard).
- **GOTCHA — stale DLL:** after kill you MUST poll `/api/dev/status` until `game_running=false`, wait ~3s, THEN deploy+launch. Otherwise a not-fully-dead process keeps the OLD DLL and your changes silently don't take. (Symptom: no new behavior despite `dll_in_sync=true`.)
- **GOTCHA — bash mount truncation:** the sandbox bash mount **truncates large files** (`StockpilePlacer.cs` shows ~242 lines via bash but is ~800 real). Use the **Read/Grep/Edit tools** (real Windows files) for source; use bash only for small files and the telemetry files below.
- **GOTCHA — flooded in-game log:** the NPC pipeline floods `/api/dev/log`, rotating out mod telemetry. So the mod **writes telemetry to files** you read from the mount:
  - `validation/colony_status.txt` — per-tick: census, last action, house step, unforbid, wood, food, home, **alerts**. (Note: it's overwritten each tick; a read can catch a mid-write — retry.)
  - `validation/building_ids.txt` — all 780 building ids.
- **Ground truth**: decompiled classes in `validation/decompiled/`.
- **Game loads to a story screen** ("Click to continue" at rel ~0.498,0.955) then gameplay. `RESUME` ≈ rel 0.847,0.325. Speed key `3` = fastest.
- **Events auto-pause** the game (new settler, beggar, trader, day summary) — each stalls the mod loop until dismissed. Not hands-free for long runs.
- Autonomy master switch: `AutonomyManager.Instance.IsFullAutonomyEnabled` (config `EnableFullAutonomy`, forced on at startup in `Plugin.Update`, toggle in the in-game "LLM NPCs Settings" panel).

---

## 3. What's built (modules + status)
Legend: ✅ verified live · ◐ built, not fully verified · ⚠ known issue

- **ResourceUnforbidder.cs** ✅ — allow all forbidden ground piles (`ResourcePileManager.AllPileInstances` → `ResourcePileInstance.IsForbidden=false`). Settlers then haul/eat. Verified: starving alert cleared.
- **WoodGatherer.cs** ✅ — designate nearby trees `OrderType.Chopping` (`PlantResourceManager.GetPlant` + `SetCurrentOrder`). Verified: settlers "Cutting", wood piles grew.
- **FoodGatherer.cs** ◐ — HUNT wild animals (`AnimalManager.Instance.Animals`; `AnimalInstance.SetOrder(AnimalOrderType.Hunt)` + `view.OnMarkForOrder`; wild = `AnimalType.Wild`) + FORAGE plants (`OrderType.Harvesting`). Bounded to home + session caps. Execution depends on LLM/mood.
- **StockpilePlacer.cs** — core placement + census + water gate:
  - `TryPlaceStockpileNear` ✅ (registeredCells 16/16).
  - `CommitPlayerBlueprint` ✅ — **no-cursor** build path: `SpawnFromPool → CreateAndReturnBuildingInstance → CacheBuildingInstance` (fires `ConstructionController.BlueprintPlaced` = the construction job) `→ ObjectPlacedOnMap`. (The old `SpawnBlueprint` = interactive cursor = the bug Ken caught.)
  - `TryPlaceBuildingAt(x,y,z,id)` ✅ — exact-cell placement (walls/floors/doors/beds).
  - `TryPlaceRoofAt` ◐ — roofs via `BuildingPlacementManager.SpawnRoofAutoTesting` (roofs are COMPONENTS, not buildings). Invokes cleanly at **ay+1**; **not visually confirmed** + no real success check yet.
  - `IsDryBuildableGround` ✅ — gates placement on `StockpileManager.CanPlaceStockpile(cell,false)` which reliably **rejects water/slopes** (building `CanPlace` does NOT).
  - `HomeAnchor` — when set, all placement anchors on `ColonyHome`.
- **HouseBuilder.cs** — 2-room connected house: dry-footprint search near home, order **floors → walls → doors → roof**, **exterior door faces home**, connecting doorway between rooms, logs its reasoning. Floors/walls/doors ✅. Beds-inside placed by `ColonyBuilder` at interior cells ◐ (blocked on degraded save). Roof ◐.
- **ColonyHome.cs** ✅ — fixes a **HOME waypoint** (centroid of settlers at start); everything anchors here → compact village (verified: stockpile + house placed adjacent).
- **ColonyBuilder.cs** — the Strategic Model / orchestrator. Ordered plan each tick: **unforbid → gather wood → produce food → stockpile → cook fire → beds(inside) → house**. Heartbeat + writes `colony_status.txt`. Gated on autonomy.
- **ColonyAlerts.cs** ✅(structurally) — computes colony PRIORITIES (food scarce / no stockpile / no cook / not enough beds) from census + `ResourcePileTracker.GetTotalStockpilePilesNutrition()`, **injected into the Player2 decision prompt** as `=== COLONY PRIORITIES (URGENT) ===`. Live-content unverified (save died).
- **DecisionEngine.cs** — `ExecuteEat` now **forces a real eat goal** (removed hunger-stat cheat) and auto-discovers the eat goal id; colony alerts injected into `senderMessage`.
- **GameBridge.cs** — `ForceGoal` now returns real success (`ForceNextGoalExclusive` returns non-null Goal if the id is valid → lets us try candidate goal ids safely); `TryTriggerEat`.
- **MenuIntegration.cs** — added `EnableFullAutonomy` toggle to the in-game panel; fixed the Decision Interval slider (was silently clamping the cost-tuned 300s back to 60s on render).
- **Plugin.cs** — forces autonomy on at startup (stale cfg pinned it off); `ColonyBuilder` tick hook; trimmed per-second log spam.

---

## 4. Ground-truth API cheatsheet (from decompiled)
- **No-cursor build**: `SpawnFromPool → CreateAndReturnBuildingInstance → CacheBuildingInstance (→ BlueprintPlaced) → ObjectPlacedOnMap`.
- **Water**: use `StockpileManager.CanPlaceStockpile(cell,false)` to reject water; building `CanPlace(bp,pos,angle,silentLogs)` does not.
- **Roofs**: `BuildingPlacementManager.SpawnRoofAutoTesting(bp,gridPos,angle,scale,positions)` → `RoofComponentManager.CreateAndCacheRoofComponentInstance`. `CanPlaceRoof` rejects cells where `GroundExists` ⇒ roof at **ay+1**.
- **Chop/forage**: `PlantResourceManager.GetPlant(Vec3Int)`; `PlantMapResourceInstance.SetCurrentOrder(OrderType.Chopping|Harvesting)`; `GetPossibleOrders()`.
- **Hunt**: `AnimalManager.Instance.Animals`; `AnimalInstance.SetOrder(AnimalOrderType.Hunt)` + `AnimalView.OnMarkForOrder`; wild = `AnimalType.Wild/WildAggressive`; `CreatureBase.GetGridPosition()`.
- **Force settler**: `WorkerGoapAgent.ForceNextGoalExclusive(string goalId)` (returns Goal, null if invalid). Known ids: HarvestGoal, ConstructBuildingGoal, StockpileHaulingGoal, HuntingGoal, FaintGoal, EquipGoal… **Eat goal id NOT yet confirmed** — `TryTriggerEat` tries candidates and logs the one that takes.
- **Alerts**: `GlobalWarningMessagesManager` holds objective/general/**effector** warning dicts (MissingStockpile, MissingBed, LowStockpileFood, MealProduction, ResearchBench, Recreation, Raid, Idle, Beggar, AnimalsHungry…). Active-state is **scattered/hard to read**, so we compute equivalents instead.
- **Food supply**: `ResourcePileTracker.GetTotalStockpilePilesNutrition()`.
- **Farming (NOT done)**: manager is `CropsController` (MonoSingleton) — the create-crop-field/sow API is **not yet cracked**. Needed for sustainable food.
- Building ids: `validation/building_ids.txt`. Key: `camp_fire`, `hay_sleeping_spot`, `wood_wall_element`, `wood_door`, `wood_floor`, `wood_roof_whole`, `research_bench`(verify id).

---

## 5. ⚠ RELEASE BLOCKER — save bloat / possible corruption
**Symptom:** the test save (Libury) now hangs loading at 37.5% after ~15 reloads.

**Cause (high confidence):** the mod tracks "already built" in **per-process static fields that reset on reload**, and it never detects structures it built in a previous session. So **every load** it: rebuilds a whole ~30-piece house (`HouseBuilder` statics reset; `Plan()` finds any nearby clear spot), forces another stockpile (`_stockpilesPlaced==0` again), and leaves chopped-wood piles. → unbounded accumulation → bloat. In normal play this duplicates a house + stockpile **every time the user loads their colony** — a silent, growing corruption path.

**Uncertain:** whether it's pure bloat (valid, too big) or partly malformed state from reflection placements (roof components, `CommitPlayerBlueprint` bypassing the cursor flow). ~70% bloat / ~30% some invalid state — unproven.

**Fix before anything else:**
1. Make all placement **idempotent against SAVED game truth** — query the world for existing houses/stockpiles (by building id / room detection) and skip if present. Don't rely on process statics.
2. Persist the mod's own "built" state (or reliably re-detect on load) so `HouseBuilder.Complete` etc. survive reloads.
3. Add a **save → reload → save round-trip test** as a hard gate: object count + save file size must be **stable** across a reload (no re-placement, no growth).
4. Verify reflection placements (roof components, blueprints) **round-trip cleanly** (place, save, reload, intact). If roofs don't survive reload, that's a corruption vector to fix or drop.

---

## 6. Plan / next steps (priority order)
1. **Fix save idempotency/bloat** (§5) — release blocker. Build the round-trip test into the workflow.
2. **Validate on a FRESH colony** — Libury is dead. New Game, verify the full pipeline clean from turn one.
3. **Roof** — add a real success check (count `RoofComponentManager` instances before/after) and visually confirm `SpawnRoofAutoTesting` at ay+1 actually lands; then confirm settlers construct it and it survives a reload.
4. **Cook a meal** — verify camp_fire + raw food + cook job → an actual cooked meal (alert "Meal preparation missing" clears).
5. **Farming** — crack `CropsController` create-field/sow for sustainable food (hunt/forage are semi-renewable only).
6. **LLM-driven loop** — confirm the LLM actually reasons on the injected COLONY PRIORITIES and the execution layer forces the right actions (eat/gather/build) reliably.
7. Restore `ColonyBuilder.TickIntervalSeconds` to ~20 for release (currently 12; was dropped to 6 for fast roof testing).

---

## 7. File map
- `src/` — all modules (above).
- `validation/decompiled/` — decompiled ground-truth classes.
- `validation/colony_status.txt`, `validation/building_ids.txt` — telemetry (read from mount).
- `BACKLOG.md` — running session log.
- `dashboard/` — Python dev dashboard (build/deploy/decompile/screen/input).
- Deployed DLL: builds land in `bin/Release/net472/LLM_NPCs.dll` → `GAME_DIR/BepInEx/plugins/`. Latest build in this session: `fae95a84` (ColonyAlerts).

*Handoff written 2026-07-07.*
