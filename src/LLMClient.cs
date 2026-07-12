using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// HTTP client for communicating with the local Player2 API.
    /// Replaces the OpenRouter client, routing queries through the local app daemon.
    /// </summary>
    public class LLMClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private string _baseUrl;   // mutable: live provider switching (Reconfigure)
        private readonly string _gameClientId;
        private readonly float _temperature;
        private readonly SemaphoreSlim _npcChatLock = new SemaphoreSlim(1, 1);
        private bool _disposed = false;

        // Static free models stub for config compatibility
        public static readonly string[] FreeModels = new[] { "player2" };
        public static readonly Dictionary<string, string> RecommendedTaskModels = new Dictionary<string, string>
        {
            ["npc_decisions"] = "player2",
            ["player_chat"] = "player2",
            ["npc_to_npc"] = "player2",
            ["adviser"] = "player2"
        };

        public LLMClient(
            string apiKey,
            string model,
            float temperature,
            bool openRouterEnableProviderOverride = true,
            string openRouterDataCollectionMode = "allow",
            string openRouterFallbackModels = null,
            float openRouterPolicyErrorLogCooldownSeconds = 30f,
            string modelNpcDecisions = null,
            string modelPlayerChat = null,
            string modelNpcToNpcChat = null)
        {
            _temperature = temperature;

            // Dynamically discover port from %APPDATA%/game.player2.client/api.port (standard Player2 daemon path)
            string port = "4315";
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var portFile = Path.Combine(appData, "game.player2.client", "api.port");
                if (File.Exists(portFile))
                {
                    string fileContent = File.ReadAllText(portFile).Trim();
                    if (int.TryParse(fileContent, out int parsedPort))
                    {
                        port = fileContent;
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[LLMClient] Failed to read Player2 port file, using default 4315: {ex.Message}");
            }

            _gameClientId = "going_medieval";
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(60);

            // PROVIDER SELECTOR (Ken: Player2 joules ran dry — OpenRouter as
            // configurable backup; the reference docs promise bring-your-own
            // backend anyway). OpenRouter is OpenAI-schema at /api/v1/..., so
            // the existing $"{_baseUrl}/v1/chat/completions" paths just work.
            if (Provider == "openrouter" && !string.IsNullOrEmpty(OpenRouterApiKey))
            {
                _baseUrl = "https://openrouter.ai/api";
                _httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + OpenRouterApiKey);
                LLMNPCsPlugin.LogToFile($"[LLMClient:Constructor] Provider=OPENROUTER model={OpenRouterModel}");
            }
            else
            {
                _baseUrl = $"http://127.0.0.1:{port}";
                _httpClient.DefaultRequestHeaders.Add("player2-game-key", _gameClientId);
                _httpClient.DefaultRequestHeaders.Add("X-Game-Client-Id", _gameClientId);
                LLMNPCsPlugin.LogToFile($"[LLMClient:Constructor] Provider=PLAYER2 at {_baseUrl}");
            }
        }

        // ── CALL BUDGET GOVERNOR (task #23 EMERGENCY: 1,792 calls / $222 /
        // 4.45M tokens burned in one run). ONE choke point for every paid
        // call (chat, simple, raw). Sliding 1h window; suppressed calls return
        // null and every caller already has a deterministic fallback (decision
        // fallbacks, skipped dialogue). Tune via MaxCallsPerHour.
        public static int MaxCallsPerHour = 8;
        // Provider selection (set from Plugin config BEFORE constructing).
        public static string Provider = "player2";           // "player2" | "openrouter"
        public static string OpenRouterApiKey = "";
        public static string OpenRouterModel = "openai/gpt-oss-120b";
        // PER-TASK MODEL ROUTING (Ken): applies on the OpenRouter provider —
        // Player2's daemon owns its own endpoint model, so tasks can't split
        // there. LIVE tasks: npc_decisions | player_chat | npc_to_npc | adviser.
        // RESERVED (no LLM wired yet): planner | chronicle. Empty/missing/"player2"
        // -> the panel-selected OpenRouterModel.
        public static readonly Dictionary<string, string> TaskModels = new Dictionary<string, string>();
        public static string ModelForTask(string task)
        {
            if (!string.IsNullOrEmpty(task) && TaskModels.TryGetValue(task, out var m) && !string.IsNullOrWhiteSpace(m) && m != "player2")
                return m;
            return OpenRouterModel;
        }
        // OpenRouter has no NPC-persona server; personas live HERE per npc.
        private static readonly System.Collections.Generic.Dictionary<string, string> _personas
            = new System.Collections.Generic.Dictionary<string, string>();
        // ── #35 BUDGET LANES: colony-critical tasks must NEVER be starved by
        // NPC chatter (Llangefni died with suppressed=703 — every survival
        // decision, including the unanswered story event, lost the race to
        // dialogue). Non-critical tasks may only fill the window up to
        // (cap - reserve); critical tasks may use the FULL cap, so at least
        // ReservedCriticalCalls slots are always available to them.
        public static int ReservedCriticalCalls = 3;
        private static readonly System.Collections.Generic.HashSet<string> _criticalTasks =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "story_event", "planner", "death_history", "siteplan", "architect" };
        private static readonly System.Collections.Generic.Queue<System.Collections.Generic.KeyValuePair<DateTime, bool>> _spend
            = new System.Collections.Generic.Queue<System.Collections.Generic.KeyValuePair<DateTime, bool>>();
        private static readonly object _spendLock = new object();
        public static int SuppressedCount = 0;
        public static int CriticalSuppressedCount = 0;   // should stay 0 unless critical itself saturates the cap

        /// <summary>Telemetry: window usage split by lane, e.g. "spent 5/8 (crit 1) suppressed=31 (crit 0)".</summary>
        public static string LaneReport()
        {
            lock (_spendLock)
            {
                var cutoff = DateTime.UtcNow.AddHours(-1);
                while (_spend.Count > 0 && _spend.Peek().Key < cutoff) _spend.Dequeue();
                int crit = 0;
                foreach (var s in _spend) if (s.Value) crit++;
                return $"spent {_spend.Count}/{MaxCallsPerHour} (crit {crit}, {ReservedCriticalCalls} reserved) suppressed={SuppressedCount} (crit {CriticalSuppressedCount})";
            }
        }

        private static bool TrySpendBudget(string what)
        {
            lock (_spendLock)
            {
                var cutoff = DateTime.UtcNow.AddHours(-1);
                while (_spend.Count > 0 && _spend.Peek().Key < cutoff) _spend.Dequeue();
                bool critical = !string.IsNullOrEmpty(what) && _criticalTasks.Contains(what);
                int laneCap = critical ? MaxCallsPerHour : Math.Max(0, MaxCallsPerHour - ReservedCriticalCalls);
                if (_spend.Count >= laneCap)
                {
                    SuppressedCount++;
                    if (critical)
                    {
                        // Rare and severe — a critical task hit the FULL cap. Always log.
                        CriticalSuppressedCount++;
                        LLMNPCsPlugin.LogToFile($"[LLMClient] BUDGET: CRITICAL suppressed '{what}' ({_spend.Count}/{MaxCallsPerHour} full cap reached, crit-suppressed {CriticalSuppressedCount} total)");
                    }
                    else if (SuppressedCount % 10 == 1)
                        LLMNPCsPlugin.LogToFile($"[LLMClient] BUDGET: suppressed '{what}' ({_spend.Count}/{laneCap} dialogue lane full, {SuppressedCount} suppressed total) — deterministic fallbacks take over");
                    return false;
                }
                _spend.Enqueue(new System.Collections.Generic.KeyValuePair<DateTime, bool>(DateTime.UtcNow, critical));
                LLMNPCsPlugin.LogToFile($"[LLMClient] BUDGET: spend {_spend.Count}/{MaxCallsPerHour} ({what}{(critical ? " [CRITICAL LANE]" : "")})");
                return true;
            }
        }

        /// <summary>
        /// Check if the local Player2 companion app is running.
        /// </summary>
        public async Task<bool> CheckHealthAsync()
        {
            try
            {
                if (Provider == "openrouter")
                    return !string.IsNullOrEmpty(OpenRouterApiKey);   // no /health endpoint upstream
                var response = await _httpClient.GetAsync($"{_baseUrl}/v1/health");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Spawn a Player2 NPC representing a settler.
        /// </summary>
        public async Task<string> SpawnNpcAsync(string settlerId, string npcName, string npcProfession, string systemPrompt)
        {
            if (Provider == "openrouter")
            {
                // No server-side personas on OpenRouter: keep it locally and use
                // the settlerId as the npc id. Chat assembles it per call.
                lock (_personas) { _personas[settlerId] = systemPrompt ?? ""; }
                LLMNPCsPlugin.LogToFile($"[LLMClient] (openrouter) persona registered locally for {npcName}");
                return settlerId;
            }
            var url = $"{_baseUrl}/v1/npc/games/{_gameClientId}/npcs/spawn";
            
            // Build command whitelists for Player2 bounding
            var commands = new JArray
            {
                JObject.FromObject(new { name = "continue_job", description = "Continue doing your current job or task.", parameters = new { type = "object", properties = new { } } }),
                JObject.FromObject(new {
                    name = "switch_job",
                    description = "Switch to a different job or task (e.g. woodcutting, mining, farming, building, cooking, etc.).",
                    parameters = new {
                        type = "object",
                        properties = new { job = new { type = "string", description = "The name of the job to switch to." } },
                        required = new[] { "job" }
                    }
                }),
                JObject.FromObject(new { name = "rest", description = "Take a break, rest, or sleep in a bed to recover energy.", parameters = new { type = "object", properties = new { } } }),
                JObject.FromObject(new { name = "eat", description = "Find and eat food to satisfy hunger.", parameters = new { type = "object", properties = new { } } }),
                JObject.FromObject(new { name = "drink", description = "Find and drink water, ale, or another beverage to satisfy thirst.", parameters = new { type = "object", properties = new { } } }),
                JObject.FromObject(new { name = "seek_medic", description = "Seek medical treatment for wounds, illness, or serious pain.", parameters = new { type = "object", properties = new { } } }),
                JObject.FromObject(new {
                    name = "socialize",
                    description = "Talk to and socialize with another settler.",
                    parameters = new {
                        type = "object",
                        properties = new { target = new { type = "string", description = "The name of the settler to talk to." } },
                        required = new[] { "target" }
                    }
                }),
                JObject.FromObject(new {
                    name = "complain",
                    description = "Complain or vent frustration to another settler about a colony problem.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            target = new { type = "string", description = "The settler to complain to, if known." },
                            complaint = new { type = "string", description = "The short complaint in character." }
                        }
                    }
                }),
                JObject.FromObject(new { name = "flee", description = "Flee and run away from dangerous threats or enemies immediately.", parameters = new { type = "object", properties = new { } } }),
                JObject.FromObject(new { name = "defend", description = "Equip weapons and prepare for combat or defend the colony.", parameters = new { type = "object", properties = new { } } }),
                JObject.FromObject(new { name = "seek_shelter", description = "Go indoors or seek shelter from bad weather or dangerous environment.", parameters = new { type = "object", properties = new { } } }),
                JObject.FromObject(new { name = "explore", description = "Wander and explore the map.", parameters = new { type = "object", properties = new { } } }),
                JObject.FromObject(new {
                    name = "gather",
                    description = "Gather or harvest raw materials or resources.",
                    parameters = new {
                        type = "object",
                        properties = new { resource = new { type = "string", description = "The resource to gather." } },
                        required = new[] { "resource" }
                    }
                }),
                JObject.FromObject(new {
                    name = "build_special",
                    description = "Propose the construction of a special, mood-boosting building.",
                    parameters = new {
                        type = "object",
                        properties = new { building_name = new { type = "string", description = "The building name (e.g. GreatHall)." } },
                        required = new[] { "building_name" }
                    }
                }),
                JObject.FromObject(new {
                    name = "prioritize_construction",
                    description = "Prioritize the most important pending construction task for the colony.",
                    parameters = new {
                        type = "object",
                        properties = new { building_type = new { type = "string", description = "The building type or priority target, if known." } }
                    }
                }),
                JObject.FromObject(new {
                    name = "build_stockpile",
                    description = "Raise a new stockpile zone in the world near where you stand, giving the colony space to store food and resources. Choose this when storage is scarce or food reserves are low.",
                    parameters = new { type = "object", properties = new { } }
                }),
                JObject.FromObject(new {
                    name = "draft",
                    description = "(TACTICAL) Draft the settler for combat and manually direct them to coordinates.",
                    parameters = new {
                        type = "object",
                        properties = new { target_coordinates = new { type = "string", description = "The target coordinates string, e.g. '[x,y,z]'." } },
                        required = new[] { "target_coordinates" }
                    }
                }),
                JObject.FromObject(new { name = "repair", description = "Prioritize repairing battle damage around the colony.", parameters = new { type = "object", properties = new { } } }),
                JObject.FromObject(new { name = "haul", description = "Prioritize hauling battlefield loot or items.", parameters = new { type = "object", properties = new { } } }),
                JObject.FromObject(new {
                    name = "change_clothing",
                    description = "Change into clothing better suited for current weather, temperature, or work.",
                    parameters = new {
                        type = "object",
                        properties = new { clothing_type = new { type = "string", description = "The desired clothing type, if known." } }
                    }
                }),
                JObject.FromObject(new {
                    name = "capture",
                    description = "Capture a downed enemy and drag them to a designated prison room.",
                    parameters = new {
                        type = "object",
                        properties = new { target = new { type = "string", description = "The name of the enemy to capture." } },
                        required = new[] { "target" }
                    }
                }),
                JObject.FromObject(new { name = "rebrand", description = "Iteratively find wooden walls and upgrade them to stone/ornate versions.", parameters = new { type = "object", properties = new { } } })
            };

            var payload = new JObject
            {
                ["name"] = npcName,
                ["short_name"] = npcName.Split(' ')[0],
                ["character_description"] = $"A {npcProfession} in a medieval colony.",
                ["system_prompt"] = systemPrompt,
                ["voice_id"] = "01955d76-ed5b-74de-83e5-800a44fee0d1", // Caleb fallback
                ["keep_game_state"] = false, // We supply context from SQLite explicitly
                ["commands"] = commands
            };

            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var respText = await response.Content.ReadAsStringAsync();
            respText = respText.Trim('"');
            return respText;
        }

        /// <summary>
        /// Sends a chat message to a spawned Player2 NPC and parses the NDJSON streamed response.
        /// </summary>
        public async Task<NpcResponse> NpcChatAsync(string npcId, string senderName, string senderMessage, string gameStateInfo, string task = "npc_decisions")
        {
            if (!TrySpendBudget(task)) return null;   // governor: callers fall back
            if (Provider == "openrouter")
                return await OpenRouterNpcChatAsync(npcId, senderName, senderMessage, gameStateInfo, task);
            await _npcChatLock.WaitAsync();
            try
            {
                var streamUrl = $"{_baseUrl}/v1/npc/games/{_gameClientId}/npcs/responses";
                var chatUrl = $"{_baseUrl}/v1/npc/games/{_gameClientId}/npcs/{npcId}/chat";

                var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/x-ndjson"));

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var reader = new StreamReader(stream))
                {
                    // Send the POST chat request after opening the stream so the reply is not missed.
                    var chatPayload = new JObject
                    {
                        ["sender_name"] = senderName,
                        ["sender_message"] = senderMessage
                    };
                    if (!string.IsNullOrEmpty(gameStateInfo))
                    {
                        chatPayload["game_state_info"] = gameStateInfo;
                    }

                    var chatContent = new StringContent(chatPayload.ToString(), Encoding.UTF8, "application/json");
                    var postResponse = await _httpClient.PostAsync(chatUrl, chatContent);
                    postResponse.EnsureSuccessStatusCode();

                    // Player2 streams newline-delimited JSON objects: { npc_id, message, command, audio }.
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < deadline)
                    {
                        string line = await reader.ReadLineAsync();
                        if (line == null) break;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var obj = JObject.Parse(line);
                            if (obj["npc_id"]?.ToString() == npcId && obj["message"] != null)
                            {
                                var msg = obj["message"]?.ToString();
                                return new NpcResponse
                                {
                                    Message = msg,
                                    Command = obj["command"]
                                };
                            }
                        }
                        catch (Exception ex)
                        {
                            LLMNPCsPlugin.LogToFile($"[LLMClient] Failed to parse stream line: {ex.Message}");
                        }
                    }
                }

                throw new TimeoutException("Player2 NPC response stream closed before receiving message.");
            }
            finally
            {
                _npcChatLock.Release();
            }
        }

        public Task<LLMDecision> GetDecisionAsync(NPCContext context, List<Message> conversationHistory = null, LLMTraceMetadata traceMetadata = null)
        {
            // Retained for signature compatibility; handled dynamically in the DecisionEngine refactor.
            return Task.FromResult<LLMDecision>(null);
        }

        /// <summary>
        /// Sends a raw chat completion request using Player2 alternative OpenAI endpoint format.
        /// </summary>
        /// <summary>OpenRouter NPC chat: persona (local) + game state as system,
        /// message as user; model instructed to answer as strict JSON
        /// {message, command:{name, args}} matching the command whitelist the
        /// decision prompts already describe. Parsed into NpcResponse.</summary>
        private async Task<NpcResponse> OpenRouterNpcChatAsync(string npcId, string senderName, string senderMessage, string gameStateInfo, string task = "npc_decisions")
        {
            try
            {
                string persona;
                lock (_personas) { _personas.TryGetValue(npcId, out persona); }
                var system = (persona ?? "You are a medieval settler.")
                    + "\n\nCURRENT GAME STATE (JSON):\n" + (gameStateInfo ?? "{}")
                    + "\n\nRespond ONLY with strict JSON: {\"message\": \"your in-character words\", "
                    + "\"command\": {\"name\": \"<one command name from the options in the user message, or continue_job>\", \"args\": {}}}";
                var payload = new JObject
                {
                    ["model"] = ModelForTask(task),
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "system", ["content"] = system },
                        new JObject { ["role"] = "user", ["content"] = $"[{senderName}] {senderMessage}" }
                    },
                    ["stream"] = false,
                    ["temperature"] = _temperature,
                    ["max_tokens"] = 400
                };
                var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync($"{_baseUrl}/v1/chat/completions", content);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    LLMNPCsPlugin.LogToFile($"[LLMClient] openrouter chat HTTP {(int)resp.StatusCode}: {body.Substring(0, Math.Min(200, body.Length))}");
                    return null;
                }
                var text = JObject.Parse(body)?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                // tolerate fenced or prefixed JSON
                int s0 = text.IndexOf('{');
                int s1 = text.LastIndexOf('}');
                if (s0 >= 0 && s1 > s0)
                {
                    try
                    {
                        var j = JObject.Parse(text.Substring(s0, s1 - s0 + 1));
                        return new NpcResponse { Message = j["message"]?.ToString() ?? text, Command = j["command"] };
                    }
                    catch { }
                }
                return new NpcResponse { Message = text, Command = null };
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile("[LLMClient] openrouter chat EXC: " + ex.Message);
                return null;
            }
        }

        public async Task<string> SendSimplePromptAsync(string prompt, LLMTraceMetadata traceMetadata = null, string task = "npc_to_npc")
        {
            if (!TrySpendBudget(task)) return null;   // governor
            var url = $"{_baseUrl}/v1/chat/completions";
            var messages = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = "You are a character in a medieval colony simulation game. Respond in character." },
                new JObject { ["role"] = "user", ["content"] = prompt }
            };

            var payload = new JObject
            {
                ["messages"] = messages,
                ["stream"] = false,
                ["temperature"] = _temperature,
                ["max_tokens"] = 256
            };
            if (Provider == "openrouter") payload["model"] = ModelForTask(task);   // required upstream

            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Player2 completions API failed: {response.StatusCode} - {errorBody}");
            }

            var respText = await response.Content.ReadAsStringAsync();
            var obj = JObject.Parse(respText);
            var choices = obj["choices"] as JArray;
            if (choices != null && choices.Count > 0)
            {
                return choices[0]["message"]?["content"]?.ToString()?.Trim();
            }

            return null;
        }

        public async Task<string> GetRawResponseAsync(List<Message> messages, LLMTraceMetadata traceMetadata = null, string task = "npc_to_npc", int maxTokens = 256)
        {
            if (!TrySpendBudget(task)) return null;   // governor
            var url = $"{_baseUrl}/v1/chat/completions";
            var msgArray = new JArray();
            foreach (var msg in messages)
            {
                msgArray.Add(new JObject { ["role"] = msg.Role, ["content"] = msg.Content });
            }

            var payload = new JObject
            {
                ["messages"] = msgArray,
                ["stream"] = false,
                ["temperature"] = _temperature,
                // 256 default suits one-liner decisions; structured outputs
                // (planner JSON) truncate mid-document at 256 and parse-fail
                // (burned 4 critical calls on 2026-07-11) — callers size it.
                ["max_tokens"] = maxTokens
            };
            if (Provider == "openrouter") payload["model"] = ModelForTask(task);   // required upstream

            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var respText = await response.Content.ReadAsStringAsync();
            var obj = JObject.Parse(respText);
            var choices = obj["choices"] as JArray;
            if (choices != null && choices.Count > 0)
            {
                return choices[0]["message"]?["content"]?.ToString()?.Trim();
            }

            return null;
        }

        public Task<float[]> GetEmbeddingAsync(string text, string modelOverride = null)
        {
            // Returns a dummy 128-dimensional embedding vector so it doesn't break SQLite schemas or logic.
            // The memory search falls back gracefully.
            return Task.FromResult(new float[128]);
        }

        public Task<List<OpenRouterModel>> GetAvailableModelsAsync()
        {
            return Task.FromResult(new List<OpenRouterModel> { new OpenRouterModel { Id = "player2", Name = "Player2 Model", IsFree = true } });
        }

        /// <summary>Real model list: Player2 entry + the OpenRouter catalog
        /// (public endpoint; key only needed for chat). The in-game panel's
        /// 'Fetch Models from OpenRouter' button lands here — no config-file
        /// editing (Ken). Selecting a model switches provider live.</summary>
        public async Task<List<OpenRouterModel>> FetchAvailableModelsAsync()
        {
            var list = new List<OpenRouterModel> { new OpenRouterModel { Id = "player2", Name = "Player2 Model (local daemon)", IsFree = true } };
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models"))
                {
                    var resp = await _httpClient.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (resp.IsSuccessStatusCode)
                    {
                        var data = JObject.Parse(body)?["data"] as JArray;
                        if (data != null)
                            foreach (var m in data)
                            {
                                var promptPrice = m["pricing"]?["prompt"]?.ToString() ?? "0";
                                var completionPrice = m["pricing"]?["completion"]?.ToString() ?? "0";
                                list.Add(new OpenRouterModel
                                {
                                    Id = m["id"]?.ToString(),
                                    Name = m["name"]?.ToString() ?? m["id"]?.ToString(),
                                    ContextLength = m["context_length"]?.ToObject<int>() ?? 4096,
                                    PricingPrompt = promptPrice,
                                    PricingCompletion = completionPrice,
                                    IsFree = promptPrice == "0" || (m["id"]?.ToString() ?? "").EndsWith(":free")
                                });
                            }
                        LLMNPCsPlugin.LogToFile($"[LLMClient] fetched {list.Count - 1} OpenRouter models");
                    }
                    else LLMNPCsPlugin.LogToFile($"[LLMClient] OpenRouter models HTTP {(int)resp.StatusCode}");
                }
            }
            catch (Exception ex) { LLMNPCsPlugin.LogToFile("[LLMClient] model fetch EXC: " + ex.Message); }
            return list;
        }

        public string GetCurrentModel() => Provider == "openrouter" ? OpenRouterModel : "player2";

        /// <summary>Switch provider LIVE from the in-game panel: 'player2' id
        /// selects the local daemon; any other id selects OpenRouter with that
        /// model. Rebuilds base URL + auth headers in place.</summary>
        public void SetModel(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return;
            if (modelId == "player2")
            {
                Provider = "player2";
            }
            else
            {
                Provider = "openrouter";
                OpenRouterModel = modelId;
                OpenRouterApiKey = (LLMNPCsPlugin.Instance?.ApiKey?.Value ?? OpenRouterApiKey ?? "").Trim();
            }
            Reconfigure();
            LLMNPCsPlugin.LogToFile($"[LLMClient] SetModel -> provider={Provider} model={GetCurrentModel()} keySet={!string.IsNullOrEmpty(OpenRouterApiKey)}");
        }

        /// <summary>Re-point base URL + auth headers after a live provider switch.</summary>
        private void Reconfigure()
        {
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.Remove("player2-game-key");
            _httpClient.DefaultRequestHeaders.Remove("X-Game-Client-Id");
            if (Provider == "openrouter" && !string.IsNullOrEmpty(OpenRouterApiKey))
            {
                _baseUrl = "https://openrouter.ai/api";
                _httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + OpenRouterApiKey);
            }
            else
            {
                Provider = "player2";
                _baseUrl = $"http://127.0.0.1:{ReadPlayer2Port()}";
                _httpClient.DefaultRequestHeaders.Add("player2-game-key", _gameClientId);
                _httpClient.DefaultRequestHeaders.Add("X-Game-Client-Id", _gameClientId);
            }
        }

        private static string ReadPlayer2Port()
        {
            try
            {
                var pf = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "game.player2.client", "api.port");
                if (System.IO.File.Exists(pf)) return System.IO.File.ReadAllText(pf).Trim();
            }
            catch { }
            return "4315";
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _httpClient?.Dispose();
                    _npcChatLock?.Dispose();
                }
                _disposed = true;
            }
        }
    }

    public class NpcResponse
    {
        public string Message { get; set; }
        public JToken Command { get; set; }
    }
}
