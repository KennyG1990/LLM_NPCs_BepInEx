using System;
using System.Collections;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Is a blueprint / crop / item LEGALLY available to the colony RIGHT NOW — the
    /// same gate a human player faces? A thing is available iff:
    ///   (1) no research gates it  (ResearchManager.UnlockedByDefault(id) == true), OR
    ///   (2) the research that unlocks it has been COMPLETED
    ///       (CurrentVillageData.GetUnlockedItems() contains it).
    ///
    /// Ken, 2026-07-13: "how the fuck did they plant a cabbage field without the
    /// research to do that." The mod was grabbing CropfieldRepository.GetFirst() and
    /// planting it with no unlock check. This mirrors the game's own gating so the mod
    /// never builds/plants what a player couldn't.
    ///
    /// NO FALLBACK (Ken's law): if availability cannot be determined, returns FALSE —
    /// we do NOT place the unverifiable thing. A visibly missing farm/building is the
    /// honest signal to fix the gate, not silent illegal placement.
    /// </summary>
    public static class ResearchGate
    {
        public static string LastCheck = "(none)";

        // Per-id cache. UnlockedByDefault() iterates every research model, so calling
        // it per placed cell (dozens per house) is costly — cache it. TRUE is cached
        // permanently (research never reverts within a colony); FALSE (locked) is
        // re-checked each call so a newly-researched item unlocks the same tick. On a
        // fresh colony / different save load, call Reset().
        private static readonly System.Collections.Generic.HashSet<string> _unlocked =
            new System.Collections.Generic.HashSet<string>();

        public static void Reset() { _unlocked.Clear(); LastCheck = "(reset)"; }

        /// <summary>DIAGNOSTIC: dump the gate decision for the survival-critical ids and
        /// the full unlocked-items set, so we can see WHY basic construction is (in)correctly
        /// gated. Written to validation/research_gate_debug.txt.</summary>
        public static void DumpDebug()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# research-gate debug (does the gate correctly allow default construction?)");
            string[] survival = { "wood_floor", "wood_wall_element", "wood_door", "wood_roof_whole",
                "camp_fire", "hay_sleeping_spot", "basic_research_table", "butchering_table",
                "smokehouse", "sewing_station", "fletchers_table", "cabbage_cropfield" };
            foreach (var id in survival)
            {
                bool u = IsUnlocked(id);
                sb.AppendLine($"{id}: {(u ? "UNLOCKED" : "LOCKED")}  ({LastCheck})");
            }
            // raw dumps
            try
            {
                var rmT = Find("ResearchManager"); var rm = Singleton(rmT);
                if (rm != null)
                {
                    var ubd = rm.GetType().GetMethod("UnlockedByDefault", new[] { typeof(string) });
                    sb.AppendLine($"\n# UnlockedByDefault raw: wood_floor={ubd?.Invoke(rm, new object[]{"wood_floor"})} camp_fire={ubd?.Invoke(rm, new object[]{"camp_fire"})}");
                }
                var gsc = Find("GlobalSaveController");
                var cvd = gsc?.GetField("CurrentVillageData", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var gui = cvd?.GetType().GetMethod("GetUnlockedItems", Type.EmptyTypes);
                var items = gui?.Invoke(cvd, null);
                sb.AppendLine($"\n# GetUnlockedItems returned type: {items?.GetType().FullName ?? "NULL"}");
                if (items is IEnumerable en)
                {
                    int n = 0; sb.Append("# unlocked ids: ");
                    foreach (var it in en) { sb.Append(it?.ToString()).Append(' '); if (++n > 400) break; }
                    sb.AppendLine($"\n# unlocked count: {n}");
                }
            }
            catch (Exception ex) { sb.AppendLine("dump EXC: " + ex.Message); }
            try { System.IO.File.WriteAllText(@"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\research_gate_debug.txt", sb.ToString()); } catch { }
        }

        /// <summary>Cached gate — use this at hot placement chokepoints.</summary>
        public static bool IsUnlockedCached(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (_unlocked.Contains(id)) return true;      // proven unlocked earlier
            if (IsUnlocked(id)) { _unlocked.Add(id); return true; }
            return false;                                  // still locked — re-check next time
        }

        private static Type Find(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }
        private static object Singleton(Type t)
        {
            for (var x = t; x != null; x = x.BaseType)
            {
                var p = x.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) { try { return p.GetValue(null, null); } catch { } }
            }
            return null;
        }

        public static bool IsUnlocked(string id)
        {
            if (string.IsNullOrEmpty(id)) { LastCheck = "empty id"; return false; }
            try
            {
                // (1) not gated by any research -> always available to a player
                var rm = Singleton(Find("ResearchManager"));
                if (rm != null)
                {
                    var ubd = rm.GetType().GetMethod("UnlockedByDefault", new[] { typeof(string) });
                    if (ubd != null && ubd.Invoke(rm, new object[] { id }) is bool byDefault && byDefault)
                    { LastCheck = id + ": unlocked-by-default"; return true; }
                }
                // (2) unlocked by COMPLETED research -> in CurrentVillageData.GetUnlockedItems()
                var gsc = Find("GlobalSaveController");
                var cvd = gsc?.GetField("CurrentVillageData", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (cvd != null)
                {
                    var gui = cvd.GetType().GetMethod("GetUnlockedItems", Type.EmptyTypes);
                    if (gui?.Invoke(cvd, null) is IEnumerable items)
                        foreach (var it in items)
                            if (it != null && it.ToString() == id) { LastCheck = id + ": unlocked-by-research"; return true; }
                }
                LastCheck = id + ": LOCKED (research needed)";
                return false;
            }
            catch (Exception ex) { LastCheck = id + ": check-EXC " + (ex.InnerException?.Message ?? ex.Message); return false; }
        }
    }
}
