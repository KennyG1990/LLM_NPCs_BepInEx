# Going Medieval — LLM NPCs — Handoff BACK to Fable

*Written 2026-07-10 by Opus, handing the work back to Fable after picking up Fable 5's
`HANDOFF_FABLE5.md`. This covers (1) what shipped this session, (2) the architectural
decisions Ken and I reasoned through — especially the memory/standalone rework — and
(3) the roadmap + hard-won gotchas. Read §2 carefully; it changes the direction.*

---

## 0. TL;DR

The planning loop is **real and live now**: the elected leader speaks through the LLM,
picks WHERE to build, and the colony builds there — validated in a live colony. WorldMap
reads the whole 3D map. Equip, roofs (root-caused + fixed), per-task models, and a
dashboard Colony tab all landed. **But Ken's verdict stands: the villagers still build the
same 2-room roofless shack, just in a better spot.** The next real work is the **house
PACKER** (a program-driven multi-room home) and **coherence** (anchor + build order), plus
two architecture threads Ken decided on: **live-modding (hot-reload)** and **making the mod
STANDALONE** (memory out of the dashboard, into mod-local JSON). Details in §2.

---

## 1. What shipped this session (✅ live-validated · ◐ compiled, live-pending)

- **TASK #25 plans/laws persistence → ✅ live.** Round-tripped `/api/plan` + `/api/laws`
  (GET/POST, replacement, step_status, 4 negative paths) via the dashboard. Corrected a
  phantom "POST wiring missing" finding that was actually the **bash-mount truncation**
  gotcha (see §4).
- **Per-task models (#30) → ✅ compile, ◐ live.** `adviser` task key added; `player_chat`
  mis-route fixed; planner/chronicle reserved→ later un-reserved. In-game per-task model
  picker UI in `MenuIntegration`. OpenRouter *live* routing never observed firing (still ◐,
  needs provider flip = spends OR budget).
- **Equip logic (#31) → ✅ detection live.** `EquipManager.cs`: hunters auto-equip a ranged
  weapon from the stockpile via the proven chain `pile.IsForbidden=false; pile.equipTarget=
  worker (private field); worker.Inventory.AddEquipOrder(pile)` → game auto-fires `EquipGoal`.
  Live: "3 hunters need a ranged weapon" detected. Positive path (a hunter actually equipping
  a bow) still ◐ — needs a weapon crafted first.
- **WorldMap (#32) → ✅ live.** `WorldMap.cs` reads the ENTIRE map: `GlobalSaveController.
  CurrentVillageData.PlayerVillage.Map.GridSpaceData` (flat `MapNode[]`) → per-column
  Surface/Cls/TowerAbove/CellarBelow. 206×206×16 = 678,976 nodes, live. **Busted the myth**
  that GoingMedievalMCP (nexus 92) rasterizes the map — it does NOT; it plots entity coords
  on a web canvas. We own the rasterizer. **Bug fixed:** `built 0` — `GridDataType` is a
  bit-flag enum WITHOUT `[Flags]`, so `.ToString()` returns raw numbers and `Contains("Building")`
  never matched. Fixed with the game's own bitwise mask (`(DataType & buildingBits)!=0`).
  Live: `built 94`.
- **Leader-voice site planning → ✅ live.** `SiteScorer.cs` (deterministic preference-weighted
  pad finder) + `HouseSitePlanner.cs` (elected leader's LLM emits a site PREFERENCE → scorer
  returns real coords). Un-reserved the `planner` task slot (first real consumer). Live:
  *"Boyd Marchmain chose (132,5,168) — flat 12x12 pad, cellar rock, clear of woods | why:
  close to camp, stone for a cellar, avoid dense forest/water."*
- **Dashboard colony tracking (#A/#B) → ✅ live.** New `/api/colony/status` (`gm_colony.py`,
  selftest 3/3, wired into do_GET/do_POST) + a **Colony overview tab** rendering census/needs/
  jobs/equip/worldmap/siteplan/alerts. HouseSitePlanner POSTs its chosen site to `/api/plan`
  (that endpoint's first real producer).
- **P2 HousePlanner v2 wire → ✅ live.** `HouseBuilder.Plan` now sites the house at the
  leader's `ChosenSite` instead of near-home. Live on save Cockhamsted: leader chose (6,126),
  house built at (16,119) — ~100 tiles from spawn (117,133). The leader's judgment now drives
  WHERE they build.
- **Roofs (#26) → ✅ root-caused + fixed, ◐ live.** STOP-AND-RESEARCH found it: decompiled
  `BuildingPlacementManager.CreateRoofs` creates the roof building but only queues it for
  settlers `if (autoconstruct)` — and `SpawnRoofAutoTesting` never sets that flag. So a roof
  ghost was created (our count-check lied "PLACED") but never built = "rain on beds" for months.
  FIX: `TryPlaceRoofAt` sets `BuildingPlacementManager.Autoconstruct=true` (save/restore) before
  the call. **Secondary risk to watch:** `CanPlaceRoof` may need FINISHED walls — if it rejects,
  gate the roof step on walls-done.
- **Live-mod Unit 1 → ✅ compile.** `Plugin` keeps the Harmony instance as `_harmony` and calls
  `_harmony.UnpatchSelf()` in `OnDestroy` so a hot-reload won't stack duplicate patches.

New files: `WorldMap.cs`, `SiteScorer.cs`, `HouseSitePlanner.cs`, `EquipManager.cs`,
`dashboard/gm_colony.py`. Touched: `HouseBuilder`, `StockpilePlacer` (roofs), `ColonyBuilder`,
`ColonyAlerts`, `MenuIntegration`, `Plugin`, `LLMClient`, `dashboard_server.py`, `app.js`,
`index.html`. Latest deployed DLL when writing: `ea6c01fa` (P2 wire); roof + Harmony fixes are
BUILT but not deployed (would kill the running colony).

---

## 2. ARCHITECTURE DECISIONS — read this, it changes direction

### 2a. Ken's coherence verdict (the villagers don't act like villagers)
Screenshot proof: same 2-room roofless shack, now on a mountain. Ken's callout, and he's right:
1. **The settlement is SPLIT.** The leader's site moved the HOUSE, but stockpile/cook/beds still
   cluster at spawn — so the villagers built a lonely shack a long walk from their food and beds.
   FIX: move the colony ANCHOR to `ChosenSite` so ALL infra clusters there (currently only the
   house uses it).
2. **Build order is backwards.** They build stockpiles/cookfire/beds/fletcher BEFORE the house.
   Real settlers build SHELTER first. FIX: shelter-first ordering in `ColonyBuilder`.
3. **It's a shack, not a home, and it has no roof.** The house layout is a HARDCODED template
   (`HouseBuilder` N=4, fixed 2-room). That's the real reason it's always the same building.

### 2b. Real houses ARE possible — no hand-authored blueprints (Ken asked directly)
The shack is a template, not a limitation. We already: place buildings **cell-by-cell**
(`StockpilePlacer.TryPlaceBuildingAt` — NOT limited to game prefabs), have **full spatial
awareness** (WorldMap), and the LLM already emits JSON. The **one missing piece is the PACKER.**
Agreed architecture (Ken chose the ambitious version):
> **LLM writes a ROOM PROGRAM** (what rooms, sizes, WHY — 1 private bedroom per settler for the
> privacy need, indoor kitchen for the rain, pantry, workshop; sized to pop + growth) →
> **deterministic PACKER** lays rooms along a corridor spine on the chosen WorldMap pad, doors so
> nobody walks through a bedroom, validates every cell → **existing per-cell builders** execute →
> **ROOFED.** Judgment (which rooms/why) = LLM; geometry = packer. ~110 interior tiles.
Ken's picks: **FULL program** (bedrooms+common+kitchen+pantry+workshop) and **LLM emits the room
program** (max thought-into-output). Roofs are the prerequisite (a roofless 110-tile home is worse
than the shack) — roof fix is done, validate it first.

### 2c. API strategy — stop reverse-engineering piecemeal
- Build a **complete API index**: full-assembly decompile of `Assembly-CSharp.dll` (or a member-
  signature dump) to a LOCAL file, so "does function X exist / what's its signature" becomes a grep,
  not an ILSpy session. `GameApiScanner.cs` already exists but only dumps a keyword-filtered slice —
  extend it (or run ilspycmd on the whole DLL).
- **Do NOT build an "X4 Forge for Going Medieval."** Wrong pattern-match: Forge is a heavy visual
  IDE because X4 modding is *text authoring* (MD/Lua/XML against a schema). GM modding is *C# runtime
  against a reflection API* — no text to validate. The dashboard already gives us the runtime harness.
  The right asset is a **consolidated `GameApi` layer** (fold the scattered reflection in
  `StockpilePlacer`/`GameBridge`/`WorldMap`/`EquipManager` into one tested library) — a byproduct of
  building features, not a separate IDE.
- Ask the *devs* (Foxy Voxel Discord) only for what the index can't tell us: is there a **stable
  modding API**, which namespaces are stable vs internal, and intent/preconditions on placement,
  roofs, goals, save data. `DEV_QUESTIONS` list is in the chat log if Ken wants it as a file.

### 2d. THE DASHBOARD IS DEV-ONLY — the mod must ship standalone (Ken's hard requirement)
Ken: the dashboard was only ever meant to **visualize** what the DB tracks so he can spot gaps.
The released mod must run **without** it.
- **Standalone RELEASE BLOCKER found:** the mod doesn't just write to the dashboard — it **reads
  memory back** at runtime: `MemoryManager` does `GET /api/memory/context` before every LLM decision
  (`MemoryManager.cs:247,284,441`). Dashboard off → NPCs lose memory/personality → LLM decides blind.
- **Why the DB is in the dashboard at all:** `deploy.bat` DELETES `System.Data.SQLite.dll` +
  `SQLite.Interop.dll` — the mod once had C# SQLite but the **native-lib packaging** was a nightmare,
  so the DB got parked in Python (which has SQLite for free). Not a design choice; a workaround.

### 2e. MEMORY INDEPENDENCE — the plan (this is the big one)
Move the memory store + RoleRAG **into the mod**, standalone. Key realizations from the discussion:
- **RoleRAG is LOGIC, not a database feature.** `gm_rolerag.py` is 186 lines of **keyword + graph**
  retrieval (NOT embeddings/vectors): build a name index → match names in the message → pull those
  characters' relationships + memories, filter by keyword, sort by importance/recency, take top-k.
  It ports to C# almost line-for-line.
- **RoleRAG runs over JSON fine.** The SQL `SELECT ... WHERE content LIKE ? ORDER BY importance DESC,
  timestamp DESC LIMIT k` becomes C# `memories.Where(...).OrderByDescending(m=>m.importance)
  .ThenByDescending(m=>m.timestamp).Take(k)`. Same filter, same ranking, no SQLite. At colony scale
  (a few NPCs, hundreds–low-thousands of memories) loading it all into memory is instant and *faster*
  than a query engine. SQLite's scale advantage is irrelevant here.
- **Fable 5 was HALF right.** The RoleRAG *retrieval* IS superior to a naive memory dump — keep it.
  But SQLite *storage* was over-engineering for this scale, and it's what caused the coupling. Keep
  the ranking logic; drop the DB engine.
- **Storage = mod-local JSON, no native libs.** Save separation = **folder-per-save**
  (`LLM_NPCs/saves/{saveId}/memories.json|relationships.json|npcs.json`), which is cleaner than a
  `save_id` column (physical separation → no cross-save leaks; only load the active save; new save =
  new folder). Ken confirmed this is the target.
- **Don't break the dashboard: DUAL-WRITE.** Mod writes JSON (source of truth) AND keeps POSTing to
  the dashboard (fail-silent) → the dashboard's views keep working as a **read-only mirror/window**.
  The mod flips its READ from `GET /api/memory/context` to a local C# RoleRAG. Dashboard off = mod
  fine, just no window to peek through.
- **Wrap-the-dashboard-in-the-game idea (Ken proposed):** great for DEV convenience — the mod can
  `Process.Start` the dashboard on load and kill it on exit so Ken never runs the bat (DO THIS, ~10
  lines). But NOT a release strategy: it'd mean shipping a **bundled Python web server** to players
  (PyInstaller, tens of MB, antivirus false-positives, port conflicts) and only HIDES the coupling.
  So: **auto-launch dashboard for dev + JSON memory for release** — not either/or.
- **Difficulty:** moderate + LOW risk (~65% clean job, ~25% friction matching retrieval *quality*,
  ~10% surprise). The seam is clean (HTTP) and RoleRAG is keyword-based. **Validate memory-parity:
  an NPC must pull the SAME memories with the dashboard OFF as it did with it ON.**

### 2f. Live-modding (hot-reload) — Ken wants it
Can't skip **compile** (C#). CAN skip the **restart + save-reload** (the ~2-min colony-losing step
we did a dozen times). BepInEx `ScriptEngine`: deploy the DLL to `BepInEx/scripts/`, press F6 →
reloaded into the RUNNING game, colony intact. Loop becomes edit → build → F6. Unit 1 (Harmony
unpatch) DONE. Unit 2: install ScriptEngine, add a scripts/ deploy target, prove F6 takes a change
live. Caveat: heavy statics — reset behavior on reload needs testing (BuiltState re-detects on load,
which helps).

---

## 3. Roadmap / open tasks (prioritized)

**Highest value (make them act like villagers):**
1. **PACKER (#27)** — room program → multi-room floor plan on the WorldMap pad. The real house.
2. **Coherence (#28)** — move colony anchor to `ChosenSite` (unify infra) + shelter-first build order.
3. **Roofs live-validation** — deploy the roof fix, build a house to its roof step, confirm settlers
   construct the roof + it survives reload. (Fix is done; watch the CanPlaceRoof-needs-walls risk.)

**Architecture threads Ken committed to:**
4. **Memory independence (#31)** — JSON store + C# RoleRAG + dual-write. Standalone RELEASE BLOCKER.
5. **Live-mod Unit 2 (#30)** — ScriptEngine hot-reload.
6. **Complete API index + `GameApi` layer** (§2c).
7. **Auto-launch the dashboard from the mod** for dev convenience (§2e).

**◐ closeouts (cheap-ish):**
8. **OpenRouter live (#21)** — flip provider to observe a real per-task call (spends OR budget).
9. **Equip positive path (#22)** — a hunter equips a bow once one is crafted.

**Housekeeping:**
10. **File cleanup (#25)** — repo root has a pile of `.tmp_*` scratch files (Ken flagged; confirm
    the removal list before deleting — deletion is destructive).

---

## 4. AAR — gotchas that cost time this session (don't repeat)

- **BASH-MOUNT TRUNCATION (bit twice).** The sandbox bash mount silently truncates large files
  (`dashboard_server.py` looked like it ended at ~3180 lines; it's 3236). Nearly "fixed" a phantom
  missing-POST-dispatch bug that was real code. USE the Read tool (real Windows files) or `git show
  HEAD:` (object store) for anything > ~3k lines; a file ending mid-statement with no `main()` is the tell.
- **WRONG SAVE_ID / WRONG DB spiral (worst near-miss).** Spent a long time convinced the new DLL
  "wasn't running" — it was fine. I was validating `/api/colony/status` against **"Aberystwyth"** (a
  stale test-seed) while the live colony was **"Thorney"**, and looking for the DB in the dashboard
  folder when it lives at `%APPDATA%\Going Medieval\...`. ALWAYS resolve the ACTIVE save from the live
  game; trust the dashboard DB/tab, not the flooding log or a stale seed row.
- **STALE-DLL gotcha.** After kill, the kill-poll must see a **sustained "dead" streak** (I use 5s)
  before deploy+launch — a single "not running" reading fires while the process still holds the DLL,
  and the game comes up on OLD code. `dll_in_sync` + deployed-sha == built-sha before trusting a run.
- **LOG BUFFER floods.** `/api/dev/log` "mod" category is ~120 lines and the NPC pipeline floods it —
  one-shot markers (`[HouseBuilder] siting...`, worldmap scan, HouseSitePlanner) scroll out in seconds.
  Validate from `colony_status.txt` / `/api/colony/status` telemetry, not the log.
- **GAME WINDOW FOCUS.** Background File Explorer windows steal input focus → game menu clicks register
  as *hover* but not *press*. Activate the game window (click its title bar / bring it foreground) before
  driving menus. `/api/game/input` force-focuses; direct computer-use clicks don't.
- **CHROME guard.** In-page `fetch` results that contain URL/query-ish strings get `[BLOCKED]` — return
  only sanitized booleans/counts from `javascript_tool`, never raw log lines.
- **`GridDataType` is NOT `[Flags]`** — `.ToString()` on a combined value returns a number. Use bitwise
  masks, not string matching, for any flags enum from this game.

---

## 5. File map (what's where)
- `src/` — mod modules. New this session: `WorldMap.cs`, `SiteScorer.cs`, `HouseSitePlanner.cs`,
  `EquipManager.cs`. Roof fix in `StockpilePlacer.cs`; leader-site wire in `HouseBuilder.cs`;
  Harmony hot-reload in `Plugin.cs`.
- `dashboard/` — Python dev observer. New: `gm_colony.py`. Memory/RoleRAG: `gm_rolerag.py`,
  `gm_systems.py`, `gm_plans.py`. Server: `dashboard_server.py` (3236 lines — use Read tool).
- `validation/decompiled/` — decompiled ground truth (roofs, MapNode, equipment, etc.).
- `validation/worldmap.txt`, `colony_status.txt` — telemetry (read from mount).
- `BACKLOG.md` — the running log; TASK #30–#35 hold this session's specs + closes.
- `HANDOFF_FABLE5.md` — the handoff I picked up from.

*The dashboard runs `start_dashboard.bat` (repo root). It crashed once mid-session — if the browser
hits chrome-error and /health fails, restart it (Opus has File Explorer access to double-click the bat).*
