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

# TASK #25 CLOSE ◐ — "plans/laws persistence: gm_plans.py + /api/plan|/api/laws wired"
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
