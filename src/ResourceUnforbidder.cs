using System;
using System.Collections;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Un-forbids the colony's ground resource piles so settlers can HAUL, STORE,
    /// and EAT them. On many starts (and after events) the map's food/supplies
    /// spawn Forbidden — the game's hauling AI then refuses to touch them, so the
    /// settlers starve next to a pile of food. This is the programmatic "Allow [F]".
    ///
    /// Ground truth (GameApiScanner 2026-07-07, save Libury):
    ///   NSMedieval.Manager.ResourcePileManager (MonoSingleton)
    ///       .AllPileInstances : IEnumerable&lt;ResourcePileInstance&gt;
    ///   NSMedieval.State.ResourcePileInstance : WorldObject
    ///       Boolean IsForbidden { get; set; }   // set false == "Allow"
    ///       Void SetCanBeHauled(Boolean)
    ///   NSMedieval.Manager.ResourcePileHaulingManager (MonoSingleton)
    ///       Void ForceProcessPileState(ResourcePileInstance)
    ///       Void QueueForReProcess(ResourcePileInstance)
    ///       Void OnPileForbidStateChanged(IForbidable)
    /// Setting IsForbidden=false fires the pile's ForbidChangeEvent, but we also
    /// nudge the hauling manager to re-evaluate immediately.
    /// </summary>
    public static class ResourceUnforbidder
    {
        private static Type _pileMgrType, _haulMgrType;
        public static string LastResult = "(idle)";

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
            // MonoSingleton<T>.Instance is a static prop on the generic base; walk up.
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                var p = cur.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) { try { var v = p.GetValue(null, null); if (v != null) return v; } catch { } }
            }
            return null;
        }

        /// <summary>Allow every currently-forbidden ground pile. Returns the number
        /// newly allowed (0 = nothing to do, -1 = API not available yet).</summary>
        public static int UnforbidAll()
        {
            try
            {
                _pileMgrType = _pileMgrType ?? FindTypeByName("ResourcePileManager");
                if (_pileMgrType == null) { LastResult = "ResourcePileManager type not found"; return -1; }
                var mgr = SingletonInstance(_pileMgrType);
                if (mgr == null) { LastResult = "ResourcePileManager.Instance null (save loading?)"; return -1; }

                var piles = (_pileMgrType.GetProperty("AllPileInstances")?.GetValue(mgr, null)
                             ?? _pileMgrType.GetProperty("AllPiles")?.GetValue(mgr, null)) as IEnumerable;
                if (piles == null) { LastResult = "AllPileInstances null"; return -1; }

                _haulMgrType = _haulMgrType ?? FindTypeByName("ResourcePileHaulingManager");
                var haulMgr = _haulMgrType != null ? SingletonInstance(_haulMgrType) : null;
                var forceProcess = _haulMgrType?.GetMethod("ForceProcessPileState");
                var queueReprocess = _haulMgrType?.GetMethod("QueueForReProcess");

                int changed = 0, total = 0, forbidden = 0;
                // Snapshot first: setting IsForbidden may mutate collections mid-iterate.
                var snapshot = new System.Collections.Generic.List<object>();
                foreach (var p in piles) if (p != null) snapshot.Add(p);

                PropertyInfo isForbiddenProp = null;
                foreach (var pile in snapshot)
                {
                    total++;
                    var pt = pile.GetType();
                    if (isForbiddenProp == null || isForbiddenProp.DeclaringType != pt)
                        isForbiddenProp = pt.GetProperty("IsForbidden", BindingFlags.Public | BindingFlags.Instance);
                    if (isForbiddenProp == null) continue;
                    bool isF; try { isF = (bool)isForbiddenProp.GetValue(pile, null); } catch { continue; }
                    if (!isF) continue;
                    forbidden++;
                    try { isForbiddenProp.SetValue(pile, false, null); changed++; }
                    catch { continue; }
                    try { forceProcess?.Invoke(haulMgr, new[] { pile }); }
                    catch { try { queueReprocess?.Invoke(haulMgr, new[] { pile }); } catch { } }
                }
                LastResult = $"allowed {changed} (forbidden={forbidden} total={total})";
                return changed;
            }
            catch (Exception ex) { LastResult = "EXC: " + (ex.InnerException?.Message ?? ex.Message); return -1; }
        }
    }
}
