using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// STOCKPILE HYGIENE v3 (Ken: "they put poop with their food, they don't
    /// differentiate what should be on a stockpile").
    ///
    /// v2 POST-MORTEM: it guessed group ids ("waste","carcass") that were never
    /// ground-truthed. HasGroupIdentifierEnabled(wrongId) returns false, the
    /// remover silently skipped every zone, and the telemetry reported "already
    /// clean" — a NO-OP that read as success for weeks. Same failure class as
    /// the HourType guessed-names bug. Rule: NEVER act on guessed ids.
    ///
    /// v3 is data-driven and self-verifying:
    ///  1. CLASSIFY from the live ResourceRepository: every Resource's real
    ///     Category flags + GroupIdentifier + id → FOOD / WASTE / OTHER sets.
    ///     The full taxonomy is dumped to validation/resource_taxonomy.txt.
    ///  2. ROLE ZONES by distance from home:
    ///       nearest  = PANTRY  (food only)          [3+ zones]
    ///       farthest = REFUSE  (waste/carcass only)
    ///       middle   = MATERIALS (everything except food+waste)
    ///     With exactly 2 zones: nearest = everything-except-waste, farthest = refuse.
    ///  3. VERIFY with the filter's own IsBlueprintAllowed(): spot-check that the
    ///     pantry accepts food and rejects waste, and the dump does the reverse.
    ///     The verdicts go into LastResult — a silent no-op is now impossible.
    /// </summary>
    public static class StockpileZoner
    {
        public static string LastResult = "(idle)";
        private static bool _dumped = false;
        private static bool _classified = false;
        private static int _appliedForZoneCount = -1;   // reapply when zone census changes

        private static readonly HashSet<string> Food = new HashSet<string>();
        private static readonly HashSet<string> Waste = new HashSet<string>();
        private static readonly HashSet<string> All = new HashSet<string>();
        private static string _sampleFood = null, _sampleWaste = null;

        public static void Reset()
        {
            _dumped = false; _classified = false; _appliedForZoneCount = -1;
            Food.Clear(); Waste.Clear(); All.Clear();
            _sampleFood = _sampleWaste = null;
            LastResult = "(idle)";
        }

        /// <summary>Build FOOD/WASTE/ALL id sets from the REAL repository data
        /// (Resource.Category flag names + GroupIdentifier + id — matched by
        /// substring against live strings, never against invented exact ids),
        /// and dump the whole taxonomy for review.</summary>
        private static bool Classify()
        {
            if (_classified) return true;
            try
            {
                var repo = RepoInstance("ResourceRepository");
                if (repo == null) { LastResult = "zoner: ResourceRepository null"; return false; }
                var rows = new System.Text.StringBuilder();
                var catHisto = new Dictionary<string, int>();
                int n = 0;
                // AGGREGATE across ALL repository fields (dedupe by id) — the
                // first live run stopped at the first non-empty field, which was
                // a 79-weapon sub-cache; the real resource set is ~2900 ids
                // spread across several caches. NEVER early-break a census.
                for (var ft = repo.GetType(); ft != null; ft = ft.BaseType)
                {
                    foreach (var f in ft.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        object val; try { val = f.GetValue(repo); } catch { continue; }
                        IEnumerable items = (val as IDictionary)?.Values ?? (val as IEnumerable);
                        if (items == null || val is string) continue;
                        foreach (var it in items)
                        {
                            if (it == null) continue;
                            string id = null;
                            try { id = it.GetType().GetMethod("GetID")?.Invoke(it, null) as string; } catch { }
                            if (id == null || All.Contains(id)) continue;
                            string cats = "";
                            try { cats = it.GetType().GetProperty("Category")?.GetValue(it, null)?.ToString() ?? ""; } catch { }
                            string gid = "";
                            try { gid = it.GetType().GetProperty("GroupIdentifier")?.GetValue(it, null) as string ?? ""; } catch { }
                            n++;
                            All.Add(id);
                            foreach (var c in cats.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                                catHisto[c] = (catHisto.TryGetValue(c, out int hc) ? hc : 0) + 1;
                            string hay = (cats + "|" + gid + "|" + id).ToLowerInvariant();
                            bool isWaste = hay.Contains("waste") || hay.Contains("carcass") || hay.Contains("manure")
                                        || hay.Contains("dung") || hay.Contains("poop") || hay.Contains("refuse");
                            bool isFood = !isWaste && (hay.Contains("food") || hay.Contains("meal") || hay.Contains("fruit")
                                        || hay.Contains("vegetable") || hay.Contains("crop") || hay.Contains("consumable"));
                            if (isWaste) { Waste.Add(id); if (_sampleWaste == null) _sampleWaste = id; }
                            else if (isFood) { Food.Add(id); if (_sampleFood == null) _sampleFood = id; }
                            rows.Append(id).Append(" | cat=").Append(cats).Append(" | group=").Append(gid)
                                .Append(" | class=").Append(isWaste ? "WASTE" : isFood ? "FOOD" : "other").Append('\n');
                        }
                    }
                }
                if (n == 0) { LastResult = "zoner: repository enumerated 0 resources"; return false; }
                if (!_dumped)
                {
                    var head = new System.Text.StringBuilder();
                    head.Append("RESOURCE TAXONOMY (live, ").Append(n).Append(" resources) — ")
                        .Append(DateTime.Now).Append('\n')
                        .Append("classified: FOOD=").Append(Food.Count).Append(" WASTE=").Append(Waste.Count)
                        .Append(" other=").Append(All.Count - Food.Count - Waste.Count).Append('\n')
                        .Append("CATEGORY HISTOGRAM:\n");
                    foreach (var kv in catHisto) head.Append("  ").Append(kv.Key).Append(" x").Append(kv.Value).Append('\n');
                    head.Append('\n').Append(rows);
                    System.IO.File.WriteAllText(
                        @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\resource_taxonomy.txt", head.ToString());
                    _dumped = true;
                }
                _classified = Waste.Count > 0 && Food.Count > 0;
                if (!_classified) LastResult = $"zoner: classification thin (food={Food.Count} waste={Waste.Count}) — see resource_taxonomy.txt";
                return _classified;
            }
            catch (Exception ex) { LastResult = "zoner classify EXC: " + (ex.InnerException?.Message ?? ex.Message); return false; }
        }

        /// <summary>Assign zone ROLES (pantry / materials / refuse) and verify.</summary>
        public static string Apply(int hx, int hz)
        {
            try
            {
                if (!Classify()) return LastResult;

                var mgrT = FindTypeByName("StockpileManager");
                object mgr = null;
                foreach (var c in UnityEngine.Object.FindObjectsOfType(mgrT)) { mgr = c; break; }
                var piles = mgr?.GetType().GetProperty("Stockpiles")?.GetValue(mgr, null) as IEnumerable;
                if (piles == null) return LastResult = "zoner: no stockpiles";

                var zones = new List<(object sp, int d)>();
                foreach (var sp in piles)
                {
                    if (sp == null) continue;
                    var start = sp.GetType().GetProperty("Start")?.GetValue(sp, null);
                    if (start == null) continue;
                    int sx, sz;
                    try
                    {
                        sx = Convert.ToInt32(start.GetType().GetField("x")?.GetValue(start) ?? 0);
                        sz = Convert.ToInt32(start.GetType().GetField("z")?.GetValue(start) ?? 0);
                    }
                    catch { continue; }
                    zones.Add((sp, Math.Abs(sx - hx) + Math.Abs(sz - hz)));
                }
                if (zones.Count < 2) return LastResult = "zoner: <2 zones, nothing to specialize";
                if (zones.Count == _appliedForZoneCount) return LastResult;   // already applied this census
                zones.Sort((a, b) => a.d.CompareTo(b.d));

                // INSIDE-HOUSE OVERRIDE (Ken, eyes-on 2026-07-12: animal corpses
                // on a stockpile INSIDE the house — the house was sited over old
                // zones, and "farthest" was still only d=7). Rules:
                //   * a zone inside the house footprint is ALWAYS a pantry
                //     (food indoors is coherent; corpses indoors never are)
                //   * REFUSE goes to the farthest zone OUTSIDE the footprint;
                //     if every zone is inside, no refuse role is assigned and we
                //     say so out loud instead of hiding corpses in the parlour.
                bool[] inHouse = new bool[zones.Count];
                for (int i = 0; i < zones.Count; i++)
                {
                    var st = zones[i].sp.GetType().GetProperty("Start")?.GetValue(zones[i].sp, null);
                    int zx = Convert.ToInt32(st?.GetType().GetField("x")?.GetValue(st) ?? -9999);
                    int zz = Convert.ToInt32(st?.GetType().GetField("z")?.GetValue(st) ?? -9999);
                    inHouse[i] = HouseBuilder.FootprintContains(zx, zz);
                }
                int refuseIdx = -1;
                for (int i = zones.Count - 1; i >= 0; i--)
                    if (!inHouse[i]) { refuseIdx = i; break; }

                for (int i = 0; i < zones.Count; i++)
                {
                    bool nearest = i == 0;
                    string role = i == refuseIdx ? "REFUSE"
                                : inHouse[i] ? "PANTRY"
                                : nearest && zones.Count >= 3 ? "PANTRY"
                                : "MATERIALS";
                    Func<string, bool> keep =
                        role == "REFUSE" ? (Func<string, bool>)(id => Waste.Contains(id))
                        : role == "PANTRY" ? (id => Food.Contains(id))
                        : zones.Count == 2 ? (id => !Waste.Contains(id))              // 2 zones: near keeps food+materials
                        : (id => !Waste.Contains(id) && !Food.Contains(id));          // 3+: middle = materials only
                    ApplyRole(zones[i].sp, keep);
                }
                if (refuseIdx < 0)
                    LLMNPCsPlugin.LogToFile("[StockpileZoner] WARNING: every zone sits inside the house footprint — no refuse zone assigned; corpses have nowhere sane to go until a zone exists outside");
                _appliedForZoneCount = zones.Count;

                // VERIFY with the game's own filter — no silent no-ops.
                string vNear = SpotCheck(zones[0].sp), vFar = SpotCheck(zones[zones.Count - 1].sp);
                LastResult = $"zoner v3: {zones.Count} zones roled (food={Food.Count} waste={Waste.Count} ids) | " +
                             $"nearest d={zones[0].d} [{vNear}] | dump d={zones[zones.Count - 1].d} [{vFar}]";
                LLMNPCsPlugin.LogToFile("[StockpileZoner] " + LastResult);
                return LastResult;
            }
            catch (Exception ex) { return LastResult = "zoner EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        /// <summary>Snapshot every stockpile zone's rect (x0,z0,x1,z1). House
        /// siting must treat zone cells as BLOCKED — the Dolgellau longhouse
        /// was placed over the colony's zones, which is how a corpse pile ended
        /// up indoors (Ken, eyes-on 2026-07-12). Called ONCE per site search;
        /// the per-cell check is then plain arithmetic (no reflection in the
        /// hot loop — that class of scan froze the main thread 4 times today).</summary>
        public static List<int[]> GetZoneRects()
        {
            var rects = new List<int[]>();
            try
            {
                var mgrT = FindTypeByName("StockpileManager");
                object mgr = null;
                foreach (var c in UnityEngine.Object.FindObjectsOfType(mgrT)) { mgr = c; break; }
                var piles = mgr?.GetType().GetProperty("Stockpiles")?.GetValue(mgr, null) as IEnumerable;
                if (piles == null) return rects;
                foreach (var sp in piles)
                {
                    if (sp == null) continue;
                    var st = sp.GetType().GetProperty("Start")?.GetValue(sp, null);
                    var en = sp.GetType().GetProperty("End")?.GetValue(sp, null);
                    if (st == null || en == null) continue;
                    int x0 = Convert.ToInt32(st.GetType().GetField("x")?.GetValue(st) ?? 0);
                    int z0 = Convert.ToInt32(st.GetType().GetField("z")?.GetValue(st) ?? 0);
                    int x1 = Convert.ToInt32(en.GetType().GetField("x")?.GetValue(en) ?? 0);
                    int z1 = Convert.ToInt32(en.GetType().GetField("z")?.GetValue(en) ?? 0);
                    rects.Add(new[] { Math.Min(x0, x1), Math.Min(z0, z1), Math.Max(x0, x1), Math.Max(z0, z1) });
                }
            }
            catch { }
            return rects;
        }

        public static bool CellInRects(List<int[]> rects, int x, int z)
        {
            foreach (var r in rects)
                if (x >= r[0] && z >= r[1] && x <= r[2] && z <= r[3]) return true;
            return false;
        }

        /// <summary>Remove every id the role does not keep (string overload of
        /// RemoveAllowedResource — verified in filter_groups.txt), re-add kept
        /// ids that were previously removed.</summary>
        private static void ApplyRole(object sp, Func<string, bool> keep)
        {
            var filter = sp.GetType().GetProperty("ResourcesFilter")?.GetValue(sp, null);
            if (filter == null) return;
            var ft = filter.GetType();
            var rem = ft.GetMethod("RemoveAllowedResource", new[] { typeof(string) });
            var add = ft.GetMethod("AddAllowedResource", new[] { typeof(string) });
            var isAllowed = ft.GetMethod("IsBlueprintAllowed", new[] { typeof(string) });
            if (rem == null || add == null) return;
            foreach (var id in All)
            {
                bool want = keep(id);
                bool have = true;
                try { have = isAllowed != null && isAllowed.Invoke(filter, new object[] { id }) is bool b && b; } catch { }
                try
                {
                    if (!want && have) rem.Invoke(filter, new object[] { id });
                    else if (want && !have) add.Invoke(filter, new object[] { id });
                }
                catch { }
            }
        }

        /// <summary>Ask the filter itself: does this zone accept our sample food
        /// and sample waste? Returns e.g. "cabbage=Y poop=n".</summary>
        private static string SpotCheck(object sp)
        {
            try
            {
                var filter = sp.GetType().GetProperty("ResourcesFilter")?.GetValue(sp, null);
                var isAllowed = filter?.GetType().GetMethod("IsBlueprintAllowed", new[] { typeof(string) });
                if (isAllowed == null) return "no IsBlueprintAllowed";
                Func<string, string> q = id => id == null ? "?"
                    : (isAllowed.Invoke(filter, new object[] { id }) is bool b && b) ? "Y" : "n";
                return $"{_sampleFood}={q(_sampleFood)} {_sampleWaste}={q(_sampleWaste)}";
            }
            catch { return "spotcheck err"; }
        }

        /// <summary>Legacy v1 surface dump — kept as a no-op shim (the surface
        /// lives in validation/filter_groups.txt; the taxonomy dump supersedes it).</summary>
        public static string Tick() => LastResult;

        private static object RepoInstance(string repoShortName)
        {
            var repo = FindTypeByName(repoShortName);
            if (repo == null) return null;
            object instance = null;
            for (var t = repo; t != null && instance == null; t = t.BaseType)
            {
                var p = t.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) { try { instance = p.GetValue(null, null); } catch { } }
            }
            return instance;
        }

        private static Type FindTypeByName(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }
    }
}
