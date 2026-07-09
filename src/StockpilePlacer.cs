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

        /// <summary>When set (grid {x,y,z}), placements anchor on the colony HOME
        /// waypoint instead of the (roaming) settler, keeping the village compact.
        /// Set by ColonyHome.Establish.</summary>
        public static int[] HomeAnchor = null;

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

        // ─── Building (blueprint) placement — real construction path ──────────
        internal static string LastBuildingDiag = "";

        private static object RepoInstance(string repoShortName)
        {
            var repo = FindTypeByName(repoShortName);
            if (repo == null) { LastBuildingDiag = repoShortName + " type not found"; return null; }
            object instance = null;
            for (var t = repo; t != null && instance == null; t = t.BaseType)
            {
                var p = t.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) { try { instance = p.GetValue(null, null); } catch { } }
            }
            if (instance == null) LastBuildingDiag = repoShortName + ".Instance null";
            return instance;
        }

        private static MethodInfo FindMethod(Type start, string name, int argc)
        {
            for (var t = start; t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (m.Name == name && m.GetParameters().Length == argc) return m;
            return null;
        }

        /// <summary>Log every building id in BaseBuildingRepository so the plan can
        /// target the cooking station / walls / roof / door by their true ids.</summary>
        public static void DumpBuildingIds()
        {
            try
            {
                var instance = RepoInstance("BaseBuildingRepository");
                if (instance == null) { LLMNPCsPlugin.LogToFile("[BuildingIds] repo instance null"); return; }
                var itype = instance.GetType();
                var ids = new System.Text.StringBuilder();
                int n = 0;
                for (var ft = itype; ft != null; ft = ft.BaseType)
                    foreach (var f in ft.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        object val; try { val = f.GetValue(instance); } catch { continue; }
                        System.Collections.IEnumerable items = (val as System.Collections.IDictionary)?.Values ?? (val as System.Collections.IEnumerable);
                        if (items == null || val is string) continue;
                        foreach (var it in items)
                        {
                            if (it == null) continue;
                            string bid = null;
                            try { bid = it.GetType().GetMethod("GetID")?.Invoke(it, null) as string; } catch { }
                            if (bid != null) { ids.Append(bid + " "); n++; }
                        }
                        if (n > 0) break;
                    }
                // chunk the log so no single line is enormous
                var s = ids.ToString();
                LLMNPCsPlugin.LogToFile($"[BuildingIds] count={n}");
                for (int i = 0; i < s.Length; i += 400)
                    LLMNPCsPlugin.LogToFile("[BuildingIds] " + s.Substring(i, Math.Min(400, s.Length - i)));
                // Also write to the mod folder (readable outside the flooded log).
                try
                {
                    System.IO.File.WriteAllText(
                        @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\building_ids.txt",
                        $"count={n}\n{s}");
                }
                catch { }
            }
            catch (Exception ex) { LLMNPCsPlugin.LogToFile("[BuildingIds] EXC: " + ex.Message); }
        }

        /// <summary>Get a building blueprint (BaseBuildingBlueprint) from the
        /// game's BaseBuildingRepository by id, listing available ids on miss.</summary>
        private static object GetBuildingBlueprint(string id)
        {
            try
            {
                var instance = RepoInstance("BaseBuildingRepository");
                if (instance == null) return null;
                var itype = instance.GetType();
                var getById = FindMethod(itype, "GetByID", 1);
                object bp = null;
                if (!string.IsNullOrEmpty(id) && getById != null)
                { try { bp = getById.Invoke(instance, new object[] { id }); } catch { } }
                if (bp != null) { LastBuildingDiag = "ok GetByID " + id; return bp; }
                // Enumerate the repository to (a) find a bed-ish blueprint and
                // (b) surface real ids for diagnosis.
                var ids = new System.Text.StringBuilder();
                object bedish = null;
                for (var ft = itype; ft != null; ft = ft.BaseType)
                    foreach (var f in ft.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        object val; try { val = f.GetValue(instance); } catch { continue; }
                        System.Collections.IEnumerable items = (val as System.Collections.IDictionary)?.Values ?? (val as System.Collections.IEnumerable);
                        if (items == null || val is string) continue;
                        foreach (var it in items)
                        {
                            if (it == null) continue;
                            string bid = null;
                            try { bid = it.GetType().GetMethod("GetID")?.Invoke(it, null) as string; } catch { }
                            if (bid == null) continue;
                            if (ids.Length < 400) ids.Append(bid + ",");
                            if (bedish == null && (bid.IndexOf("bed", StringComparison.OrdinalIgnoreCase) >= 0 || bid.IndexOf("sleep", StringComparison.OrdinalIgnoreCase) >= 0))
                                bedish = it;
                        }
                        if (ids.Length > 0) break;
                    }
                if (bedish != null && string.IsNullOrEmpty(id)) { LastBuildingDiag = "ok bedish"; return bedish; }
                LastBuildingDiag = $"GetByID('{id}')=null; avail=[{ids}]";
                return null;
            }
            catch (Exception ex) { LastBuildingDiag = "EXC: " + (ex.InnerException?.Message ?? ex.Message); return null; }
        }

        /// <summary>
        /// Place a real building BLUEPRINT the game's settlers then construct —
        /// AUTONOMOUSLY, with NO cursor hand-off. The old path called
        /// BuildingPlacementManager.SpawnBlueprint, which (decompiled) runs
        /// InitializeBuilding + MouseUpSpawnInitializeBuildings: that's the
        /// INTERACTIVE placement that attaches a preview to the player's mouse.
        /// Instead we replicate the game's OWN post-click commit chain directly
        /// at a chosen cell (ground truth: SpawnEnemyBuilding + CacheBuildingInstance):
        ///   SpawnFromPool -> CreateAndReturnBuildingInstance -> CacheBuildingInstance
        ///   (fires ConstructionController.BlueprintPlaced => a real construction
        ///   job settlers haul + build) -> ObjectPlacedOnMap.
        /// BuildingsManagerMain.CanPlace still gates the cell (rejects water /
        /// invalid terrain), and SpawnFromPool returning null is a second gate.
        /// </summary>
        public static string TryPlaceBuildingNear(GameObject settlerGo, string buildingId)
        {
            try
            {
                object blueprint = GetBuildingBlueprint(buildingId);
                if (blueprint == null) return "no building blueprint :: " + LastBuildingDiag;

                var vmType = FindType("NSMedieval.Village.VillageManager");
                var activeVillage = vmType?.GetProperty("ActiveVillage",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                var map = activeVillage?.GetType().GetProperty("Map")?.GetValue(activeVillage, null);
                if (map == null) return "no VillageMap";
                var bmm = map.GetType().GetProperty("BuildingsManagerMain")?.GetValue(map, null);
                if (bmm == null) return "no BuildingsManagerMain";
                var origin = settlerGo != null ? settlerGo.transform.position : Vector3.zero;
                var settlerNode = map.GetType().GetMethod("GetNodeByWorldPos")?.Invoke(map, new object[] { origin });
                if (settlerNode == null) return "settler node null";
                var anchor = settlerNode.GetType().GetProperty("Position")?.GetValue(settlerNode, null);
                int ax = ReadIntField(anchor, "x", "X"), ay = ReadIntField(anchor, "y", "Y"), az = ReadIntField(anchor, "z", "Z");
                // Anchor on the colony HOME waypoint (compact village) if set.
                if (HomeAnchor != null) { ax = HomeAnchor[0]; ay = HomeAnchor[1]; az = HomeAnchor[2]; }

                var canPlace = bmm.GetType().GetMethod("CanPlace",
                    new[] { blueprint.GetType(), _vec3IntType ?? (_vec3IntType = FindTypeByName("Vec3Int")), typeof(int), typeof(bool) });
                if (canPlace == null) return "CanPlace method not found";

                // Scan outward from the settler for the first cell CanPlace accepts
                // (dry land, valid stability/terrain — no water, no impossible tile).
                for (int radius = 1; radius <= 14; radius++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                        foreach (var dz in (Math.Abs(dx) == radius
                                 ? Enumerable.Range(-radius, radius * 2 + 1)
                                 : new[] { -radius, radius }.AsEnumerable()))
                        {
                            int cx = ax + dx, cz = az + dz;
                            var cell = MakeVec3Int(cx, ay, cz);
                            if (cell == null) return "Vec3Int null";
                            bool ok;
                            try { ok = (bool)canPlace.Invoke(bmm, new[] { blueprint, cell, (object)0, (object)true }); }
                            catch { ok = false; }
                            if (!ok) continue;
                            // Never site furniture ON/BESIDE a stockpile zone or
                            // loose piles — footprint-aware (live bug: research
                            // table on the pile). House pieces use exact-cell.
                            if (IsNearStockpileOrPiles(cx, ay, cz)) continue;
                            // ...and never on a cell holding ANY building/blueprint
                            // (live bug: table 'blocked by wooden door' — CanPlace
                            // tolerated the door cell). Ground truth: BuildingExists.
                            if (AnyBuildingAt(cx, ay, cz)) continue;

                            string commit = CommitPlayerBlueprint(map, bmm, blueprint, cell, 0);
                            if (commit != null) { LastBuildingDiag = commit; continue; } // this cell was blocked; try next

                            string bid = null;
                            try { bid = blueprint.GetType().GetMethod("GetID")?.Invoke(blueprint, null) as string; } catch { }
                            return $"ok: building blueprint '{bid}' committed at ({cx},{ay},{cz}) — NO cursor; settlers will construct it";
                        }
                }
                return $"no valid CanPlace cell within 14 of settler ({ax},{ay},{az}) for the building :: {LastBuildingDiag}";
            }
            catch (Exception ex) { return "building placement error: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        /// <summary>Place a specific building at an EXACT grid cell (for multi-cell
        /// structures like houses). Returns "ok:..." on success, else a diagnostic.
        /// CanPlace gates the cell; SpawnFromPool-null is a second gate.</summary>
        public static string TryPlaceBuildingAt(int cx, int cy, int cz, string buildingId, int angle = 0)
        {
            try
            {
                object blueprint = GetBuildingBlueprint(buildingId);
                if (blueprint == null) return "no bp :: " + LastBuildingDiag;
                var vmType = FindType("NSMedieval.Village.VillageManager");
                var activeVillage = vmType?.GetProperty("ActiveVillage",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                var map = activeVillage?.GetType().GetProperty("Map")?.GetValue(activeVillage, null);
                if (map == null) return "no map";
                var bmm = map.GetType().GetProperty("BuildingsManagerMain")?.GetValue(map, null);
                if (bmm == null) return "no bmm";
                var canPlace = bmm.GetType().GetMethod("CanPlace",
                    new[] { blueprint.GetType(), _vec3IntType ?? (_vec3IntType = FindTypeByName("Vec3Int")), typeof(int), typeof(bool) });
                if (canPlace == null) return "no CanPlace";
                var cell = MakeVec3Int(cx, cy, cz);
                if (cell == null) return "no cell";
                // Hard gate: never build in water / on slopes (building CanPlace lets
                // walls over water through; CanPlaceStockpile reliably rejects it).
                if (!IsDryBuildableGround(cx, cy, cz)) return $"not dry ground @({cx},{cy},{cz})";
                if (IsOnStockpile(cx, cy, cz)) return $"on stockpile zone @({cx},{cy},{cz})";
                bool ok;
                try { ok = (bool)canPlace.Invoke(bmm, new[] { blueprint, cell, (object)angle, (object)true }); }
                catch { ok = false; }
                if (!ok) return $"CanPlace false @({cx},{cy},{cz})";
                string commit = CommitPlayerBlueprint(map, bmm, blueprint, cell, angle);
                if (commit != null) return "blocked :: " + commit;
                return $"ok: '{buildingId}' @({cx},{cy},{cz})";
            }
            catch (Exception ex) { return "err: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        /// <summary>Place a ROOF at a grid cell. Roofs are NOT normal buildings —
        /// they're components placed via BuildingPlacementManager.SpawnRoofAutoTesting
        /// (the game's own non-interactive roof path: sets the roof blueprint +
        /// RoofComponentBlueprint, then CreateRoofs → DragSpawnRoof + CanPlaceRoof +
        /// RoofComponentManager.CreateAndCacheRoofComponentInstance). Ground truth:
        /// decompiled BuildingPlacementManager.SpawnRoofAutoTesting/CreateRoofs.</summary>
        public static string TryPlaceRoofAt(int cx, int cy, int cz, string roofId = "wood_roof_whole")
        {
            try
            {
                object blueprint = GetBuildingBlueprint(roofId);
                if (blueprint == null) return "no roof bp :: " + LastBuildingDiag;
                var bpmType = FindType("NSMedieval.BuildingComponents.BuildingPlacementManager");
                var bpm = bpmType?.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                if (bpm == null) return "no BuildingPlacementManager";
                var method = FindMethod(bpmType, "SpawnRoofAutoTesting", 5);
                if (method == null) return "no SpawnRoofAutoTesting";
                _vec3IntType = _vec3IntType ?? FindTypeByName("Vec3Int");
                var cell = MakeVec3Int(cx, cy, cz);
                var scale = MakeVec3Int(1, 1, 1);
                if (cell == null || scale == null) return "Vec3Int null";
                var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(_vec3IntType);
                var positions = Activator.CreateInstance(listType);
                listType.GetMethod("Add").Invoke(positions, new[] { cell });
                // MEASURED success (ground truth: CreateRoofs silently no-ops when
                // DragSpawnRoof's SpawnFromPool returns null or CanPlaceRoof rejects
                // — 'ok: invoked' was a LIE; roofs never landed, Ken saw rain on
                // beds). Count RoofComponentManager instances before/after.
                int before = CountRoofComponents();
                try { method.Invoke(bpm, new[] { blueprint, cell, (object)0, scale, positions }); }
                catch (Exception se) { return "SpawnRoofAutoTesting exc: " + (se.InnerException?.Message ?? se.Message); }
                int after = CountRoofComponents();
                if (after > before)
                    return $"ok: roof PLACED @({cx},{cy},{cz}) (components {before}->{after})";
                return $"roof REJECTED @({cx},{cy},{cz}) (components {before}->{after}: SpawnFromPool-null or CanPlaceRoof false)";
            }
            catch (Exception ex) { return "roof err: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        /// <summary>Count cached roof component instances — the MEASURED truth of
        /// roof placement (Map.RoofComponentManager's instance collection).</summary>
        private static int CountRoofComponents()
        {
            try
            {
                var vmType = FindType("NSMedieval.Village.VillageManager");
                var activeVillage = vmType?.GetProperty("ActiveVillage",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                var map = activeVillage?.GetType().GetProperty("Map")?.GetValue(activeVillage, null);
                var rcm = map?.GetType().GetProperty("RoofComponentManager")?.GetValue(map, null);
                if (rcm == null) return -1;
                int best = 0;
                for (var t = rcm.GetType(); t != null; t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        object val; try { val = f.GetValue(rcm); } catch { continue; }
                        if (val is string || !(val is IEnumerable en)) continue;
                        int c = 0; bool roofish = false;
                        foreach (var it in en) { c++; if (it != null && it.GetType().Name.Contains("Roof")) roofish = true; if (c > 4096) break; }
                        if (roofish && c > best) best = c;
                    }
                return best;
            }
            catch { return -1; }
        }

        /// <summary>The settler's current grid node (x,y,z level) or null.</summary>
        public static int[] SettlerNode(GameObject settlerGo)
        {
            try
            {
                var vmType = FindType("NSMedieval.Village.VillageManager");
                var activeVillage = vmType?.GetProperty("ActiveVillage",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                var map = activeVillage?.GetType().GetProperty("Map")?.GetValue(activeVillage, null);
                if (map == null || settlerGo == null) return null;
                var node = map.GetType().GetMethod("GetNodeByWorldPos")?.Invoke(map, new object[] { settlerGo.transform.position });
                var pos = node?.GetType().GetProperty("Position")?.GetValue(node, null);
                if (pos == null) return null;
                return new[] { ReadIntField(pos, "x", "X"), ReadIntField(pos, "y", "Y"), ReadIntField(pos, "z", "Z") };
            }
            catch { return null; }
        }

        /// <summary>True if CanPlace accepts a wall at this exact cell — used by the
        /// house planner to find clear, buildable ground.</summary>
        public static bool CanPlaceWallAt(int cx, int cy, int cz, string wallId = "wood_wall_element")
        {
            try
            {
                object blueprint = GetBuildingBlueprint(wallId);
                if (blueprint == null) return false;
                var vmType = FindType("NSMedieval.Village.VillageManager");
                var activeVillage = vmType?.GetProperty("ActiveVillage",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                var map = activeVillage?.GetType().GetProperty("Map")?.GetValue(activeVillage, null);
                var bmm = map?.GetType().GetProperty("BuildingsManagerMain")?.GetValue(map, null);
                if (bmm == null) return false;
                var canPlace = bmm.GetType().GetMethod("CanPlace",
                    new[] { blueprint.GetType(), _vec3IntType ?? (_vec3IntType = FindTypeByName("Vec3Int")), typeof(int), typeof(bool) });
                var cell = MakeVec3Int(cx, cy, cz);
                if (canPlace == null || cell == null) return false;
                // Only site the house on DRY, FLAT ground (rejects water/slopes).
                if (!IsDryBuildableGround(cx, cy, cz)) return false;
                try { return (bool)canPlace.Invoke(bmm, new[] { blueprint, cell, (object)0, (object)true }); }
                catch { return false; }
            }
            catch { return false; }
        }

        private static Type _factionOwnershipType;

        /// <summary>
        /// Directly commit a player construction blueprint at a grid cell with NO
        /// cursor interaction, mirroring the game's own internal chain. Returns
        /// null on success, or a diagnostic string on failure (so the caller can
        /// try another cell).
        /// </summary>
        private static string CommitPlayerBlueprint(object map, object bmm, object blueprint, object cell, int angle)
        {
            try
            {
                var bpmType = FindType("NSMedieval.BuildingComponents.BuildingPlacementManager");
                var bpm = bpmType?.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                if (bpm == null) return "BuildingPlacementManager.Instance null";

                _factionOwnershipType = _factionOwnershipType ?? FindTypeByName("FactionOwnership");
                object player = _factionOwnershipType != null ? Enum.ToObject(_factionOwnershipType, 0) : (object)0; // Player

                // SpawnFromPool(blueprint, Vec3Int gridPosition, int angleY, FactionOwnership)
                MethodInfo spawnFromPool = null;
                for (var t = bpmType; t != null && spawnFromPool == null; t = t.BaseType)
                    foreach (var m in t.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        if (m.Name == "SpawnFromPool")
                        {
                            var ps = m.GetParameters();
                            if (ps.Length == 4 && ps[1].ParameterType == (_vec3IntType ?? (_vec3IntType = FindTypeByName("Vec3Int"))))
                            { spawnFromPool = m; break; }
                        }
                if (spawnFromPool == null) return "SpawnFromPool(bp,Vec3Int,int,Faction) not found";

                object view;
                try { view = spawnFromPool.Invoke(bpm, new[] { blueprint, cell, (object)angle, player }); }
                catch (Exception se) { return "SpawnFromPool exc: " + (se.InnerException?.Message ?? se.Message); }
                if (view == null) return "SpawnFromPool returned null (cell blocked)";

                // worldPos = GridUtils.GetWorldPosition(cell)
                var gridUtils = FindTypeByName("GridUtils");
                var getWorldPos = gridUtils?.GetMethod("GetWorldPosition", new[] { _vec3IntType });
                object worldPos = getWorldPos != null ? getWorldPos.Invoke(null, new[] { cell }) : (object)Vector3.zero;

                // CreateAndReturnBuildingInstance(bp, view, worldPos, angleY, Player) — binds instance to view
                var createRet = FindMethod(bmm.GetType(), "CreateAndReturnBuildingInstance", 5);
                if (createRet == null) return "CreateAndReturnBuildingInstance(5) not found";
                try { createRet.Invoke(bmm, new[] { blueprint, view, worldPos, (object)angle, player }); }
                catch (Exception ce) { return "CreateAndReturnBuildingInstance exc: " + (ce.InnerException?.Message ?? ce.Message); }

                // CacheBuildingInstance(view, false) — registers + fires
                // ConstructionController.BlueprintPlaced (the construction JOB) +
                // stability + forbidden areas, because a fresh instance is in
                // ConstructionPhase.Blueprint.
                var cache = FindMethod(bmm.GetType(), "CacheBuildingInstance", 2);
                if (cache == null) return "CacheBuildingInstance(2) not found";
                try { cache.Invoke(bmm, new[] { view, (object)false }); }
                catch (Exception cae) { return "CacheBuildingInstance exc: " + (cae.InnerException?.Message ?? cae.Message); }

                // ObjectPlacedOnMap(view) — finalizes physical placement.
                var objPlaced = FindMethod(bpmType, "ObjectPlacedOnMap", 1);
                if (objPlaced != null)
                {
                    try { objPlaced.Invoke(bpm, new[] { view }); }
                    catch (Exception oe) { LastBuildingDiag = "ObjectPlacedOnMap exc (cached ok): " + (oe.InnerException?.Message ?? oe.Message); }
                }
                return null; // success
            }
            catch (Exception ex) { return "commit error: " + (ex.InnerException?.Message ?? ex.Message); }
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
                // Anchor on the colony HOME waypoint (compact village) if set.
                if (HomeAnchor != null) { ax = HomeAnchor[0]; ay = HomeAnchor[1]; az = HomeAnchor[2]; }

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

        // ─── Census helpers for the Strategic Model (ColonyBuilder) ───────────
        // Reuse the exact live-manager / map machinery the verified placers use so
        // the census reads the same game truth we place against.

        private static object GetLiveStockpileManager()
        {
            _managerType = _managerType ?? FindType("NSMedieval.Stockpiles.StockpileManager");
            if (_managerType == null) return null;
            object fallback = null;
            foreach (var c in UnityEngine.Object.FindObjectsOfType(_managerType))
            {
                if (SelfVerifies(c)) return c;
                fallback = fallback ?? c;
            }
            return fallback ?? ManagerInstance();
        }

        private static MethodInfo _canPlaceStock;
        /// <summary>True only on DRY, FLAT, BUILDABLE ground. Uses the game's own
        /// StockpileManager.CanPlaceStockpile (strict) which — unlike building
        /// CanPlace — reliably REJECTS water and slopes. Used to gate house/building
        /// placement so villagers never build in the pond. Returns true if the
        /// check can't run (don't block when the manager isn't ready).</summary>
        public static bool IsDryBuildableGround(int x, int ay, int z)
        {
            try
            {
                var mgr = GetLiveStockpileManager();
                if (mgr == null) return true;
                _canPlaceStock = _canPlaceStock ?? mgr.GetType().GetMethod("CanPlaceStockpile");
                var cell = MakeVec3Int(x, ay, z);
                if (_canPlaceStock == null || cell == null) return true;
                return (bool)_canPlaceStock.Invoke(mgr, new[] { cell, (object)false });
            }
            catch { return true; }
        }

        /// <summary>Number of stockpile zones currently in the world. Returns -1
        /// when the manager isn't available yet (save still loading). Used by the
        /// Strategic Model to decide whether the colony still needs storage.</summary>
        public static int CountStockpilesInWorld()
        {
            var mgr = GetLiveStockpileManager();
            if (mgr == null) return -1;
            return CountStockpiles(mgr);
        }

        private static object GetBuildingsManager()
        {
            var vmType = FindType("NSMedieval.Village.VillageManager");
            var activeVillage = vmType?.GetProperty("ActiveVillage",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
            var map = activeVillage?.GetType().GetProperty("Map")?.GetValue(activeVillage, null);
            return map?.GetType().GetProperty("BuildingsManagerMain")?.GetValue(map, null);
        }

        /// <summary>Count of placed buildings with this id (blueprints under
        /// construction + completed), via BuildingsManagerMain.GetBuildingsCount.
        /// Returns -1 when the map isn't ready. Counts by the SAME id string the
        /// placer targets, so it self-limits bed placement without double-counting.</summary>
        public static int CountBuildings(string id)
        {
            try
            {
                var bmm = GetBuildingsManager();
                if (bmm == null) return -1;
                var m = bmm.GetType().GetMethod("GetBuildingsCount", new[] { typeof(string) });
                if (m == null) return -1;
                return (int)m.Invoke(bmm, new object[] { id });
            }
            catch { return -1; }
        }

        /// <summary>True if a building with this blueprint id (blueprint under
        /// construction OR finished) occupies this exact cell. World-truth
        /// idempotency check — lets the builders SKIP pieces that already exist
        /// in the loaded save instead of re-placing them (the save-bloat bug).
        /// Returns false when the world isn't ready (callers then place normally,
        /// gated by CanPlace as before).</summary>
        public static bool BuildingExistsAt(int x, int y, int z, string id)
        {
            try
            {
                var bmm = GetBuildingsManager();
                if (bmm == null) return false;
                var cell = MakeVec3Int(x, y, z);
                if (cell == null) return false;
                // GetBuildings(Vec3Int) -> List<BaseBuildingInstance> (decompiled
                // BuildingsManagerMain:1176); each instance's Blueprint.GetID() is
                // the same id string the placers target.
                var m = bmm.GetType().GetMethod("GetBuildings", new[] { _vec3IntType ?? (_vec3IntType = FindTypeByName("Vec3Int")) });
                var list = m?.Invoke(bmm, new[] { cell }) as IEnumerable;
                if (list == null) return false;
                foreach (var inst in list)
                {
                    if (inst == null) continue;
                    var bp = inst.GetType().GetProperty("Blueprint")?.GetValue(inst, null);
                    string bid = null;
                    try { bid = bp?.GetType().GetMethod("GetID")?.Invoke(bp, null) as string; } catch { }
                    if (bid == id) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>True if a stockpile ZONE occupies EXACTLY this cell.
        /// EXACT-cell semantics only (Ken visual-confirmed the bug: the old 3x3
        /// neighborhood version false-positived on house walls merely ADJACENT
        /// to a zone — red-circle-blocked walls beside a stockpile that was
        /// clearly outside the building).</summary>
        public static bool IsOnStockpile(int x, int y, int z)
        {
            try
            {
                var mgr = GetLiveStockpileManager();
                var exists = mgr?.GetType().GetMethod("StockpileExists");
                var cell = MakeVec3Int(x, y, z);
                if (exists == null || cell == null) return false;
                return exists.Invoke(mgr, new[] { cell }) is bool b && b;
            }
            catch { return false; }
        }

        /// <summary>Footprint-aware variant for SITE SEARCH of multi-cell
        /// furniture (research table on the pile, live bug ×2): zone in the
        /// 3x3 neighborhood OR loose ground piles within 1 cell.</summary>
        public static bool IsNearStockpileOrPiles(int x, int y, int z)
        {
            try
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (IsOnStockpile(x + dx, y, z + dz)) return true;
                return ResourceUnforbidder.AnyPileAt(x, y, z, 1);
            }
            catch { return false; }
        }

        /// <summary>True if ANY building/blueprint occupies this cell — conflict
        /// guard so two different pieces (wall + door) can never stack on one
        /// cell (live bug: overlapping blueprints during reload churn).</summary>
        public static bool AnyBuildingAt(int x, int y, int z)
        {
            try
            {
                var bmm = GetBuildingsManager();
                var cell = MakeVec3Int(x, y, z);
                if (bmm == null || cell == null) return false;
                var m = bmm.GetType().GetMethod("BuildingExists", new[] { _vec3IntType ?? (_vec3IntType = FindTypeByName("Vec3Int")) });
                return m != null && m.Invoke(bmm, new[] { cell }) is bool b && b;
            }
            catch { return false; }
        }

        /// <summary>Stockpiles that VERIFY against their own manager</summary> — i.e. the
        /// manager confirms StockpileExists at the instance's Start cell. The raw
        /// Stockpiles count proved unreliable (a pooled/dead instance produced a
        /// false 'stockpiles=1', which is why placement was session-flag-gated —
        /// the reload-bloat bug). Verified instances are real, so the census can
        /// safely gate placement again. Returns -1 when the manager isn't ready.</summary>
        public static int CountVerifiedStockpiles()
        {
            try
            {
                var mgr = GetLiveStockpileManager();
                if (mgr == null) return -1;
                var sp = mgr.GetType().GetProperty("Stockpiles")?.GetValue(mgr, null) as IEnumerable;
                var exists = mgr.GetType().GetMethod("StockpileExists");
                if (sp == null || exists == null) return -1;
                int c = 0, mbh = GetMapBlockHeight();
                foreach (var inst in sp)
                {
                    var start = inst?.GetType().GetProperty("Start")?.GetValue(inst, null);
                    if (start == null) continue;
                    int sx = ReadIntField(start, "x", "X"), sy = ReadIntField(start, "y", "Y"), sz = ReadIntField(start, "z", "Z");
                    // GOTCHA (live-verified): SpawnStockpile takes start.y in WORLD
                    // units (level * MapBlockHeight) and the instance stores it that
                    // way, but StockpileExists expects the LEVEL. Verifying at the
                    // stored Start therefore always failed ('stockpiles=0' with two
                    // real zones in the world — and the historic phantom-census
                    // confusion). Check the stored y AND the level-converted y.
                    bool ok = false;
                    try { ok = exists.Invoke(mgr, new[] { start }) is bool b1 && b1; } catch { }
                    if (!ok && mbh > 0 && sy % mbh == 0)
                    {
                        var lvl = MakeVec3Int(sx, sy / mbh, sz);
                        try { ok = lvl != null && exists.Invoke(mgr, new[] { lvl }) is bool b2 && b2; } catch { }
                    }
                    if (ok) c++;
                }
                return c;
            }
            catch { return -1; }
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
