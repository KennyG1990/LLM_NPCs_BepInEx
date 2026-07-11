using System.Collections.Generic;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// The colony's HOME waypoint — a single fixed grid reference the whole
    /// autonomous system anchors on, so villagers don't cut/gather/build/hunt
    /// scattered across the entire map. Established ONCE from the centroid of the
    /// settlers at colony start (their initial camp), then held stable. All
    /// placement (stockpile, cook, beds, house) and work designation (tree
    /// chopping) reference this point + a bounded work radius, keeping the village
    /// compact and the sense of "here is home".
    /// </summary>
    public static class ColonyHome
    {
        public static bool Established = false;
        public static int X, Y, Z;              // grid coords of home
        public const int WorkRadius = 22;       // how far from home work/placement may reach
        public static string LastResult = "(none)";

        /// <summary>Clear per-session state. Called by BuiltState on world (re)load.</summary>
        public static void Reset()
        {
            Established = false;
            X = Y = Z = 0;
            StockpilePlacer.HomeAnchor = null;
            LastResult = "(none)";
        }

        /// <summary>MOVE the colony home — coherence (#32). Once the roofed house
        /// stands, the colony LIVES there: every placer, designator and radius
        /// re-anchors on the shelter. Persisted immediately so reloads keep it.
        /// (Live bug this fixes: settlers slept and ate at the original camp
        /// ~100 tiles from their finished house — "Will is rebellious, reason:
        /// Damp" in the rain while the dry roofed house stood empty.)</summary>
        public static void MoveTo(int x, int y, int z, string reason)
        {
            X = x; Y = y; Z = z;
            Established = true;
            StockpilePlacer.HomeAnchor = new[] { X, Y, Z };
            BuiltState.SaveHome(X, Y, Z);
            LastResult = $"home MOVED to ({X},{Y},{Z}) — {reason}";
            LLMNPCsPlugin.LogToFile($"[ColonyHome] {LastResult}");
        }

        /// <summary>Fix HOME. Priority: (1) the PERSISTED home for this save —
        /// settlers scatter once the colony runs, so a reload must NOT re-derive
        /// a drifted centroid (that drift made the house planner search a new
        /// neighborhood every load = the duplicate-house bug); (2) fresh centroid
        /// of the settlers' current nodes (first-ever run on this save, when
        /// they're clustered at camp), then persisted.</summary>
        public static bool Establish(List<Settler> settlers)
        {
            if (Established) return true;

            // (1) Persisted home from a previous session with this save.
            if (BuiltState.TryGetHome(out int px, out int py, out int pz))
            {
                X = px; Y = py; Z = pz;
                Established = true;
                StockpilePlacer.HomeAnchor = new[] { X, Y, Z };
                LastResult = $"home RESTORED at ({X},{Y},{Z}) from persisted state, radius {WorkRadius}";
                LLMNPCsPlugin.LogToFile($"[ColonyHome] {LastResult}");
                return true;
            }

            // (2) First run on this save: centroid of the settlers at camp.
            if (settlers == null || settlers.Count == 0) { LastResult = "no settlers to anchor"; return false; }

            long sx = 0, sy = 0, sz = 0; int n = 0;
            foreach (var s in settlers)
            {
                if (s == null || s.gameObject == null) continue;
                var node = StockpilePlacer.SettlerNode(s.gameObject);
                if (node == null) continue;
                sx += node[0]; sy += node[1]; sz += node[2]; n++;
            }
            if (n == 0) { LastResult = "no settler nodes resolved"; return false; }

            X = (int)(sx / n); Y = (int)(sy / n); Z = (int)(sz / n);
            Established = true;
            // Point every placer / designator at HOME instead of a roaming settler.
            StockpilePlacer.HomeAnchor = new[] { X, Y, Z };
            BuiltState.SaveHome(X, Y, Z);   // stable across reloads from now on
            LastResult = $"home fixed at ({X},{Y},{Z}) from {n} settlers, radius {WorkRadius}";
            LLMNPCsPlugin.LogToFile($"[ColonyHome] {LastResult}");
            return true;
        }
    }
}
