using System;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// VILLAGE LAYOUT (Ken 2026-07-12: "this should only exist after they have
    /// sited a plot for their village; the plaza is the center — everything
    /// exists around it"). ONE village center, chosen once (the leader's sited
    /// plot), holding a PLAZA — the open social heart with the hearth. Every
    /// building the architect designs takes a SLOT around the plaza, door
    /// facing in. This replaces near-home spiral placement, which produced two
    /// competing "centers" the same night it was pointed out (longhouse at
    /// (113,147) vs the leader's farm plot at (90,150)).
    /// </summary>
    public static class VillageLayout
    {
        public const int PlazaHalf = 3;     // plaza is (2*3+1)^2 = 7x7 open cells
        public const int StreetGap = 2;     // breathing room between plaza and walls
        public static string LastResult = "(no village center)";

        public static bool Established => BuiltState.TryGetVillageCenter(out _, out _, out _);

        public static int[] PlazaCenter =>
            BuiltState.TryGetVillageCenter(out int x, out int y, out int z) ? new[] { x, y, z } : null;

        public static void Reset() { LastResult = "(no village center)"; }

        /// <summary>Fix the village center ONCE per save — at the leader's sited
        /// plot when one exists, else the current colony home.</summary>
        public static void EstablishIfNeeded()
        {
            try
            {
                if (Established) return;
                int cx, cy, cz;
                if (HouseSitePlanner.HasSite)
                { cx = HouseSitePlanner.ChosenSite.X; cy = HouseSitePlanner.ChosenSite.Y; cz = HouseSitePlanner.ChosenSite.Z; }
                else if (ColonyHome.Established)
                { cx = ColonyHome.X; cy = ColonyHome.Y; cz = ColonyHome.Z; }
                else return;
                BuiltState.SaveVillageCenter(cx, cy, cz);
                LastResult = $"village center FIXED at ({cx},{cz}) — plaza {2 * PlazaHalf + 1}x{2 * PlazaHalf + 1}, buildings slot around it";
                LLMNPCsPlugin.LogToFile("[VillageLayout] " + LastResult);
            }
            catch (Exception ex) { LastResult = "village EXC: " + ex.Message; }
        }

        /// <summary>Anchor for building N of the village queue: slots ring the
        /// plaza N, E, S, W, then corners. Returns false when no slot passes the
        /// terrain check (caller falls back to the spiral).</summary>
        public static bool TryGetSlot(int index, int fw, int fh, out int ox, out int oz, out int ay, out string slotName)
        {
            ox = oz = ay = 0; slotName = "";
            if (!BuiltState.TryGetVillageCenter(out int cx, out ay, out int cz)) return false;
            int r = PlazaHalf + StreetGap;
            // (name, originX, originZ) — building rects sit OUTSIDE the plaza ring,
            // roughly centered on their side.
            var slots = new[]
            {
                new { Name = "north", X = cx - fw / 2,        Z = cz + r + 1 },
                new { Name = "east",  X = cx + r + 1,         Z = cz - fh / 2 },
                new { Name = "south", X = cx - fw / 2,        Z = cz - r - fh },
                new { Name = "west",  X = cx - r - fw,        Z = cz - fh / 2 },
                new { Name = "northeast", X = cx + r + 1,     Z = cz + r + 1 },
                new { Name = "southeast", X = cx + r + 1,     Z = cz - r - fh },
                new { Name = "southwest", X = cx - r - fw,    Z = cz - r - fh },
                new { Name = "northwest", X = cx - r - fw,    Z = cz + r + 1 },
            };
            // Zone rects fetched once per call — the slot the Dolgellau longhouse
            // took contained the colony's stockpiles, putting a corpse pile
            // INSIDE the house (Ken, eyes-on 2026-07-12). A slot with someone's
            // stockpile in it is not clear.
            var zoneRects = StockpileZoner.GetZoneRects();
            var swSlots = System.Diagnostics.Stopwatch.StartNew();
            for (int k = 0; k < slots.Length; k++)
            {
                // 50ms budget across the slot ring (each slot = ~260 map queries;
                // under water-sim contention that's seconds — same freeze class).
                if (swSlots.ElapsedMilliseconds > 50) return false;   // caller's cooldown handles retry
                var s = slots[(index + k) % slots.Length];   // building i prefers slot i, then walks the ring
                bool clear = true;
                for (int ix = 0; ix < fw && clear; ix++)
                    for (int iz = 0; iz < fh && clear; iz++)
                        if (!WorldMap.SnapshotBuildableDry(s.X + ix, ay, s.Z + iz)
                            || StockpileZoner.CellInRects(zoneRects, s.X + ix, s.Z + iz)
                            || !StockpilePlacer.CanPlaceWallAt(s.X + ix, ay, s.Z + iz)
                            || !StockpilePlacer.CellIsDry(s.X + ix, ay, s.Z + iz)) clear = false;   // no marsh housing, no zone-squatting
                if (!clear) continue;
                ox = s.X; oz = s.Z; slotName = s.Name;
                return true;
            }
            return false;
        }
    }
}
