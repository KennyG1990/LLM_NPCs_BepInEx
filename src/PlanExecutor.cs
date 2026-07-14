using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// UNIT C (Chronicle Test): executes a VillageForge plan.json in-game.
    /// The forge is the architect (offline, visually validated); this module is
    /// the hands — it turns each `new_work` item into blueprints via the SAME
    /// proven actuators the improvised builders use, then gets out of the way.
    ///
    /// Channel: validation\active_plan.json (written by forge2.py / the operator).
    /// Polled by mtime+length hash; a hash persisted in BuiltState as done never
    /// restarts. No file → module inert, colony behaves exactly as before.
    ///
    /// v1 scope (spec: BACKLOG 2026-07-13 ~03:00):
    ///  - the FIRST "house" item is ADOPTED into HouseBuilder (AdoptForgeRect),
    ///    so beds/roof strips/move-in/persistence keep their one owner;
    ///  - remaining rect+walls items are built here as shells: floors → walls →
    ///    door (batched, budgeted, BuildingExistsAt-idempotent);
    ///  - field/graveyard/gate/wall_ring/towers/cellar/upper_floors: logged
    ///    DEFERRED once (v1.1+ — FarmPlanner/DefenseBuilder own some already).
    ///
    /// Laws honored: main-thread only; 40ms budget checked per PLACEMENT;
    /// snapshot-first cell checks; never act on guessed ids (ids come verbatim
    /// from the plan, defaulting to the proven wood_* set).
    /// </summary>
    public static class PlanExecutor
    {
        public static string LastStep = "(no plan)";

        private const string PlanPath =
            @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\active_plan.json";
        private const int BatchCap = 24;      // placements per pass (HouseBuilder's proven cap)
        private const int BudgetMs = 40;

        private sealed class Item
        {
            public string Kind = "";
            public int X, Z, W, H;
            public int Floors = 1;   // forge multi-story (fidelity)
            public string WallId = "wood_wall_element", DoorId = "wood_door", FloorId = "wood_floor";
            public string Status = "pending";   // pending|adopted|building|done|failed|deferred
            public string Note = "";
            public int Ay = -1;                 // resolved surface level
            public int FloorIdx, WallIdx, RoofIdx;   // own-shell progress (BuildingExistsAt fast-forwards on reload)
            public bool DoorPlaced;
            public int RoofWaitTicks;                // ticks the roof has waited for walls to finish
        }

        private static readonly List<Item> _items = new List<Item>();
        private static string _loadedHash = "";
        private static float _nextPollAt;
        private static bool _adoptionDone;      // first house handed to HouseBuilder this plan

        public static bool HasPlan => _items.Count > 0;
        public static bool HasPendingWork
        {
            get
            {
                foreach (var it in _items)
                    if (it.Status == "pending" || it.Status == "building") return true;
                return false;
            }
        }

        public static void Reset()
        {
            _items.Clear();
            _loadedHash = "";
            _nextPollAt = 0f;
            _adoptionDone = false;
            LastStep = "(no plan)";
        }

        /// <summary>Cheap mtime/length poll (30s cadence). Loads or reloads the
        /// plan when the file changes; a hash persisted as done stays done.</summary>
        public static void Poll()
        {
            try
            {
                if (UnityEngine.Time.realtimeSinceStartup < _nextPollAt) return;
                _nextPollAt = UnityEngine.Time.realtimeSinceStartup + 30f;
                var fi = new System.IO.FileInfo(PlanPath);
                if (!fi.Exists)
                {
                    if (_items.Count > 0) Reset();
                    return;
                }
                string hash = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
                if (hash == _loadedHash) return;                       // already loaded THIS session
                // RE-VERIFY ON RELOAD (2026-07-13, Ken eyes-on: shells were left
                // roofless). We no longer SKIP a done-hash-matched plan on reload —
                // we RE-LOAD and re-run it. Placement is idempotent (BuildingExistsAt
                // fast-forwards built floors/walls/doors), so re-running only fills
                // gaps — chiefly the missing ROOFS. Within a session _loadedHash
                // still prevents redundant reloads; the done-hash is just telemetry now.
                Load(hash);
            }
            catch (Exception ex) { LastStep = "plan poll error: " + ex.Message; }
        }

        private static void Load(string hash)
        {
            _items.Clear();
            _adoptionDone = false;
            var doc = JObject.Parse(System.IO.File.ReadAllText(PlanPath));
            var work = doc["new_work"] as JArray;
            if (work == null) { LastStep = "plan file has no new_work array"; return; }
            int deferred = 0;
            foreach (var w in work)
            {
                var o = w as JObject;
                if (o == null) continue;
                string kind = o.Value<string>("kind") ?? "?";
                var rect = o["rect"] as JArray;
                bool shell = rect != null && rect.Count == 4 && o["walls"] != null;
                if (!shell)
                {
                    _items.Add(new Item { Kind = kind, Status = "deferred", Note = "kind not executable in v1" });
                    deferred++;
                    continue;
                }
                var it = new Item
                {
                    Kind = kind,
                    X = (int)rect[0], Z = (int)rect[1], W = (int)rect[2], H = (int)rect[3],
                    // PHASE-1 = BUILDABLE SINGLE-STORY (Ken, 2026-07-13): every building
                    // in the plan was floors=2, but PlanExecutor only builds the ground
                    // shell and defers the upper floor — so the roof anchored at y=+2
                    // over a second story that never existed and floated forever. Every
                    // plan building came out a roofless box. Until multi-story roofing
                    // (upper walls actually built + roof on top) is proven, cap phase-1
                    // buildings to 1 story so the roof lands. Multi-story returns when
                    // PlanExecutor builds the upper_floors it currently defers.
                    Floors = 1,
                    WallId = o.Value<string>("walls") ?? "wood_wall_element",
                    DoorId = o.Value<string>("door") ?? "wood_door",
                    FloorId = o.Value<string>("floor") ?? "wood_floor",
                };
                if (o["cellar"] != null || o["upper_floors"] != null)
                    it.Note = "cellar/upper floors deferred (v1 builds the ground shell)";
                _items.Add(it);
            }
            _loadedHash = hash;
            LastStep = $"plan loaded: {_items.Count} items ({deferred} deferred)";
            LLMNPCsPlugin.LogToFile($"[PlanExecutor] {LastStep} — phase {doc.Value<int?>("phase") ?? 0}, style '{(doc["style"] as JObject)?.Value<string>("name") ?? "?"}'");
        }

        /// <summary>Resolve the build level for an item's rect from the worldmap
        /// snapshot. Requires a FLAT pad (forge picks flat ground; a mismatch
        /// means the plan and the world disagree — fail honestly, never guess).</summary>
        private static bool ResolveLevel(Item it)
        {
            if (WorldMap.LastScanTicks == 0) return false;     // snapshot not ready — retry later
            // RE-VERIFY (2026-07-13, Ken eyes-on): if this building is ALREADY built,
            // use its actual FLOOR level and SKIP the flat-pad check. The built walls
            // raise the re-scanned surface (6..7), which was falsely failing a
            // building that's already standing (so its roof never got added). Probe
            // an interior floor cell across plausible levels.
            int ix = it.X + 1, iz = it.Z + 1;
            for (int y = 3; y <= 14; y++)
                if (StockpilePlacer.BuildingExistsAt(ix, y, iz, it.FloorId)) { it.Ay = y; return true; }
            int lo = int.MaxValue, hi = int.MinValue;
            for (int x = it.X; x < it.X + it.W; x++)
                for (int z = it.Z; z < it.Z + it.H; z++)
                {
                    if (x < 0 || z < 0 || x >= WorldMap.SizeX || z >= WorldMap.SizeZ)
                    { it.Status = "failed"; it.Note = "rect outside the map"; return false; }
                    int s = WorldMap.Surface[x, z];
                    if (s < lo) lo = s;
                    if (s > hi) hi = s;
                }
            if (lo < 0 || lo != hi)
            {
                it.Status = "failed";
                it.Note = $"pad not flat (surface {lo}..{hi}) — plan/world mismatch";
                LLMNPCsPlugin.LogToFile($"[PlanExecutor] {it.Kind} at ({it.X},{it.Z}) FAILED: {it.Note}");
                return false;
            }
            it.Ay = lo;
            return true;
        }

        /// <summary>Hand the first pending house to HouseBuilder. Called every
        /// tick BEFORE HouseBuilder.Step so forge siting beats improvisation.
        /// No plan / already handed / HouseBuilder busy → cheap no-op.</summary>
        public static void TryAdoptFirstHouse()
        {
            try
            {
                if (_adoptionDone || _items.Count == 0) return;
                if (HouseBuilder.IsPlanned || HouseBuilder.Complete) { _adoptionDone = true; return; }
                foreach (var it in _items)
                {
                    if (it.Kind != "house" || it.Status != "pending") continue;
                    if (it.Ay < 0 && !ResolveLevel(it)) return;   // not scanned yet or failed
                    if (it.Status == "failed") continue;          // try the next house
                    // SURVIVAL SHELTER = SINGLE STORY (Duninc, 2026-07-13): the FIRST
                    // house must get every settler under a roof by night 2 (Gate 1).
                    // A plan-specified 2-story first house set _roofLevel=ay+2, so the
                    // roof anchored at y=7 on an UPPER story that never got built on a
                    // fresh 3-settler colony — the roof strip floated ("support(wallish)
                    // below=n") and rejected forever, leaving beds roofless & settlers
                    // unhappy. Force the survival house to 1 story; multi-story is for
                    // later village growth (subsequent plan shells keep their floors).
                    if (HouseBuilder.AdoptForgeRect(it.X, it.Z, it.Ay, it.W, it.H, 1))
                    {
                        it.Status = "adopted";
                        _adoptionDone = true;
                        LastStep = $"house #1 adopted by HouseBuilder ({it.W}x{it.H} at {it.X},{it.Z}) — forced 1-story (survival shelter)";
                    }
                    return;
                }
                _adoptionDone = true;                             // no house in this plan
            }
            catch (Exception ex) { LastStep = "adopt error: " + ex.Message; }
        }

        /// <summary>Build the next own-shell item (floors → walls → door), batched
        /// and budgeted. Returns a status line when work happened / is pending,
        /// null when the executor has nothing to do this tick.</summary>
        public static string Step(GameObject settlerGo)
        {
            try
            {
                Item it = null;
                foreach (var c in _items)
                    if (c.Status == "pending" || c.Status == "building") { it = c; break; }
                if (it == null) { MarkDoneIfFinished(); return null; }

                if (it.Ay < 0 && !ResolveLevel(it))
                {
                    if (it.Status == "failed") return LastStep = $"{it.Kind}: {it.Note}";
                    // Snapshot not scanned yet (post-reload rescans take minutes on
                    // big maps): YIELD the tick — never starve the priorities below
                    // while waiting on something no work can accelerate.
                    LastStep = "waiting for worldmap snapshot to place plan items";
                    return null;
                }
                if (it.Status == "failed") return LastStep = $"{it.Kind}: {it.Note}";
                it.Status = "building";

                var sw = System.Diagnostics.Stopwatch.StartNew();
                int placed = 0;

                // FLOORS: interior cells (the forge shell matches HouseBuilder's convention).
                var interior = new List<int[]>();
                var perimeter = new List<int[]>();
                var door = new[] { it.X + it.W / 2, it.Z };
                for (int x = it.X; x < it.X + it.W; x++)
                    for (int z = it.Z; z < it.Z + it.H; z++)
                    {
                        if (x == door[0] && z == door[1]) continue;
                        if (x == it.X || x == it.X + it.W - 1 || z == it.Z || z == it.Z + it.H - 1)
                            perimeter.Add(new[] { x, z });
                        else interior.Add(new[] { x, z });
                    }

                while (it.FloorIdx < interior.Count && placed < BatchCap && sw.ElapsedMilliseconds < BudgetMs)
                {
                    var c = interior[it.FloorIdx++];
                    if (StockpilePlacer.BuildingExistsAt(c[0], it.Ay, c[1], it.FloorId)) continue;
                    StockpilePlacer.TryPlaceBuildingAt(c[0], it.Ay, c[1], it.FloorId);
                    placed++;
                }
                if (it.FloorIdx < interior.Count)
                    return LastStep = $"{it.Kind} floors {it.FloorIdx}/{interior.Count} (batched {placed})";

                while (it.WallIdx < perimeter.Count && placed < BatchCap && sw.ElapsedMilliseconds < BudgetMs)
                {
                    var c = perimeter[it.WallIdx++];
                    if (StockpilePlacer.BuildingExistsAt(c[0], it.Ay, c[1], it.WallId)) continue;
                    StockpilePlacer.TryPlaceBuildingAt(c[0], it.Ay, c[1], it.WallId);
                    placed++;
                }
                if (it.WallIdx < perimeter.Count)
                    return LastStep = $"{it.Kind} walls {it.WallIdx}/{perimeter.Count} (batched {placed})";

                if (!it.DoorPlaced)
                {
                    it.DoorPlaced = true;
                    if (!StockpilePlacer.BuildingExistsAt(door[0], it.Ay, door[1], it.DoorId))
                    {
                        // conflict guard (HouseBuilder's law): never stack a door on another piece
                        if (StockpilePlacer.AnyBuildingAt(door[0], it.Ay, door[1]))
                            LLMNPCsPlugin.LogToFile($"[PlanExecutor] {it.Kind} door cell ({door[0]},{it.Ay},{door[1]}) occupied — SKIPPED (conflict guard)");
                        else
                            StockpilePlacer.TryPlaceBuildingAt(door[0], it.Ay, door[1], it.DoorId);
                    }
                }

                // ROOF (2026-07-13, Ken eyes-on: shells were left ROOFLESS — the
                // building isn't done until it's roofed). One strip per z-row at
                // ay+1; a strip only places once its supporting WALLS are built, so
                // reject = wait, NOT skip. Item stays "building" until fully roofed.
                int rz1 = it.Z + it.H - 1;
                while (it.RoofIdx <= (it.H - 1) && placed < BatchCap && sw.ElapsedMilliseconds < BudgetMs)
                {
                    int rowZ = it.Z + it.RoofIdx;
                    var rres = StockpilePlacer.TryPlaceRoofStrip(it.X, it.X + it.W - 1, it.Ay + 1, rowZ, "wood_roof_whole");
                    if (rres.StartsWith("ok:")) { it.RoofIdx++; it.RoofWaitTicks = 0; placed++; }
                    else break;   // walls not constructed yet — retry next tick (never skip)
                }
                if (it.RoofIdx <= (it.H - 1))
                {
                    it.RoofWaitTicks++;
                    if (it.RoofWaitTicks > 200)   // ~40 min of no progress = a real geometry problem, surface it
                    { it.Status = "failed"; it.Note = $"roof stuck at row {it.RoofIdx}/{it.H} — walls/support issue"; LLMNPCsPlugin.LogToFile($"[PlanExecutor] {it.Kind} at ({it.X},{it.Z}) {it.Note}"); }
                    return LastStep = $"{it.Kind} roof {it.RoofIdx}/{it.H} (waiting for walls to build, {it.RoofWaitTicks}t)";
                }

                it.Status = "done";
                if (!string.IsNullOrEmpty(it.Note))
                    LLMNPCsPlugin.LogToFile($"[PlanExecutor] {it.Kind} at ({it.X},{it.Z}) shell done — {it.Note}");
                LLMNPCsPlugin.LogToFile($"[PlanExecutor] {it.Kind} shell blueprinted at ({it.X},{it.Z}) {it.W}x{it.H} lvl {it.Ay}");
                MarkDoneIfFinished();
                return LastStep = $"{it.Kind} shell done at ({it.X},{it.Z})";
            }
            catch (Exception ex) { return LastStep = "step error: " + ex.Message; }
        }

        /// <summary>All items terminal AND the adopted house finished → persist the
        /// done-hash so this plan never re-executes on reload.</summary>
        private static void MarkDoneIfFinished()
        {
            if (_items.Count == 0 || HasPendingWork) return;
            foreach (var it in _items)
                if (it.Status == "adopted" && !HouseBuilder.Complete) return;
            if (BuiltState.PlanExecDoneHash == _loadedHash) return;
            BuiltState.PlanExecDoneHash = _loadedHash;
            int done = 0, failed = 0, deferred = 0;
            foreach (var it in _items)
            {
                if (it.Status == "done" || it.Status == "adopted") done++;
                else if (it.Status == "failed") failed++;
                else deferred++;
            }
            LastStep = $"plan complete: {done} built, {failed} failed, {deferred} deferred";
            LLMNPCsPlugin.LogToFile($"[PlanExecutor] {LastStep}");
        }

        /// <summary>One-line census for the telemetry file.</summary>
        public static string Census()
        {
            if (_items.Count == 0) return LastStep;
            int done = 0, failed = 0, deferred = 0, pending = 0;
            foreach (var it in _items)
            {
                if (it.Status == "done" || it.Status == "adopted") done++;
                else if (it.Status == "failed") failed++;
                else if (it.Status == "deferred") deferred++;
                else pending++;
            }
            return $"{done} done / {pending} pending / {failed} failed / {deferred} deferred — {LastStep}";
        }
    }
}
