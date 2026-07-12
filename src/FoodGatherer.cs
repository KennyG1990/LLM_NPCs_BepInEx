using System;
using System.Collections;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Autonomous FOOD production so the colony stops starving: HUNT wild animals
    /// and FORAGE wild food plants near the colony HOME. Bounded to a home radius
    /// and per-session caps so it doesn't strip the whole map.
    ///
    /// Ground truth (decompiled 2026-07-07):
    ///   NSMedieval.Manager.AnimalManager (MonoSingleton).Animals : dict&lt;AnimalInstance,AnimalView&gt;
    ///   AnimalInstance: AnimalType (Wild/WildAggressive = huntable), OrderType,
    ///       internal SetOrder(AnimalOrderType); CreatureBase.GetGridPosition():Vec3Int
    ///   AnimalView.OnMarkForOrder(AnimalOrderType) — registers the hunt task/marker
    ///   (mirrors AnimalManager.OnMarkAnimalForOrder: SetOrder + view.OnMarkForOrder)
    ///   NSMedieval.Manager.PlantResourceManager.GetPlant(Vec3Int) +
    ///       PlantMapResourceInstance.SetCurrentOrder(OrderType.Harvesting) = forage.
    /// </summary>
    public static class FoodGatherer
    {
        public static string LastResult = "(idle)";
        private static int _huntSession = 0, _forageSession = 0;
        private const int HuntCap = 16, ForageCap = 40;

        /// <summary>CRISIS (#37, Ken: Llangefni starved AFTER hitting these very
        /// caps — "hunt+0 forage+0" during a famine with animals all over the
        /// map). When the colony is starving, the peacetime don't-strip-the-map
        /// bounds YIELD: caps ignored, callers widen the radius. Survival
        /// constraints beat all other constraints.</summary>
        public static bool Crisis = false;

        /// <summary>Clear per-session caps. Called by BuiltState on world (re)load.</summary>
        public static void Reset() { _huntSession = 0; _forageSession = 0; LastResult = "(idle)"; }

        private static Type _animMgr, _animOrder, _animTypeEnum, _plantMgr, _orderType, _vec3;

        private static Type FindTypeByName(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }
        private static object Singleton(Type t)
        {
            for (var c = t; c != null; c = c.BaseType)
            {
                var p = c.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) { try { var v = p.GetValue(null, null); if (v != null) return v; } catch { } }
            }
            return null;
        }
        private static int F(object o, string n)
        {
            var f = o?.GetType().GetField(n, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { try { return Convert.ToInt32(f.GetValue(o)); } catch { } }
            return 0;
        }

        public static string ProduceFoodNear(int hx, int hy, int hz, int radius)
        {
            int hunted = HuntWildAnimals(hx, hz, radius);
            int foraged = ForageFoodPlants(hx, hy, hz, radius);
            LastResult = $"hunt+{hunted} forage+{foraged} (session hunt={_huntSession} forage={_forageSession})";
            return LastResult;
        }

        private static int HuntWildAnimals(int hx, int hz, int radius)
        {
            try
            {
                if (!Crisis && _huntSession >= HuntCap) return 0;
                _animMgr = _animMgr ?? FindTypeByName("AnimalManager");
                var mgr = _animMgr != null ? Singleton(_animMgr) : null;
                if (mgr == null) return 0;
                var animals = _animMgr.GetProperty("Animals")?.GetValue(mgr, null) as IEnumerable;
                if (animals == null) return 0;

                _animOrder = _animOrder ?? FindTypeByName("AnimalOrderType");
                _animTypeEnum = _animTypeEnum ?? FindTypeByName("AnimalType");
                if (_animOrder == null || _animTypeEnum == null) return 0;
                object hunt = Enum.Parse(_animOrder, "Hunt");
                object wild = Enum.Parse(_animTypeEnum, "Wild");
                object wildAgg = Enum.Parse(_animTypeEnum, "WildAggressive");

                int marked = 0;
                foreach (var kv in animals)
                {
                    if ((!Crisis && _huntSession >= HuntCap) || marked >= (Crisis ? 12 : 6)) break;
                    var animal = kv.GetType().GetProperty("Key")?.GetValue(kv, null);
                    var view = kv.GetType().GetProperty("Value")?.GetValue(kv, null);
                    if (animal == null) continue;
                    var at = animal.GetType();
                    try { if ((bool)(at.GetProperty("HasDied")?.GetValue(animal, null) ?? false)) continue; } catch { }
                    var atype = at.GetProperty("AnimalType")?.GetValue(animal, null);
                    if (atype == null || (!atype.Equals(wild) && !atype.Equals(wildAgg))) continue;
                    var curOrder = at.GetProperty("OrderType")?.GetValue(animal, null);
                    if (curOrder != null && curOrder.Equals(hunt)) continue;
                    object gp; try { gp = at.GetMethod("GetGridPosition", Type.EmptyTypes)?.Invoke(animal, null); } catch { continue; }
                    if (gp == null) continue;
                    if (Math.Abs(F(gp, "x") - hx) > radius || Math.Abs(F(gp, "z") - hz) > radius) continue;

                    try { at.GetMethod("SetOrder", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(animal, new[] { hunt }); }
                    catch { continue; }
                    try { view?.GetType().GetMethod("OnMarkForOrder")?.Invoke(view, new[] { hunt }); } catch { }
                    marked++; _huntSession++;
                }
                return marked;
            }
            catch (Exception ex) { LLMNPCsPlugin.LogToFile("[FoodGatherer] hunt EXC: " + ex.Message); return 0; }
        }

        private static object MakeVec3(int x, int y, int z)
        {
            _vec3 = _vec3 ?? FindTypeByName("Vec3Int");
            return _vec3?.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) })?.Invoke(new object[] { x, y, z });
        }

        private static int ForageFoodPlants(int hx, int hy, int hz, int radius)
        {
            try
            {
                if (!Crisis && _forageSession >= ForageCap) return 0;
                _plantMgr = _plantMgr ?? FindTypeByName("PlantResourceManager");
                var mgr = _plantMgr != null ? Singleton(_plantMgr) : null;
                if (mgr == null) return 0;
                _orderType = _orderType ?? FindTypeByName("OrderType");
                if (_orderType == null) return 0;
                object harvesting = Enum.Parse(_orderType, "Harvesting");
                var getPlant = _plantMgr.GetMethod("GetPlant", new[] { _vec3 ?? (_vec3 = FindTypeByName("Vec3Int")) });
                if (getPlant == null) return 0;

                int marked = 0;
                for (int dx = -radius; dx <= radius && marked < (Crisis ? 14 : 6) && (Crisis || _forageSession < ForageCap); dx++)
                    for (int dz = -radius; dz <= radius && marked < (Crisis ? 14 : 6) && (Crisis || _forageSession < ForageCap); dz++)
                    {
                        var cell = MakeVec3(hx + dx, hy, hz + dz);
                        if (cell == null) continue;
                        object plant; try { plant = getPlant.Invoke(mgr, new[] { cell }); } catch { continue; }
                        if (plant == null) continue;
                        var pt = plant.GetType();
                        object possible; try { possible = pt.GetMethod("GetPossibleOrders").Invoke(plant, null); } catch { continue; }
                        bool canForage; try { canForage = ((Enum)possible).HasFlag((Enum)harvesting); } catch { canForage = false; }
                        if (!canForage) continue; // trees support Chopping not Harvesting; this selects berries/food
                        var cur = pt.GetProperty("CurrentOrder")?.GetValue(plant, null);
                        if (cur != null && cur.Equals(harvesting)) continue;
                        try { pt.GetMethod("SetPlayerOrder", new[] { typeof(bool) })?.Invoke(plant, new object[] { true }); } catch { }
                        try { pt.GetMethod("SetCurrentOrder", new[] { _orderType, typeof(bool) })?.Invoke(plant, new[] { harvesting, (object)false }); marked++; _forageSession++; }
                        catch { }
                    }
                return marked;
            }
            catch (Exception ex) { LLMNPCsPlugin.LogToFile("[FoodGatherer] forage EXC: " + ex.Message); return 0; }
        }
    }
}
