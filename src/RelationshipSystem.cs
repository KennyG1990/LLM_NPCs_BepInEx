using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Manages relationships between NPCs including friendship, romance, and rivalry.
    /// Tracks relationship progression and delegates storage to the Python dashboard database via MemoryManager.
    /// </summary>
    public class RelationshipSystem : IDisposable
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Dictionary<string, Relationship>> _relationshipCache;
        private MemoryManager _memoryManager;

        // Relationship thresholds
        public const float FRIENDSHIP_THRESHOLD = 0.3f;
        public const float GOOD_FRIENDS_THRESHOLD = 0.6f;
        public const float ROMANCE_THRESHOLD = 0.5f;
        public const float MARRIAGE_THRESHOLD = 0.8f;
        public const float RIVALRY_THRESHOLD = -0.3f;
        public const float ENEMY_THRESHOLD = -0.6f;

        public RelationshipSystem(MemoryManager memoryManager = null)
        {
            LLMNPCsPlugin.LogToFile("[RelationshipSystem:Constructor] Starting");
            _memoryManager = memoryManager;
            _relationshipCache = new Dictionary<string, Dictionary<string, Relationship>>();
            LLMNPCsPlugin.LogToFile("[RelationshipSystem:Constructor] Initialized (HTTP Mode)");
        }

        /// <summary>Injects MemoryManager reference after initialization.</summary>
        public void SetMemoryManager(MemoryManager mm) => _memoryManager = mm;

        private static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            dateTime = dateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dateTime;
        }

        private static double? DateTimeToUnixTimeStamp(DateTime? dateTime)
        {
            if (!dateTime.HasValue) return null;
            var utc = dateTime.Value.ToUniversalTime();
            return (utc - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>
        /// Gets or creates a relationship between two NPCs.
        /// </summary>
        public Relationship GetRelationship(string npcAId, string npcBId)
        {
            // Ensure consistent ordering
            if (string.Compare(npcAId, npcBId) > 0)
            {
                var temp = npcAId;
                npcAId = npcBId;
                npcBId = temp;
            }

            // Check cache first
            if (_relationshipCache.TryGetValue(npcAId, out var inner) && 
                inner.TryGetValue(npcBId, out var cached))
            {
                return cached;
            }

            lock (_lock)
            {
                if (_memoryManager != null)
                {
                    var saveId = Uri.EscapeDataString(_memoryManager.ActiveSaveId);
                    var escapedA = Uri.EscapeDataString(npcAId);
                    var escapedB = Uri.EscapeDataString(npcBId);
                    var route = $"/api/memory/relationship?save_id={saveId}&subject={escapedA}&object={escapedB}";

                    var json = _memoryManager.HttpGetSync(route);
                    if (!string.IsNullOrEmpty(json))
                    {
                        try
                        {
                            var resp = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                            if (resp != null && resp.TryGetValue("relationship", out var relObj) && relObj != null)
                            {
                                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(relObj.ToString());
                                if (data != null)
                                {
                                    var rel = new Relationship
                                    {
                                        NPC_A_Id = npcAId,
                                        NPC_B_Id = npcBId,
                                        Friendship = data.TryGetValue("friendship", out var f) ? Convert.ToSingle(f) : 0f,
                                        Romance = data.TryGetValue("romance", out var r) ? Convert.ToSingle(r) : 0f,
                                        Rivalry = data.TryGetValue("rivalry", out var riv) ? Convert.ToSingle(riv) : 0f,
                                        Trust = data.TryGetValue("trust", out var t) ? Convert.ToSingle(t) : 0.5f,
                                        Attraction = data.TryGetValue("attraction", out var attr) ? Convert.ToSingle(attr) : 0f,
                                        RelationshipType = data.TryGetValue("relationship_type", out var type) ? type?.ToString() : "strangers",
                                        IsMarried = data.TryGetValue("is_married", out var marr) && Convert.ToInt32(marr) == 1,
                                        MarriageDate = data.TryGetValue("marriage_date", out var mdate) && mdate != null ? 
                                            (DateTime?)UnixTimeStampToDateTime(Convert.ToDouble(mdate)) : null,
                                        TotalInteractions = data.TryGetValue("total_interactions", out var tot) ? Convert.ToInt32(tot) : 0,
                                        PositiveInteractions = data.TryGetValue("positive_interactions", out var pos) ? Convert.ToInt32(pos) : 0,
                                        NegativeInteractions = data.TryGetValue("negative_interactions", out var neg) ? Convert.ToInt32(neg) : 0
                                    };
                                    CacheRelationship(rel);
                                    return rel;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LLMNPCsPlugin.LogToFile($"[RelationshipSystem] Parse error in GetRelationship: {ex.Message}");
                        }
                    }
                }

                // Create new relationship
                var newRel = new Relationship
                {
                    NPC_A_Id = npcAId,
                    NPC_B_Id = npcBId,
                    RelationshipType = "strangers"
                };

                SaveRelationship(newRel);
                CacheRelationship(newRel);
                return newRel;
            }
        }

        /// <summary>
        /// Modifies a relationship based on interaction outcome.
        /// </summary>
        public void ModifyRelationship(string npcAId, string npcBId, InteractionOutcome outcome, 
            string eventDescription = null)
        {
            var rel = GetRelationship(npcAId, npcBId);
            
            rel.TotalInteractions++;
            rel.LastInteraction = DateTime.UtcNow;

            // Apply changes based on outcome
            switch (outcome)
            {
                case InteractionOutcome.VeryPositive:
                    rel.Friendship = Mathf.Clamp01(rel.Friendship + 0.15f);
                    rel.Trust = Mathf.Clamp01(rel.Trust + 0.1f);
                    rel.PositiveInteractions++;
                    break;
                case InteractionOutcome.Positive:
                    rel.Friendship = Mathf.Clamp01(rel.Friendship + 0.08f);
                    rel.Trust = Mathf.Clamp01(rel.Trust + 0.05f);
                    rel.PositiveInteractions++;
                    break;
                case InteractionOutcome.Neutral:
                    rel.Trust = Mathf.Clamp01(rel.Trust + 0.02f);
                    break;
                case InteractionOutcome.Negative:
                    rel.Friendship = Mathf.Clamp01(rel.Friendship - 0.08f);
                    rel.Rivalry = Mathf.Clamp01(rel.Rivalry + 0.05f);
                    rel.Trust = Mathf.Clamp01(rel.Trust - 0.05f);
                    rel.NegativeInteractions++;
                    break;
                case InteractionOutcome.VeryNegative:
                    rel.Friendship = Mathf.Clamp01(rel.Friendship - 0.15f);
                    rel.Rivalry = Mathf.Clamp01(rel.Rivalry + 0.12f);
                    rel.Trust = Mathf.Clamp01(rel.Trust - 0.1f);
                    rel.NegativeInteractions++;
                    break;
                case InteractionOutcome.Flirtatious:
                    rel.Romance = Mathf.Clamp01(rel.Romance + 0.1f);
                    rel.Attraction = Mathf.Clamp01(rel.Attraction + 0.08f);
                    rel.Friendship = Mathf.Clamp01(rel.Friendship + 0.05f);
                    rel.PositiveInteractions++;
                    break;
                case InteractionOutcome.Romantic:
                    rel.Romance = Mathf.Clamp01(rel.Romance + 0.15f);
                    rel.Attraction = Mathf.Clamp01(rel.Attraction + 0.12f);
                    rel.Friendship = Mathf.Clamp01(rel.Friendship + 0.08f);
                    rel.PositiveInteractions++;
                    break;
            }

            // Update relationship type
            UpdateRelationshipType(rel);

            // Save changes
            SaveRelationship(rel);

            // Record event directly into standard memory log so it is contextually accessible via RAG
            if (!string.IsNullOrEmpty(eventDescription) && _memoryManager != null)
            {
                _memoryManager.RecordEvent(npcAId, "relationship", eventDescription, importance: 5);
                _memoryManager.RecordEvent(npcBId, "relationship", eventDescription, importance: 5);
            }

            LLMNPCsPlugin.LogToFile($"[RelationshipSystem] {npcAId} ↔ {npcBId}: {outcome} | " +
                $"Friendship: {rel.Friendship:F2}, Romance: {rel.Romance:F2}");
        }

        /// <summary>
        /// Attempts to marry two NPCs if their relationship allows it.
        /// </summary>
        public bool TryMarry(string npcAId, string npcBId)
        {
            var rel = GetRelationship(npcAId, npcBId);

            if (rel.IsMarried)
                return false;

            if (rel.Romance < MARRIAGE_THRESHOLD || rel.Friendship < GOOD_FRIENDS_THRESHOLD)
                return false;

            rel.IsMarried = true;
            rel.MarriageDate = DateTime.UtcNow;
            rel.RelationshipType = "married";

            SaveRelationship(rel);

            // Permanent memory — both spouses remember this forever
            if (_memoryManager != null)
            {
                var dateStr = rel.MarriageDate.Value.ToString("yyyy-MM-dd");
                var marriageMsg = $"{npcAId} and {npcBId} got married!";
                
                _memoryManager.RecordLifeEvent(npcAId, "marriage",
                    $"Married {npcBId} on {dateStr}. They are my spouse. {marriageMsg}");
                _memoryManager.RecordLifeEvent(npcBId, "marriage",
                    $"Married {npcAId} on {dateStr}. They are my spouse. {marriageMsg}");
            }

            LLMNPCsPlugin.LogToFile($"[RelationshipSystem] MARRIAGE: {npcAId} married {npcBId}!");
            return true;
        }

        /// <summary>
        /// Gets all relationships for an NPC.
        /// </summary>
        public List<Relationship> GetAllRelationships(string npcId)
        {
            var relationships = new List<Relationship>();
            if (_memoryManager == null) return relationships;

            lock (_lock)
            {
                var saveId = Uri.EscapeDataString(_memoryManager.ActiveSaveId);
                var escapedId = Uri.EscapeDataString(npcId);
                var route = $"/api/relationships?save_id={saveId}&npc_id={escapedId}";

                var json = _memoryManager.HttpGetSync(route);
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        var resp = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                        if (resp != null && resp.TryGetValue("relationships", out var listObj) && listObj != null)
                        {
                            var list = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(listObj.ToString());
                            if (list != null)
                            {
                                foreach (var data in list)
                                {
                                    var npcA = data.TryGetValue("subject", out var a) ? a?.ToString() : null;
                                    var npcB = data.TryGetValue("object", out var b) ? b?.ToString() : null;
                                    if (npcA != null && npcB != null)
                                    {
                                        var rel = new Relationship
                                        {
                                            NPC_A_Id = npcA,
                                            NPC_B_Id = npcB,
                                            Friendship = data.TryGetValue("friendship", out var f) ? Convert.ToSingle(f) : 0f,
                                            Romance = data.TryGetValue("romance", out var r) ? Convert.ToSingle(r) : 0f,
                                            Rivalry = data.TryGetValue("rivalry", out var riv) ? Convert.ToSingle(riv) : 0f,
                                            Trust = data.TryGetValue("trust", out var t) ? Convert.ToSingle(t) : 0.5f,
                                            Attraction = data.TryGetValue("attraction", out var attr) ? Convert.ToSingle(attr) : 0f,
                                            RelationshipType = data.TryGetValue("relationship_type", out var type) ? type?.ToString() : "strangers",
                                            IsMarried = data.TryGetValue("is_married", out var marr) && Convert.ToInt32(marr) == 1,
                                            MarriageDate = data.TryGetValue("marriage_date", out var mdate) && mdate != null ? 
                                                (DateTime?)UnixTimeStampToDateTime(Convert.ToDouble(mdate)) : null,
                                            TotalInteractions = data.TryGetValue("total_interactions", out var tot) ? Convert.ToInt32(tot) : 0,
                                            PositiveInteractions = data.TryGetValue("positive_interactions", out var pos) ? Convert.ToInt32(pos) : 0,
                                            NegativeInteractions = data.TryGetValue("negative_interactions", out var neg) ? Convert.ToInt32(neg) : 0
                                        };
                                        relationships.Add(rel);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LLMNPCsPlugin.LogToFile($"[RelationshipSystem] Parse error in GetAllRelationships: {ex.Message}");
                    }
                }
            }

            return relationships;
        }

        /// <summary>
        /// Gets potential conversation partners for an NPC.
        /// </summary>
        public List<string> GetPotentialPartners(string npcId, float minFriendship = 0f)
        {
            return GetAllRelationships(npcId)
                .Where(r => r.Friendship >= minFriendship || r.Romance > 0)
                .Select(r => r.NPC_A_Id == npcId ? r.NPC_B_Id : r.NPC_A_Id)
                .ToList();
        }

        private void UpdateRelationshipType(Relationship rel)
        {
            if (rel.IsMarried)
            {
                rel.RelationshipType = "married";
            }
            else if (rel.Romance >= ROMANCE_THRESHOLD && rel.Friendship >= FRIENDSHIP_THRESHOLD)
            {
                rel.RelationshipType = rel.Romance >= MARRIAGE_THRESHOLD ? "lovers" : "romantic";
            }
            else if (rel.Friendship >= GOOD_FRIENDS_THRESHOLD)
            {
                rel.RelationshipType = "good_friends";
            }
            else if (rel.Friendship >= FRIENDSHIP_THRESHOLD)
            {
                rel.RelationshipType = "friends";
            }
            else if (rel.Rivalry >= Math.Abs(ENEMY_THRESHOLD))
            {
                rel.RelationshipType = "enemies";
            }
            else if (rel.Rivalry >= Math.Abs(RIVALRY_THRESHOLD))
            {
                rel.RelationshipType = "rivals";
            }
            else if (rel.TotalInteractions > 0)
            {
                rel.RelationshipType = "acquaintances";
            }
            else
            {
                rel.RelationshipType = "strangers";
            }
        }

        private void SaveRelationship(Relationship rel)
        {
            if (_memoryManager == null) return;

            var payload = new Dictionary<string, object>
            {
                { "save_id", _memoryManager.ActiveSaveId },
                { "subject", rel.NPC_A_Id },
                { "object", rel.NPC_B_Id },
                { "friendship", rel.Friendship },
                { "romance", rel.Romance },
                { "rivalry", rel.Rivalry },
                { "trust", rel.Trust },
                { "attraction", rel.Attraction },
                { "relationship_type", rel.RelationshipType },
                { "is_married", rel.IsMarried ? 1 : 0 },
                { "marriage_date", DateTimeToUnixTimeStamp(rel.MarriageDate) },
                { "total_interactions", rel.TotalInteractions },
                { "positive_interactions", rel.PositiveInteractions },
                { "negative_interactions", rel.NegativeInteractions },
                { "last_interaction", DateTimeToUnixTimeStamp(rel.LastInteraction) }
            };

            _memoryManager.HttpPostAsync("/api/memory/relationship", payload);
        }

        private void CacheRelationship(Relationship rel)
        {
            if (!_relationshipCache.TryGetValue(rel.NPC_A_Id, out var inner))
            {
                inner = new Dictionary<string, Relationship>();
                _relationshipCache[rel.NPC_A_Id] = inner;
            }
            inner[rel.NPC_B_Id] = rel;
        }

        /// <summary>
        /// Calculates relationship tension score for an NPC (0.0 to 1.0) based on maximum rivalry.
        /// </summary>
        public float GetRelationshipPressure(string npcId)
        {
            var relationships = GetAllRelationships(npcId);
            if (relationships.Count == 0) return 0f;

            float maxTension = 0f;
            foreach (var r in relationships)
            {
                if (r.Rivalry > maxTension)
                {
                    maxTension = r.Rivalry;
                }
            }
            return maxTension;
        }

        /// <summary>
        /// Gets a list of NPC IDs this NPC has negative standing with (rivals or enemies).
        /// </summary>
        public List<string> GetTensionPartners(string npcId)
        {
            return GetAllRelationships(npcId)
                .Where(r => r.Rivalry >= Math.Abs(RIVALRY_THRESHOLD) || r.RelationshipType == "enemies" || r.RelationshipType == "rivals")
                .Select(r => r.NPC_A_Id == npcId ? r.NPC_B_Id : r.NPC_A_Id)
                .ToList();
        }

        /// <summary>
        /// Records that a social interaction occurred.
        /// </summary>
        public void RecordSocialInteraction(string npcId, string partnerId, bool positive)
        {
            var outcome = positive ? InteractionOutcome.Positive : InteractionOutcome.Negative;
            string desc = positive ? $"Social interaction: positive with {partnerId}" : $"Social interaction: negative with {partnerId}";
            ModifyRelationship(npcId, partnerId, outcome, desc);
        }

        public void Dispose()
        {
        }
    }

    public class Relationship
    {
        public string NPC_A_Id { get; set; }
        public string NPC_B_Id { get; set; }
        public float Friendship { get; set; } = 0f;
        public float Romance { get; set; } = 0f;
        public float Rivalry { get; set; } = 0f;
        public float Trust { get; set; } = 0.5f;
        public float Attraction { get; set; } = 0f;
        public string RelationshipType { get; set; } = "strangers";
        public bool IsMarried { get; set; } = false;
        public DateTime? MarriageDate { get; set; }
        public int TotalInteractions { get; set; } = 0;
        public int PositiveInteractions { get; set; } = 0;
        public int NegativeInteractions { get; set; } = 0;
        public DateTime? LastInteraction { get; set; }

        public string GetDisplayName(string myId, string otherName)
        {
            if (IsMarried) return $"💍 Spouse ({otherName})";
            if (Romance >= RelationshipSystem.ROMANCE_THRESHOLD) return $"💕 Lover ({otherName})";
            if (Friendship >= RelationshipSystem.GOOD_FRIENDS_THRESHOLD) return $"🤝 Good Friend ({otherName})";
            if (Friendship >= RelationshipSystem.FRIENDSHIP_THRESHOLD) return $"👋 Friend ({otherName})";
            if (Rivalry >= Math.Abs(RelationshipSystem.ENEMY_THRESHOLD)) return $"⚔️ Enemy ({otherName})";
            if (Rivalry >= Math.Abs(RelationshipSystem.RIVALRY_THRESHOLD)) return $"😠 Rival ({otherName})";
            return $"👤 {otherName}";
        }
    }

    public enum InteractionOutcome
    {
        VeryPositive,
        Positive,
        Neutral,
        Negative,
        VeryNegative,
        Flirtatious,
        Romantic
    }
}
