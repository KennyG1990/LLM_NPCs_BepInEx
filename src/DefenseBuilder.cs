using System;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// DEFENSE v1 — PALISADE RING (player-competence canon, 2026-07-12: "wall a
    /// single chokepoint before the first raid (arrives within days)"; raiders
    /// attack DOORS and exposed objects but IGNORE blank walls; the AI won't
    /// cross water gaps). Doctrine for an open colony:
    ///   * ring of wooden walls at radius R around HOME, enclosing the core
    ///     (house, stockpile, stations)
    ///   * cells that are WATER are skipped — the marsh is a natural moat;
    ///     only the dry approaches get walled
    ///   * exactly ONE gate (wood door): the first dry ring cell scanned —
    ///     deterministic, and raiders funnel to it (they attack doors)
    /// Freeze-safe by construction (today's lesson, 6 freezes): the ring scan
    /// is time-budgeted with a resume index, runs AFTER the survival floor,
    /// and goes dormant permanently once a full pass places nothing new.
    /// Merlons/traps/walkways are v2 (research-gated).
    /// </summary>
    public static class DefenseBuilder
    {
        public static string LastResult = "(idle)";
        private const string WallId = "wood_wall_element";
        private const string DoorId = "wood_door";
        private const int Radius = 14;        // ring half-size around home
        private const int BatchCap = 8;       // wall commits per tick (material drip)
        private static int _resumeIdx = 0;    // ring-cell index for time-budget resume
        private static bool _gatePlaced = false;
        private static bool _done = false;

        public static void Reset() { _resumeIdx = 0; _gatePlaced = false; _done = false; LastResult = "(idle)"; }

        /// <summary>One budgeted pass over the ring. Call only when home is
        /// established and the first shelter is complete (survival floor first).</summary>
        public static string Tick(int hx, int hy, int hz)
        {
            if (_done) return LastResult;
            try
            {
                // ring cells in deterministic order (top row, right col, bottom row, left col)
                var ring = new System.Collections.Generic.List<int[]>();
                for (int dx = -Radius; dx <= Radius; dx++) ring.Add(new[] { hx + dx, hz - Radius });
                for (int dz = -Radius + 1; dz <= Radius; dz++) ring.Add(new[] { hx + Radius, hz + dz });
                for (int dx = Radius - 1; dx >= -Radius; dx--) ring.Add(new[] { hx + dx, hz + Radius });
                for (int dz = Radius - 1; dz >= -Radius + 1; dz--) ring.Add(new[] { hx - Radius, hz + dz });

                var zoneRects = StockpileZoner.GetZoneRects();   // once per pass — a wall through the stockpile is not a defense
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int placed = 0, existing = 0, water = 0, blocked = 0;
                int i = _resumeIdx;
                for (; i < ring.Count; i++)
                {
                    if (sw.ElapsedMilliseconds > 40 || placed >= BatchCap)
                    {
                        _resumeIdx = i;
                        return LastResult = $"palisade: {placed} placed this pass (ring {i}/{ring.Count}, resumes)";
                    }
                    int cx = ring[i][0], cz = ring[i][1];
                    if (StockpileZoner.CellInRects(zoneRects, cx, cz)) { blocked++; continue; } // never wall the stockpile
                    if (!StockpilePlacer.CellIsDry(cx, hy, cz)) { water++; continue; }        // marsh = free moat
                    if (StockpilePlacer.BuildingExistsAt(cx, hy, cz, WallId) ||
                        StockpilePlacer.BuildingExistsAt(cx, hy, cz, DoorId)) { existing++; continue; }
                    string id = _gatePlaced ? WallId : DoorId;                                 // first dry cell = the gate
                    var r = StockpilePlacer.TryPlaceBuildingAt(cx, hy, cz, id);
                    if (r != null && r.StartsWith("ok"))
                    {
                        if (!_gatePlaced) { _gatePlaced = true; LLMNPCsPlugin.LogToFile($"[DefenseBuilder] GATE placed at ({cx},{hy},{cz}) — raiders funnel here"); }
                        placed++;
                    }
                    else blocked++;
                }
                _resumeIdx = 0;
                if (placed == 0)
                {
                    // full pass, nothing new to place: ring is as complete as the
                    // terrain allows — go dormant (no standing scan cost).
                    _done = true;
                    LastResult = $"palisade: COMPLETE (existing={existing} water-moat={water} unplaceable={blocked})";
                    LLMNPCsPlugin.LogToFile("[DefenseBuilder] " + LastResult);
                }
                else LastResult = $"palisade: {placed} placed (pass done: existing={existing} water={water} blocked={blocked})";
                return LastResult;
            }
            catch (Exception ex) { return LastResult = "palisade EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }
    }
}
