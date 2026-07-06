using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Manages autonomous conversations between NPCs.
    /// Uses LLM to generate dialogue based on both NPCs' contexts and relationship.
    /// </summary>
    public class NPCToNPCDialogueManager
    {
        private readonly LLMClient _llmClient;
        private readonly RelationshipSystem _relationshipSystem;
        private readonly MemoryManager _memoryManager;
        private readonly ChatBubbleManager _chatBubbleManager;
        private readonly NPCRegistry _npcRegistry;

        // Active conversations
        private readonly Dictionary<string, NPCConversation> _activeConversations;
        private readonly Dictionary<string, DateTime> _lastConversationTime;

        // Configuration
        private readonly float _conversationCooldown = 120f; // 2 minutes between conversations
        private readonly float _maxConversationDistance = 5f;
        private readonly int _maxExchanges = 4;

        public NPCToNPCDialogueManager(
            LLMClient llmClient,
            RelationshipSystem relationshipSystem,
            MemoryManager memoryManager,
            ChatBubbleManager chatBubbleManager,
            NPCRegistry npcRegistry)
        {
            _llmClient = llmClient;
            _relationshipSystem = relationshipSystem;
            _memoryManager = memoryManager;
            _chatBubbleManager = chatBubbleManager;
            _npcRegistry = npcRegistry;
            _activeConversations = new Dictionary<string, NPCConversation>();
            _lastConversationTime = new Dictionary<string, DateTime>();

            LLMNPCsPlugin.LogToFile("[NPCToNPCDialogueManager:Constructor] Initialized");
        }

        /// <summary>
        /// Called periodically to check for and initiate NPC conversations.
        /// </summary>
        public void Update()
        {
            // Check every 5 seconds (don't spam)
            if (Time.frameCount % 300 != 0) return;

            TryInitiateConversations();
            UpdateActiveConversations();
        }

        /// <summary>
        /// Looks for NPC pairs that should start a conversation.
        /// </summary>
        private void TryInitiateConversations()
        {
            var settlers = GetAllSettlers();
            var now = DateTime.UtcNow;

            foreach (var settlerA in settlers)
            {
                var idA = GetSettlerId(settlerA);
                
                // Check cooldown
                if (_lastConversationTime.TryGetValue(idA, out var lastTime))
                {
                    if ((now - lastTime).TotalSeconds < _conversationCooldown) continue;
                }

                // Check if already in conversation
                if (IsInConversation(idA)) continue;

                // Find nearby NPCs
                var posA = GetSettlerPosition(settlerA);
                var nearbyNPCs = settlers
                    .Where(s => s != settlerA)
                    .Select(s => new { Settler = s, Id = GetSettlerId(s) })
                    .Where(x => !IsInConversation(x.Id))
                    .Where(x => Vector3.Distance(posA, GetSettlerPosition(x.Settler)) < _maxConversationDistance)
                    .ToList();

                if (nearbyNPCs.Count == 0) continue;

                // Pick best conversation partner based on relationship
                var bestPartner = PickConversationPartner(idA, nearbyNPCs.Select(x => x.Settler).ToList());
                if (bestPartner == null) continue;

                var idB = GetSettlerId(bestPartner);

                // Check if both are available for conversation
                if (ShouldStartConversation(settlerA, bestPartner))
                {
                    StartConversation(settlerA, bestPartner);
                }
            }
        }

        private Settler PickConversationPartner(string npcAId, List<Settler> candidates)
        {
            var random = new System.Random();
            
            // Score each candidate
            var scored = candidates.Select(c =>
            {
                var id = GetSettlerId(c);
                var rel = _relationshipSystem.GetRelationship(npcAId, id);
                
                // Higher score for friends and lovers, lower for enemies
                float score = rel.Friendship * 100 + rel.Romance * 150 - rel.Rivalry * 50;
                
                // Random factor
                score += random.Next(0, 50);
                
                return new { Settler = c, Score = score };
            })
            .OrderByDescending(x => x.Score)
            .ToList();

            // Pick from top 3 weighted by score
            if (scored.Count == 0) return null;
            
            var top3 = scored.Take(Math.Min(3, scored.Count)).ToList();
            return top3[random.Next(top3.Count)].Settler;
        }

        private bool ShouldStartConversation(Settler a, Settler b)
        {
            // Check if they're not busy with critical tasks
            var contextA = NPCContextExtractor.Extract(a);
            var contextB = NPCContextExtractor.Extract(b);

            // Don't talk if critically low on needs
            if (contextA?.Needs != null && (contextA.Needs.Food < 20 || contextA.Needs.Rest < 15))
                return false;
            if (contextB?.Needs != null && (contextB.Needs.Food < 20 || contextB.Needs.Rest < 15))
                return false;

            // Check if it's a good time (not in combat, etc.)
            if (contextA?.Environment?.NearbyThreats?.Count > 0) return false;
            if (contextB?.Environment?.NearbyThreats?.Count > 0) return false;

            // Higher chance during breaks/recreation
            var rel = _relationshipSystem.GetRelationship(contextA.Id, contextB.Id);
            var baseChance = 0.1f;
            baseChance += rel.Friendship * 0.3f; // Friends talk more
            baseChance += rel.Romance * 0.4f;    // Lovers talk even more

            return UnityEngine.Random.value < baseChance;
        }

        public void StartConversation(Settler a, Settler b)
        {
            var contextA = NPCContextExtractor.Extract(a);
            var contextB = NPCContextExtractor.Extract(b);
            
            if (contextA == null || contextB == null) return;

            var conversationId = Guid.NewGuid().ToString();
            var conversation = new NPCConversation
            {
                Id = conversationId,
                NPC_A_Id = contextA.Id,
                NPC_B_Id = contextB.Id,
                NPC_A_Name = contextA.Name,
                NPC_B_Name = contextB.Name,
                SettlerA = a,
                SettlerB = b,
                StartedAt = DateTime.UtcNow,
                Exchanges = new List<DialogueExchange>(),
                CurrentSpeaker = contextA.Id, // Randomize?
                Relationship = _relationshipSystem.GetRelationship(contextA.Id, contextB.Id)
            };

            _activeConversations[conversationId] = conversation;
            _lastConversationTime[contextA.Id] = DateTime.UtcNow;
            _lastConversationTime[contextB.Id] = DateTime.UtcNow;

            LLMNPCsPlugin.LogToFile($"[NPCToNPCDialogueManager] Started conversation: {contextA.Name} ↔ {contextB.Name}");

            // Generate first line
            _ = ContinueConversationAsync(conversation);
        }

        private async Task ContinueConversationAsync(NPCConversation conversation)
        {
            try
            {
                var speakerId = conversation.CurrentSpeaker;
                var listenerId = speakerId == conversation.NPC_A_Id ? conversation.NPC_B_Id : conversation.NPC_A_Id;
                var speakerName = speakerId == conversation.NPC_A_Id ? conversation.NPC_A_Name : conversation.NPC_B_Name;
                var listenerName = speakerId == conversation.NPC_A_Id ? conversation.NPC_B_Name : conversation.NPC_A_Name;
                var speakerSettler = speakerId == conversation.NPC_A_Id ? conversation.SettlerA : conversation.SettlerB;

                // Get contexts
                var speakerContext = NPCContextExtractor.Extract(speakerSettler);
                var listenerContext = speakerId == conversation.NPC_A_Id ? 
                    NPCContextExtractor.Extract(conversation.SettlerB) : 
                    NPCContextExtractor.Extract(conversation.SettlerA);

                // Inject memory context
                if (speakerContext != null)
                {
                    var speakerMemTask = _memoryManager?.GetContextForPromptAsync(speakerContext.Id, speakerContext.Profession, $"Speaking to {listenerName}", maxTokens: 800);
                    speakerContext.MemoryContext = speakerMemTask != null ? await speakerMemTask : "";
                }
                if (listenerContext != null)
                {
                    var listenerMemTask = _memoryManager?.GetContextForPromptAsync(listenerContext.Id, listenerContext.Profession, $"Listening to {speakerName}", maxTokens: 800);
                    listenerContext.MemoryContext = listenerMemTask != null ? await listenerMemTask : "";
                }

                // Generate dialogue
                var dialogue = await GenerateDialogueAsync(speakerContext, listenerContext, conversation);

                if (string.IsNullOrEmpty(dialogue))
                {
                    EndConversation(conversation);
                    return;
                }

                // Add to conversation
                conversation.Exchanges.Add(new DialogueExchange
                {
                    SpeakerId = speakerId,
                    SpeakerName = speakerName,
                    Text = dialogue,
                    Timestamp = DateTime.UtcNow
                });

                // Show chat bubble
                var worldPos = GetSettlerPosition(speakerSettler);
                _chatBubbleManager.ShowBubble(
                    speakerId, 
                    speakerName, 
                    dialogue, 
                    worldPos,
                    conversation.Relationship.IsMarried,
                    GetRelationshipIcon(conversation.Relationship, speakerId)
                );

                // Record in memory
                _memoryManager?.RecordEvent(speakerId, "conversation", $"Said to {listenerName}: {dialogue}",
                    importance: 5, context: new Dictionary<string, object> { 
                        { "listener", listenerId },
                        { "relationship_type", conversation.Relationship.RelationshipType }
                    });

                // Switch speaker
                conversation.CurrentSpeaker = listenerId;

                // Check if conversation should end
                if (conversation.Exchanges.Count >= _maxExchanges * 2 || // Each exchange = 2 lines
                    UnityEngine.Random.value < 0.15f) // 15% chance to end naturally
                {
                    EndConversation(conversation);
                    
                    // Update relationship based on conversation quality
                    UpdateRelationshipFromConversation(conversation);
                }
                else
                {
                    // Schedule next line (use System.Random, not UnityEngine.Random - thread safety)
                    var rng = new System.Random();
                    await Task.Delay(rng.Next(2000, 4000));
                    
                    // Check if conversation is still active
                    if (_activeConversations.ContainsKey(conversation.Id))
                    {
                        _ = ContinueConversationAsync(conversation);
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"[NPCToNPCDialogueManager] Error in conversation: {ex}");
                EndConversation(conversation);
            }
        }

        private async Task<string> GenerateDialogueAsync(NPCContext speaker, NPCContext listener, NPCConversation conversation)
        {
            var prompt = BuildNPCDialoguePrompt(speaker, listener, conversation);
            
            try
            {
                var response = await _llmClient.GetRawResponseAsync(
                    prompt,
                    new LLMTraceMetadata
                    {
                        FlowType = PromptFlowTypes.NpcToNpc,
                        SenderName = speaker?.Name,
                        TargetName = listener?.Name
                    });
                if (string.IsNullOrEmpty(response)) return null;

                var normalized = NormalizeDialogueResponse(response);
                if (string.IsNullOrWhiteSpace(normalized))
                    return null;

                // Parse JSON response when possible, otherwise treat as plain dialogue text.
                var parsed = TryParseDialogueJson(normalized);
                if (parsed != null)
                    return parsed;

                LLMNPCsPlugin.LogToFile($"[NPCToNPCDialogueManager] Non-JSON dialogue payload fallback used. Raw: {TruncateForLog(normalized, 240)}");
                return normalized;
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"[NPCToNPCDialogueManager] Failed to generate dialogue: {ex}");
                return null;
            }
        }

        private static string NormalizeDialogueResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return null;

            var text = response.Trim();

            if (text.Contains("```"))
            {
                var start = text.IndexOf('{');
                var end = text.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    return text.Substring(start, end - start + 1).Trim();
                }

                // Remove fenced markers if there is no JSON object.
                text = text.Replace("```json", string.Empty)
                           .Replace("```", string.Empty)
                           .Trim();
            }

            return text;
        }

        private static string TryParseDialogueJson(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            if (!normalized.StartsWith("{") && !normalized.StartsWith("["))
                return null;

            try
            {
                var json = JObject.Parse(normalized);
                return json["dialogue"]?.ToString()
                    ?? json["text"]?.ToString()
                    ?? json["message"]?.ToString();
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[NPCToNPCDialogueManager] Dialogue JSON parse failed, falling back to plain text. Error={ex.Message}; payload={TruncateForLog(normalized, 240)}");
                return null;
            }
        }

        private static string TruncateForLog(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
                return value;
            return value.Substring(0, Math.Max(1, max)) + "...";
        }

        private List<Message> BuildNPCDialoguePrompt(NPCContext speaker, NPCContext listener, NPCConversation conversation)
        {
            var messages = new List<Message>();

            // System prompt
            messages.Add(new Message
            {
                Role = "system",
                Content = @"You are generating dialogue for a settler in a medieval colony game. 
Generate a single line of dialogue (1-2 sentences max) that the speaking settler would say to the listening settler.
Consider their relationship, current situation, and personalities.

Respond with JSON: {""dialogue"": ""what they say""}"
            });

            // Build context
            var context = $@"SPEAKER: {speaker.Name} ({speaker.Profession})
{PromptBuilder.BuildRichContextSummary(speaker, includeMemory: true)}

LISTENER: {listener.Name} ({listener.Profession})
{PromptBuilder.BuildRichContextSummary(listener, includeMemory: true)}

RELATIONSHIP: {conversation.Relationship.GetDisplayName(speaker.Id, listener.Name)}
- Friendship: {conversation.Relationship.Friendship:F2}
- Romance: {conversation.Relationship.Romance:F2}
- Trust: {conversation.Relationship.Trust:F2}

CONVERSATION HISTORY:
{string.Join("\n", conversation.Exchanges.Select(e => $"{e.SpeakerName}: {e.Text}"))}

Generate what {speaker.Name} says next:";

            messages.Add(new Message
            {
                Role = "user",
                Content = context
            });

            return messages;
        }

        private void UpdateRelationshipFromConversation(NPCConversation conversation)
        {
            var rel = conversation.Relationship;
            
            // Analyze conversation tone (simplified - could use LLM for sentiment)
            var positiveWords = new[] { "good", "great", "happy", "love", "friend", "thanks", "wonderful" };
            var negativeWords = new[] { "bad", "hate", "angry", "stupid", "annoying", "go away" };
            
            int positiveCount = 0;
            int negativeCount = 0;

            foreach (var exchange in conversation.Exchanges)
            {
                var lower = exchange.Text.ToLower();
                positiveCount += positiveWords.Count(w => lower.Contains(w));
                negativeCount += negativeWords.Count(w => lower.Contains(w));
            }

            InteractionOutcome outcome;
            if (rel.Romance > RelationshipSystem.ROMANCE_THRESHOLD && positiveCount > 2)
                outcome = InteractionOutcome.Romantic;
            else if (positiveCount > negativeCount)
                outcome = InteractionOutcome.Positive;
            else if (negativeCount > positiveCount)
                outcome = InteractionOutcome.Negative;
            else
                outcome = InteractionOutcome.Neutral;

            _relationshipSystem.ModifyRelationship(
                conversation.NPC_A_Id, 
                conversation.NPC_B_Id, 
                outcome,
                $"Conversation with {conversation.Exchanges.Count} exchanges"
            );
        }

        private void EndConversation(NPCConversation conversation)
        {
            _activeConversations.Remove(conversation.Id);
            LLMNPCsPlugin.LogToFile($"[NPCToNPCDialogueManager] Ended conversation: {conversation.NPC_A_Name} ↔ {conversation.NPC_B_Name} ({conversation.Exchanges.Count} exchanges)");
        }

        private void UpdateActiveConversations()
        {
            var now = DateTime.UtcNow;
            var toEnd = _activeConversations.Values
                .Where(c => (now - c.StartedAt).TotalMinutes > 2) // Max 2 minutes
                .Select(c => c.Id)
                .ToList();

            foreach (var id in toEnd)
            {
                if (_activeConversations.TryGetValue(id, out var conv))
                {
                    EndConversation(conv);
                }
            }
        }

        private bool IsInConversation(string npcId)
        {
            return _activeConversations.Values.Any(c => 
                c.NPC_A_Id == npcId || c.NPC_B_Id == npcId);
        }

        private string GetRelationshipIcon(Relationship rel, string myId)
        {
            if (rel.IsMarried) return "💍";
            if (rel.Romance >= RelationshipSystem.ROMANCE_THRESHOLD) return "💕";
            if (rel.Friendship >= RelationshipSystem.FRIENDSHIP_THRESHOLD) return "🤝";
            if (rel.Rivalry >= RelationshipSystem.RIVALRY_THRESHOLD) return "😠";
            return null;
        }

        private List<Settler> GetAllSettlers()
        {
            return GameBridge.GetValidatedSettlers()
                .Where(s => s != null)
                .ToList();
        }

        private string GetSettlerId(Settler settler)
        {
            var gameId = NPCContextExtractor.GetFieldValue<string>(settler, "id");
            return gameId ?? settler.GetInstanceID().ToString();
        }

        private Vector3 GetSettlerPosition(Settler settler)
        {
            var transform = settler.GetComponent<Transform>();
            return transform != null ? transform.position : Vector3.zero;
        }

        /// <summary>
        /// Gets all active conversations for monitoring.
        /// </summary>
        public List<NPCConversation> GetActiveConversations()
        {
            return _activeConversations.Values.ToList();
        }
    }

    public class NPCConversation
    {
        public string Id { get; set; }
        public string NPC_A_Id { get; set; }
        public string NPC_B_Id { get; set; }
        public string NPC_A_Name { get; set; }
        public string NPC_B_Name { get; set; }
        public Settler SettlerA { get; set; }
        public Settler SettlerB { get; set; }
        public DateTime StartedAt { get; set; }
        public List<DialogueExchange> Exchanges { get; set; }
        public string CurrentSpeaker { get; set; }
        public Relationship Relationship { get; set; }
    }

    public class DialogueExchange
    {
        public string SpeakerId { get; set; }
        public string SpeakerName { get; set; }
        public string Text { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
