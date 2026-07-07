using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// B3 slice 1: real in-world stockpile placement via the game's own
    /// NSMedieval.Stockpiles.StockpileManager (surface captured by
    /// GameApiScanner on 2026-07-06):
    ///
    ///   Void SpawnStockpile(Stockpile blueprint, Vec3Int start, Vec3Int end)
    ///   Boolean CanPlaceStockpile(Vec3Int v, Boolean ignoreExistingStockpileCheck)
    ///   IEnumerable Stockpiles { get; }   (existing instances -> .Blueprint)
    ///
    /// The NPC "decides" where: a spiral search around the settler finds the
    /// first rectangle the game itself validates via CanPlaceStockpile.
    /// </summary>
    public static class StockpilePlacer
    {
        private static Type _managerType;
        private static Type _vec3IntType;

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static Type FindTypeByName(string shortName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (t.Name == shortName) return t;
                }
                catch { }
            }
            return null;
        }

        private static object ManagerInstance()
        {
            _managerType = _managerType ?? FindType("NSMedieval.Stockpiles.StockpileManager");
            if (_managerType == null) return null;
            var prop = _managerType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            return prop?.GetValue(null, null);
        }

        private static object MakeVec3Int(int x, int y, int z)
        {
            _vec3IntType = _vec3IntType ?? FindTypeByName("Vec3Int");
            if (_vec3IntType == null) return null;
            var ctor = _vec3IntType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
            return ctor?.Invoke(new object[] { x, y, z });
        }

        /// <summary>
        /// Places a size x size stockpile near the settler. Returns a
        /// human-readable result string; prefix "ok:" on success.
        /// </summary>
        public static string TryPlaceStockpileNear(GameObject settlerGo, int size = 2)
        {
            try
            {
                // The MonoSingleton Instance proved to be a non-live manager
                // (exists=False at its own stockpile's Start). Probe every
                // scene instance and pick the one that verifies against its
                // own data; fall back to the singleton.
                object manager = null;
                _managerType = _managerType ?? FindType("NSMedieval.Stockpiles.StockpileManager");
                if (_managerType != null)
                {
                    var candidates = UnityEngine.Object.FindObjectsOfType(_managerType);
                    foreach (var candidate in candidates)
                    {
                        if (SelfVerifies(candidate))
                        {
                            manager = candidate;
                            break;
                        }
                        manager = manager ?? candidate;
                    }
                }
                manager = manager ?? ManagerInstance();
                if (manager == null)
                    return "StockpileManager not found (is a save loaded?)";

                // Blueprint: reuse the model from an existing stockpile.
                object blueprint = null;
                var stockpilesProp = manager.GetType().GetProperty("Stockpiles");
                var existing = stockpilesProp?.GetValue(manager, null) as IEnumerable;
                if (existing != null)
                {
                    foreach (var instance in existing)
                    {
                        blueprint = instance?.GetType().GetProperty("Blueprint")?.GetValue(instance, null);
                        if (blueprint != null) break;
                    }
                }
                if (blueprint == null)
                    return "no existing stockpile to source a blueprint from (repository lookup not yet implemented)";

                var canPlace = manager.GetType().GetMethod("CanPlaceStockpile");
                var spawn = manager.GetType().GetMethod("SpawnStockpile");
                var existsMethod = manager.GetType().GetMethod("StockpileExists");
                if (canPlace == null || spawn == null || existsMethod == null)
                    return "StockpileManager API mismatch (CanPlaceStockpile/SpawnStockpile/StockpileExists missing)";

                // Anchor on the SETTLER'S REAL MAP NODE. Ground truth (decompiled
                // VillageMap): world->grid is GridUtils.GetGridPosition, exposed
                // as VillageMap.GetNodeByWorldPos(worldPos); node.Position is the
                // canonical Vec3Int grid coord. Hand-rounding transform.position
                // queried the WRONG nodes — that was the real bug.
                var vmType = FindType("NSMedieval.Village.VillageManager");
                var activeVillage = vmType?.GetProperty("ActiveVillage",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                var map = activeVillage?.GetType().GetProperty("Map")?.GetValue(activeVillage, null);
                if (map == null)
                    return "no active VillageMap (save not fully loaded?)";
                var origin = settlerGo != null ? settlerGo.transform.position : Vector3.zero;
                var settlerNode = map.GetType().GetMethod("GetNodeByWorldPos")?.Invoke(map, new object[] { origin });
                if (settlerNode == null)
                    return $"settler node null at world {origin}";
                var anchorPos = settlerNode.GetType().GetProperty("Position")?.GetValue(settlerNode, null);
                int ax = ReadIntField(anchorPos, "x", "X");
                int ay = ReadIntField(anchorPos, "y", "Y");
                int az = ReadIntField(anchorPos, "z", "Z");

                // Scan rings around the settler's node cell; place at the first
                // cell the game itself validates (strict CanPlaceStockpile, the
                // same variant SpawnStockpile uses internally via GetMeshArea).
                for (int radius = 0; radius <= 12; radius++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        foreach (var dz in (radius == 0)
                                 ? new[] { 0 }.AsEnumerable()
                                 : (Math.Abs(dx) == radius
                                    ? Enumerable.Range(-radius, radius * 2 + 1)
                                    : new[] { -radius, radius }.AsEnumerable()))
                        {
                            int cx = ax + dx, cz = az + dz;
                            var cell = MakeVec3Int(cx, ay, cz);
                            if (cell == null) return "Vec3Int type not resolvable";
                            bool ok;
                            try { ok = (bool)canPlace.Invoke(manager, new[] { cell, (object)false }); }
                            catch { ok = false; }
                            if (!ok) continue;
                            var end = MakeVec3Int(cx + size - 1, ay, cz + size - 1);
                            spawn.Invoke(manager, new[] { blueprint, cell, end });
                            // Success = the game registered the stockpile in the
                            // grid. Check EVERY cell of the placed rectangle, not
                            // just the anchor (the anchor corner can fall outside
                            // the game-validated mesh).
                            int registered = 0;
                            for (int ix = 0; ix < size; ix++)
                                for (int iz = 0; iz < size; iz++)
                                {
                                    var pc = MakeVec3Int(cx + ix, ay, cz + iz);
                                    try { if ((bool)existsMethod.Invoke(manager, new[] { pc })) registered++; }
                                    catch { }
                                }
                            if (registered > 0)
                                return $"ok: stockpile placed at ({cx},{ay},{cz}) registeredCells={registered}/{size * size} [node-anchored]";
                            // Body ran but nothing registered — keep scanning for a
                            // cell that actually takes, then report if none do.
                            continue;
                        }
                    }
                }

                // Diagnostics grounded on the real node (only if nothing placed).
                string ProbeCell(int x, int y, int z)
                {
                    try
                    {
                        var v = MakeVec3Int(x, y, z);
                        var loose = (bool)canPlace.Invoke(manager, new[] { v, (object)true });
                        var strict = (bool)canPlace.Invoke(manager, new[] { v, (object)false });
                        var ex = (bool)existsMethod.Invoke(manager, new[] { v });
                        return $"({x},{y},{z}) loose={loose} strict={strict} exists={ex}";
                    }
                    catch (Exception e) { return $"({x},{y},{z}) err {e.InnerException?.Message ?? e.Message}"; }
                }
                return $"no placeable {size}x{size} near settler node; rawPos=({origin.x:F2},{origin.y:F2},{origin.z:F2}) "
                       + $"node=({ax},{ay},{az}) ;; nodeCell={ProbeCell(ax, ay, az)} "
                       + $"above={ProbeCell(ax, ay + 1, az)} below={ProbeCell(ax, ay - 1, az)}";
            }
            catch (Exception ex)
            {
                return $"placement error: {ex.InnerException?.Message ?? ex.Message}";
            }
        }

        /// <summary>
        /// PROBE (research 2026-07-06): answers whether we can BYPASS the
        /// CanPlaceStockpile gate. Enumerates every live StockpileManager, then
        /// calls SpawnStockpile DIRECTLY at the settler's cell (no CanPlace
        /// check) and reports whether the stockpile count changed. If it lands,
        /// we own placement and build our own buildability layer on top; if it
        /// no-ops/throws, the manager instance is the real problem.
        /// </summary>
        public static string ProbeDirectSpawn(GameObject settlerGo)
        {
            try
            {
                _managerType = _managerType ?? FindType("NSMedieval.Stockpiles.StockpileManager");
                if (_managerType == null) return "probe: StockpileManager type not found";
                var candidates = UnityEngine.Object.FindObjectsOfType(_managerType);
                var sb = new System.Text.StringBuilder();
                sb.Append($"PROBE mgrs={candidates.Length};");
                object live = null;
                for (int i = 0; i < candidates.Length; i++)
                {
                    var m = candidates[i];
                    int cnt = 0; string firstStart = "none"; bool existsAtFirst = false;
                    object firstInst = null;
                    var sp = m.GetType().GetProperty("Stockpiles")?.GetValue(m, null) as IEnumerable;
                    if (sp != null)
                        foreach (var inst in sp) { if (inst == null) continue; if (firstInst == null) firstInst = inst; cnt++; }
                    var existsM = m.GetType().GetMethod("StockpileExists");
                    if (firstInst != null)
                    {
                        var st = firstInst.GetType().GetProperty("Start")?.GetValue(firstInst, null);
                        firstStart = st?.ToString() ?? "null";
                        if (st != null && existsM != null)
                            try { existsAtFirst = (bool)existsM.Invoke(m, new[] { st }); } catch { }
                    }
                    bool sv = SelfVerifies(m);
                    if (sv && live == null) live = m;
                    sb.Append($" mgr{i} sp={cnt} selfV={sv} start={firstStart} existAtStart={existsAtFirst};");
                }
                var manager = live ?? (candidates.Length > 0 ? candidates[0] : ManagerInstance());
                if (manager == null) return sb.ToString() + " no manager to spawn with";

                var origin = settlerGo != null ? settlerGo.transform.position : Vector3.zero;
                int gx = Mathf.RoundToInt(origin.x), gy = Mathf.RoundToInt(origin.y), gz = Mathf.RoundToInt(origin.z);
                sb.Append($" settlerGrid=({gx},{gy},{gz}) usingLiveMgr={(live != null)};");

                object blueprint = null;
                var stockProp = manager.GetType().GetProperty("Stockpiles")?.GetValue(manager, null) as IEnumerable;
                if (stockProp != null)
                    foreach (var inst in stockProp) { blueprint = inst?.GetType().GetProperty("Blueprint")?.GetValue(inst, null); if (blueprint != null) break; }
                sb.Append($" blueprint={(blueprint != null)};");

                var spawn = manager.GetType().GetMethod("SpawnStockpile");
                var existsMethod = manager.GetType().GetMethod("StockpileExists");
                if (spawn == null || blueprint == null) return sb.ToString() + " cannot direct-spawn (missing spawn or blueprint)";

                int before = CountStockpiles(manager);
                var start = MakeVec3Int(gx, gy, gz);
                var end = MakeVec3Int(gx + 1, gy, gz + 1);
                string spawnResult;
                try { spawn.Invoke(manager, new[] { blueprint, start, end }); spawnResult = "invoked"; }
                catch (Exception ex) { spawnResult = "EXC:" + (ex.InnerException?.Message ?? ex.Message); }
                int after = CountStockpiles(manager);
                bool existsAfter = false;
                try { existsAfter = (bool)existsMethod.Invoke(manager, new[] { start }); } catch { }
                sb.Append($" DIRECTSPAWN@({gx},{gy},{gz})={spawnResult} before={before} after={after} existsAfter={existsAfter}");
                return sb.ToString();
            }
            catch (Exception ex) { return "probe error: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        private static int CountStockpiles(object manager)
        {
            try
            {
                var sp = manager.GetType().GetProperty("Stockpiles")?.GetValue(manager, null) as IEnumerable;
                if (sp == null) return -1;
                int c = 0; foreach (var x in sp) c++; return c;
            }
            catch { return -2; }
        }

        /// <summary>True when this manager instance confirms one of its own
        /// stockpiles exists at that stockpile's Start cell.</summary>
        private static bool SelfVerifies(object manager)
        {
            try
            {
                var stockpiles = manager.GetType().GetProperty("Stockpiles")?.GetValue(manager, null) as IEnumerable;
                var exists = manager.GetType().GetMethod("StockpileExists");
                if (stockpiles == null || exists == null) return false;
                foreach (var instance in stockpiles)
                {
                    var start = instance?.GetType().GetProperty("Start")?.GetValue(instance, null);
                    if (start == null) continue;
                    if (exists.Invoke(manager, new[] { start }) is bool ok && ok)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static int ReadIntField(object obj, params string[] names)
        {
            if (obj == null) return 0;
            foreach (var name in names)
            {
                var f = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(int))
                    return (int)f.GetValue(obj);
                var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.PropertyType == typeof(int))
                    return (int)p.GetValue(obj, null);
            }
            return 0;
        }

        private static bool RectPlaceable(object manager, MethodInfo canPlace, MethodInfo exists, int x, int y, int z, int size)
        {
            // A cell is usable when the terrain check passes with the
            // existing-stockpile pre-check bypassed AND no stockpile occupies
            // it. The strict variant returned false everywhere in live probes.
            for (int ix = 0; ix < size; ix++)
            {
                for (int iz = 0; iz < size; iz++)
                {
                    var cell = MakeVec3Int(x + ix, y, z + iz);
                    if (cell == null) return false;
                    try
                    {
                        if (!(canPlace.Invoke(manager, new[] { cell, (object)true }) is bool ok) || !ok)
                            return false;
                        if (exists.Invoke(manager, new[] { cell }) is bool occupied && occupied)
                            return false;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
