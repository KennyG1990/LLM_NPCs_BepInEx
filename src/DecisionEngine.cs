using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    public class ScoredOption
    {
        public string Name { get; set; }
        public float Score { get; set; }
        public string Description { get; set; }

        public ScoredOption(string name, float score, string description)
        {
            Name = name;
            Score = score;
            Description = description;
        }
    }

    /// <summary>
    /// Orchestrates the 3-Stage Influence Engine for NPCs.
    /// Stage 1: Deterministic Needs mathematical scoring.
    /// Stage 2: Bounded LLM decision calling via Player2 daemon.
    /// Stage 3: Validation, database incident logging, and main-thread execution.
    /// </summary>
    public class DecisionEngine
    {
        private readonly LLMClient _llmClient;
        private readonly NPCRegistry _npcRegistry;
        private readonly MemoryManager _memoryManager;
        private readonly Dictionary<string, DateTime> _lastSkipLogByReason = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private const double SkipLogIntervalSeconds = 15d;

        public DecisionEngine(LLMClient llmClient, NPCRegistry npcRegistry, MemoryManager memoryManager)
        {
            _llmClient = llmClient;
            _npcRegistry = npcRegistry;
            _memoryManager = memoryManager;
            LLMNPCsPlugin.LogToFile("[DecisionEngine:Constructor] Initialized");
        }

        public void TriggerImmediateEvaluation(string reason)
        {
            LLMNPCsPlugin.Log.LogInfo($"[DecisionEngine] Immediate evaluation triggered by: {reason}");
            LLMNPCsPlugin.LogToFile($"[DecisionEngine:TriggerImmediateEvaluation] Reason: {reason}");
            _npcRegistry.ForceImmediateEvaluation();
        }

        /// <summary>
        /// Gets a decision from the LLM for a settler using the 3-Stage Influence Engine.
        /// </summary>
        public async Task<LLMDecision> GetDecisionAsync(NPCContext context)
        {
            if (context == null)
            {
                LogSkipThrottled("context-null", "context is null");
                return null;
            }

            if (string.IsNullOrWhiteSpace(context.Id))
            {
                LogSkipThrottled("context-id-empty", $"context ID is null/empty (name={context.Name ?? "unknown"})");
                return null;
            }

            var contextSettler = FindSettlerById(context.Id);
            if (contextSettler == null || contextSettler.gameObject == null)
            {
                LogSkipThrottled($"settler-missing-{context.Id}", $"settler is null/invalid for id {context.Id} ({context.Name ?? "unknown"})");
                return null;
            }

            LLMNPCsPlugin.LogToFile($"[DecisionEngine:GetDecisionAsync] Starting 3-Stage Influence Engine for {context.Name} ({context.Id})");

            // ==========================================
            // STAGE 1: Deterministic Pressures & Scoring
            // ==========================================

            // --- Domain 1: Survival ---
            float hunger     = (100f - (context.Needs?.Food       ?? 50f)) / 100f;
            float thirst     = (100f - (context.Needs?.Water      ?? 100f)) / 100f;
            float exhaustion = (100f - (context.Needs?.Rest       ?? 50f)) / 100f;

            // Multiply by domain weights
            hunger     *= 1.4f;
            thirst     *= 1.3f;
            exhaustion *= 1.2f;

            // Critical-need spikes
            if (context.States != null && context.States.Contains("isStarving"))  hunger  = Math.Max(hunger,  2.0f);
            if (context.States != null && context.States.Contains("isThirsty"))   thirst  = Math.Max(thirst,  1.8f);
            if (context.States != null && context.States.Contains("isExhausted")) exhaustion = Math.Max(exhaustion, 2.0f);

            // --- Domain 2: Health ---
            float injury = 0f;
            if (context.Health != null && context.Health.Max > 0)
                injury = (context.Health.Max - context.Health.Current) / context.Health.Max;
            float illnessPenalty = 0f;
            if (context.States != null && context.States.Contains("isInjured"))
            {
                injury = Math.Max(injury, 0.4f);
                illnessPenalty = 0.3f;
            }
            if (context.States != null && context.States.Contains("isSick"))
                illnessPenalty = Math.Max(illnessPenalty, 0.4f);
            injury *= 1.1f;

            // --- Domain 3: Threat ---
            int nearbyThreatCount = context.Environment?.NearbyThreats?.Count ?? 0;
            float threat    = nearbyThreatCount > 0 ? 1.5f + nearbyThreatCount * 0.1f : 0f;
            float raidAlert = 0f; // populated by future raid-state extractor

            // --- Domain 4: Mood ---
            float moodDistress  = (100f - context.MoodScore) / 100f * 0.9f;
            float recreationLag = (100f - (context.Needs?.Recreation ?? 50f)) / 100f * 0.6f;

            // --- Domain 5: Work ---
            // Simple proxy: if profession matches a top skill we have a good fit; otherwise slight mismatch
            float workSkillMismatch = 0f;
            if (context.Skills != null && context.Skills.Count > 0)
            {
                var profession = context.Profession ?? context.BackgroundOrRole ?? string.Empty;
                bool hasProfessionSkill = context.Skills.ContainsKey(profession) &&
                                          context.Skills[profession] >= 3;
                workSkillMismatch = hasProfessionSkill ? 0f : 0.25f;
            }
            float idlePressure = (context.CurrentActivity == null ||
                                  string.IsNullOrEmpty(context.CurrentActivity.Type) ||
                                  context.CurrentActivity.Type == "idle")
                                 ? 0.35f : 0f;

            // --- Domain 6: Social ---
            var personality = _npcRegistry.GetPersonality(contextSettler);
            float sociability = personality?.Sociability ?? 0.5f;
            float socialDebt = ((100f - (context.Needs?.Recreation ?? 50f)) / 100f) * sociability * 0.7f;
            // Relationship tension pulled from RelationshipSystem if available
            float relTension = 0f;
            if (LLMNPCsPlugin.Instance?.RelationshipSystem != null)
            {
                relTension = LLMNPCsPlugin.Instance.RelationshipSystem.GetRelationshipPressure(context.Id);
            }
            else if (context.Relationships != null && context.Relationships.Count > 0)
            {
                foreach (var rel in context.Relationships.Values)
                {
                    if (rel.Opinion < -20f)
                        relTension = Math.Min(1f, relTension + 0.15f);
                }
            }
            relTension *= 0.6f;

            // --- Domain 7: Construction ---
            // Simple proxy: if there are haul items or repair needed, score rises
            float colonyNeedScore = 0.2f; // baseline; Phase 3 will populate from BuildingsManager
            float haulNeed        = 0.2f; // baseline

            // --- Domain 8: Attire ---
            float attireMismatch = 0f;
            bool isOutdoors = context.Environment?.Room == null ||
                              context.Environment.Room.ToLower().Contains("outdoor") ||
                              context.Environment.Room.ToLower().Contains("outside");
            string weather = context.Environment?.Weather ?? "clear";
            bool badWeather = weather != "clear" && !string.IsNullOrEmpty(weather);
            if (badWeather && isOutdoors)
                attireMismatch = 0.4f;
            bool hasNoArmor = context.Equipment?.Armor == null || context.Equipment.Armor == "none";
            bool hasNoClothing = context.Equipment?.Clothing == null || context.Equipment.Clothing == "none";
            if (hasNoArmor && hasNoClothing) attireMismatch = Math.Max(attireMismatch, 0.3f);
            attireMismatch *= 0.3f; // low weight â€” not life-threatening

            // Build and persist the SettlerPressures struct
            var pressures = new SettlerPressures
            {
                Hunger              = hunger,
                Thirst              = thirst,
                Exhaustion          = exhaustion,
                Injury              = injury,
                IllnessPenalty      = illnessPenalty,
                ThreatLevel         = threat,
                RaidAlert           = raidAlert,
                MoodDistress        = moodDistress,
                RecreationLag       = recreationLag,
                WorkSkillMismatch   = workSkillMismatch,
                IdlePressure        = idlePressure,
                SocialDebt          = socialDebt,
                RelationshipTension = relTension,
                ColonyNeedScore     = colonyNeedScore,
                HaulNeed            = haulNeed,
                AttireMismatch      = attireMismatch
            };
            _memoryManager.SaveSettlerPressures(context.Id, pressures);

            // ---- Build Scored Options from all 8 domains ----
            var options = new List<ScoredOption>();

            // --- Survival actions ---
            options.Add(new ScoredOption("eat",           hunger,     "Find and eat food to satisfy hunger."));
            options.Add(new ScoredOption("drink",         thirst,     "Find and drink water or a beverage."));
            options.Add(new ScoredOption("rest",          exhaustion, "Sleep in a bed or resting spot to recover energy."));

            // --- Health actions ---
            options.Add(new ScoredOption("seek_medic",   injury * 1.0f,           "Seek medical attention or treatment for injuries."));

            // --- Threat actions ---
            float aggression = personality?.Aggression ?? 0.5f;
            float fleeScore    = threat * (1f - aggression * 0.5f);
            float defendScore  = threat > 0f ? 0.4f + aggression * 0.8f : 0f;
            options.Add(new ScoredOption("flee",          fleeScore,  "Flee immediately from dangerous threats."));
            options.Add(new ScoredOption("defend",        defendScore,"Equip weapons and prepare for combat to defend the colony."));
            options.Add(new ScoredOption("seek_shelter",  badWeather && isOutdoors ? 0.6f : 0f,
                                                          "Seek shelter indoors away from bad weather."));

            // --- Mood actions ---
            options.Add(new ScoredOption("socialize",    socialDebt, "Talk to and socialize with another settler."));
            options.Add(new ScoredOption("complain",     moodDistress * 0.6f,
                                                          "Vent frustration to a nearby settler to improve mood."));

            // --- Work actions ---
            options.Add(new ScoredOption("continue_job",  0.4f + (1f - workSkillMismatch) * 0.2f,
                                                           "Continue your current job or task."));
            options.Add(new ScoredOption("switch_job",     workSkillMismatch * 0.6f + idlePressure * 0.4f,
                                                           "Switch to a different job that better fits your skills."));
            options.Add(new ScoredOption("explore",       idlePressure * 0.5f, "Explore the map or survey surroundings."));

            // --- Construction/work actions ---
            options.Add(new ScoredOption("gather",                   0.3f, "Gather raw resources or materials."));
            options.Add(new ScoredOption("haul",                     haulNeed * 0.8f, "Haul battlefield items or colony resources."));
            options.Add(new ScoredOption("repair",                   0.25f, "Repair damaged colony buildings."));
            options.Add(new ScoredOption("build_special",            moodDistress * 0.4f,
                                                                      "Construct a special mood-boosting building."));
            options.Add(new ScoredOption("prioritize_construction",  colonyNeedScore * 0.7f,
                                                                      "Focus on completing the colony's most needed structure."));
            options.Add(new ScoredOption("build_stockpile",          colonyNeedScore * 0.6f,
                                                                      "Raise a new stockpile zone near you so the colony has space to store food and resources."));
            options.Add(new ScoredOption("rebrand",                  0.15f, "Upgrade wooden walls to stone versions."));

            // --- Attire actions ---
            options.Add(new ScoredOption("change_clothing", attireMismatch * 0.8f,
                                                              "Change to more appropriate clothing for current conditions."));

            // --- Other ---
            options.Add(new ScoredOption("draft",   0.0f, "Draft for combat direction."));
            options.Add(new ScoredOption("capture", 0.0f, "Capture downed enemies."));

            // Sort descending by score
            options.Sort((a, b) => b.Score.CompareTo(a.Score));
            var topMathChoice = options[0];
            LLMNPCsPlugin.LogToFile($"[DecisionEngine] Stage 1 top choice: {topMathChoice.Name} ({topMathChoice.Score:F2}) | DomainMax={pressures.DomainMax:F2}");

            // ==========================================
            // STAGE 2: Bounded LLM Selection (Player2)
            // ==========================================
            var fallbackDecision = GetFallbackDecision(context);

            // Connect to local companion daemon
            if (_llmClient != null && await _llmClient.CheckHealthAsync())
            {
                string npcId = await GetOrCreateNpcIdAsync(context);
                if (!string.IsNullOrEmpty(npcId))
                {
                    // Compile scored priorities prompt
                    var sb = new StringBuilder();
                    sb.AppendLine("=== CURRENT BODY STATUS & ENVIRONMENT ===");
                    sb.AppendLine($"- Hunger Level: {hunger:P0}");
                    sb.AppendLine($"- Tiredness: {exhaustion:P0}");
                    sb.AppendLine($"- Physical Pain/Injury: {injury:P0}");
                    sb.AppendLine($"- Mood Distress: {moodDistress:P0}");
                    if (context.Environment?.NearbyThreats?.Count > 0)
                    {
                        sb.AppendLine($"- WARNING: {context.Environment.NearbyThreats.Count} threat(s) nearby!");
                    }
                    sb.AppendLine();
                    // PRIORITY: the whole colony's urgent needs — the settler should
                    // weigh these heavily (a starving, shelterless colony needs food
                    // and building far more than personal errands).
                    sb.AppendLine("=== COLONY PRIORITIES (URGENT — the whole settlement) ===");
                    sb.AppendLine(ColonyAlerts.Current);
                    sb.AppendLine();
                    sb.AppendLine("=== EVALUATED ACTION OPTIONS (Ranked by priority) ===");
                    for (int i = 0; i < Math.Min(5, options.Count); i++)
                    {
                        sb.AppendLine($"{i+1}. {options[i].Name} (Mathematical Score: {options[i].Score:F2}) - {options[i].Description}");
                    }
                    sb.AppendLine();
                    sb.AppendLine("Decide which action to take. Select the appropriate command corresponding to your choice, and express your medieval thoughts.");

                    string senderMessage = sb.ToString();
                    string gameStateInfo = JsonConvert.SerializeObject(context);

                    NpcResponse response = null;
                    try
                    {
                        response = await RequestNpcDecisionWithCommandRepairAsync(npcId, senderMessage, gameStateInfo, options, context);
                    }
                    catch (HttpRequestException ex) when (ex.Message.Contains("404"))
                    {
                        LLMNPCsPlugin.LogToFile($"[DecisionEngine] NPC {npcId} not found (404), resetting binding and recreating...");
                        _memoryManager.SetNpcBinding(context.Id, "");
                        npcId = await GetOrCreateNpcIdAsync(context);
                        if (!string.IsNullOrEmpty(npcId))
                        {
                            try
                            {
                                response = await RequestNpcDecisionWithCommandRepairAsync(npcId, senderMessage, gameStateInfo, options, context);
                            }
                            catch (Exception innerEx)
                            {
                                LLMNPCsPlugin.LogToFile($"[DecisionEngine] Retry chat failed: {innerEx.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LLMNPCsPlugin.LogToFile($"[DecisionEngine] Player2 NPC chat failed: {ex.Message}");
                    }

                    // ==========================================
                    // STAGE 3: Action Validation & Execution
                    // ==========================================
                    if (response != null && response.Command != null)
                    {
                        try
                        {
                            var parsed = ParseNpcCommand(response.Command);
                            string actionName = parsed.ActionName;
                            var parameters = parsed.Parameters;

                            if (IsWhitelistedAction(actionName))
                            {
                                string reasoning = response.Message;
                                if (string.IsNullOrWhiteSpace(reasoning) &&
                                    parameters != null &&
                                    parameters.TryGetValue("_internal_message", out var internalMessage) &&
                                    internalMessage != null)
                                {
                                    reasoning = internalMessage.ToString();
                                }

                                var decision = new LLMDecision
                                {
                                    Success = true,
                                    Action = actionName.ToLower(),
                                    Parameters = parameters,
                                    Reasoning = string.IsNullOrWhiteSpace(reasoning) ? $"Chose command {actionName}" : reasoning,
                                    DialogueComplaint = reasoning
                                };

                                // Validate against critical needs and threats
                                if (ValidateDecision(decision, context, contextSettler))
                                {
                                    LLMNPCsPlugin.LogToFile($"[DecisionEngine] Stage 2 validated action: {decision.Action}");
                                    _memoryManager.RecordIncident(context.Id, decision.Action, decision.Reasoning, true);
                                    return decision;
                                }
                                else
                                {
                                    LLMNPCsPlugin.LogToFile($"[DecisionEngine] Stage 2 action '{decision.Action}' failed safety validation. Falling back.");
                                }
                            }
                            else
                            {
                                LLMNPCsPlugin.LogToFile($"[DecisionEngine] Stage 2 returned non-whitelisted command: {actionName}; raw={TrimForLog(response.Command.ToString(Formatting.None), 240)}");
                            }
                        }
                        catch (Exception ex)
                        {
                            LLMNPCsPlugin.LogToFile($"[DecisionEngine] Error parsing Player2 response command: {ex.Message}; raw={TrimForLog(response.Command.ToString(Formatting.None), 240)}");
                        }
                    }
                }
            }

            // ==========================================
            // DETERMINISTIC FALLBACK
            // ==========================================
            LLMNPCsPlugin.LogToFile($"[DecisionEngine] Triggering Stage 1 deterministic fallback: {topMathChoice.Name}");
            var fallback = new LLMDecision
            {
                Success = true,
                Action = topMathChoice.Name,
                Parameters = new Dictionary<string, object>(),
                Reasoning = $"Deterministic fallback based on need priorities: {topMathChoice.Description}",
                DialogueComplaint = $"ðŸ’­ I must focus on {topMathChoice.Name} right now..."
            };

            _memoryManager.RecordIncident(context.Id, fallback.Action, fallback.Reasoning, false);
            return fallback;
        }

        private async Task<string> GetOrCreateNpcIdAsync(NPCContext context)
        {
            var npcId = _memoryManager.GetNpcBinding(context.Id);
            if (!string.IsNullOrEmpty(npcId))
            {
                return npcId;
            }

            var traitsStr = (context.Traits != null && context.Traits.Count > 0) 
                ? string.Join(", ", context.Traits) 
                : "average medieval settler";
            
            var systemPrompt = $"You are {context.Name}, a {context.Age}-year-old {context.Gender} {context.BackgroundOrRole} in a medieval colony. " +
                               $"Personality Traits: {traitsStr}. " +
                               "You think, reason, and react like a medieval settler. Your goal is to survive, work, and interact. " +
                               "Answer in the first person with your short, in-character thoughts (1-2 sentences) and choose the command matching your intent.";

            try
            {
                LLMNPCsPlugin.LogToFile($"[DecisionEngine] Spawning new Player2 NPC for {context.Name} ({context.Id})");
                var newNpcId = await _llmClient.SpawnNpcAsync(context.Id, context.Name, context.BackgroundOrRole, systemPrompt);
                if (!string.IsNullOrEmpty(newNpcId))
                {
                    _memoryManager.SetNpcBinding(context.Id, newNpcId, traitsStr, null, context.Name, context.BackgroundOrRole);
                    LLMNPCsPlugin.LogToFile($"[DecisionEngine] Bound {context.Name} to Player2 NPC ID: {newNpcId}");
                    return newNpcId;
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[DecisionEngine] Failed to spawn Player2 NPC: {ex.Message}");
            }

            return null;
        }

        private class ParsedCompletionDecision
        {
            public string ActionName { get; set; }
            public string Thought { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        }

        private async Task<NpcResponse> RequestNpcDecisionWithCommandRepairAsync(
            string npcId,
            string senderMessage,
            string gameStateInfo,
            List<ScoredOption> options,
            NPCContext context)
        {
            var response = await _llmClient.NpcChatAsync(npcId, "System", senderMessage, gameStateInfo);
            if (HasUsableCommand(response))
            {
                return response;
            }

            LLMNPCsPlugin.LogToFile($"[DecisionEngine] Player2 returned no command for {context.Id}; requesting bounded command repair. message={TrimForLog(response?.Message, 240)}");
            var repairPrompt = BuildCommandRepairPrompt(options);
            response = await _llmClient.NpcChatAsync(npcId, "System", repairPrompt, gameStateInfo);
            if (HasUsableCommand(response))
            {
                return response;
            }

            LLMNPCsPlugin.LogToFile($"[DecisionEngine] Player2 command repair failed for {context.Id}; respawning NPC binding once. message={TrimForLog(response?.Message, 240)}");
            _memoryManager.SetNpcBinding(context.Id, "");
            var newNpcId = await GetOrCreateNpcIdAsync(context);
            if (string.IsNullOrWhiteSpace(newNpcId))
            {
                return response;
            }

            response = await _llmClient.NpcChatAsync(newNpcId, "System", senderMessage, gameStateInfo);
            if (HasUsableCommand(response))
            {
                return response;
            }

            LLMNPCsPlugin.LogToFile($"[DecisionEngine] Respawned Player2 NPC still returned no command for {context.Id}. message={TrimForLog(response?.Message, 240)}");
            return response;
        }

        private bool HasUsableCommand(NpcResponse response)
        {
            return response?.Command != null && response.Command.Type != JTokenType.Null;
        }

        private string BuildCommandRepairPrompt(List<ScoredOption> options)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Your previous response did not include a command. You must now select exactly one valid command.");
            sb.AppendLine("Do not answer with only prose. Use the command interface for one of these top options:");
            for (int i = 0; i < Math.Min(5, options.Count); i++)
            {
                sb.AppendLine($"- {options[i].Name}: {options[i].Description}");
            }
            sb.AppendLine("Keep your in-character thought short, but the command selection is mandatory.");
            return sb.ToString();
        }

        private ParsedCompletionDecision ParseNpcCommand(JToken commandToken)
        {
            if (commandToken == null || commandToken.Type == JTokenType.Null)
            {
                throw new ArgumentException("Player2 command was empty");
            }

            if (commandToken.Type == JTokenType.String)
            {
                return new ParsedCompletionDecision
                {
                    ActionName = commandToken.ToString().Trim()
                };
            }

            if (commandToken.Type == JTokenType.Array)
            {
                var arr = (JArray)commandToken;
                if (arr.Count == 0)
                {
                    throw new ArgumentException("Player2 command array was empty");
                }
                return ParseNpcCommand(arr[0]);
            }

            if (commandToken.Type != JTokenType.Object)
            {
                throw new ArgumentException($"Unsupported Player2 command token type: {commandToken.Type}");
            }

            var obj = (JObject)commandToken;
            var actionName =
                obj["name"]?.ToString()?.Trim()
                ?? obj["type"]?.ToString()?.Trim()
                ?? obj["command"]?.ToString()?.Trim()
                ?? obj["action"]?.ToString()?.Trim();

            var parameters =
                obj["parameters"]?.ToObject<Dictionary<string, object>>()
                ?? obj["params"]?.ToObject<Dictionary<string, object>>()
                ?? new Dictionary<string, object>();

            if (obj["arguments"] != null)
            {
                if (obj["arguments"].Type == JTokenType.String)
                {
                    var argText = obj["arguments"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(argText))
                    {
                        try
                        {
                            var argObj = JObject.Parse(argText);
                            foreach (var prop in argObj.Properties())
                            {
                                parameters[prop.Name] = prop.Value.Type == JTokenType.Null ? null : prop.Value.ToObject<object>();
                            }
                        }
                        catch
                        {
                            parameters["arguments"] = argText;
                        }
                    }
                }
                else if (obj["arguments"].Type == JTokenType.Object)
                {
                    foreach (var prop in ((JObject)obj["arguments"]).Properties())
                    {
                        parameters[prop.Name] = prop.Value.Type == JTokenType.Null ? null : prop.Value.ToObject<object>();
                    }
                }
            }

            foreach (var prop in obj.Properties())
            {
                if (string.Equals(prop.Name, "name", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "type", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "command", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "action", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "arguments", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "parameters", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "params", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!parameters.ContainsKey(prop.Name))
                {
                    parameters[prop.Name] = prop.Value.Type == JTokenType.Null ? null : prop.Value.ToObject<object>();
                }
            }

            return new ParsedCompletionDecision
            {
                ActionName = actionName,
                Parameters = parameters
            };
        }

        private ParsedCompletionDecision ParseCompletionDecision(string rawText)
        {
            var normalized = (rawText ?? string.Empty).Trim();
            if (normalized.StartsWith("```", StringComparison.Ordinal))
            {
                normalized = normalized.Trim('`').Trim();
                if (normalized.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(4).Trim();
                }
            }

            try
            {
                var obj = JObject.Parse(normalized);
                return new ParsedCompletionDecision
                {
                    ActionName = obj["command"]?.ToString()?.Trim(),
                    Thought = obj["thought"]?.ToString()?.Trim(),
                    Parameters = obj["parameters"]?.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>()
                };
            }
            catch
            {
                var lowered = normalized.ToLowerInvariant();
                foreach (var action in new[] { "flee", "defend", "eat", "rest", "seek_shelter", "continue_job", "switch_job", "socialize", "gather", "haul", "repair", "complain" })
                {
                    if (lowered.Contains(action))
                    {
                        return new ParsedCompletionDecision
                        {
                            ActionName = action,
                            Thought = normalized
                        };
                    }
                }
                throw;
            }
        }

        private string TrimForLog(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text;
            }
            return text.Substring(0, maxLength) + "...";
        }

        private bool IsWhitelistedAction(string action)
        {
            if (string.IsNullOrEmpty(action)) return false;
            var whitelisted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Survival
                "eat", "drink", "rest",
                // Health
                "seek_medic",
                // Threat
                "flee", "defend", "seek_shelter",
                // Mood / Social
                "socialize", "complain",
                // Work
                "continue_job", "switch_job", "explore",
                // Construction / Work
                "gather", "haul", "repair", "build_special", "prioritize_construction", "build_stockpile", "rebrand",
                // Attire
                "change_clothing",
                // Other
                "draft", "capture"
            };
            return whitelisted.Contains(action);
        }

        /// <summary>
        /// Validates that a decision is appropriate for the NPC's state.
        /// </summary>
        private bool ValidateDecision(LLMDecision decision, NPCContext context, Settler settler)
        {
            LLMNPCsPlugin.LogToFile($"[DecisionEngine:ValidateDecision] Checking action: {decision.Action}");
            if (string.IsNullOrEmpty(decision.Action))
            {
                LLMNPCsPlugin.LogToFile("[DecisionEngine:ValidateDecision] FAILED: No action");
                return false;
            }

            // Check for critical survival needs â€” only survival/threat actions pass
            if (context.Needs != null)
            {
                bool criticalFood  = context.Needs.Food  < 20;
                bool criticalWater = context.Needs.Water < 20;
                bool criticalRest  = context.Needs.Rest  < 20;

                if (criticalFood || criticalWater || criticalRest)
                {
                    var survivalActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "eat", "drink", "rest", "seek_shelter", "flee", "seek_medic" };
                    if (!survivalActions.Contains(decision.Action))
                    {
                        LLMNPCsPlugin.Log.LogDebug($"Overriding non-survival action during critical needs");
                        LLMNPCsPlugin.LogToFile("[DecisionEngine:ValidateDecision] FAILED: Non-survival during critical needs");
                        return false;
                    }
                }
            }

            // Check for threats â€” must respond with defensive action
            if (context.Environment?.NearbyThreats?.Count > 0)
            {
                var defensiveActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "flee", "defend", "seek_shelter", "seek_medic" };
                if (!defensiveActions.Contains(decision.Action))
                {
                    LLMNPCsPlugin.Log.LogDebug($"Overriding non-defensive action during threat");
                    LLMNPCsPlugin.LogToFile("[DecisionEngine:ValidateDecision] FAILED: Non-defensive during threat");
                    return false;
                }
            }

            // Check captive status
            if (_npcRegistry.IsCaptive(settler))
            {
                var captiveActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "gather", "eat", "drink", "rest" };
                if (!captiveActions.Contains(decision.Action))
                {
                    LLMNPCsPlugin.Log.LogDebug($"Overriding non-captive action for captive settler ({context.Name})");
                    LLMNPCsPlugin.LogToFile("[DecisionEngine:ValidateDecision] FAILED: Invalid action for captive");
                    return false;
                }
            }

            // change_clothing only passes if settler has equipment context
            if (string.Equals(decision.Action, "change_clothing", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Equipment == null)
                {
                    LLMNPCsPlugin.LogToFile("[DecisionEngine:ValidateDecision] FAILED: change_clothing but no equipment context");
                    return false;
                }
            }

            LLMNPCsPlugin.LogToFile("[DecisionEngine:ValidateDecision] PASSED");
            return true;
        }

        /// <summary>
        /// Returns a context-appropriate fallback decision when LLM decision is invalid.
        /// </summary>
        private LLMDecision GetFallbackDecision(NPCContext context)
        {
            string action   = "continue_job";
            string reasoning = "Fallback: continuing current task";

            if (context.Needs != null)
            {
                if (context.Needs.Food  < 20) { action = "eat";   reasoning = "Fallback: critically hungry"; }
                else if (context.Needs.Water < 20) { action = "drink"; reasoning = "Fallback: critically thirsty"; }
                else if (context.Needs.Rest  < 20) { action = "rest";  reasoning = "Fallback: critically tired"; }
            }

            if (context.Environment?.NearbyThreats?.Count > 0)
            {
                action   = "flee";
                reasoning = "Fallback: threats nearby";
            }

            if (context.States != null && context.States.Contains("isInjured") && action == "continue_job")
            {
                action   = "seek_medic";
                reasoning = "Fallback: injured, seeking medical attention";
            }

            LLMNPCsPlugin.LogToFile($"[DecisionEngine:GetFallbackDecision] {action} - {reasoning}");

            return new LLMDecision
            {
                Success         = true,
                Action          = action,
                Parameters      = new Dictionary<string, object>(),
                Reasoning       = reasoning,
                DialogueComplaint = $"ðŸ’­ I must focus on {action} right now..."
            };
        }

        private Settler FindSettlerById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var go = GameBridge.FindSettlerById(id);
            if (go == null)
                return null;

            if (!GameBridge.TryGetValidatedSettlerIdentity(go, out var resolvedId, out _, out _))
                return null;

            if (!string.Equals(resolvedId, id, StringComparison.Ordinal))
                return null;

            return GameBridge.EnsureSettlerComponent(go);
        }

        private void LogSkipThrottled(string reasonKey, string detail)
        {
            var now = DateTime.UtcNow;
            if (_lastSkipLogByReason.TryGetValue(reasonKey, out var last)
                && (now - last).TotalSeconds < SkipLogIntervalSeconds)
            {
                return;
            }

            _lastSkipLogByReason[reasonKey] = now;
            LLMNPCsPlugin.LogToFile($"[DecisionEngine:GetDecisionAsync] Skipping tick: {detail}");
        }
    }

    /// <summary>
    /// Executes LLM decisions in the game world.
    /// </summary>
    public static class DecisionExecutor
    {
        private static readonly Dictionary<string, DateTime> _lastSwitchJobAttemptUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _lastSwitchJobName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private const double SwitchJobRepeatGuardSeconds = 30d;

        public static void Execute(Settler settler, LLMDecision decision)
        {
            if (settler == null || decision == null)
                return;

            try
            {
                switch (decision.Action.ToLower())
                {
                    case "continue_job":
                        ExecuteContinueJob(settler);
                        break;

                    case "switch_job":
                        var jobName = GetParam<string>(decision.Parameters, "job");
                        ExecuteSwitchJob(settler, jobName);
                        break;

                    case "rest":
                        ExecuteRest(settler);
                        break;

                    case "eat":
                        ExecuteEat(settler);
                        break;

                    case "socialize":
                        var target = GetParam<string>(decision.Parameters, "target");
                        ExecuteSocialize(settler, target);
                        break;

                    case "flee":
                        ExecuteFlee(settler);
                        break;

                    case "defend":
                        ExecuteDefend(settler);
                        break;

                    case "seek_shelter":
                        ExecuteSeekShelter(settler);
                        break;

                    case "explore":
                        ExecuteExplore(settler);
                        break;

                    case "gather":
                        var resource = GetParam<string>(decision.Parameters, "resource");
                        ExecuteGather(settler, resource);
                        break;

                    case "build_special":
                        var buildingName = GetParam<string>(decision.Parameters, "building_name");
                        ExecuteBuildSpecial(settler, buildingName);
                        break;

                    case "draft":
                        var targetCoords = GetParam<string>(decision.Parameters, "target_coordinates");
                        ExecuteDraft(settler, targetCoords);
                        break;

                    case "repair":
                        ExecuteRepair(settler);
                        break;

                    case "haul":
                        ExecuteHaul(settler);
                        break;

                    case "capture":
                        var captureTarget = GetParam<string>(decision.Parameters, "target");
                        ExecuteCapture(settler, captureTarget);
                        break;

                    case "rebrand":
                        ExecuteRebrand(settler);
                        break;

                    // ---- New Phase 1 actions ----
                    case "drink":
                        ExecuteDrink(settler);
                        break;

                    case "complain":
                        var complaintTarget = GetParam<string>(decision.Parameters, "target");
                        ExecuteComplain(settler, complaintTarget, decision.DialogueComplaint);
                        break;

                    case "change_clothing":
                        var clothingType = GetParam<string>(decision.Parameters, "clothing_type");
                        ExecuteChangeClothing(settler, clothingType);
                        break;

                    case "seek_medic":
                        ExecuteSeekMedic(settler);
                        break;

                    case "prioritize_construction":
                        var buildTarget = GetParam<string>(decision.Parameters, "building_type");
                        ExecutePrioritizeConstruction(settler, buildTarget);
                        break;

                    case "build_stockpile":
                    case "place_stockpile":
                        ExecuteBuildStockpile(settler);
                        break;

                    default:
                        LLMNPCsPlugin.Log.LogWarning($"Unknown action: {decision.Action}");
                        break;
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute decision: {ex}");
            }
        }

        /// <summary>
        /// A settler acts on its own reasoning (Player2) to raise a stockpile
        /// zone in the world near where it stands. This is the autonomous
        /// "villager builds their own stockpile" action.
        /// </summary>
        private static void ExecuteBuildStockpile(Settler settler)
        {
            try
            {
                if (settler == null || settler.gameObject == null)
                    return;
                var result = StockpilePlacer.TryPlaceStockpileNear(settler.gameObject, 2);
                if (result != null && result.StartsWith("ok"))
                    LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Built a stockpile: {result}");
                else
                    LLMNPCsPlugin.Log.LogWarning($"[{settler.name}] Stockpile build did not place: {result}");
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"[{settler?.name}] ExecuteBuildStockpile failed: {ex}");
            }
        }

        private static void ExecuteContinueJob(Settler settler)
        {
            LLMNPCsPlugin.Log.LogDebug($"[{settler.name}] Continuing current job");
        }

        private static void ExecuteSwitchJob(Settler settler, string jobName)
        {
            if (string.IsNullOrEmpty(jobName))
                return;

            var guardId = ResolveGuardId(settler);
            var now = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(guardId)
                && _lastSwitchJobAttemptUtc.TryGetValue(guardId, out var lastTime)
                && _lastSwitchJobName.TryGetValue(guardId, out var lastJob)
                && string.Equals(lastJob, jobName, StringComparison.OrdinalIgnoreCase)
                && (now - lastTime).TotalSeconds < SwitchJobRepeatGuardSeconds)
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] switch_job guard: skipping repeated '{jobName}' within {SwitchJobRepeatGuardSeconds:F0}s window");
                return;
            }

            try
            {
                var jobSystem = NPCContextExtractor.GetFieldValue<object>(settler, "jobSystem");
                if (jobSystem != null)
                {
                    var method = jobSystem.GetType().GetMethod("AssignJob");
                    if (method != null)
                    {
                        method.Invoke(jobSystem, new object[] { jobName });
                        if (!string.IsNullOrWhiteSpace(guardId))
                        {
                            _lastSwitchJobAttemptUtc[guardId] = now;
                            _lastSwitchJobName[guardId] = jobName;
                        }
                        LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Switched job to: {jobName}");
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to switch job: {ex}");
            }
        }

        private static string ResolveGuardId(Settler settler)
        {
            try
            {
                if (settler?.gameObject == null)
                    return null;

                if (GameBridge.TryGetValidatedSettlerIdentity(settler.gameObject, out var id, out _, out _) && !string.IsNullOrWhiteSpace(id))
                    return id;

                return settler.gameObject.GetInstanceID().ToString();
            }
            catch
            {
                return null;
            }
        }

        private static void ExecuteRest(Settler settler)
        {
            try
            {
                if (settler?.gameObject == null) return;
                if (!GameBridge.TryGetValidatedSettlerIdentity(settler.gameObject, out _, out _, out var runtimeWorkerComponent))
                {
                    LLMNPCsPlugin.Log.LogWarning($"[{settler.name}] ExecuteRest: Could not resolve native worker component.");
                    return;
                }

                // Try setting via Stats system first (StatType.Sleep = 2)
                if (NPCContextExtractor.SetStatValue(runtimeWorkerComponent, 2, 5.0f))
                {
                    LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Injected rest need override (value = 5.0f) via Stats system.");
                    return;
                }

                var needs = NPCContextExtractor.GetFieldValue<object>(runtimeWorkerComponent, "needs");
                if (needs != null)
                {
                    var rest = NPCContextExtractor.GetFieldValue<object>(needs, "rest");
                    if (rest != null)
                    {
                        NPCContextExtractor.SetFieldValue(rest, "value", 5.0f);
                        LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Injected rest need override (value = 5.0f) via fallback needs field.");
                    }
                    else
                    {
                        LLMNPCsPlugin.Log.LogWarning($"[{settler.name}] ExecuteRest: 'rest' need object not found.");
                    }
                }
                else
                {
                    LLMNPCsPlugin.Log.LogWarning($"[{settler.name}] ExecuteRest: 'needs' object not found.");
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute rest: {ex}");
            }
        }

        private static void ExecuteEat(Settler settler)
        {
            try
            {
                if (settler?.gameObject == null) return;
                // REAL behaviour, not a cheat: force the settler to actually walk to
                // accessible food and eat it. (The old code just set the hunger stat
                // to full — fake. The LLM decides they're hungry; we make them GO EAT.)
                var goal = GameBridge.TryTriggerEat(settler.gameObject);
                if (goal != null)
                    LLMNPCsPlugin.LogToFile($"[Eat] {settler.name} forced to go eat for real (goal={goal}).");
                else
                    LLMNPCsPlugin.LogToFile($"[Eat] {settler.name} no eat-goal matched; the game's own hunger AI will feed them if food is accessible + in supply.");
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute eat: {ex}");
            }
        }

        private static void ExecuteSocialize(Settler settler, string targetName)
        {
            if (string.IsNullOrEmpty(targetName))
                return;

            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Attempting to socialize with {targetName}");
                var targetGo = GameBridge.FindSettlerById(targetName);
                if (targetGo == null)
                {
                    targetGo = GameBridge.GetAllSettlers().FirstOrDefault(s => GameBridge.GetSettlerName(s) == targetName);
                }

                if (targetGo != null)
                {
                    var targetSettler = GameBridge.EnsureSettlerComponent(targetGo);
                    if (targetSettler != null && LLMNPCsPlugin.Instance?.NPCToNPCDialogueManager != null)
                    {
                        LLMNPCsPlugin.Instance.NPCToNPCDialogueManager.StartConversation(settler, targetSettler);
                        LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Socialize: Started conversation with {targetSettler.Name}.");
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute socialize: {ex}");
            }
        }

        private static void ExecuteFlee(Settler settler)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Fleeing from danger!");
                if (GameBridge.TryGetValidatedSettlerIdentity(settler.gameObject, out _, out _, out var workerComp))
                {
                    GameBridge.ForceGoal(workerComp, "ForcedFleeGoal");
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute flee: {ex}");
            }
        }

        private static void ExecuteDefend(Settler settler)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Preparing to defend");
                GameBridge.TrySetCombatMode(settler.gameObject, true);
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute defend: {ex}");
            }
        }

        private static void ExecuteSeekShelter(Settler settler)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Seeking shelter");
                if (GameBridge.TryGetValidatedSettlerIdentity(settler.gameObject, out _, out _, out var workerComp))
                {
                    GameBridge.ForceGoal(workerComp, "FleeGoal");
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute seek shelter: {ex}");
            }
        }

        private static void ExecuteExplore(Settler settler)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Exploring");
                if (GameBridge.TryGetValidatedSettlerIdentity(settler.gameObject, out _, out _, out var workerComp))
                {
                    GameBridge.ForceGoal(workerComp, "IdleGoal");
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute explore: {ex}");
            }
        }

        private static void ExecuteGather(Settler settler, string resource)
        {
            if (string.IsNullOrEmpty(resource))
                return;

            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Gathering {resource}");
                if (GameBridge.TryGetValidatedSettlerIdentity(settler.gameObject, out _, out _, out var workerComp))
                {
                    GameBridge.ForceGoal(workerComp, "HarvestGoal");
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute gather: {ex}");
            }
        }

        private static void ExecuteBuildSpecial(Settler settler, string buildingName)
        {
            if (string.IsNullOrEmpty(buildingName))
                return;

            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Proposing special building: {buildingName}");
                GameBridge.TryTriggerBuild(settler.gameObject, buildingName);
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute build_special: {ex}");
            }
        }

        private static void ExecuteDraft(Settler settler, string targetCoords)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Drafting to coordinates: {targetCoords ?? "current"}");
                var workerComp = GameBridge.EnsureSettlerComponent(settler.gameObject);
                if (workerComp != null)
                {
                    var draftMethod = workerComp.GetType().GetMethod("Draft", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (draftMethod != null)
                    {
                        draftMethod.Invoke(workerComp, null);
                        LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Successfully invoked native Draft method.");
                    }
                    else
                    {
                        var isDraftedProp = workerComp.GetType().GetProperty("IsDrafted", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (isDraftedProp != null && isDraftedProp.CanWrite)
                        {
                            isDraftedProp.SetValue(workerComp, true, null);
                            LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Successfully set IsDrafted property.");
                        }
                    }

                    if (!string.IsNullOrEmpty(targetCoords))
                    {
                        var moveToMethod = workerComp.GetType().GetMethod("MoveTo", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (moveToMethod != null)
                        {
                            var coordsParts = targetCoords.Trim('[', ']').Split(',');
                            if (coordsParts.Length >= 2 && float.TryParse(coordsParts[0], out float x) && float.TryParse(coordsParts[1], out float z))
                            {
                                float y = coordsParts.Length >= 3 && float.TryParse(coordsParts[2], out float parsedY) ? parsedY : 0f;
                                moveToMethod.Invoke(workerComp, new object[] { new UnityEngine.Vector3(x, y, z) });
                                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Successfully invoked native MoveTo method.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute draft: {ex}");
            }
        }

        private static void ExecuteRepair(Settler settler)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Prioritizing repair task");
                GameBridge.TryTriggerRepair(settler.gameObject);
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute repair: {ex}");
            }
        }

        private static void ExecuteHaul(Settler settler)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Prioritizing haul task");
                GameBridge.TryTriggerHaul(settler.gameObject);
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute haul: {ex}");
            }
        }

        private static void ExecuteCapture(Settler settler, string target)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Attempting to capture target: {target ?? "unknown"}");
                var workerComp = GameBridge.EnsureSettlerComponent(settler.gameObject);
                if (workerComp != null)
                {
                    var captureMethod = workerComp.GetType().GetMethod("Capture", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (captureMethod != null)
                    {
                        captureMethod.Invoke(workerComp, new object[] { null });
                        LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Successfully invoked native Capture method.");
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute capture: {ex}");
            }
        }

        private static void ExecuteRebrand(Settler settler)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Initiating asset rebranding (upgrading walls)");
                var mapV2Type = Type.GetType("NSMedieval.MapV2, Assembly-CSharp");
                if (mapV2Type != null)
                {
                    var instances = UnityEngine.Object.FindObjectsOfType(mapV2Type);
                    if (instances != null && instances.Length > 0)
                    {
                        var rebrandMethod = mapV2Type.GetMethod("UpgradeMaterials", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (rebrandMethod != null)
                        {
                            rebrandMethod.Invoke(instances[0], new object[] { "Wood", "Stone" });
                            LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Successfully called UpgradeMaterials on MapV2.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Failed to execute rebrand: {ex}");
            }
        }

        // ── Phase 1 New Executors ───────────────────────────────────────────────────

        private static void ExecuteDrink(Settler settler)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Executing drink action");
                if (!GameBridge.TryGetValidatedSettlerIdentity(settler.gameObject, out _, out _, out var workerComp)) { LLMNPCsPlugin.Log.LogWarning($"[{settler.name}] drink: could not get worker component"); return; }
                bool success = NPCContextExtractor.SetStatValue(workerComp, 12, 100f); // Stat 12 = Alcohol (drink need)
                if (success) LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Drink: drink stat set to 100.");
                
                GameBridge.ForceGoal(workerComp, "DrinkGoal");
            }
            catch (Exception ex) { LLMNPCsPlugin.Log.LogError($"Failed to execute drink: {ex}"); }
        }

        private static void ExecuteComplain(Settler settler, string targetSettlerId, string complaint)
        {
            try
            {
                var sname = settler?.name ?? "Unknown";
                LLMNPCsPlugin.Log.LogInfo($"[{sname}] Executing complain — target: {targetSettlerId ?? "anyone"}");
                if (!string.IsNullOrEmpty(targetSettlerId)) 
                { 
                    var tgo = GameBridge.FindSettlerById(targetSettlerId); 
                    if (tgo != null) 
                    { 
                        var ts = GameBridge.EnsureSettlerComponent(tgo); 
                        if (ts != null && LLMNPCsPlugin.Instance?.NPCToNPCDialogueManager != null) 
                        { 
                            LLMNPCsPlugin.Instance.NPCToNPCDialogueManager.StartConversation(settler, ts); 
                            LLMNPCsPlugin.Log.LogInfo($"[{sname}] Complain: started conversation with target {targetSettlerId}."); 
                            return; 
                        } 
                    } 
                }
                LLMNPCsPlugin.Log.LogInfo($"[{sname}] Complaint recorded (no nearby target).");
            }
            catch (Exception ex) { LLMNPCsPlugin.Log.LogError($"Failed to execute complain: {ex}"); }
        }

        private static void ExecuteChangeClothing(Settler settler, string clothingType)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Executing change_clothing — type: {clothingType ?? "any"}");
                GameBridge.TryChangeClothing(settler.gameObject, clothingType);
            }
            catch (Exception ex) { LLMNPCsPlugin.Log.LogError($"Failed to execute change_clothing: {ex}"); }
        }

        private static void ExecuteSeekMedic(Settler settler)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Executing seek_medic");
                GameBridge.TrySeekMedic(settler.gameObject);
            }
            catch (Exception ex) { LLMNPCsPlugin.Log.LogError($"Failed to execute seek_medic: {ex}"); }
        }

        private static void ExecutePrioritizeConstruction(Settler settler, string buildingType)
        {
            try
            {
                LLMNPCsPlugin.Log.LogInfo($"[{settler.name}] Executing prioritize_construction — type: {buildingType ?? "highest-priority"}");
                GameBridge.TryAssignConstructionPriority(settler.gameObject, 1);
                GameBridge.TryTriggerBuild(settler.gameObject, buildingType);
            }
            catch (Exception ex) { LLMNPCsPlugin.Log.LogError($"Failed to execute prioritize_construction: {ex}"); }
        }

        private static T GetParam<T>(Dictionary<string, object> parameters, string key)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var value))
                return default;

            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return default;
            }
        }
    }
}
