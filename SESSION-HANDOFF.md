# SESSION-HANDOFF — overwritten 2026-07-13 ~07:00 (Unit 1 roofs closed + HONEST reckoning)

## 🔧 THREE BUGS FIXED THIS SESSION (2026-07-13 ~12:45) — read this FIRST
Real development this session (all compile-clean; #1-2 deployed+live, #3 built but NOT yet deployed):
1. PLACEMENT DEADLOCK (StockpilePlacer.TryPlaceBuildingNear): resume saved only radius, not cell → one slow
   marsh cell (CanPlace ~2.3s) re-tried forever, colony stuck 5 days. FIX: per-cell flat-index resume. PROVEN
   advancing in log (radius 1→2, cell 3→10). DEPLOYED.
2. BUILD SERIALIZATION (ColonyBuilder): cook/butcher/beds `return`ed every tick until placed, starving the
   house build → everyone slept in marsh. FIX: non-blocking + bounded (MaxBlockingPasses=3) fall-through so the
   HOUSE (dry platform) builds first on wet maps. DEPLOYED.
3. FOOD-PRIORITY INVERSION (ColonyBuilder): farm was Priority 4.7 (after house/plan-exec/research/weapons) so
   plan-exec starved it → farm NEVER placed → colony starved with shelter built (Dowsby autumn-day-8: farm idle,
   stored nutrition 0, a death). FIX: farm moved to Priority 1.5 (right after storage). BUILT, NOT yet deployed
   (running game still has prior DLL). Deploy with kill→build{deploy:true}→launch.
PROVEN eyes-on: the mod builds real houses WITH FULL ROOFS on dry VALLEY terrain (Dowsby, day6_now.png). The
recurring blocker was never building — it's FOOD (every bed starved). #3 targets it but is UNVALIDATED.
TEST-BED TRAP (banked): both Edenham AND Tranent are map_type_WETLAND (hardest terrain). "Start.sav" names are
UNRELIABLE (Edenham/Start=day1 spring; Dowsby/Start=autumn day8). A clean Gate-1 bed = NEW GAME on a VALLEY
biome, spring start. Valley fresh saves to try: Thorney, Dunningworth, Tenby (verify each is actually day1).
NEXT UNIT: deploy #3, start a New Game (valley/spring), validate the colony plants its farm early + holds food
above the 1-day buffer over 3 days (Gate 1). Then the remaining food sub-units (farm scaling, crisis-forage, cellar).
COMMIT POINT: `fix(colony): per-cell placement resume, wet-map build order, food-security-first priority`.

## 🌱 GATE 1 PROPER RUN LIVE (2026-07-13 ~07:45, AUTONOMOUS) — earlier
Pivoted off the broken Tranent winter save. RECONCILE win: Ken already had a FRESH save — EDENHAM/Start.sav
(Spring day 1, 4 settlers, wetland, 600 wood). Killed Tranent, relaunched, drove the Load menu eyes-on, loaded
Edenham. VERIFIED live: mod planning coherently on the fresh save — placed 2 stockpiles, chose a house site
(90,6,126) with a competent LLM rationale, split labor (cook=Clarae, forager=Euphemia), separated food/waste
zones, alerts read like a real player (campfire, beds-in-roofed-house, arm the hunter). This is the Gate-1 bed
the spec always wanted (a season to prep before winter).
SOAK RUNNING: background task bznn15g6y (gate1_soak_v2.ps1, up to 150 min) — screenshots every 5 min to
scratchpad\gate1\, watching every Gate 1 criterion: beds=4, cookfire, FULL ROOF, cellar dug, food buffer,
deaths, freezes>2s. Output: tasks\bznn15g6y.output. Do NOT deploy mid-soak (disrupts it). On completion, read
the output + the gate1\ screenshots and validate Gate 1 EYES-ON (esp. beds-under-roof by night 2, cellar dug,
day reaches 4). If Gate 1 passes clean on a fresh save, THAT is the real evidence Tranent could never give.

## ⛳ WHERE WE ARE — earlier (2026-07-13 ~07:00, AUTONOMOUS, long session)
ROOFS (Unit 1): FIXED + validated EYES-ON (shellcheck.png). Main house = full pitched roof; a shell now has a
roof frame going up after the ResolveLevel built-level probe fix (re-verify finds the built floors, skips the
false "pad not flat", runs the roof phase). Roof PLACEMENT is the bug and it's fixed. Full completion (settlers
finishing the roof) pending — it's winter night, build is slow.

HARD TRUTH about the test bed: the Tranent save is a WINTER DEATH-SPIRAL (Winter day 6, -4.5C, "settlers
suffering from cold", food CRISIS, storage still outside in snow, beehive still standing). My repeated
reload/deploy cycles walked it deep into an unprepared winter — a competent colony preps in autumn, which is
long past on this save. You CANNOT validate coherent seasonal play here. The remaining coherence defects Ken
named (U2 cold/winter survival, U3 outdoor storage, U4 research-gated placement, U5 carcass separation) AND the
deeper fugazi problem (Gate 2/3 "events" are dashboard-SIMULATED then narrated, not proven to have physically
happened in-game) need a FRESH SAVE, spring start, to validate honestly.

RECOMMENDED NEXT UNIT: start a fresh game (spring), let the mod drive a full VillageForge phase-1 build with the
now-honest roofs + research-gating, and validate the Chronicle Test gates on THAT run with eyes-on at each step.
The current save has served its purpose (found + fixed the perf stack and the roof-placement bug); it's now a
liability as a proving ground.

# SESSION-HANDOFF — earlier: overwritten 2026-07-13 ~03:30 (Unit C close)

## ⚡ CHRONICLE TEST CAMPAIGN (Ken /goal 2026-07-13 ~02:00 — THE standing goal)
Three cumulative gates in ONE fresh-save run: (1) Coherent Colony — 3 hands-off in-game days executing a VillageForge phase-1 plan, beds by night 2, food ≥1-day buffer, a cellar level dug, clean freeze log + screenshots; (2) Living World — diplomacy round with REAL factions + one event propagated into settler dialogue (DB rows + transcript); (3) The Scenario — one doc-11 slice end-to-end, Ken believes the transcript. PARTIAL until all three.

## CHRONICLE TEST STATUS (2026-07-13 ~06:10, AUTONOMOUS) — all three gates addressed + fidelity + depth
NEW since ~05:40:
- MULTI-STORY forge houses IMPLEMENTED (staged, compile-verified): HouseBuilder builds floors>1 with beams/floors/
  walls/stair-shaft/roof-on-top; floors=1 byte-identical (working build safe). Closes Ken's "fallback shell" gap.
  Needs a FRESH house build to validate (current save's houses already built single-story). See BACKLOG ~06:05.
- LIVING-WORLD DEPTH: Tranent world_events show diplomacy (war/peace/trade), dynamic events (hailstorm/visitors),
  AND disease (cold/fever outbreaks) all live. SECOND transcript (Alfred Benson) voiced the disease outbreak AND
  synthesized hailstorm + bandits in one line — emergent cross-system narrative. Both chronicles updated.
- Extended Gate 1 soak (blgx24e7h, 200 min) running: 40+ min, NO deaths, colony stable, capturing the 3-in-game-day window.

## CHRONICLE TEST STATUS (2026-07-13 ~05:40, AUTONOMOUS) — all three gates addressed
- GATE 1 (Coherent Colony): SUBSTANTIALLY MET. Two hands-off soaks ~150 min total, NO deaths, honest village built,
  cabbage farm placed, cellar dug, labor split, sleep-on-schedule, food self-managed. Gaps vs strict spec: full
  3-in-game-day window not yet run in one shot (soaks ~1.5hr each); ~2 intermittent 2.3-2.5s freezes/75min remain but
  they're GAME-operation costs (autosave/building-count) under contention, not mod-logic scans. Eyes-on shots banked.
- GATE 2 (Living World): **MET.** Real seeded factions (75 faction_relations) -> 20-min diplomacy rounds -> world events
  (56) -> propagated to settlers (1662 rows) -> in dialogue context -> VOICED. Transcript: Mariota Ros said "word spreads
  oft that the River Bandits hath set aside swords for peace." DB rows + transcript in validation/chronicles/gate2_transcript.md.
- GATE 3 (The Scenario): **CAPTURED, pending Ken's eyeball.** Doc-11 §1 "The Rumor and the War": bandits at war ->
  autonomous diplomacy -> war_fatigue_peace round 18 (CONSEQUENCE: relation war->peace + 25g reparations) -> world event
  -> rumor -> Mariota voices it. Full chain (DB+log+transcript) in validation/chronicles/gate3_the_rumor_and_the_war.md.
  The ONLY open item: KEN READS THE TRANSCRIPT and agrees it's believable (a human gate).
How Gate 2/3 were captured autonomously (Ken asleep, no in-game dialogue trigger): built the settler's REAL dialogue
context via the game's own build_dialogue_prompt_context, generated her line via Player2 on that context. Authentic path.

## *** BREAKTHROUGH — THE COLONY WORKS *** (2026-07-13 ~05:00, AUTONOMOUS)
The mod went from "can't build a thing" to PLAYING THE GAME. It was ALL performance, never logic. Fixes (all deployed):
1. HONEST construction — roofs no longer free-built (real DeliverResourceJob; settlers haul).
2. LOG PERF — LogToFile Flush()'d every line (~15 disk stalls/sec -> 2-min ticks). Now buffered 1/sec + debug spam gated.
3. FAST REGION SCAN — the full 1.36M-node GridSpaceData lazy-enum froze 2.8s/MoveNext -> 2-min ticks + ~90-min scan. Replaced
   with a radius-45 GetNode(int,int,int) region scan: **SCAN COMPLETE IN 15s** (was 90 min), **ticks recovered to ~13s**.
4. GetNode BY-REF BUG — GetNode(in Vec3Int) is by-ref so the type lookup returned null; used GetNode(int,int,int). Same bug fixed
   in CellarBuilder (its NodeSolid always read false = cellar never dug).
5. CELLAR down-dig on flat terrain; PLAN-EXEC YIELDS every 4th tick so farm/cellar/food aren't starved; safe-game hunting + labor split.
LIVE-PROVEN this session: ticks ~13s, scan 15s, PlanExecutor building the forge plan (research floors 1..9, walls 1..15 as honest
blueprints), 5 settlers cutting/hauling/building, ~2000 wood, labor split, NO deaths. Eyes-on: gate1_shots/building_now.png.
GATE 1 SOAK RUNNING (watch b4231xnmu, waiter bwqn1ce1q): validating farm placed + cellar dug + buildings complete + food + no deaths
over multiple in-game days. If it holds, the WORKING CORE is delivered. RESUME recipe: window(0,0)+foreground+click(0.87,0.33).
Suggested commit: perf(colony): fast home-region scan + buffered logging + honest construction + survival priority — the mod plays the game.
DEFERRED: upper-floor fidelity; full-map forge re-export (region scan exports only the box); bound [mod:food-scan] 3s freeze.

## PRIOR one-line (superseded ~04:30 — pre-breakthrough)
## One-line state (2026-07-13 ~04:30, AUTONOMOUS — Ken asleep, goal: "deliver a mod that works")
FIVE fixes shipped this session, all in the deployed dll, region-scan validating NOW:
(A) HONEST construction — removed the AutoConstructSequence free-build; roofs are real DeliverResourceJobs (settlers haul).
(B) CELLAR on flat terrain — down-dig staircase (was hill-face only).
(C) LOG PERF — LogToFile was Flush()ing every line (~15 disk stalls/sec); now buffered + 1/sec flush; debug spam -> LogDebug.
(D) **FAST HOME-REGION SCAN — the real tick-killer.** The full 1.36M-node GridSpaceData lazy-enum froze 2.8s/MoveNext, dragging
    ticks to ~2 MIN so the colony never built. Replaced with a radius-45 GetNode region scan (cached reflection), ~90s not ~90min.
(E) safe-game-only hunting + skill-gate + labor split (earlier).
Validation chain (watch binw4q9ul): scan completes ~90s -> ticks stay 12s -> colony BUILDS house2 (honest roof, settlers haul) +
digs cellar. If region scan proves out, the WORKING CORE is delivered. Prior blockers were all perf (mod choking on logging + full
scan), not logic. DEFERRED: upper-floor fidelity, full-map forge re-export (region scan only exports the scanned box now).
RESUME recipe on redeploy: window(0,0)+foreground+click(0.87,0.33). LAW banked: never Flush() per log line; never enumerate the
full GridSpaceData lazy collection on the main thread — GetNode the bounded region.

## PRIOR one-line (superseded ~04:00)
Ken reset the goal: build to fullest spec (AI Influence 00-11 + player-competence + VillageForge + laws), improvise+test+iterate,
know how to play (Comprehensive Reference Guide on Desktop). THREE fixes this session, all deployed together, validating now:
1. HONEST CONSTRUCTION — mod was FREE-BUILDING roofs (AutoConstructSequence=instant finish, no resources/labor; Ken caught it).
   Fixed via the game's real path SetConstructionPhase(Blueprint,false)->DeliverResourceJob (settlers haul + build, like walls).
   Completeness-checked: NO free-build cheats remain.
2. CELLAR ON FLAT TERRAIN — CellarBuilder only mined hill faces -> never dug on flat Tranent. Added down-dig staircase (guide:
   "dig a straight stair shaft down, 2+ levels"). Closes Gate 1's cellar requirement.
3. **THE BIG ONE — PERF: mod was strangling itself with logging.** LogToFile Flush()'d every line = ~15 synchronous disk
   stalls/sec -> ColonyBuilder ticked every ~2 MIN (should be 12s), worldmap scan crawled ~90min, colony couldn't build.
   Fixed: buffered log + FlushLogPeriodic (1/sec); NPCContextExtractor/GameBridge/ProcessSettler debug spam -> LogDebug (gated).
CURRENT VALIDATION (watch btsuxmmoe): measuring tick cadence recovery (target ~12s) -> scan completes fast -> house2 builds
honestly (settlers haul for roof) -> cellar dug. Food was recovering (forage+crisis routing, no deaths); pop grew to 5.
DEFERRED until core validated: upper-floor/rich-house fidelity, scan-speed (home-region-first). DO NOT touch the game.
RESUME recipe (game boots to MENU on redeploy): SetWindowPos(0,0)+foreground+click (0.87,0.33) — validated working.

## PRIOR one-line (superseded ~02:50)
**GATE 1 CLEAN RUN LIVE (redeployed with job fixes).** Ken flagged 2 live defects (everyone hauling; low-skill
settlers hunting boars and dying). Both ROOT-CAUSED + FIXED + redeployed: FoodGatherer never auto-hunts
WildAggressive (safe game only); JobRouter hunt-gate (Marksman<6 never hunts) + DivideLabor (distinct
builder/cook/forager pinned, rest haul). Unit C already PROVEN live this session (house adopted from forge plan,
"no improvisation"; 4-block depth grid live; zero mod freezes). Watch bg b3izyw2y5 = boot+world-live+soak.
ALSO this session: gm_embeddings.py core built+validated (Player2 /v1/embeddings live, 1536d); JSON memory
migration fully SPECIFIED in docs/JSON_MEMORY_MIGRATION.md (Player2 has NO hosted DB - it's OUR store + their
embeddings; 6-phase reversible plan; Option-1 rumor-sync recommended - needs Ken's call). DEFERRED past Gate 1:
embeddings RAG wiring + JSON migration (both need a dashboard restart).

## PRIOR one-line (superseded ~02:30)
**GATE 1 ATTEMPT IS LIVE.** Ken authorized full deploy ("game loaded, making dinner, do the rest") and is AWAY.
Full batch DEPLOYED (kill+verify -> build deploy dll_in_sync -> dashboard restart with report_raid fix ->
launch). World live 02:23 on Tranent autosave. PlanExecutor loaded the staged phase-1 plan (5 items, market
village), 17 factions seeded, colony under autonomous management, waiting on the 206x206x16 worldmap scan
(~10 min) before house adoption + construction. Two bg watchers: b9jd9p2dm (90-min soak, milestones+shots+
freeze) and b3yfqo562 (waits for house-adopted OR plan-failure). If resuming: read those task outputs +
validation/colony_status.txt + freeze_log.txt. DO NOT touch the game (Ken's hands-off Gate 1 run in progress).
Freeze note: one 209s [engine] freeze = the save-load hitch, NOT mod-attributed (Gate 1 bar is mod-freezes).

## Which project / which save
Going Medieval LLM_NPCs mod. Active save: **Tranent** (fresh 2026-07-13 ~01:00), map 206x206, **71% water**
island, colony home (99,6,99), pop 4. Ken live at the machine — machine-state rule in force.

## What's deployed vs staged (CRITICAL — one build apart)
- **Deployed dll (running now):** A (grid export) + B (faction seed) + snapshot-first siting + shack-disabled + all freeze/crash laws. NO PlanExecutor.
- **Staged dll (compiled code=0, NOT deployed):** all of the above **+ Unit C PlanExecutor** (+ HouseBuilder.AdoptForgeRect/LayoutRect + v3 re-adoption, BuiltState v3 keys + PlanExecDoneHash, ColonyBuilder plan-poll/plan-adopt/plan-exec phases + `planexec:` telemetry line) **+ grid export v2 (4 blocks: class/surface/cellar-depth/tower — Ken's 16-layer ask) + TryPlaceBuildingNear budget/resume/snapshot-prefilter (kills the plan-manager 2906ms freeze class) + NPC-to-NPC lane backoff (kills the 181-suppressed churn; transcripts fire when the lane has budget) + Gate 3 link: EventInteractor→GameTruthBridge.ReportRaidIfRaid (real raids feed diplomacy, roster-matched attribution only)**. Forge renders orientation-LOCKED to the in-game view (Ken eyeballed: "it's B" = left-right mirror; _flipped_view, pixels only, plan.json hash-verified unchanged).
- **Plan channel STAGED:** `validation\active_plan.json` = market_village phase 1 for Tranent, anchored at home (99,99), every rect pre-verified flat+dry against `validation\worldmap_grid.txt` (research 5x5 @103,90; houses 7x6 @81,110 + @113,103; field @74,86 — all level 6). INERT until the staged dll deploys.
- Dashboard: restarted earlier tonight (/api/diplomacy/seed live). **NEW uncommitted dashboard change needs a restart, bundle with deploy:** report_raid now makes every raid a propagatable world event (Gate 3 fix — raiders already at war used to produce no rumor) + extracted _propagate_to_all_settlers. 33/33 offline tests green (added test_gate3_cascade.py, 3/3, proving the raid→event→propagate→known_events→typed_memory chain end-to-end). 17 real factions in npc_memory.sqlite3.
- forge2.py upgraded: `--anchor x,z` (plaza near colony home), surface-row parsing, FLAT-PAD fits() rule — the forge can no longer emit a rect the executor would refuse.

## Next unit's first command (the deploy window — Ken-gated)
When Ken is NOT in the game (ask, or he says go):
1. `POST /api/dev/game/kill` → **verify no `Going Medieval` process remains** (last night a straggler survived) → `POST /api/dev/build {deploy:true}` → `POST /api/dev/game/launch`.
2. The 0,0 click recipe: SetWindowPos to (0,0), verify button on-screen, click 0.856/0.328, screenshot-verify (see ACTUATOR_CATALOG).
3. Watch the mod log, in order: `[PlanExecutor] plan loaded: 5 items` → `[HouseBuilder] FORGE PLAN house 7x6 at (81,110)` (adoption) → house steps batching → after HouseComplete: plan-exec shells (**research 5x5 @103,90 first — the never-live own-shell path; eyes-on screenshot of its blueprints is THE validation gate**) → `planexec:` telemetry counts.
4. Negative checks: no `mod:` freeze attributions; CURRENT_HOUR None stays 0; a mismatched rect must log FAILED honestly (none expected — pads pre-verified).
Then: **Gate 1 attempt** — Tranent qualifies as the fresh save: 3 hands-off in-game days, screenshot sequence, all settlers bedded under roof by night 2.

## Open observations / hazards / eyeball queue
- **Diplomacy rounds complete with 0 moves** (rounds 1-13). Inspect gm_systems run_diplomacy_round's agent mover (LLM lane gated? choose_move fallback no-op?). Offline-safe — the ideal waiting-room unit.
- Telemetry cosmetic: "beds 0/4: interior FULL" prints when no house is planned yet — misleading, backlog.
- Last night's ambiguous "MOD LOOP DEAD" watch: explained — the watch tailed a boot my own deploy cycle killed, and a straggler instance survived a kill. Law above.
- **Eyeball queue for Ken (30s):** `tools\villageforge\fromgame_tranent\village_seed3_valley_market_village_phase1.png` — the anchored plan on his real map. Click-by-click: open file, confirm plaza sits on the settler camp area, confirm no lot touches water.

## Commit state
Ken committed through Units A+B this session ("I just committed"). UNCOMMITTED since: Unit C (src/PlanExecutor.cs NEW, HouseBuilder.cs, BuiltState.cs, ColonyBuilder.cs, tools/villageforge/forge2.py, validation/active_plan.json, BACKLOG.md, this file).
**Pre-written commit title:** `feat(chronicle): Unit C PlanExecutor — forge plans drive in-game construction; anchored flat-pad from-game plans`

## Standing laws (unchanged, enforced)
No game-state reads off the main thread — slice, don't thread. Per-origin time budgets. Snapshot-first siting.
Never act on guessed ids. Kill→verify-dead→deploy→launch. Eyes-on screenshots for every player-facing claim.
Commits are Ken's only (titles pre-written at every close).
