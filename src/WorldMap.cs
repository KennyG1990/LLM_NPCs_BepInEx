using System;
using System.Collections;
using System.Reflection;
using System.Text;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// WORLDMAP — full 3D spatial awareness (Ken: "the settlers need to know the
    /// ENTIRE map — they build up (towers), down (cellars), and want to start new
    /// villages and war each other"). This is the ENGINE layer: it consumes the
    /// whole map deterministically so the LLM never has to. The LLM says "find me
    /// somewhere else to build"; this reads every voxel and hands back a compact,
    /// scored answer ("grid @x,z looks promising").
    ///
    /// Ground truth (decompiled):
    ///   GlobalSaveController.CurrentVillageData.PlayerVillage.Map  -> VillageMap
    ///   VillageMap.Size : Vec3Int   (x,y,z bounds — y is the vertical/level axis)
    ///   VillageMap.GridSpaceData : MapNode[]   (EVERY node, flat array)
    ///   MapNode: Position(Vec3Int), IsWalkable, IsWater, IsGrass,
    ///            VoxelTypeIdByte(0=air), DataType(GridDataType), HasShadowCasterPlants
    ///
    /// Unit 1 = read the whole map into a compact per-column (x,z) 2.5D model
    /// across all Z-levels + a downsampled overview for validation. The site-scorer
    /// (Unit 2) consumes Surface/Cls/TowerAbove/CellarBelow; it is NOT built here.
    ///
    /// Classification (Cls byte): 0 water · 1 open-flat-buildable · 2 rough/slope ·
    ///   3 tree/forest · 4 built (building/roof/furniture) · 5 rock/unbuildable ·
    ///   9 unknown/air-column. Read-only, null-safe (missing map => no-op).
    /// </summary>
    public static class WorldMap
    {
        public const byte CLS_WATER = 0, CLS_OPEN = 1, CLS_ROUGH = 2, CLS_TREE = 3, CLS_BUILT = 4, CLS_ROCK = 5, CLS_NONE = 9;
        private const char AIR = ' ';

        public static int SizeX, SizeY, SizeZ;
        public static int[,] Surface;      // highest walkable level per column, -1 = none
        public static byte[,] Cls;         // classification per column (see consts)
        public static byte[,] TowerAbove;  // built voxels above surface (verticality up)
        public static byte[,] CellarBelow; // diggable/air voxels below surface (verticality down)
        public static string LastSummary = "(never scanned)";
        public static long LastScanTicks = 0;
        public static int MinSurface = 0, MaxSurface = 0;   // for high-ground normalization

        private static Type _gscT;

        private static Type FindTypeByName(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }

        private static object HGet(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null) { try { var v = p.GetValue(o, null); if (v != null) return v; } catch { } }
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) { try { var v = f.GetValue(o); if (v != null) return v; } catch { } }
            }
            return null;
        }

        // static property/field on a type (e.g. GlobalSaveController.CurrentVillageData)
        private static object SGet(Type t, string name)
        {
            for (var x = t; x != null; x = x.BaseType)
            {
                var p = x.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) { try { var v = p.GetValue(null, null); if (v != null) return v; } catch { } }
                var f = x.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (f != null) { try { var v = f.GetValue(null); if (v != null) return v; } catch { } }
            }
            return null;
        }

        private static int VInt(object vec, string lower, string upper)
        {
            try { var t = vec.GetType(); var f = t.GetField(lower) ?? t.GetField(upper);
                  return f != null ? Convert.ToInt32(f.GetValue(vec)) : 0; }
            catch { return 0; }
        }

        private static object GetVillageMap()
        {
            _gscT = _gscT ?? FindTypeByName("GlobalSaveController");
            if (_gscT == null) return null;
            var vdata = SGet(_gscT, "CurrentVillageData");
            if (vdata == null) return null;
            // PlayerVillage.Map (preferred) or CurrentVillageData.Map (fallback)
            var pv = HGet(vdata, "PlayerVillage");
            return HGet(pv, "Map") ?? HGet(vdata, "Map");
        }

        // GridDataType is a BIT-FLAG enum but (per decompiled) NOT marked [Flags], so
        // ToString() of a combined value (BuildingFinished|ForbiddenByBuilding|...) is a
        // raw NUMBER, not names — string matching fails. Use the game's own bitwise test
        // ((node.DataType & BuildingFinished) != 0, ground truth GlobalWarnings:2061).
        private static long _builtMask = -1;
        private static long BuiltMask()
        {
            if (_builtMask != -1) return _builtMask;
            long mask = 0;
            var t = FindTypeByName("GridDataType");
            if (t != null && t.IsEnum)
                foreach (var name in new[] {
                    "BuildingFinished","BuildingUnfinished","BuildingBlueprint","Furniture","FurnitureGate",
                    "ProductionBuilding","Roof","RugFinished","RugBlueprint","RugFoundation",
                    "BeamFinished","BeamUnfinished","BeamBlueprint","Drawbridge","Grave","Cropfield",
                    "Stairs","SlopeOrStairs","Trap","SocketableItem" })
                {
                    try { mask |= Convert.ToInt64(Enum.Parse(t, name)); } catch { }
                }
            _builtMask = mask;   // 0 if resolution failed (then nothing counts as built)
            return _builtMask;
        }

        private static bool IsBuiltData(object dataType)
        {
            if (dataType == null) return false;
            try { return (Convert.ToInt64(dataType) & BuiltMask()) != 0; }
            catch { return false; }
        }

        /// <summary>Read the entire map into the per-column model. Returns a summary.
        /// Bounded/defensive: any missing API => LastSummary explains + returns.</summary>
        // REVERTED TO MAIN THREAD, SLICED (2026-07-12 ~23:20): the background
        // thread "cure" was the poison — three native crashes and a 1000x
        // map-query slowdown (2.5s/query) line up exactly with the bg-scan
        // builds. Enumerating live game collections off-thread while the main
        // thread mutates them is the classic Unity/Mono native-crash recipe.
        // Lesson (banked): NO game-state reads off the main thread, ever —
        // "pure C# model data" is still the game's mutating state.
        // The slice keeps the freeze fix honestly: 150ms per tick with a kept
        // enumerator; the full pass completes across ~a minute of ticks.
        public static string Scan()
        {
            if (LastScanTicks != 0) return LastSummary;
            return ScanHomeRegionSlice();   // fast bounded scan (the full GridSpaceData enum was the tick-killer)
        }

        // ── FAST HOME-REGION SCAN (2026-07-13, autonomous perf fix) ──────────────
        // Enumerating the full 206x206x16 GridSpaceData (1.36M nodes, 2 passes) via
        // its LAZY enumerator was the tick-killer: a single MoveNext could take 2.8s
        // (freeze log: [mod:worldmap-scan] 2797ms), blowing the 150ms budget and
        // dragging ColonyBuilder ticks to ~2 MINUTES so the colony never got to
        // build. The colony only sites buildings NEAR HOME, so scan a bounded box via
        // direct GetNode lookups (cached reflection getters), budgeted + resumable.
        private const int RegionRadius = 45;
        private static int _regX0, _regX1, _regZ0, _regZ1, _regCurX, _regCurZ;
        private static bool _regionStarted;
        private static object _regMap; private static MethodInfo _regGetNode;
        private static readonly object[] _regCellArg = new object[1];
        private static PropertyInfo _pWalk, _pWater, _pDataType, _pVox, _pGrass, _pTree;
        private static System.Reflection.ConstructorInfo _vecCtor;
        private static Type _vec3IntType;

        private static object MakeCell(int x, int y, int z)
        {
            _vec3IntType = _vec3IntType ?? FindTypeByName("Vec3Int");
            if (_vecCtor == null && _vec3IntType != null)
                _vecCtor = _vec3IntType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
            try { return _vecCtor?.Invoke(new object[] { x, y, z }); } catch { return null; }
        }

        private static void CacheNodeGetters(Type nt)
        {
            PropertyInfo P(string n) => nt.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
            _pWalk = P("IsWalkable"); _pWater = P("IsWater"); _pDataType = P("DataType");
            _pVox = P("VoxelTypeIdByte"); _pGrass = P("IsGrass"); _pTree = P("HasShadowCasterPlants");
        }

        private static void ScanColumn(int x, int z)
        {
            int surface = -1;
            for (int y = SizeY - 1; y >= 0; y--)
            {
                object node; try { node = _regGetNode.Invoke(_regMap, new object[] { x, y, z }); } catch { continue; }
                if (node == null) continue;
                if (_pWalk == null) CacheNodeGetters(node.GetType());
                bool walk = false; try { walk = Convert.ToBoolean(_pWalk?.GetValue(node) ?? false); } catch { }
                if (surface < 0 && walk)
                {
                    surface = y; Surface[x, z] = y;
                    bool water = false, grass = false, tree = false, built = false; byte vox = 0;
                    try { water = Convert.ToBoolean(_pWater?.GetValue(node) ?? false); } catch { }
                    try { grass = Convert.ToBoolean(_pGrass?.GetValue(node) ?? false); } catch { }
                    try { tree = Convert.ToBoolean(_pTree?.GetValue(node) ?? false); } catch { }
                    try { built = IsBuiltData(_pDataType?.GetValue(node)); } catch { }
                    try { vox = Convert.ToByte(_pVox?.GetValue(node) ?? (byte)0); } catch { }
                    Cls[x, z] = water ? CLS_WATER : built ? CLS_BUILT : tree ? CLS_TREE
                              : (grass || vox == 0) ? CLS_OPEN : CLS_ROCK;
                }
                else if (surface >= 0 && y < surface)
                {
                    byte vb = 0; try { vb = Convert.ToByte(_pVox?.GetValue(node) ?? (byte)0); } catch { }
                    if (vb != 0 && CellarBelow[x, z] < 255) CellarBelow[x, z]++;
                }
            }
        }

        private static string ScanHomeRegionSlice()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!_regionStarted)
                {
                    var map = GetVillageMap();
                    if (map == null) return LastSummary = "worldmap: no VillageMap (save loading?)";
                    var size = HGet(map, "Size");
                    if (size == null) return LastSummary = "worldmap: no Size";
                    SizeX = VInt(size, "x", "X"); SizeY = VInt(size, "y", "Y"); SizeZ = VInt(size, "z", "Z");
                    if (SizeX <= 0 || SizeZ <= 0) return LastSummary = "worldmap: bad size";
                    Surface = new int[SizeX, SizeZ]; Cls = new byte[SizeX, SizeZ];
                    TowerAbove = new byte[SizeX, SizeZ]; CellarBelow = new byte[SizeX, SizeZ];
                    for (int ix = 0; ix < SizeX; ix++)
                        for (int iz = 0; iz < SizeZ; iz++) { Surface[ix, iz] = -1; Cls[ix, iz] = CLS_NONE; }
                    int hx = SizeX / 2, hz = SizeZ / 2;
                    if (BuiltState.TryGetHome(out int bx, out int _, out int bz)) { hx = bx; hz = bz; }
                    _regX0 = Math.Max(0, hx - RegionRadius); _regX1 = Math.Min(SizeX - 1, hx + RegionRadius);
                    _regZ0 = Math.Max(0, hz - RegionRadius); _regZ1 = Math.Min(SizeZ - 1, hz + RegionRadius);
                    _regCurX = _regX0; _regCurZ = _regZ0;
                    _regMap = map;
                    // Use the clean GetNode(int,int,int) overload — the GetNode(in Vec3Int)
                    // one is by-ref (Vec3Int&) so a by-value type lookup returns null
                    // ("no GetNode method" bug). 3 ints = no Vec3Int construction needed.
                    _regGetNode = map.GetType().GetMethod("GetNode", new[] { typeof(int), typeof(int), typeof(int) });
                    _pWalk = _pWater = _pDataType = _pVox = _pGrass = _pTree = null;
                    _sliceNodes = 0;
                    _regionStarted = true;
                    LLMNPCsPlugin.LogToFile($"[WorldMap] FAST home-region scan begin ({_regX0}..{_regX1} x {_regZ0}..{_regZ1}) around home");
                }
                if (_regGetNode == null) { _regionStarted = false; return LastSummary = "worldmap: no GetNode method"; }
                while (sw.ElapsedMilliseconds < 120)
                {
                    ScanColumn(_regCurX, _regCurZ);
                    _sliceNodes++;
                    _regCurZ++;
                    if (_regCurZ > _regZ1) { _regCurZ = _regZ0; _regCurX++; }
                    if (_regCurX > _regX1)
                    {
                        var summary = FinishScan(_sliceNodes);   // sets LastScanTicks + exports grid
                        _regionStarted = false;
                        return summary;
                    }
                }
                return LastSummary = $"worldmap: FAST region scan col x={_regCurX}/{_regX1} ({_sliceNodes} cols done, resumes next tick)";
            }
            catch (Exception ex) { _regionStarted = false; return LastSummary = "worldmap region EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        /// <summary>SNAPSHOT PRE-FILTER (the freeze-class endgame, 2026-07-12:
        /// with water-sim contention pricing each live map query in SECONDS,
        /// budgeted spirals advanced ~one origin per tick and the shack
        /// fallback was unreachable). Pure array reads from the last completed
        /// background scan: a FALSE is definitive enough to skip the cell; a
        /// TRUE still gets live CanPlace/CellIsDry verification. Falls open
        /// (true) until the first scan completes.</summary>
        public static bool SnapshotBuildableDry(int x, int y, int z)
        {
            if (LastScanTicks == 0) return true;          // no snapshot yet — let live checks decide
            var cls = Cls; var surf = Surface;
            if (cls == null || surf == null) return true;
            if (x < 0 || z < 0 || x >= SizeX || z >= SizeZ) return false;
            byte c = cls[x, z];
            if (c == CLS_WATER || c == CLS_BUILT || c == CLS_TREE) return false;
            return surf[x, z] == y;                       // standable at exactly this level
        }

        // Sliced-scan state (main thread only; enumerator survives across ticks)
        private static IEnumerator _sliceEnum;
        private static IEnumerable _sliceGrid;
        private static int _slicePass;      // 0 = not started, 1, 2
        private static long _sliceNodes;

        private static void ResetSlice()
        { _sliceEnum = null; _sliceGrid = null; _slicePass = 0; _sliceNodes = 0; }

        /// <summary>Clear scan progress on world (re)load so the region scan
        /// re-inits against the NEW map (called from BuiltState.ResetSession).</summary>
        public static void ResetScanState()
        {
            LastScanTicks = 0; _regionStarted = false; _sliceNodes = 0;
            _regMap = null; _regGetNode = null;
            _pWalk = _pWater = _pDataType = _pVox = _pGrass = _pTree = null;
        }

        private static string ScanSlice()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (_slicePass == 0)
                {
                    var map = GetVillageMap();
                    if (map == null) return LastSummary = "worldmap: no VillageMap (save loading?)";
                    var size = HGet(map, "Size");
                    if (size == null) return LastSummary = "worldmap: no Size";
                    SizeX = VInt(size, "x", "X"); SizeY = VInt(size, "y", "Y"); SizeZ = VInt(size, "z", "Z");
                    _sliceGrid = HGet(map, "GridSpaceData") as IEnumerable;
                    if (_sliceGrid == null || SizeX <= 0 || SizeZ <= 0)
                        return LastSummary = $"worldmap: no GridSpaceData (size {SizeX}x{SizeY}x{SizeZ})";
                    Surface = new int[SizeX, SizeZ];
                    Cls = new byte[SizeX, SizeZ];
                    TowerAbove = new byte[SizeX, SizeZ];
                    CellarBelow = new byte[SizeX, SizeZ];
                    for (int ix = 0; ix < SizeX; ix++)
                        for (int iz = 0; iz < SizeZ; iz++) { Surface[ix, iz] = -1; Cls[ix, iz] = CLS_NONE; }
                    _sliceEnum = _sliceGrid.GetEnumerator();
                    _slicePass = 1;
                    _sliceNodes = 0;
                }
                while (sw.ElapsedMilliseconds < 150)
                {
                    if (!_sliceEnum.MoveNext())
                    {
                        if (_slicePass == 1)
                        {
                            _sliceEnum = _sliceGrid.GetEnumerator();
                            _slicePass = 2;
                            continue;
                        }
                        var summary = FinishScan(_sliceNodes);
                        ResetSlice();
                        return summary;
                    }
                    var node = _sliceEnum.Current;
                    if (node == null) continue;
                    if (_slicePass == 1) { _sliceNodes++; Pass1Node(node); }
                    else Pass2Node(node);
                }
                return LastSummary = $"worldmap: scanning… pass {_slicePass}/2, {_sliceNodes} nodes (slice resumes next tick)";
            }
            catch (Exception ex)
            {
                ResetSlice();
                return LastSummary = "worldmap EXC: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        private static void Pass1Node(object node)
        {
            var pos = HGet(node, "Position");
            if (pos == null) return;
            int x = VInt(pos, "x", "X"), y = VInt(pos, "y", "Y"), z = VInt(pos, "z", "Z");
            if (x < 0 || x >= SizeX || z < 0 || z >= SizeZ) return;
            bool walk = false; try { walk = Convert.ToBoolean(HGet(node, "IsWalkable") ?? false); } catch { }
            bool water = false; try { water = Convert.ToBoolean(HGet(node, "IsWater") ?? false); } catch { }
            var dt = HGet(node, "DataType");
            bool built = IsBuiltData(dt);
            byte voxByte = 0; try { voxByte = Convert.ToByte(HGet(node, "VoxelTypeIdByte") ?? (byte)0); } catch { }
            bool solid = voxByte != 0;
            if (walk && y > Surface[x, z])
            {
                Surface[x, z] = y;
                bool grass = false; try { grass = Convert.ToBoolean(HGet(node, "IsGrass") ?? false); } catch { }
                bool tree = false; try { tree = Convert.ToBoolean(HGet(node, "HasShadowCasterPlants") ?? false); } catch { }
                byte c;
                if (water) c = CLS_WATER;
                else if (built) c = CLS_BUILT;
                else if (tree) c = CLS_TREE;
                else if (grass || !solid) c = CLS_OPEN;
                else c = CLS_ROCK;
                Cls[x, z] = c;
            }
        }

        private static void Pass2Node(object node)
        {
            var pos = HGet(node, "Position");
            if (pos == null) return;
            int x = VInt(pos, "x", "X"), y = VInt(pos, "y", "Y"), z = VInt(pos, "z", "Z");
            if (x < 0 || x >= SizeX || z < 0 || z >= SizeZ) return;
            int surf = Surface[x, z];
            if (surf < 0) return;
            if (y >= surf && IsBuiltData(HGet(node, "DataType")))
            {
                if (y > surf && TowerAbove[x, z] < 255) TowerAbove[x, z]++;
                if (Cls[x, z] != CLS_WATER) Cls[x, z] = CLS_BUILT;
            }
            byte vb = 0; try { vb = Convert.ToByte(HGet(node, "VoxelTypeIdByte") ?? (byte)0); } catch { }
            if (y < surf && vb != 0 && CellarBelow[x, z] < 255) CellarBelow[x, z]++;
        }

        private static string FinishScan(long nodes)
        {
            {
                // Summary + histogram + downsampled overview.
                long water2 = 0, open = 0, tree2 = 0, built2 = 0, rock = 0, none = 0, towers = 0, cellars = 0;
                int minY = int.MaxValue, maxY = int.MinValue;
                for (int ix = 0; ix < SizeX; ix++)
                    for (int iz = 0; iz < SizeZ; iz++)
                    {
                        switch (Cls[ix, iz]) { case CLS_WATER: water2++; break; case CLS_OPEN: open++; break;
                            case CLS_TREE: tree2++; break; case CLS_BUILT: built2++; break; case CLS_ROCK: rock++; break; default: none++; break; }
                        if (TowerAbove[ix, iz] > 0) towers++;
                        if (CellarBelow[ix, iz] > 2) cellars++;
                        int s = Surface[ix, iz];
                        if (s >= 0) { if (s < minY) minY = s; if (s > maxY) maxY = s; }
                    }
                long cols = (long)SizeX * SizeZ;
                LastSummary = $"worldmap: {SizeX}x{SizeZ} x {SizeY} levels, {nodes} nodes | surface levels {(minY == int.MaxValue ? 0 : minY)}..{(maxY == int.MinValue ? 0 : maxY)} | " +
                    $"open {Pct(open, cols)} water {Pct(water2, cols)} forest {Pct(tree2, cols)} built {built2} rock {Pct(rock, cols)} | " +
                    $"tower-cols {towers} cellar-capable {cellars}";

                MinSurface = (minY == int.MaxValue ? 0 : minY);
                MaxSurface = (maxY == int.MinValue ? 0 : maxY);
                DumpOverview(minY, maxY);
                DumpGrid();   // VillageForge --from-game input (Chronicle Test Gate 1)
                LastScanTicks = DateTime.UtcNow.Ticks;
                LLMNPCsPlugin.LogToFile("[WorldMap] sliced scan complete: " + LastSummary);
                return LastSummary;
            }
        }

        private static string Pct(long n, long total) => total > 0 ? $"{100 * n / total}%" : "0%";

        /// <summary>FULL-RESOLUTION grid export — VillageForge's --from-game
        /// input, so master plans are generated against the REAL map. Format v2
        /// (Ken 2026-07-13: "every map has a maximum depth of 16 layers" — the
        /// forge must see the vertical column, not just the surface):
        /// header "W H Y" (Y = level count), then FOUR blocks of H rows each:
        ///   1. class chars (~.T#^ space=none)
        ///   2. surface level, hex (F = none/16+)
        ///   3. cellar-capable depth below surface, hex (solid diggable voxels)
        ///   4. built levels above surface, hex (towers/upper floors)</summary>
        private static void DumpGrid()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(SizeX).Append(' ').Append(SizeZ).Append(' ').Append(SizeY).Append('\n');
                for (int z = 0; z < SizeZ; z++)
                {
                    for (int x = 0; x < SizeX; x++)
                        sb.Append(Cls[x, z] == CLS_WATER ? '~' : Cls[x, z] == CLS_OPEN ? '.'
                            : Cls[x, z] == CLS_TREE ? 'T' : Cls[x, z] == CLS_BUILT ? '#'
                            : Cls[x, z] == CLS_ROCK ? '^' : ' ');
                    sb.Append('\n');
                }
                AppendHexBlock(sb, (x, z) => Surface[x, z]);
                AppendHexBlock(sb, (x, z) => CellarBelow[x, z]);
                AppendHexBlock(sb, (x, z) => TowerAbove[x, z]);
                System.IO.File.WriteAllText(
                    @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\worldmap_grid.txt",
                    sb.ToString());
                LLMNPCsPlugin.LogToFile($"[WorldMap] grid exported for VillageForge ({SizeX}x{SizeZ}x{SizeY}, 4 blocks: class/surface/cellar-depth/tower)");
            }
            catch { }
        }

        private static void AppendHexBlock(StringBuilder sb, Func<int, int, int> get)
        {
            for (int z = 0; z < SizeZ; z++)
            {
                for (int x = 0; x < SizeX; x++)
                {
                    int v = get(x, z);
                    sb.Append(v < 0 || v > 15 ? 'F' : "0123456789ABCDEF"[v]);
                }
                sb.Append('\n');
            }
        }

        // Downsampled ASCII overview so a HUMAN can validate the full-map read.
        // Samples the column grid down to <= 72 wide, dominant class per block.
        private static void DumpOverview(int minY, int maxY)
        {
            try
            {
                int targetW = 72;
                int step = Math.Max(1, (SizeX + targetW - 1) / targetW);
                var sb = new StringBuilder();
                sb.Append($"WORLD OVERVIEW (downsampled 1:{step}) — {SizeX}x{SizeZ}, {SizeY} levels.\n");
                sb.Append("Legend: .=open ~=water #=built T=forest ^=rock ' '=none  (each cell = ")
                  .Append(step).Append("x").Append(step).Append(" tiles, dominant)\n");
                for (int z = 0; z < SizeZ; z += step)
                {
                    for (int x = 0; x < SizeX; x += step)
                    {
                        int w = 0, o = 0, tr = 0, b = 0, r = 0;
                        for (int dz = 0; dz < step && z + dz < SizeZ; dz++)
                            for (int dx = 0; dx < step && x + dx < SizeX; dx++)
                                switch (Cls[x + dx, z + dz]) { case CLS_WATER: w++; break; case CLS_OPEN: o++; break;
                                    case CLS_TREE: tr++; break; case CLS_BUILT: b++; break; case CLS_ROCK: r++; break; }
                        int max = Math.Max(Math.Max(w, o), Math.Max(Math.Max(tr, b), r));
                        char c = AIR;
                        if (max == 0) c = ' ';
                        else if (b == max) c = '#';
                        else if (w == max) c = '~';
                        else if (tr == max) c = 'T';
                        else if (r == max) c = '^';
                        else c = '.';
                        sb.Append(c);
                    }
                    sb.Append('\n');
                }
                sb.Append(LastSummary).Append('\n');
                try { System.IO.File.WriteAllText(
                    @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\worldmap.txt", sb.ToString()); } catch { }
            }
            catch { }
        }
    }
}
