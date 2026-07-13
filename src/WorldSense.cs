using System;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// THE PLANNER, leg 1 (Ken: "convert the map into a math grid the model
    /// understands, like printers rasterize"). Renders the home region as an
    /// LLM-readable character grid + colony summary — the world state the
    /// Player2 planning call reasons over (WHERE/WHAT/WHY/WHEN/HOW).
    ///
    /// Legend: '.'=open buildable  '~'=wet/unbuildable  'X'=building
    ///         'S'=stockpile zone  'T'=tree  'H'=home anchor
    /// v2 channels: soil quality, elevation, indoor/outdoor (RoomDetection).
    /// Also dumped to validation/worldsense.txt for human validation.
    /// </summary>
    public static class WorldSense
    {
        public static string LastGrid = "";
        private static Type _plantMgrT, _vec3T;

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

        /// <summary>Rasterize a (2*half+1)^2 region centred on home at level hy.</summary>
        // DEFERRED (2026-07-12 ~23:20): the 80s main-thread raster's "background
        // thread" cure crashed the game (off-thread reads of live game state =
        // native crashes — the same lesson as WorldMap). Nothing critical
        // consumes this raster (WorldMap feeds the site scorer), so it is
        // DISABLED until it gets the sliced-enumerator treatment. Honest no-op.
        public static string Rasterize(int hx, int hy, int hz, int half = 18)
        {
            if (LastGrid.Length == 0)
                LastGrid = "(worldsense raster deferred — off-thread reads crash; slice it like WorldMap before re-enabling)";
            return LastGrid;
        }

        private static string RasterizeCore(int hx, int hy, int hz, int half = 18)
        {
            try
            {
                _plantMgrT = _plantMgrT ?? FindTypeByName("PlantResourceManager");
                _vec3T = _vec3T ?? FindTypeByName("Vec3Int");
                var plantMgr = _plantMgrT != null ? Singleton(_plantMgrT) : null;
                var getPlant = plantMgr?.GetType().GetMethod("GetPlant", new[] { _vec3T });
                var ctor = _vec3T?.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });

                var sb = new System.Text.StringBuilder();
                sb.Append($"MAP {2 * half + 1}x{2 * half + 1} centred on HOME({hx},{hz}) level {hy}. ");
                sb.Append("Legend: .=open ~=wet/unbuildable X=building S=stockpile T=tree H=home\n");
                for (int dz = -half; dz <= half; dz++)
                {
                    for (int dx = -half; dx <= half; dx++)
                    {
                        int x = hx + dx, z = hz + dz;
                        char c;
                        if (dx == 0 && dz == 0) c = 'H';
                        else if (StockpilePlacer.AnyBuildingAt(x, hy, z)) c = 'X';
                        else if (StockpilePlacer.IsOnStockpile(x, hy, z)) c = 'S';
                        else if (HasTree(getPlant, plantMgr, ctor, x, hy, z)) c = 'T';
                        else if (!StockpilePlacer.IsDryBuildableGround(x, hy, z)) c = '~';
                        else c = '.';
                        sb.Append(c);
                    }
                    sb.Append('\n');
                }
                LastGrid = sb.ToString();
                try { System.IO.File.WriteAllText(
                    @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\worldsense.txt", LastGrid); } catch { }
                return LastGrid;
            }
            catch (Exception ex) { return LastGrid = "worldsense EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        private static bool HasTree(MethodInfo getPlant, object mgr, ConstructorInfo ctor, int x, int y, int z)
        {
            if (getPlant == null || mgr == null || ctor == null) return false;
            try
            {
                var cell = ctor.Invoke(new object[] { x, y, z });
                return getPlant.Invoke(mgr, new[] { cell }) != null;
            }
            catch { return false; }
        }
    }
}
