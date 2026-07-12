using System.Collections.Generic;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Builds a small HOUSE with TWO CONNECTED ROOMS autonomously, one piece per
    /// tick, so the game's settlers construct it as it goes. Instead of an
    /// isolated box, it lays out a real floor plan: a footprint N wide x (2N-1)
    /// deep split by a shared middle wall into two NxN rooms, with a DOORWAY in
    /// that middle wall so the rooms CONNECT, plus an exterior door into the front
    /// room. Every piece is gated by CanPlace (no water/invalid terrain) and
    /// placed via the game's own no-cursor commit (StockpilePlacer.TryPlaceBuildingAt).
    ///
    /// Order: FLOORS -> perimeter + divider WALLS -> the two DOORS (exterior +
    /// connecting) -> ROOF over each room's interior.
    ///
    /// IDEMPOTENT ACROSS RELOADS (the save-bloat fix):
    ///   - The chosen site is persisted per save (BuiltState). After a reload the
    ///     plan is RE-ADOPTED at the same origin and verified against the world's
    ///     actual buildings — never re-planned onto a fresh clear spot next to
    ///     the old house.
    ///   - Step() checks the world per-cell (BuildingExistsAt) before placing, so
    ///     pieces that already exist in the loaded save are skipped, not re-placed.
    ///   - Roof progress is persisted (roofs are components with no cheap per-cell
    ///     query); the safe failure is a missing roof, never duplicate components.
    /// </summary>
    public static class HouseBuilder
    {
        private const string Wall = "wood_wall_element";
        private const string Door = "wood_door";
        private const string Floor = "wood_floor";
        private const string Roof = "wood_roof_whole";
        private const int N = 4; // each room is N x N; footprint is N x (2N-1)

        private static bool _planned = false;
        private static int _ay;
        private static readonly List<int[]> _wallCells = new List<int[]>();
        private static readonly List<int[]> _doorCells = new List<int[]>();
        private static readonly List<int[]> _roofCells = new List<int[]>();
        // #31 Packer (v2 plans): purpose-tagged cells the rest of the colony
        // wires to — beds go in dorm/bedrooms, the hearth in the hall-spine,
        // the pantry gets an INDOOR stockpile zone (goods out of the rain).
        private static readonly List<int[]> _bedCells = new List<int[]>();
        private static readonly List<int[]> _pantryCells = new List<int[]>();
        private static int[] _hearthCell = null;
        private static string _roomsSummary = "";
        public static int TargetPop = 4;       // set by ColonyBuilder each tick
        private static int _wallIdx = 0, _doorIdx = 0, _roofIdx = 0, _floorIdx = 0;
        private static int _roofRetries = 0;   // consecutive rejections of the CURRENT row
        private static string _reason = "";

        public static bool Complete = false;
        public static string LastStep = "(idle)";

        // Exposed so the builder can place BEDS inside the rooms (on the floor of
        // the interior cells) instead of scattering them outside.
        public static bool IsPlanned => _planned;
        public static int Level => _ay;
        public static List<int[]> InteriorCells => _roofCells;
        public static List<int[]> BedCells => _bedCells.Count > 0 ? _bedCells : _roofCells;
        public static List<int[]> PantryCells => _pantryCells;
        public static int[] HearthCell => _hearthCell;
        public static string RoomsSummary => _roomsSummary;

        /// <summary>Clear ALL per-session state. Called by BuiltState when a
        /// world (re)load is detected, before any tick can act on stale flags.</summary>
        public static void Reset()
        {
            _planned = false;
            Complete = false;
            _ay = 0;
            _wallCells.Clear(); _doorCells.Clear(); _roofCells.Clear();
            _bedCells.Clear(); _pantryCells.Clear(); _hearthCell = null; _roomsSummary = "";
            _wallIdx = _doorIdx = _roofIdx = _floorIdx = 0;
            _roofRetries = 0;
            _reason = "";
            LastStep = "(idle)";
        }

        // Footprint bounds from the wall list (strips span wall-to-wall).
        /// <summary>Grid center of the planned/built house (null before planning) —
        /// the colony's move-in target once the shelter is complete (#32).</summary>
        public static int[] HouseCenter => _planned && _wallCells.Count > 0
            ? new[] { (MinX() + MaxX()) / 2, _ay, (MinZ() + MaxZ()) / 2 } : null;

        private static int MinX() { int m = int.MaxValue; foreach (var c in _wallCells) if (c[0] < m) m = c[0]; return m == int.MaxValue ? 0 : m; }
        private static int MaxX() { int m = int.MinValue; foreach (var c in _wallCells) if (c[0] > m) m = c[0]; return m == int.MinValue ? 0 : m; }
        private static int MinZ() { int m = int.MaxValue; foreach (var c in _wallCells) if (c[1] < m) m = c[1]; return m == int.MaxValue ? 0 : m; }
        private static int MaxZ() { int m = int.MinValue; foreach (var c in _wallCells) if (c[1] > m) m = c[1]; return m == int.MinValue ? 0 : m; }

        /// <summary>Deterministically generate the full cell layout for a house</summary>
        /// whose footprint origin is (ox,oz) at level ay, with the exterior door
        /// on the perimeter cell closest to home (hx,hz). Same code path for a
        /// FRESH plan and for RE-ADOPTING a persisted plan after reload, so the
        /// cells always come out identical for the same inputs.</summary>
        private static void Layout(int ox, int oz, int ay, int hx, int hz)
        {
            _wallCells.Clear(); _doorCells.Clear(); _roofCells.Clear();
            int depth = 2 * N - 1;
            int zDiv = oz + N - 1;              // shared middle wall row

            // A settler puts the front door on the side FACING HOME, so it's a
            // short walk from the hearth. Pick the front-room perimeter cell
            // (non-corner) closest to home.
            var candidates = new List<int[]>();
            for (int x = ox + 1; x < ox + N - 1; x++) candidates.Add(new[] { x, oz });
            for (int z = oz + 1; z < zDiv; z++) { candidates.Add(new[] { ox, z }); candidates.Add(new[] { ox + N - 1, z }); }
            int[] extDoor = candidates.Count > 0 ? candidates[0] : new[] { ox + 1, oz };
            int bestD = int.MaxValue;
            foreach (var c in candidates)
            {
                int d = System.Math.Abs(c[0] - hx) + System.Math.Abs(c[1] - hz);
                if (d < bestD) { bestD = d; extDoor = c; }
            }
            var connDoor = new[] { ox + 1, zDiv };         // doorway BETWEEN the two rooms

            for (int x = ox; x < ox + N; x++)
                for (int z = oz; z < oz + depth; z++)
                {
                    bool perimeter = (x == ox || x == ox + N - 1 || z == oz || z == oz + depth - 1);
                    bool divider = (z == zDiv);
                    bool isDoor = (x == extDoor[0] && z == extDoor[1]) || (x == connDoor[0] && z == connDoor[1]);
                    if (isDoor) continue;
                    if (perimeter || divider) _wallCells.Add(new[] { x, z });
                    else _roofCells.Add(new[] { x, z }); // interior of a room
                }
            _doorCells.Add(extDoor);
            _doorCells.Add(connDoor);
            _ay = ay;
        }

        // ── #31 PACKER: corridor-spine longhouse generator (v2 plans) ─────────
        // Direct port of the ASCII-validated prototype (validation/
        // packer_designs.txt). pop>=5: 2-wide hall-spine with the hearth IN it;
        // pop<=4: 1-wide corridor + a hall room. Rooms by function (dorm/
        // bedrooms/pantry/workshop), every room doored onto the spine, last
        // room per strip consumes leftover width (no sealed chambers), footprint
        // capped to the 12x12 site pad by shrinking the widest rooms.
        private sealed class RoomSpec { public string Name; public int W; public RoomSpec(string n, int w) { Name = n; W = w; } }

        private static readonly Dictionary<string, int[]> _sizes = new Dictionary<string, int[]>
        { { "dorm", new[] { 4, 6 } }, { "pantry", new[] { 3, 4 } }, { "workshop", new[] { 4, 5 } }, { "hall", new[] { 3, 5 } }, { "bedroom", new[] { 3, 3 } } };

        /// <summary>THE ARCHITECT (#31 slice B): an LLM-authored room program —
        /// "name:width,name:width". When persisted (house.program), LayoutV2
        /// packs THESE rooms instead of the deterministic defaults, so the
        /// building reflects the architect's judgment. Same corridor-spine
        /// packing either way — that geometry is the proven buildable form.</summary>
        private static List<RoomSpec> ParseProgram(string program)
        {
            var rooms = new List<RoomSpec>();
            if (string.IsNullOrEmpty(program)) return rooms;
            foreach (var part in program.Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length != 2 || !_sizes.ContainsKey(kv[0].Trim())) continue;
                int w; if (!int.TryParse(kv[1].Trim(), out w)) continue;
                var name = kv[0].Trim();
                w = System.Math.Max(_sizes[name][0], System.Math.Min(w, _sizes[name][1] + 2));
                rooms.Add(new RoomSpec(name, w));
            }
            return rooms;
        }

        private static void LayoutV2(int ox, int oz, int ay, int hx, int hz, int seed, int pop)
        {
            _wallCells.Clear(); _doorCells.Clear(); _roofCells.Clear();
            _bedCells.Clear(); _pantryCells.Clear(); _hearthCell = null;
            var rng = new System.Random(seed);
            var program = ParseProgram(BuiltState.HouseProgram);
            bool hasProgram = program.Count >= 2;
            bool hallInCorridor = hasProgram ? !ProgramHas(program, "hall") : pop >= 5;
            int corrW = hallInCorridor ? 2 : 1, depthN = 3, maxPad = 12;

            List<RoomSpec> rooms;
            if (hasProgram)
            {
                rooms = program;   // the architect's judgment, widths pre-clamped
            }
            else
            {
                rooms = new List<RoomSpec>();
                rooms.Add(new RoomSpec("dorm", 0));
                if (!hallInCorridor) rooms.Add(new RoomSpec("hall", 0));
                rooms.Add(new RoomSpec("pantry", 0));
                rooms.Add(new RoomSpec("workshop", 0));
                for (int i = 0; i < (pop > 4 ? (pop - 4 + 1) / 2 : 0); i++) rooms.Add(new RoomSpec("bedroom", 0));
            }
            for (int i = rooms.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); var t = rooms[i]; rooms[i] = rooms[j]; rooms[j] = t; }

            var north = new List<RoomSpec>(); var south = new List<RoomSpec>();
            int nw = 0, sw = 0;
            foreach (var r in rooms)
            {
                if (r.W <= 0) r.W = rng.Next(_sizes[r.Name][0], _sizes[r.Name][1] + 1);
                if (nw <= sw) { north.Add(r); nw += r.W + 1; } else { south.Add(r); sw += r.W + 1; }
            }
            int innerLen = System.Math.Max(nw, sw) - 1;
            while (innerLen + 2 > maxPad)
            {
                var strip = SumW(north) >= SumW(south) ? north : south;
                RoomSpec widest = null;
                foreach (var r in strip) if (widest == null || r.W > widest.W) widest = r;
                if (widest == null || widest.W <= _sizes[widest.Name][0]) break;
                widest.W--;
                nw = SumW(north) + north.Count; sw = SumW(south) + south.Count;
                innerLen = System.Math.Max(nw, sw) - 1;
            }
            int W = innerLen + 2;
            int H = depthN * 2 + corrW + 4;                       // strips + spine + 2 strip walls + 2 perimeter
            int corrY0 = 1 + depthN + 1;

            // grid marks: 0 floor, 1 wall, 2 door
            var g = new int[W, H];
            for (int x = 0; x < W; x++) { g[x, 0] = 1; g[x, H - 1] = 1; }
            for (int z = 0; z < H; z++) { g[0, z] = 1; g[W - 1, z] = 1; }
            for (int x = 1; x < W - 1; x++) { g[x, corrY0 - 1] = 1; g[x, corrY0 + corrW] = 1; }

            _pendingOx = ox; _pendingOz = oz; _roomsSummary = "";
            LayStrip(g, north, 1, depthN, corrY0 - 1, W);
            LayStrip(g, south, corrY0 + corrW + 1, H - 2, corrY0 + corrW, W);
            // exterior door at the spine end FACING the village plaza/home —
            // buildings open onto the center (Ken's plaza rule), never away.
            g[(hx <= ox + W / 2) ? 0 : W - 1, corrY0] = 2;

            // translate marks into world cells
            for (int x = 0; x < W; x++)
                for (int z = 0; z < H; z++)
                {
                    int wx = ox + x, wz = oz + z;
                    if (g[x, z] == 1) _wallCells.Add(new[] { wx, wz });
                    else if (g[x, z] == 2) _doorCells.Add(new[] { wx, wz });
                    else _roofCells.Add(new[] { wx, wz });        // interior incl. spine
                }
            if (hallInCorridor) _hearthCell = new[] { ox + W / 2, ay, oz + corrY0 + corrW / 2 };
            _ay = ay;
        }

        private static int SumW(List<RoomSpec> s) { int n = 0; foreach (var r in s) n += r.W; return n; }
        private static bool ProgramHas(List<RoomSpec> rooms, string name)
        { foreach (var r in rooms) if (r.Name == name) return true; return false; }

        private static void LayStrip(int[,] g, List<RoomSpec> strip, int z0, int z1, int wallRow, int W)
        {
            int x = 1;
            var summary = new System.Text.StringBuilder(_roomsSummary);
            for (int idx = 0; idx < strip.Count; idx++)
            {
                var r = strip[idx];
                bool last = idx == strip.Count - 1;
                int w = r.W;
                if (last || x + w >= W - 3) { w = W - 2 - x; last = true; }
                if (w < 2) break;
                if (!last) for (int z = z0; z <= z1; z++) g[x + w, z] = 1;
                g[x + w / 2, wallRow] = 2;                        // room door onto the spine
                for (int cx = x; cx < x + w; cx++)                // purpose-tag interior cells
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        if (r.Name == "dorm" || r.Name == "bedroom") _bedCells.Add(new[] { _pendingOx + cx, _pendingOz + cz });
                        else if (r.Name == "pantry") _pantryCells.Add(new[] { _pendingOx + cx, _pendingOz + cz });
                    }
                summary.Append($"{r.Name}({w}x{z1 - z0 + 1}) ");
                x += w + 1;
                if (last) break;
            }
            _roomsSummary = summary.ToString();
        }
        private static int _pendingOx, _pendingOz;   // origin for purpose-tagging during LayStrip

        /// <summary>How many of the plan's floor/wall/door cells already hold the
        /// intended piece in the LOADED WORLD (blueprint or finished).</summary>
        private static int CountExistingPieces()
        {
            int n = 0;
            foreach (var c in _roofCells) if (StockpilePlacer.BuildingExistsAt(c[0], _ay, c[1], Floor)) n++;
            foreach (var c in _wallCells) if (StockpilePlacer.BuildingExistsAt(c[0], _ay, c[1], Wall)) n++;
            foreach (var c in _doorCells) if (StockpilePlacer.BuildingExistsAt(c[0], _ay, c[1], Door)) n++;
            return n;
        }

        /// <summary>Try to RE-ADOPT a previously persisted plan for this save:
        /// regenerate the identical layout at the persisted origin and verify it
        /// against the world. Adopted only if at least one planned piece actually
        /// exists in the loaded save (a rolled-back save where nothing was built
        /// yet fails verification and is re-planned fresh — which is safe, since
        /// the ground there is genuinely clear in THAT save).</summary>
        private static bool TryAdoptPersistedPlan(int hx, int hz)
        {
            if (!BuiltState.TryGetHousePlan(out int ox, out int oz, out int ay)) return false;
            // Version dispatch: v2 regenerates the corridor-spine plan from the
            // persisted seed+pop (identical layout); v1/absent = legacy shack.
            if (BuiltState.HousePlanVersion == 2 && BuiltState.TryGetHousePlanV2(out int seed, out int pop))
                LayoutV2(ox, oz, ay, hx, hz, seed, pop);
            else
                Layout(ox, oz, ay, hx, hz);
            int existing = CountExistingPieces();
            if (existing == 0)
            {
                // Nothing of it exists in this world (rolled-back save) — the
                // persisted plan describes a house that was never built here.
                _wallCells.Clear(); _doorCells.Clear(); _roofCells.Clear();
                BuiltState.ClearHousePlan();
                LLMNPCsPlugin.LogToFile("[HouseBuilder] persisted plan failed world verification (0 pieces exist) — cleared, will re-plan");
                return false;
            }
            _planned = true;
            _roofIdx = BuiltState.RoofsPlaced;   // roofs: persisted progress (no per-cell query)
            if (_roofIdx > _roofCells.Count) _roofIdx = _roofCells.Count;
            _reason = $"RE-ADOPTED persisted site ({ox},{oz}) lvl {ay}: {existing} pieces verified in-world, roofs {_roofIdx}/{_roofCells.Count}";
            LLMNPCsPlugin.LogToFile($"[HouseBuilder] {_reason}");
            if (BuiltState.HouseComplete)
            {
                Complete = true;
                LastStep = "house: re-adopted, already complete (persisted + verified in-world)";
            }
            return true;
        }

        /// <summary>#31: the colony outgrew the legacy shack. Retire the v1 plan
        /// slot and plan a LONGHOUSE on the next tick. The old building stays
        /// in-world as an outbuilding; home/beds/hearth re-anchor to the new
        /// build as it completes.</summary>
        public static void GraduateToLonghouse()
        {
            BuiltState.ClearHousePlan();
            Reset();
            LLMNPCsPlugin.LogToFile("[HouseBuilder] GRADUATION: colony outgrew the shack — planning a longhouse (v2) next tick");
        }

        public static bool Plan(GameObject settlerGo)
        {
            if (_planned) return true;
            var node = StockpilePlacer.SettlerNode(settlerGo);
            // Site the house at the colony HOME, not wherever a settler wandered.
            if (StockpilePlacer.HomeAnchor != null) node = StockpilePlacer.HomeAnchor;
            if (node == null) { LastStep = "house: no home/settler node"; return false; }
            int ay = node[1], nx = node[0], nz = node[2];

            // HOUSEPLANNER v2 (Ken): if the elected leader chose a build site (LLM
            // preference -> deterministic SiteScorer pad), plan the house THERE
            // instead of the near-home default — the leader's judgment drives WHERE.
            // Set before the persisted-plan check so adoption keys off the real site.
            if (HouseSitePlanner.HasSite)
            {
                nx = HouseSitePlanner.ChosenSite.X;
                nz = HouseSitePlanner.ChosenSite.Z;
                ay = HouseSitePlanner.ChosenSite.Y;
                LLMNPCsPlugin.LogToFile($"[HouseBuilder] siting house at the leader's chosen spot ({nx},{ay},{nz})");
            }

            int depth = 2 * N - 1; // two rooms sharing the middle wall row

            // FIRST: if this save already has our house (persisted plan), adopt
            // it instead of hunting for a fresh clear footprint next to it.
            if (TryAdoptPersistedPlan(nx, nz))
            {
                LastStep = $"house: {_reason}";
                return true;
            }

            // NEW plans are v2 LONGHOUSES (#31): generate once at a probe origin
            // to learn the footprint, then scan for a clear pad of that size.
            int seed = System.Math.Abs((MemoryManager.GetActiveSaveId() ?? "seed").GetHashCode() ^ (nx * 31 + nz));
            int pop = System.Math.Max(TargetPop, 4);
            LayoutV2(0, 0, ay, 0, 0, seed, pop);
            int fw = 0, fh = 0;
            foreach (var c in _wallCells) { if (c[0] > fw) fw = c[0]; if (c[1] > fh) fh = c[1]; }
            fw++; fh++;   // footprint width/height from the relative plan

            // VILLAGE PLAZA SLOTS first (Ken's rule: one center, buildings ring
            // it, doors facing in). Spiral search survives only as fallback.
            VillageLayout.EstablishIfNeeded();
            if (VillageLayout.TryGetSlot(BuiltState.VillageQueueIndex, fw, fh, out int sox, out int soz, out int say, out string slotName))
            {
                var plaza = VillageLayout.PlazaCenter;
                LayoutV2(sox, soz, say, plaza[0], plaza[2], seed, pop);
                _planned = true;
                BuiltState.SaveHousePlanV2(sox, soz, say, seed, pop);
                _reason = $"LONGHOUSE {fw}x{fh} on the {slotName} side of the plaza ({sox},{soz}) [{_roomsSummary.Trim()}], door facing the square" +
                          (_hearthCell != null ? $", hearth @({_hearthCell[0]},{_hearthCell[2]})" : "");
                LastStep = $"house: planned {_reason}";
                LLMNPCsPlugin.LogToFile($"[HouseBuilder] {_reason}");
                return true;
            }

            for (int radius = 2; radius <= 18 && !_planned; radius++)
            {
                for (int dx = -radius; dx <= radius && !_planned; dx++)
                    for (int dz = -radius; dz <= radius && !_planned; dz++)
                    {
                        int ox = nx + dx, oz = nz + dz;
                        bool clear = true;
                        for (int ix = 0; ix < fw && clear; ix++)
                            for (int iz = 0; iz < fh && clear; iz++)
                                if (!StockpilePlacer.CanPlaceWallAt(ox + ix, ay, oz + iz)
                                    || !StockpilePlacer.CellIsDry(ox + ix, ay, oz + iz)) clear = false;   // buildable ≠ habitable
                        if (!clear) continue;

                        LayoutV2(ox, oz, ay, nx, nz, seed, pop);
                        _planned = true;
                        BuiltState.SaveHousePlanV2(ox, oz, ay, seed, pop);   // survive reloads
                        var extDoor = _doorCells[_doorCells.Count - 1];
                        _reason = $"LONGHOUSE {fw}x{fh} at ({ox},{oz}) for pop {pop} [{_roomsSummary.Trim()}], " +
                                  $"{System.Math.Abs(ox - nx) + System.Math.Abs(oz - nz)} tiles from home, {_doorCells.Count} doors, " +
                                  (_hearthCell != null ? $"hearth in the hall-spine @({_hearthCell[0]},{_hearthCell[2]})" : "hall room");
                    }
            }
            LastStep = _planned
                ? $"house: planned {_reason}"
                : $"house: no dry, flat, open {fw}x{fh} footprint near home (avoided water/slopes)";
            if (_planned) LLMNPCsPlugin.LogToFile($"[HouseBuilder] {_reason}");
            return _planned;
        }

        /// <summary>Place ONE piece: floors, then walls, then doors, then roofs.
        /// Pieces that ALREADY EXIST in the world are skipped (not re-placed) —
        /// several skips can happen in one call, but at most ONE placement.</summary>
        public static string Step(GameObject settlerGo)
        {
            if (Complete) return "house complete";
            if (!_planned && !Plan(settlerGo)) return LastStep;
            if (Complete) return LastStep;  // Plan() may adopt an already-complete house

            // S1a BATCH PLACEMENT (scaling roadmap): place a whole PHASE per
            // pass (capped) instead of one piece per ~13s tick — settlers
            // construct in parallel natively, so a building rises in minutes.
            const int BatchCap = 24;   // commits per pass; avoids frame spikes
            int placed = 0;
            // FLOORS first — a real house has a floor in each room (the interior cells).
            while (_floorIdx < _roofCells.Count && placed < BatchCap)
            {
                var c = _roofCells[_floorIdx++];
                if (StockpilePlacer.BuildingExistsAt(c[0], _ay, c[1], Floor)) continue; // already in the save
                StockpilePlacer.TryPlaceBuildingAt(c[0], _ay, c[1], Floor);
                placed++;
            }
            if (placed > 0 || _floorIdx < _roofCells.Count)
            {
                LastStep = $"house floors {_floorIdx}/{_roofCells.Count} (batched {placed})";
                return LastStep;
            }
            while (_wallIdx < _wallCells.Count && placed < BatchCap)
            {
                var c = _wallCells[_wallIdx++];
                if (StockpilePlacer.BuildingExistsAt(c[0], _ay, c[1], Wall)) continue;
                StockpilePlacer.TryPlaceBuildingAt(c[0], _ay, c[1], Wall);
                placed++;
            }
            if (placed > 0 || _wallIdx < _wallCells.Count)
            {
                LastStep = $"house walls {_wallIdx}/{_wallCells.Count} (batched {placed})";
                return LastStep;
            }
            while (_doorIdx < _doorCells.Count)
            {
                var c = _doorCells[_doorIdx++];
                if (StockpilePlacer.BuildingExistsAt(c[0], _ay, c[1], Door)) continue;
                // conflict guard: NEVER stack a door onto a cell holding any
                // other piece (a wall etc.) — skip and log instead.
                if (StockpilePlacer.AnyBuildingAt(c[0], _ay, c[1]))
                { LLMNPCsPlugin.LogToFile($"[HouseBuilder] door cell ({c[0]},{_ay},{c[1]}) occupied by another building — SKIPPED (conflict guard)"); continue; }
                StockpilePlacer.TryPlaceBuildingAt(c[0], _ay, c[1], Door);
                placed++;
            }
            if (placed > 0)
            {
                LastStep = $"house doors placed ({_doorCells.Count} total)";
                return LastStep;
            }
            // ROOF v3 — STRIPS, not cells (ground truth CanPlaceRoof:745-756:
            // only strip ENDPOINTS need support; endpoints sit over the perimeter
            // walls, the middle spans the room like a real roof). One strip per
            // z-row across the full house width; _roofIdx counts rows.
            int ox2 = _wallCells.Count > 0 ? MinX() : 0;
            int depth = _roofCells.Count > 0 || _wallCells.Count > 0 ? (MaxZ() - MinZ() + 1) : 0;
            if (_roofIdx < depth)
            {
                int rowZ = MinZ() + _roofIdx;
                // v4 FIRST: phantom blueprints from the no-autoconstruct era sit on
                // the roof level and block every strip (diag: BLOCKER@cell=Y,
                // support=Y). If the row already holds roof instances, KICK the
                // unqueued ones into construction — that IS the roof for this row.
                int width = MaxX() - MinX() + 1;
                string kick = StockpilePlacer.KickRoofRow(MinX(), MaxX(), _ay + 1, rowZ, Roof, out int have);
                if (have >= width)
                {
                    _roofIdx++; _roofRetries = 0; BuiltState.RoofsPlaced = _roofIdx;
                    LastStep = $"house roof row {_roofIdx}/{depth}: existing blueprints — {kick}";
                    return LastStep;
                }
                if (have > 0)
                {
                    // Partial phantom row: kicked what exists; strip can't span the
                    // blockers. Count it done for the pass and log the gap honestly.
                    _roofIdx++; _roofRetries = 0; BuiltState.RoofsPlaced = _roofIdx;
                    LastStep = $"house roof row {_roofIdx}/{depth}: PARTIAL {kick} — gap cells left for a later pass";
                    LLMNPCsPlugin.LogToFile($"[HouseBuilder] {LastStep}");
                    return LastStep;
                }
                string res = StockpilePlacer.TryPlaceRoofStrip(MinX(), MaxX(), _ay + 1, rowZ, Roof);
                bool ok = res.StartsWith("ok:");
                // HONEST counting (the old code counted ATTEMPTS — rejected rows
                // were persisted as progress and never retried after a reload).
                if (ok) { _roofIdx++; _roofRetries = 0; BuiltState.RoofsPlaced = _roofIdx; }
                else if (++_roofRetries >= 4)
                {
                    LLMNPCsPlugin.LogToFile($"[HouseBuilder] roof row z={rowZ} gave up after {_roofRetries} rejections — SKIPPED: {res}");
                    _roofIdx++; _roofRetries = 0;
                }
                LastStep = $"house roof row {(ok ? _roofIdx : _roofIdx + 1)}/{depth}{(ok ? "" : $" (try {_roofRetries})")}: {res}";
                return LastStep;
            }
            Complete = true;
            BuiltState.HouseComplete = true;    // survives reloads
            LastStep = "house complete (two connected rooms: walls + exterior door + connecting doorway)";
            return LastStep;
        }
    }
}
