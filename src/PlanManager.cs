using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// #17 THE PLANNER, slice 1 (spec: BACKLOG 2026-07-11 ~20:55). The LLM is
    /// the colony's strategist: given the SAME telemetry snapshot a human
    /// operator reads, it writes an immediate plan (WHAT/WHY per step) against
    /// a CONSTRAINED VERB MENU; a deterministic executor validates and runs
    /// one step at a time on proven actuators, reporting per-step status to
    /// the dashboard (gm_plans: POST /api/plan, /api/plan/step_status).
    ///
    /// Discipline:
    ///  - The deterministic survival floor (build priorities, crisis reactor)
    ///    is NEVER overridden by a plan — steps run after it each tick.
    ///  - Unknown verbs/args → step "rejected", kept for the audit trail.
    ///  - LLM budget-gated (task "planner" = critical lane); no plan is
    ///    better than a guessed plan.
    /// </summary>
    public static class PlanManager
    {
        public static string LastResult = "(idle)";

        private sealed class Step
        {
            public int Seq;
            public string What = "", Verb = "", Args = "", Why = "";
            public string Status = "pending";   // pending|active|done|failed|rejected
            public int ServerStepId = -1;
        }

        private static readonly List<Step> _steps = new List<Step>();
        private static string _rationale = "";
        private static long _planId = -1;
        private static bool _requested, _generating;
        private static string _rawPlanJson;              // set by async LLM, consumed on main thread
        private static DateTime _lastPlanAt = DateTime.MinValue;
        private static DateTime _lastAttemptAt = DateTime.MinValue;   // HARD cooldown — a failed
        // generation must NEVER retry-loop (4 critical calls burned in 2 min on 2026-07-11)
        private const int AttemptCooldownMinutes = 5;
        private static bool _wasCrisis;
        private static int _tickPacer;

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(3000) };
        private const string BaseUrl = "http://127.0.0.1:8714";
        private static readonly string[] VerbMenu = { "produce", "build", "focus_job" };
        // one-per-colony stations: a plan asking for an existing one is satisfied by census
        private static readonly string[] _singletonBuildings = { "basic_research_table", "fletchers_table", "camp_fire", "research_table" };
        // furniture/structure the HOUSING system owns — never generic-spiral these
        private static readonly string[] _housingManagedIds = { "hay_sleeping_spot", "wooden_bed", "wood_door", "wood_wall_element", "wood_roof_whole" };

        public static void Reset()
        {
            _steps.Clear(); _rationale = ""; _planId = -1;
            _requested = _generating = false; _rawPlanJson = null;
            _lastPlanAt = DateTime.MinValue; _wasCrisis = false;
            LastResult = "(idle)";
        }

        public static void Tick(List<Settler> settlers)
        {
            try
            {
                if (settlers == null || settlers.Count == 0) { LastResult = "(no settlers)"; return; }

                // Replan triggers: no plan yet; crisis just entered; all steps terminal (≥10 min old).
                bool crisis = ColonyAlerts.LastNutrition >= 0 && ColonyAlerts.LastNutrition < settlers.Count * 6;
                bool crisisEntered = crisis && !_wasCrisis;
                _wasCrisis = crisis;
                bool exhausted = _steps.Count > 0 && _steps.TrueForAll(s => s.Status != "pending" && s.Status != "active");
                bool wantPlan = _steps.Count == 0 || crisisEntered ||
                                (exhausted && (DateTime.UtcNow - _lastPlanAt).TotalMinutes >= 10);
                bool cooledDown = (DateTime.UtcNow - _lastAttemptAt).TotalMinutes >= AttemptCooldownMinutes;

                if (wantPlan && cooledDown && !_requested && !_generating)
                {
                    _requested = true;
                    _lastAttemptAt = DateTime.UtcNow;
                    _ = GenerateAsync(crisisEntered);
                    LastResult = "plan: generating (LLM, critical lane)…";
                }

                // Async result lands here, parsed+validated on the MAIN thread.
                if (_rawPlanJson != null)
                {
                    var raw = _rawPlanJson; _rawPlanJson = null;
                    AdoptPlan(raw);
                }

                // Execute one step per 2 ticks — steady, observable pacing.
                if (++_tickPacer % 2 == 0) ExecuteNextStep(settlers);

                LastResult = Describe();
            }
            catch (Exception ex) { LastResult = "plan EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        private static string Describe()
        {
            if (_steps.Count == 0) return _generating ? "plan: generating…" : LastResult;
            int done = 0, rejected = 0; Step active = null;
            foreach (var s in _steps)
            {
                if (s.Status == "done") done++;
                else if (s.Status == "rejected" || s.Status == "failed") rejected++;
                else if (active == null && (s.Status == "active" || s.Status == "pending")) active = s;
            }
            string head = active == null
                ? "complete"
                : $"active='{Trunc(active.What, 60)}' ({active.Verb})";
            return $"plan#{_planId} [{done}✓/{rejected}✗/{_steps.Count}] {head} — {Trunc(_rationale, 90)}";
        }

        // ── generation ──────────────────────────────────────────────────────
        private static async Task GenerateAsync(bool crisisEntered)
        {
            try
            {
                _generating = true;
                var client = LLMNPCsPlugin.Instance?.LLMClient;
                if (client == null) { LastResult = "plan: no LLM client"; return; }
                // Distilled reality: strategy-relevant lines only (the worldmap
                // line alone is ~200 chars of noise for a planner).
                string telemetry = "";
                try
                {
                    var keep = new[] { "census:", "food:", "equip:", "weapons:", "alerts:", "research:", "farm:", "production:", "blueprints:", "home:", "events:", "jobs:" };
                    var sb2 = new StringBuilder();
                    foreach (var line in System.IO.File.ReadAllLines(@"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\colony_status.txt"))
                        foreach (var k in keep)
                            if (line.TrimStart().StartsWith(k)) { sb2.AppendLine(line.Trim()); break; }
                    telemetry = sb2.ToString();
                }
                catch { }
                string prompt =
$@"You are the LEADER of this medieval colony. Below is the live colony telemetry (the same report a human overseer reads). Write an IMMEDIATE plan: 3-5 concrete steps for the next stretch{(crisisEntered ? " — the colony just entered a FOOD CRISIS; survival first" : "")}.

TELEMETRY:
{telemetry}

You may ONLY use these verbs (steps with other verbs will be rejected):
- produce(table_id, product_id) — queue a craft at a CONSTRUCTED station. Stations: camp_fire, fletchers_table, basic_research_table. Products: meal, sling, short_bow, war_bow, basic_research_book.
- build(blueprint_id) — place ONE NEW building near home. ONLY these ids: camp_fire, basic_research_table, fletchers_table. (Beds/doors/walls are managed by the housing system — never plan them.)
- focus_job(job_name) — set every settler's priority-1 job (Hunting, Harvesting, Construction, Crafting, Cooking, Research, Fishing, Animal)

Respond ONLY with JSON:
{{""rationale"": ""one sentence strategy"", ""steps"": [{{""what"": ""human description"", ""verb"": ""produce|build|focus_job"", ""args"": ""comma,separated"", ""why"": ""one clause""}}]}}";
                var messages = new List<Message>
                {
                    new Message { Role = "system", Content = "You are a pragmatic medieval colony leader. Survival first, then growth. Output only the JSON object." },
                    new Message { Role = "user", Content = prompt }
                };
                string raw = await client.GetRawResponseAsync(messages,
                    new LLMTraceMetadata { FlowType = PromptFlowTypes.ColonyAdvisor, SenderName = "PlanManager" }, task: "planner",
                    maxTokens: 1024);   // a 5-step JSON plan truncates at the 256 default
                if (string.IsNullOrWhiteSpace(raw))
                {
                    LastResult = "plan: no plan (LLM budget/offline — deterministic floor continues)";
                    LLMNPCsPlugin.LogToFile("[PlanManager] generation returned empty (budget/offline) — cooldown holds");
                    return;
                }
                _rawPlanJson = raw;   // adopted on the next main-thread tick
            }
            catch (Exception ex)
            {
                LastResult = "plan gen EXC: " + ex.Message;
                LLMNPCsPlugin.LogToFile("[PlanManager] generation EXC: " + ex.Message + " — cooldown holds");
            }
            finally { _generating = false; _requested = false; }
        }

        private static void AdoptPlan(string raw)
        {
            try
            {
                int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
                if (a < 0 || b <= a) { LastResult = "plan: no plan (unparseable)"; LLMNPCsPlugin.LogToFile("[PlanManager] unparseable: " + Trunc(raw, 300)); return; }
                var obj = Newtonsoft.Json.Linq.JObject.Parse(raw.Substring(a, b - a + 1));
                _rationale = obj.Value<string>("rationale") ?? "";
                _steps.Clear();
                var arr = obj["steps"] as Newtonsoft.Json.Linq.JArray;
                if (arr == null || arr.Count == 0) { LastResult = "plan: empty plan"; return; }
                int seq = 0;
                foreach (var t in arr)
                {
                    if (seq >= 8) break;
                    var s = new Step
                    {
                        Seq = seq++,
                        What = t.Value<string>("what") ?? "",
                        Verb = (t.Value<string>("verb") ?? "").Trim().ToLowerInvariant(),
                        Args = t.Value<string>("args") ?? "",
                        Why = t.Value<string>("why") ?? ""
                    };
                    if (Array.IndexOf(VerbMenu, s.Verb) < 0)
                    {
                        s.Status = "rejected";
                        LLMNPCsPlugin.LogToFile($"[PlanManager] step {s.Seq} REJECTED (unknown verb '{s.Verb}'): {s.What}");
                    }
                    _steps.Add(s);
                }
                _planId = DateTime.UtcNow.Ticks % 1000000;   // provisional; server id adopted on POST reply in slice 2
                _lastPlanAt = DateTime.UtcNow;
                LLMNPCsPlugin.LogToFile($"[PlanManager] PLAN ADOPTED ({_steps.Count} steps) — {_rationale}");
                PostPlanFireAndForget();
            }
            catch (Exception ex)
            {
                LastResult = "plan adopt EXC: " + ex.Message;
                LLMNPCsPlugin.LogToFile("[PlanManager] adopt EXC: " + ex.Message + " — raw head: " + Trunc(raw, 200));
            }
        }

        // ── execution (main thread, one step per pacer beat) ────────────────
        private static void ExecuteNextStep(List<Settler> settlers)
        {
            Step step = null;
            foreach (var s in _steps) { if (s.Status == "pending" || s.Status == "active") { step = s; break; } }
            if (step == null) return;
            step.Status = "active";
            string outcome; bool ok;
            try
            {
                var args = (step.Args ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < args.Length; i++) args[i] = args[i].Trim();
                switch (step.Verb)
                {
                    case "produce":
                        if (args.Length < 2) { ok = false; outcome = "produce needs (table_id, product_id)"; break; }
                        var pr = ProductionPlanner.Tick(args[0], args[1]);
                        ok = pr.Contains("QUEUED") || pr.Contains("already queued");
                        outcome = pr;
                        break;
                    case "build":
                        if (args.Length < 1) { ok = false; outcome = "build needs (blueprint_id)"; break; }
                        // COHERENCE GUARD (Ken, eyes-on 2026-07-12 00:00: a bed
                        // and a door standing alone in a field): furniture and
                        // structural pieces belong to the HOUSING system's
                        // interior/wall placement — a generic near-home spiral
                        // may not place them. Reject honestly.
                        if (Array.IndexOf(_housingManagedIds, args[0]) >= 0)
                        { ok = false; outcome = $"'{args[0]}' is housing-managed (interior/wall placement) — not plannable via generic build"; break; }
                        if (BuiltState.SkillBlocked(args[0])) { ok = false; outcome = $"'{args[0]}' is skill-blocked (remembered)"; break; }
                        // CENSUS GUARD (coherence, 21:45: plan queued a research
                        // table the colony already has): one-of-a-kind stations
                        // aren't duplicated by a plan — that's a player-waste.
                        if (Array.IndexOf(_singletonBuildings, args[0]) >= 0 && StockpilePlacer.CountBuildings(args[0]) > 0)
                        { ok = true; outcome = $"'{args[0]}' already exists — step satisfied by census"; break; }
                        var builder = settlers[0];
                        var br = StockpilePlacer.TryPlaceBuildingNear(builder.gameObject, args[0]);
                        ok = br != null && br.StartsWith("ok");
                        outcome = br ?? "null result";
                        break;
                    case "focus_job":
                        if (args.Length < 1) { ok = false; outcome = "focus_job needs (job_name)"; break; }
                        ok = FocusJobAll(settlers, args[0], out outcome);
                        break;
                    default:
                        ok = false; outcome = "unknown verb (should have been rejected at adopt)";
                        break;
                }
            }
            catch (Exception ex) { ok = false; outcome = ex.InnerException?.Message ?? ex.Message; }
            step.Status = ok ? "done" : "failed";
            LLMNPCsPlugin.LogToFile($"[PlanManager] step {step.Seq} '{step.What}' [{step.Verb}({step.Args})] → {step.Status}: {Trunc(outcome, 160)}");
            PostStepStatusFireAndForget(step);
        }

        private static bool FocusJobAll(List<Settler> settlers, string jobName, out string outcome)
        {
            var jobTypeT = FindTypeFull("NSMedieval.State.WorkerJobs.JobType");
            if (jobTypeT == null) { outcome = "no JobType"; return false; }
            object jobVal;
            try { jobVal = Enum.Parse(jobTypeT, jobName, true); }
            catch { outcome = $"unknown job '{jobName}'"; return false; }
            int applied = 0;
            foreach (var s in settlers)
            {
                if (s == null || s.gameObject == null) continue;
                if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out _, out var rc)) continue;
                var agent = GameBridge.GetGoapAgent(rc);
                var change = agent?.GetType().GetMethod("ChangeJobPriority", BindingFlags.Public | BindingFlags.Instance);
                if (change == null) continue;
                try { change.Invoke(agent, new[] { jobVal, (object)1 }); applied++; } catch { }
            }
            outcome = $"{jobName} prio 1 for {applied} settler(s)";
            return applied > 0;
        }

        // ── dashboard persistence (fire-and-forget, never blocks the tick) ──
        private static void PostPlanFireAndForget()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"save_id\":").Append(Json(MemoryManager.GetActiveSaveId() ?? "unknown"))
                  .Append(",\"tier\":\"immediate\",\"rationale\":").Append(Json(_rationale))
                  .Append(",\"author\":\"planner-c#\",\"steps\":[");
                for (int i = 0; i < _steps.Count; i++)
                {
                    var s = _steps[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"what\":").Append(Json(s.What))
                      .Append(",\"why\":").Append(Json(s.Why))
                      .Append(",\"how\":").Append(Json($"{s.Verb}({s.Args})")).Append('}');
                }
                sb.Append("]}");
                _ = _http.PostAsync(BaseUrl + "/api/plan",
                    new StringContent(sb.ToString(), Encoding.UTF8, "application/json"));
            }
            catch { }
        }

        private static void PostStepStatusFireAndForget(Step s)
        {
            // Server step ids arrive in slice 2 (resume support); v1 logs suffice
            // for audit and the plan row itself is already persisted.
            if (s.ServerStepId < 0) return;
            try
            {
                var body = $"{{\"save_id\":{Json(MemoryManager.GetActiveSaveId() ?? "unknown")},\"step_id\":{s.ServerStepId},\"status\":\"{s.Status}\"}}";
                _ = _http.PostAsync(BaseUrl + "/api/plan/step_status",
                    new StringContent(body, Encoding.UTF8, "application/json"));
            }
            catch { }
        }

        private static string Json(string s) =>
            "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ") + "\"";

        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";

        private static Type FindTypeFull(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            { try { var t = asm.GetType(fullName, false); if (t != null) return t; } catch { } }
            return null;
        }
    }
}
