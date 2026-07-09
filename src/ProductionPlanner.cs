using System;
using System.Collections;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// WORKSTATIONS CRAFT THINGS (Ken's find): tables are production stations
    /// with QUEUES — the research table crafts 'Chronicle' resources (Job:
    /// Research) that advanced tech requires; the campfire crafts meals. The
    /// colony must KEEP QUEUES FILLED or stations sit idle forever (why the
    /// campfire never cooked).
    ///
    /// Ground truth (decompiled):
    ///   building -> BuildingsManagerMain.GetComponentInstance&lt;ProductionComponentInstance&gt;
    ///   component -> ProductionSystemInstance.AddNewProduction(Production blueprint)
    ///   blueprint -> Repository&lt;ProductionRepository, Production&gt;.GetByID(id)
    ///
    /// v1: keep ONE 'chronicle' order queued at the basic research table.
    /// Dumps available production ids once (validation/production_ids.txt) so
    /// the cookfire meal id can be wired next.
    /// </summary>
    public static class ProductionPlanner
    {
        public static string LastResult = "(idle)";
        private static bool _dumped = false;

        public static void Reset() { LastResult = "(idle)"; }

        private static Type FindTypeByName(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }
        private static object RepoInstance(string shortName)
        {
            var t = FindTypeByName(shortName);
            for (var x = t; x != null; x = x.BaseType)
            {
                var p = x.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) { try { return p.GetValue(null, null); } catch { } }
            }
            return null;
        }
        private static MethodInfo FindMethod(Type start, string name, int argc)
        {
            for (var t = start; t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (m.Name == name && m.GetParameters().Length == argc) return m;
            return null;
        }

        /// <summary>Keep one chronicle queued at the (constructed) research table.</summary>
        public static string Tick(string tableId = "basic_research_table", string productId = "chronicle")
        {
            try
            {
                // 1) find the constructed table instance
                var vmT = FindTypeByName("VillageManager");
                var village = vmT?.GetProperty("ActiveVillage", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                var map = village?.GetType().GetProperty("Map")?.GetValue(village, null);
                var bmm = map?.GetType().GetProperty("BuildingsManagerMain")?.GetValue(map, null);
                if (bmm == null) return LastResult = "prod: world not ready";
                var bbiT = FindTypeByName("BaseBuildingInstance");
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(bbiT);
                var all = Activator.CreateInstance(listT);
                bmm.GetType().GetMethod("GetAllBuildings", new[] { listT })?.Invoke(bmm, new[] { all });
                object table = null;
                foreach (var inst in (IEnumerable)all)
                {
                    if (inst == null) continue;
                    var t = inst.GetType();
                    if (t.GetProperty("ConstructionPhase")?.GetValue(inst, null)?.ToString() == "Blueprint") continue;
                    var bp = t.GetProperty("Blueprint")?.GetValue(inst, null);
                    string id = null;
                    try { id = bp?.GetType().GetMethod("GetID")?.Invoke(bp, null) as string; } catch { }
                    if (id == tableId) { table = inst; break; }
                }
                if (table == null) return LastResult = "prod: no constructed " + tableId;

                // 2) its production component via BuildingsManagerMain.GetComponentInstance<T>
                var pciT = FindTypeByName("ProductionComponentInstance");
                var getComp = bmm.GetType().GetMethod("GetComponentInstance")?.MakeGenericMethod(pciT);
                var comp = getComp?.Invoke(bmm, new[] { table });
                if (comp == null) return LastResult = "prod: table has no ProductionComponentInstance";

                // 3) the ProductionSystemInstance (property or field on the component)
                object sys = comp.GetType().GetProperty("ProductionSystem")?.GetValue(comp, null);
                if (sys == null)
                    foreach (var f in comp.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (f.FieldType.Name == "ProductionSystemInstance") { sys = f.GetValue(comp); break; }
                if (sys == null) return LastResult = "prod: no ProductionSystemInstance on component";

                // 4) queue state: count existing ProductionInstance entries
                int queued = 0;
                foreach (var f in sys.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!(f.GetValue(sys) is IEnumerable en) || f.GetValue(sys) is string) continue;
                    bool isProdList = false; int c = 0;
                    foreach (var it in en) { if (it != null && it.GetType().Name == "ProductionInstance") { isProdList = true; c++; } }
                    if (isProdList) { queued = c; break; }
                }
                if (queued > 0) return LastResult = $"prod: {queued} order(s) already queued at {tableId}";

                // 5) production blueprint by id (+ one-time id dump for cookfire wiring)
                var repo = RepoInstance("ProductionRepository");
                if (repo == null) return LastResult = "prod: no ProductionRepository";
                if (!_dumped)
                {
                    _dumped = true;
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        var getAllItems = FindMethod(repo.GetType(), "GetAllItems", 0);
                        if (getAllItems?.Invoke(repo, null) is IEnumerable items)
                            foreach (var it in items)
                            { try { sb.Append(it.GetType().GetMethod("GetID")?.Invoke(it, null) as string).Append('\n'); } catch { } }
                        System.IO.File.WriteAllText(@"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\production_ids.txt", sb.ToString());
                    }
                    catch { }
                }
                var getById = FindMethod(repo.GetType(), "GetByID", 1);
                object prodBp = null;
                try { prodBp = getById?.Invoke(repo, new object[] { productId }); } catch { }
                if (prodBp == null) return LastResult = $"prod: no production blueprint '{productId}' (see production_ids.txt)";

                // 6) queue it — the game's own AddNewProduction (creates the real order)
                var add = FindMethod(sys.GetType(), "AddNewProduction", 1);
                if (add == null) return LastResult = "prod: no AddNewProduction";
                try
                {
                    add.Invoke(sys, new[] { prodBp });
                    LastResult = $"prod: QUEUED '{productId}' at {tableId} — settlers with the job will craft it";
                    LLMNPCsPlugin.LogToFile("[ProductionPlanner] " + LastResult);
                }
                catch (Exception ae) { LastResult = "prod: AddNewProduction exc " + (ae.InnerException?.Message ?? ae.Message); }
                return LastResult;
            }
            catch (Exception ex) { return LastResult = "prod EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }
    }
}
