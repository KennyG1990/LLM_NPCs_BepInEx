using System;
using System.Collections.Generic;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// RELEASE-BLOCKER FIX (save bloat / duplicate structures across reloads).
    ///
    /// The builder modules tracked "already built" in per-process statics that
    /// reset on every save load, so each reload re-placed a whole house, another
    /// stockpile, etc. — unbounded save growth that eventually killed the Libury
    /// save. This module gives the strategic layer:
    ///
    ///   1. WORLD-CHANGE DETECTION — notices when a (re)load happened (the
    ///      VillageManager.ActiveVillage instance changed) and resets ALL
    ///      per-session builder state before the next tick acts on stale flags.
    ///   2. A PER-SAVE SIDECAR — persists the mod's own plan (home waypoint,
    ///      house site, roof progress, completion) keyed by the game's save id,
    ///      so after a reload the planner RE-ADOPTS its existing house instead
    ///      of finding a fresh clear spot next to the old one.
    ///   3. Nothing is trusted blindly: sidecar claims are cross-checked against
    ///      the loaded save's actual buildings per-cell (HouseBuilder verifies at
    ///      adoption; every placement step checks the world first), so a
    ///      save-scummed / rolled-back save can never desync us into duplicating.
    ///
    /// Failure direction is deliberately SAFE: when in doubt we SKIP building
    /// (worst case: a missing roof), never re-place (worst case before: a
    /// corrupted save).
    /// </summary>
    public static class BuiltState
    {
        private const string Dir = @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\built_state";

        // Identity of the currently-loaded world. ActiveVillage is a new object
        // every load, so reference identity is a reliable "a load happened" signal.
        private static WeakReference _villageRef = new WeakReference(null);
        private static string _saveId = null;
        public static string LastResult = "(none)";

        // ── Sidecar state (persisted per save id) ──────────────────────────────
        private static Dictionary<string, string> _kv = new Dictionary<string, string>();

        /// <summary>Call FIRST every ColonyBuilder tick. Detects that a different
        /// world is loaded (new game / reload) and resets all per-session builder
        /// state, then loads this save's sidecar. Returns true when a reset fired.</summary>
        public static bool OnTick()
        {
            object village = GetActiveVillage();
            if (village == null) return false;               // no world yet — nothing to do
            if (ReferenceEquals(_villageRef.Target, village)) return false; // same world

            _villageRef = new WeakReference(village);
            _saveId = MemoryManager.GetActiveSaveId();
            ResetSession();
            Load();
            LastResult = $"world (re)load detected, session reset, sidecar '{_saveId}' loaded ({_kv.Count} keys)";
            LLMNPCsPlugin.LogToFile($"[BuiltState] {LastResult}");
            return true;
        }

        /// <summary>Reset every per-session static in the strategic layer.</summary>
        private static void ResetSession()
        {
            _kv.Clear();
            ColonyBuilder.Reset();
            HouseBuilder.Reset();
            ColonyHome.Reset();
            WoodGatherer.Reset();
            FoodGatherer.Reset();
            CellarBuilder.Reset();
            ResearchPlanner.Reset();
            ProductionPlanner.Reset();
            FarmPlanner.Reset();
            ResearchGate.Reset();   // clear the per-id unlock cache on a fresh colony / save load
            JobRouter.Reset();
            StockpileZoner.Reset();
            ScheduleRouter.Reset();
            EventInteractor.Reset();
            DeathChronicler.Reset();
            WeaponChain.Reset();
            PlanManager.Reset();
            HouseArchitect.Reset();
            VillageLayout.Reset();
            DefenseBuilder.Reset();
            GameTruthBridge.Reset();
            PlanExecutor.Reset();
            // CROSS-VILLAGE STALENESS (caught live 2026-07-12: Ken's new game
            // showed Dolgellau's worldmap stats verbatim — the site planner
            // scored the NEW map against the OLD village's terrain and reported
            // "NO site fits a 12x12 pad" on unscanned ground). These three keep
            // once-per-session caches and had no Reset():
            WorldMap.ResetScanState();         // → fresh fast region scan on the new map
            WorldSense.LastGrid = "";          // → re-rasterize home region
            HouseSitePlanner.Done = false;     // → fresh leader siteplan…
            HouseSitePlanner.HasSite = false;  //   …against the FRESH worldmap
        }

        private static object GetActiveVillage()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = asm.GetType("NSMedieval.Village.VillageManager", false); } catch { }
                    if (t == null) continue;
                    return t.GetProperty("ActiveVillage",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
                }
            }
            catch { }
            return null;
        }

        // ── Typed accessors used by the builders ──────────────────────────────

        public static bool TryGetHome(out int x, out int y, out int z)
        {
            x = y = z = 0;
            return TryInt("home.x", out x) && TryInt("home.y", out y) && TryInt("home.z", out z);
        }

        public static void SaveHome(int x, int y, int z)
        {
            _kv["home.x"] = x.ToString(); _kv["home.y"] = y.ToString(); _kv["home.z"] = z.ToString();
            Persist();
        }

        public static bool TryGetHousePlan(out int ox, out int oz, out int ay)
        {
            ox = oz = ay = 0;
            return TryInt("house.ox", out ox) && TryInt("house.oz", out oz) && TryInt("house.ay", out ay);
        }

        public static void SaveHousePlan(int ox, int oz, int ay)
        {
            _kv["house.ox"] = ox.ToString(); _kv["house.oz"] = oz.ToString(); _kv["house.ay"] = ay.ToString();
            Persist();
        }

        public static void ClearHousePlan()
        {
            _kv.Remove("house.ox"); _kv.Remove("house.oz"); _kv.Remove("house.ay");
            _kv.Remove("house.roofs"); _kv.Remove("house.complete");
            _kv.Remove("house.ver"); _kv.Remove("house.seed"); _kv.Remove("house.pop");
            _kv.Remove("house.program"); _kv.Remove("house.pw"); _kv.Remove("house.ph"); _kv.Remove("house.pf");
            Persist();
        }

        /// <summary>#31 Packer plan versioning. Version 1 (or absent) = the legacy
        /// fixed 4x7 two-room layout; version 2 = the corridor-spine generator
        /// (seed+pop regenerate the identical layout on re-adoption).</summary>
        public static int HousePlanVersion
        {
            get { return TryInt("house.ver", out var v) ? v : 1; }
            set { _kv["house.ver"] = value.ToString(); Persist(); }
        }

        public static bool TryGetHousePlanV2(out int seed, out int pop)
        {
            seed = pop = 0;
            return TryInt("house.seed", out seed) && TryInt("house.pop", out pop);
        }

        public static void SaveHousePlanV2(int ox, int oz, int ay, int seed, int pop)
        {
            _kv["house.ox"] = ox.ToString(); _kv["house.oz"] = oz.ToString(); _kv["house.ay"] = ay.ToString();
            _kv["house.ver"] = "2"; _kv["house.seed"] = seed.ToString(); _kv["house.pop"] = pop.ToString();
            Persist();
        }

        /// <summary>Unit C: forge-plan house (version 3) — arbitrary rect adopted
        /// from VillageForge's plan.json; w/h persisted so re-adoption regenerates
        /// the identical shell layout.</summary>
        public static bool TryGetHousePlanV3(out int w, out int h)
        {
            w = h = 0;
            return TryInt("house.pw", out w) && TryInt("house.ph", out h);
        }

        /// <summary>Forge house floors (multi-story fidelity); 1 if absent.</summary>
        public static int HousePlanFloors => TryInt("house.pf", out var f) && f >= 1 ? f : 1;

        public static void SaveHousePlanV3(int ox, int oz, int ay, int w, int h, int floors = 1)
        {
            _kv["house.ox"] = ox.ToString(); _kv["house.oz"] = oz.ToString(); _kv["house.ay"] = ay.ToString();
            _kv["house.ver"] = "3"; _kv["house.pw"] = w.ToString(); _kv["house.ph"] = h.ToString();
            _kv["house.pf"] = Math.Max(1, floors).ToString();
            Persist();
        }

        /// <summary>Unit C: hash of the forge plan file already executed to
        /// completion for this save — a finished plan never restarts.</summary>
        public static string PlanExecDoneHash
        {
            get { return _kv.TryGetValue("planexec.done", out var v) ? v : ""; }
            set { _kv["planexec.done"] = value ?? ""; Persist(); }
        }

        /// <summary>#31 slice B: the ARCHITECT's room program for the CURRENT
        /// building ("name:width,name:width"; empty = deterministic defaults).
        /// Persisted so reload re-adoption regenerates the identical design.</summary>
        public static string HouseProgram
        {
            get { return _kv.TryGetValue("house.program", out var v) ? v : ""; }
            set { _kv["house.program"] = value ?? ""; Persist(); }
        }

        /// <summary>Village building queue (architect strategy): entries
        /// "label|program" separated by ';'. Index advances as buildings complete.</summary>
        public static string VillageQueue
        {
            get { return _kv.TryGetValue("village.queue", out var v) ? v : ""; }
            set { _kv["village.queue"] = value ?? ""; Persist(); }
        }
        public static int VillageQueueIndex
        {
            get { return TryInt("village.qidx", out var n) ? n : 0; }
            set { _kv["village.qidx"] = value.ToString(); Persist(); }
        }

        /// <summary>The village CENTER (the plaza) — fixed once per save at the
        /// leader's sited plot; every building slots around it.</summary>
        public static bool TryGetVillageCenter(out int x, out int y, out int z)
        {
            x = y = z = 0;
            return TryInt("village.cx", out x) && TryInt("village.cy", out y) && TryInt("village.cz", out z);
        }
        public static void SaveVillageCenter(int x, int y, int z)
        {
            _kv["village.cx"] = x.ToString(); _kv["village.cy"] = y.ToString(); _kv["village.cz"] = z.ToString();
            Persist();
        }

        /// <summary>Roof pieces successfully invoked so far. Roofs are components
        /// (not buildings) with no cheap per-cell existence query yet, so progress
        /// is persisted here — the safe failure is a missing roof, never a stack
        /// of duplicate roof components.</summary>
        public static int RoofsPlaced
        {
            get { return TryInt("house.roofs", out var n) ? n : 0; }
            set { _kv["house.roofs"] = value.ToString(); Persist(); }
        }

        /// <summary>True when this building id was observed NO-SKILLED-WORKER —
        /// stop re-placing what the crew cannot build (blueprint churn fix).</summary>
        public static bool SkillBlocked(string id)
        { return _kv.TryGetValue("skillblocked." + id, out var v) && v == "1"; }
        public static void SetSkillBlocked(string id)
        { _kv["skillblocked." + id] = "1"; Persist(); }

        /// <summary>One 4x4 crop field per save (sustainable food).</summary>
        public static bool FarmPlaced
        {
            get { return _kv.TryGetValue("farm.placed", out var v) && v == "1"; }
            set { _kv["farm.placed"] = value ? "1" : "0"; Persist(); }
        }

        /// <summary>One cellar dig designated per save (underground food storage).</summary>
        public static bool CellarMarked
        {
            get { return _kv.TryGetValue("cellar.marked", out var v) && v == "1"; }
            set { _kv["cellar.marked"] = value ? "1" : "0"; Persist(); }
        }

        public static bool HouseComplete
        {
            get { return _kv.TryGetValue("house.complete", out var v) && v == "1"; }
            set { _kv["house.complete"] = value ? "1" : "0"; Persist(); }
        }

        // ── Persistence (simple key=value lines; no JSON dependency) ──────────

        private static bool TryInt(string key, out int val)
        {
            val = 0;
            return _kv.TryGetValue(key, out var s) && int.TryParse(s, out val);
        }

        private static string FilePath()
        {
            var id = _saveId ?? MemoryManager.GetActiveSaveId() ?? "default_save";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return System.IO.Path.Combine(Dir, id + ".txt");
        }

        private static void Persist()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Dir);
                var sb = new System.Text.StringBuilder();
                foreach (var kv in _kv) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                System.IO.File.WriteAllText(FilePath(), sb.ToString());
            }
            catch (Exception ex) { LLMNPCsPlugin.LogToFile("[BuiltState] persist EXC: " + ex.Message); }
        }

        private static void Load()
        {
            try
            {
                _kv.Clear();
                var path = FilePath();
                if (!System.IO.File.Exists(path)) return;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) _kv[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch (Exception ex) { LLMNPCsPlugin.LogToFile("[BuiltState] load EXC: " + ex.Message); }
        }
    }
}
