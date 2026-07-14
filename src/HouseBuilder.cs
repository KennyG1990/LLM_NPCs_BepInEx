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
        private static int _searchResumeRadius = 2;      // time-budgeted site search resumes here
        private static float _searchCooldownUntil = 0f;  // failed full pass → don't rescan for 5 min
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
        // MULTI-STORY (2026-07-13): forge houses with floors>1 add upper stories.
        // _upperPieces = {x, y, z, type}  type: 0 floor, 1 wall, 2 beam, 3 stair.
        // Placed AFTER the ground floor, before the roof. floors=1 leaves this
        // empty and _roofLevel = _ay+1, so the working single-story path is byte-
        // identical. Structural law (Guide §3): beams span walls to hold the floor
        // above (every ~3 tiles); stability inherits upward free; roof on top.
        private static readonly List<int[]> _upperPieces = new List<int[]>();
        private static int _upperIdx = 0;
        private static int _floors = 1;
        private static int _roofLevel;         // _ay + _floors (top); =_ay+1 for 1 story
        private static int _roofRetries = 0;   // consecutive rejections of the CURRENT row
        private static bool _roofGap = false;  // a roof row that never placed — house is NOT fully roofed
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

        /// <summary>True when (x,z) lies within the planned house footprint
        /// (interior + wall ring). Used by StockpileZoner to keep corpse/refuse
        /// roles OUT of the family home (Ken, eyes-on 2026-07-12: corpse
        /// stockpile inside the house — the house was placed over old zones).</summary>
        public static bool FootprintContains(int x, int z)
        {
            if (!_planned || _wallCells.Count == 0) return false;
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (var c in _wallCells)
            {
                if (c[0] < minX) minX = c[0];
                if (c[0] > maxX) maxX = c[0];
                if (c[1] < minZ) minZ = c[1];
                if (c[1] > maxZ) maxZ = c[1];
            }
            return x >= minX && x <= maxX && z >= minZ && z <= maxZ;
        }

        /// <summary>Clear ALL per-session state. Called by BuiltState when a
        /// world (re)load is detected, before any tick can act on stale flags.</summary>
        public static void Reset()
        {
            _planned = false;
            Complete = false;
            _searchResumeRadius = 2;
            _searchCooldownUntil = 0f;
            _ay = 0;
            _wallCells.Clear(); _doorCells.Clear(); _roofCells.Clear();
            _bedCells.Clear(); _pantryCells.Clear(); _hearthCell = null; _roomsSummary = "";
            _upperPieces.Clear(); _upperIdx = 0; _floors = 1; _roofLevel = 0;
            _wallIdx = _doorIdx = _roofIdx = _floorIdx = 0;
            _roofRetries = 0; _roofGap = false;
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
            if (BuiltState.HousePlanVersion == 3 && BuiltState.TryGetHousePlanV3(out int pw, out int ph))
                LayoutRect(ox, oz, ay, pw, ph, BuiltState.HousePlanFloors);
            else if (BuiltState.HousePlanVersion == 2 && BuiltState.TryGetHousePlanV2(out int seed, out int pop))
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
            // ROOF RE-VERIFY (2026-07-13, Ken eyes-on: houses flagged 'complete' but
            // ROOFLESS — rows that rejected while walls were still building had been
            // skipped-and-counted, then persisted as done). On reload ALWAYS re-run
            // the roof pass from row 0: KickRoofRow no-ops on roofs that exist and
            // places the missing rows (now that the walls are built). Never trust a
            // persisted 'complete' for the roof — verify it in the world.
            _roofIdx = 0; _roofRetries = 0; _roofGap = false;
            _reason = $"RE-ADOPTED persisted site ({ox},{oz}) lvl {ay}: {existing} pieces verified in-world; re-verifying roof";
            LLMNPCsPlugin.LogToFile($"[HouseBuilder] {_reason}");
            Complete = false;   // stays incomplete until the roof is actually verified/placed this session
            return true;
        }

        /// <summary>Unit C: simple shell layout for a FORGE-PLAN rect (version 3) —
        /// perimeter walls, full interior floor, one door centered on the south
        /// edge. Same code path for fresh adoption and reload re-adoption, so
        /// the cells always come out identical for the same inputs.</summary>
        private static void LayoutRect(int ox, int oz, int ay, int w, int h)
            => LayoutRect(ox, oz, ay, w, h, 1);

        private static void LayoutRect(int ox, int oz, int ay, int w, int h, int floors)
        {
            _wallCells.Clear(); _doorCells.Clear(); _roofCells.Clear();
            _bedCells.Clear(); _pantryCells.Clear(); _hearthCell = null;
            _upperPieces.Clear();
            _floors = System.Math.Max(1, floors);
            _roomsSummary = _floors > 1 ? $"forge {w}x{h} {_floors}-story " : $"forge shell {w}x{h} ";
            var extDoor = new[] { ox + w / 2, oz };
            int stairX = ox + w - 2, stairZ = oz + 1;   // interior corner = the stair shaft
            bool multi = _floors > 1;

            for (int x = ox; x < ox + w; x++)
                for (int z = oz; z < oz + h; z++)
                {
                    if (x == extDoor[0] && z == extDoor[1]) continue;
                    bool perimeter = (x == ox || x == ox + w - 1 || z == oz || z == oz + h - 1);
                    if (perimeter) { _wallCells.Add(new[] { x, z }); continue; }
                    if (multi && x == stairX && z == stairZ) continue;   // stair base — not a floor tile
                    _roofCells.Add(new[] { x, z });   // ground-floor interior floor cells
                }
            _doorCells.Add(extDoor);
            _ay = ay;
            _roofLevel = ay + _floors;   // roof sits on TOP of the topmost story (=ay+1 for 1 story)

            // UPPER STORIES: beams span the interior (every 3 tiles) to hold the
            // floor above, perimeter walls enclose, a stair shaft climbs the SE
            // corner (stair on each level EXCEPT the top, which is the landing).
            // Structural law (Guide 3): beams support floors; stability inherits up.
            if (multi)
            {
                _upperPieces.Add(new[] { stairX, ay, stairZ, 3 });   // ground stair -> 2nd story
                for (int level = 1; level < _floors; level++)
                {
                    int uy = ay + level;
                    bool topStory = (level == _floors - 1);
                    for (int x = ox; x < ox + w; x++)
                        for (int z = oz; z < oz + h; z++)
                        {
                            bool perimeter = (x == ox || x == ox + w - 1 || z == oz || z == oz + h - 1);
                            if (perimeter) { _upperPieces.Add(new[] { x, uy, z, 1 }); continue; }   // wall
                            if (x == stairX && z == stairZ)
                            {
                                _upperPieces.Add(new[] { x, uy, z, topStory ? 0 : 3 });   // top=landing floor, else stair up
                                continue;
                            }
                            if (((x - ox) % 3 == 0) && ((z - oz) % 3 == 0))
                                _upperPieces.Add(new[] { x, uy, z, 2 });   // beam (support), placed before floor
                            _upperPieces.Add(new[] { x, uy, z, 0 });       // floor
                        }
                }
            }
        }

        /// <summary>Unit C: adopt a VillageForge plan rect as THE house. Keeps every
        /// downstream coupling intact (beds inside InteriorCells, roof strips,
        /// BuiltState persistence, move-in) because the executor feeds the plan
        /// INTO this builder instead of building around it. Refused once a plan
        /// already exists — the forge plan must arrive before improvised siting.</summary>
        public static bool AdoptForgeRect(int ox, int oz, int ay, int w, int h, int floors = 1)
        {
            if (_planned || Complete) return false;
            if (w < 3 || h < 3) return false;                  // a shell needs an interior
            LayoutRect(ox, oz, ay, w, h, floors);
            _planned = true;
            _wallIdx = _doorIdx = _roofIdx = _floorIdx = _upperIdx = 0;
            BuiltState.SaveHousePlanV3(ox, oz, ay, w, h, _floors);
            _reason = $"FORGE PLAN house {w}x{h}{(_floors > 1 ? $" x{_floors}-story" : "")} at ({ox},{oz}) lvl {ay} — VillageForge siting, no improvisation";
            LastStep = $"house: adopted {_reason}";
            LLMNPCsPlugin.LogToFile($"[HouseBuilder] {_reason}");
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
            // FALLBACK DISABLED (Ken, 2026-07-13): the hardcoded N=4 (4x7) improvised
            // shack is banned. Houses come ONLY from the VillageForge/LLM plan via
            // AdoptForgeRect. If no plan produces a buildable house, the colony gets no
            // house and fails visibly — no silent fallback to mask a broken plan.
            LastStep = "house: NO improvised fallback (plan-only policy) — waiting for a VillageForge plan house";
            return false;
            #pragma warning disable CS0162
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

            // COOLDOWN GATES *ALL* SITING, slots included (freeze #5, 17:21:47:
            // the spiral was cooling down but TryGetSlot's ~2k reflected map
            // queries still ran every tick and hit the same water-sim race —
            // the deadlock strikes on ANY map query; only NOT querying is safe).
            if (UnityEngine.Time.realtimeSinceStartup < _searchCooldownUntil)
            {
                LastStep = $"house: no dry {fw}x{fh} footprint (siting cooling down; terrain won't change soon)";
                return false;
            }

            // SNAPSHOT-FIRST (2026-07-13, the radius-2 infinite loop: without
            // the snapshot every candidate costs live queries, one ring can't
            // finish inside the budget, and ring-granularity resume never
            // advances). Siting WAITS for the sliced worldmap scan — ~a minute
            // once per session — then the snapshot prefilter makes the whole
            // search near-free.
            if (WorldMap.LastScanTicks == 0)
            {
                LastStep = "house: waiting for worldmap snapshot (siting becomes cheap once scanned)";
                return false;
            }

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

            // FREEZE FIX (2026-07-12, breadcrumb-confirmed 4/4): this spiral is
            // ~360k reflected map queries per full pass (37² origins × fw*fh
            // cells × 2 checks) on the MAIN THREAD, re-run EVERY tick when it
            // fails (marsh map: it always fails) — each pass raced the water-sim
            // thread until one deadlocked. Now: 50ms budget per tick with
            // radius-resume, and a 5-minute cooldown after a full failed pass.
            if (_searchResumeRadius <= 2)   // log once per pass, not every resume slice
                LLMNPCsPlugin.LogToFile($"[HouseBuilder] site search begin {fw}x{fh} (radius {_searchResumeRadius}..18, snapshot-backed)");
            var zoneRects = StockpileZoner.GetZoneRects();   // once per pass, NOT per cell
            var swSearch = System.Diagnostics.Stopwatch.StartNew();
            for (int radius = _searchResumeRadius; radius <= 18 && !_planned; radius++)
            {
                for (int dx = -radius; dx <= radius && !_planned; dx++)
                    for (int dz = -radius; dz <= radius && !_planned; dz++)
                    {
                        // BUDGET PER ORIGIN, not per ring (freeze #10: one radius-18
                        // ring is ~39k map queries — under water-sim contention a
                        // per-ring check allowed a 67-SECOND freeze).
                        if (swSearch.ElapsedMilliseconds > 50)
                        {
                            _searchResumeRadius = radius;
                            LastStep = $"house: site search paused at radius {radius}/18 (time budget; resumes next tick)";
                            return false;
                        }
                        int ox = nx + dx, oz = nz + dz;
                        bool clear = true;
                        for (int ix = 0; ix < fw && clear; ix++)
                            for (int iz = 0; iz < fh && clear; iz++)
                                if (!WorldMap.SnapshotBuildableDry(ox + ix, ay, oz + iz)      // snapshot FIRST: array read, no live query
                                    || StockpileZoner.CellInRects(zoneRects, ox + ix, oz + iz)
                                    || !StockpilePlacer.CanPlaceWallAt(ox + ix, ay, oz + iz)
                                    || !StockpilePlacer.CellIsDry(ox + ix, ay, oz + iz)) clear = false;   // buildable ≠ habitable ≠ someone's stockpile
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
            _searchResumeRadius = 2;   // full pass finished — start over next time
            if (!_planned)
            {
                _searchCooldownUntil = UnityEngine.Time.realtimeSinceStartup + 300f;
                LLMNPCsPlugin.LogToFile($"[HouseBuilder] site search complete — no dry {fw}x{fh} footprint; cooling down 5 min");

                // STARTER-SHACK FALLBACK — DISABLED BY KEN (2026-07-13: "I don't
                // want a fallback basic house, the system needs to be able to
                // reason around the end game which will be full of buildings").
                // The village layout intelligence moves to the OFFLINE
                // VillageForge generator (visual validation first, then ported);
                // in-game siting executes its plans instead of improvising.
                const bool StarterShackEnabled = false;
                if (StarterShackEnabled && !BuiltState.HouseComplete)
                {
                    int sw2 = 4, sh2 = 2 * 4 - 1;   // N=4 shack: 4 wide, 7 deep
                    bool shackBudgetOut = false;
                    for (int radius = 2; radius <= 18 && !_planned && !shackBudgetOut; radius++)
                    {
                        for (int dx = -radius; dx <= radius && !_planned && !shackBudgetOut; dx++)
                            for (int dz = -radius; dz <= radius && !_planned; dz++)
                            {
                                if (swSearch.ElapsedMilliseconds > 80) { shackBudgetOut = true; break; }   // per-origin check; shelter urgent, short retry below
                                int ox2 = nx + dx, oz2 = nz + dz;
                                bool clear2 = true;
                                for (int ix = 0; ix < sw2 && clear2; ix++)
                                    for (int iz = 0; iz < sh2 && clear2; iz++)
                                        if (!WorldMap.SnapshotBuildableDry(ox2 + ix, ay, oz2 + iz)
                                            || StockpileZoner.CellInRects(zoneRects, ox2 + ix, oz2 + iz)
                                            || !StockpilePlacer.CanPlaceWallAt(ox2 + ix, ay, oz2 + iz)
                                            || !StockpilePlacer.CellIsDry(ox2 + ix, ay, oz2 + iz)) clear2 = false;
                                if (!clear2) continue;
                                Layout(ox2, oz2, ay, nx, nz);
                                _planned = true;
                                _ay = ay;
                                BuiltState.SaveHousePlan(ox2, oz2, ay);   // v1 plan — graduation upgrades it later
                                _reason = $"STARTER SHACK {sw2}x{sh2} at ({ox2},{oz2}) — no pad for the longhouse; shelter first, upgrade later";
                                LLMNPCsPlugin.LogToFile($"[HouseBuilder] {_reason}");
                            }
                    }
                    if (_planned) _searchCooldownUntil = 0f;                                        // building now; no cooldown
                    else if (shackBudgetOut) _searchCooldownUntil = UnityEngine.Time.realtimeSinceStartup + 30f;   // shelter urgent: retry soon, not in 5 min
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
            // UPPER STORIES (multi-story forge houses): beams/floors/walls/stairs at
            // their own levels, honest blueprints (settlers haul + build). Empty for
            // floors=1 so the single-story path is untouched. Placed before the roof
            // (which sits on the TOP story's walls at _roofLevel).
            while (_upperIdx < _upperPieces.Count && placed < BatchCap)
            {
                var p = _upperPieces[_upperIdx++];   // {x, y, z, type: 0 floor,1 wall,2 beam,3 stair}
                string uid = p[3] == 0 ? Floor : p[3] == 1 ? Wall : p[3] == 2 ? "wood_beam" : "wood_stair_straight";
                if (StockpilePlacer.BuildingExistsAt(p[0], p[1], p[2], uid)) continue;
                StockpilePlacer.TryPlaceBuildingAt(p[0], p[1], p[2], uid);
                placed++;
            }
            if (placed > 0 || _upperIdx < _upperPieces.Count)
            {
                LastStep = $"house upper stories {_upperIdx}/{_upperPieces.Count} (batched {placed})";
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
                int roofY = _roofLevel > 0 ? _roofLevel : _ay + 1;   // top of the topmost story (=ay+1 single-story)
                string kick = StockpilePlacer.KickRoofRow(MinX(), MaxX(), roofY, rowZ, Roof, out int have);
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
                string res = StockpilePlacer.TryPlaceRoofStrip(MinX(), MaxX(), (_roofLevel > 0 ? _roofLevel : _ay + 1), rowZ, Roof);
                bool ok = res.StartsWith("ok:");
                // HONEST counting (the old code counted ATTEMPTS — rejected rows
                // were persisted as progress and never retried after a reload).
                if (ok) { _roofIdx++; _roofRetries = 0; BuiltState.RoofsPlaced = _roofIdx; }
                else if (++_roofRetries >= 60)
                {
                    // 60 ticks (~13 min) with no placement = the WALLS below this row
                    // never got built or a real geometry problem. Surface it honestly
                    // (a roofless GAP) — do NOT silently count it and flag the house
                    // done, which is how roofless houses got marked complete.
                    LLMNPCsPlugin.LogToFile($"[HouseBuilder] ROOF GAP row z={rowZ} after {_roofRetries} tries — house will report roofless: {res}");
                    _roofIdx++; _roofRetries = 0; _roofGap = true;
                }
                LastStep = $"house roof row {(ok ? _roofIdx : _roofIdx + 1)}/{depth}{(ok ? "" : $" (try {_roofRetries})")}: {res}";
                return LastStep;
            }
            Complete = true;
            BuiltState.HouseComplete = true;    // survives reloads
            LastStep = _roofGap
                ? "house built but ROOFLESS GAP — some roof rows could not place (walls/support); NOT fully weatherproof"
                : "house complete — walls + door + FULL ROOF (weatherproof)";
            if (_roofGap) LLMNPCsPlugin.LogToFile("[HouseBuilder] " + LastStep);
            return LastStep;
        }
    }
}
