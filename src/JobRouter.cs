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
        public static void Reset() { _routed.Clear(); _cookName = _foragerName = _boostedName = null; LastResult = "(idle)"; }

        private static readonly (string skill, int[] jobs)[] Map =
        {
            ("Intellectual", new[]{0x1000}), ("Construction", new[]{4}), ("Mining", new[]{2}),
            ("Botany", new[]{0x10,0x40,0x80}), ("Culinary", new[]{0x400}), ("Smithing", new[]{0x100}),
            ("Carpentry", new[]{0x200,8}), ("Marksman", new[]{0x20}), ("Medicine", new[]{0x4000}),
            ("Tailoring", new[]{0x800}), ("AnimalHandling", new[]{0x10000}), ("Art", new[]{0x20000}),
        };

        // CRISIS (#37): when the colony is starving, skill/passion routing YIELDS.
        // Everyone's SAFE food jobs go to priority 1 (harvest, plant, cook, haul,
        // animals, fish); research/art drop to 4. Applied once per crisis entry.
        private static bool _crisisApplied = false;
        // HUNTING (0x20) is NOT here (Ken, live 2026-07-13: low-skill settlers
        // forced to hunt were engaging boars and DYING). Hunting is gated to
        // skilled marksmen separately — see ApplyHuntGate.
        private static readonly int[] SafeFoodJobs = { 0x10, 0x40, 0x400, 1, 0x10000, 0x100000 }; // Harvesting,PlantCropfields,Cooking,Hauling,Animal,Fishing
        private static readonly int[] CrisisSuspendJobs = { 0x1000, 0x20000 };                    // Research, Art
        private const int HuntJob = 0x20;
        // Marksman below this NEVER hunts (a competent colony hunts with its
        // skilled bowman; the rest do safe food work). Prevents boar deaths.
        private const int HuntGateLevel = 6;

        public static string CrisisRouteAll(List<Settler> settlers)
        {
            try
            {
                if (_crisisApplied) return LastResult;
                var jobTypeT = FindType("NSMedieval.State.WorkerJobs.JobType");
                if (jobTypeT == null) return LastResult = "jobs: no JobType";
                int applied = 0;
                foreach (var s in settlers)
                {
                    if (s == null || s.gameObject == null) continue;
                    if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out _, out var rc)) continue;
                    var agent = GameBridge.GetGoapAgent(rc);
                    var change = agent?.GetType().GetMethod("ChangeJobPriority", BindingFlags.Public | BindingFlags.Instance);
                    if (change == null) continue;
                    foreach (var j in SafeFoodJobs)
                        try { change.Invoke(agent, new[] { Enum.ToObject(jobTypeT, j), (object)1 }); } catch { }
                    ApplyHuntGate(change, agent, jobTypeT, rc, 1);   // hunting only for skilled marksmen
                    foreach (var j in CrisisSuspendJobs)
                        try { change.Invoke(agent, new[] { Enum.ToObject(jobTypeT, j), (object)4 }); } catch { }
                    applied++;
                }
                if (applied > 0)
                {
                    _crisisApplied = true;
                    _routed.Clear();   // normal routing re-applies after the crisis
                    LastResult = $"jobs: ⚠CRISIS — {applied} settler(s) to SAFE food work (harvest/plant/cook/haul/animals/fish prio 1; hunting skilled-marksmen only; research/art suspended)";
                    LLMNPCsPlugin.LogToFile("[JobRouter] " + LastResult);
                }
                return LastResult;
            }
            catch (Exception ex) { return LastResult = "crisis jobs EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        /// <summary>Crisis over: allow skill routing to reassert.</summary>
        public static void ExitCrisis() { _crisisApplied = false; }

        // FOOD WARNING TIER (Ken's playbook §2: stock < 12×3×settlers → raise
        // farming/hunting BEFORE crisis). Gentler than crisis: food jobs to
        // priority 2, NOTHING suspended — research and art continue.
        private static bool _foodPressureApplied = false;

        public static string FoodPressureAll(List<Settler> settlers)
        {
            try
            {
                if (_foodPressureApplied) return LastResult;
                var jobTypeT = FindType("NSMedieval.State.WorkerJobs.JobType");
                if (jobTypeT == null) return LastResult;
                int applied = 0;
                foreach (var s in settlers)
                {
                    if (s == null || s.gameObject == null) continue;
                    if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out _, out var rc)) continue;
                    var agent = GameBridge.GetGoapAgent(rc);
                    var change = agent?.GetType().GetMethod("ChangeJobPriority", BindingFlags.Public | BindingFlags.Instance);
                    if (change == null) continue;
                    foreach (var j in SafeFoodJobs)
                        try { change.Invoke(agent, new[] { Enum.ToObject(jobTypeT, j), (object)2 }); } catch { }
                    ApplyHuntGate(change, agent, jobTypeT, rc, 2);   // hunting only for skilled marksmen
                    applied++;
                }
                if (applied > 0)
                {
                    _foodPressureApplied = true;
                    LastResult = $"jobs: FOOD WARNING (3-day buffer breached) — safe food jobs to prio 2 for {applied} settler(s); hunting skilled-marksmen only; research/art continue";
                    LLMNPCsPlugin.LogToFile("[JobRouter] " + LastResult);
                }
                return LastResult;
            }
            catch (Exception ex) { return LastResult = "food pressure EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        public static void ExitFoodPressure() { _foodPressureApplied = false; }

        // ── BUILD PRESSURE (coherence fix, eyes-on-screen 2026-07-11) ──
        // The fletcher blueprint sat "all buildable" for HOURS while the JOBS
        // grid showed Construct at 3/4/4 for everyone (Giles Fish=1 by the
        // river, Linyeve Carp=1) — the build job could mathematically never
        // win. Needs beat preferences: while blueprints are pending ≥3 ticks,
        // the best-Construction-skill settler gets Construct prio 1; released
        // back to 3 when the queue clears.
        public static string PressureResult = "(idle)";
        private const long JobTypeConstruction = 4;
        private static int _pendingTicks = 0;
        private static string _boostedName = null;

        public static void EnsureBuildPressure(List<Settler> settlers, int pendingBlueprints)
        {
            try
            {
                var jobTypeT = FindType("NSMedieval.State.WorkerJobs.JobType");
                if (jobTypeT == null || settlers == null) return;

                if (pendingBlueprints <= 0)
                {
                    _pendingTicks = 0;
                    if (_boostedName != null)
                    {
                        SetConstructPrio(settlers, _boostedName, jobTypeT, 3);
                        LLMNPCsPlugin.LogToFile($"[JobRouter] build-pressure released: {_boostedName} Construct→3 (no blueprints pending)");
                        PressureResult = $"released ({_boostedName} back to 3)";
                        _boostedName = null;
                    }
                    else PressureResult = "(idle)";
                    return;
                }

                _pendingTicks++;
                if (_boostedName != null) { PressureResult = $"{_boostedName} Construct=1 ({pendingBlueprints} pending)"; return; }
                if (_pendingTicks < 3) { PressureResult = $"watching ({pendingBlueprints} pending, tick {_pendingTicks}/3)"; return; }

                // pick the best-Construction-skill settler
                string bestName = null; int bestLvl = -1;
                foreach (var s in settlers)
                {
                    if (s == null || s.gameObject == null) continue;
                    if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out var name, out var rc)) continue;
                    int lvl = ReadConstructionLevel(rc);
                    if (lvl > bestLvl) { bestLvl = lvl; bestName = name; }
                }
                if (bestName == null) { PressureResult = "no settler resolvable"; return; }
                if (SetConstructPrio(settlers, bestName, jobTypeT, 1))
                {
                    _boostedName = bestName;
                    PressureResult = $"{bestName} Construct→1 ({pendingBlueprints} pending, Construction lvl {bestLvl})";
                    LLMNPCsPlugin.LogToFile($"[JobRouter] build-pressure: {PressureResult}");
                }
                else PressureResult = "ChangeJobPriority failed";
            }
            catch (Exception ex) { PressureResult = "EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        private static bool SetConstructPrio(List<Settler> settlers, string name, Type jobTypeT, int prio)
            => SetJobPrio(settlers, name, jobTypeT, (int)JobTypeConstruction, prio);

        private static bool SetJobPrio(List<Settler> settlers, string name, Type jobTypeT, int job, int prio)
        {
            foreach (var s in settlers)
            {
                if (s == null || s.gameObject == null) continue;
                if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out var n, out var rc) || n != name) continue;
                var agent = GameBridge.GetGoapAgent(rc);
                var change = agent?.GetType().GetMethod("ChangeJobPriority", BindingFlags.Public | BindingFlags.Instance);
                if (change == null) return false;
                try { change.Invoke(agent, new[] { Enum.ToObject(jobTypeT, job), (object)prio }); return true; }
                catch { return false; }
            }
            return false;
        }

        /// <summary>Best-skill settler for `skillId`, excluding names in `taken`.
        /// Returns null if none resolvable.</summary>
        private static string BestSkilled(List<Settler> settlers, string skillId, HashSet<string> taken)
        {
            string best = null; int bestLvl = -1;
            foreach (var s in settlers)
            {
                if (s == null || s.gameObject == null) continue;
                if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out var name, out var rc)) continue;
                if (taken.Contains(name)) continue;
                int lvl = ReadSkillLevel(rc, skillId);
                if (lvl > bestLvl) { bestLvl = lvl; best = name; }
            }
            return best;
        }

        // ── LABOR DIVISION (Ken, live 2026-07-13: "everyone is hauling... we have
        // to split up the workload"). Pin DISTINCT settlers to the essential
        // concurrent duties so the colony runs several tasks in parallel instead
        // of all converging on hauling. Builder is pinned by EnsureBuildPressure;
        // this adds a distinct COOK and FORAGER. Leftover settlers keep skill
        // routing (hauling as the game needs it). Re-applied only when the
        // assignment changes, so no per-tick thrash.
        private static string _cookName, _foragerName;

        public static string DivideLabor(List<Settler> settlers)
        {
            try
            {
                var jobTypeT = FindType("NSMedieval.State.WorkerJobs.JobType");
                if (jobTypeT == null || settlers == null || settlers.Count < 3) return LastResult;
                var taken = new HashSet<string>(StringComparer.Ordinal);
                if (_boostedName != null) taken.Add(_boostedName);   // the pinned builder
                string cook = BestSkilled(settlers, "Culinary", taken);
                if (cook != null) { taken.Add(cook); if (cook != _cookName) { SetJobPrio(settlers, cook, jobTypeT, 0x400, 1); _cookName = cook; } }
                string forager = BestSkilled(settlers, "Botany", taken);
                if (forager != null) { taken.Add(forager); if (forager != _foragerName) { SetJobPrio(settlers, forager, jobTypeT, 0x10, 1); _foragerName = forager; } }
                return LastResult = $"labor split: builder={_boostedName ?? "-"} cook={cook ?? "-"} forager={forager ?? "-"} (rest: skill/haul)";
            }
            catch (Exception ex) { return LastResult = "labor split EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        /// <summary>Set the Hunting job for one settler: priority `p` ONLY if their
        /// Marksman skill clears HuntGateLevel; otherwise Hunting = 4 (never), so
        /// low-skill settlers are never sent at wild game. The colony's skilled
        /// bowman hunts; everyone else does safe food work.</summary>
        private static void ApplyHuntGate(MethodInfo change, object agent, Type jobTypeT, object rc, int p)
        {
            int marks = ReadSkillLevel(rc, "Marksman");
            int prio = marks >= HuntGateLevel ? p : 4;
            try { change.Invoke(agent, new[] { Enum.ToObject(jobTypeT, HuntJob), (object)prio }); } catch { }
        }

        private static int ReadConstructionLevel(object rc) => ReadSkillLevel(rc, "Construction");

        private static int ReadSkillLevel(object rc, string skillId)
        {
            try
            {
                object HG(object o, string pn)
                {
                    if (o == null) return null;
                    for (var t = o.GetType(); t != null; t = t.BaseType)
                    {
                        var p = t.GetProperty(pn, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        if (p != null) { try { var v = p.GetValue(o, null); if (v != null) return v; } catch { } }
                        var f = t.GetField(pn, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        if (f != null) { try { var v = f.GetValue(o); if (v != null) return v; } catch { } }
                    }
                    return null;
                }
                var model = HG(rc, "HumanoidInstance") ?? rc;
                var owner = HG(model, "Skills") ?? HG(model, "WorkerSkills");
                var list = owner != null ? HG(owner, "Skills") as IEnumerable : null;
                if (list == null) return 0;
                foreach (var sk in list)
                {
                    if (sk == null) continue;
                    if (HG(sk, "Id")?.ToString()?.Replace(" ", "") != skillId) continue;
                    try { return Convert.ToInt32(HG(sk, "Level") ?? 0); } catch { return 0; }
                }
            }
            catch { }
            return 0;
        }

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
                // HUNT SAFETY GATE (Ken, live 2026-07-13): a passion for hunting
                // does NOT make a low-skill settler survive a boar. Marksman below
                // HuntGateLevel never gets Hunting elevated — forced to 4 (never),
                // so only the skilled bowman hunts. Deaths came from exactly this.
                if (skill == "Marksman" && v.lvl < HuntGateLevel) prio = 4;
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
