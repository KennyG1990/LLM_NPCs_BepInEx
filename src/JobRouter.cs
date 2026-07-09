using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// COMPARATIVE ADVANTAGE (Ken: "Emmota Int 26 should research, not haul;
    /// differentiate jobs people love from jobs they hate"). Per settler, read
    /// WorkerSkills (Level + GoalPreferenceLevel: passionate = bonus XP + mood,
    /// resentful = 0.2x XP + mood penalty) and set the game's OWN per-settler
    /// job priorities via WorkerGoapAgent.ChangeJobPriority(JobType, priority)
    /// (proven path: TryAssignConstructionPriority). Priority 1 = do first,
    /// 4 = last resort. Runs once per session per settler.
    ///
    /// SkillType -> JobType mapping (JobType decompiled, flags):
    ///   Intellectual->Research; Construction->Construction; Mining->Mining;
    ///   Botany->Harvesting+PlantCropfields+PlantCutting; Culinary->Cooking;
    ///   Smithing->Smithing; Carpentry->Carpentry+Crafting; Marksman->Hunting;
    ///   Medicine->TendWounds; Tailoring->Tailoring; AnimalHandling->Animal;
    ///   Art->Art.
    /// </summary>
    public static class JobRouter
    {
        public static string LastResult = "(idle)";
        private static string _lastDiag = "?";
        private static readonly HashSet<string> _routed = new HashSet<string>();
        public static void Reset() { _routed.Clear(); LastResult = "(idle)"; }

        private static readonly (string skill, int[] jobs)[] Map =
        {
            ("Intellectual", new[]{0x1000}), ("Construction", new[]{4}), ("Mining", new[]{2}),
            ("Botany", new[]{0x10,0x40,0x80}), ("Culinary", new[]{0x400}), ("Smithing", new[]{0x100}),
            ("Carpentry", new[]{0x200,8}), ("Marksman", new[]{0x20}), ("Medicine", new[]{0x4000}),
            ("Tailoring", new[]{0x800}), ("AnimalHandling", new[]{0x10000}), ("Art", new[]{0x20000}),
        };

        public static string RouteAll(List<Settler> settlers)
        {
            try
            {
                var jobTypeT = FindType("NSMedieval.State.WorkerJobs.JobType");
                if (jobTypeT == null) return LastResult = "jobs: no JobType";
                int routed = 0, tried = 0; var sb = new System.Text.StringBuilder();
                foreach (var s in settlers)
                {
                    if (s == null || s.gameObject == null) continue;
                    var id = GameBridge.GetSettlerId(s.gameObject) ?? s.gameObject.GetInstanceID().ToString();
                    if (_routed.Contains(id)) continue;
                    tried++;
                    if (!RouteOne(s, jobTypeT, sb)) continue;
                    _routed.Add(id); routed++;
                }
                if (routed > 0)
                {
                    LastResult = $"jobs: routed {routed} settler(s) by skill+passion — {sb}";
                    LLMNPCsPlugin.LogToFile("[JobRouter] " + LastResult);
                }
                else if (tried > 0)
                    LastResult = $"jobs: 0/{tried} routed — diag: {_lastDiag}";   // surface WHY
                return LastResult;
            }
            catch (Exception ex) { return LastResult = "jobs EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        private static bool RouteOne(Settler s, Type jobTypeT, System.Text.StringBuilder sb)
        {
            // runtime component -> goap agent -> ChangeJobPriority
            if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out var name, out var rc)) { _lastDiag = "identity failed"; return false; }
            var agent = GameBridge.GetGoapAgent(rc);
            var change = agent?.GetType().GetMethod("ChangeJobPriority", BindingFlags.Public | BindingFlags.Instance);
            if (change == null) { _lastDiag = agent == null ? "no goap agent" : "no ChangeJobPriority"; return false; }

            // Skills live on the MODEL (HumanoidInstance), not the WorkerView
            // component (live diag). Resolve model first, hierarchy-walk both.
            object HGet(object o, string pn2)
            {
                if (o == null) return null;
                for (var t2 = o.GetType(); t2 != null; t2 = t2.BaseType)
                {
                    var p2 = t2.GetProperty(pn2, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (p2 != null) { try { var v = p2.GetValue(o, null); if (v != null) return v; } catch { } }
                    var f2 = t2.GetField(pn2, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (f2 != null) { try { var v = f2.GetValue(o); if (v != null) return v; } catch { } }
                }
                return null;
            }
            var model = HGet(rc, "HumanoidInstance") ?? rc as object;
            object skills = null;
            foreach (var owner in new[] { model, rc })
            {
                if (owner == null) continue;
                skills = HGet(owner, "Skills") ?? HGet(owner, "WorkerSkills") ?? HGet(owner, "workerSkills");
                if (skills != null && skills.GetType().Name != "WorkerSkills" && HGet(skills, "Skills") == null) skills = null;
                if (skills != null) break;
            }
            var list = skills?.GetType().GetProperty("Skills")?.GetValue(skills, null) as IEnumerable;
            if (list == null) { _lastDiag = skills == null ? "no WorkerSkills on " + rc.GetType().Name : "no Skills list"; return false; }

            // skill name -> (level, pref)
            var have = new Dictionary<string, (int lvl, int pref)>(StringComparer.OrdinalIgnoreCase);
            foreach (var sk in list)
            {
                if (sk == null) continue;
                var t = sk.GetType();
                string sn = null; int lvl = 0, pref = 0;
                try { sn = t.GetProperty("Id")?.GetValue(sk, null)?.ToString(); } catch { }
                try { lvl = Convert.ToInt32(t.GetProperty("Level")?.GetValue(sk, null) ?? 0); } catch { }
                try { pref = Convert.ToInt32(t.GetMethod("GetGoalPreferenceLevel")?.Invoke(sk, null) ?? 0); } catch { }
                if (sn != null) have[sn.Replace(" ", "")] = (lvl, pref);
            }
            if (have.Count == 0) return false;

            int applied = 0; string best = "?"; int bestLvl = -1;
            foreach (var (skill, jobs) in Map)
            {
                if (!have.TryGetValue(skill, out var v)) continue;
                // 1=first (high skill or passionate), 2=good, 3=default, 4=hates it
                int prio = v.pref < 0 ? 4 : (v.lvl >= 10 || v.pref >= 2) ? 1 : (v.lvl >= 5 || v.pref == 1) ? 2 : 3;
                foreach (var j in jobs)
                {
                    try { change.Invoke(agent, new[] { Enum.ToObject(jobTypeT, j), (object)prio }); applied++; }
                    catch { }
                }
                if (v.lvl > bestLvl) { bestLvl = v.lvl; best = skill; }
            }
            if (applied > 0) sb.Append($"{name}:{best}(lvl{bestLvl}) ");
            return applied > 0;
        }

        private static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { var t = a.GetType(full, false); if (t != null) return t; } catch { } }
            return null;
        }
    }
}
