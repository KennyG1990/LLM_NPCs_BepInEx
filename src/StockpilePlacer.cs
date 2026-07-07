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

        private static Type _repoBaseType;

        /// <summary>
        /// Get a Stockpile blueprint (model) from the game's own model
        /// repository, exactly how the game does it
        /// (Repository&lt;StockpileRepository, Stockpile&gt;.Instance). Works on a
        /// FRESH colony with no placed stockpile. Returns null on failure.
        /// </summary>
        internal static string LastBlueprintDiag = "";

        private static object GetStockpileBlueprint()
        {
            try
            {
                // Enumerate ALL loaded types whose name contains "Stockpile" +
                // "Repository" so we don't depend on an exact short name.
                Type stockRepo = FindTypeByName("StockpileRepository");
                if (stockRepo == null)
                {
                    var names = new System.Text.StringBuilder();
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type[] ts; try { ts = asm.GetTypes(); } catch { continue; }
                        foreach (var tt in ts)
                            if (tt.Name.IndexOf("Stockpile", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                tt.Name.IndexOf("Repository", StringComparison.OrdinalIgnoreCase) >= 0)
                            { stockRepo = tt; names.Append(tt.FullName + ","); break; }
                        if (stockRepo != null) break;
                    }
                    LastBlueprintDiag = "repoScan=" + names + "found=" + (stockRepo != null);
                }
                if (stockRepo == null)
                {
                    LastBlueprintDiag = "StockpileRepository type NOT FOUND";
                    return null;
                }
                // Instance is a static prop on the generic base; walk up to find it.
                object instance = null;
                var t = stockRepo;
                while (t != null && instance == null)
                {
                    var p = t.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (p != null) { try { instance = p.GetValue(null, null); } catch { } }
                    t = t.BaseType;
                }
                if (instance == null)
                {
                    LastBlueprintDiag = "repo=" + stockRepo.FullName + " Instance=NULL";
                    return null;
                }
                var itype = instance.GetType();
                // GetFirst(): TM (defined on the generic base). Walk with
                // DeclaredOnly to avoid "Ambiguous match" in the CRTP hierarchy.
                MethodInfo getFirst = null;
                for (var mt = itype; mt != null && getFirst == null; mt = mt.BaseType)
                    foreach (var m in mt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        if (m.Name == "GetFirst" && m.GetParameters().Length == 0) { getFirst = m; break; }
                object bp = null;
                try { bp = getFirst?.Invoke(instance, null); }
                catch (Exception ge) { LastBlueprintDiag = "GetFirst invoke exc: " + (ge.InnerException?.Message ?? ge.Message); }
                if (bp != null) { LastBlueprintDiag = "ok via GetFirst"; return bp; }
                // Fallback: pull first value out of the protected 'dictionary'/'repository'.
                for (var ft = itype; ft != null; ft = ft.BaseType)
                    foreach (var f in ft.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        object val; try { val = f.GetValue(instance); } catch { continue; }
                        if (val is System.Collections.IDictionary d)
                            foreach (var v in d.Values) { if (v != null) { LastBlueprintDiag = "ok via " + f.Name + " dict"; return v; } }
                        if (val is System.Collections.IList lst && lst.Count > 0)
                            foreach (var v in lst) { if (v != null) { LastBlueprintDiag = "ok via " + f.Name + " list"; return v; } }
                    }
                LastBlueprintDiag = $"repo={itype.Name} GetFirst=null(found={getFirst != null}) no-collection-yielded-model";
                return null;
            }
            catch (Exception ex)
            {
                LastBlueprintDiag = "EXC: " + (ex.InnerException?.Message ?? ex.Message);
                return null;
            }
        }

        private static int _mbh = 0;
        private static int GetMapBlockHeight()
        {
            if (_mbh > 0) return _mbh;
            try
            {
                var wt = FindType("NSMedieval.Map.World") ?? FindTypeByName("World");
                var f = wt?.GetField("MapBlockHeight",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) _mbh = Convert.ToInt32(f.GetValue(null));
            }
            catch { }
            if (_mbh <= 0) _mbh = 3; // decompiled default: World.MapBlockHeight = 3
            return _mbh;
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

                // Blueprint from the game's MODEL REPOSITORY — how the game
                // itself does it (StockpileInstance.Blueprint => Repository<
                // StockpileRepository, Stockpile>.Instance.GetByID(...)). This
                // works on a FRESH colony with no existing stockpile. Fallback:
                // copy an existing zone's blueprint if the repo lookup fails.
                object blueprint = GetStockpileBlueprint();
                if (blueprint == null)
                {
                    var stockpilesProp = manager.GetType().GetProperty("Stockpiles");
                    var existing = stockpilesProp?.GetValue(manager, null) as IEnumerable;
                    if (existing != null)
                        foreach (var instance in existing)
                        {
                            blueprint = instance?.GetType().GetProperty("Blueprint")?.GetValue(instance, null);
                            if (blueprint != null) break;
                        }
                }
                if (blueprint == null)
                    return "no stockpile blueprint :: " + LastBlueprintDiag;

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

                // SITE SELECTION (utility-scored, per the RTS reference docs):
                // score every candidate anchor by how many cells of the size x size
                // rectangle the game validates, and place at the MOST-OPEN cell.
                // This prefers open ground and avoids cramped spots (e.g. the
                // settler's own bedroom, where only 1 of 4 cells is free).
                bool CellOk(int x, int z)
                {
                    var c = MakeVec3Int(x, ay, z);
                    try { return c != null && (bool)canPlace.Invoke(manager, new[] { c, (object)false }); }
                    catch { return false; }
                }
                int RectScore(int x, int z)
                {
                    int s = 0;
                    for (int ix = 0; ix < size; ix++)
                        for (int iz = 0; iz < size; iz++)
                            if (CellOk(x + ix, z + iz)) s++;
                    return s;
                }
                int bestScore = 0, bx = 0, bz = 0; bool found = false;
                for (int radius = 0; radius <= 12 && bestScore < size * size; radius++)
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
                            if (!CellOk(cx, cz)) continue;      // anchor must itself be legal
                            int score = RectScore(cx, cz);
                            if (score > bestScore) { bestScore = score; bx = cx; bz = cz; found = true; }
                            if (bestScore == size * size) break;
                        }
                        if (bestScore == size * size) break;
                    }
                }
                if (found && bestScore > 0)
                {
                    // GROUND TRUTH (MeshAreaMaker.GetMeshArea): SpawnStockpile
                    // expects start/end.y in WORLD units and divides by
                    // World.MapBlockHeight(=3) to get the level. node.Position.y
                    // is the LEVEL, so multiply. CanPlaceStockpile/StockpileExists
                    // use the LEVEL directly.
                    int mbh = GetMapBlockHeight();
                    int wy = ay * mbh;
                    var start = MakeVec3Int(bx, wy, bz);
                    var end = MakeVec3Int(bx + size - 1, wy, bz + size - 1);
                    if (start == null || end == null) return "Vec3Int type not resolvable";
                    spawn.Invoke(manager, new[] { blueprint, start, end });
                    int registered = 0;
                    for (int ix = 0; ix < size; ix++)
                        for (int iz = 0; iz < size; iz++)
                        {
                            var pc = MakeVec3Int(bx + ix, ay, bz + iz);
                            try { if ((bool)existsMethod.Invoke(manager, new[] { pc })) registered++; }
                            catch { }
                        }
                    if (registered > 0)
                        return $"ok: stockpile placed level=({bx},{ay},{bz}) worldY={wy} registeredCells={registered}/{size * size} [best-open-ground]";
                    return $"placed level=({bx},{ay},{bz}) worldY={wy} openScore={bestScore} but 0 registered";
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
