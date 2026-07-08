using System;
using System.Reflection;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Autonomously DESIGNATES nearby trees for chopping so the colony gathers
    /// WOOD — the materials the settlers need to actually CONSTRUCT the placed
    /// blueprints (cook fire, beds, house). Without this the settlers stand idle
    /// next to unbuilt blueprints because there's no wood.
    ///
    /// Ground truth (decompiled 2026-07-07):
    ///   NSMedieval.Manager.PlantResourceManager (MonoSingleton)
    ///       PlantMapResourceInstance GetPlant(Vec3Int gridPos)
    ///       Dictionary&lt;OrderType,HashSet&lt;MapResourceInstance&gt;&gt; ResourcesWithOrders
    ///   NSMedieval.State.MapResourceInstance
    ///       OrderType GetPossibleOrders();  OrderType CurrentOrder;
    ///       void SetCurrentOrder(OrderType, bool); void SetPlayerOrder(bool)
    ///   OrderType.Chopping == fell a tree for wood.
    /// Marking a tree's order = Chopping creates the chop task the settlers' job AI
    /// (HarvestGoal) then works, exactly like the player selecting a tree -> Chop.
    /// Bounded (radius + per-pass + session caps) so it never clear-cuts the map.
    /// </summary>
    public static class WoodGatherer
    {
        private static Type _mgrType, _orderType, _vec3IntType;
        public static string LastResult = "(idle)";
        private static int _sessionDesignated = 0;
        private const int SessionCap = 40; // never designate more than this per session

        /// <summary>Clear per-session caps. Called by BuiltState on world (re)load.</summary>
        public static void Reset() { _sessionDesignated = 0; LastResult = "(idle)"; }

        private static Type FindTypeByName(string shortName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { foreach (var t in asm.GetTypes()) if (t.Name == shortName) return t; }
                catch { }
            }
            return null;
        }

        private static object SingletonInstance(Type t)
        {
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                var p = cur.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) { try { var v = p.GetValue(null, null); if (v != null) return v; } catch { } }
            }
            return null;
        }

        private static object MakeVec3Int(int x, int y, int z)
        {
            _vec3IntType = _vec3IntType ?? FindTypeByName("Vec3Int");
            var ctor = _vec3IntType?.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
            return ctor?.Invoke(new object[] { x, y, z });
        }

        /// <summary>Designate up to maxNew choppable trees near the settler for
        /// chopping. Returns count newly designated (-1 if API unavailable).</summary>
        public static int DesignateTreesNear(GameObject settlerGo, int radius = 14, int maxNew = 12)
        {
            try
            {
                if (_sessionDesignated >= SessionCap) { LastResult = $"session cap reached ({_sessionDesignated})"; return 0; }

                _mgrType = _mgrType ?? FindTypeByName("PlantResourceManager");
                if (_mgrType == null) { LastResult = "PlantResourceManager not found"; return -1; }
                var mgr = SingletonInstance(_mgrType);
                if (mgr == null) { LastResult = "PlantResourceManager.Instance null"; return -1; }

                _orderType = _orderType ?? FindTypeByName("OrderType");
                if (_orderType == null) { LastResult = "OrderType enum not found"; return -1; }
                object chopping;
                try { chopping = Enum.Parse(_orderType, "Chopping"); }
                catch { LastResult = "OrderType.Chopping missing"; return -1; }

                var node = StockpilePlacer.SettlerNode(settlerGo);
                // Designate trees around the colony HOME (compact), not a roaming settler.
                if (StockpilePlacer.HomeAnchor != null) node = StockpilePlacer.HomeAnchor;
                if (node == null) { LastResult = "no home/settler node"; return -1; }

                var getPlant = _mgrType.GetMethod("GetPlant", new[] { _vec3IntType ?? (_vec3IntType = FindTypeByName("Vec3Int")) });
                if (getPlant == null) { LastResult = "GetPlant(Vec3Int) not found"; return -1; }

                int designated = 0, scanned = 0;
                // scan outward in rings so we designate the CLOSEST trees first
                for (int r = 1; r <= radius && designated < maxNew && _sessionDesignated < SessionCap; r++)
                {
                    for (int dx = -r; dx <= r && designated < maxNew; dx++)
                        foreach (var dz in (Math.Abs(dx) == r
                                 ? RangeInclusive(-r, r)
                                 : new[] { -r, r }))
                        {
                            var cell = MakeVec3Int(node[0] + dx, node[1], node[2] + dz);
                            if (cell == null) continue;
                            object plant;
                            try { plant = getPlant.Invoke(mgr, new[] { cell }); }
                            catch { continue; }
                            if (plant == null) continue;
                            scanned++;
                            var pt = plant.GetType();
                            object possible;
                            try { possible = pt.GetMethod("GetPossibleOrders").Invoke(plant, null); }
                            catch { continue; }
                            bool canChop;
                            try { canChop = ((Enum)possible).HasFlag((Enum)chopping); }
                            catch { canChop = false; }
                            if (!canChop) continue;
                            object current = null;
                            try { current = pt.GetProperty("CurrentOrder")?.GetValue(plant, null); } catch { }
                            if (current != null && current.Equals(chopping)) continue; // already ordered
                            try { pt.GetMethod("SetPlayerOrder", new[] { typeof(bool) })?.Invoke(plant, new object[] { true }); } catch { }
                            try
                            {
                                var setOrder = pt.GetMethod("SetCurrentOrder", new[] { _orderType, typeof(bool) });
                                setOrder.Invoke(plant, new[] { chopping, (object)false });
                                designated++; _sessionDesignated++;
                            }
                            catch { }
                        }
                }
                LastResult = $"designated {designated} trees (scanned {scanned}, session {_sessionDesignated})";
                return designated;
            }
            catch (Exception ex) { LastResult = "EXC: " + (ex.InnerException?.Message ?? ex.Message); return -1; }
        }

        private static System.Collections.Generic.IEnumerable<int> RangeInclusive(int a, int b)
        {
            for (int i = a; i <= b; i++) yield return i;
        }
    }
}
