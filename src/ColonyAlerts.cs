using System;
using System.Collections.Generic;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Computes the colony's URGENT, colony-level priorities (the same signals the
    /// game's warning panel shows: no stockpile / low food / no cooking / not enough
    /// beds, etc.) and exposes them as a compact string that gets injected into the
    /// Player2 LLM decision prompt as PRIORITY context. This is the "brain sees the
    /// colony's problems" layer: each settler's LLM decision now reasons about what
    /// the whole settlement needs, not just its own hunger.
    ///
    /// Ground truth: census via StockpilePlacer.Count* + ResourcePileTracker
    /// .GetTotalStockpilePilesNutrition() (total food nutrition in storage).
    /// </summary>
    public static class ColonyAlerts
    {
        public static string Current = "Colony status not yet assessed.";
        private static Type _trackerType;

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

        /// <summary>Total nutrition of food currently in stockpiles (-1 if unavailable).</summary>
        private static int TotalFoodNutrition()
        {
            try
            {
                _trackerType = _trackerType ?? FindTypeByName("ResourcePileTracker");
                var mgr = _trackerType != null ? Singleton(_trackerType) : null;
                var m = _trackerType?.GetMethod("GetTotalStockpilePilesNutrition");
                if (mgr == null || m == null) return -1;
                return Convert.ToInt32(m.Invoke(mgr, null));
            }
            catch { return -1; }
        }

        public static void Compute(int pop)
        {
            try
            {
                var a = new List<string>();
                int stock = StockpilePlacer.CountStockpilesInWorld();
                int cook = StockpilePlacer.CountBuildings("camp_fire");
                int beds = StockpilePlacer.CountBuildings("hay_sleeping_spot");
                int food = TotalFoodNutrition();

                if (food >= 0 && food < Math.Max(1, pop) * 8)
                    a.Add($"FOOD IS SCARCE (stored nutrition {food} for {pop} settlers) — hunt, forage, or farm and cook meals before anyone starves.");
                if (stock == 0)
                    a.Add("No stockpile exists — build storage so resources can be gathered and organized.");
                if (cook == 0)
                    a.Add("No cooking station — build a campfire so raw food becomes edible meals.");
                if (beds >= 0 && beds < pop)
                    a.Add($"Not enough beds ({beds}/{pop}) — build beds inside a roofed house so settlers rest and stay dry.");
                if (EquipManager.LastHuntersMissingWeapon > 0)
                    a.Add($"{EquipManager.LastHuntersMissingWeapon} hunter(s) have NO ranged weapon — equip a bow/sling from the stockpile, or craft one at a fletcher's table, so they can actually hunt.");

                Current = a.Count > 0
                    ? "The whole settlement urgently needs (highest priority):\n- " + string.Join("\n- ", a)
                    : "Colony infrastructure is adequate; focus on comfort, defence, and growth.";
            }
            catch (Exception ex) { Current = "colony-alerts error: " + ex.Message; }
        }
    }
}
