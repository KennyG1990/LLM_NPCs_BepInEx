using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    public class ColonyInfluenceEngine
    {
        private static ColonyInfluenceEngine _instance;
        public static ColonyInfluenceEngine Instance => _instance ?? (_instance = new ColonyInfluenceEngine());

        private ColonyRecommendation _latestRecommendation;
        private ColonyState _latestState;

        public ColonyRecommendation LatestRecommendation => _latestRecommendation;
        public ColonyState LatestState => _latestState;

        public ColonyState ComputeColonyState(List<NPCContext> contexts)
        {
            var state = new ColonyState();
            if (contexts == null || contexts.Count == 0)
            {
                state.TotalSettlers = 0;
                state.ColonyWealth = GameBridge.GetColonyWealth();
                return state;
            }

            float totalMood = 0f;
            float totalHunger = 0f;
            float totalHealth = 0f;
            int idleCount = 0;
            float maxThreat = 0f;
            var activeTensions = new List<string>();

            foreach (var ctx in contexts)
            {
                totalMood += ctx.MoodScore;
                if (ctx.Needs != null)
                {
                    totalHunger += (100f - ctx.Needs.Food); // Hunger is the inverse of Food need
                }
                if (ctx.Health != null)
                {
                    totalHealth += ctx.Health.Overall;
                }

                if (ctx.CurrentActivity == null || 
                    string.IsNullOrEmpty(ctx.CurrentActivity.Type) || 
                    ctx.CurrentActivity.Type.ToLower() == "idle" ||
                    ctx.CurrentActivity.Type.ToLower() == "none")
                {
                    idleCount++;
                }

                if (ctx.Environment != null)
                {
                    maxThreat = Math.Max(maxThreat, ctx.Environment.HostilesNearby);
                }

                // Collect relationships with low opinions as active tensions
                if (ctx.Relationships != null)
                {
                    foreach (var rel in ctx.Relationships.Values)
                    {
                        if (rel.Opinion < -30f)
                        {
                            var tensionStr = $"{ctx.Name} ↔ {rel.NPCId} (Opinion: {rel.Opinion:F0})";
                            if (!activeTensions.Contains(tensionStr))
                            {
                                activeTensions.Add(tensionStr);
                            }
                        }
                    }
                }
            }

            state.TotalSettlers = contexts.Count;
            state.AverageMood = totalMood / contexts.Count;
            state.AverageHunger = totalHunger / contexts.Count;
            state.AverageHealth = totalHealth / contexts.Count;
            state.IdleSettlers = idleCount;
            state.ColonyWealth = GameBridge.GetColonyWealth();
            state.ThreatLevel = maxThreat;
            state.PendingBlueprints = GetPendingBlueprintsCount();
            state.ActiveTensions = activeTensions;

            _latestState = state;
            return state;
        }

        public ColonyRecommendation GenerateRecommendations(ColonyState state)
        {
            var recommendations = new List<ColonyRecommendation>();

            // 1. Food/Hunger
            if (state.AverageHunger > 40f) // Average food need < 60
            {
                recommendations.Add(new ColonyRecommendation
                {
                    Type = "reassign_job",
                    Description = "Colony hunger is rising. Recommend reassigning settlers to farming or cooking.",
                    Score = (state.AverageHunger / 100f) * 1.5f,
                    Parameters = new Dictionary<string, object> { { "job", "cook" }, { "urgency", "high" } }
                });
            }

            // 2. Health
            if (state.AverageHealth < 85f)
            {
                recommendations.Add(new ColonyRecommendation
                {
                    Type = "seek_medic",
                    Description = "Average settler health is low. Recommend medical care and resting.",
                    Score = ((100f - state.AverageHealth) / 100f) * 1.6f,
                    Parameters = new Dictionary<string, object> { { "action", "seek_medic" }, { "urgency", "critical" } }
                });
            }

            // 3. Threat
            if (state.ThreatLevel > 0f)
            {
                recommendations.Add(new ColonyRecommendation
                {
                    Type = "build_priority",
                    Description = "Hostile threats detected nearby. Recommend drafting combatants and securing defenses.",
                    Score = Math.Min(1.0f, state.ThreatLevel * 0.4f) * 1.8f,
                    Parameters = new Dictionary<string, object> { { "threat", state.ThreatLevel }, { "action", "defend" } }
                });
            }

            // 4. Blueprints / Construction
            if (state.PendingBlueprints > 0)
            {
                recommendations.Add(new ColonyRecommendation
                {
                    Type = "build_priority",
                    Description = $"There are {state.PendingBlueprints} pending blueprints. Recommend prioritizing construction.",
                    Score = Math.Min(1.0f, state.PendingBlueprints * 0.1f) * 1.2f,
                    Parameters = new Dictionary<string, object> { { "blueprints", state.PendingBlueprints } }
                });
            }

            // 5. Mood / Recreation
            if (state.AverageMood < 50f)
            {
                recommendations.Add(new ColonyRecommendation
                {
                    Type = "reassign_job",
                    Description = "Average mood is low. Recommend scheduling more recreation time.",
                    Score = ((100f - state.AverageMood) / 100f) * 1.3f,
                    Parameters = new Dictionary<string, object> { { "need", "recreation" } }
                });
            }

            // Fallback recommendation
            if (recommendations.Count == 0)
            {
                recommendations.Add(new ColonyRecommendation
                {
                    Type = "continue_operations",
                    Description = "Colony operations are stable. Keep up the good work.",
                    Score = 0.5f
                });
            }

            // Pick highest scoring recommendation
            _latestRecommendation = recommendations.OrderByDescending(r => r.Score).First();
            return _latestRecommendation;
        }

        public async Task<string> AskLLMForNarrativeAsync(LLMClient client, ColonyState state, ColonyRecommendation rec)
        {
            if (client == null) return "No narrative available (LLM offline).";

            var prompt = $@"You are the Overseer's strategic assistant. Summarize the colony state and explain the recommendation.
COLONY STATE:
- Total Settlers: {state.TotalSettlers}
- Idle Settlers: {state.IdleSettlers}
- Average Mood: {state.AverageMood:F0}/100
- Average Hunger: {state.AverageHunger:F0}/100
- Average Health: {state.AverageHealth:F0}/100
- Colony Wealth: {state.ColonyWealth:F0}
- Threats Nearby: {state.ThreatLevel}
- Pending Blueprints: {state.PendingBlueprints}

RECOMMENDATION:
- Type: {rec.Type}
- Urgency Score: {rec.Score:F2}
- Description: {rec.Description}

Provide a short, immersive, 1-2 sentence response summarizing the state and urging the player to act.";

            try
            {
                var messages = new List<Message>
                {
                    new Message { Role = "system", Content = "You are a medieval colony advisor. Respond in character." },
                    new Message { Role = "user", Content = prompt }
                };

                return await client.GetRawResponseAsync(messages, new LLMTraceMetadata { FlowType = PromptFlowTypes.NpcDecisions });
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[ColonyInfluenceEngine] LLM query failed: {ex.Message}");
                return $"Advisor: We must act on the recommendation to {rec.Type}.";
            }
        }

        private int GetPendingBlueprintsCount()
        {
            try
            {
                var blueprintType = Type.GetType("NSMedieval.BuildingComponents.Blueprint, Assembly-CSharp") 
                    ?? Type.GetType("NSMedieval.Blueprint, Assembly-CSharp");
                if (blueprintType != null)
                {
                    var blueprints = UnityEngine.Object.FindObjectsOfType(blueprintType);
                    return blueprints?.Length ?? 0;
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[ColonyInfluenceEngine] Error getting blueprint count: {ex.Message}");
            }
            return 0;
        }
    }
}
