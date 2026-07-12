using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// THE ARCHITECT (#31 slice B, Ken 2026-07-12): villager + village context
    /// goes to the LLM; it answers a design questionnaire (strategy: one common
    /// house or individual houses per NPC; rooms + sizes per building); the
    /// deterministic packer (HouseBuilder.LayoutV2) turns each program into a
    /// buildable floor plan the settlers construct.
    ///
    /// Discipline (lessons already paid for):
    ///  - Critical-lane LLM call with HARD cooldown — a failed consult can
    ///    never retry-loop (PlanManager incident, 2026-07-11).
    ///  - Deterministic validation clamps everything (purposes to the known
    ///    set, widths to the size table, floors to 1 — stairs aren't a
    ///    primitive yet); the LLM decides, the validator makes it buildable.
    ///  - FALLBACK: LLM silent/unparseable → the deterministic longhouse.
    ///    The colony always houses itself (survival floor).
    /// </summary>
    public static class HouseArchitect
    {
        public static string LastResult = "(idle)";
        public static int FailedAttempts = 0;   // ColonyBuilder falls back to the deterministic longhouse at 2
        private static bool _consultRequested, _consulting, _adopted;
        private static string _rawDesign;
        private static DateTime _lastAttemptAt = DateTime.MinValue;
        private const int CooldownMinutes = 10;

        public static void Reset()
        {
            _consultRequested = _consulting = _adopted = false;
            _rawDesign = null; _lastAttemptAt = DateTime.MinValue;
            FailedAttempts = 0;
            LastResult = "(idle)";
        }

        /// <summary>True once a design has been adopted this session (or a queue
        /// already exists from a previous one).</summary>
        public static bool DesignReady => _adopted || BuiltState.VillageQueue.Length > 0;

        /// <summary>Called by ColonyBuilder when housing is needed (graduation
        /// condition met). Runs the consult once (cooldown-guarded); adopts the
        /// design on the main thread; returns true when a design is ready.</summary>
        public static bool Tick(List<Settler> settlers)
        {
            try
            {
                if (DesignReady) return true;

                if (_rawDesign != null)
                {
                    var raw = _rawDesign; _rawDesign = null;
                    AdoptDesign(raw);
                    return DesignReady;
                }

                bool cooled = (DateTime.UtcNow - _lastAttemptAt).TotalMinutes >= CooldownMinutes;
                if (!_consultRequested && !_consulting && cooled)
                {
                    _consultRequested = true;
                    _lastAttemptAt = DateTime.UtcNow;
                    _ = ConsultAsync(settlers);
                    LastResult = "architect: consulting (LLM, critical lane)…";
                }
                return false;
            }
            catch (Exception ex) { LastResult = "architect EXC: " + (ex.InnerException?.Message ?? ex.Message); return false; }
        }

        private static async Task ConsultAsync(List<Settler> settlers)
        {
            try
            {
                _consulting = true;
                var client = LLMNPCsPlugin.Instance?.LLMClient;
                if (client == null) { LastResult = "architect: no LLM client"; return; }

                var roster = new StringBuilder();
                foreach (var s in settlers)
                {
                    if (s == null || s.gameObject == null) continue;
                    if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out var name, out _)) continue;
                    roster.Append("- ").Append(name).Append('\n');
                }
                string jobs = JobRouter.LastResult;         // includes each settler's best skill+level
                string alerts = ColonyAlerts.Current.Replace("\n", " | ");

                string prompt =
$@"You are the master builder of the medieval colony of Dowsby. Design its housing.

VILLAGERS:
{roster}Skills: {jobs}
COLONY STATE: {alerts}

Decide between ONE COMMON HOUSE for all, or INDIVIDUAL HOUSES per villager (or a mix), based on their needs. Room purposes available: dorm (shared sleeping), bedroom (private sleeping), pantry (indoor food storage), workshop (crafting stations), hall (gathering + hearth). Room widths 3-6 (all rooms are 3 deep). Single floor only. 2-5 rooms per building; buildings are built ONE AT A TIME so order them by importance.

Respond ONLY with JSON:
{{""strategy"": ""common_house"" or ""individual_houses"", ""rationale"": ""one sentence"",
  ""buildings"": [{{""label"": ""common house"" or a villager's name, ""rooms"": [{{""purpose"": ""dorm|bedroom|pantry|workshop|hall"", ""width"": 3}}]}}]}}";

                var messages = new List<Message>
                {
                    new Message { Role = "system", Content = "You are a pragmatic medieval master builder. Shelter everyone first; personal quarters when the colony can afford them. Output only the JSON object." },
                    new Message { Role = "user", Content = prompt }
                };
                string raw = await client.GetRawResponseAsync(messages,
                    new LLMTraceMetadata { FlowType = PromptFlowTypes.ColonyAdvisor, SenderName = "HouseArchitect" }, task: "architect",
                    maxTokens: 1024);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    FailedAttempts++;
                    LastResult = $"architect: LLM silent (budget/offline) — attempt {FailedAttempts}/2 before longhouse fallback";
                    LLMNPCsPlugin.LogToFile("[HouseArchitect] consult empty — cooldown holds, attempt " + FailedAttempts);
                    return;
                }
                _rawDesign = raw;   // adopted on the next main-thread tick
            }
            catch (Exception ex)
            {
                FailedAttempts++;
                LastResult = "architect consult EXC: " + ex.Message;
                LLMNPCsPlugin.LogToFile("[HouseArchitect] consult EXC: " + ex.Message + " — cooldown holds, attempt " + FailedAttempts);
            }
            finally { _consulting = false; _consultRequested = false; }
        }

        private static void AdoptDesign(string raw)
        {
            try
            {
                int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
                if (a < 0 || b <= a) { LastResult = "architect: unparseable — longhouse fallback"; LLMNPCsPlugin.LogToFile("[HouseArchitect] unparseable: " + Trunc(raw, 200)); return; }
                var obj = Newtonsoft.Json.Linq.JObject.Parse(raw.Substring(a, b - a + 1));
                string strategy = obj.Value<string>("strategy") ?? "common_house";
                string rationale = obj.Value<string>("rationale") ?? "";
                var buildings = obj["buildings"] as Newtonsoft.Json.Linq.JArray;
                if (buildings == null || buildings.Count == 0) { LastResult = "architect: no buildings in design — fallback"; return; }

                var queue = new StringBuilder();
                int adopted = 0;
                foreach (var bTok in buildings)
                {
                    if (adopted >= 6) break;   // sanity cap on queue length
                    string label = bTok.Value<string>("label") ?? $"building {adopted + 1}";
                    var rooms = bTok["rooms"] as Newtonsoft.Json.Linq.JArray;
                    if (rooms == null || rooms.Count == 0) continue;
                    var prog = new StringBuilder();
                    int taken = 0;
                    foreach (var r in rooms)
                    {
                        if (taken >= 5) break;
                        string purpose = (r.Value<string>("purpose") ?? "").Trim().ToLowerInvariant();
                        int width = r.Value<int?>("width") ?? 0;
                        if (purpose != "dorm" && purpose != "bedroom" && purpose != "pantry" && purpose != "workshop" && purpose != "hall")
                        { LLMNPCsPlugin.LogToFile($"[HouseArchitect] clamped unknown purpose '{purpose}' (dropped)"); continue; }
                        if (prog.Length > 0) prog.Append(',');
                        prog.Append(purpose).Append(':').Append(Math.Max(3, Math.Min(width, 6)));
                        taken++;
                    }
                    if (taken < 2) continue;   // a building needs at least 2 valid rooms
                    if (queue.Length > 0) queue.Append(';');
                    queue.Append(label.Replace(';', ' ').Replace('|', ' ')).Append('|').Append(prog);
                    adopted++;
                }
                if (adopted == 0) { LastResult = "architect: design had no buildable buildings — fallback"; return; }

                BuiltState.VillageQueue = queue.ToString();
                BuiltState.VillageQueueIndex = 0;
                _adopted = true;
                LastResult = $"architect: {strategy} — {adopted} building(s) queued — {Trunc(rationale, 100)}";
                LLMNPCsPlugin.LogToFile($"[HouseArchitect] DESIGN ADOPTED ({strategy}, {adopted} buildings): {BuiltState.VillageQueue} — {rationale}");
            }
            catch (Exception ex)
            {
                LastResult = "architect adopt EXC: " + ex.Message + " — fallback";
                LLMNPCsPlugin.LogToFile("[HouseArchitect] adopt EXC: " + ex.Message + " raw: " + Trunc(raw, 200));
            }
        }

        /// <summary>Current queue entry ("label|program"), or null when done.</summary>
        public static bool TryCurrentBuilding(out string label, out string program)
        {
            label = program = null;
            var q = BuiltState.VillageQueue;
            if (string.IsNullOrEmpty(q)) return false;
            var entries = q.Split(';');
            int idx = BuiltState.VillageQueueIndex;
            if (idx < 0 || idx >= entries.Length) return false;
            var kv = entries[idx].Split('|');
            if (kv.Length != 2) return false;
            label = kv[0]; program = kv[1];
            return true;
        }

        /// <summary>The current building finished — advance to the next queue
        /// entry (returns true) or report the village complete (false).</summary>
        public static bool AdvanceQueue()
        {
            var q = BuiltState.VillageQueue;
            if (string.IsNullOrEmpty(q)) return false;
            int next = BuiltState.VillageQueueIndex + 1;
            if (next >= q.Split(';').Length) return false;
            BuiltState.VillageQueueIndex = next;
            return true;
        }

        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
