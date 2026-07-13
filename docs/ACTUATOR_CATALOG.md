# Actuator Catalog — the validated action space (2026-07-12)

**Purpose:** the single document a fresh LLM session reads to learn what this system can DO to Going Medieval — every actuator with its real signature, preconditions, failure modes, and validation date. Read `player-competence-canon.md` (StarForge wiki) first for WHAT to do; this catalog is HOW. The API index (`GameApiIndex/`) is the lookup for anything not listed.

**The prime law (paid for in blood):** *invoked ≠ applied ≠ placed ≠ built ≠ seen.* Every actuator below lists how "applied" was proven. A plausible method name is not semantics.

---

## Game-side actuators (C# via reflection, main thread ONLY)

| Actuator | Real call | Preconditions | Failure modes | Validated |
|---|---|---|---|---|
| Job priority | `WorkerGoapAgent.ChangeJobPriority(JobType, int)` (1=highest, 4=suspended) | Validated settler identity via GameBridge | Wrong enum assembly (`Unity.Jobs` collision — resolve `NSMedieval.State.WorkerJobs.JobType` by FULL name) | 07-11 crisis routing live |
| Building blueprint | `CommitPlayerBlueprint` after `BuildingsManagerMain.CanPlace(blueprint, Vec3Int, angle, bool)` | Blueprint from repository by TRUE id (`building_ids.txt`); cell not in stockpile zone (CellInRects); not on existing building (BuildingExistsAt) | CanPlace true but UNREACHABLE (no region check — fix specced: `IsRegionReachable`); "blocked by door" class | 07-10→12, dozens live |
| Stockpile zone | StockpilePlacer.TryPlaceStockpileNear | Home anchor set | Sprawl if radius unbounded | 07-10 live |
| Zone filters | `ResourcesFilter.RemoveAllowedResource(string)` / re-add | REAL ids from `resource_taxonomy.txt` (NEVER guessed — v2 no-op'd for weeks on invented ids) | Silent no-op on wrong id; verify with `IsBlueprintAllowed` spot-check | 07-11 zoner v3 |
| Dig designation | `DigMarkerResourceManager` CreateResource path + `ConstructionJobManager.CreateDigJobs` | Solid voxel (`VoxelTypeIdByte != 0`); model id from repository `GetFirst` | Marking air; scans must be TIME-BUDGETED (freeze class) | 07-12 CellarBuilder v1 |
| Tree/plant designation | WoodGatherer/FoodGatherer designators | Bounded radius + session caps | Radius scans = freeze class — budget them | 07-10→12 live |
| Production order | `SetProductTargetCount` on `ProductionComponentBlueprint.Productions` (component's blueprint, NOT building's) | Recipe id from `production_ids.txt`; station CONSTRUCTED | Truncated LLM JSON → parse-throw loop (maxTokens 1024 + cooldown pattern) | 07-11 weapons; meals |
| Research pick | ResearchPlanner via research repository | Node ids from `research_ids.txt`; priority = canon order (architecture first) | Guessed node ids | 07-11 live |
| Equip weapon | `Resource.EquipmentBlueprint` (property!) + quality-prefix suffix match (`good_leather_cow_sling`) | Pile visible to census | `EquipmentRepository.GetByID` misses prefixed ids; `AttackType` lives on `WeaponMode.WeaponTypeSettings` | 07-11 3/3 hunters |
| Event answer | `instance.stateMachine.currentPhase.OnClose(optionIndex)` — NOT `EventOptionChosen` (notification-only decoy; its int is dialogShowingIndex) | Phase HAS OnClose(int) (answerability gate); DialogContent members are FIELDS (Member() accessor) | Zombie windows — call `DialogViewManager.CloseSilent()` after apply | 07-11 Lee/Jeffrey live |
| Programmatic save | `GlobalSaveController.AutosaveCurrentVillage()` (waits for water-sim thread) | Main thread | **`QuicksaveCurrentVillage()` is an EMPTY STUB** — the purest invoked≠applied trap | 07-12 on-disk files |
| Game speed | AutoSpeed → GameSpeedManager | World live | Keys/clicks need window FOCUS; input API = FRACTIONS 0..1, `target=` ignored | 07-12 |
| Recap/stuck-load rescue | `LoadingScreenFake.OnContinueClick()` (self-contained: SetupScene/StartGame/StartGoapTicker) after `InvokeLoadingCompleteEvent()` | Screen active 30s+ without button; **run on REAL time** (screens freeze game time) | Force-finishing a HALF-loaded world (check the load actually completed); singleton poisoning (below) | 07-12 |

### Standing hazards (game side)
- **NO game-state reads off the main thread, EVER.** "Pure C# model data" is still the game's mutating state — enumerating live collections (GridSpaceData etc.) from a worker thread caused three native crashes and a 1000x map-query slowdown (2026-07-12). Long scans are SLICED on the main thread (kept enumerator + per-tick time budget), never threaded.
- **Singleton poisoning:** `MonoSingleton<T>.Instance` before the scene object exists caches null forever and the REAL instance self-destructs in Awake. Probe `IsInstantiated()` first. One violation wedged 14 consecutive loads.
- **Freeze class:** ANY main-thread map query (`GetNode` and kin) races the water-sim thread. Time-budget every scan (50ms + resume index + failure cooldown); the tick-phase breadcrumb (`validation/tick_phase.txt`) names any new freezer.
- **Real time vs game time:** recap screens and paused game FREEZE scaled coroutines — anything that must always run (dismissers, event pump, bridge) drives from `Plugin.Update` real time.
- Mod logs: `%USERPROFILE%\AppData\LocalLow\Foxy Voxel\Going Medieval\LLM_NPCs\logs\`. Telemetry: `validation/colony_status.txt`.

---

## Dashboard actuators (REST on 127.0.0.1:8714 — the P3-P10 simulation layer)

All POST JSON with `save_id`. Offline suites: `dashboard/test_{diplomacy,disease,romance,combat,events}.py` — 30/30 green 2026-07-12. **Routes marked (new) activate on next dashboard restart.**

| Route | Effect | Bounded-LLM hook |
|---|---|---|
| /api/orders/issue | free text → `parse_order_text` → bounded multi-step plan (unknown segments become `unsupported` — nothing free-form reaches the game) | order vocabulary IS the menu |
| /api/diplomacy/round | fatigue ticks, drift, forced peace, + ONE agent move (new) | `diplomacy_legal_moves` menu; garbage → deterministic fallback |
| /api/diplomacy/raid (new) | real raid → relations sour, losses accrue, escalates to war at −0.6 | none (ground truth) |
| /api/diplomacy/relation | apply action: declare_war (shatters alliances, cascades ally scores) / make_peace (loss-ratio reparations) / form_alliance / trade_pact (cap 2) / tribute / banish / pardon | action from the legal menu only |
| /api/disease/infect · /tick · /treat | infection (immunity/Medicine resist); tick = progression + spread (quarantine ×0.25) + seasonal onset; treat sets treated/quarantined | none (deterministic sim) |
| /api/romance/tick (new) | decay + autonomous bonds from live romance/attraction + initiative proposals + wedding world events | trait-gated proposals |
| /api/death/record · /history | facts record (50-interaction gate) · story generation (LLM may replace template) | story text only |
| /api/combat/incident | verdict (aggressor/defenders/panic/stances by trust) + faction interventions + diplomacy feed + casualties→P8 | LLM may narrate; verdict is code |
| /api/events/create · /evolve (new) | lived/generated events · aging lifecycle, war-truth resolution | event TEXT is LLM; lifecycle is code |
| /api/plan | colony plan persistence (verb menu: produce/build/focus_job) | plan steps from verb menu only |

**Propagation invariant:** every consequential world event (proclamation, outbreak, wedding, battle) is propagated to settlers' `world_event_knowledge` → surfaces in `known_events` → dialogue context. If you add an event source, propagate it or nobody will ever speak of it.

**The heartbeat:** `GameTruthBridge` (mod) drives /diplomacy/round (20 min real), /romance/tick + /disease/tick + /events/evolve (60 min real), and posts deaths with cause. Real season read from `WorldTimeManager` by reflection.

---

## LLM lanes (budget-governed — 8/hr sliding, critical lanes full cap)
`story_event` · `planner` · `death_history` · `siteplan` · `architect` (+ `diplomacy` planned) — every lane: bounded menu or validated schema, hard cooldown, logged failure path, deterministic fallback. Dialogue lanes capped at cap−3. **Loop-safety pattern is mandatory:** the planner once burned 4 critical calls in 2 minutes on a silent parse-throw retry loop.
