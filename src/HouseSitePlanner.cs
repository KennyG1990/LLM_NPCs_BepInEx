using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// LEADER-VOICE SITE PLANNER (Ken: "ask the villagers where they want to build,
    /// then find a spot that satisfies it — if everything's deterministic there's no
    /// creativity"). This is the creative half: the colony's ELECTED LEADER speaks
    /// through the LLM and emits a PREFERENCE (near the forest, up high, away from the
    /// others), which the deterministic SiteScorer turns into real coordinates.
    ///
    /// Flow: WorldMap.Scan (terrain) -> leader persona + colony/world summary ->
    /// LLM call on the "planner" task slot -> parse preference weights + rationale ->
    /// SiteScorer.FindSites -> shortlist -> (leader picks; for now top) -> ChosenSite.
    /// Fires ONCE per session, budget-gated (it's an LLM call). The builder/HousePlanner
    /// consumes ChosenSite next.
    ///
    /// NOTE: real elections don't exist yet — leader = a heuristic placeholder
    /// (first validated settler) until the governance layer lands. Marked TODO.
    /// </summary>
    public static class HouseSitePlanner
    {
        public static string LastResult = "(idle)";
        public static string LastRationale = "";
        public static bool Done = false;
        public static bool HasSite = false;
        public static SiteScorer.Site ChosenSite;

        public static void PlanOnce(List<Settler> settlers)
        {
            if (Done) return;
            Done = true;
            try { _ = PlanAsync(settlers); }
            catch (Exception ex) { LastResult = "siteplan sync EXC: " + ex.Message; }
        }

        private static async Task PlanAsync(List<Settler> settlers)
        {
            try
            {
                if (WorldMap.Surface == null) WorldMap.Scan();
                if (WorldMap.Surface == null) { LastResult = "siteplan: no worldmap"; return; }

                // Pick the leader (placeholder until elections exist).
                string leader = "the village elder";
                if (settlers != null)
                    foreach (var s in settlers)
                    {
                        if (s == null || s.gameObject == null) continue;
                        if (GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out var nm, out _)) { leader = nm; break; }
                    }

                var client = LLMNPCsPlugin.Instance?.LLMClient;
                if (client == null) { LastResult = "siteplan: no LLM client"; return; }

                int homeX = ColonyHome.Established ? ColonyHome.X : -1;
                int homeZ = ColonyHome.Established ? ColonyHome.Z : -1;

                string prompt =
$@"You are {leader}, the elected leader of a medieval colony deciding WHERE to build the settlement's proper house.
Colony situation: {ColonyAlerts.Current}
Land (whole map): {WorldMap.LastSummary}
As the leader, express what YOU value in a building site — driven by your character and the colony's needs.
Respond with ONLY a JSON object of weights from -1.0 (avoid) to 1.0 (strongly want), plus a one-sentence rationale:
{{""near_home"":0.0,""forest"":0.0,""fertile_soil"":0.0,""water"":0.0,""stone"":0.0,""high_ground"":0.0,""openness"":0.0,""privacy"":0.0,""cellar"":0.0,""rationale"":""...""}}";

                var messages = new List<Message>
                {
                    new Message { Role = "system", Content = "You are a decisive medieval colony leader. Output only the JSON object." },
                    new Message { Role = "user", Content = prompt }
                };

                // "planner" task slot — under OpenRouter this uses the planner model.
                string raw = await client.GetRawResponseAsync(messages,
                    new LLMTraceMetadata { FlowType = PromptFlowTypes.ColonyAdvisor, SenderName = leader }, task: "planner");

                if (string.IsNullOrWhiteSpace(raw)) { LastResult = "siteplan: LLM gave nothing (budget/offline) — no site chosen"; return; }

                var pref = ParsePreference(raw, out string rationale);
                LastRationale = rationale;

                var sites = SiteScorer.FindSites(pref, footprint: 12, topN: 3, homeX: homeX, homeZ: homeZ);
                if (sites.Count == 0) { LastResult = $"siteplan: leader wants [{rationale}] but NO site fits a 12x12 pad"; LLMNPCsPlugin.LogToFile("[HouseSitePlanner] " + LastResult); return; }

                ChosenSite = sites[0];   // leader takes the best match (LLM re-pick = later)
                HasSite = true;
                LastResult = $"siteplan: {leader} chose ({ChosenSite.X},{ChosenSite.Y},{ChosenSite.Z}) — {ChosenSite.Reason} | why: {rationale}";
                LLMNPCsPlugin.LogToFile("[HouseSitePlanner] " + LastResult);
                foreach (var s in sites)
                    LLMNPCsPlugin.LogToFile($"[HouseSitePlanner]   candidate ({s.X},{s.Y},{s.Z}) pad{s.PadSize} score{s.Score:F2} — {s.Reason}");

                // PERSIST the plan to the dashboard (gm_plans /api/plan — gap #3:
                // this is /api/plan's first real producer). Tracked + auditable.
                try
                {
                    var mem = LLMNPCsPlugin.Instance?.MemoryManager;
                    if (mem != null)
                        mem.HttpPostAsync("/api/plan", new Dictionary<string, object>
                        {
                            { "save_id", MemoryManager.GetActiveSaveId() },
                            { "tier", "immediate" },
                            { "author", leader },
                            { "rationale", rationale },
                            { "steps", new object[] { new Dictionary<string, object>
                                {
                                    { "what", "build the colony house at the leader's chosen site" },
                                    { "where_xyz", $"{ChosenSite.X},{ChosenSite.Y},{ChosenSite.Z}" },
                                    { "why", rationale },
                                    { "how", ChosenSite.Reason }
                                } } }
                        });
                }
                catch (Exception pe) { LLMNPCsPlugin.LogToFile("[HouseSitePlanner] plan POST failed: " + pe.Message); }
            }
            catch (Exception ex) { LastResult = "siteplan EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        private static SiteScorer.Preference ParsePreference(string raw, out string rationale)
        {
            rationale = "";
            var p = new SiteScorer.Preference();
            try
            {
                int i = raw.IndexOf('{'), j = raw.LastIndexOf('}');
                if (i < 0 || j <= i) return p;
                var o = JObject.Parse(raw.Substring(i, j - i + 1));
                float G(string k) { var t = o[k]; try { return t != null ? (float)Math.Max(-1.0, Math.Min(1.0, t.Value<double>())) : 0f; } catch { return 0f; } }
                p.NearHome = G("near_home"); p.Forest = G("forest"); p.FertileSoil = G("fertile_soil");
                p.Water = G("water"); p.Stone = G("stone"); p.HighGround = G("high_ground");
                p.Openness = G("openness"); p.Privacy = G("privacy"); p.Cellar = G("cellar");
                rationale = o["rationale"]?.ToString() ?? "";
            }
            catch { }
            return p;
        }
    }
}
