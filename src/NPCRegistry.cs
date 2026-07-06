using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Tracks NPCs, their memory, and decision history.
    /// Persists data to disk for long-term memory.
    /// </summary>
    public class NPCRegistry
    {
        private readonly Dictionary<string, NPCData> _npcs;
        private readonly string _storagePath;

        public NPCRegistry()
        {
            _npcs = new Dictionary<string, NPCData>();
            _storagePath = Path.Combine(Application.persistentDataPath, "LLM_NPCs", "npc_data");
            
            if (!Directory.Exists(_storagePath))
            {
                Directory.CreateDirectory(_storagePath);
            }
            LLMNPCsPlugin.LogToFile($"[NPCRegistry:Constructor] Storage path: {_storagePath}");
        }

        /// <summary>
        /// Checks if a settler should be processed (rate limiting).
        /// </summary>
        public bool ShouldProcess(Settler settler, float intervalSeconds)
        {
            var id = GetSettlerId(settler);
            if (string.IsNullOrEmpty(id))
            {
                LLMNPCsPlugin.LogToFile("[NPCRegistry:ShouldProcess] Settler was null/invalid, skipping processing");
                return false;
            }

            if (!_npcs.TryGetValue(id, out var data))
            {
                data = LoadOrCreate(id);
                _npcs[id] = data;
                LLMNPCsPlugin.LogToFile($"[NPCRegistry:ShouldProcess] Created new data for {id}");
            }

            if (data.NextDecisionTime == null)
            {
                // Initial stagger: spread first decisions out over the interval
                data.NextDecisionTime = DateTime.UtcNow.AddSeconds(UnityEngine.Random.Range(0f, intervalSeconds));
                LLMNPCsPlugin.LogToFile($"[NPCRegistry:ShouldProcess] {id} - Assigned initial stagger, next at {data.NextDecisionTime}");
            }

            var result = DateTime.UtcNow >= data.NextDecisionTime.Value;
            if (result)
            {
                LLMNPCsPlugin.LogToFile($"[NPCRegistry:ShouldProcess] {id} - Time reached for decision.");
            }
            return result;
        }

        /// <summary>
        /// Resets all decision timers so every NPC evaluates on the next tick.
        /// Used for high-priority events like Raid Spawn.
        /// </summary>
        public void ForceImmediateEvaluation()
        {
            foreach (var data in _npcs.Values)
            {
                data.NextDecisionTime = DateTime.UtcNow;
            }
            LLMNPCsPlugin.LogToFile("[NPCRegistry:ForceImmediateEvaluation] All NPC decision timers reset to NOW.");
        }

        /// <summary>
        /// Records a decision for an NPC and sets their next staggered time.
        /// </summary>
        public void RecordDecision(Settler settler, LLMDecision decision, float intervalSeconds)
        {
            var id = GetSettlerId(settler);
            if (string.IsNullOrEmpty(id))
            {
                LLMNPCsPlugin.LogToFile("[NPCRegistry:RecordDecision] Settler was null/invalid, decision not recorded");
                return;
            }

            if (!_npcs.TryGetValue(id, out var data))
            {
                data = LoadOrCreate(id);
                _npcs[id] = data;
            }

            data.LastDecisionTime = DateTime.UtcNow;
            
            // Add a ±20% random jitter to the interval so they organically desync further over time
            float jitter = intervalSeconds * 0.2f;
            data.NextDecisionTime = DateTime.UtcNow.AddSeconds(intervalSeconds + UnityEngine.Random.Range(-jitter, jitter));

            data.DecisionHistory.Add(new DecisionRecord
            {
                Timestamp = DateTime.UtcNow,
                Action = decision.Action,
                Parameters = decision.Parameters,
                Reasoning = decision.Reasoning
            });

            // Keep only last 20 decisions
            while (data.DecisionHistory.Count > 20)
            {
                data.DecisionHistory.RemoveAt(0);
            }

            Save(data);
            LLMNPCsPlugin.LogToFile($"[NPCRegistry:RecordDecision] Recorded {decision.Action} for {id}");
        }

        /// <summary>
        /// Gets conversation history for context building.
        /// </summary>
        public List<Message> GetConversationHistory(Settler settler, int count = 5)
        {
            var id = GetSettlerId(settler);
            if (string.IsNullOrEmpty(id))
            {
                LLMNPCsPlugin.LogToFile("[NPCRegistry:GetConversationHistory] Settler was null/invalid, returning empty history");
                return new List<Message>();
            }

            if (!_npcs.TryGetValue(id, out var data))
            {
                return new List<Message>();
            }

            var messages = new List<Message>();
            var recentDecisions = data.DecisionHistory.GetRange(
                Math.Max(0, data.DecisionHistory.Count - count),
                Math.Min(count, data.DecisionHistory.Count)
            );

            foreach (var decision in recentDecisions)
            {
                messages.Add(new Message
                {
                    Role = "assistant",
                    Content = $"Action: {decision.Action}, Reasoning: {decision.Reasoning}"
                });
            }

            return messages;
        }

        /// <summary>
        /// Assigns personality traits to an NPC.
        /// </summary>
        public void AssignPersonality(Settler settler, NPCTraits traits)
        {
            var id = GetSettlerId(settler);
            if (string.IsNullOrEmpty(id))
            {
                LLMNPCsPlugin.LogToFile("[NPCRegistry:AssignPersonality] Settler was null/invalid, personality not assigned");
                return;
            }

            if (!_npcs.TryGetValue(id, out var data))
            {
                data = LoadOrCreate(id);
                _npcs[id] = data;
            }

            data.Traits = traits;
            Save(data);
        }

        /// <summary>
        /// Gets personality traits for an NPC.
        /// </summary>
        public NPCTraits GetPersonality(Settler settler)
        {
            var id = GetSettlerId(settler);
            if (string.IsNullOrEmpty(id))
            {
                LLMNPCsPlugin.LogToFile("[NPCRegistry:GetPersonality] Settler was null/invalid, using default traits");
                return new NPCTraits();
            }

            if (!_npcs.TryGetValue(id, out var data))
            {
                data = LoadOrCreate(id);
                _npcs[id] = data;
            }

            return data.Traits;
        }

        /// <summary>
        /// Gets whether an NPC is marked as a captive.
        /// </summary>
        public bool IsCaptive(Settler settler)
        {
            var id = GetSettlerId(settler);
            if (string.IsNullOrEmpty(id)) return false;

            if (!_npcs.TryGetValue(id, out var data))
            {
                data = LoadOrCreate(id);
                _npcs[id] = data;
            }

            return data.IsCaptive;
        }

        /// <summary>
        /// Sets an NPC's captive status.
        /// </summary>
        public void SetCaptive(Settler settler, bool isCaptive)
        {
            var id = GetSettlerId(settler);
            if (string.IsNullOrEmpty(id))
            {
                LLMNPCsPlugin.LogToFile("[NPCRegistry:SetCaptive] Settler was null/invalid, captive status not set");
                return;
            }

            if (!_npcs.TryGetValue(id, out var data))
            {
                data = LoadOrCreate(id);
                _npcs[id] = data;
            }

            data.IsCaptive = isCaptive;
            Save(data);
        }

        private string GetSettlerId(Settler settler)
        {
            if (settler == null)
                return null;

            if (settler.gameObject == null)
                return null;

            if (GameBridge.TryGetValidatedSettlerIdentity(settler.gameObject, out var gameId, out _, out _)
                && !string.IsNullOrWhiteSpace(gameId))
            {
                return gameId;
            }

            try
            {
                return settler.gameObject.GetInstanceID().ToString();
            }
            catch
            {
                return null;
            }
        }

        private NPCData LoadOrCreate(string id)
        {
            var path = Path.Combine(_storagePath, $"{id}.json");
            
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<NPCData>(json) ?? new NPCData { Id = id };
                }
                catch (Exception ex)
                {
                    LLMNPCsPlugin.Log.LogError($"Failed to load NPC data for {id}: {ex}");
                }
            }

            return new NPCData { Id = id };
        }

        private void Save(NPCData data)
        {
            try
            {
                var path = Path.Combine(_storagePath, $"{data.Id}.json");
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to save NPC data for {data.Id}: {ex}");
            }
        }
    }

    /// <summary>
    /// Persisted data for an NPC.
    /// </summary>
    public class NPCData
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("last_decision_time")]
        public DateTime? LastDecisionTime { get; set; }

        [JsonIgnore]
        public DateTime? NextDecisionTime { get; set; }

        [JsonProperty("decision_history")]
        public List<DecisionRecord> DecisionHistory { get; set; } = new List<DecisionRecord>();

        [JsonProperty("traits")]
        public NPCTraits Traits { get; set; }

        [JsonProperty("memory")]
        public List<string> Memory { get; set; } = new List<string>();

        [JsonProperty("is_captive")]
        public bool IsCaptive { get; set; } = false;
    }

    public class DecisionRecord
    {
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("parameters")]
        public Dictionary<string, object> Parameters { get; set; }

        [JsonProperty("reasoning")]
        public string Reasoning { get; set; }
    }

    /// <summary>
    /// Personality traits for an NPC.
    /// </summary>
    public class NPCTraits
    {
        [JsonProperty("aggression")]
        public float Aggression { get; set; } = 0.5f;

        [JsonProperty("sociability")]
        public float Sociability { get; set; } = 0.5f;

        [JsonProperty("work_ethic")]
        public float WorkEthic { get; set; } = 0.5f;

        [JsonProperty("risk_tolerance")]
        public float RiskTolerance { get; set; } = 0.5f;

        [JsonProperty("creativity")]
        public float Creativity { get; set; } = 0.5f;

        [JsonProperty("altruism")]
        public float Altruism { get; set; } = 0.5f;

        [JsonProperty("description")]
        public string Description => GenerateDescription();

        private string GenerateDescription()
        {
            var parts = new List<string>();
            
            if (Aggression > 0.7f) parts.Add("aggressive");
            else if (Aggression < 0.3f) parts.Add("peaceful");

            if (Sociability > 0.7f) parts.Add("sociable");
            else if (Sociability < 0.3f) parts.Add("introverted");

            if (WorkEthic > 0.7f) parts.Add("hardworking");
            else if (WorkEthic < 0.3f) parts.Add("lazy");

            if (RiskTolerance > 0.7f) parts.Add("risk-taking");
            else if (RiskTolerance < 0.3f) parts.Add("cautious");

            if (parts.Count == 0) parts.Add("balanced");

            return string.Join(", ", parts);
        }
    }
}
