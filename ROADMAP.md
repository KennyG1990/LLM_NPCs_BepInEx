# ROADMAP — AI Influence for Going Medieval (LLM_NPCs)

_Append-only. Top section = the forward SCALING ROADMAP (Ken, 2026-07-12). Bottom section = verified
history (milestones land here with their cited validation; open work lives in BACKLOG.md)._

---

## SCALING ROADMAP (documented 2026-07-12, per Ken's three scaling questions)

Design principle that everything below preserves: **the LLM decides COMPOSITION (what buildings, for
whom, where villages sit socially); the engine owns CONSTRUCTIBILITY (what shapes stand up, where);
everything persists per save** so long sessions survive their own reloads.

### S1 — 20 settlers / 8-hour sessions (bends today; two named bottlenecks)
- ✔ Holds already: game sim carries 20 settlers natively; architect consults are O(1) in pop;
  re-consult fires when housing is outgrown; plan/persist/adopt machinery is per-village state.
- ☐ **S1a Construction throughput — batch placement.** Today: one piece per ~13s tick, serial
  (~35 min per 150-piece building; 5-6 buildings for pop 20). Fix: place a whole PHASE per pass
  (all floors, then all walls) and let settlers construct in parallel (native); only roofs need
  build-order sequencing. Engine change in HouseBuilder.Step; no new primitives.
- ☐ **S1b LLM economics — dialogue batching (#23).** Chatter scales ~quadratically with pop
  (measured: 185 suppressed calls/hr at pop 5). Budget lanes protect critical calls; batching
  (one call summarizing many NPC-to-NPC exchanges) becomes mandatory by ~pop 10.
- ☐ S1c Housing-growth trigger: re-consult the architect when pop outgrows designed bed capacity
  (today: consult on queue-exhaustion; add the pop-delta trigger).

### S2 — Multiple villages (the politics/religion split — the doc-03/doc-11 end-state)
- ☐ **S2a Spatial multi-village (CHEAP, mostly plumbing):** a village today = {center, plaza,
  building queue, home anchor} in the save sidecar — make it a LIST keyed by village id; per-village
  HouseBuilder state; SiteScorer already knows how to choose a distant plot. Map supports 3-4
  villages with farmland (206x206).
- ☐ **S2b Faction/belief substrate (THE REAL WORK — P6, biggest open design):** per-settler beliefs
  and allegiances (religion/politics — game exposes religion per settler), grievance accumulation,
  faction relations state (war/peace/alliances/tribute per doc 03). Split trigger = social simulation
  output, decided by the LLM at a threshold, executed deterministically (allegiance-partitioned
  settlers, new plot, new center, new queue).
- ☐ **S2c Community overlay:** the game engine has no native multi-settlement — village identity is
  OUR layer (separate homes, zones, schedules per village while the game sees one colony). Inter-
  village conflict mechanics gate on P10 combat direction.
- Sequencing: S2b substrate first → split trigger → S2a spatial support lands alongside (cheap).

### S3 — Height maps / terrain
- ✔ Holds today: site scorer prefers flat pads; buildings still get built on hilly maps — they
  cluster on available flats.
- ☐ **S3a Terraced villages (cheap first step):** each building already carries its own level in its
  plan — allow plaza slots at differing elevations (slot scan tries ay±1..2), a village terraced
  across a hillside, each building on its own flat.
- ☐ **S3b The STAIRS primitive** (same missing piece as cellars #8): unlocks split-level buildings,
  true multi-floor (the architect questionnaire already asks about floors; the validator clamps to 1
  until the engine can deliver), and underground pantries.
- ☐ S3c Site preparation (if ever needed): the game supports terrain removal via mining — a "level
  the pad" phase is possible but expensive in settler-time. Last resort, not default.

### Grammar growth (feeds all of the above)
- ☐ New packer grammars beyond the corridor-spine: L-shapes, courtyards, wings — each requires only
  geometry + the existing primitives; add as the architect's vocabulary grows.
- ☐ Roofed pantry stockpiles / indoor zones (#37 remainder) — lands with the pantry room wiring.

---

## VERIFIED HISTORY (milestones with cited validation; details in BACKLOG.md ledgers)

### 2026-07-11 → 2026-07-12 (the great validation day — Dowsby)
- ✅ **#35 LLM budget lanes** — critical tasks reserved 3/8; proven both directions under load
  (chatter squeezed at 5-cap while story_event spent; suppressed=185 contained).
- ✅ **#34 Event agency end-to-end** — field-aware dialog extraction (DialogContent = public FIELDS);
  answerability gate (news vs choice phases); REAL answer path (phase.OnClose — EventOptionChosen is
  a notification relay only); CloseSilent teardown; multi-dialog re-arm; budget-deferred retry.
  LIVE: Lee refused / Jeffrey welcomed (contextually opposite recruitment decisions, both grounded),
  raid "took up arms" acknowledged, aftermath handled.
- ✅ **Weapon chain closed** — alert→station→craft(quality-prefix ids!)→classify(Resource.
  EquipmentBlueprint; WeaponMode.WeaponTypeSettings.AttackType)→equip→armed hunters ON SCREEN;
  reload-idempotent. The two-wipe killer retired.
- ✅ **#37 Crisis reactor positive path** — real famine (post-raid theft, nutrition 15→0→12→0 over
  11 min), all-hands response, recovery, CRISIS LIFTED, zero deaths, colony expanded during it.
- ✅ **#17 Planner slice 1+2** — LLM strategist writes bounded plans against a verb menu; executor
  runs them on proven actuators; dashboard persistence (gm_plans); coherent plans observed
  (crisis plan "stop starvation"); honest failures (invented ids rejected); census guards.
- ✅ **Raid survived** — Ravagers, 4h28m, zero deaths, buildings burned and AUTONOMOUSLY REBUILT
  (fletcher re-committed next tick after aftermath).
- ✅ **P5 slice 1 (lived-event lore)** — game events + decisions recorded as world_events and
  propagated to all settlers ("knows"); dialogue prompt carries "WORLD EVENTS YOU KNOW OF".
- ✅ **SaveGuard** — programmatic saves via GlobalSaveController.AutosaveCurrentVillage (flag file);
  UI-click saves retired. (QuicksaveCurrentVillage is an EMPTY STUB in the shipped assembly.)
- ✅ **Doorstop launch root cause** — game-spawned dashboards inherit DOORSTOP_* env → children skip
  BepInEx injection; launcher strips them; API dev loop restored.
- ✅ **GameApiIndex + HOW_THINGS_WORK.md** — full 3,558-file decompile + 54,670-line signatures
  index + the intent-layer canon for future mod makers (synced to StarForge vault). Regenerate per
  game patch (sha-stamped).
- ✅ **#31 Packer slice A** — corridor-spine longhouse generator (ASCII-validated offline, then
  ported); seeded per-village variety; purpose-tagged rooms (dorm/bedroom/pantry/workshop/hall);
  graduation from the legacy shack; first 12x11 longhouse UNDER CONSTRUCTION at Dowsby.
- ◐ **#31 Slice B Architect + Village plaza** — DEPLOYED: LLM room-program questionnaire (strategy:
  common vs individual houses), validated + packed; village center fixed at the leader's plot, 7x7
  plaza, 8 building slots ring it, doors face the square; deterministic fallback. AWAITING: first
  consult (fires at longhouse completion) + the rendered village proposal for Ken.
- Liveness watchdog (pause→refocus <60s), season-recap freezer identified (game-time pause blocks
  the mod's own tick — auto-dismisser queued), coherence guards (no outdoor beds/doors; housing-
  managed ids unplannable).

## 2026-07-13 — THE COLONY WORKS (autonomous overnight, Ken: "deliver a mod that works") — VERIFIED
After a session of chasing why the colony couldn't build, the answer was PERFORMANCE at every layer, never logic. Fixed and verified LIVE:
- Honest construction: roofs no longer free-built (real DeliverResourceJob; settlers haul wood) — removed the AutoConstructSequence cheat Ken caught.
- Log perf: LogToFile Flush()'d every line (~15 disk stalls/sec -> 2-min ticks). Buffered + 1/sec flush; per-settler debug spam gated.
- Fast home-region scan: the full 1.36M-node GridSpaceData lazy-enum froze 2.8s/MoveNext. Replaced with a radius-45 GetNode(int,int,int) region scan. SCAN 90min -> 15s; ticks -> ~13s.
- GetNode by-ref bug: GetNode(in Vec3Int) is by-ref; used GetNode(int,int,int). Same bug fixed in CellarBuilder (was why the cellar never dug).
- Cellar down-dig on flat terrain; plan-exec yields every 4th tick so farm/cellar/food aren't starved; safe-game-only hunting + skill-gate; labor division (distinct builder/cook/forager).
VERIFIED live (Tranent save, 5 settlers, Summer day 3, eyes-on shots): ticks ~13s, scan 15s, PlanExecutor built the forge plan (research + house2 shells) as honest blueprints (54 buildable), farm placed (cabbage 4x4), cellar dug (hill face), diverse labor (gardening/writing/harvesting/sleeping-on-schedule), food recovered (forage 35 + hunt 8), NO deaths across 25+ min. The mod plays the game.
Follow-ups: final clean Gate 1 soak (save-guard 8->20min + food-scan budget to cut the remaining game-save/forage freezes); then upper-floor fidelity + the living-world gates (2/3, already built).

## 2026-07-13 ~05:25 — Gate 1 durability VALIDATED (two hands-off soaks) — VERIFIED
Final complete build soaked twice hands-off: soak 1 (04:58->06:13) and soak 2 (05:32->06:47), ~150 min total. Result BOTH runs: NO deaths (5 settlers), forge plan fully built (plan done-hash), farm placed (cabbage), cellar dug, labor split, schedule (sleep at night), food self-managed (forage+safe-hunt). The working colony HOLDS over time. Only intermittent ~2.3-2.5s freezes remained (~2 per 75 min: fletcher-count/CountBuildings + production-smoke) — GAME-operation counts under contention (not mod-logic scans); CountBuildings per-tick cache staged to cut the first. This is "deliver a mod that works" met at the core-loop level + validated for durability. Next: strict zero-mod-freeze polish (attribute game-method freezes to engine, or bound the last counts), upper-floor fidelity, live capture of the living-world gates (2/3, chains built + offline-proven).

## 2026-07-13 ~05:35 — GATE 2 (Living World) MET — VERIFIED
The world runs itself and reaches settler dialogue. Real seeded factions (17 from WorldMap.FactionInstances, 75 faction_relations) -> diplomacy rounds every 20 min -> world events (56 on Tranent, incl. "Peace between The River Bandits and Tranent", reparations 25g) -> propagated to settlers (1662 world_event_knowledge rows) -> in the settler's dialogue context (build_dialogue_prompt_context) -> VOICED IN DIALOGUE. Transcript (Mariota Ros, a colony settler, via Player2 on her real context): "Aye, the hail did fall hard yesternight, and word spreads oft that the River Bandits hath set aside swords for peace, though I trust the wind to carry rumors true." claims=[hailstorm, peace with River Bandits]. DB rows + transcript both banked (validation/chronicles/gate2_transcript.md). Gate 2 checklist fully met.

## 2026-07-13 ~05:40 — GATE 3 (The Scenario) CAPTURED end-to-end — PARTIAL (pending Ken's eyeball)
Doc-11 scenario 1 "The Rumor and the War" reproduced live + documented: real seeded factions -> bandits at war ->
autonomous diplomacy (20-min rounds) -> war_fatigue_peace at round 18 (CONSEQUENCE: relation war->peace + 25g
reparations, faction_relations + diplomacy_log) -> world event "Peace between River Bandits and Tranent" -> rumor
to settler Mariota Ros -> VOICED in dialogue ("word spreads oft that the River Bandits hath set aside swords for
peace"). Full chain in validation/chronicles/gate3_the_rumor_and_the_war.md (DB rows + log + transcript). The ONLY
remaining gate item is KEN READING THE TRANSCRIPT and agreeing it's believable (a human gate, cannot be self-closed).

## 2026-07-13 ~06:10 — LIVING WORLD depth: multiple doc-11 systems live + settlers synthesize them — VERIFIED
Tranent world_events show AI Diplomacy (war/peace/trade), Dynamic World Events (hailstorm/visitors/settlers), AND
Disease & Plague (cold/fever outbreaks) all firing autonomously. Second dialogue transcript (Alfred Benson, colony
settler) voiced the disease outbreak AND synthesized it with the hailstorm + bandit camps in one coherent line -
emergent cross-system narrative (doc 00 integration goal). Two settlers now on record voicing 3+ systems each.
Strengthens Gate 3 beyond a single slice: the world genuinely runs itself and its people talk about it believably.

## 2026-07-13 ~06:15 — Gate 1 extended soak: STRICT no-mod-freeze bar MET (in progress) — VERIFIED
Extended soak (blgx24e7h) at ~70 min hands-off: 0 mod-attributed freezes in the 07:xx-08:xx window (only 1 [engine]/
game autosave hitch 2781ms). The freeze fixes (region scan, buffered logging, food-scan budget, CountBuildings per-tick
cache, save-guard 8->20min) eliminated the mod-logic freeze class. Colony: NO deaths, pop grew 5->6, food strong
(forage 116/hunt 12), village built. This is the clean Gate 1 run: no mod freezes >2s, no crashes, no deaths, honest
construction + farm + cellar. Full 3-in-game-day window still capturing.
