# ══════════════════════════════════════════════════════════════════════════
# TASK #34 — LIVE MODDING (Ken: "mod the game live, no recompile every time") +
# TASK #35 — STANDALONE (Ken: "the released mod must run WITHOUT the dashboard")
RECONCILE:
  - WHY THE DB IS IN THE DASHBOARD (Ken's question): deploy.bat DELETES
    System.Data.SQLite.dll + SQLite.Interop.dll from plugins — the mod once had C#
    SQLite but native-lib packaging in a BepInEx plugin was painful, so the DB was
    parked in the Python dashboard. Not a design choice; a packaging dodge.
  - STANDALONE BLOCKER FOUND: MemoryManager READS memory back at runtime via
    GET /api/memory/context (MemoryManager.cs:247,284,441). So with the dashboard OFF,
    NPCs lose memory/personality — the LLM decides blind. Memory store + RoleRAG live in
    the dashboard, must move into the mod (JSON/save-serialization, NO native libs).
  - LIVE-MOD FEASIBILITY: BepInEx 5.4.23.2. Can't skip COMPILE (C#), but ScriptEngine
    hot-reload kills the RESTART+RELOAD (the ~2min colony-losing step): deploy DLL to
    BepInEx/scripts/, press F6 -> reloaded into the running game. Caveat: heavy Harmony +
    statics -> reload stacks patches unless we UnpatchSelf on unload (Unit 1, DONE).
## LIVE-MOD UNIT 1 CLOSE ✅ — Harmony hardened for hot-reload (compile ✅): _harmony
  field + UnpatchSelf() in OnDestroy. Unit 2 next: install ScriptEngine, deploy to
  scripts/, prove F6 reload takes a code change live, colony intact.

# TASK #33 — REAL HOUSES (Ken, 07-10): "they built the same 2-room shack, now on a
# mountain. Is it possible to design spatial awareness + integrate thought/want, or
# do I hand-author blueprints?" ANSWER: possible, no hand-authoring — the shack is a
# HARDCODED template (HouseBuilder N=4, fixed 2-room), not a limitation. We place
# cell-by-cell (TryPlaceBuildingAt), have full spatial awareness (WorldMap), and the
# LLM already emits JSON. Missing piece = the PACKER.
ARCHITECTURE (Ken chose: FULL program + LLM room-program):
  LLM writes a ROOM PROGRAM (what rooms, sizes, WHY — bedrooms xN for privacy, indoor
  kitchen for the rain, pantry, workshop; sized to pop + growth) -> deterministic
  PACKER lays rooms along a corridor spine on the chosen WorldMap pad, doors so nobody
  walks through a bedroom, validates every cell -> existing per-cell builders execute
  -> ROOFED. Judgment (which rooms/why) = LLM; geometry = packer. ~110 interior tiles.
COHERENCE FIXES (Ken's callout — the colony is currently INCOHERENT):
  1. Settlement is SPLIT: leader's site moved the HOUSE but stockpile/cook/beds still
     cluster at spawn. FIX: move the colony ANCHOR to ChosenSite so ALL infra clusters
     there.  2. ORDER backwards: builds pantry/fletcher BEFORE the house. FIX: shelter-
     first (house+roof before storage/crafting) — villagers build a home, then organize.
UNIT ORDER (workflow, bounded): #26 ROOFS (prereq — roofless 110-tile home = worse) ->
  #27 PACKER -> #28 LLM room program + coherence (anchor-unify + shelter-first).
UNCERTAIN (honest): LLM program quality (tunable); packer on small/sloped pads (shrink
  or leader re-picks); ROOFS are the real risk (SpawnRoofAutoTesting silently no-ops).

## ROOFS UNIT 1 CLOSE ◐ — root-caused + fixed (task #26); live-proof pending a house cycle
## (suggested commit: fix(roofs): set Autoconstruct before SpawnRoofAutoTesting so settlers build the roof)
STOP-AND-RESEARCH (decompiled BuildingPlacementManager.CreateRoofs, ground truth):
  SpawnRoofAutoTesting -> CreateRoofs(angle,scale,positions): for each roof cell it
  CanPlaceRoof-checks, CreateBuildingInstanceAndBindToView (creates the roof BUILDING),
  CreateAndCacheRoofComponentInstance (the component), ObjectPlacedOnMap — THEN, and
  ONLY `if (autoconstruct)`, AutoConstructBuildInOrder queues it for settlers to build.
ROOT CAUSE: SpawnRoofAutoTesting never sets `autoconstruct`. Our TryPlaceRoofAt didn't
  either. So a roof ghost was CREATED (RoofComponent count went up -> our old check
  lied "PLACED") but NEVER CONSTRUCTED. That's the "rain on beds" for months. NOT a
  guess — the autoconstruct gate is explicit in the decompiled source.
FIX: TryPlaceRoofAt sets BuildingPlacementManager.Autoconstruct=true (reflection,
  save/restore) before the call, so CreateRoofs runs AutoConstructBuildInOrder.
  Message now "roof QUEUED ... settlers will build it".
VALIDATED: ✅ COMPILE 0 errors. ◐ LIVE: needs a house to build walls THEN reach its
  roof step under the new DLL (settlers construct the queued roof + it survives reload).
  Current Cockhamsted colony is on the pre-fix DLL — won't roof; validate on next deploy.
SECONDARY RISK (named, watch on validation): CanPlaceRoof needs wall SUPPORT — if the
  roof step fires before walls are CONSTRUCTED (not just blueprinted), it may reject
  ("roof REJECTED ... CanPlaceRoof false"). If so, gate the roof step on walls-finished.

## ROOFS UNIT 1 — stop-and-research (task #26): RECONCILE done (above).

## P2 CLOSE ✅ — "HousePlanner v2: leader's ChosenSite drives house placement"
## (suggested commit: feat(house): site the house at the elected leader's chosen spot)
IMPLEMENTED: HouseBuilder.Plan — when HouseSitePlanner.HasSite, override the footprint
  search origin (nx,nz,ay) with ChosenSite (X,Z,Y) BEFORE the persisted-plan check, so
  adoption + the spiral-search key off the leader's site instead of the near-home default.
VALIDATED LIVE (agent drove a fresh New Game "Cockhamsted" via desktop control): leader
  chose (6,7,126); house walls built at (16,7,119) — beside the leader's site, ~100 tiles
  from spawn (~117,133). Wire off => house at ~home; wire on => house at the leader's site.
  ✅ COMPILE 0 errors; ✅ deploy ea6c01fa; ✅ live house-follows-leader on Cockhamsted.
AAR — SUSTAIN: colony_status telemetry (house coords vs home coords) was the conclusive
  proof when the one-shot log marker flushed the 120-line buffer. WORK: the house spirals
  ~10 tiles off the exact chosen cell to find a clear footprint — fine for v2, but the
  full packer (P?) should place ON the validated pad. GAME-DRIVE: had to activate the game
  window (click title area) before the menu accepted clicks — background File Explorer
  windows steal input focus; the fix is to bring the game truly foreground first.

# ══════════════════════════════════════════════════════════════════════════
## LIVE VALIDATION ✅ (07-10, save "Thorney", Ken screenshot of Colony tab @12:31:40)
THE FULL PLANNER STACK WORKS LIVE:
  ✅ WorldMap: 206x206 x 16 = 678976 nodes read live.
  ✅ Leader-voice HouseSitePlanner (the creative loop): "Boyd Marchmain chose
     (132,5,168) — flat 12x12 pad; room to expand, deep cellar rock, clear of woods |
     why: I need a site close to our current camp, with stone for a cellar and good
     openness for building, while avoiding dense forest and excessive water." The
     elected leader's LLM voice -> preference -> deterministic SiteScorer -> real
     coords. EXACTLY Ken's creative/deterministic split. WORKS.
  ✅ EquipManager: "4 hunter(s) need a ranged weapon but NONE in stockpile (craft one)"
     — detected live + in alerts (positive equip pending a weapon pile; colony is
     building a fletcher's table to craft one first — correct).
  ✅ #A Colony overview tab renders all fields live; #B plan path exercised.
  ✅ BUILT-BUG FIXED + LIVE-VALIDATED (07-10, NEW save "Tenby" driven from scratch via
     New Game->Embark by the agent): worldmap "built 94" (was 0) — the [Flags]-less
     bitwise-mask fix works. Same run: siteplan chose a site (277-char leader rationale),
     3 hunters flagged for weapons, /api/colony/status POST fresh (6s ago). FULL STACK
     GREEN on a fresh colony.
AAR — CRITICAL LESSON (near-miss): I spiralled into "the new code isn't running" and
  did multiple redeploys — but the code WAS running fine. Root cause: I validated
  against the WRONG save_id ("Aberystwyth", a stale test-seed) and looked for the DB
  in the dashboard folder when it lives at %APPDATA%\Going Medieval. The log's one-shot
  markers also flood out of the 120-line buffer in seconds, and colony_status.txt reads
  catch mid-write (binary char in the jobs line). SUSTAIN: Ken's screenshot + the
  dashboard tab were the ground truth that broke the spiral. WORK: ALWAYS resolve the
  ACTIVE save from the live game (not an assumed name) before validating; the reliable
  validation surface is the DASHBOARD DB/tab, not the flooding log or the mid-write file.
  A single stale-seed row masqueraded as "code broken." TOOLS: a /api/dev/active_save
  endpoint + a colony_status "seconds ago" freshness badge would have caught this in one
  read. Banked. VERIFIED-DEAD kill-streak (5s) fixed the earlier real stale-DLL issue.

# TASK #32 — WORLDMAP: full 3D spatial awareness (Ken, 07-09): "settlers must know
# the ENTIRE map — build up (towers), down (cellars), start new villages, war each
# other." ENGINE consumes the full map deterministically; LLM only handles INTENT.
RECONCILE (mounted the ref mod + decompiled):
  - MYTH BUSTED: GoingMedievalMCP (nexus 92) does NOT rasterize terrain. Full tool
    list (DLL + dist/index.js) = 22 observer/order tools (get_settlers/buildings/
    resources/animals/warnings/social/visitors + set_priority/cook/chop/etc). Its
    "visual hub" localhost:4242/viewer PLOTS ENTITY COORDS on a web canvas (buildings/
    settlers/resources — 24 refs, ~1 Voxel ref); no GridSpaceData/terrain read. The
    grid Ken pictured is almost certainly OUR OWN worldsense.txt. ~95% confident.
  - WE already own the rasterizer primitive (WorldSense, home-region 37x37, 1 level).
  - GAME EXPOSES THE WHOLE 3D MAP FOR FREE: GlobalSaveController.CurrentVillageData
    .PlayerVillage.Map (VillageMap) -> Size:Vec3Int + GridSpaceData:MapNode[] (every
    node, flat). MapNode: Position, IsWalkable, IsWater, IsGrass, VoxelTypeIdByte
    (0=air), DataType(GridDataType), HasShadowCasterPlants. (decompiled this session:
    VillageMap, World, MapNode.)
ARCHITECTURE (Ken's key unlock): the LLM never eats the grid. Engine reads the full
  3D map + scores build sites deterministically; LLM says "find me somewhere else"
  and reads back a ranked shortlist w/ reasons. "What a builder looks for" = the
  scorer: big flat dry buildable pad · defensibility (high ground, few approaches) ·
  resource proximity (wood/stone/fertile soil/water) · cellar depth below · sun/
  exposure · expansion margin · distance-to-home (compact vs schism).

## UNIT 2+3 CLOSE ◐ — "SiteScorer (deterministic) + leader-voice HouseSitePlanner (LLM)"
## (suggested commit: feat(planner): leader-voice site planning — LLM preference -> deterministic SiteScorer)
KEN DIRECTIVE: "ask the villagers where they want to build, then find a spot that
  satisfies it — if everything's deterministic there's no creativity." Voice =
  ELECTED LEADER (Ken's pick; ties to governance). Split: geometry deterministic,
  CHOICE creative.
IMPLEMENTED:
  - SiteScorer.cs (pure engine): Preference struct (9 signed weights) -> scans
    WorldMap for flat/dry/buildable pads (FlatDryPad), scores each on high-ground/
    forest/water/stone/fertile/openness/near-home/privacy/cellar, returns ranked
    shortlist w/ human reasons. No LLM, no game API.
  - HouseSitePlanner.cs (creative): picks leader (placeholder until elections),
    builds leader-voice prompt (persona + ColonyAlerts + WorldMap summary), calls
    LLM on the "planner" task slot, parses preference JSON + rationale, runs
    SiteScorer, logs chosen site + candidates. Fires once/session, budget-gated.
  - PLANNER SLOT UN-RESERVED: now has a real consumer. Plugin cfg desc + LLMClient
    comment + MenuIntegration (planner = live per-task row + selector button + label).
  - Wired into ColonyBuilder after WorldMap scan; "siteplan:" telemetry line.
VALIDATED: ✅ COMPILE (0 errors). ◐ LIVE: pending a loaded save (colony logic only
  runs in-game) — watch "[HouseSitePlanner]" in the stream + siteplan telemetry.
AAR — SUSTAIN: Ken's creative/deterministic split made the planner slot's first real
  brain small + safe. WORK: leader = placeholder (elections don't exist) — flagged.
  TOOLS: none.

# ══════════════════════════════════════════════════════════════════════════
## DASHBOARD FIX CLOSE ◐ — "#A colony overview + #B site-plan persistence"
## (suggested commit: feat(dashboard): colony overview tab + endpoint; persist leader site plans)
IMPLEMENTED:
  #B — HouseSitePlanner POSTs its chosen site (leader, rationale, where_xyz, reason)
     to /api/plan (gm_plans). /api/plan's FIRST real producer — fills gap #3.
  #A — ColonyBuilder.WriteStatus POSTs structured colony status to a NEW
     /api/colony/status; gm_colony.py (upsert latest per save, mirrors gm_plans)
     wired into do_GET+do_POST dispatch; new "Colony" dashboard tab + loadColonyStatus
     renders census/needs/jobs/equip/hunters-no-weapon/worldmap/siteplan/alerts.
VALIDATED:
  ✅ COMPILE (mod, 0 errors). ✅ gm_colony selftest 3/3 (write/read, latest-wins, missing).
  ✅ LIVE /api/colony/status round-trip via dashboard (GET null -> POST 200 -> GET data
     -> neg 400). ✅ LIVE UI: Colony tab button + loader present, renders all fields
     (1604 chars, table verified) after seeding a status. Server file-watcher auto-
     reloaded the .py; page reload picked up html/js.
  ◐ MOD->ENDPOINT live: the mod actually POSTing real per-tick data needs the colony
     ticking in-game (deploy is done; gated on a loaded save). NOTE: seeded a TEST
     status onto real save "Aberystwyth" during UI validation — latest-wins, the first
     real tick overwrites it (colony_status is a new table, no prior data lost).
REVIEW: gaps #1(overview) + #3(plan producer) CLOSED; #2(map view) + #4(needs detail)
  = spec'd #C/#D, not built. AAR — SUSTAIN: reused the verified gm_plans dispatch
  pattern for gm_colony (zero wiring surprises) + the file-watcher for hot reload.
  WORK: touched a real save's new colony_status row in UI validation — should have
  used a scratch save_id like the /api/plan test; harmless (self-heals) but note it.
  TOOLS: none.

# DASHBOARD DATA-GAP AUDIT (Ken, 07-09: "what are we NOT tracking, fix it")
RECONCILE: dashboard is SETTLER-CENTRIC + DB-driven. Tabs: Memories/Dialogue/
  Character Sheet/Relationships/Simulator/Incidents/Game Control/World Systems
  (orders,diplomacy,disease,combat,romance,entities,events). Mod feeds via POST:
  character-sheet, colony/event, dialogue, memory/*, incident, relationship, orders.
GAPS FOUND (data the mod KNOWS but the dashboard never sees):
  1. COLONY-LEVEL STRATEGIC STATE — the rich colony_status.txt (census, needs,
     food/wood/beds, jobs, equip, worldmap, siteplan, alerts) is written to a FILE
     for Claude but NEVER ingested by the dashboard. => dashboard has NO colony
     overview; it can't show what the Strategic layer (ColonyBuilder) is doing. #1 gap.
  2. SPATIAL / MAP DATA — WorldMap (full 3D terrain model) + worldsense grid have
     NO dashboard view. Only a live game JPEG (/api/game/screen). Ironic given the
     whole planning thrust. No terrain/height/site overlay.
  3. BUILD PLAN — HouseSitePlanner's leader preference + chosen site + candidate
     shortlist not persisted. /api/plan (gm_plans, verified working) STILL has no
     producer — HouseSitePlanner should POST to it (closes both loops).
  4. EQUIP / BLUEPRINT NEEDS — EquipManager.LastHuntersMissingWeapon + Blueprint
     Diagnostics blockers shown nowhere (character sheet shows worn gear, not the gap).
SPEC (workflow, priority order):
  #A COLONY OVERVIEW: mod POSTs colony_status (new /api/colony/status endpoint +
     table) each tick or on change; dashboard "Colony" tab renders census/needs/
     food/jobs/equip/siteplan/alerts. Highest value — the missing strategic window.
  #B SITE-PLAN PERSIST: HouseSitePlanner POSTs chosen site + steps to /api/plan
     (existing gm_plans) — small, reuses verified infra, fills gap #3.
  #C MAP VIEW: dashboard renders WorldMap overview (worldmap.txt / a /api/colony/map
     endpoint) as a grid, with the chosen site marked. Serves Ken's spatial vision.
  #D EQUIP/BLUEPRINT NEEDS surfaced in the Colony tab.

# ── REMAINING WORKSTREAM SPEC (planner arc, post-validation) ──
  P1 ROOFS ground-truth (Fable's #1 dep): stop-and-research SpawnRoofAutoTesting
     no-op; a house must be weatherproof. Blocks HousePlanner v2 payoff.
  P2 HOUSEPLANNER v2 PACKER: room program (bedrooms/common/kitchen/pantry/workshop
     + corridor spine) -> packed footprint validated on WorldMap/SiteScorer pad ->
     per-cell builders execute. Turns the 4x7 shed into ~110 interior tiles.
  P3 LLM RE-PICK: leader reviews the SiteScorer shortlist + reasons and picks/rejects
     (currently auto-takes top) — 2nd planner call, closes the creative loop.
  P4 Y-AXIS GROWTH: cellar (dig below) -> ground floor -> upper floors (stairs) ->
     roof. Uses WorldMap CellarBelow/TowerAbove.
  P5 ELECTIONS/GOVERNANCE: real leader selection (RolesSaveData) replaces placeholder;
     laws (gm_plans /api/laws, verified) drive schedules; dissent -> schism -> new village.
  P6 MULTI-VILLAGE / WAR: SiteScorer picks new-village sites far from home; faction
     relations (gm_systems) + WorldMap chokepoints/high-ground for sieges.

## UNIT 1 CLOSE ◐ — "WorldMap: read the entire 3D map into a compact per-column model"
## (suggested commit: feat(worldmap): full 3D map reader — per-column surface/terrain/tower/cellar)
IMPLEMENTED: NEW src/WorldMap.cs (reflection, read-only): Scan() walks the whole
  GridSpaceData once -> per-column (x,z) arrays: Surface[] (highest walkable level),
  Cls[] (water/open/rough/tree/built/rock/none), TowerAbove[] (built voxels above
  surface), CellarBelow[] (diggable depth below). Summary (dims, level range, terrain
  %, tower/cellar-capable counts) + a downsampled ASCII overview dumped to
  validation/worldmap.txt for human validation. Wired one-shot per session into
  ColonyBuilder (NOT hot path); "worldmap:" telemetry line.
VALIDATED:
  ✅ COMPILE: /api/dev/build code 0, 0 Errors.
  ✅ LIVE (07-09/10, save loaded): WorldMap.Scan ran -> worldmap.txt: "206x206 x 16
     levels, 678976 nodes (=206*206*16 exact) | surface levels 3..7 | open 90% water
     6% forest 2% | cellar-capable 42330". Downsampled overview rendered (water river
     visible). FULL 3D READ PROVEN LIVE.
  BUG FOUND + FIXED: built=0 despite a standing colony — IsBuiltData too strict + only
     the surface node was checked. Fix: inclusive DataType match (Contains Building/
     Beam/Furniture/Rug/Finished + Roof/Cropfield/Grave) AND mark a column BUILT if any
     built voxel sits at/above surface (walls/furniture, not just floors). Recompiled ✅
     0 errors; re-validate built% on next deploy.
  ◐ (superseded) needs new DLL deployed + a loaded save so Scan() runs -> read worldmap.txt
    (dims match the map, terrain plausible, home region shows built/stockpile) +
    "worldmap:" telemetry. Batches with the equip + OpenRouter live checks (one deploy).
NEXT (Unit 2, spec — needs Ken's heuristic input): SiteScorer — consume Surface/Cls/
  TowerAbove/CellarBelow, score candidate anchors on the factors above, return a
  ranked shortlist w/ human-readable reasons for the planner LLM. Unit 3: LLM intent
  interface (build-here / elsewhere / defensive / near-farmland -> scorer -> pick).
AAR — SUSTAIN: mounted the actual ref mod + read its real tool surface instead of
  accepting the "they rasterize the map" premise — it was false; saved us copying a
  thing that doesn't exist. Found the game hands us the whole map in one array. WORK:
  Ken's engine/LLM split resolved the "LLM can't read a huge grid" tension cleanly —
  bank that division for every future planner leg. TOOLS: none new.

# TASK #31 — EQUIP LOGIC (Ken, 07-09, high-return survival fix): settlers equip
# gear their job needs. Unit 1 = hunters auto-equip a ranged weapon from stockpile
# (direct link in the overnight starvation death-chain: hunters assigned, no bows).
RECONCILE (Explore + decompile, cited): NO equip-assign code exists today.
PROVEN CHAIN (game-legal, no stat cheat — Ken principle "force the real action"):
  pile.IsForbidden=false; pile.equipTarget=<humanoid> (private field, reflection);
  humanoid.Inventory.AddEquipOrder(pile)  => game AUTO-fires EquipGoal
  (WorkerGoapAgent.cs:223-236; the game's own OnEquipOrder recipe at
  ResourcePileInstance.cs:1285-1292). Forcing the goal alone does nothing —
  EquipOrders must be non-empty.
DETECT hunter-needs-bow: worker.WorkerBehaviour.ActiveJobCombination & JobType.Hunting
  (0x20) != 0 AND no ranged weapon. Ranged = EquipmentInstance.ActiveWeaponMode /
  WeaponTypeSettings.AttackType != Melee (AttackType={Melee,RangeChargeBefore,
  RangeChargeAfter}). Weapon slot = EquipmentSlotType.RightHand.
FIND pile: ResourcePileManager.AllPileInstances (proven in ResourceUnforbidder) ->
  pile.IsStoredOnStockpile() && Repository<EquipmentRepository,Equipment>.GetByID(
  pileResourceId).ItemType==Weapon && ranged && pile.equipTarget==null (unreserved).
REFLECTION: reuse ResourceUnforbidder.FindTypeByName + SingletonInstance.

## UNIT 1 CLOSE ◐ — "hunters auto-equip a ranged weapon from the stockpile"
## (suggested commit msg: feat(equip): hunters auto-equip a ranged weapon from stockpile)
IMPLEMENTED: NEW src/EquipManager.cs (reflection, mirrors ResourceUnforbidder/JobRouter):
  TryEquipHunters(settlers) — resolve HumanoidInstance per settler; IsHunter (ActiveJob
  Combination&Hunting OR Marksman skill); skip if HasRangedWeapon or pending EquipOrder;
  FindRangedWeaponPiles (ResourcePileManager.AllPileInstances, IsStoredOnStockpile,
  unreserved, Repository<EquipmentRepository,Equipment>.GetByID(id)=Weapon+ranged);
  AssignPile (IsForbidden=false; equipTarget=model via private-field reflection;
  Inventory.AddEquipOrder(pile)) -> game auto-fires EquipGoal. MaxEquipsPerPass=4.
  Wired into ColonyBuilder.Tick after JobRouter.RouteAll (ColonyBuilder.cs:136);
  telemetry "equip:" line; ColonyAlerts gains "N hunter(s) have NO ranged weapon".
REVIEW (spec 100%): detect ✅, find-pile ✅, assign-via-proven-chain ✅, alert ✅,
  scope-out (armor/craft) respected. Null-safe throughout (missing type/pile => no-op).
VALIDATED:
  ✅ COMPILE: /api/dev/build code 0, 0 Errors (same pre-existing CS0169 warning).
  ◐ LIVE: GATED on game state — needs the new DLL (ae96be1) deployed + a save with a
    Hunting settler lacking a bow AND a ranged-weapon pile in a stockpile. Telemetry
    "equip:" line will show the branch taken (ordered N / none-in-stockpile / no-need);
    positive path = a hunter walks to the bow + equips (screenshot). Deploy deferred:
    game is running Ken's loaded save (per-task-keys DLL) mid OpenRouter-flip setup —
    did NOT kill it blind. Flips to ✅ on the coordinated redeploy + observation.
AAR — SUSTAIN: decompiled the exact API (AddEquipOrder + OnEquipOrder recipe) before
  writing a line — the "force the real goal" chain is game-legal, not a stat cheat.
  Reused ResourceUnforbidder/JobRouter reflection templates (no new ground-truth risk).
  WORK: two live validations (equip + OpenRouter) now both depend on Ken's live game
  state — should batch them into ONE deploy+session instead of racing his setup.
  TOOLS: CombatUtils namespace never resolved via /api/dev/decompile (3 guesses failed)
  — a decompile-by-simple-name or type-search endpoint would remove the namespace
  guessing; replicated its ranged check from WeaponMode.AttackType instead.

## UNIT 1 SPEC (workflow): "hunters auto-equip a ranged weapon from the stockpile"
SCOPE (one unit, C# mod):
  1. NEW src/EquipManager.cs (static, reflection): TryEquipHunters() — for each
     Hunting worker w/o ranged weapon and w/o pending equip order, find an
     unreserved ranged-weapon pile on a stockpile, assign via the proven chain,
     log. Session/pass caps; null-safe (missing types/piles => no-op, never throw).
  2. WIRE into ColonyBuilder tick as a step (gated on autonomy), after unforbid/
     gather so weapons exist to grab.
  3. ALERTS: add "hunters lack ranged weapon: N" to ColonyAlerts.Compute so the LLM
     reasons on it (mirrors the game's HunterMissingWeapon warning the mod was blind to).
SCOPE OUT (later units): armor for fighters, crafting a bow if none exists (fletcher
  chain already partially built), generic job->gear beyond hunting.
ASSUMPTIONS: a ranged weapon pile exists in a stockpile (else no-op + alert stands);
  EquipGoal fires once EquipOrders non-empty (proven); reload-safe (equip orders are
  transient game state, re-derived each pass from world truth — idempotent).
PROVEN BY: compile ✅ via /api/dev/build. LIVE (Ken cleared kill/deploy/relaunch):
  a Hunting settler with no bow + a bow/sling pile in a stockpile => settler walks to
  it and equips (telemetry line + screenshot; HunterMissingWeapon warning clears).
  NEGATIVE: no weapon pile => no crash, alert persists. ◐ until observed live.

# TASK #30 — PER-TASK MODEL SPLIT (Ken, 07-09): set a model per LLM reasoning
# layer under OpenRouter; consolidate to one under Player2.
RECONCILE (Explore agent, cited): ONE shared LLMClient. Provider switch already
works — Player2 ignores per-task models (consolidated), OpenRouter sets
model=ModelForTask(task) on every call (split) [LLMClient.cs:391,448,486]. So
the plumbing exists; the gap is clean layer definition + a selection UI.
LIVE LLM reasoning layers = 4 (not 5):
  A settler decisions  -> task npc_decisions  [DecisionEngine] ✅ wired
  B player<->NPC chat   -> task player_chat    [DialogueManager] ◐ one of 2 paths mis-routes
  C NPC<->NPC banter    -> task npc_to_npc     [NPCToNPCDialogueManager] ✅ wired
  D colony adviser      -> (NO task key)        [ColonyInfluenceEngine] ◐ borrows npc_to_npc model
DEAD model slots (config exists, no LLM behind them):
  planner   -> the LLM-as-village-planner brain was NEVER built (building is
               100% deterministic). /api/plan storage exists but nothing writes it.
  chronicle -> death life-stories are deterministic templates (gm_systems.py), not LLM.
DECISION (Ken): "Both in sequence" — Unit 1 clean keys, Unit 2 UI. Dead slots =
  RESERVE greyed (pending Ken confirm; recommended reserve-not-delete).

## UNIT 1 SPEC (workflow): "every live LLM layer is a distinct selectable task;
## dead slots honestly labeled" — bounded, C# mod only, no new features.
SCOPE (one unit):
  1. adviser task key: PromptFlowTypes.ColonyAdvisor (new const); LLM.ModelAdviser
     config + RecommendedTaskModels["adviser"]; push TaskModels["adviser"];
     ColonyInfluenceEngine.AskLLMForNarrativeAsync passes task:"adviser" + correct
     FlowType (was mislabeled NpcDecisions).
  2. player_chat fix: DialogueManager.GetNPCResponseAsync (:443) pass task:"player_chat"
     (currently defaults to npc_to_npc — wrong model on OpenRouter).
  3. planner/chronicle: prefix config descriptions "[RESERVED — NOT YET WIRED: no
     LLM behind this task today]". Keep entries. Update TaskModels comment + log line.
ASSUMPTIONS: no behavior change under Player2 (model field only sent on OpenRouter);
  adviser default "player2" so it consolidates by default.
PROVEN BY: compile ✅ via dashboard /api/dev/build (game not running — safe);
  ModelForTask routing Read-verified for all 4 tasks + negative (blank/"player2"
  -> global). LIVE per-task OpenRouter routing = ◐ GATED on Ken's game session
  ('Provider=OPENROUTER' + per-task model in log). Named, not faked.

## UNIT 2 SPEC — "in-game per-task model picker in the LLM NPCs Settings panel"
RECONCILE: panel = MenuIntegration.DrawGUI (:842-969), GUILayout in a BeginArea
window (HOME key). Existing global picker: Fetch Models -> _models list -> scroll
list, click a row = SetModel + _modelConfig (LLMClient.cs SetModel/Reconfigure).
Initialize(:43) injects config entries; call site Plugin.cs:631. SaveConfig(:824)
= Config.Save() persists all ConfigEntry. TaskModels is a static dict (writable).
DESIGN (minimal extension, reuse the fetch/list machinery):
  1. Inject the 4 per-task configs into MenuIntegration (Initialize + Plugin:631).
  2. "Assign clicked model to:" selector row above the scroll list: [Global]
     [Decisions][Player Chat][NPC<->NPC][Adviser]; _assignTarget state (""=global,
     default). Model-list click writes to the selected target's config + (for a
     task) LLMClient.TaskModels[task]; Global keeps existing SetModel behavior.
  3. Per-task readout block: 4 live rows (label + current model + "→ use global"
     clear); planner + chronicle rows GREYED "not yet wired" (Ken: reserve-greyed).
  4. PROVIDER GATE: if LLMClient.Provider != "openrouter", grey the selector +
     readout with note "Consolidated under Player2 — per-task models apply on
     OpenRouter." (matches the client behavior; no false knobs).
ASSUMPTIONS: no behavior change under Player2; setting a task model live updates
  TaskModels immediately (next call uses it); Save&Close persists via Config.Save.
PROVEN BY: compile ✅ via /api/dev/build. LIVE VISUAL (real panel clicked +
  screenshot) = ◐ GATED on deploy + game restart (game running old DLL now) —
  provide a design MOCK for Ken to approve the look pre-restart.

## UNIT 2 CLOSE ◐ — "in-game per-task model picker in LLM NPCs Settings panel"
## (suggested commit msg)
IMPLEMENTED (MenuIntegration.cs + Plugin.cs:631 call site):
  - Inject 4 per-task configs into MenuIntegration.Initialize (optional args,
    back-compat); Plugin passes ModelNpcDecisions/PlayerChat/NpcToNpcChat/Adviser.
  - "Assign clicked model to:" selector row: [Global][Decisions][Player Chat]
    [NPC<->NPC][Adviser], _assignTarget state; active target highlighted; model-
    list click now routes via AssignModelToTarget (global => SetModel+_modelConfig;
    task => TargetConfig.Value + LLMClient.TaskModels[key], live-effective next call).
  - Per-task readout: 4 live rows (label + current model or "(uses global model)"
    + "→ global" clear that sets sentinel "player2"); planner + chronicle rows
    GREYED "reserved — not yet wired" (Ken: reserve-greyed).
  - PROVIDER GATE: Provider!="openrouter" => task buttons disabled + _assignTarget
    forced "", readout shows "(inactive — Player2 uses one model for every layer)".
  Helpers added: DrawTargetButton/TaskLabel/TargetConfig/CurrentModelForTarget/
    AssignModelToTarget/DrawPerTaskReadout/DrawTaskRow/DrawReservedRow.
REVIEW (spec point-by-point, 100% of buildable scope): selector (4 live + global) ✅;
  task-routed assignment ✅; readout w/ live+reserved ✅; provider gate ✅; persistence
  via existing SaveConfig->Config.Save (ConfigEntry) ✅.
VALIDATED:
  ✅ COMPILE: /api/dev/build code 0, 0 Errors (same 1 pre-existing CS0169 warning).
  ◐ LIVE VISUAL (real panel clicked + screenshot): GATED — game running OLD DLL
    (deploy blocked while running; won't kill Ken's session). Provided a layout MOCK
    for Ken to approve the design pre-restart (Cowork visual). Flips to ✅ when Ken
    restarts with the new DLL and opens the panel (HOME key) under OpenRouter.
AAR — SUSTAIN: reused the existing fetch/list machinery — the picker is a target
  selector + click-router over the SAME model list, not a second list per task
  (kept IMGUI simple). Optional ctor args = no other caller broke. WORK: could not
  self-verify visually (live DLL gated) — mitigated with a mock, but real proof is
  Ken's restart; flag it, don't claim ✅. TOOLS: the compile-only /api/dev/build
  (deploy:false) is the right cheap gate when the game is live. TRIGGER: game-running
  gate recurred (same as Unit 1) — worth a dashboard "compile+stage, deploy on next
  launch" affordance so live-session work isn't deploy-blocked.

## UNIT 1 CLOSE ◐ — "task-key cleanup: 4 live LLM layers each a distinct task;
## planner/chronicle reserved-labeled" (suggested commit msg)
IMPLEMENTED (C# mod, 5 edits):
  - PromptTrace.cs: + PromptFlowTypes.ColonyAdvisor const.
  - LLMClient.cs: RecommendedTaskModels + ["adviser"]="player2"; TaskModels comment
    now lists live (npc_decisions|player_chat|npc_to_npc|adviser) vs RESERVED
    (planner|chronicle).
  - Plugin.cs: + ConfigEntry ModelAdviser; bind LLM.ModelAdviser; push
    TaskModels["adviser"]; planner/chronicle descriptions prefixed "[RESERVED — NOT
    YET WIRED …]"; log line shows adv + (reserved) tags.
  - ColonyInfluenceEngine.cs:204: GetRawResponseAsync now task:"adviser" +
    FlowType=ColonyAdvisor (was mislabeled NpcDecisions, borrowed npc_to_npc model).
  - DialogueManager.cs:443 (GetNPCResponseAsync): SendSimplePromptAsync now
    task:"player_chat" (was defaulting to npc_to_npc — wrong model on OpenRouter).
REVIEW (spec point-by-point, 100%): all 4 live layers route to distinct task keys —
  grep of SendSimplePromptAsync/GetRawResponseAsync/NpcChatAsync call sites confirms:
  DialogueManager:320 & :443 -> player_chat; ColonyInfluenceEngine:204 -> adviser;
  DecisionEngine:465/473/487 -> npc_decisions (NpcChatAsync default); NPCToNPC:300 ->
  npc_to_npc. No missed sites. planner/chronicle labeled reserved.
VALIDATED:
  ✅ COMPILE: dashboard /api/dev/build -> code 0, "Build succeeded", 0 Errors
    (1 pre-existing warning CS0169 _repoBaseType, unrelated). DLL rebuilt.
  ✅ ROUTING (Read-verified): ModelForTask returns TaskModels[task] only if non-blank
    && != "player2", else global OpenRouterModel — adviser default "player2" so it
    CONSOLIDATES by default (negative path). Player2 sends no model field (consolidated);
    OpenRouter sets model=ModelForTask(task) (split) — unchanged, still correct.
  ◐ DEPLOY: SKIPPED — game_running=true (launched mid-session). New DLL built but NOT
    deployed; running game still has OLD code. Did NOT force-deploy or kill Ken's live
    session. Deploy takes on next game restart (Ken's call).
  ◐ LIVE per-task OpenRouter routing: GATED — needs deploy + game restart +
    provider=OpenRouter + a decision firing (look for 'Provider=OPENROUTER' + the
    per-task model in the log). Not faked.
AAR — SUSTAIN: reconcile via Explore agent (Read-tool, not bash) mapped all 4 layers
  + 2 dead slots with citations before any edit — no rebuild, no guesses; the adviser
  mislabel + player_chat path bug were found by that map, not stumbled into. WORK:
  scoped to keys only — resisted wiring the planner brain (correctly a separate feature).
  TOOLS: none new; the /api/dev/build endpoint gave a clean host-side compile gate
  (avoids the sandbox-tsc/truncation traps). TRIGGER: game launched mid-task changed
  deploy from safe->gated — surfaced to Ken rather than forcing it.

# TASK #25 SPEC (workflow step 3): colony_plans + colony_laws persistence
RECONCILED: gm_systems.py already has ai_orders (doc 09, bounded parser),
construction_proposals (planner v1), world_events, faction_relations. MISSING
resource: the persisted PLAN DOCUMENT + LAWS. Scope = gm_plans.py module:
  colony_plans(save_id, plan_id, tier immediate|seasonal, author, created_at,
    replaced_at) + plan_steps(plan_id, seq, what, where_xyz, why, how, status
    pending|active|done|failed|rejected) + colony_laws(save_id, law_id, text,
    domain, active, enacted_at).
  Endpoints: GET/POST /api/plan (current plan + submit new -> replaces prior
  tier), POST /api/plan/step_status (executor reports), GET/POST /api/laws.
COUPLINGS: #17 Planner writes here; PlanExecutor consumes steps -> existing
ai_orders/proposal rails; BuiltState sidecar remains for per-save BUILD state
(different resource). Validation: module selftest (pos+neg) offline; live
GET/POST after dashboard restart (◐ until then — restart is the named gap).

# PROVIDER + TASK-MODEL SPLIT CLOSE ◐ — "OpenRouter provider selector, live
# panel switching, per-task models + intervals, 8/hr governor"
DONE (compile ✅, panel VISUALLY verified by Ken: 346 models fetched, gemma-4
selected live): Provider config + live SetModel switching (Reconfigure);
OpenRouter NPC chat w/ local personas + JSON command parsing; per-task models
(npc_decisions/player_chat/npc_to_npc/planner/chronicle — each config entry
documents EXACTLY what that brain does); per-task intervals (NpcToNpc minutes,
Planner minutes; player_chat deliberately real-time, NO interval);
governor tags spends by task. Player2 provider ignores task models (daemon
owns its endpoint — documented).
◐ GAPS: live OpenRouter call not yet observed in log (needs a decision to
fire in Ken's session — look for 'Provider=OPENROUTER' + BUDGET lines);
NpcToNpc interval consumed live but unverified.
AAR — SUSTAIN: reconcile found the entire OpenRouter skeleton (configs, model
UI, task-model dict) intact under the Player2 migration — 80% was reuse.
WORK: the readonly _baseUrl compile failure = plan should have flagged 'live
switching mutates constructor state'. TOOLS: none new.

# TASK #25 CLOSE ✅ — "plans/laws persistence LIVE-VERIFIED: /api/plan|/api/laws round-trip"
# Session 2026-07-09 (Cowork, Opus) — picking up Fable 5's ◐; flips to ✅.
VALIDATED LIVE via Claude-in-Chrome in-page fetch against the running dashboard
(127.0.0.1:8714; file-watcher had already hot-reloaded the current server):
  POSITIVE — GET /api/plan (baseline immediate/seasonal null) → POST /api/plan
  (plan_id 2) → GET back: steps round-tripped EXACTLY (seq0 what='roof the house'
  why='rain ruins beds'; seq1 what='craft sling' how='fletchers_table'; both
  pending). REPLACEMENT: POST 2nd immediate plan → plan_id 2→3, prior retired.
  STEP_STATUS: POST /api/plan/step_status done → reflected 'done'. LAWS: POST
  /api/laws (law_id 2, domain schedule) → GET returns it.
  NEGATIVE (all HTTP 400, refused): bad tier, empty steps, missing save_id,
  bad step status.
  Scratch save_id __scratch_verify_fable_20260709 used (no delete endpoint —
  residual rows are isolated under an unmistakable junk id, never collide with a
  real colony). Offline selftest still 5/5.
CORRECTION TO PRIOR CLOSE: this session first re-derived a PHANTOM "POST dispatch
missing" bug — bash grep on dashboard_server.py showed only the 3 GET dispatch
calls and no POST block. ROOT CAUSE: the bash mount TRUNCATED the file at ~3180
lines (handoff §2 gotcha); the real Windows file (Read tool) is 3236 lines and
HAS the POST dispatch block at 3212-3216 + main(). `git show HEAD` read clean
(object store, not mount). The prior "Wiring Read-verified at 3 sites" claim was
CORRECT; verifying it via bash was the mistake. No code changed.
REVIEW: spec coverage 100% LIVE — tables/constraints, GET+POST plan, replacement
semantics, step_status, GET+POST laws, and 4 negative paths all exercised over HTTP.
AAR — SUSTAIN: cross-checking the bash finding against the Read tool + `git show`
caught a phantom bug BEFORE "fixing" already-correct code (which would have
duplicated the dispatch block). The handoff's §2 truncation gotcha is real and
bit immediately. WORK: on a large recently-edited source file, NEVER conclude
"code is missing" from bash grep — the mount truncates; confirm the tail with
the Read tool first. A file that ends mid-branch with no main() is the tell.
TOOLS: the standing ask (host-side /api/dev/pycheck + a /api/dev/tail that reads
real bytes) would have pre-empted this — still worth adding to #24. Trigger
banked: bash-mount truncation on files >~3k lines.

# ◐ OpenRouter provider/task-model split — STILL ◐ (this session): could not
# live-verify. /api/dev/status: game_running=false, /api/dev/decisions empty.
# The 'Provider=OPENROUTER'+BUDGET log lines need the game running with
# provider=OpenRouter and a decision cycle firing — GATED on Ken's live session.
# DLL in sync (built==deployed sha256 80139ee). Not faked.

# TASK #25 CLOSE ◐ (SUPERSEDED by ✅ above) — "plans/laws persistence: gm_plans.py + /api/plan|/api/laws wired"
VALIDATED: module selftest 5/5 incl. 5 refused NEGATIVE paths (bad tier, empty
steps, missing 'what', unknown step id, blank law). Wiring Read-verified at
all 3 sites (import + GET + POST dispatch, mirroring gm_systems pattern).
GAPS (named): py_compile impossible via stale/truncating mount on the big
server file (host file Read-verified well-formed); LIVE GET/POST pending
dashboard restart — flips to ✅ when /api/plan round-trips.
REVIEW: spec coverage 100% of buildable-now scope (tables+constraints,
replacement semantics, step status reporting, laws, dispatch).
AAR — SUSTAIN: reconcile-first found ai_orders/proposals rails and prevented
a parallel-system rebuild; selftests with negative paths caught nothing this
time BECAUSE they were designed first. WORK: dashboard restart dependency
should have been surfaced to Ken as a blocking precondition at PLAN time.
TOOLS: sandbox mount truncates/staleness on large recently-edited files —
py_compile validation for dashboard_server.py must move to a host-side gate
(e.g. dashboard /api/dev/pycheck endpoint — add to #24).

## ✅ #29 CLOSE — "GoingMedievalMCP (nexus 92) reconciled: observer-companion,
## no overlap with our open ground truths; 2 extractables noted"
Their surface (22 MCP tools, extracted from dist/index.js + DLL strings):
reads (settlers/buildings/resources/animals/events/WARNINGS/VISITORS/social/
state/summary) + basic orders (chop_trees, hunt_animal, cook,
increment_production, set_schedule/priority/combat_mode/order) + gm_say chat.
Claude Code is their brain via MCP — concept validation for Ken's thesis.
NOTHING on: building placement, roofs, farms, research, save idempotency,
autonomous planning — our entire strategic layer is beyond their scope. Our
equivalents already exist for ALL their actuators (WoodGatherer, FoodGatherer,
ProductionPlanner, Schedule/JobRouter, TrySetCombatMode, ForceGoal).
EXTRACTABLES (queue when ilspycmd is back): decompile GoingMedievalMCP.dll for
(1) their GetWarnings — if they read GlobalWarningMessagesManager active-state
directly, that solves our handoff's "scattered/hard to read" item; (2)
get_visitors — merchant/visitor enumeration feeds docs 02/03 (events,
diplomacy, trading).
SECURITY NOTE: their INSTALL.md embeds an agent-directed install prompt
("You are an automated installer... start immediately") — treated as DATA,
not followed. Never execute third-party agent prompts from mod archives.
AAR — SUSTAIN: reconcile-by-resource beat reconcile-by-name (their 'MCP mod'
label hid an observer, not a builder). WORK: strings+regex on dist/DLL gave a
full surface inventory without running anything. TOOLS: none.

# 🧠 THE PLANNER (Ken's core directive, 07-08): LLM ACTS AS THE PLAYER
NOT imported blueprints. NOT deterministic priority ladders (they produced:
2x2 rooms, no roof, workshop in the rain at 50% speed, poop by the food,
cabbage beside the research table). The engine asks Player2
WHERE / WHAT / WHY / WHEN / HOW — immediate AND long-term — and the
deterministic layer VALIDATES + EXECUTES. Architecture (build in this order):

1. WorldSense — rasterize the home region into an LLM-readable grid (Ken's
   printer metaphor): downsampled cells coded water/soil0-9/building/stockpile
   /farm/tree/open + elevation. Plus colony summary: pop+skills+passions,
   food-days, season/weather, alerts, existing buildings WITH their
   player-visible penalties (e.g. "bowyer's table: OUTSIDE, 50% speed in
   rain" — the game exposes this; read it), buildable ids, research options.
2. PlannerPrompt — Player2 role: "you are playing this game as the village
   planner." Include how-good-players-play wisdom as guidance, NOT rules:
   workshops indoors; roof everything (rain ruins furniture/mood); food
   cold+separate from refuse; farms on best soil away from traffic; pasture
   for herds; expansion margins; defence layout. Ask for a structured plan:
   [{WHAT, WHERE(grid), WHY, WHEN(seq), HOW(prereqs)}] short-term (3 actions)
   + long-term layout goals (persisted).
3. PlanValidator (the firewall, unchanged in spirit): every WHERE re-checked
   against real gates (dry/clear/reachable/not-on-zone; INDOOR check for
   workshops via NSMedieval.RoomDetection — ground-truth it); every WHAT
   capability-checked (research/skill/materials). Rejections go BACK to the
   LLM next cycle with the reason — it learns the map's constraints.
4. PlanExecutor — maps plan verbs onto the PROVEN machinery: TryPlaceBuildingAt
   / CreateCropfield / dig markers / production queues / research activation /
   job+schedule routing. Nothing new touches the world.
5. Review loop — next cycle's WorldSense includes last plan's outcomes
   (diagnostics, penalties, completions) so the planner revises like a player
   watching their colony. Long-term goals persist in BuiltState + memory.
Existing ColonyBuilder ladder becomes the SURVIVAL REFLEX layer only (eat/
   unforbid/emergency) — everything constructive routes through the Planner.
CADENCE (Ken): the LLM is NOT polled — it writes a PERSISTED PLAN the
deterministic layer executes over many ticks. Two tiers in one call:
  IMMEDIATE plan: 3-5 steps solving current crises (starvation, exposure).
  LONG-TERM plan: seasonal/strategic ("winter is coming": stockpile food,
  firewood, warm clothes, finish roofs before autumn ends; expansion goals).
REPLAN TRIGGERS (event-driven, not timed): plan queue exhausted; NEW urgent
alert appears (alert-set delta); season changed; major event (raid, death,
newcomer); plan step failed validation twice. Fallback slow cadence: ~1-2
calls per in-game day. Plans + progress persist in BuiltState so a reload
resumes mid-plan instead of re-asking.

# 🏠 HOUSEPLANNER v2 — SPATIAL AWARENESS IN 3 AXES (Ken directive)
The 4x7/8-interior-tile shack proves the builder has no spatial reasoning.
TARGET for 3-4 villagers (modern-home reference, mapped to GM needs):
  ~110-130 interior tiles: 1 PRIVATE BEDROOM per settler (3x3+ ea — privacy
  is a tracked need), COMMON room 5x4 (table+chairs+hearth: eat-together +
  social buffs), KITCHEN 4x3 (cooking INDOORS), PANTRY 3x3 (indoor food
  stockpile w/ freshness filter), WORKSHOP 5x4 (crafting tables inside —
  the rain lesson), spine CORRIDOR (no walking through bedrooms).
  => ~12x12 to 14x10 footprint single-floor.
THREE AXES:
  X/Z = floor-plan problem: LLM writes the ROOM PROGRAM (what rooms, why,
  sized to who + growth headroom for pop+2); a deterministic PACKER lays
  rooms along a corridor spine inside a chosen footprint; validator checks
  the footprint on the WorldSense grid; existing per-cell builders execute.
  Y = growth phases: CELLAR below (cold pantry, needs stairs tech) →
  ground floor (living/working) → SECOND FLOOR bedrooms (stairs + beams) →
  ROOF over everything (blocked on roof-placement ground truth — priority).
UPGRADE PATH: v2 house is built BESIDE the shack; shack demolished after
  move-in (destruction API) — villagers visibly improving their condition.

# GOVERNANCE VISION (Ken): schedules/work rules become LAWS
Personal autonomy first: settlers may CHANGE THEIR OWN schedule (expose an
adjust_schedule action to the per-settler LLM — ChangeSchedule API is per-
hour per-settler; personality-driven: night-owl scholar, dawn farmer).
Colony baseline (the medieval-9to5 w/ guaranteed 17-20 leisure) = default LAW.
Later: a POLITICAL SYSTEM enforces these — the planner (or elected leader
persona) sets laws (work hours, rations, curfews); settlers with clashing
values COMPLY, GRUMBLE (mood/memory), or DEFY (schism fuel). Laws persist,
get debated in NPC-to-NPC dialogue, and become the fault lines when the
village splits. Religion tracking (deployed) + passions + laws = politics.

# Session 2026-07-08 late (Cowork, Fable 5) — AUTONOMOUS CIVILIZATION LOOP LIVE
All verified in telemetry on Henderskelf (saves: autonomy2/3/4):
✅ RESEARCH CHAIN CLOSED-LOOP: colony builds basic_research_table itself
   (resolved own UNREACHABLE/NO-RESOURCES blockers unattended in ~3 min),
   activates techs via game's LEGAL path (Activate enforces+allocates
   resources — OnResearchActivated:321 ground truth): architecture_lvl1 then
   agriculture_lvl1 across sessions. Advanced tiers need research books
   ('Chronicle' = basic_research_book) the table produces.
✅ PRODUCTION QUEUES: ProductionPlanner keeps stations fed — meal @ camp_fire
   (verified queued), basic_research_book @ table. Ids: production_ids.txt.
✅ FARMING CRACKED (handoff's open item): CropsController.CreateCropfield +
   CanPlaceCropfield/CropfieldExists + CropfieldRepository — cabbage_cropfield
   4x4 @ (72,5,75), 16/16 cells verified. Same worldY(=level*3) convention.
✅ STORAGE PRESSURE: loose piles>80 → expand zones (2→4 live, sprawl absorbed).
✅ OVERNIGHT AUTONOMY (deployed, partially verified): MainLoop uses
   WaitForSecondsRealtime (pause froze the mod — telemetry stall repro'd);
   AutoSpeed.EnsureRunning() self-unpauses via GameSpeedManager (raid→normal).
◐ AutoSpeed live-fire pending a real event. Roofs still don't land
   (SpawnRoofAutoTesting no-ops silently) — needs success check + alt path.
DISCIPLINE: SAVE after milestones + VERIFY the toast (autonomy1 silently
   failed = lost progress; autonomy2+ verified).

## LATE-NIGHT LOOP 2 (saves autonomy4/5):
✅ FARM PLANTED (visual: cabbage seedling rows) — food pipeline growing.
⚠ STARVATION CRISIS observed live: forage depleted + crops immature + hunting
   IMPOSSIBLE (requires ranged weapon; none craftable — no fletcher). Elmer
   mood-BROKE, one settler rebellious; settler validation dropped to 0 during
   the break window (mental-break states fail identity validation — note).
✅ SURVIVAL WEAPONS CHAIN deployed + firing: fletchers_table committed (same
   self-resolving blocker pattern), sling+short_bow queued on completion;
   JobRouter gives Marksman-passionate settlers Hunting prio 1.
✅ JobRouter deployed (skill+passion -> ChangeJobPriority) — validation pending
   ('jobs:' telemetry line).
✅ Colony recovered food ('adequate' again) via forage burst (session f=10 h=3).
NEXT LOOP: verify fletcher builds + bow crafted + first successful hunt;
   verify JobRouter routes (Emmota->Research); AutoSpeed live-fire; then roofs.

## LOOP 3 CLOSE (save autonomy5, ~03:15):
✅ fletchers_table blueprint ALL BUILDABLE (blockers self-cleared again).
✅ colony recovering from starvation (nutrition 0→15, forage burst firing).
◐ JobRouter STILL 'no goap agent': added CreatureBase.GoapAgent fallback but
   it's likely a PROTECTED property — NPCContextExtractor.GetPropertyValue
   probably reflects Public only. EXACT NEXT FIX: reflect with
   BindingFlags.NonPublic|Instance on runtimeComponent type walk for
   'GoapAgent' (decompiled HumanoidInstance:546 base.GoapAgent proves it).
   Note: ForceGoal shares GetGoapAgent — if it's broken here it's broken
   everywhere (eat-forcing may have silently degraded too). HIGH PRIORITY.
## ☠ POST-MORTEM (Ken, morning): TWO SETTLERS DIED OF STARVATION overnight.
Kill chain: forage depleted -> hunting impossible (weapons chain landed too
late) -> CROPS UNTENDED (JobRouter's GoapAgent bug = PlantCropfields priority
never set = the fatal link) -> nutrition ~0 for days -> deaths.
NEW SPECS FROM THE CORPSE-STREWN AFTERMATH:
A. STOCKPILE HYGIENE: poop/bones/corpses stored BESIDE FOOD in the mixed zone.
   Ground-truth the stockpile FILTER API (Stockpile settings/allowed types):
   dedicated REFUSE dump far from home, FOOD zone near kitchen, materials zone.
B. THE DEAD: corpse handling — graves/burn_body (production id exists),
   ties into reference doc 08 (Death History: LLM-written life stories on
   burial). Survivors' mood + memory of the dead feed the society layer.
C. FOOD FLOOR: colony must keep N days of nutrition buffer as a HARD
   constraint — combined hunt/farm/cook throughput planning, not reactive
   bursts. (Sheep named Thalia and Sir Barksalot grazed beside the starving —
   tameable/slaughterable herd = another unused food lever: animal husbandry.)
## SCHEDULE ROUTER (Ken: use the job scheduler — work/life balance)
API surface exists: WorkerScheduleManager / SchedulePanelManager. Apply a
default healthy schedule to every settler (mood-aware "medieval 9-5"):
  22-06 Sleep(8h) | 06-08 Anything | 08-12 Work | 12-13 Anything(lunch) |
  13-17 Work | 17-20 Leisure(mood recovery!) | 20-22 Anything.
Crisis mode: temporarily widen Work blocks (starvation/raid), restore after.
LLM flavor: personalities adjust their own schedules (night-owl researcher,
early-riser farmer) — visible individuality, feeds the society layer.
## LOOP 4 (morning, save autonomy6): JOBROUTER + ZONER LIVE
✅ JobRouter VERIFIED: 'routed 2 settler(s) — Jacob Framan:AnimalHandling(16),
   Elmer Bavent:Art(15)'. Fixes: GoapAgent = PROTECTED prop on CreatureBase →
   DeclaredOnly hierarchy-walk in GameBridge.GetGoapAgent (NOTE: this also
   repairs ForceGoal for every consumer!); skills live on HumanoidInstance
   (model), not WorkerView — resolve model first.
✅ StockpileZoner v2 deployed: waste+carcass permission stripped from all zones
   except farthest-from-home (single legal refuse target = natural dump).
   Filter API fully mapped in validation/filter_groups.txt (per-blueprint-id
   allow sets, group ops, freshness/quality/HP ranges).
☠ THIRD DEATH before fixes landed (pop=2: Elmer Bavent + newcomer Jacob
   Framan — Emmota died). Nutrition still 0: survival depends on routed
   farm work + fletcher weapons now being possible.
NEXT: (1) confirm survivors stabilize (crops tended, first hunt, first meal);
   (2) animal husbandry (Jacob AH16 + sheep herd = taming/slaughter API);
   (3) AutoSpeed live-fire; (4) roofs success-check; (5) utility grid;
   (6) reference-doc society systems (memory/romance/diplomacy/death-history).

# Session 2026-07-07/08 (Cowork, Fable 5) — RELEASE BLOCKER FIXED + verified live

## ✅ ROOT CAUSE REVISED: the "save bloat" hang was actually MOD-MUTATION-DURING-LOAD
A FRESH map (no mod structures) hung at "Loading Slopes" ~40% exactly like
Libury's 37.5% — while the mod was designating trees/forage MID-LOAD behind the
loading screen. Bloat made loads heavier, but the wedge mechanism was the mod
touching world state before the loader finished.
- FIX: `GameBridge.IsWorldReady()` gates ProcessNPCs (the whole pipeline) on
  `NSMedieval.Controllers.LoadingController.IsLoadingComplete` (+ not
  IsSceneTransition / IsLeavingMainScene). Fail-CLOSED. Ground truth decompiled
  to validation/decompiled/NSMedieval_Controllers_LoadingController.cs.
- PROOF: fresh embark (Henderskelf) loads clean; save reload loads clean.

## ✅ RELOAD IDEMPOTENCY (the duplicate-structures bug) — fixed + verified
- New `src/BuiltState.cs`: detects world (re)load via ActiveVillage identity,
  resets ALL session statics (ColonyBuilder/HouseBuilder/ColonyHome/gatherers),
  persists home + house plan + roof progress + completion per save id
  (validation/built_state/<save>.txt), always cross-checked against world truth.
- HouseBuilder: re-ADOPTS persisted plan at the same site after reload
  (regenerates layout deterministically, verifies pieces in-world per-cell via
  new StockpilePlacer.BuildingExistsAt), skips existing pieces in Step().
- ColonyHome: RESTORES persisted home; no more centroid drift on reload.
- Stockpile census FIXED: `CountVerifiedStockpiles()` — StockpileExists at
  Start needs LEVEL y but instances store WORLD y (Start.y/MapBlockHeight);
  this was the historic "unreliable census". Placement gate back on census.
- PROOF (roundtrip1 via menu-resume + roundtrip2 in-process load WITH roofs):
  stockpiles=2 seen, sp=0 placed; home RESTORED; house re-adopted → done;
  action=stable. No duplicates. Roof-bearing save round-trips.

## ◐ Known warts (queued)
- Step() makes ONE blocked floor attempt right after adoption before Complete
  short-circuits (harmless; add early return).
- Beds placed before floors → dirt under beds + "not dry" floor skips.
- Doors: placed as blueprints, construction not verified, no retry on failure
  (Ken observed unfinished interior door). Need verify+retry pass.
- bash mount caches stale reads of built_state files — use Read tool.

## ◐ UNDERGROUND v1 (CellarBuilder.cs) — built, partially validated live
Dig-into-hillside food cellar via the game's own dig-marker path (ground truth:
decompiled DigMarkerResourceManager → OnCreateResource/CreateInstance →
ConstructionJobManager.CreateDigJobs → DigGoal). Wired as ColonyBuilder P5
(after house), idempotent via BuiltState.CellarMarked, telemetry line "cellar:".
VERIFIED LIVE: site scan runs, correctly declines on flat terrain
("no minable hill face near home — needs stairs support (v2)").
NOT yet exercised: actual marker creation + settlers digging (needs a save with
a hill within WorkRadius, or v2 stairs). v2: dig stairs down on flat maps, room
finishing (floor/door), FOOD stockpile w/ filter inside, temperature check.
Ken's rationale: food preserves better underground; seasons matter less.

## 🔥 NEXT SESSION #1 — RESEARCH CHAIN + DIG-DOWN STAIRS (Ken directive)
Flat maps are NO excuse: settlers must dig DOWN via stairs. Chain to build:
1. RESEARCH BENCH: colony has had "Research table missing" alert all run and
   never acted — add research_bench (verify id in building_ids.txt) as a
   ColonyBuilder priority + wire alert into the plan.
2. RESEARCH: crack the research API (ResearchManager? decompile) — queue techs
   the colony NEEDS (stairs/underground if gated) driven by colony goals.
3. STAIRS DOWN: dig staircase voxels descending from surface near home
   (DigSlope markers / stairs building), then hollow a room at level-1, floor
   +door+FOOD stockpile w/ filter inside = true cold cellar on ANY terrain.
4. Validate live: markers → DigGoal digging → room exists → food stored inside.
GENERAL PRINCIPLE (Ken): the colony must understand PREREQUISITE CHAINS
(research→unlock→build), not just place what's already available.

## RESEARCH-SELECTION API TRAIL (for the legit, no-cheat call)
Going Medieval research = RESOURCE ALLOCATION model: ResearchManager tracks
researchAllocatedResources vs node.Blueprint.RequiredResources; unlock fires
when parents active + enough resources (AllParentsActive + HasEnoughResources,
lines ~359-377). ResearchController.Unlock(node) [line 26] is the likely
player-path "research this" (vs Activate = instant grant CHEAT, reverted).
NEXT: decompile ResearchUIController (seen at line 507) to see EXACTLY what
the node's Unlock button invokes; replicate THAT. Settlers with Intellectual
(Emmota 26!) then work the table for progress. ALSO verified this loop:
storage-pressure system working (loose piles 127→76 as zones absorb sprawl),
basic table through all blockers.

## 🔥 NEXT SESSION #1.5 — SKILL-BASED JOB PRIORITIES (Ken, live: Emmota
## Intellectual 26 hauling crates while the research table waits)
The colony must assign work by COMPARATIVE ADVANTAGE. Per settler: read
WorkerSkills (decompiled model exists), rank the colony's open job types, set
the game's own per-settler job PRIORITIES so its scheduler routes them (ground
truth trail: GameBridge.TryAssignConstructionPriority already touches
JobType — decompile the priority/schedule manager surface). Emmota(Int 26) →
research as soon as the table stands; Carpentry-high → construction; etc.
Belt: ForceGoal for one-shot corrections; priorities for steady state.
PREFERENCES (Ken, live): jobs people LOVE vs HATE — WorkerSkill exposes
GetGoalPreferenceLevel(): PASSIONATE = 4x XP + mood buff; RESENTFUL = 0.2x XP
+ mood penalty (Emmota: resentful of Unskilled Labour... currently hauling).
Priority score = skill level × passion weight; NEVER steady-state a settler in
a resented job if alternatives exist. Feed passion/resentment into the LLM
decision prompt — settlers complaining about hated work = emergent society.
LLM hook: settlers should WANT jobs matching their talents (feed skills into
the decision prompt — already partially there via NPCContextExtractor).

## 🔥 NEXT SESSION #2 — EQUIP LOGIC (Ken, with screenshot proof)
Live alert "HUNTER LACKS RANGED WEAPON": all 3 settlers assigned hunting jobs
with NO ranged weapons — while weapons AND armour sit on the stockpile. The
colony assigns jobs but never equips for them. Fix:
1. Read the equipment alerts (GlobalWarningMessagesManager effector warnings
   include this) into ColonyAlerts.
2. Execution: force EquipGoal (known-valid goal id per handoff cheatsheet) on
   settlers whose job needs gear; ground-truth the weapon/armor assignment API
   (decompile equipment/outfit manager) so hunters take bows, fighters take
   armor.
3. Same prerequisite principle: job assignment implies equipment implies
   crafting it if none exists (bow production chain later).

## 🔥 NEXT SESSION #0a — BLUEPRINT DIAGNOSTICS READER (Ken principle:
## "the mod must be aware of everything the user is aware of")
Live proof (research table STATS panel): the game exposes per-blueprint
blockers — "Building can't be reached" / "Not enough allowed resources
(0/80 wood)" / "No settler with necessary construction skills (10)". Our
planner saw none of it. API surface found in Assembly-CSharp:
  GetRequiredSkillLevel / GetLocalizedRequiredConstructionSkillLevel,
  RefreshMissingResources + missing-resources getters,
  IsReachableByWorker, Add/Remove/GetMutualAllowedResources.
BUILD: BlueprintDiagnostics.cs — every tick, enumerate OUR pending blueprints
(buildingsById via GetBuildings), read phase/completion + the 3 blocker
states + resources needed + required skill; write to telemetry ("blueprints:"
line) AND inject into ColonyAlerts so the LLM reasons on it. ColonyBuilder
REACTS: unreachable→resite; missing resources→designate wood/haul; skill
too high→don't place / train (ties into #0 capability check below).
This closes the loop: the colony reads the same UI truth the player does.

## 🔥 NEXT SESSION #0 — WORKFORCE CAPABILITY CHECK (Ken, live finding)
Research table sat unbuilt: NOBODY in the colony has the required skill to
construct it (and settlers hauled corpses instead). The planner placed
something the workforce CANNOT build = not thinking the situation through.
1. Before ANY placement: read the blueprint's required skill/job type
   (decompile BaseBuildingBlueprint / construction job requirements) and check
   settlers' WorkerSkills (decompiled class exists) — place only if someone
   qualifies; else telemetry "nobody can build X (needs <skill> <lvl>)".
2. If unbuildable: plan the CHAIN — raise the skill (assign practice jobs),
   or pick an alternative the crew CAN build.
3. Force-construction pulse (ConstructBuildingGoal on idle settlers when our
   blueprints are pending) so hauling never starves construction.
ALSO deployed-pending: footprint+ground-pile-aware placement exclusion
(IsOnStockpile now checks neighbors + ResourceUnforbidder.AnyPileAt) — built
OK, NOT yet deployed/validated (table-on-pile fix attempt #2).

## ✅ MILESTONE (Ken confirmed live): settlers BUILDING basic_research_table
Full honest chain worked: capability-grounded pick (basic tier) → clean-site
placement (zone+pile+building exclusions) → diagnostics read → wood reaction →
manual door-litter cancel → settlers constructing. Cheat reverted (WANTS-only
until legit selection API).

## 🔥 ARCHITECTURE (Ken): THE UTILITY GRID — "convert the map grid into a math
## grid the model can understand, the way printers rasterize"
A numeric layer over the map: each cell scored on channels the game already
exposes — soil%, sunlight, dry/wet, occupancy (building/zone/pile), traversal,
distance-to-home/stockpile/water, stability. Placement = argmax over channel-
weighted scores PER BUILDING TYPE (farm wants soil+sun; house wants dry+near-
work+POOR soil; storage wants central; cellar wants hill/underground). Makes
every siting decision EXPLAINABLE ("table here: 0.91 — near stockpile, dry,
not farmland") and gives the LLM a compact spatial summary it can reason over.
This is the substrate for Village Plan v2 zoning below — build it FIRST.
✅ this session: storage-pressure expansion verified (127 loose piles → 4 zones).

## 🔥 NEXT SESSION — VILLAGE PLAN v2 (Ken: "plan for the future")
The colony builds ITEMS, not a VILLAGE. Needed: a persistent SITE PLAN.
1. ZONING: house plot (sized for target pop, e.g. 4x4 rooms + expansion strip),
   work yard (research/crafting tables TOGETHER, near stockpile, not mid-field),
   food zone (cook+cellar+future farm on best soil), commons. Anchored on home,
   persisted in BuiltState, all placement draws from its zones.
2. HOUSE v2: current 2-room 2x2 w/ partial floors is a shack. Bigger rooms,
   full floors BEFORE beds, verified door construction, upgrade path (v2
   replaces v1 — requires DESTRUCTION, below).
3. DESTRUCTION (Ken): the colony must be able to TEAR DOWN old for new —
   ground-truth the deconstruct order API (BuildingDeconstructed / deconstruct
   job exists in decompiled surface), then: blueprint-litter cleanup pass +
   planned upgrades (demolish shack -> build proper house).
4. Save at end of every dev cycle (stop blueprint-litter churn permanently).

## 🎯 DESIGN BAR (Ken, this session): reference docs are the finished-mod spec
`C:\Users\Moshi\Desktop\X4 AI Influence\AI Influence - Systems - Going Medieval`
(00-Overview … 11-Gameplay Examples; incl. 06 Romance & Marriage = lineages:
courtship → children, father's surname, LLM-written family backstories.)
Strategic layer must PLAN AHEAD, not react ("not a child reacting to emotions"):
- House v2: bigger rooms (≥4x4 interior), space for future furniture/settlers,
  verified doors, floors-before-beds, expansion margin around footprint.
- Siting v2: use per-cell game metrics (Soil%, Sunlight, Traversal, Stability)
  — do NOT build the house on 100% soil (reserve for future fields).
- Stockpiles v2: multiple specialized zones w/ filters (food near cookfire,
  materials near construction, armory) instead of one mixed pile.
- NPC lens: "what would I WANT if I lived here" drives colony decisions, on
  top of per-settler mood/stats via Player2.

# Session 2026-07-07 (Cowork, opus) — summary

✅✅ STOCKPILE ACTUALLY BUILDS NOW (game-verified, not a DB flag). Two real
bugs found by grounding in decompiled game code, both fixed:
  1. Blueprint sourcing: old code copied an EXISTING zone's blueprint → failed
     on a fresh colony. Now sources from Repository<StockpileRepository,
     Stockpile>.Instance.GetFirst() (how the game does it), via DeclaredOnly
     hierarchy-walk reflection (FlattenHierarchy threw "Ambiguous match" on the
     CRTP generic base).
  2. Coordinate convention: MeshAreaMaker.GetMeshArea does y = start.y /
     World.MapBlockHeight(=3). SpawnStockpile expects start/end.y in WORLD units
     (level*3); we passed the level directly → wrong layer → empty mesh → no
     zone. Fixed: pass ay*MapBlockHeight to SpawnStockpile; keep level-y for
     CanPlaceStockpile/StockpileExists.
  LIVE PROOF (Libury order #43): "ok: stockpile placed level=(118,6,137)
  worldY=18 registeredCells=4/4". The game's OWN alerts changed: "Nowhere to
  store resources" and "Settlers are starving" both CLEARED; settlers began
  eating. This is the first genuinely-functional structure the mod has placed.
  Honest note: earlier "proofs" were StockpileExists grid flags, not visible/
  functional zones — corrected.
  NEXT: (a) actual BUILDINGS via BuildingsManagerMain/ConstructionController
  (place blueprint → settlers haul+construct); (b) Strategic Model to trigger
  builds from colony alerts organically (task 11).



Shipped + verified this session:
- ✅ LLM cost: DecisionInterval 10s→180s + migration guard (min 60s). Was
  ~1400 calls/hr; now ~18x fewer. Event triggers still immediate. (deployed)
- ✅ Stockpile placement B3 root-caused + FIXED: the bug was world→grid
  conversion. Ground truth via new /api/dev/decompile (ilspycmd): the real
  conversion is VillageMap.GetNodeByWorldPos (GridUtils.GetGridPosition);
  MapNode.Position is canonical. SpawnStockpile is the complete entry point
  (validates via CanPlaceStockpile → GetMeshArea → Save→AddAreaToTheWorld).
  StockpilePlacer now anchors on the settler's real node + utility-scores cells
  for the most-open ground. LIVE: order #15 placed a game-registered stockpile
  (registeredCells>0). (deployed)
- ✅ Player2 build_stockpile wired: LLMClient tool + scored/whitelisted decision
  + DecisionExecutor case + ForceProcessSettler trigger. FINDING: in famine or
  fresh colonies Player2 rationally picks eat/continue_job, not build — the
  build decision belongs in the STRATEGIC layer (resource ledger), per the RTS
  reference docs. That is the correct next architecture (Read Model → Strategic
  Model → Validator/Actuator).
- ✅ Character data model FULLY GROUNDED (decompile + live /api/dev/dump_character):
  HumanoidInstance(NSMedieval.State).Skills=WorkerSkills; SkillsOrdered=
  List<WorkerSkill>{Id:SkillType, Level, Experience, GetGoalPreferenceLevel()=
  PASSION}; Perks List; GetCharacterInfo()→CharacterInfoBase{FirstName,LastName,
  Height,GetWeight(),Age(base)}. Extractor + payload + Python upsert extended
  for skill passions + height + weight (idempotent column migration). Server
  upsert VERIFIED via manual POST (height/weight/passions persist correctly).

✅ RESOLVED — the telemetry pipeline. TWO stacked bugs, both fixed + verified:
  (1) MemoryManager.PostJson early-returned when the _isServerOffline circuit
  breaker was latched, silently DROPPING every write. Fix: always attempt the
  write; a healthy write clears the flag (breaker is now log-throttle only).
  (2) upsert_character_sheet threw IntegrityError on a duplicate
  character_sheet_mood_modifiers (kind,label) which rolled back the ENTIRE
  sheet transaction (that's why NO real sheet ever persisted). Fix: INSERT OR
  REPLACE on mood_m
## 2026-07-07 — Strategic Model (autonomous village builder)
Built `ColonyBuilder.cs`: deterministic three-layer actuator (Read Model → Strategic → Validator/Actuator).
- Census from GAME TRUTH: `StockpilePlacer.CountStockpilesInWorld()` (StockpileManager.Stockpiles) + `CountBuildings(id)` (BuildingsManagerMain.GetBuildingsCount — placed blueprints + built).
- Decide: one build/tick, priority STORAGE (stockpiles==0) → BED (beds<pop). Self-limits vs census; per-session caps + 120s failure backoff as belt-and-suspenders.
- Act: verified TryPlaceStockpileNear / deployed TryPlaceBuildingNear (game CanPlace* rejects water/invalid terrain).
- Hooked in Plugin.ProcessNPCs after TryStartColonyInfluence; gated on AutonomyManager.IsFullAutonomyEnabled (EnableFullAutonomy config, default false).
- Build: SUCCESS 0 errors. Deploy skipped (game running). STATUS ◐ built+compiles, NOT live-verified — game not foreground-able while user's browser holds focus (game pauses in background).
- To verify next: EnableFullAutonomy=true, bring GM to foreground, watch census → stockpile (verified) then bed blueprint appears on valid ground and settlers construct it. Bed placer (task 12) still needs first live visual confirmation.

## 2026-07-07 — Autonomous survival + build loop (settlers gather/cook/eat/build)
GROUND TRUTH (decompiled): ResourcePileInstance.IsForbidden(set); ResourcePileManager.AllPileInstances;
BuildingPlacementManager.SpawnBlueprint = INTERACTIVE cursor path (bug) → replaced with game's own
no-cursor commit: SpawnFromPool→CreateAndReturnBuildingInstance→CacheBuildingInstance(fires
ConstructionController.BlueprintPlaced)→ObjectPlacedOnMap (from decompiled SpawnEnemyBuilding+CacheBuildingInstance).
PlantResourceManager.GetPlant(Vec3Int)+PlantMapResourceInstance.SetCurrentOrder(OrderType.Chopping)=designate tree for wood.

NEW MODULES:
- ResourceUnforbidder: allow all forbidden ground piles (settlers can haul/eat). VERIFIED: 0 forbidden/197 piles, starving alert cleared.
- WoodGatherer: designate nearby trees OrderType.Chopping (bounded, session cap 40). VERIFIED: 4 settlers "Cutting", piles 101→197.
- HouseBuilder: staged NxN house (perimeter walls→door→roof) via exact-cell CommitPlayerBlueprint. VERIFIED: walls+door placed & built (Maud "Constructing"). Roof: place at ay+1 (fix), needs final live confirm.
- StockpilePlacer.CommitPlayerBlueprint / TryPlaceBuildingAt / DumpBuildingIds / Count* census helpers.
- ColonyBuilder ordered plan: unforbid → gather wood → stockpile → camp_fire(cook) → beds(per pop) → house. Heartbeat + writes validation/colony_status.txt (reliable telemetry vs flooded log).
- Plugin: force EnableFullAutonomy on at startup (stale cfg pinned it off); MenuIntegration exposes autonomy toggle + fixes Decision Interval slider (was clamping 300→60).

KEY IDS: camp_fire (cook), hay_sleeping_spot (bed), wood_wall_element/wood_door/wood_roof_whole (house). 780 ids in validation/building_ids.txt.
GOTCHA: kill→must poll status until game_running=false (+3s) BEFORE launch, else stale DLL keeps running. In-game log floods (NPC pipeline) → use colony_status.txt / building_ids.txt files.
REMAINING: roof live-confirm (ay+1); cooking a MEAL (station built, needs raw-food+cook job verify); LLM zone-hinted planner (task 15) still deterministic.
_devops.py endpoints (/api/dev/build,
  /api/dev/game/launch|kill|restart, /api/dev/status) + game stream
  (/api/game/screen + /api/game/input) = full edit→build→deploy→relaunch→verify
  cycle over HTTP, no desktop automation. Runbook in QUICKSTART.md.

## P2 RC follow-ups (found during live proof)

- ✅ RESOLVED 2026-07-06: settler identity stability. GameBridge now derives
  stable IDs (gm_ + sha1(name)[:12]) when the game exposes no persistent id;
  /api/dev/merge_identities migrated Wolferlow (3 settlers × 6-7 session IDs
  each → canonical). LIVE PROOF across a full game restart: Alison recalled
  "you did accuse me of withholding food" from the previous session on
  gm_936935a5bc89. Residual stale npcs-table rows are cosmetic — cleanup pass
  deferred.
- ✅ Voice trait sanitization (lexical-only trait words) — fixed server-side.
- ✅ Enter-to-send investigated: DialogueManager already handles KeyCode.Return
  in IMGUI; the miss was a pydirectinput injection artifact, not a bug.
- 🟡 Name-collision caveat: two settlers with identical display names in one
  save would share a canonical ID; add a disambiguator if it ever occurs.

Actionable, ordered backlog derived from the blueprint documents in
`C:\Users\Moshi\Desktop\X4 AI Influence\AI Influence - Systems - Going Medieval`
and the locked priority order in `PLAN.md`. Deferrals live in `DEFERRED_BACKLOG.md`.

Legend: ✅ done+verified · ◐ implemented, not fully verified · ☐ not started · 🖥 host-gated (needs dotnet build / game runtime on host)

## P2 — AI Dialogues (finish)

- ✅ Slice 1: dialogue state, claims, contradictions, barter intents, prompt integration (Codex, 2026-07-05)
- ☐ Slice 2a: contradiction matcher v2 — negation-aware claim-vs-claim matching, self-contradiction detection, remove test-specific hardcoded markers
- ☐ Slice 2b: deterministic trust rules — promise kept/broken, repeat-contradiction escalation, per-exchange clamping, trust_events audit log
- ☐ Slice 2c: barter intent resolution endpoint (`fulfilled|broken|declined`) wired to trust rules
- ☐ Slice 2d: voice authoring — persistent voice profile built from traits+profession+backstory, medieval register cues
- ☐ Slice 2e: dashboard — trust-event timeline card, barter resolve buttons
- 🖥 Prove floating in-game dialogue UI live (game running, user or Steam cycle)
- 🖥 Conflict-escalation hooks (dialogue → hostility) — needs C# + P10 incident schema

## P3 — AI Actions

- ✅ `ai_orders` table + persistence
- ✅ NL order parser → bounded plans (proven live: "Prioritize mining, then return to work")
- ✅ Multi-step order plans with per-step status + notes
- ✅ `/api/orders` endpoints + dashboard order inspection
- ✅ C# OrderExecutor: polls queue, maps verbs onto DecisionExecutor, reports
  back. LIVE PROOF 2026-07-06: order #1 set Alison's Mine priority to 1
  (visible in JOBS panel); order #2 ran the construction pipeline.
- ◐ move_to/patrol/attack are approximations (explore/defend); hold=rest;
  follow_player unsupported. True pathing needs MoveTo(Vector3) wiring (hook
  exists in ExecuteDraft) + coordinate targets. 🖥
- ☐ Dialogue → order bridge (barter intent "order" type auto-issues)

## NPC Self-Building Framework (see NPC_BUILDING_FRAMEWORK.md)

- ✅ B1 proposals backend: construction_proposals + propose/approve endpoints,
  typed-memory tie-in
- ✅ B2 approval → ai_order → executor runs prioritize_construction +
  build_special. LIVE: proposal #1 (research table, Estrild) → order #2
  completed both steps in-game.
- ◐ B3 stockpile placement — API captured live via GameApiScanner
  (validation/api_surface.json, 116 types): StockpileManager.SpawnStockpile/
  CanPlaceStockpile/StockpileExists. StockpilePlacer.cs implements spiral
  placement. BLOCKED on a grounded finding (2026-07-06 probes):
  Vec3Int round-trip is byte-perfect (equal=True), yet the MonoSingleton
  Instance returns exists=False AT AN EXISTING STOCKPILE'S OWN Start cell —
  the Instance being queried is NOT the live map's manager (its Stockpiles
  enumeration likely yields profile TEMPLATES; note SpawnProfileStockpiles).
  NEXT STEP: UnityEngine.Object.FindObjectsOfType(StockpileManager) and probe
  EACH candidate at anchor.Positions cells; use the instance where exists=True,
  then rerun orders 4/5. Alternative ground truth: ILSpy CanPlaceStockpile.
- ◐ B3 UPDATE 2026-07-06 (Cowork session 2, opus): implemented BOTH next steps
  and RULED OUT the coordinate/anchor theory with live in-game probes.
  StockpilePlacer now (a) FindObjectsOfType(StockpileManager)+SelfVerifies to
  pick the live manager, (b) anchors on the SETTLER'S live cell, (c) scans a
  29x29x7 volume. Live result (order #11, Alison gm_936935a5bc89, DLL fe30268e):
    rawPos=(129.11,15.21,69.60) grid=(129,15,70) mgr=StockpileManager
    selfVerify=False looseHitsInVolume=0 firstLooseHit=none
    settlerCell=(129,15,70) loose=False strict=False exists=False
    below/below2 also all False ;; templateStarts=[[X:124,Y:15,Z:78]]
  CONCLUSIONS: (1) coords are NOT wrong — settler Y=15 matches template Y=15,
  round-trip byte-perfect; (2) anchor is NOT the bug — CanPlaceStockpile(loose)
  is False for EVERY cell in the volume incl. the settler's own valid ground;
  (3) selfVerify=False => the Instance/first FindObjectsOfType candidate cannot
  confirm its own template's existence. So the true blocker is EITHER the held
  StockpileManager is a non-live/template manager, OR CanPlaceStockpile has an
  unmet precondition invisible to reflection. RESOLUTION REQUIRES decompiling
  CanPlaceStockpile (ILSpy on E:\...\Going Medieval_Data\Managed\Assembly-CSharp.dll)
  — do NOT attempt another blind reflection build. RECOMMENDED PIVOT: "NPCs
  build their own BUILDINGS" (Ken's actual ask) is better served by
  BaseBuildingManager.CanPlace(blueprint,gridPos,angle,silentLogs) + the proven
  TryTriggerBuild path than by stockpile ZONES; validate buildings first, treat
  stockpile zones as a separate follow-up gated on the decompile.
- Dev iteration hooks added this session: POST /api/dev/place_test (inject a
  single-action order for live C# testing) and GET /api/dev/last_order (clean
  query-less read; the Chrome content filter blocks ?query= JSON reads).
- ◐ B3 DIRECT-SPAWN PROBE 2026-07-06 (order #12, DLL 641bd0e4) — settles the
  bypass question. Result:
    PROBE mgrs=1; mgr0 sp=1 selfV=False start=[X:124,Y:15,Z:78] existAtStart=False;
    settlerGrid=(129,15,70) usingLiveMgr=False; blueprint=True;
    DIRECTSPAWN@(129,15,70)=invoked before=1 after=2 existsAfter=False
  DECISIVE CONCLUSIONS:
  1. mgrs=1 => there is exactly ONE StockpileManager (the singleton IS the live
     manager). The "wrong instance" theory is DEAD.
  2. The pre-existing sp=1 has existAtStart=False => it is a PROFILE TEMPLATE
     (SpawnProfileStockpiles), never placed on the map.
  3. SpawnStockpile DID NOT THROW and grew the collection (before=1 -> after=2),
     but existsAfter=False AND no functional zone rendered in-world (confirmed by
     stream screenshot). So raw SpawnStockpile is an INCOMPLETE placement: it
     adds to the Stockpiles collection but never registers the cells in the
     spatial existence grid (StockpileSpaceDataDictionary) or spawns the
     StockpileView. CanPlaceStockpile==False everywhere is the same missing
     precondition — these public methods are FRAGMENTS of the game's internal
     drag-place pipeline (UI: CanPlace -> Spawn -> grid register -> view),
     not self-sufficient entry points.
  IMPLICATION: placing a functional STOCKPILE ZONE via reflection requires
  replicating the game's full placement controller flow -> needs an ILSpy
  decompile of the stockpile placement command/controller (the code the UI
  runs on drag-release). BUILDINGS are unaffected: TryTriggerBuild already
  places functional blueprints (research table proven). So the NPC self-build
  DECISION layer should be built against the working BUILDING path first;
  stockpile zones are gated on the decompile.
  HANDOFF to research sessions (spatial/what/where/why): the "can/place"
  primitive is the blocker, not the planner. Two sub-problems: (a) reverse the
  stockpile placement controller (ILSpy) so we can place zones; (b) design the
  decision loop (GOAP/HTN + utility for WHAT, influence-map/buildability tile
  scoring for WHERE) on top of readable cell data (level/soil/stability/
  occupancy/reachability) + the proven TryTriggerBuild for buildings.
- ✅ B3 GROUND TRUTH 2026-07-06 (decompiled StockpileManager via new
  /api/dev/decompile -> validation/decompiled/). CORRECTS the earlier
  "SpawnStockpile is incomplete" conclusion — that was WRONG.
  REAL FINDINGS from the source:
  * SpawnStockpile(blueprint,start,end) is COMPLETE and correct: it builds a
    mesh area via meshAreaMaker.GetMeshArea(start,end, CanPlaceStockpile), and
    IFF that area is non-zero it instantiates the StockpileView, creates the
    StockpileInstance, Save()s it (AddAreaToTheWorld + RegisterStorage) and
    fires StockpileController.StockpilePlaced. No missing step. There IS NO
    separate placement controller to reverse for basic placement — SpawnStockpile
    is the entry point. (The earlier probe's before=1->after=2 was the
    SpawnProfileStockpiles template load, NOT our spawn; our spawn body was
    skipped because meshArea came back all-zero.)
  * The ENTIRE blocker is CanPlaceStockpile returning false at our cells. Its
    real preconditions (now readable): node = Map.GetNode(v) must be non-null
    and node.IsVoxelAir(); HasSomethingToStandOn(v) = a finished Floor at v OR
    GroundManager.GroundExists(v+down) OR a finished vertical-stability carrier
    below; not already Stockpile (unless ignore flag); plus world-object checks.
  * ROOT CAUSE of "false everywhere": we anchored on
    Mathf.RoundToInt(settler.transform.position) and tested naive computed
    Vec3Ints. The game maps grid<->world with an offset (renders a cell at
    (Vector3)cell + (-0.5,0.03,-0.5)) and converts via position.ToGridXZ(); Y
    uses World.MapBlockHeight in the expand/load paths. We were querying the
    WRONG NODES. FIX: get the settler's real MapNode and iterate actual
    Map nodes (node.Position is the canonical grid coord) testing
    CanPlaceStockpile, then SpawnStockpile at a valid node — never hand-compute
    grid coords again.
  * NEXT (small, grounded): decompile the Vec3 extension ToGridXZ + World
    (MapBlockHeight) + VillageMap.GetNode/GetNodeFromWorld to wire the settler's
    world pos to its node, OR read the settler's own grid Position via the
    existing GameBridge settler reflection. Then rerun the settler-anchored
    placement using real nodes. This is now a coordinate-plumbing fix, not a
    mystery.
- Tooling added: GET/POST /api/dev/decompile (installs pinned ilspycmd
  8.2.0.7535 as a dotnet global tool, decompiles named game types into
  validation/decompiled/). This is the Going Medieval analog of grounding
  against a proven reference — use it BEFORE guessing at any game API again.
  NOTE: validation/**/*.cs is now Compile-Removed in the csproj — decompiled
  reference source is NEVER compiled (ilspycmd's version-nag footer + single
  quotes broke the build once; fixed).
- ✅ B3 COORDINATE FIX LANDED + PARTIALLY VERIFIED 2026-07-06 (DLL 54b031cd).
  StockpilePlacer now anchors via VillageMap.GetNodeByWorldPos(settler world
  pos) and iterates real MapNode.Position cells (the game's own grid space).
  LIVE (order #13, Alison): "ok: stockpile 2x2 at (128,5,69) [node-anchored]"
  — CanPlaceStockpile returned TRUE and SpawnStockpile executed its full body.
  This is DECISIVE vs the old code, which found ZERO placeable cells anywhere
  (the RoundToInt anchor produced Y=15; the real settler node is Y=5). Root
  cause = wrong world->grid conversion, now fixed.
  OPEN (minor, not yet confirmed): existsAfter=False on the immediate probe.
  Ground truth (decompiled StockpileInstance): Positions = validated meshArea
  cells; AddAreaToTheWorld flags those nodes synchronously. The false is
  almost certainly because the probe checked the ANCHOR CORNER (128,5,69),
  which may lie outside the final validated mesh — NOT a placement failure.
  TO CONFIRM: (a) probe StockpileExists across the placed mesh (not just the
  anchor) and/or (b) eyeball the stockpile zone in-world near the settler.
  Visual confirm was deferred: the game pauses its update loop when
  backgrounded (Ken foregrounded the X4 window), so the executor stopped
  polling order #14 — resume by foregrounding GM. AVOID force_focus while Ken
  is actively using the desktop; /api/game/screen?force_focus=true raised his
  WinRAR/GitHub window, not GM.
  NEXT (small): change the place path to report existsAfter across the actual
  placed cells; then one live run confirms in-world render -> B3 DONE.
- ☐ B3b: same pattern for farm plots / mine designations / room blueprints
  once the live manager instance is resolved.
- ☐ B4 autonomous initiative loop (pressure-driven propose→place→build→memory)

## Colony planner v1 (Ken: "planning houses, mining, stockpiles, food")

- ✅ POST /api/construction/plan: needs→proposals→orders with role affinity,
  idempotency, auto_approve. LIVE RUN: farm plot, food stockpile, stockpile
  zone, wooden bed, mine shaft proposals + harvest/mining work orders; 5/7
  orders executed in-game (job priorities switched, build proposals fired);
  the 2 stockpile orders blocked on B3 above.
- ✅ Planner only assigns live-seen settlers (stale-profile "Helewisa Pevrel"
  ghost caused first-run failures — fixed with last_seen filter).
- 🟡 avg_hunger scale is 0-100 (not 0-1); threshold currently fires always —
  recalibrate planner thresholds against real colony_events ranges.
- 🟡 Live profile roles are 'unemployed' so role-affinity assignment falls
  back to first settler — populate roles from character sheets during profiling.

## New requirement (Ken, 2026-07-06): Birth system

- Man+woman relationship above threshold → baby. Builds on romance_states
  (courting→betrothed→married + intimacy). Going Medieval has NO native child
  settlers, so v1 design: conception check on married pairs (intimacy+time) →
  pregnancy timer → birth world event + relationship milestone memories →
  child tracked as family entity; joins as a settler via the game's arrival
  events when "grown" (or immediately, reskinned, if arrival API allows).
  Slice into P7 after B3.

## Iteration-loop gotchas (hard-won, do not relearn)

- Game window position changes between boots — /api/game/input relative
  coords are window-relative so clicks still land; don't cache absolutes.
- GameApiScanner's reflection sweep froze the game on load — now runs off the
  main thread (fixed 2026-07-06, ships with next build).
- Steam/Unity boot takes ~60-90s to main menu; RESUME → ~15s story screen →
  "Click to continue" → ~30s to settlers. Executor waits (settlers.Count==0
  guard) instead of failing orders during load.

## P4 — Additional Systems (world grounding)

- ☐ Entity tables: settlers, factions, settlements, regions, goods + mention extraction from dialogue exchanges
- ☐ Visit/location history schema
- ☐ Faction/standing schema (own/allied/enemy/neutral)
- ☐ Recruitment-opportunity detection (advisory/dashboard output)
- ☐ Dashboard entities panel

## P5 — Dynamic World Events

- ☐ `world_events` schema: type, origin, affected entities, rumor state, confidence, lifecycle
- ☐ Propagation: events → NPC event_knowledge → typed memories → dialogue prompt context
- ☐ Event evolution (update/escalate/resolve instead of one-shot rows)
- ☐ Dashboard event timeline
- ☐ Adviser prompt feed

## P6 — AI Diplomacy

- ☐ Faction relation state: war/peace, alliances (shatter on war), trade pacts (max 2), tribute, reparations, war fatigue, banishment/pardon
- ☐ Diplomacy rounds (turn-based, delayed) as endpoint-triggered first, scheduled later
- ☐ AI proclamations persisted as faction events feeding P5
- ☐ Dashboard diplomacy panel

## P7-P10 — Later systems (backend-first)

- ☐ P7 Romance: intimacy distinct from trust, decay, NPC-initiated courtship events
- ☐ P8 Death History: 50+ interaction gate, milestone-based life story, decline option
- ☐ P9 Disease: infection/immunity/quarantine/treatment state machine, seasonal odds, outbreak → world event
- ☐ P10 Combat: incident classification (aggressor/defender/witnesses/casualties), companion stance, aftermath events
- 🖥 All in-game surfacing for P7-P10

## P11 — Scenario acceptance suite

- ☐ Convert the 5 gameplay examples into reproducible demo scripts + pass/fail checklists

## Standing gates (every slice)

- `python -m py_compile dashboard/dashboard_server.py` + all selftests PASS
- 🖥 `dotnet build --configuration Release -t:Rebuild` when C# touched
- Dashboard shows living proof of every new table/endpoint
- Canonical settler IDs preserved in all DB writes

---

## AAR — #22 Roofs CLOSED (2026-07-10 night session)

**Outcome: the house at (16,118) lvl 7 on Cockhamsted has a real pitched roof, visually confirmed in-game, and the game created "Room: Chamber" (enclosure test only passes roofed).**

What actually happened (three root causes, peeled in order):
1. **CanPlaceRoof strip geometry** (prior session): support only checked at strip endpoints → v3 wall-to-wall strips. Correct but NOT sufficient.
2. **CreateRoofs collapses the view to ONE cell** (BuildingPlacementManager.CreateRoofs:859-866): via SpawnRoofAutoTesting, `roofPositionView` is rebuilt at `raycastGridStart` only, so CanPlaceRoof effectively tests the single gridPos cell — strip geometry was never the live check.
3. **Phantom blueprints from the no-autoconstruct era**: every roof cell already held an old roof blueprint → CanPlaceRoof clause `GetFirstBuilding(~(Default|Floor|Rug), cell)` rejected ALL new placements. Clause-level diag (`RoofCellDiag`) proved it live: `support=Y BLOCKER@cell=Y`.

Fixes shipped:
- `StockpilePlacer.RoofCellDiag(x,y,z)` — replicates every CanPlaceRoof clause, reports WHICH fails (runtime enum resolution, no guessed names).
- `StockpilePlacer.KickRoofRow(...)` — sweeps a roof row, counts existing roof instances (any phase), calls the game's own `BaseBuildingInstance.AutoConstructSequence()` on unqueued blueprints (the exact call AutoConstructBuildInOrder:646 makes).
- HouseBuilder roof v4 — row done if all cells hold roof instances; else strip; **honest counting** (only `ok:`/verified rows persist to BuiltState; rejected rows retry ≤4 then skip WITHOUT persisting).

Negative path proven live: REJECTED strips reported with clause diag; give-up path logged; sidecar stayed 0 through rejections (the old code persisted 8/8 attempted-not-placed — that lie is what closed the house prematurely for weeks).

Round-trip: roof built by settlers in a prior session, survived save→reload, adoption walked the plan and placed nothing duplicate (census stable).

Ground-truth lesson for the ledger: **"invoked" is not "placed"; "placed" is not "built"; "built" is not "seen".** All four were different states in this bug.

Live coherence observations for #32 (next): settlers sleep/eat at the ORIGINAL stockpile ~100 tiles from the roofed house; alerts showed "Settlers are starving", "Will is rebellious (reason: Damp)" while their dry roofed house stands empty across the map. The colony anchor must move to the built shelter (or the shelter must be built at the anchor).

---

## AAR — #32 Coherence slice 1: MOVE-IN (2026-07-11)

**Outcome: the colony now LIVES in the house it built — validated on screen.**

Shipped:
- `ColonyHome.MoveTo(x,y,z,reason)` — re-anchors + persists home.
- `HouseBuilder.HouseCenter` — move-in target from the built plan.
- `StockpilePlacer.AnyStockpileNear` / `AnyBuildingNear(id,...)` — positional census (storage/cook must exist AT HOME, not merely somewhere on the map). AnyBuildingNear walks the manager's public `PositionInstanceListDictionary` (BuildingsManagerMain:128).
- ColonyBuilder: move-in trigger (distance-gated >12 tiles, idempotent), STORAGE-AT-HOME + COOKFIRE-AT-HOME gates, Priority 3.5 move-in beds (world-truth idempotent).

Live validation (Cockhamsted):
- telemetry: "home MOVED to (17,7,121) — roofed house complete — the colony moves in"
- STORAGE-AT-HOME: stockpile zone placed by the house (census 2→3)
- COOKFIRE-AT-HOME: camp_fire blueprint committed at (20,7,121), beside the front door; BlueprintDiagnostics immediately flagged it NO-RESOURCES and WoodGatherer designated 4 trees near the NEW home — the reaction chain re-anchored correctly
- EYES ON SCREEN: Helewys asleep INSIDE the roofed house (pet too), Will at the doorstep at 02h, cutting happening next to the house
- SAVED: Start.sav overwrite verified by timestamp (7/11 12:04:32 AM)
- Move-in beds: silent fall-through — beds already existed on interior cells from earlier sessions (gate counts world truth, correctly did nothing)

Still open in #32 (next slices): round-trip proof of restored home on next reload; farm remains at the old camp; old-camp stockpile goods migration/cleanup (StockpileZoner now marks the far camp zones as refuse targets — observe before forcing anything); "roofs 7/8" display denominator (rows vs interior cells) is cosmetic and should read 7/7.

Chronicle fodder observed (for #27 Death History / doc 08): "The Death of Margaria Jolland (died aged 47)", Donald's rescue event ("would be burned alive by the Disciples"). These are exactly the events the mod should be writing into settler memory.

**#32 slice 1 round-trip CLOSED (00:11):** reload → "home RESTORED at (17,7,121) from persisted state", census stable (sp=0 cook=0 bed=0 re-placements — idempotent), camp_fire blueprint now "all buildable" (wood chain re-anchored). Full workflow honored: implement → live validate → eyes-on-screen → save (timestamp-verified) → reload round-trip.

---

## SESSION LEDGER — coherence audit + Cockhamsted wipe (2026-07-11 ~01:30)

**Ken's audit was correct and my status report was wrong.** He watched poop stored with food, undifferentiated stockpiles, a shack-sized house. Root finding: StockpileZoner v2 acted on GUESSED group ids ("waste"/"carcass") → HasGroupIdentifierEnabled(wrongId)=false → silent no-op reporting "already clean" for weeks. I reported it as working without ever opening a zone's allow-list on screen. Rule reaffirmed: coherence claims require eyes on the OUTCOME, and NEVER act on guessed ids (HourType lesson, now twice).

**Zoner v3 shipped (validation in progress):** data-driven classification from live ResourceRepository (Category flags + GroupIdentifier + id), role zones (PANTRY nearest / MATERIALS middle / REFUSE farthest), self-verifying spot-checks via the filter's own IsBlueprintAllowed — silent no-ops now impossible. First live run correctly REFUSED to act: repository enumeration had hit a 79-weapon sub-cache (early-break bug, category enum members are Ctg*-prefixed). v3.1 aggregates all fields, dedupes by id — BUILT, deploy pending next cycle.

**COCKHAMSTED WIPED ("ALL IS LOST", day ~13, winter).** Likely chain: the Donald/Disciples story event ("he may be pursued") was blind-clicked through by our own driving loop; colony had 0 weapons (alert up for days) + 0 stored nutrition. The game rolled a "new life" colony (Llangefni) on the same map — loaded as the new test bed. Cockhamsted saves retained (Start.sav 12:04AM alive + autosaves to 1:16AM) for forensics/#27.

**#34 EVENT INTERACTOR registered + API ground-truthed** (validation/event_api.txt): GameEventSystem.RunningEvents/IsBlockingEventRunning; GameEventInstance.GetDialogContent/GetEventTitle/GetEventInfo/ForceEnd; GameEventSystemController.EventOptionChosen(instance, optionIndex) = the write path; Raid lifecycle delegates. Ken's directive: the engine must interact with game-injected events (accept/deny NPCs etc.) — LLM-as-player decides, never blind-click. This event choice IS the player role the whole mod exists to fill.

---

## AAR — zoner v3 + camp coherence + EventInteractor v1 (2026-07-11 ~02:05, Llangefni)

**Shipped and live-validated this cycle:**
1. **StockpileZoner v3 VERIFIED**: `4 zones roled (food=50 waste=26 ids) | nearest [redcurrant=Y dungbrick=n] | dump [redcurrant=n dungbrick=Y]` — pantry legally rejects waste, dump rejects food, confirmed by the game's own IsBlueprintAllowed. Poop-with-food is fixed at the filter level. (v3.0 caught its own bad census — 79-weapon sub-cache from an early-break — and refused to act; v3.1 aggregates all repo fields.) Zone filters persisted via manual save (Start.sav 2:04:40 AM, timestamp-verified).
2. **Site leash + camp-moves-first (#32 slice 2, Ken: "house a mile from their stockpile")**: SiteScorer.MaxLeash=35 hard cap; ColonyBuilder moves the CAMP to the leader's chosen site BEFORE construction. Live race found+fixed on reload: PlanOnce ran before home restore (homeX=-1 → no leash → 84-tile site → camp ping-pong, orphan campfire at (130,100)). Fix: Establish BEFORE PlanOnce; PlanOnce and camp-mover both gated on !BuiltState.HouseComplete (persisted truth, not the in-memory flag). Reload-proven: home RESTORED, siteplan idle, cookfire committed at (159,5,179) beside the house.
3. **EventInteractor v1 DEPLOYED** (#34): reads GameEventSystem.RunningEvents → BuildDialogContent (title/body/options), leader LLM answers by index (task="story_event", colony reality in prompt), applied main-thread via GameEventSystemController.EventOptionChosen. Discipline: LLM decides or NOBODY does — "NEEDS PLAYER" surfaces in telemetry, no guessed defaults. Negative path verified live ("events: (no events)"); POSITIVE PATH PENDING a real event firing.
4. **Stale-DLL gotcha refined**: deploy can land on disk AFTER the game loads the old DLL into memory (dll_in_sync lies about the RUNNING code). New protocol: kill → build+deploy → verify deployed mtime is fresh (<120s) → ONLY then launch. Absence of new telemetry lines = the tell.

**Cleanup queued**: orphan campfire blueprint at (130,100) from the race (cancel-blueprints-beyond-WorkRadius slice); fletchers_table NO-SKILLED-WORKER (capability chain); Llangefni old-camp stockpile goods migration.

**Watch item**: budget suppressed=64 spike in one session earlier — NPC dialogue churn still the biggest call consumer (#23 batching).
