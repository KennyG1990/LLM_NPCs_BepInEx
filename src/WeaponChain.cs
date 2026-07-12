using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// WEAPON CHAIN (#37 slice: the "hunters have NO ranged weapon" alert gets
    /// a reaction owner). Both colony wipes shared this signature: the fletcher
    /// pipeline silently stuck (NO-SKILLED-WORKER / no resources) while armed
    /// hunting stayed impossible and telemetry showed nothing actionable.
    ///
    /// Why the existing wiring was blind: ColonyBuilder queues sling/short_bow
    /// via ProductionPlanner, but ProductionPlanner.LastResult is ONE shared
    /// string — the campfire meal call overwrites the fletcher outcome every
    /// tick, so a stuck weapon order was invisible.
    ///
    /// This module owns the pipeline end-to-end and reports it honestly:
    ///   station (any whose Blueprint.Productions hosts a ranged recipe)
    ///   → queued weapon orders → each order's ProductionState
    ///   → skill gate per LIVE settler via the game's OWN
    ///     Production.HasSkillsRequired (manual Key/Value compare fallback)
    ///   → REACTION: raise the qualified settler's Crafting priority to 1
    ///     (WorkerGoapAgent.ChangeJobPriority), or NAME the unmet skill.
    ///
    /// Ground truth (validation/decompiled, 2026-07-11):
    ///   NSMedieval.Model.Production { RequiredSkills: List&lt;SkillLevelPair&gt;
    ///     {Key: SkillType, Value: int}, JobType, Recipe,
    ///     HasSkillsRequired(IProductionAgent), FindFirstUnmetSkillRequirement }
    ///   ProductionComponentInstance.ProductionSystemInstance.Productions :
    ///     List&lt;ProductionInstance&gt; { BlueprintId, State, Blueprint }
    ///   ProductionState: WaitingForWorker | NoSkilledWorker |
    ///     WaitingForResources | InProgress | Paused | TargetReached
    ///   Station hosting: BuildingBlueprint.Productions (List&lt;string&gt;).
    ///   JobType flags: Crafting = 8 (decompiled, see JobRouter header).
    /// </summary>
    public static class WeaponChain
    {
        public static string LastResult = "(idle)";
        private const long JobTypeCrafting = 8;
        private static readonly string[] RangedIds =
            { "sling", "sling_staff", "short_bow", "war_bow", "long_bow", "curved_bow", "light_crossbow", "crossbow", "heavy_crossbow" };
        private static readonly HashSet<string> _craftBoosted = new HashSet<string>();   // one boost per settler per session

        public static void Reset() { _craftBoosted.Clear(); LastResult = "(idle)"; }

        public static void Tick(List<Settler> settlers)
        {
            try
            {
                if (EquipManager.LastHuntersMissingWeapon <= 0) { LastResult = "(hunters armed)"; return; }
                if (EquipManager.LastRangedStored + EquipManager.LastRangedGround > 0)
                { LastResult = "(ranged pile exists — equip orders handle pickup)"; return; }

                // 1) find CONSTRUCTED stations hosting a ranged recipe.
                var stations = FindWeaponStations();
                if (stations.Count == 0)
                { LastResult = "weapons: no constructed station hosts a ranged recipe (fletcher build priority pending)"; return; }

                // 2) walk their production queues for weapon orders.
                var parts = new List<string>();
                bool anyOrder = false;
                foreach (var (stationId, comp) in stations)
                {
                    var sys = HGet(comp, "ProductionSystemInstance") ?? HGet(comp, "ProductionSystem");
                    var prods = sys != null ? HGet(sys, "Productions") as IEnumerable : null;
                    if (prods == null) { parts.Add($"{stationId}: no production system"); continue; }
                    foreach (var order in prods)
                    {
                        var bpId = HGet(order, "BlueprintId") as string;
                        if (bpId == null || Array.IndexOf(RangedIds, bpId) < 0) continue;
                        anyOrder = true;
                        var state = HGet(order, "State")?.ToString() ?? "?";
                        string line = $"{bpId}@{stationId}={state}";
                        if (state == "NoSkilledWorker" || state == "WaitingForWorker")
                            line += " → " + ReactToWorkerGap(order, settlers);
                        else if (state == "TargetReached")
                            line += " → " + ReactToTargetReached(order, bpId);
                        parts.Add(line);
                    }
                }
                if (!anyOrder) parts.Add($"{stations.Count} station(s) up, no weapon order queued yet (planner queues next tick)");
                LastResult = "weapons: " + string.Join(" ; ", parts);
            }
            catch (Exception ex) { LastResult = "weapons EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        /// <summary>TargetReached with hunters still unarmed = the target is too
        /// low (or the crafted item vanished into worker storage — seen live
        /// 19:18: TargetReached, ranged piles 0, no equip order ever issued).
        /// The need is the target: 3 unarmed hunters ⇒ produce-until-3. Uses
        /// the game's own SetProductTargetCount (decompiled, fires UpdateState
        /// so the order re-enters the work queue).</summary>
        private static readonly HashSet<string> _targetRaised = new HashSet<string>();
        private static string ReactToTargetReached(object order, string bpId)
        {
            try
            {
                int missing = EquipManager.LastHuntersMissingWeapon;
                int target = 1; try { target = Convert.ToInt32(HGet(order, "ProductTargetCount") ?? 1); } catch { }
                var prodBp = HGet(order, "Blueprint");
                int worldCount = -1;
                try { worldCount = Convert.ToInt32(prodBp?.GetType().GetMethod("GetAllProductsCount", BindingFlags.Public | BindingFlags.Instance)?.Invoke(prodBp, null) ?? -1); } catch { }
                if (missing <= 0) return "hunters armed";
                if (missing <= target && !_targetRaised.Contains(bpId))
                    return $"target {target} claimed reached (world={worldCount}) but {missing} hunter(s) still unarmed — watching";
                if (_targetRaised.Contains(bpId)) return $"target already raised (world={worldCount})";
                var set = order.GetType().GetMethod("SetProductTargetCount", BindingFlags.Public | BindingFlags.Instance);
                if (set == null) return "no SetProductTargetCount";
                set.Invoke(order, new object[] { missing, true });
                _targetRaised.Add(bpId);
                LLMNPCsPlugin.LogToFile($"[WeaponChain] {bpId}: TargetReached at target={target} yet {missing} hunter(s) unarmed (worldCount={worldCount}) — target raised to {missing}");
                return $"target raised {target}→{missing} (world={worldCount})";
            }
            catch (Exception ex) { return "raise EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        /// <summary>An order is stuck for want of a worker: find a settler who
        /// passes the game's own skill gate and raise their Crafting priority;
        /// otherwise name the unmet requirement honestly.</summary>
        private static string ReactToWorkerGap(object order, List<Settler> settlers)
        {
            try
            {
                var prodBp = HGet(order, "Blueprint");
                if (prodBp == null) return "no blueprint on order";
                var hasSkills = prodBp.GetType().GetMethod("HasSkillsRequired", BindingFlags.Public | BindingFlags.Instance);

                object bestModel = null; string bestName = null; UnityEngine.Component bestRc = null;
                foreach (var s in settlers)
                {
                    if (s == null || s.gameObject == null) continue;
                    if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out var name, out var rc)) continue;
                    var model = HGet(rc, "HumanoidInstance") ?? rc;
                    if (model == null) continue;
                    bool ok = false;
                    if (hasSkills != null)
                    {
                        try { ok = hasSkills.Invoke(prodBp, new[] { model }) is bool b && b; }
                        catch { ok = MeetsSkillsManually(prodBp, model); }   // model may not be the IProductionAgent
                    }
                    else ok = MeetsSkillsManually(prodBp, model);
                    if (ok) { bestModel = model; bestName = name; bestRc = rc; break; }
                }

                if (bestModel == null)
                    return "NOBODY qualifies — " + DescribeUnmetSkill(prodBp);

                // Qualified settler exists → the gap is job priority. Boost once.
                if (_craftBoosted.Contains(bestName)) return $"{bestName} qualifies (Crafting already prio 1)";
                var agent = GameBridge.GetGoapAgent(bestRc);
                var change = agent?.GetType().GetMethod("ChangeJobPriority", BindingFlags.Public | BindingFlags.Instance);
                // FULL name — short-name lookup grabbed Unity.Jobs...JobType
                // and threw on Enum.ToObject (live 19:44).
                var jobTypeT = FindTypeFull("NSMedieval.State.WorkerJobs.JobType");
                if (change == null || jobTypeT == null) return $"{bestName} qualifies but no ChangeJobPriority path";
                change.Invoke(agent, new[] { Enum.ToObject(jobTypeT, JobTypeCrafting), (object)1 });
                _craftBoosted.Add(bestName);
                LLMNPCsPlugin.LogToFile($"[WeaponChain] {bestName} qualifies for the stuck weapon order — Crafting priority raised to 1");
                return $"{bestName} qualifies → Crafting prio 1";
            }
            catch (Exception ex) { return "react EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        // Manual gate: RequiredSkills List<SkillLevelPair{Key,Value}> vs the
        // settler's Skills list (Id/Level — same read as JobRouter).
        private static bool MeetsSkillsManually(object prodBp, object model)
        {
            try
            {
                var req = HGet(prodBp, "RequiredSkills") as IEnumerable;
                if (req == null) return true;   // no requirements
                var have = ReadSkills(model);
                foreach (var pair in req)
                {
                    if (pair == null) continue;
                    var key = HGet(pair, "Key")?.ToString()?.Replace(" ", "");
                    int need = 0; try { need = Convert.ToInt32(HGet(pair, "Value") ?? 0); } catch { }
                    if (key == null) continue;
                    if (!have.TryGetValue(key, out int lvl) || lvl < need) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static string DescribeUnmetSkill(object prodBp)
        {
            try
            {
                var req = HGet(prodBp, "RequiredSkills") as IEnumerable;
                if (req == null) return "no skill data";
                var needs = new List<string>();
                foreach (var pair in req)
                {
                    if (pair == null) continue;
                    needs.Add($"{HGet(pair, "Key")}≥{HGet(pair, "Value")}");
                }
                return needs.Count == 0 ? "no skill requirement (worker/job gap?)" : "needs " + string.Join("+", needs);
            }
            catch { return "no skill data"; }
        }

        private static Dictionary<string, int> ReadSkills(object model)
        {
            var have = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var owner = HGet(model, "Skills") ?? HGet(model, "WorkerSkills");
                var list = owner != null ? HGet(owner, "Skills") as IEnumerable : null;
                if (list == null) return have;
                foreach (var sk in list)
                {
                    if (sk == null) continue;
                    var sn = HGet(sk, "Id")?.ToString()?.Replace(" ", "");
                    int lvl = 0; try { lvl = Convert.ToInt32(HGet(sk, "Level") ?? 0); } catch { }
                    if (sn != null) have[sn] = lvl;
                }
            }
            catch { }
            return have;
        }

        /// <summary>All CONSTRUCTED buildings whose blueprint's Productions list
        /// hosts at least one ranged recipe → (stationId, ProductionComponentInstance).</summary>
        private static List<(string id, object comp)> FindWeaponStations()
        {
            var found = new List<(string, object)>();
            try
            {
                var vmT = FindTypeByName("VillageManager");
                var village = vmT?.GetProperty("ActiveVillage", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                var map = village?.GetType().GetProperty("Map")?.GetValue(village, null);
                var bmm = map?.GetType().GetProperty("BuildingsManagerMain")?.GetValue(map, null);
                if (bmm == null) return found;
                var bbiT = FindTypeByName("BaseBuildingInstance");
                var listT = typeof(List<>).MakeGenericType(bbiT);
                var all = Activator.CreateInstance(listT);
                bmm.GetType().GetMethod("GetAllBuildings", new[] { listT })?.Invoke(bmm, new[] { all });
                var pciT = FindTypeByName("ProductionComponentInstance");
                var getComp = bmm.GetType().GetMethod("GetComponentInstance")?.MakeGenericMethod(pciT);
                foreach (var inst in (IEnumerable)all)
                {
                    if (inst == null) continue;
                    var t = inst.GetType();
                    if (t.GetProperty("ConstructionPhase")?.GetValue(inst, null)?.ToString() == "Blueprint") continue;
                    var comp = getComp?.Invoke(bmm, new[] { inst });
                    if (comp == null) continue;   // not a production building
                    // Recipes live on the ProductionComponentBlueprint (the
                    // COMPONENT's Blueprint), NOT the building blueprint —
                    // building bp only carries ProductionComponentID
                    // (decompiled ProductionComponent.Initialize; reading
                    // Productions off the building bp was a silent
                    // false-negative on the CONSTRUCTED fletcher, 19:15).
                    var hosted = HGet(HGet(comp, "Blueprint"), "Productions") as IEnumerable;
                    if (hosted == null) continue;
                    bool hostsRanged = false;
                    foreach (var pid in hosted)
                        if (pid is string ps && Array.IndexOf(RangedIds, ps) >= 0) { hostsRanged = true; break; }
                    if (!hostsRanged) continue;
                    string id = null;
                    try { var bbp = t.GetProperty("Blueprint")?.GetValue(inst, null); id = bbp?.GetType().GetMethod("GetID")?.Invoke(bbp, null) as string; } catch { }
                    found.Add((id ?? "?", comp));
                }
            }
            catch { }
            return found;
        }

        // ── shared reflection helpers (module-local, same pattern as siblings) ──
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

        private static Type FindTypeByName(string shortName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in asm.GetTypes()) if (t.Name == shortName) return t; } catch { } }
            return null;
        }

        private static Type FindTypeFull(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            { try { var t = asm.GetType(fullName, false); if (t != null) return t; } catch { } }
            return null;
        }
    }
}
