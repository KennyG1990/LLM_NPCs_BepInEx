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
        private static int _wallIdx = 0, _doorIdx = 0, _roofIdx = 0, _floorIdx = 0;
        private static string _reason = "";

        public static bool Complete = false;
        public static string LastStep = "(idle)";

        // Exposed so the builder can place BEDS inside the rooms (on the floor of
        // the interior cells) instead of scattering them outside.
        public static bool IsPlanned => _planned;
        public static int Level => _ay;
        public static List<int[]> InteriorCells => _roofCells;

        /// <summary>Clear ALL per-session state. Called by BuiltState when a
        /// world (re)load is detected, before any tick can act on stale flags.</summary>
        public static void Reset()
        {
            _planned = false;
            Complete = false;
            _ay = 0;
            _wallCells.Clear(); _doorCells.Clear(); _roofCells.Clear();
            _wallIdx = _doorIdx = _roofIdx = _floorIdx = 0;
            _reason = "";
            LastStep = "(idle)";
        }

        /// <summary>Deterministically generate the full cell layout for a house
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

        public static bool Plan(GameObject settlerGo)
        {
            if (_planned) return true;
            var node = StockpilePlacer.SettlerNode(settlerGo);
            // Site the house at the colony HOME, not wherever a settler wandered.
            if (StockpilePlacer.HomeAnchor != null) node = StockpilePlacer.HomeAnchor;
            if (node == null) { LastStep = "house: no home/settler node"; return false; }
            int ay = node[1], nx = node[0], nz = node[2];
            int depth = 2 * N - 1; // two rooms sharing the middle wall row

            // FIRST: if this save already has our house (persisted plan), adopt
            // it instead of hunting for a fresh clear footprint next to it.
            if (TryAdoptPersistedPlan(nx, nz))
            {
                LastStep = $"house: {_reason}";
                return true;
            }

            for (int radius = 2; radius <= 18 && !_planned; radius++)
            {
                for (int dx = -radius; dx <= radius && !_planned; dx++)
                    for (int dz = -radius; dz <= radius && !_planned; dz++)
                    {
                        int ox = nx + dx, oz = nz + dz;
                        bool clear = true;
                        for (int ix = 0; ix < N && clear; ix++)
                            for (int iz = 0; iz < depth && clear; iz++)
                                if (!StockpilePlacer.CanPlaceWallAt(ox + ix, ay, oz + iz)) clear = false;
                        if (!clear) continue;

                        Layout(ox, oz, ay, nx, nz);
                        _planned = true;
                        BuiltState.SaveHousePlan(ox, oz, ay);   // survive reloads
                        var extDoor = _doorCells[0];
                        _reason = $"site ({ox},{oz}) {N}x{depth}, {System.Math.Abs(ox - nx) + System.Math.Abs(oz - nz)} tiles from home, " +
                                  $"whole footprint dry+flat, front door faces home @({extDoor[0]},{extDoor[1]}), {_doorCells.Count} doors (1 connecting the rooms)";
                    }
            }
            LastStep = _planned
                ? $"house: planned 2-room {N}x{depth} — {_reason}"
                : "house: no dry, flat, open footprint near home (avoided water/slopes)";
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

            // FLOORS first — a real house has a floor in each room (the interior cells).
            while (_floorIdx < _roofCells.Count)
            {
                var c = _roofCells[_floorIdx++];
                if (StockpilePlacer.BuildingExistsAt(c[0], _ay, c[1], Floor)) continue; // already in the save
                LastStep = $"house floor {_floorIdx}/{_roofCells.Count}: {StockpilePlacer.TryPlaceBuildingAt(c[0], _ay, c[1], Floor)}";
                return LastStep;
            }
            while (_wallIdx < _wallCells.Count)
            {
                var c = _wallCells[_wallIdx++];
                if (StockpilePlacer.BuildingExistsAt(c[0], _ay, c[1], Wall)) continue;
                LastStep = $"house wall {_wallIdx}/{_wallCells.Count}: {StockpilePlacer.TryPlaceBuildingAt(c[0], _ay, c[1], Wall)}";
                return LastStep;
            }
            while (_doorIdx < _doorCells.Count)
            {
                var c = _doorCells[_doorIdx++];
                string which = _doorIdx == 1 ? "exterior" : "connecting";
                if (StockpilePlacer.BuildingExistsAt(c[0], _ay, c[1], Door)) continue;
                // conflict guard: NEVER stack a door onto a cell holding any
                // other piece (a wall etc.) — skip and log instead.
                if (StockpilePlacer.AnyBuildingAt(c[0], _ay, c[1]))
                { LLMNPCsPlugin.LogToFile($"[HouseBuilder] door cell ({c[0]},{_ay},{c[1]}) occupied by another building — SKIPPED (conflict guard)"); continue; }
                LastStep = $"house {which} door: {StockpilePlacer.TryPlaceBuildingAt(c[0], _ay, c[1], Door)}";
                return LastStep;
            }
            if (_roofIdx < _roofCells.Count)
            {
                var c = _roofCells[_roofIdx++];
                // Roofs use the game's roof-component path (not normal placement) and
                // must sit ONE LEVEL ABOVE the floor — CanPlaceRoof rejects any cell
                // where ground/floor exists (so ay fails; ay+1 is the ceiling).
                // Progress is PERSISTED so a reload never re-invokes placed roofs.
                LastStep = $"house roof {_roofIdx}/{_roofCells.Count}: {StockpilePlacer.TryPlaceRoofAt(c[0], _ay + 1, c[1], Roof)}";
                BuiltState.RoofsPlaced = _roofIdx;
                return LastStep;
            }
            Complete = true;
            BuiltState.HouseComplete = true;    // survives reloads
            LastStep = "house complete (two connected rooms: walls + exterior door + connecting doorway)";
            return LastStep;
        }
    }
}
