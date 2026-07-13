using System;
using System.Reflection;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// REAL MOVEMENT ORDERS (doc 09: "go to the Blackfen holding, patrol its
    /// edge, return"). Ground truth (decompile 2026-07-12): the game's own
    /// order channel is `HumanoidInstance.EnemyBehaviour.CurrentOrder`
    /// (despite the name it is the ORDER slot for all humanoids — every
    /// FollowOrderBaseGoal&lt;T&gt; reads exactly this), and
    /// `NSMedieval.CommanderAI.Orders.MoveOrder(Vector3 destination, float
    /// guardModeRadius)` is the move/guard order the drafted-move UI issues.
    /// GuardModeRadius doubles as PATROL (guard mode holds the area).
    ///
    /// HONESTY GATE: setting the order is INVOKED, not APPLIED — whether
    /// FollowMoveOrderGoal wins the GOAP race against work goals is the live
    /// validation (watch the settler actually walk). Every call logs.
    /// </summary>
    public static class MovementOrders
    {
        public static string LastResult = "(idle)";

        /// <summary>Order a settler to a grid cell. guardRadius > 0 = stay and
        /// guard/patrol the area after arriving.</summary>
        public static bool TryMoveTo(GameObject settlerGo, int x, int y, int z, float guardRadius = 2f)
        {
            try
            {
                var humanoid = ResolveHumanoid(settlerGo);
                if (humanoid == null) { LastResult = "move: no HumanoidInstance"; return false; }
                var behaviour = humanoid.GetType().GetProperty("EnemyBehaviour")?.GetValue(humanoid, null);
                if (behaviour == null) { LastResult = "move: no EnemyBehaviour (order slot)"; return false; }

                // world position from the grid cell (proven GridUtils path)
                var vec3T = FindTypeByName("Vec3Int");
                var cell = vec3T?.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) })
                    ?.Invoke(new object[] { x, y, z });
                var gridUtils = FindTypeByName("GridUtils");
                var wposObj = gridUtils?.GetMethod("GetWorldPosition", new[] { vec3T })
                    ?.Invoke(null, new[] { cell });
                if (wposObj == null) { LastResult = "move: no world position"; return false; }

                var orderT = FindType("NSMedieval.CommanderAI.Orders.MoveOrder");
                var ctor = orderT?.GetConstructor(new[] { typeof(Vector3), typeof(float) });
                if (ctor == null) { LastResult = "move: MoveOrder ctor not found"; return false; }
                var order = ctor.Invoke(new object[] { (Vector3)wposObj, guardRadius });

                var slot = behaviour.GetType().GetProperty("CurrentOrder");
                if (slot == null) { LastResult = "move: CurrentOrder slot not found"; return false; }
                slot.SetValue(behaviour, order);
                LastResult = $"MoveOrder set → ({x},{y},{z}) guard={guardRadius} — INVOKED; goal race validates live";
                LLMNPCsPlugin.LogToFile("[MovementOrders] " + LastResult);
                return true;
            }
            catch (Exception ex)
            {
                LastResult = "move EXC: " + (ex.InnerException?.Message ?? ex.Message);
                LLMNPCsPlugin.LogToFile("[MovementOrders] " + LastResult);
                return false;
            }
        }

        /// <summary>Cancel the current order (the game's own Stop semantics:
        /// null order releases the settler back to normal work goals).</summary>
        public static bool TryRelease(GameObject settlerGo)
        {
            try
            {
                var humanoid = ResolveHumanoid(settlerGo);
                var behaviour = humanoid?.GetType().GetProperty("EnemyBehaviour")?.GetValue(humanoid, null);
                var slot = behaviour?.GetType().GetProperty("CurrentOrder");
                if (slot == null) { LastResult = "release: no order slot"; return false; }
                slot.SetValue(behaviour, null);
                LastResult = "order released — settler returns to work goals";
                LLMNPCsPlugin.LogToFile("[MovementOrders] " + LastResult);
                return true;
            }
            catch (Exception ex)
            {
                LastResult = "release EXC: " + (ex.InnerException?.Message ?? ex.Message);
                return false;
            }
        }

        private static object ResolveHumanoid(GameObject settlerGo)
        {
            if (settlerGo == null) return null;
            // The settler view component exposes its model instance; walk the
            // components for anything with a HumanoidInstance-typed member.
            foreach (var comp in settlerGo.GetComponents<Component>())
            {
                if (comp == null) continue;
                var t = comp.GetType();
                foreach (var name in new[] { "HumanoidInstance", "Humanoid", "Instance", "Model" })
                {
                    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var v = p?.GetValue(comp, null)
                            ?? t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(comp);
                    if (v != null && v.GetType().Name == "HumanoidInstance") return v;
                }
            }
            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { var t = a.GetType(fullName, false); if (t != null) return t; } catch { } }
            return null;
        }

        private static Type FindTypeByName(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }
    }
}
