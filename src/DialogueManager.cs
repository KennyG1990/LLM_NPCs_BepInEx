using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Manages player-NPC dialogue conversations.
    /// Hooks into the game to provide an interactive chat interface with settlers.
    /// </summary>
    public class DialogueManager
    {
        private readonly LLMClient _llmClient;
        private readonly MemoryManager _memoryManager;
        private readonly NPCRegistry _npcRegistry;
        private readonly ConfigEntry<KeyboardShortcut> _dialogueHotkey;

        // Active conversation state (floating window)
        private NPCContext _activeNPC;
        private List<DialogueMessage> _currentConversation;
        private bool _isInDialogue;

        // UI State
        private bool _showDialogueWindow;
        private Rect _windowRect = new Rect(100, 100, 500, 400);
        private string _playerInput = "";
        private Vector2 _scrollPosition;
        private bool _isWaitingForResponse;

        public bool IsInDialogue => _isInDialogue;

        public DialogueManager(LLMClient llmClient, MemoryManager memoryManager, NPCRegistry npcRegistry, ConfigEntry<KeyboardShortcut> dialogueHotkey = null)
        {
            _llmClient = llmClient;
            _memoryManager = memoryManager;
            _npcRegistry = npcRegistry;
            _dialogueHotkey = dialogueHotkey;
            _currentConversation = new List<DialogueMessage>();
            var keyName = dialogueHotkey?.Value.MainKey.ToString() ?? "BackQuote";
            LLMNPCsPlugin.LogToFile($"[DialogueManager:Constructor] Initialized - Hotkey: {keyName}");
        }

        /// <summary>
        /// Call this from Plugin.Update() to handle input and rendering
        /// </summary>
        /// <summary>Toolbar entry point: open chat with the currently selected
        /// settler (same path as the hotkey).</summary>
        public void OpenDialogueWithSelected()
        {
            if (!_isInDialogue) TryStartDialogue();
        }

        public void Update()
        {
            // Check for dialogue initiation hotkey (configurable via BepInEx config)
            if (!_isInDialogue && _dialogueHotkey != null && _dialogueHotkey.Value.IsDown())
            {
                LLMNPCsPlugin.LogToFile($"[DialogueManager:Update] {_dialogueHotkey.Value.MainKey} pressed, trying to start dialogue");
                TryStartDialogue();
            }

            // Check for escape to close dialogue
            if (Input.GetKeyDown(KeyCode.Escape) && _isInDialogue)
            {
                LLMNPCsPlugin.LogToFile("[DialogueManager:Update] Escape pressed, ending dialogue");
                EndDialogue();
            }
        }

        /// <summary>
        /// Call this from Plugin.OnGUI() to render the dialogue window
        /// </summary>
        public void OnGUI()
        {
            if (!_showDialogueWindow) return;

            // Dark background overlay
            GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.95f);
            
            _windowRect = GUI.Window(
                999, // Unique window ID
                _windowRect,
                DrawDialogueWindow,
                $"Talking to {_activeNPC?.Name ?? "Unknown"}"
            );
        }

        private void DrawDialogueWindow(int windowID)
        {
            // Make window draggable
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 20));

            // Close button
            if (GUI.Button(new Rect(_windowRect.width - 25, 2, 23, 18), "X"))
            {
                EndDialogue();
                return;
            }

            // NPC Info header
            if (_activeNPC != null)
            {
                GUI.Label(
                    new Rect(10, 25, _windowRect.width - 20, 20),
                    $"{_activeNPC.Profession} | Mood: {_activeNPC.Mood} | Health: {_activeNPC.Health?.Current:F0}%",
                    new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic, fontSize = 11 }
                );
            }

            // Conversation history area
            float historyY = 50;
            float historyHeight = _windowRect.height - 110;
            
            _scrollPosition = GUI.BeginScrollView(
                new Rect(10, historyY, _windowRect.width - 20, historyHeight),
                _scrollPosition,
                new Rect(0, 0, _windowRect.width - 40, Mathf.Max(historyHeight, _currentConversation.Count * 60))
            );

            float messageY = 0;
            foreach (var msg in _currentConversation)
            {
                DrawMessage(msg, ref messageY, _windowRect.width - 50);
            }

            GUI.EndScrollView();

            // Auto-scroll to bottom
            if (_currentConversation.Count > 0 && Event.current.type == EventType.Repaint)
            {
                float contentHeight = _currentConversation.Count * 60;
                if (contentHeight > historyHeight)
                {
                    _scrollPosition.y = contentHeight - historyHeight + 20;
                }
            }

            // Input area at bottom
            float inputY = _windowRect.height - 50;
            
            // Text input field
            GUI.SetNextControlName("PlayerInput");
            _playerInput = GUI.TextField(
                new Rect(10, inputY, _windowRect.width - 90, 30),
                _playerInput,
                200
            );

            // Send button (or press Enter)
            bool sendClicked = GUI.Button(
                new Rect(_windowRect.width - 75, inputY, 65, 30),
                _isWaitingForResponse ? "..." : "Send"
            );

            if ((sendClicked || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return))
                && !string.IsNullOrWhiteSpace(_playerInput)
                && !_isWaitingForResponse)
            {
                SendPlayerMessage(_playerInput.Trim());
                _playerInput = "";
                GUI.FocusControl("PlayerInput");
            }

            // Hint text
            GUI.Label(
                new Rect(10, _windowRect.height - 20, _windowRect.width - 20, 20),
                "Press ESC to close, ENTER to send",
                new GUIStyle(GUI.skin.label) { fontSize = 10, alignment = TextAnchor.MiddleCenter }
            );
        }

        private void DrawMessage(DialogueMessage msg, ref float y, float width)
        {
            float height = 50;
            
            if (msg.IsPlayer)
            {
                // Player message (right-aligned, blue tint)
                GUI.backgroundColor = new Color(0.2f, 0.4f, 0.8f, 0.8f);
                GUI.Box(new Rect(width - 250, y, 240, height), "");
                GUI.Label(
                    new Rect(width - 245, y + 5, 230, height - 10),
                    $"You:\n{msg.Text}",
                    new GUIStyle(GUI.skin.label) { wordWrap = true, normal = { textColor = Color.white } }
                );
            }
            else
            {
                // NPC message (left-aligned, gray tint)
                GUI.backgroundColor = new Color(0.3f, 0.3f, 0.3f, 0.8f);
                GUI.Box(new Rect(10, y, 240, height), "");
                GUI.Label(
                    new Rect(15, y + 5, 230, height - 10),
                    $"{_activeNPC?.Name ?? "NPC"}:\n{msg.Text}",
                    new GUIStyle(GUI.skin.label) { wordWrap = true, normal = { textColor = Color.white } }
                );
            }

            y += height + 10;
        }

        private void TryStartDialogue()
        {
            // Try to get currently selected settler or find nearest
            var settler = GetTargetSettler();
            if (settler == null)
            {
                LLMNPCsPlugin.Log.LogInfo("[Dialogue] No settler found to talk to. Click on a settler first or stand near one.");
                return;
            }

            var context = NPCContextExtractor.Extract(settler);
            if (context == null)
            {
                LLMNPCsPlugin.Log.LogWarning("[Dialogue] Failed to extract context from settler");
                return;
            }

            StartDialogue(context);
        }

        private void StartDialogue(NPCContext npcContext)
        {
            _activeNPC = npcContext;
            _isInDialogue = true;
            _showDialogueWindow = true;
            _currentConversation.Clear();
            _isWaitingForResponse = false;
            _playerInput = "";

            // Load conversation history from memory
            LoadConversationHistory();

            // Add initial greeting from NPC
            if (_currentConversation.Count == 0)
            {
                var greeting = GenerateGreeting();
                AddNPCMessage(greeting);
            }

            LLMNPCsPlugin.Log.LogInfo($"[Dialogue] Started conversation with {npcContext.Name}");
        }

        /// <summary>
        /// Public method to start dialogue with a specific settler (opens floating window).
        /// Called from hotkey flow. For SocialHub inline chat, use SendInlineMessage.
        /// </summary>
        public void StartDialogueWithSettler(Settler settler)
        {
            if (settler == null) return;

            var context = NPCContextExtractor.Extract(settler);
            if (context == null)
            {
                LLMNPCsPlugin.Log.LogWarning("[Dialogue] Failed to extract context from settler");
                return;
            }

            StartDialogue(context);
        }

        /// <summary>
        /// Sends a player message in inline/embedded mode (used by SocialHubWindow).
        /// Does NOT open the floating window.
        /// </summary>
        public void SendInlineMessage(
            Settler settler,
            string playerMessage,
            Action<string> onResponse,
            Action<string> onError = null)
        {
            if (settler == null || string.IsNullOrWhiteSpace(playerMessage))
            {
                onError?.Invoke("Invalid settler or message");
                return;
            }

            var context = NPCContextExtractor.Extract(settler);
            if (context == null)
            {
                onError?.Invoke("Could not read NPC context");
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    var memoryContextTask = _memoryManager?.GetContextForPromptAsync(context.Id, context.Profession, playerMessage);
                    var memoryContext = memoryContextTask != null ? await memoryContextTask : "";
                    var dialogueStateTask = _memoryManager?.GetDialogueStateForPromptAsync(context.Id);
                    var dialogueState = dialogueStateTask != null ? await dialogueStateTask : "";
                    
                    var prompt = $@"You are {context.Name}, a {context.Profession} in a medieval colony.

Your current context:
{PromptBuilder.BuildRichContextSummary(context, includeMemory: false)}

Your dialogue state:
{dialogueState}

Your memories:
{memoryContext}

The player says: ""{playerMessage}""

Respond in character as {context.Name}. Keep your spoken dialogue to 1-2 sentences. Be consistent with your mood ({context.Mood}) and current situation.

Return JSON when possible:
{{""dialogue"":""spoken response"",""claims"":[""persistent factual claim, promise, warning, or preference""],""trust_delta"":0.0,""contradiction"":null,""barter_intent"":null}}

Use barter_intent only for a proposed trade/request, e.g. {{""intent_type"":""request_food"",""item"":""meal"",""terms"":""asks player to provide food""}}.
Use contradiction only if you believe the player contradicted an earlier claim.";

                    var response = await _llmClient.SendSimplePromptAsync(
                        prompt,
                        new LLMTraceMetadata
                        {
                            FlowType   = PromptFlowTypes.PlayerToNpc,
                            SenderName = "Player",
                            TargetName = context.Name
                        },
                        task: "player_chat");   // real-time, player-facing — best model, no interval

                    var parsed = ParseDialogueResponse(response);
                    var final = parsed.Text;

                    _memoryManager?.RecordDialogueExchange(
                        context.Id,
                        playerMessage,
                        final,
                        parsed.Claims,
                        parsed.TrustDelta,
                        parsed.Contradiction,
                        parsed.BarterIntent);

                    onResponse?.Invoke(final);
                }
                catch (Exception ex)
                {
                    LLMNPCsPlugin.Log.LogError($"[Dialogue:Inline] Error: {ex.Message}");
                    onError?.Invoke(ex.Message);
                }
            });
        }

        private void EndDialogue()
        {
            if (_isInDialogue && _activeNPC != null)
            {
                // Save conversation to memory
                SaveConversationToMemory();
                
                LLMNPCsPlugin.Log.LogInfo($"[Dialogue] Ended conversation with {_activeNPC.Name}");
            }

            _isInDialogue = false;
            _showDialogueWindow = false;
            _activeNPC = null;
            _currentConversation.Clear();
            _isWaitingForResponse = false;
        }

        private void SendPlayerMessage(string message)
        {
            // Add to UI
            _currentConversation.Add(new DialogueMessage { Text = message, IsPlayer = true });
            _isWaitingForResponse = true;

            // Get NPC response asynchronously
            Task.Run(async () =>
            {
                try
                {
                    var response = await GetNPCResponseAsync(message);
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        AddNPCMessage(response.Text);
                        _memoryManager?.RecordDialogueExchange(
                            _activeNPC.Id,
                            message,
                            response.Text,
                            response.Claims,
                            response.TrustDelta,
                            response.Contradiction,
                            response.BarterIntent);
                        _isWaitingForResponse = false;
                    });
                }
                catch (Exception ex)
                {
                    LLMNPCsPlugin.Log.LogError($"[Dialogue] Error getting response: {ex}");
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        AddNPCMessage("*seems distracted and doesn't respond*");
                        _isWaitingForResponse = false;
                    });
                }
            });
        }

        private void AddNPCMessage(string message)
        {
            _currentConversation.Add(new DialogueMessage { Text = message, IsPlayer = false });
        }

        private async Task<DialogueResponseResult> GetNPCResponseAsync(string playerMessage)
        {
            // Build conversation context
            var memoryTask = _memoryManager?.GetContextForPromptAsync(_activeNPC.Id, _activeNPC.Profession, playerMessage);
            var memoryContext = memoryTask != null ? await memoryTask : "";
            var dialogueStateTask = _memoryManager?.GetDialogueStateForPromptAsync(_activeNPC.Id);
            var dialogueState = dialogueStateTask != null ? await dialogueStateTask : "";
            
            // Build prompt
            var prompt = $@"You are {_activeNPC.Name}, a {_activeNPC.Profession} in a medieval colony.

Your current live context:
{PromptBuilder.BuildRichContextSummary(_activeNPC, includeMemory: false)}

Your dialogue state:
{dialogueState}

Your memories:
{memoryContext}

The player says to you: ""{playerMessage}""

Respond in character as {_activeNPC.Name}. Keep your spoken dialogue to 1-2 sentences. Be consistent with your mood and current situation.

Return JSON when possible:
{{""dialogue"":""spoken response"",""claims"":[""persistent factual claim, promise, warning, or preference""],""trust_delta"":0.0,""contradiction"":null,""barter_intent"":null}}

Use barter_intent only for a proposed trade/request, e.g. {{""intent_type"":""request_food"",""item"":""meal"",""terms"":""asks player to provide food""}}.
Use contradiction only if you believe the player contradicted an earlier claim.";

            // Send to LLM
            var response = await _llmClient.SendSimplePromptAsync(
                prompt,
                new LLMTraceMetadata
                {
                    FlowType = PromptFlowTypes.PlayerToNpc,
                    SenderName = "Player",
                    TargetName = _activeNPC?.Name
                });
            return ParseDialogueResponse(response);
        }

        private static DialogueResponseResult ParseDialogueResponse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new DialogueResponseResult { Text = "*nods silently*" };
            }

            var normalized = raw.Trim();
            var fenceStart = normalized.IndexOf("```", StringComparison.Ordinal);
            if (fenceStart >= 0)
            {
                var afterFence = normalized.IndexOf('\n', fenceStart);
                var fenceEnd = normalized.LastIndexOf("```", StringComparison.Ordinal);
                if (afterFence >= 0 && fenceEnd > afterFence)
                {
                    normalized = normalized.Substring(afterFence + 1, fenceEnd - afterFence - 1).Trim();
                }
            }

            var jsonStart = normalized.IndexOf('{');
            var jsonEnd = normalized.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
            {
                return new DialogueResponseResult { Text = normalized };
            }

            try
            {
                var obj = JObject.Parse(normalized.Substring(jsonStart, jsonEnd - jsonStart + 1));
                var text = obj.Value<string>("dialogue")
                           ?? obj.Value<string>("response")
                           ?? obj.Value<string>("text")
                           ?? normalized;

                var claims = new List<string>();
                var claimsToken = obj["claims"];
                if (claimsToken is JArray claimArray)
                {
                    foreach (var claim in claimArray)
                    {
                        if (claim.Type == JTokenType.String)
                        {
                            var claimText = claim.ToString().Trim();
                            if (!string.IsNullOrWhiteSpace(claimText)) claims.Add(claimText);
                        }
                        else if (claim.Type == JTokenType.Object)
                        {
                            var claimText = claim.Value<string>("text") ?? claim.Value<string>("claim");
                            if (!string.IsNullOrWhiteSpace(claimText)) claims.Add(claimText.Trim());
                        }
                    }
                }

                return new DialogueResponseResult
                {
                    Text = string.IsNullOrWhiteSpace(text) ? "*nods silently*" : text.Trim(),
                    Claims = claims,
                    TrustDelta = obj.Value<float?>("trust_delta") ?? 0f,
                    Contradiction = ToPlainObject(obj["contradiction"]),
                    BarterIntent = ToPlainObject(obj["barter_intent"])
                };
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[Dialogue] Structured response parse failed, using plain text. Error={ex.Message}");
                return new DialogueResponseResult { Text = normalized };
            }
        }

        private static object ToPlainObject(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Boolean && token.Value<bool>() == false) return null;
            return token.ToObject<object>();
        }

        private string GenerateGreeting()
        {
            // Context-aware greeting based on NPC state
            if (_activeNPC.Health?.Current < 30)
                return "*clutching wound* Oh... hello. I'm not doing too well...";
            
            if (_activeNPC.Needs?.Rest < 20)
                return "*yawning* Hey... I'm exhausted. What do you need?";
            
            if (_activeNPC.Needs?.Food < 20)
                return "*stomach growling* Hello... have you seen any food around?";

            if (_activeNPC.MoodScore > 70)
                return "Hey there! Beautiful day to be alive, isn't it?";
            
            if (_activeNPC.MoodScore < 30)
                return "*grumbles* What do you want?";

            return "Hello. What can I do for you?";
        }

        private void LoadConversationHistory()
        {
            // Load last few dialogue exchanges from memory
            // This could be implemented to show recent conversation context
        }

        private void SaveConversationToMemory()
        {
            // Save summary of conversation to long-term memory
            if (_currentConversation.Count > 0)
            {
                _memoryManager?.RecordEvent(
                    _activeNPC.Id,
                    "conversation_summary",
                    $"Had a conversation with the player ({_currentConversation.Count / 2} exchanges)",
                    importance: 5
                );
            }
        }

        private Settler GetTargetSettler()
        {
            // Strategy 1: Use the game's selection system (player clicked on a settler)
            var selectedGO = GameBridge.GetSelectedSettler();
            if (selectedGO != null)
            {
                if (GameBridge.TryGetValidatedSettlerIdentity(selectedGO, out var selectedId, out var selectedName, out _))
                {
                    LLMNPCsPlugin.LogToFile($"[Dialogue] Using selected settler: {selectedName} ({selectedId})");
                    var selectedSettler = GameBridge.EnsureSettlerComponent(selectedGO);
                    if (selectedSettler != null)
                        return selectedSettler;
                }
                else
                {
                    LLMNPCsPlugin.LogToFile($"[Dialogue] Ignoring selected object because it is not a validated settler: {selectedGO.name}");
                }
            }

            // Strategy 2: Resolve from current known settlers even without explicit selection.
            var allSettlers = GameBridge.GetValidatedSettlers()
                .Where(s => s != null && s.gameObject != null)
                .ToList();
            LLMNPCsPlugin.LogToFile($"[Dialogue] GetValidatedSettlers returned {allSettlers.Count} settlers");

            if (allSettlers.Count == 0)
                return null;

            // Prefer nearest settler to camera when available.
            var camera = Camera.main;
            if (camera != null)
            {
                GameObject nearest = null;
                float nearestDist = float.MaxValue;

                foreach (var settler in allSettlers)
                {
                    var go = settler.gameObject;
                    if (go == null) continue;

                    var dist = Vector3.Distance(camera.transform.position, go.transform.position);
                    if (dist < nearestDist && dist < 30f)
                    {
                        nearestDist = dist;
                        nearest = go;
                    }
                }

                if (nearest != null)
                {
                    var nearestSettler = GameBridge.EnsureSettlerComponent(nearest);
                    if (nearestSettler != null)
                        return nearestSettler;
                }
            }

            // Last fallback: use first currently known settler.
            foreach (var settler in allSettlers)
            {
                if (settler != null && settler.gameObject != null)
                {
                    LLMNPCsPlugin.LogToFile($"[Dialogue] Using fallback settler from validated list: {settler.gameObject.name}");
                    return settler;
                }
            }

            return null;
        }
    }

    public class DialogueMessage
    {
        public string Text { get; set; }
        public bool IsPlayer { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class DialogueResponseResult
    {
        public string Text { get; set; } = "*nods silently*";
        public List<string> Claims { get; set; } = new List<string>();
        public float TrustDelta { get; set; }
        public object Contradiction { get; set; }
        public object BarterIntent { get; set; }
    }
}
