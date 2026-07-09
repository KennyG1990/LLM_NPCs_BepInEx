using System;
using System.Collections;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// STOCKPILE HYGIENE (Ken: "food and poop should not be near each other").
    /// Every zone currently allows EVERYTHING -> waste/carcasses beside food.
    /// The game's own filter lives at StockpileInstance.ResourcesFilter
    /// (decompiled) with AllowResource / RemoveAllowedResourceByGroupId /
    /// ClearAllowedResourceTypes / SetAllowedResourceTypes.
    ///
    /// v1 (dump-first discipline): reflect one zone's ResourcesFilter, dump its
    /// group/category ids + member shape to validation/filter_groups.txt so v2
    /// can specialize zones with REAL ids:
    ///   FOOD zone (near cookfire, high prio) / MATERIALS / GOODS /
    ///   REFUSE (waste+carcass only, far from home, low prio).
    /// </summary>
    public static class StockpileZoner
    {
        public static string LastResult = "(idle)";
        private static bool _dumped = false;
        public static void Reset() { _dumped = false; LastResult = "(idle)"; }

        /// <summary>v2 (filter API live-dumped in filter_groups.txt): remove
        /// waste+carcass permission from every zone EXCEPT the farthest from
        /// home — haulers then have exactly one legal refuse destination, a
        /// natural dump away from the food. Verified via HasGroupIdentifierEnabled.</summary>
        public static string Apply(int hx, int hz)
        {
            try
            {
                var mgrT = FindTypeByName("StockpileManager");
                object mgr = null;
                foreach (var c in UnityEngine.Object.FindObjectsOfType(mgrT)) { mgr = c; break; }
                var piles = mgr?.GetType().GetProperty("Stockpiles")?.GetValue(mgr, null) as IEnumerable;
                if (piles == null) return LastResult = "zoner: no stockpiles";

                // collect (instance, distance) — farthest keeps everything
                var zones = new System.Collections.Generic.List<(object sp, int d)>();
                foreach (var sp in piles)
                {
                    if (sp == null) continue;
                    var start = sp.GetType().GetProperty("Start")?.GetValue(sp, null);
                    if (start == null) continue;
                    int sx = 0, sz = 0;
                    try
                    {
                        sx = Convert.ToInt32(start.GetType().GetField("x")?.GetValue(start) ?? 0);
                        sz = Convert.ToInt32(start.GetType().GetField("z")?.GetValue(start) ?? 0);
                    }
                    catch { continue; }
                    zones.Add((sp, Math.Abs(sx - hx) + Math.Abs(sz - hz)));
                }
                if (zones.Count < 2) return LastResult = "zoner: <2 zones, nothing to specialize";
                zones.Sort((a, b) => a.d.CompareTo(b.d));
                int cleaned = 0, already = 0;
                for (int i = 0; i < zones.Count - 1; i++)   // all but farthest
                {
                    var filter = zones[i].sp.GetType().GetProperty("ResourcesFilter")?.GetValue(zones[i].sp, null);
                    if (filter == null) continue;
                    var has = filter.GetType().GetMethod("HasGroupIdentifierEnabled");
                    var rem = filter.GetType().GetMethod("RemoveAllowedResourceByGroupId");
                    if (rem == null) continue;
                    bool changed = false;
                    foreach (var gid in new[] { "waste", "Waste", "carcass", "Carcass" })
                    {
                        try
                        {
                            bool enabled = has != null && has.Invoke(filter, new object[] { gid }) is bool hb && hb;
                            if (!enabled) continue;
                            rem.Invoke(filter, new object[] { gid });
                            changed = true;
                        }
                        catch { }
                    }
                    if (changed) cleaned++; else already++;
                }
                LastResult = $"zoner: {cleaned} zone(s) cleaned of waste/carcass ({already} already clean); farthest zone (d={zones[zones.Count - 1].d}) is the dump";
                if (cleaned > 0) LLMNPCsPlugin.LogToFile("[StockpileZoner] " + LastResult);
                return LastResult;
            }
            catch (Exception ex) { return LastResult = "zoner EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        public static string Tick()
        {
            if (_dumped) return LastResult;
            try
            {
                var mgrT = FindTypeByName("StockpileManager");
                object mgr = null;
                foreach (var c in UnityEngine.Object.FindObjectsOfType(mgrT)) { mgr = c; break; }
                if (mgr == null) return LastResult = "zoner: no StockpileManager";
                var piles = mgr.GetType().GetProperty("Stockpiles")?.GetValue(mgr, null) as IEnumerable;
                if (piles == null) return LastResult = "zoner: no Stockpiles";

                foreach (var sp in piles)
                {
                    if (sp == null) continue;
                    var filter = sp.GetType().GetProperty("ResourcesFilter")?.GetValue(sp, null);
                    if (filter == null) continue;
                    var sb = new System.Text.StringBuilder();
                    var ft = filter.GetType();
                    sb.Append("FILTER TYPE: ").Append(ft.FullName).Append('\n').Append("METHODS:\n");
                    foreach (var m in ft.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        sb.Append("  ").Append(m.Name).Append('(');
                        foreach (var p in m.GetParameters()) sb.Append(p.ParameterType.Name).Append(' ').Append(p.Name).Append(',');
                        sb.Append(")\n");
                    }
                    sb.Append("FIELDS:\n");
                    foreach (var f in ft.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        sb.Append("  ").Append(f.FieldType.Name).Append(' ').Append(f.Name);
                        var val = f.GetValue(filter);
                        if (val is IEnumerable en && !(val is string))
                        {
                            int n = 0; var sample = new System.Text.StringBuilder();
                            foreach (var it in en)
                            {
                                n++;
                                if (n <= 12 && it != null)
                                {
                                    string id = null;
                                    try { id = it.GetType().GetMethod("GetID")?.Invoke(it, null) as string ?? it.ToString(); } catch { id = it.ToString(); }
                                    sample.Append(id).Append(',');
                                }
                            }
                            sb.Append(" count=").Append(n).Append(" sample=[").Append(sample).Append(']');
                        }
                        else sb.Append(" = ").Append(val);
                        sb.Append('\n');
                    }
                    System.IO.File.WriteAllText(
                        @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\filter_groups.txt", sb.ToString());
                    _dumped = true;
                    LastResult = "zoner: filter surface dumped to filter_groups.txt (v2 applies zones)";
                    LLMNPCsPlugin.LogToFile("[StockpileZoner] " + LastResult);
                    return LastResult;
                }
                return LastResult = "zoner: no stockpile with filter yet";
            }
            catch (Exception ex) { return LastResult = "zoner EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        private static Type FindTypeByName(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }
    }
}
