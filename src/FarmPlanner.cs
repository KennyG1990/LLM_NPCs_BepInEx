using System;
using System.Collections;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// FARMING — the sustainable-food leg (hunt/forage are semi-renewable; the
    /// reference docs demand a colony that feeds its own growth). Unlocked by
    /// the colony's OWN research (agriculture_lvl1, activated legally).
    ///
    /// Ground truth (decompiled CropsController + string surface):
    ///   CropsController.CreateCropfield(Vec3Int start, Vec3Int end, string id)
    ///   CanPlaceCropfield / CropfieldExists  (StockpileManager pattern)
    ///   field blueprint ids from CropfieldRepository (GetFirst fallback).
    /// Same worldY gotcha as SpawnStockpile is PROBED: try level-y, verify via
    /// CropfieldExists, retry with worldY (level*MapBlockHeight) if 0 verified.
    /// One 4x4 field per save (BuiltState), placed on dry ground near home.
    /// </summary>
    public static class FarmPlanner
    {
        public static string LastResult = "(idle)";
        private static bool _doneThisSession = false;
        public static void Reset() { _doneThisSession = false; LastResult = "(idle)"; }

        private static Type FindTypeByName(string n)
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
        private static MethodInfo FindMethod(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (m.Name == name) return m;
            return null;
        }

        public static string Tick(int hx, int hy, int hz, int radius)
        {
            if (_doneThisSession) return LastResult;
            try
            {
                if (BuiltState.FarmPlaced) { _doneThisSession = true; return LastResult = "farm: already placed (persisted)"; }

                var ctrlT = FindTypeByName("CropsController");
                var ctrl = ctrlT != null ? Singleton(ctrlT) : null;
                if (ctrl == null) return LastResult = "farm: no CropsController";
                var create = FindMethod(ctrl, "CreateCropfield");
                if (create == null) return LastResult = "farm: no CreateCropfield";

                // field id from CropfieldRepository
                object repo = null; string fieldId = null;
                var repoT = FindTypeByName("CropfieldRepository");
                if (repoT != null) repo = Singleton(repoT);
                if (repo != null)
                {
                    MethodInfo gf = null;
                    for (var x = repo.GetType(); x != null && gf == null; x = x.BaseType)
                        foreach (var m in x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                            if (m.Name == "GetFirst" && m.GetParameters().Length == 0) { gf = m; break; }
                    object bp = null; try { bp = gf?.Invoke(repo, null); } catch { }
                    try { fieldId = bp?.GetType().GetMethod("GetID")?.Invoke(bp, null) as string; } catch { }
                }
                if (fieldId == null) return LastResult = "farm: no cropfield id from repository";

                // RESEARCH GATE (Ken, 2026-07-13: cabbage planted with no agriculture
                // research). Only plant a crop the colony has legally unlocked — same
                // gate a player faces. If locked, DON'T plant; the ResearchPlanner
                // prioritises agriculture, and the farm places once it's researched.
                if (!ResearchGate.IsUnlocked(fieldId))
                    return LastResult = $"farm: crop '{fieldId}' NOT unlocked ({ResearchGate.LastCheck}) — researching agriculture first, no illegal planting";

                // CanPlaceCropfield / CropfieldExists — find their owner (controller or a manager)
                var canPlace = FindMethod(ctrl, "CanPlaceCropfield");
                var exists = FindMethod(ctrl, "CropfieldExists");
                object owner = ctrl;
                if (canPlace == null)
                {
                    foreach (var name in new[] { "CropfieldManager", "CropsManager" })
                    {
                        var t = FindTypeByName(name); var inst = t != null ? Singleton(t) : null;
                        if (inst != null && FindMethod(inst, "CanPlaceCropfield") != null)
                        { owner = inst; canPlace = FindMethod(inst, "CanPlaceCropfield"); exists = FindMethod(inst, "CropfieldExists"); break; }
                    }
                }

                var vec3T = FindTypeByName("Vec3Int");
                object Cell(int x, int y, int z) => vec3T.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) })?.Invoke(new object[] { x, y, z });
                bool CellOk(int x, int z)
                {
                    if (!StockpilePlacer.IsDryBuildableGround(x, hy, z)) return false;
                    if (StockpilePlacer.IsOnStockpile(x, hy, z) || StockpilePlacer.AnyBuildingAt(x, hy, z)) return false;
                    if (canPlace == null) return true;
                    try
                    {
                        var ps = canPlace.GetParameters();
                        var args = ps.Length == 2 ? new[] { Cell(x, hy, z), (object)false } : new[] { Cell(x, hy, z) };
                        return canPlace.Invoke(owner, args) is bool b && b;
                    }
                    catch { return true; }
                }

                const int size = 4;
                for (int r = 4; r <= radius; r++)
                    for (int dx = -r; dx <= r; dx++)
                        for (int dz = -r; dz <= r; dz++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                            int ox = hx + dx, oz = hz + dz;
                            bool ok = true;
                            for (int ix = 0; ix < size && ok; ix++)
                                for (int iz = 0; iz < size && ok; iz++)
                                    if (!CellOk(ox + ix, oz + iz)) ok = false;
                            if (!ok) continue;

                            // try level-y first, verify, then worldY (stockpile gotcha)
                            foreach (int wy in new[] { hy, hy * 3 })
                            {
                                try { create.Invoke(ctrl, new[] { Cell(ox, wy, oz), Cell(ox + size - 1, wy, oz + size - 1), fieldId }); }
                                catch (Exception ce) { LastResult = "farm: create exc " + (ce.InnerException?.Message ?? ce.Message); continue; }
                                int verified = 0;
                                if (exists != null)
                                    for (int ix = 0; ix < size; ix++)
                                        for (int iz = 0; iz < size; iz++)
                                            try { if (exists.Invoke(owner, new[] { Cell(ox + ix, hy, oz + iz) }) is bool eb && eb) verified++; } catch { }
                                if (exists == null || verified > 0)
                                {
                                    _doneThisSession = true;
                                    BuiltState.FarmPlaced = true;
                                    LastResult = $"farm: '{fieldId}' {size}x{size} at ({ox},{hy},{oz}) y-arg={wy} verified={verified} cells — settlers will sow";
                                    LLMNPCsPlugin.LogToFile("[FarmPlanner] " + LastResult);
                                    return LastResult;
                                }
                            }
                        }
                _doneThisSession = true;
                return LastResult = "farm: no clear dry 4x4 near home";
            }
            catch (Exception ex) { return LastResult = "farm EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }
    }
}
