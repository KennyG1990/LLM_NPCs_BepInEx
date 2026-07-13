using System;
using System.Collections.Generic;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// WORK/LIFE BALANCE (Ken: exhausted settlers awake at 20h on default
    /// schedules). Applies the mood-aware "medieval 9-5" to every settler via
    /// the game's own HumanoidInstance.ChangeSchedule(hour, HourType)
    /// (decompiled :2452 — public, fires the schedule-changed notification):
    ///   22-06 Sleep(8h) | 06-08 Anything | 08-12 Work | 12-13 Anything |
    ///   13-17 Work | 17-20 Leisure (mood RECOVERY) | 20-22 Anything.
    /// Once per settler per session. LLM personalities may later adjust their
    /// own hours (night-owl researcher etc.) through the same call.
    /// </summary>
    public static class ScheduleRouter
    {
        public static string LastResult = "(idle)";
        private static readonly HashSet<string> _done = new HashSet<string>();
        public static void Reset() { _done.Clear(); LastResult = "(idle)"; }

        /// <summary>Ken's day: Work MOST of the day, RoleDuties some, Leisure
        /// evening, Sleep >=6h(we give 7), minimal Anything (meals only).
        /// `shift` = chronotype: night-owls live the same day, hours later.</summary>
        private static string HourPlan(int h, int shift)
        {
            h = ((h - shift) % 24 + 24) % 24;
            if (h >= 23 || h < 6) return "Sleep";      // 7h sleep
            if (h < 7) return "Anything";              // breakfast
            if (h < 12) return "Work";                 // 5h morning work
            if (h < 13) return "Anything";             // lunch
            if (h < 17) return "Work";                 // 4h afternoon work
            if (h < 19) return "RoleDuties";           // duty block (guard drills etc.)
            if (h < 22) return "Leisure";              // evening recovery
            return "Anything";                         // wind-down
        }

        /// <summary>Map plan names onto the game's REAL HourType members —
        /// Enum.Parse guessing failed silently live (Work slots stayed
        /// Anything). Discover members once, match by substring.</summary>
        private static readonly Dictionary<string, object> _hourVals = new Dictionary<string, object>();
        private static object HourVal(Type hourT, string plan)
        {
            if (_hourVals.Count == 0)
                foreach (var n in Enum.GetNames(hourT))
                {
                    var ln = n.ToLowerInvariant();
                    // CRASH LESSON (2026-07-12, native crash Crash_..._023620596):
                    // "RoleJob" matched the work||job branch and OVERWROTE Work —
                    // every Work hour became RoleJob; role-less settlers resolved
                    // it to None (-1) and the goal loop crash-spun. Rules:
                    //   * None is NEVER schedulable
                    //   * role-ish names are consumed BEFORE the work branch
                    //   * first match WINS (no overwrites)
                    if (ln == "none") continue;
                    if (ln.Contains("role") || ln.Contains("dut"))
                    { if (!_hourVals.ContainsKey("RoleDuties")) _hourVals["RoleDuties"] = Enum.Parse(hourT, n); continue; }
                    if (ln.Contains("sleep"))
                    { if (!_hourVals.ContainsKey("Sleep")) _hourVals["Sleep"] = Enum.Parse(hourT, n); }
                    else if (ln.Contains("work") || ln.Contains("job"))
                    { if (!_hourVals.ContainsKey("Work")) _hourVals["Work"] = Enum.Parse(hourT, n); }
                    else if (ln.Contains("leisure") || ln.Contains("joy") || ln.Contains("recreation"))
                    { if (!_hourVals.ContainsKey("Leisure")) _hourVals["Leisure"] = Enum.Parse(hourT, n); }
                    else if (ln.Contains("any") || ln.Contains("free"))
                    { if (!_hourVals.ContainsKey("Anything")) _hourVals["Anything"] = Enum.Parse(hourT, n); }
                }
            if (_hourVals.TryGetValue(plan, out var v)) return v;
            if (plan == "RoleDuties" && _hourVals.TryGetValue("Work", out var w)) return w;   // no role system yet -> work
            return _hourVals.TryGetValue("Anything", out var a) ? a : null;
        }

        public static string ApplyAll(List<Settler> settlers)
        {
            try
            {
                int applied = 0, tried = 0; string diag = "?";
                foreach (var s in settlers)
                {
                    if (s == null || s.gameObject == null) continue;
                    var id = GameBridge.GetSettlerId(s.gameObject) ?? s.gameObject.GetInstanceID().ToString();
                    if (_done.Contains(id)) continue;
                    tried++;
                    if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out var name, out var rc)) { diag = "identity"; continue; }
                    // resolve the HumanoidInstance model (hierarchy-walk, proven)
                    object model = HGet(rc, "HumanoidInstance") ?? rc;
                    var change = FindMethod(model, "ChangeSchedule");
                    if (change == null) { diag = "no ChangeSchedule on " + model.GetType().Name; continue; }
                    var hourT = change.GetParameters()[1].ParameterType;
                    // PER-NPC chronotype (Ken: independent schedules, night owls
                    // exist): perk-driven when readable, else stable id-hash
                    // variation of -1..+3h so the village doesn't move in
                    // lockstep. NightOwl perk -> +5h; SunSeeker -> -1h.
                    int shift = Math.Abs(id.GetHashCode()) % 4 - 1;
                    try
                    {
                        var perks = HGet(model, "Perks");
                        if (perks is System.Collections.IEnumerable pl)
                            foreach (var p in pl)
                            {
                                var pn = p?.ToString() ?? "";
                                if (pn.IndexOf("NightOwl", StringComparison.OrdinalIgnoreCase) >= 0) shift = 5;
                                else if (pn.IndexOf("SunSeeker", StringComparison.OrdinalIgnoreCase) >= 0) shift = -1;
                            }
                    }
                    catch { }
                    int hoursSet = 0;
                    for (int h = 0; h < 24; h++)
                    {
                        var val = HourVal(hourT, HourPlan(h, shift));
                        if (val == null) continue;
                        try { change.Invoke(model, new object[] { h, val }); hoursSet++; }
                        catch { }
                    }
                    if (hoursSet > 0) { _done.Add(id); applied++; }
                    else diag = "0 hours set";
                }
                if (applied > 0)
                {
                    LastResult = $"schedule: applied medieval-9to5 to {applied} settler(s)";
                    LLMNPCsPlugin.LogToFile("[ScheduleRouter] " + LastResult);
                }
                else if (tried > 0) LastResult = $"schedule: 0/{tried} — {diag}";
                return LastResult;
            }
            catch (Exception ex) { return LastResult = "schedule EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

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
        private static MethodInfo FindMethod(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (m.Name == name && m.GetParameters().Length == 2) return m;
            return null;
        }
    }
}
