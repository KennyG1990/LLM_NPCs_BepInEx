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
        private readonly string _baseUrl;
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
            ["npc_to_npc"] = "player2"
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

            _baseUrl = $"http://127.0.0.1:{port}";
            _gameClientId = "going_medieval";

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
            _httpClient.DefaultRequestHeaders.Add("player2-game-key", _gameClientId);
            _httpClient.DefaultRequestHeaders.Add("X-Game-Client-Id", _gameClientId);

            LLMNPCsPlugin.LogToFile($"[LLMClient:Constructor] Initialized Player2 client at {_baseUrl}");
        }

        /// <summary>
        /// Check if the local Player2 companion app is running.
        /// </summary>
        public async Task<bool> CheckHealthAsync()
        {
            try
            {
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
        public async Task<NpcResponse> NpcChatAsync(string npcId, string senderName, string senderMessage, string gameStateInfo)
        {
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
        public async Task<string> SendSimplePromptAsync(string prompt, LLMTraceMetadata traceMetadata = null)
        {
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

        public async Task<string> GetRawResponseAsync(List<Message> messages, LLMTraceMetadata traceMetadata = null)
        {
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
                ["max_tokens"] = 256
            };

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

        public async Task<List<OpenRouterModel>> FetchAvailableModelsAsync()
        {
            return await GetAvailableModelsAsync();
        }

        public string GetCurrentModel() => "player2";
        public void SetModel(string modelId) { }

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
