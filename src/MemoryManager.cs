using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Memory management system that delegates all storage, RAG, and retrieval
    /// operations to the local Python dashboard server via HTTP.
    /// Includes a 250ms timeout and circuit breaker to prevent main-thread stutters.
    /// </summary>
    public class MemoryManager : IDisposable
    {
        private readonly string _serverUrl;
        private readonly HttpClient _httpClient;

        // Circuit breaker fields
        private bool _isServerOffline = false;
        private DateTime _lastConnectionAttempt = DateTime.MinValue;
        private readonly object _connLock = new object();

        // Max tokens injected into a prompt
        private const int DEFAULT_PROMPT_TOKENS = 1600;

        // Nullable LLM client reference (kept for compatibility)
        private LLMClient _llmClient;

        public string ActiveSaveId
        {
            get
            {
                return GetActiveSaveId();
            }
        }

        public static string GetActiveSaveId()
        {
            try
            {
                Type saveControllerType = Type.GetType("NSMedieval.GlobalSaveController, Assembly-CSharp");
                if (saveControllerType != null)
                {
                    PropertyInfo instanceProp = saveControllerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    object controllerInstance = instanceProp?.GetValue(null, null);
                    if (controllerInstance != null)
                    {
                        FieldInfo dataField = saveControllerType.GetField("CurrentVillageData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            ?? saveControllerType.GetField("currentVillageData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        object villageData = dataField?.GetValue(controllerInstance);
                        if (villageData != null)
                        {
                            Type villageDataType = villageData.GetType();
                            
                            PropertyInfo folderNameProp = villageDataType.GetProperty("FolderName", BindingFlags.Public | BindingFlags.Instance);
                            string folderName = folderNameProp?.GetValue(villageData, null) as string;
                            if (!string.IsNullOrEmpty(folderName)) return folderName;

                            PropertyInfo fileNameProp = villageDataType.GetProperty("FileName", BindingFlags.Public | BindingFlags.Instance);
                            string fileName = fileNameProp?.GetValue(villageData, null) as string;
                            if (!string.IsNullOrEmpty(fileName)) return fileName;

                            PropertyInfo mapSeedProp = villageDataType.GetProperty("MapSeed", BindingFlags.Public | BindingFlags.Instance);
                            string mapSeed = mapSeedProp?.GetValue(villageData, null) as string;
                            if (!string.IsNullOrEmpty(mapSeed)) return "seed_" + mapSeed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[MemoryManager] GetActiveSaveId failed: {ex.Message}");
            }
            return "default_save";
        }

        public MemoryManager(LLMClient llmClient = null)
        {
            LLMNPCsPlugin.LogToFile("[MemoryManager:Constructor] Initialized (HTTP Mode)");
            _llmClient = llmClient;
            _serverUrl = "http://127.0.0.1:8714";
            // Localhost calls should resolve in <10ms. A 250ms timeout detects offline/hung server immediately.
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };
        }

        public void SetLLMClient(LLMClient client) => _llmClient = client;

        // ─── Circuit Breaker & Status ──────────────────────────────────────────────

        private bool CheckServerStatus()
        {
            if (!_isServerOffline) return true;

            // Once offline, throttle retries to every 30 seconds to prevent constant connection attempts
            lock (_connLock)
            {
                if ((DateTime.UtcNow - _lastConnectionAttempt).TotalSeconds > 30)
                {
                    _lastConnectionAttempt = DateTime.UtcNow;
                    try
                    {
                        // Synchronously ping the server health endpoint
                        var task = _httpClient.GetAsync($"{_serverUrl}/health");
                        var response = task.GetAwaiter().GetResult();
                        if (response.IsSuccessStatusCode)
                        {
                            _isServerOffline = false;
                            LLMNPCsPlugin.LogToFile("[MemoryManager] Python server connection re-established. Circuit breaker reset.");
                            return true;
                        }
                    }
                    catch
                    {
                        // Still offline
                    }
                }
            }
            return false;
        }

        private void MarkServerOffline(Exception ex)
        {
            if (!_isServerOffline)
            {
                _isServerOffline = true;
                _lastConnectionAttempt = DateTime.UtcNow;
                LLMNPCsPlugin.LogToFile($"[MemoryManager] Python server connection failed. Circuit breaker active. Error: {ex.Message}");
            }
        }

        // ─── HTTP Helpers ──────────────────────────────────────────────────────────

        private void PostJson(string route, object payload)
        {
            if (_isServerOffline && !CheckServerStatus()) return;

            Task.Run(async () =>
            {
                try
                {
                    var json = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await _httpClient.PostAsync($"{_serverUrl}{route}", content);
                    if (!response.IsSuccessStatusCode)
                    {
                        LLMNPCsPlugin.LogToFile($"[MemoryManager:PostJson] Bad status response from {route}: {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    MarkServerOffline(ex);
                }
            });
        }

        public string HttpGetSync(string routeAndQuery)
        {
            if (_isServerOffline && !CheckServerStatus()) return null;

            try
            {
                var response = _httpClient.GetAsync($"{_serverUrl}{routeAndQuery}").GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                MarkServerOffline(ex);
            }
            return null;
        }

        public void HttpPostAsync(string route, object payload)
        {
            PostJson(route, payload);
        }

        // ─── Recording ─────────────────────────────────────────────────────────────

        public void RecordEvent(string npcId, string eventType, string content,
            int importance = 5, Dictionary<string, object> context = null)
        {
            if (string.IsNullOrWhiteSpace(npcId)) return;

            importance = Math.Max(1, Math.Min(10, importance));
            LLMNPCsPlugin.LogToFile($"[MemoryManager:RecordEvent] Forwarding event for {npcId} (imp={importance})");

            var payload = new Dictionary<string, object>
            {
                { "npc_id", npcId },
                { "event_type", eventType },
                { "content", content },
                { "importance", importance },
                { "save_id", ActiveSaveId }
            };

            PostJson("/api/memory/event", payload);
        }

        public void RecordLifeEvent(string npcId, string eventType, string content,
            Dictionary<string, object> ctx = null)
            => RecordEvent(npcId, eventType, content, importance: 10, context: ctx);

        // ─── Context Retrieval ──────────────────────────────────────────────────────

        /// <summary>
        /// Asynchronously queries the Python server to assemble memory context (recent + RoleRAG + RAG).
        /// </summary>
        public async Task<string> GetContextForPromptAsync(string npcId, string role, string query, int maxTokens = DEFAULT_PROMPT_TOKENS)
        {
            if (string.IsNullOrWhiteSpace(npcId)) return string.Empty;
            if (_isServerOffline && !CheckServerStatus()) return string.Empty;

            try
            {
                var saveId = Uri.EscapeDataString(ActiveSaveId);
                var escapedId = Uri.EscapeDataString(npcId);
                var escapedRole = Uri.EscapeDataString(role ?? "unemployed");
                var escapedQuery = Uri.EscapeDataString(query ?? "");
                var url = $"{_serverUrl}/api/memory/context?npc_id={escapedId}&save_id={saveId}&role={escapedRole}&query={escapedQuery}&max_tokens={maxTokens}";

                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    if (dict != null && dict.TryGetValue("context", out var contextVal))
                    {
                        return contextVal;
                    }
                }
            }
            catch (Exception ex)
            {
                MarkServerOffline(ex);
            }
            return string.Empty;
        }

        /// <summary>Legacy signature for async context retrieval without role.</summary>
        public Task<string> GetContextForPromptAsync(string npcId, string query, int maxTokens = DEFAULT_PROMPT_TOKENS)
            => GetContextForPromptAsync(npcId, "unemployed", query, maxTokens);

        /// <summary>
        /// Synchronously queries the Python server to assemble memory context.
        /// </summary>
        public string GetContextForPrompt(string npcId, string role, int maxTokens = DEFAULT_PROMPT_TOKENS)
        {
            if (string.IsNullOrWhiteSpace(npcId)) return string.Empty;
            if (_isServerOffline && !CheckServerStatus()) return string.Empty;

            try
            {
                var saveId = Uri.EscapeDataString(ActiveSaveId);
                var escapedId = Uri.EscapeDataString(npcId);
                var escapedRole = Uri.EscapeDataString(role ?? "unemployed");
                var url = $"{_serverUrl}/api/memory/context?npc_id={escapedId}&save_id={saveId}&role={escapedRole}&query=&max_tokens={maxTokens}";

                var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    if (dict != null && dict.TryGetValue("context", out var contextVal))
                    {
                        return contextVal;
                    }
                }
            }
            catch (Exception ex)
            {
                MarkServerOffline(ex);
            }
            return string.Empty;
        }

        /// <summary>Legacy signature for sync context retrieval without role.</summary>
        public string GetContextForPrompt(string npcId, int maxTokens = DEFAULT_PROMPT_TOKENS)
            => GetContextForPrompt(npcId, "unemployed", maxTokens);

        // ─── Player2 NPC Bindings ───────────────────────────────────────────────────

        public string GetNpcBinding(string settlerId)
        {
            if (string.IsNullOrWhiteSpace(settlerId)) return null;
            if (_isServerOffline && !CheckServerStatus()) return null;

            try
            {
                var saveId = Uri.EscapeDataString(ActiveSaveId);
                var escapedId = Uri.EscapeDataString(settlerId);
                var url = $"/api/memory/npc?settler_id={escapedId}&save_id={saveId}";

                var json = HttpGetSync(url);
                if (!string.IsNullOrEmpty(json))
                {
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                    if (dict != null && dict.TryGetValue("npc", out var npcObj) && npcObj != null)
                    {
                        var npcDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(npcObj.ToString());
                        if (npcDict != null && npcDict.TryGetValue("npc_id", out var npcIdVal))
                        {
                            return npcIdVal?.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[MemoryManager:GetNpcBinding] Failed: {ex.Message}");
            }
            return null;
        }

        public void SetNpcBinding(string settlerId, string npcId, string traits = null, string stats = null)
            => SetNpcBinding(settlerId, npcId, traits, stats, null, null);

        public void SetNpcBinding(string settlerId, string npcId, string traits, string stats, string name, string profession)
        {
            if (string.IsNullOrWhiteSpace(settlerId) || string.IsNullOrWhiteSpace(npcId)) return;

            var payload = new Dictionary<string, object>
            {
                { "settler_id", settlerId },
                { "save_id", ActiveSaveId },
                { "npc_id", npcId },
                { "traits", traits },
                { "stats", stats },
                { "name", name },
                { "profession", profession }
            };

            PostJson("/api/memory/npc", payload);
        }

        public void SaveCharacterSheet(NPCContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.Id)) return;

            var identity = new Dictionary<string, object>
            {
                { "name", context.Name },
                { "age", context.Age },
                { "gender", context.Gender },
                { "background_or_role", context.BackgroundOrRole },
                { "pseudonym", context.Pseudonym },
                { "profession", context.Profession }
            };

            var sheet = new Dictionary<string, object>
            {
                { "identity", identity },
                { "name", context.Name },
                { "age", context.Age },
                { "gender", context.Gender },
                { "background_or_role", context.BackgroundOrRole },
                { "pseudonym", context.Pseudonym },
                { "profession", context.Profession },
                { "health", context.Health },
                { "mood", context.Mood },
                { "mood_score", context.MoodScore },
                { "vitals", context.Vitals },
                { "states", context.States },
                { "needs", context.Needs },
                { "skills", context.Skills },
                { "skill_experience", context.SkillExperience },
                { "traits", context.Traits },
                { "perks", context.Perks },
                { "background_tags", context.BackgroundTags },
                { "equipment", context.Equipment },
                { "inventory", context.Inventory },
                { "current_activity", context.CurrentActivity },
                { "work_priorities", context.WorkPriorities },
                { "environment", context.Environment },
                { "relationships", context.Relationships },
                { "reputation", context.Reputation },
                { "mood_logs", context.MoodLogs },
                { "social_logs", context.SocialLogs },
                { "belief_logs", context.BeliefLogs },
                { "colony_wealth", context.ColonyWealth }
            };

            var payload = new Dictionary<string, object>
            {
                { "settler_id", context.Id },
                { "save_id", ActiveSaveId },
                { "sheet", sheet }
            };

            PostJson("/api/character-sheet", payload);
        }

        public async Task<string> GetDialogueStateForPromptAsync(string settlerId)
        {
            if (string.IsNullOrWhiteSpace(settlerId)) return string.Empty;
            if (_isServerOffline && !CheckServerStatus()) return string.Empty;

            try
            {
                var saveId = Uri.EscapeDataString(ActiveSaveId);
                var escapedId = Uri.EscapeDataString(settlerId);
                var url = $"{_serverUrl}/api/dialogue/state?settler_id={escapedId}&save_id={saveId}";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return string.Empty;

                var json = await response.Content.ReadAsStringAsync();
                var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (dict == null || !dict.TryGetValue("state", out var stateObj) || stateObj == null) return string.Empty;

                var state = JsonConvert.DeserializeObject<Dictionary<string, object>>(stateObj.ToString());
                if (state != null && state.TryGetValue("prompt_context", out var promptContext))
                {
                    return promptContext?.ToString() ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                MarkServerOffline(ex);
            }
            return string.Empty;
        }

        public void RecordDialogueExchange(
            string settlerId,
            string playerText,
            string npcText,
            IEnumerable<string> claims = null,
            float trustDelta = 0f,
            object contradiction = null,
            object barterIntent = null,
            string voiceProfile = null,
            string backstoryVoice = null)
        {
            if (string.IsNullOrWhiteSpace(settlerId)) return;

            var payload = new Dictionary<string, object>
            {
                { "settler_id", settlerId },
                { "save_id", ActiveSaveId },
                { "player_text", playerText },
                { "npc_text", npcText },
                { "claims", claims == null ? new List<string>() : new List<string>(claims) },
                { "trust_delta", trustDelta },
                { "contradiction", contradiction },
                { "barter_intent", barterIntent },
                { "voice_profile", voiceProfile },
                { "backstory_voice", backstoryVoice }
            };

            PostJson("/api/dialogue/exchange", payload);
        }

        // ─── Pressures ──────────────────────────────────────────────────────────────

        public void SaveSettlerPressures(string settlerId, SettlerPressures pressures)
        {
            if (string.IsNullOrWhiteSpace(settlerId) || pressures == null) return;

            var payload = new Dictionary<string, object>
            {
                { "settler_id", settlerId },
                { "save_id", ActiveSaveId },
                { "hunger", pressures.Hunger },
                { "thirst", pressures.Thirst },
                { "exhaustion", pressures.Exhaustion },
                { "injury", pressures.Injury },
                { "illness", pressures.IllnessPenalty },
                { "threat", pressures.ThreatLevel },
                { "raid", pressures.RaidAlert },
                { "mood", pressures.MoodDistress },
                { "recreation", pressures.RecreationLag },
                { "work_skill", pressures.WorkSkillMismatch },
                { "idle", pressures.IdlePressure },
                { "social", pressures.SocialDebt },
                { "rel_tension", pressures.RelationshipTension },
                { "colony_need", pressures.ColonyNeedScore },
                { "haul", pressures.HaulNeed },
                { "attire", pressures.AttireMismatch }
            };

            PostJson("/api/memory/pressures", payload);
        }

        public void SaveSettlerPressures(string settlerId, float hunger, float injury, float exhaustion, float mood)
        {
            SaveSettlerPressures(settlerId, new SettlerPressures
            {
                Hunger = hunger,
                Injury = injury,
                Exhaustion = exhaustion,
                MoodDistress = mood
            });
        }

        // ─── Incidents ──────────────────────────────────────────────────────────────

        public void RecordIncident(string settlerId, string action, string reasoning, bool success)
        {
            if (string.IsNullOrWhiteSpace(settlerId)) return;

            var payload = new Dictionary<string, object>
            {
                { "settler_id", settlerId },
                { "save_id", ActiveSaveId },
                { "action", action },
                { "reasoning", reasoning },
                { "success", success ? 1 : 0 }
            };

            PostJson("/api/memory/incident", payload);
        }

        // ─── Colony Events ──────────────────────────────────────────────────────────

        public void RecordColonyEvent(ColonyState state, ColonyRecommendation rec, string narrative)
        {
            if (state == null || rec == null) return;

            var payload = new Dictionary<string, object>
            {
                { "save_id", ActiveSaveId },
                { "state", state },
                { "rec", rec },
                { "narrative", narrative }
            };

            PostJson("/api/colony/event", payload);
        }

        // ─── Relationships (Legacy Helper) ──────────────────────────────────────────

        public void SaveRelationship(string npcAId, string npcBId, float standing, float trust, float fear, float resentment)
        {
            var payload = new Dictionary<string, object>
            {
                { "save_id", ActiveSaveId },
                { "subject", npcAId },
                { "object", npcBId },
                { "standing", standing },
                { "trust", trust },
                { "fear", fear },
                { "resentment", resentment }
            };

            PostJson("/api/memory/relationship", payload);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    public static class MemoryImportance
    {
        public const int Permanent = 10;
        public const int LifeEvent = 9;
        public const int Major = 8;
        public const int Significant = 6;
        public const int Routine = 4;
        public const int Noise = 2;
    }

    public class MemoryRecord
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType { get; set; }
        public string Content { get; set; }
        public int Importance { get; set; }
        public string ContextJson { get; set; }
        public float[] Embedding { get; set; }
    }
}
