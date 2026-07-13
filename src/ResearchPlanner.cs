using System;
using System.Collections;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// PREREQUISITE-CHAIN brain, leg 1: RESEARCH. A colony that wants to grow
    /// from 3 settlers to a town must research (stairs, buildings, farming) —
    /// not just place what's already unlocked.
    ///
    /// Ground truth (decompiled): ResearchManager.GetUnlockableResearchNodes()
    /// -> HashSet&lt;ResearchNodeInstance&gt;; ResearchController.Activate(node)
    /// selects a project the research table then works.
    ///
    /// v1 behavior, once a research_table exists:
    ///   1. Dump every unlockable node id to validation/research_ids.txt (once)
    ///      — ground truth for the NEEDS priority list.
    ///   2. Activate the first match from Needs (persisted per save; never
    ///      re-activates on reload).
    /// </summary>
    public static class ResearchPlanner
    {
        public static string LastResult = "(idle)";
        private static bool _dumped = false, _doneThisSession = false;

        // Priority list — substring matches against node ids. Canon order
        // (Ken's v1.1.x player guide, 2026-07-12): ARCHITECTURE FIRST — it
        // gates beams (15 wood, wall-to-wall), and beams gate every upper
        // storey AND safe cellar ceilings. Then agriculture (the sustainable
        // food leg) and tailoring (flimsy starting clothes carry a standing
        // mood debuff). Stairs/underground follow for the vertical program.
        private static readonly string[] Needs = { "architecture", "agri", "farm", "tailor", "stair", "under", "construction", "cook", "storage" };

        public static void Reset() { _doneThisSession = false; LastResult = "(idle)"; }

        private static Type FindTypeByName(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }
        private static object Singleton(string shortName)
        {
            var t = FindTypeByName(shortName);
            for (var x = t; x != null; x = x.BaseType)
            {
                var p = x.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) { try { return p.GetValue(null, null); } catch { } }
            }
            return null;
        }
        private static string NodeId(object node)
        {
            try
            {
                var id = node.GetType().GetMethod("GetID")?.Invoke(node, null) as string;
                if (id != null) return id;
                var bp = node.GetType().GetProperty("Blueprint")?.GetValue(node, null)
                      ?? node.GetType().GetProperty("Model")?.GetValue(node, null);
                return bp?.GetType().GetMethod("GetID")?.Invoke(bp, null) as string;
            }
            catch { return null; }
        }

        /// <summary>Pick and activate the colony's next research project.</summary>
        public static string Tick()
        {
            if (_doneThisSession) return LastResult;
            try
            {
                var mgr = Singleton("ResearchManager");
                var ctrl = Singleton("ResearchController");
                if (mgr == null || ctrl == null) return LastResult = "research: managers not ready";
                var getNodes = mgr.GetType().GetMethod("GetUnlockableResearchNodes");
                var nodes = getNodes?.Invoke(mgr, null) as IEnumerable;
                if (nodes == null) return LastResult = "research: no unlockable-nodes API";

                var ids = new System.Text.StringBuilder(); int n = 0;
                object pick = null; string pickId = null; int pickRank = int.MaxValue;
                foreach (var node in nodes)
                {
                    var id = NodeId(node); if (id == null) continue;
                    ids.Append(id).Append('\n'); n++;
                    for (int i = 0; i < Needs.Length && i < pickRank; i++)
                        if (id.IndexOf(Needs[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        { pick = node; pickId = id; pickRank = i; break; }
                }
                if (!_dumped)
                {
                    _dumped = true;
                    try { System.IO.File.WriteAllText(
                        @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\research_ids.txt",
                        $"unlockable={n}\n{ids}"); } catch { }
                }
                if (pick == null) { _doneThisSession = true; return LastResult = $"research: no NEEDS match among {n} unlockable (see research_ids.txt)"; }

                // GROUND TRUTH (ResearchManager.OnResearchActivated line 321):
                // Activate(node, afterLoading:false) ENFORCES HasEnoughResources
                // and ALLOCATES the node's RequiredResources from colony stock —
                // it is the game's own legal unlock (refuses when broke). Early
                // nodes cost basic materials; ADVANCED tiers cost research points
                // the (now built) table produces via Intellectual work. So this
                // is the player-equivalent path, not a grant: the game said yes
                // because the colony genuinely paid.
                var act = ctrl.GetType().GetMethod("Activate");
                if (act == null) { _doneThisSession = true; return LastResult = "research: no Activate method"; }
                try
                {
                    var ps = act.GetParameters();
                    act.Invoke(ctrl, ps.Length == 2 ? new object[] { pick, false } : new object[] { pick });
                    _doneThisSession = true;
                    LastResult = $"research: activated '{pickId}' via game's legal path (resources enforced+allocated), rank {pickRank}, {n} unlockable";
                    LLMNPCsPlugin.LogToFile("[ResearchPlanner] " + LastResult);
                }
                catch (Exception ae) { LastResult = "research: Activate exc " + (ae.InnerException?.Message ?? ae.Message); }
                return LastResult;
            }
            catch (Exception ex) { return LastResult = "research EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }
    }
}
