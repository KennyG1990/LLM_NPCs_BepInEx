# Workstation Placement Plan — Going Medieval LLM_NPCs

Status: **SPECIFIED** (2026-07-13). Author: agent, at Ken's direction ("systematically plan out the
placement of every single table... where they should be placed for optimal construction").

This replaces the improvised, spiral-search workstation placement (`StockpilePlacer.TryPlaceBuildingNear`)
with a **deterministic, geometry-aware, workshop-zoned** scheme. It is the design; implementation follows
in bounded units (see §8).

---

## 1. Why the current placement is wrong (root cause, not symptom)

Two uncoordinated construction systems:
1. `PlanExecutor` builds the VillageForge plan (houses, research building, field).
2. `ColonyBuilder` **separately improvises** each workstation via `TryPlaceBuildingNear`, which spiral-searches
   outward from a settler for the first cell the game's `CanPlace` accepts.

`CanPlace` only validates the building's **own** footprint cells (dry, unoccupied, stable). It does **not**
stop a table from being placed:
- on top of a crop field (crop fields are `CropsController`'s system, not `BuildingsManager`),
- one tile from another table (no inter-table spacing),
- with its work-access cell blocked (so the settler can never stand to build or operate it → sits unbuilt for hours).

Result (Ken, eyes-on 2026-07-13): research table ON the cabbage farm, tables crammed edge-to-edge occupying
each other's work cells, none of them ever built.

## 2. Ground truth: the game already gives us the geometry

Every `BaseBuildingBlueprint` exposes (verified in decompiled source):

| Property | What it is | Source |
|---|---|---|
| `Size` (Vec3Int x,y,z) | Footprint — cells the building occupies | `BuildingPlacementManager.cs:1160` |
| `ForbiddenAreaInfo` | `ForbiddenAreaFrontOffset/Back/Left/Right` (+ `HasFrontOffset`…): cells around the building that must stay clear | `BuildingPlacementManager.cs:1159–1181` |
| `WorkPositions` | The cell(s) a settler stands in to operate it (set via `SetWorkPositionsMarkersTransforms`) | `BuildingPlacementManager.cs:1258` |

**Design consequence:** we do NOT hardcode footprints. At placement time we read `Size`,
`ForbiddenAreaInfo`, and `WorkPositionsArray` off the blueprint by reflection, and treat each station's
**claim** as:

```
claim(station) = footprint cells  ∪  work-position cell(s)  ∪  designed work aisle
```

No two stations may have overlapping claims. A claim may not include a crop-field, stockpile, wall/door,
or designated-path cell. The work-position cell must be walkable and reachable.

### ⚠ RECONCILE CORRECTION (2026-07-13, ground-truth dump — `validation/workstation_geometry.txt`)

Running the dump (§8.0) **corrected two assumptions before they became code**:

1. **`ForbiddenAreaInfo` is ~all zero.** Every crafting station reads `F0/B0/L0/R0` EXCEPT
   `basic_research_table` (`B1`). So the forbidden area is NOT the work-aisle mechanism I assumed — it's a
   rarely-used special no-build offset. **Spacing/aisle must come from OUR design (reserve footprint + a
   1-cell work aisle we define), not from the game's forbidden data.** This is why the claim formula above
   dropped "forbidden-area cells" and added "designed work aisle."
2. **`WorkPositionsArray` is `TransformSettings[]`** (position+rotation), not `Vec3Int[]`. The **count** is
   authoritative and meaningful: `camp_fire` and `skep` expose **4** work sides (approach from any side);
   every table exposes **1** (one-sided → orientation matters, work side must face the aisle). The exact
   per-station offset is read live at placement time (transform by anchor+angle); the runtime proxy until
   then is "≥1 free walkable orthogonal neighbour" (already implemented this session).

This single rule — reserve footprint + work cell + a defined aisle, never overlapping another claim or the
farm/stockpile/wall/path — fixes cramming, farm-overlap, and unbuildability, now using **real** numbers.

### Ground-truth footprints (real, from the dump — used to size rooms)

Format `W×D` (x by z; height always 1). Work sides = # of `WorkPositionsArray` entries.

| station | W×D | work sides | notes |
|---|---|---|---|
| `camp_fire` | 1×1 | 4 | tiny, 4-side approach |
| `skep` | 1×1 | 4 | outdoors |
| `minting_station` | 2×1 | 1 | |
| `woodwork_bench`, `stonemasons_bench`, `basic_research_table` | 3×1 | 1 | `basic_research_table` has `B1` forbidden |
| `ice_station` | 1×2 | 1 | |
| `smokehouse`, `limestone_smokehouse`, `fermenting_station`, `easel` | 2×2 | 1 | |
| `limestone_stove`+variants, `butchering_table`, `brewing_station`, `oil_press`, `smelting_station`+lime, `kiln`+lime, `sewing_station`, `fletchers_table`, `apothecary_bench`, `research_table`, `grand_research_table` | 3×2 | 1 | the common table size |
| `spirit_destilary`, `saltpeter_pit`, `advanced_research_table` | 3×3 | 1 | |
| `blacksmith_station`, `clay_brick_blacksmith_station` | 4×3 | 1 | large |
| `armourer_table` | 5×2 | 1 | widest |

Room-sizing rule: interior must fit each station's `W×D` + a shared front aisle (≥1) + input storage row.
A kitchen with `camp_fire`(1×1) + `butchering_table`(3×2) + `smokehouse`(2×2) along a back wall needs an
interior ≥ 6 wide × 4 deep (3 back + 1 aisle) before storage. forge2.py computes this from the table above.

## 3. Complete workstation inventory (from `validation/building_ids.txt`, 782 buildings)

Crafting/production stations, grouped by function and shared inputs. `research` gate = the station is
locked until its tech is researched (see U4); place only after unlock.

### A. KITCHEN / LARDER (food; some emit heat/fire)
| id | role | inputs → near | heat |
|---|---|---|---|
| `camp_fire` | basic cooking (meal) | food stockpile | yes (fire) |
| `limestone_stove`, `limestone_block_stove`, `clay_brick_stove` | better cooking | food stockpile | yes |
| `butchering_table` | corpse → meat | food/corpse stockpile | no |
| `smokehouse`, `limestone_smokehouse` | smoked/preserved meat | food stockpile | yes |
| `brewing_station` | beer/ale | grain stockpile | no |
| `fermenting_station` | fermenting | food stockpile | no |
| `spirit_destilary` | spirits | food stockpile | no |
| `oil_press` | oil | crop stockpile | no |
| `ice_station` | ice / cold | cold room / cellar | no |
| `skep` | honey (**gate: beekeeping**) | outdoors, near flowers | no |

### B. FOUNDRY / SMITHY (metal; hot; fire hazard; keep from beds & flammables)
| id | role | inputs → near |
|---|---|---|
| `smelting_station`, `limestone_smelting_station` | ore → ingot | ore + fuel stockpile |
| `kiln`, `limestone_kiln` | clay→brick, wood→coal | clay/wood + fuel |
| `blacksmith_station`, `clay_brick_blacksmith_station` | weapons/tools | ingot stockpile |
| `minting_station` | coins | precious-metal stockpile |
| `saltpeter_pit` | saltpeter | outdoors |

### C. WORKSHOP (raw materials)
| id | role | inputs → near |
|---|---|---|
| `woodwork_bench` | wood items | wood stockpile |
| `stonemasons_bench` | stone blocks/tiles | stone stockpile |

### D. TEXTILE / ARMORY (gear)
| id | role | inputs → near |
|---|---|---|
| `sewing_station` | clothes/armor cloth | cloth/leather stockpile |
| `fletchers_table` | bows/slings (ranged) | wood stockpile |
| `armourer_table` | armor | ingot/leather stockpile |

### E. CARE / ART / STUDY (quiet, indoor)
| id | role | notes |
|---|---|---|
| `apothecary_bench` | healing kits/medicine | infirmary, quiet |
| `easel` | paintings (art) | lit room |
| `basic_research_table`, `research_table`, `advanced_research_table`, `grand_research_table` | research (chronicle) | study, indoor, lit, quiet |

### F. SECONDARY WORK-OBJECTS (same placement model, lower priority)
`warden_desk`, `map_table`, `strategy_table`, `book_stand`, `librarian_book_stand`, `merchants_stall`,
`caravan_post`, `archery_range`, `practice_dummy`, `sergeants_podium`, `scales`.

## 4. Per-category placement rules (the "optimal construction" logic)

Rules a competent player follows, bound to actuator-checkable conditions:

1. **Reserve geometry (all stations).** Claim = footprint ∪ forbidden-area ∪ work cell (§2). Never overlap
   another claim, a crop field, a stockpile, a wall/door, or a path.
2. **Work-cell reachable.** The `WorkPositions` cell must be walkable, unclaimed, and connected to the
   settlement (no placing a station whose only work cell is boxed in). This is the "gets built" guarantee.
3. **Indoors for weather-sensitive work.** Research, apothecary, easel, sewing, textile → inside a roofed
   room (mood + no rain interruption). Verify by roof/enclosure check at the work cell.
4. **Heat/fire discipline.** Fire emitters (`camp_fire`, stoves, `smokehouse`, `kiln`, `smelting_station`,
   forge) → NOT adjacent to wood walls/floors where avoidable, NOT in the same room as beds, and grouped so
   their heat doesn't cook the larder. In winter, one heat source near the work area is a feature (keep
   workers warm) — but never inside the cold cellar.
5. **Cold for storage.** `ice_station` and the food larder belong next to / above the dug **cellar** (cold
   keeps food from spoiling). Do not put heat sources next to the larder.
6. **Adjacency to inputs.** Each station sits within short haul range of the stockpile holding its inputs
   (table §3) — minimizes hauler travel, the single biggest throughput cost. Concretely: a matching
   stockpile within N tiles of the station, or placed as part of the same workshop room.
7. **Research gate.** Place a station only once its unlocking tech is researched (U4). Until then it is not
   a legal player build; skip it.
8. **One of each until scaled.** Phase-1 places exactly one station per active need (cook, butcher, research,
   fletcher for defense). Duplicates only when throughput demands (queue backlog) — no station spam.

## 5. Workshop-zone layout model (how tables sit together)

Stop placing tables in open fields. Tables live in **workshop rooms** — the same VillageForge building
shells the plan already generates, tagged by function. Within a room:

```
   ┌─────────────────────────┐   back wall
   │ [S] [S] [S] [S] [S]     │   stations along the back, work side facing IN
   │  ·   ·   ·   ·   ·       │   work-cell row (kept clear = the forbidden "front")
   │                         │   central aisle (≥1 clear, haulers + workers path here)
   │ [barrel][shelf][chest]  │   input storage along the front/opposite wall
   └────────┬────────────────┘
            door (on the aisle)
```

- Stations line the back wall, **work-position facing the aisle**; the forbidden-front cells ARE the aisle
  so workers always have room to stand (this is exactly the data in §2).
- One clear tile minimum between adjacent stations (spacing) — falls out of not overlapping forbidden areas.
- Input storage (barrels/shelves/chests) on the opposite wall → short hauls (rule 6).
- Door opens onto the aisle so haulers path straight in.
- Room function decides which stations: **Kitchen** (A) next to cellar; **Foundry** (B) detached, stone-built,
  away from beds; **Workshop** (C) + **Armory** (D) near the material yard; **Study/Infirmary** (E) quiet wing.

## 6. Deterministic placement algorithm

```
For each workshop room R in the VillageForge plan (function-tagged):
  1. floor cells  = interior(R) minus walls/door
  2. back_wall_cells = the row of interior cells adjacent to the room's back wall
  3. occupancy = {}   // per-room reserved-cell set
  4. For each station S assigned to R (in build order, research-gated):
       bp        = blueprint(S)
       size, forbidden, workcells = read(bp)          // §2, ground truth
       For each candidate anchor along back_wall_cells (left→right):
         claim = footprintCells(anchor,size) ∪ forbiddenCells(anchor,forbidden) ∪ workCells(anchor)
         if claim ∩ occupancy         : continue      // overlaps a placed station/aisle
         if claim ∩ cropfield/stockpile/wall/door/path : continue
         if any workcell not walkable/reachable        : continue
         if not researched(S)                          : skip station
         place S at anchor; occupancy ∪= claim; break
  5. Input stockpile for R placed on the opposite wall, its cells added to occupancy.
```

Everything reads the game's own `Size`/`ForbiddenAreaInfo`/`WorkPositions` — no magic numbers, no guessing,
no spiral search. If a room can't fit all its stations, that's a HOUSE-EXTENSION signal (grow the room /
add a room), not a "dump it in a field" fallback.

## 7. Integration — unify the two construction systems

- **VillageForge plan** gains `workshop` items: `{kind:"workshop", function:"kitchen|foundry|workshop|armory|study|infirmary", rect, stations:[...]}`.
  The offline generator (`forge2.py`) sizes each workshop room to hold its stations' claims + aisle + input
  storage (it can compute this from the same Size/forbidden data, dumped once).
- **`PlanExecutor`** builds the workshop shell (walls/floor/door/roof) like any building (now single-story,
  roofable — already fixed).
- **`ColonyBuilder` STOPS improvising workstations.** All station placement moves behind §6, keyed to the
  plan's workshop rooms. The only "improvised" placement that remains is the earliest survival cookfire
  before any room exists — and even that follows the geometry rules (§2) and is relocated into the kitchen
  once built.

This is the real fix for "same shit as always": one construction brain (the plan), tables inside rooms,
placed by geometry the game itself defines.

## 8. Implementation units (bounded, workflow-tracked)

- **8.0 RECONCILE / ground-truth dump. ✅ DONE (VERIFIED 2026-07-13).** `StockpilePlacer.DumpWorkstationGeometry()`
  dumps `Size`, `ForbiddenAreaInfo`, `WorkPositionsArray` for all 30 stations to
  `validation/workstation_geometry.txt` (fires on ColonyBuilder's first tick). Evidence: that file +
  the schema block. **Result corrected the plan** (forbidden≈0; work-positions are TransformSettings;
  real footprints tabulated above). Open sub-item: extract the exact `TransformSettings` position field for
  live work-cell reservation in 8.1 (proxy = free orthogonal neighbour works meanwhile).
- **8.1 Geometry-aware claim check.** Implement `StationClaim(bp, anchor, angle)` reading the dumped
  properties; add `ClaimOverlaps(existingOccupancy)` and the cropfield/stockpile/wall/path checks
  (generalize the ones already added this session). VALIDATE = unit-place two stations; second refuses
  overlapping claims; eyes-on shows a gap + reachable work cell.
- **8.2 Workshop rooms in the plan + forge2.py sizing.** Add the `workshop` item kind; size rooms from 8.0
  data. VALIDATE = plan builds a kitchen room whose interior fits cook+butcher+aisle+storage.
- **8.3 Deterministic in-room placement (§6).** Route ColonyBuilder station placement through the room
  algorithm; remove improvised open-field placement. VALIDATE = eyes-on: tables in a row along the wall,
  aisle clear, storage opposite, all built, none on the farm.
- **8.4 Category rules.** Heat/cold, indoor, adjacency, research gate (rules §4). VALIDATE = foundry not in
  the bedroom; larder by the cellar; no station placed before its research.

Acceptance for the whole plan (Chronicle Gate-1 relevant): a fresh colony builds a coherent kitchen +
workshop where every table is inside a room, spaced, on a reachable work cell, adjacent to its inputs, and
actually constructed — verified by eyes-on screenshot, not telemetry.
