using System;
using System.Collections;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// "The mod must be aware of everything the user is aware of" (Ken).
    /// Reads the SAME per-blueprint truth the game's STATS panel shows the
    /// player. Ground truth (decompiled BaseBuildingInstance):
    ///   ConstructionPhase (Blueprint/...), Reachable, ResourcesAvailable,
    ///   SkilledConstructionWorkerExists — the exact three red lines the player
    ///   sees ("can't be reached" / "not enough allowed resources" /
    ///   "no settler with necessary construction skills").
    /// Scan() summarizes every blueprint-phase building each tick into Current
    /// (telemetry + LLM alerts), so the strategic layer can REACT instead of
    /// placing things and wondering.
    /// </summary>
    public static class BlueprintDiagnostics
    {
        public static string Current = "(none)";
        public static int Blocked = 0;
        // Reaction flags for the strategic layer (set each Scan)
        public static bool AnyNoResources = false, AnyNoSkill = false, AnyUnreachable = false;

        public static string Scan()
        {
            try
            {
                var vmT = FindType("NSMedieval.Village.VillageManager");
                var village = vmT?.GetProperty("ActiveVillage",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                var map = village?.GetType().GetProperty("Map")?.GetValue(village, null);
                var bmm = map?.GetType().GetProperty("BuildingsManagerMain")?.GetValue(map, null);
                if (bmm == null) return Current = "(world not ready)";

                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(FindTypeByName("BaseBuildingInstance"));
                var all = Activator.CreateInstance(listT);
                var getAll = bmm.GetType().GetMethod("GetAllBuildings", new[] { listT });
                if (getAll == null) return Current = "(no GetAllBuildings)";
                getAll.Invoke(bmm, new[] { all });

                var sb = new System.Text.StringBuilder();
                int blueprints = 0; Blocked = 0;
                AnyNoResources = AnyNoSkill = AnyUnreachable = false;
                foreach (var inst in (IEnumerable)all)
                {
                    if (inst == null) continue;
                    var t = inst.GetType();
                    var phase = t.GetProperty("ConstructionPhase")?.GetValue(inst, null)?.ToString();
                    if (phase != "Blueprint") continue;
                    blueprints++;
                    bool reach = ReadBool(inst, t, "Reachable", true);
                    bool res = ReadBool(inst, t, "ResourcesAvailable", true);
                    bool skill = ReadBool(inst, t, "SkilledConstructionWorkerExists", true);
                    if (reach && res && skill) continue;   // buildable — settlers will get to it
                    Blocked++;
                    string id = null;
                    try
                    {
                        var bp = t.GetProperty("Blueprint")?.GetValue(inst, null);
                        id = bp?.GetType().GetMethod("GetID")?.Invoke(bp, null) as string;
                    }
                    catch { }
                    var why = new System.Text.StringBuilder();
                    if (!reach) { why.Append("UNREACHABLE "); AnyUnreachable = true; }
                    if (!res) { why.Append("NO-RESOURCES "); AnyNoResources = true; }
                    if (!skill) { why.Append("NO-SKILLED-WORKER "); AnyNoSkill = true; }
                    sb.Append($"'{id ?? "?"}' blocked: {why.ToString().Trim()}; ");
                }
                Current = blueprints == 0
                    ? "(no blueprints pending)"
                    : (Blocked == 0 ? $"{blueprints} blueprint(s), all buildable"
                                    : $"{Blocked}/{blueprints} blueprint(s) BLOCKED — {sb}");
                return Current;
            }
            catch (Exception ex) { return Current = "EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        private static bool ReadBool(object o, Type t, string prop, bool dflt)
        {
            try { var p = t.GetProperty(prop); return p == null ? dflt : (bool)p.GetValue(o, null); }
            catch { return dflt; }
        }

        private static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { var x = a.GetType(full, false); if (x != null) return x; } catch { } }
            return null;
        }
        private static Type FindTypeByName(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }
    }
}
