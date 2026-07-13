using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// UNDERGROUND CONSTRUCTION v1 — a food CELLAR dug into a hillside.
    /// Going Medieval preserves food better underground (stable temperature),
    /// so a settler who watches food rot WANTS a cellar. v1 scope: find a solid
    /// hill face at HOME level adjacent to walkable ground (so settlers can walk
    /// in — no stairs needed), designate a 3x3 dig via the game's OWN dig-marker
    /// path, which creates real dig jobs (DigGoal) settlers then work.
    ///
    /// Ground truth (decompiled DigMarkerResourceManager):
    ///   CreateInstance(modelId, prefabId, pos) -> DigMarkerResourceInstance
    ///   + ConstructionJobManager.CreateDigJobs(inst); creation is routed via
    ///   MapResourceManager.OnCreateResource(modelId, prefabId, worldPos).
    ///   modelId comes from Repository&lt;DigMarkerResourceRepository, DigMarkerResource&gt;.
    ///
    /// Idempotent: DigMarkerExists() gates re-marking; progress persisted via
    /// BuiltState ("cellar.*"); session reset by BuiltState like every module.
    /// </summary>
    public static class CellarBuilder
    {
        public static string LastResult = "(idle)";
        private static bool _doneThisSession = false;
        private static int _resumeRing = 2;   // time-budgeted scan resumes here next tick

        public static void Reset() { _doneThisSession = false; _resumeRing = 2; LastResult = "(idle)"; }

        private static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { var t = a.GetType(full, false); if (t != null) return t; } catch { } }
            return null;
        }
        private static Type FindTypeByName(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }
        private static object Singleton(string shortName)
        {
            var t = FindTypeByName(shortName);
            if (t == null) return null;
            for (var x = t; x != null; x = x.BaseType)
            {
                var p = x.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) { try { return p.GetValue(null, null); } catch { } }
            }
            return null;
        }

        /// <summary>Designate a 3x3 cellar dig into a hill face near home.
        /// Returns a status string; marks at most one cellar per save.</summary>
        public static string DigCellarNear(int hx, int hy, int hz, int radius)
        {
            if (_doneThisSession) return LastResult;
            try
            {
                if (BuiltState.CellarMarked) { _doneThisSession = true; return LastResult = "cellar: already marked (persisted)"; }

                var mgr = Singleton("DigMarkerResourceManager");
                if (mgr == null) return LastResult = "cellar: no DigMarkerResourceManager";
                var mgrT = mgr.GetType();
                var exists = mgrT.GetMethod("DigMarkerExists");

                // model id from the repository (how CreateInstance sources it)
                object model = null; string modelId = null;
                var repoT = FindTypeByName("DigMarkerResourceRepository");
                object repo = null;
                for (var x = repoT; x != null && repo == null; x = x.BaseType)
                {
                    var p = x.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (p != null) { try { repo = p.GetValue(null, null); } catch { } }
                }
                if (repo != null)
                {
                    MethodInfo gf = null;
                    for (var x = repo.GetType(); x != null && gf == null; x = x.BaseType)
                        foreach (var m in x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                            if (m.Name == "GetFirst" && m.GetParameters().Length == 0) { gf = m; break; }
                    try { model = gf?.Invoke(repo, null); } catch { }
                    try { modelId = model?.GetType().GetMethod("GetID")?.Invoke(model, null) as string; } catch { }
                }
                if (modelId == null) return LastResult = "cellar: no dig-marker model id";

                // map access for node queries (solid vs walkable)
                var vmT = FindType("NSMedieval.Village.VillageManager");
                var village = vmT?.GetProperty("ActiveVillage", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                var map = village?.GetType().GetProperty("Map")?.GetValue(village, null);
                if (map == null) return LastResult = "cellar: no map";
                var vec3T = FindTypeByName("Vec3Int");
                var getNode = map.GetType().GetMethod("GetNode", new[] { vec3T });
                object MakeCell(int x, int y, int z)
                { return vec3T.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) })?.Invoke(new object[] { x, y, z }); }
                bool NodeSolid(int x, int y, int z)
                {
                    try
                    {
                        var n = getNode?.Invoke(map, new[] { MakeCell(x, y, z) });
                        if (n == null) return false;
                        var vt = n.GetType().GetProperty("VoxelTypeIdByte")?.GetValue(n, null);
                        return vt != null && Convert.ToInt32(vt) != 0;   // 0 = air (ground truth: LoadSavedDigMarkers)
                    }
                    catch { return false; }
                }

                var gridUtils = FindTypeByName("GridUtils");
                var getWorld = gridUtils?.GetMethod("GetWorldPosition", new[] { vec3T });
                // Creation entry point: any *CreateResource*/CreateInstance method
                // whose params start with string and include a Vector3 — signatures
                // vary ('in' modifiers, extra args) so match loosely and DIAGNOSE.
                MethodInfo create = null; var seen = new System.Text.StringBuilder();
                for (var x = mgrT; x != null && create == null; x = x.BaseType)
                    foreach (var m in x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name.IndexOf("CreateResource", StringComparison.OrdinalIgnoreCase) < 0 &&
                            m.Name != "CreateInstance") continue;
                        var ps = m.GetParameters();
                        seen.Append(m.Name + "/" + ps.Length + " ");
                        if (ps.Length == 3 && ps[0].ParameterType == typeof(string) &&
                            (ps[2].ParameterType == typeof(Vector3) || ps[2].ParameterType.Name.StartsWith("Vector3")))
                        { create = m; break; }
                    }
                if (create == null || getWorld == null)
                    return LastResult = $"cellar: no create entry (getWorld={(getWorld != null)}; candidates: {seen})";

                // Find a hill face: a SOLID cell at home level whose neighbor toward
                // home is walkable air — settlers can stand there and mine inward.
                //
                // TIME-BUDGETED + instrumented (2026-07-12): three consecutive
                // main-thread freezes all died between the food log and the
                // cellar log with LastResult never set — this scan (radius 60 ≈
                // 14k reflected GetNode calls, on a map whose water sim is hot)
                // is the prime suspect and previously ran unbounded and silent.
                // Now: entry log, 50ms budget per tick (resumes at the saved
                // ring), progress log every 10 rings — a freeze inside the scan
                // becomes visible and bounded instead of killing the session.
                LLMNPCsPlugin.LogToFile($"[CellarBuilder] scan begin (ring {_resumeRing}..{radius})");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int r = _resumeRing; r <= radius; r++)
                {
                    if (r % 10 == 0) LLMNPCsPlugin.LogToFile($"[CellarBuilder] scan at ring {r}/{radius}");
                    for (int dx = -r; dx <= r; dx++)
                        for (int dz = -r; dz <= r; dz++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                            // per-CELL budget (per-ring allowed ~1000 queries between
                            // checks at r=60 — the 67s-freeze lesson applies here too)
                            if (sw.ElapsedMilliseconds > 50)
                            {
                                _resumeRing = r;
                                return LastResult = $"cellar: scan paused at ring {r}/{radius} (time budget; resumes next tick)";
                            }
                            int fx = hx + dx, fz = hz + dz;
                            if (!NodeSolid(fx, hy, fz)) continue;                 // face voxel must be rock/dirt
                            if (NodeSolid(fx - Math.Sign(dx), hy, fz - (dx == 0 ? Math.Sign(dz) : 0))) continue; // approach cell must be open
                            // mark a 3x3 into the hill (deeper along the approach direction)
                            int ax = Math.Sign(dx) == 0 ? 0 : Math.Sign(dx), az = Math.Sign(dz) == 0 ? 0 : Math.Sign(dz);
                            if (ax == 0 && az == 0) az = 1;
                            int marked = 0;
                            for (int depth = 0; depth < 3; depth++)
                                for (int side = -1; side <= 1; side++)
                                {
                                    int cx = fx + ax * depth + (az != 0 ? side : 0);
                                    int cz = fz + az * depth + (ax != 0 ? side : 0);
                                    if (!NodeSolid(cx, hy, cz)) continue;
                                    var cell = MakeCell(cx, hy, cz);
                                    try { if (exists != null && (bool)exists.Invoke(mgr, new[] { cell })) continue; } catch { }
                                    var wpos = getWorld.Invoke(null, new[] { cell });
                                    try { create.Invoke(mgr, new object[] { modelId, null, (Vector3)wpos }); marked++; }
                                    catch (Exception ce) { LastResult = "cellar: create exc " + (ce.InnerException?.Message ?? ce.Message); }
                                }
                            if (marked > 0)
                            {
                                _doneThisSession = true;
                                BuiltState.CellarMarked = true;
                                LastResult = $"cellar: marked {marked} dig cells into hill face at ({fx},{hy},{fz}) — settlers will mine it (DigGoal)";
                                LLMNPCsPlugin.LogToFile("[CellarBuilder] " + LastResult);
                                return LastResult;
                            }
                        }
                }
                _doneThisSession = true;    // don't rescan every tick on a flat map
                LLMNPCsPlugin.LogToFile($"[CellarBuilder] scan complete — no hill face (radius {radius})");
                return LastResult = "cellar: no minable hill face near home (flat terrain) — needs stairs support (v2)";
            }
            catch (Exception ex) { return LastResult = "cellar EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }
    }
}
