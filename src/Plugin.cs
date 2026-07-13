using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// BepInEx plugin entry point for LLM NPCs mod.
    /// Hooks into Going Medieval's settler AI system to enable LLM-driven decision making.
    /// </summary>
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class LLMNPCsPlugin : BaseUnityPlugin
    {
        // Use PluginInfo constants for consistency
        public const string PLUGIN_GUID = PluginInfo.PLUGIN_GUID;
        public const string PLUGIN_NAME = PluginInfo.PLUGIN_NAME;
        public const string PLUGIN_VERSION = PluginInfo.PLUGIN_VERSION;

        internal static ManualLogSource Log;
        internal static LLMNPCsPlugin Instance;

        // Configuration
        public ConfigEntry<string> ApiKey { get; private set; }
        public ConfigEntry<string> Model { get; private set; }
        public ConfigEntry<string> ModelNpcDecisions { get; private set; }
        public ConfigEntry<string> ModelPlayerChat { get; private set; }
        public ConfigEntry<string> ModelNpcToNpcChat { get; private set; }
        public ConfigEntry<string> ModelAdviser { get; private set; }
        public ConfigEntry<int> NpcToNpcIntervalMinutes { get; private set; }
        public ConfigEntry<int> PlannerIntervalMinutes { get; private set; }
        public ConfigEntry<float> Temperature { get; private set; }
        public ConfigEntry<float> DecisionInterval { get; private set; }
        public ConfigEntry<bool> EnableFullAutonomy { get; private set; }
        public ConfigEntry<bool> EnableMod { get; private set; }
        public ConfigEntry<bool> LogDecisions { get; private set; }
        public ConfigEntry<KeyboardShortcut> DialogueHotkey { get; private set; }
        public ConfigEntry<KeyboardShortcut> SocialHubHotkey { get; private set; }
        public ConfigEntry<bool> EnablePromptTracing { get; private set; }
        public ConfigEntry<bool> PromptMonitorVisible { get; private set; }
        public ConfigEntry<KeyboardShortcut> PromptMonitorHotkey { get; private set; }
        public ConfigEntry<bool> OpenRouterEnableProviderOverride { get; private set; }
        public ConfigEntry<string> OpenRouterDataCollectionMode { get; private set; }
        public ConfigEntry<string> OpenRouterFallbackModels { get; private set; }
        public ConfigEntry<float> OpenRouterPolicyErrorLogCooldownSeconds { get; private set; }

        // Harmony instance kept as a field so OnDestroy can UnpatchSelf — required
        // for clean ScriptEngine HOT-RELOAD (else reloads stack duplicate patches).
        private Harmony _harmony;

        // Core Systems
        internal LLMClient LLMClient { get; private set; }
        internal NPCRegistry NPCRegistry { get; private set; }
        internal DecisionEngine DecisionEngine { get; private set; }
        internal MemoryManager MemoryManager { get; private set; }
        internal DialogueManager DialogueManager { get; private set; }
        internal MenuIntegration MenuIntegration { get; private set; }
        
        // Social Systems (NPC-to-NPC interactions)
        internal RelationshipSystem RelationshipSystem { get; private set; }
        internal ChatBubbleManager ChatBubbleManager { get; private set; }
        internal NPCToNPCDialogueManager NPCToNPCDialogueManager { get; private set; }
        internal SocialHubWindow SocialHubWindow { get; private set; }
        internal PromptDebugMonitorWindow PromptDebugMonitorWindow { get; private set; }
        private readonly HashSet<string> _activeDecisionRequests = new HashSet<string>();
        private DateTime _lastColonyInfluenceUtc = DateTime.MinValue;
        private bool _colonyInfluenceInFlight;
        private const double ColonyInfluenceIntervalSeconds = 900d; // was 60s — a per-minute LLM call; 15 min is plenty

        private void OnDestroy()
        {
            Log.LogInfo("[Plugin] Shutting down...");

            try
            {
                // Un-patch Harmony so a ScriptEngine HOT-RELOAD doesn't stack a second
                // set of patches on top of the first (double-execution / crashes).
                try { _harmony?.UnpatchSelf(); LogToFile("[Plugin] Harmony unpatched (hot-reload safe)"); } catch { }
                if (MenuIntegration != null)
                {
                    Destroy(MenuIntegration.gameObject);
                }
                RelationshipSystem?.Dispose();
                MemoryManager?.Dispose();
                LLMClient?.Dispose();
                PromptTrace.Dispose();
                _logFile?.Close();
            }
            catch (Exception ex)
            {
                Log.LogError($"[Plugin] Error during shutdown: {ex}");
                File.AppendAllText(
                    Path.Combine(Application.persistentDataPath, "LLM_NPCs", "crash.log"),
                    $"[{DateTime.UtcNow}] Shutdown error: {ex}\n");
            }
        }

        private void SetupFileLogging()
        {
            try
            {
                var logDir = Path.Combine(Application.persistentDataPath, "LLM_NPCs", "logs");
                Directory.CreateDirectory(logDir);
                _logDirectory = logDir;
                var logPath = Path.Combine(logDir, $"mod_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                _logFile = new StreamWriter(logPath, true);
                _logFile.WriteLine($"[{DateTime.UtcNow}] LLM NPCs v{PLUGIN_VERSION} started");
                _logFile.WriteLine($"[{DateTime.UtcNow}] Game version: {Application.unityVersion}");
                _logFile.WriteLine($"[{DateTime.UtcNow}] Platform: {Application.platform}");
                _logFile.Flush();
                Log.LogInfo($"[Plugin] Log file: {logPath}");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Plugin] Failed to setup file logging: {ex}");
            }
        }

        public static void LogToFile(string message)
        {
            if (Instance?._logFile != null)
            {
                Instance._logFile.WriteLine($"[{DateTime.UtcNow}] {message}");
                Instance._logFile.Flush();
            }
        }

        private bool _autonomyForcedOnce = false;

        private void Update()
        {
            // FREEZE DETECTOR heartbeat — every frame, first thing (Ken 2026-07-12:
            // "the game is freezing constantly"). The watchdog thread attributes
            // any stall >2s to whatever phase was running.
            FreezeDetector.Beat();

            // Autonomy is the operating mode for this build (the villagers run the
            // colony themselves). An older/stale config could pin EnableFullAutonomy
            // off; correct it ONCE at startup, then respect the in-panel toggle.
            if (!_autonomyForcedOnce && EnableFullAutonomy != null)
            {
                _autonomyForcedOnce = true;
                if (!EnableFullAutonomy.Value)
                {
                    EnableFullAutonomy.Value = true;
                    try { Config.Save(); } catch { }
                }
            }
            AutonomyManager.Instance.IsFullAutonomyEnabled = EnableFullAutonomy?.Value ?? true;

            PromptTrace.SetEnabled(EnablePromptTracing?.Value ?? true);

            // REAL-TIME (game time may be frozen by the very screens this
            // dismisses — Dowsby's famine fell in frozen time, 2026-07-12).
            FreezeDetector.SetPhase("mod:recap-dismisser");
            RecapDismisser.Tick();
            // Blocking EVENT dialogs also freeze game time — the leader must be
            // able to answer them from real time (same deadlock class).
            FreezeDetector.SetPhase("mod:event-pump");
            EventInteractor.RealTimePump();

            FreezeDetector.SetPhase("mod:dialogue-ui");
            DialogueManager?.Update();
            MenuIntegration?.Update();
            FreezeDetector.Clear();   // mod frame work done — stalls after this are the ENGINE's
            
            // Social systems update
            ChatBubbleManager?.Update();
            NPCToNPCDialogueManager?.Update();
            SocialHubWindow?.Update();
            PromptDebugMonitorWindow?.Update();
        }

        private void OnGUI()
        {
            DialogueManager?.OnGUI();
            // Mod toolbar + settings window: drawn from HERE because this OnGUI
            // provably renders in-game (chat windows do), while MenuIntegration's
            // Unity-called OnGUI was observed dead during gameplay (Ken:
            // 'there is no config menu'). Single render path.
            MenuIntegration?.DrawGUI();

            // Social systems GUI
            ChatBubbleManager?.OnGUI();
            SocialHubWindow?.OnGUI();
            PromptDebugMonitorWindow?.OnGUI();
        }

        private StreamWriter _logFile;
        private string _logDirectory;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            
            // Ensure plugin survives scene transitions
            DontDestroyOnLoad(gameObject);

            // Setup file logging for debugging (must be first so LogToFile works)
            SetupFileLogging();
            LogToFile("[Plugin:Awake] Starting initialization");
            LogAssemblyFingerprint();

            Log.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loading...");
            Log.LogInfo($"Game version: {Application.unityVersion}");
            Log.LogInfo($"Platform: {Application.platform}");

            try
            {
                SetupConfiguration();
                LogToFile("[Plugin:Awake] Configuration setup complete");

                // DASHBOARD AUTO-START (Ken believed this always existed — it
                // never did; the python process just happened to stay alive for
                // days). If 8714 doesn't answer, spawn start_dashboard.bat
                // hidden, so the colony's memory brain truly launches with the
                // game and picks up dashboard code changes on game restarts.
                EnsureDashboardRunning();

                PromptTrace.Initialize(_logDirectory, EnablePromptTracing.Value);
                LogToFile("[Plugin:Awake] PromptTrace initialized");
                
                if (!EnableMod.Value)
                {
                    Log.LogInfo("Mod is disabled in config. Not initializing.");
                    LogToFile("[Plugin:Awake] Mod disabled in config, returning early");
                    return;
                }

                InitializeSystems();
                LogToFile("[Plugin:Awake] Systems initialized");

                // Initialize Harmony and apply patches
                _harmony = new Harmony(PLUGIN_GUID);
                _harmony.PatchAll();
                Log.LogInfo("[Plugin:Awake] Harmony patches applied successfully.");
                LogToFile("[Plugin:Awake] Harmony patches applied successfully.");
                
                // Start the main loop
                StartCoroutine(MainLoop());
                LogToFile("[Plugin:Awake] Main loop started");

                Log.LogInfo($"{PLUGIN_NAME} loaded successfully!");
                LogToFile("[Plugin:Awake] Plugin loaded successfully");
            }
            catch (Exception ex)
            {
                Log.LogError($"Critical error during initialization: {ex}");
                Log.LogError("Mod will not function. Check DEBUGGING.md for help.");
                LogToFile($"[Plugin:Awake] CRITICAL ERROR: {ex}");
                enabled = false; // Disable the plugin component
            }
        }

        private void SetupConfiguration()
        {
            LogToFile("[Plugin:SetupConfiguration] Starting");
            
            // Ensure ConfigurationManager uses a key that doesn't conflict with the game
            // Going Medieval uses F1 for building menu, so we use Insert instead
            var configManagerConfig = Config.Bind(
                "General",
                "ConfigManagerHotkey",
                "Insert",
                "Hotkey to open Configuration Manager (default: Insert). Change in com.bepis.bepinex.configurationmanager.cfg"
            );
            
            // General settings
            EnableMod = Config.Bind(
                "General",
                "EnableMod",
                true,
                "Enable or disable the LLM NPCs mod"
            );

            // LLM Settings
            ApiKey = Config.Bind(
                "LLM",
                "ApiKey",
                "",
                "OpenRouter API key (encrypted storage recommended)"
            );

            Model = Config.Bind(
                "LLM",
                "Model",
                LLMClient.FreeModels[0],
                "Fallback/default LLM model if per-task model is blank. Free models: " + string.Join(", ", LLMClient.FreeModels)
            );

            // PROVIDER SELECTOR (Ken: Player2 joules ran dry). "player2" (local
            // daemon, default) or "openrouter" (uses LLM.ApiKey + LLM.Model).
            var provider = Config.Bind(
                "LLM",
                "Provider",
                "player2",
                "LLM backend: 'player2' (local companion app) or 'openrouter' (cloud, needs ApiKey)."
            );
            var callsPerHour = Config.Bind(
                "LLM",
                "MaxLLMCallsPerHour",
                8,
                "Hard budget cap across ALL LLM calls (decisions, dialogue, summaries). Suppressed calls use deterministic fallbacks."
            );
            LLMClient.Provider = (provider.Value ?? "player2").Trim().ToLowerInvariant();
            LLMClient.OpenRouterApiKey = (ApiKey.Value ?? "").Trim();
            LLMClient.OpenRouterModel = string.IsNullOrWhiteSpace(Model.Value) ? "openai/gpt-oss-120b" : Model.Value.Replace(":free", "");
            LLMClient.MaxCallsPerHour = Math.Max(1, callsPerHour.Value);

            // PER-TASK MODELS (OpenRouter only; Ken: each brain gets the right
            // size). Each entry documents EXACTLY what that model does.
            var modelPlanner = Config.Bind("LLM.TaskModels", "Planner",
                "",
                "COLONY PLANNER (LIVE): the elected leader's voice deciding WHERE to build — reads the full-map + " +
                "colony state and emits a site PREFERENCE the deterministic SiteScorer solves. Low-frequency, " +
                "high-stakes — use a SMART model. Empty = panel-selected model.");
            var modelChronicle = Config.Bind("LLM.TaskModels", "Chronicle",
                "",
                "[RESERVED — NOT YET WIRED: death histories are deterministic templates today, no LLM. Setting this " +
                "does nothing until an LLM chronicle writer is built.] CHRONICLES & SUMMARIES (intended): death " +
                "histories, memory summaries, family stories — long-form, mid-tier w/ long context. Empty = panel model.");
            // PER-TASK INTERVALS (player_chat deliberately has none: real-time).
            NpcToNpcIntervalMinutes = Config.Bind("LLM.Intervals", "NpcToNpcConversationMinutes", 15,
                "Minutes between ambient NPC-to-NPC conversations (each costs 1 call). Raise to save budget.");
            PlannerIntervalMinutes = Config.Bind("LLM.Intervals", "PlannerMinutes", 30,
                "Minutes between colony-planner calls (also replans on crises). The planner is the expensive brain — keep this high.");
            // NOTE: TaskModels are pushed AFTER the per-task binds below —
            // reading ModelNpcDecisions.Value here crashed Awake (NRE, live).

            ModelNpcDecisions = Config.Bind(
                "LLM",
                "ModelNpcDecisions",
                LLMClient.RecommendedTaskModels["npc_decisions"],
                "Model for autonomous NPC behaviour decisions (high-frequency). Recommended: google/gemini-2.0-flash-lite-001"
            );

            ModelPlayerChat = Config.Bind(
                "LLM",
                "ModelPlayerChat",
                LLMClient.RecommendedTaskModels["player_chat"],
                "Model for player<->NPC conversations (best quality). Recommended: openai/gpt-oss-120b:free"
            );

            ModelNpcToNpcChat = Config.Bind(
                "LLM",
                "ModelNpcToNpcChat",
                LLMClient.RecommendedTaskModels["npc_to_npc"],
                "Model for NPC<->NPC autonomous dialogue (bulk, low cost). Recommended: meta-llama/llama-3.2-3b-instruct:free"
            );

            ModelAdviser = Config.Bind(
                "LLM",
                "ModelAdviser",
                LLMClient.RecommendedTaskModels["adviser"],
                "Model for the colony ADVISER/OVERSEER narrative nudge (deterministic engine picks the recommendation; " +
                "the LLM writes the immersive 1-2 sentence 'you should act' line). Low-frequency. Recommended: a cheap voice model."
            );

            // PER-TASK MODEL ROUTING → LLMClient (now that all binds exist).
            LLMClient.TaskModels["npc_decisions"] = ModelNpcDecisions.Value;   // AUTONOMOUS DECISIONS: high-frequency next-action picks — cheapest/fastest model
            LLMClient.TaskModels["player_chat"] = ModelPlayerChat.Value;       // PLAYER CONVERSATIONS: real-time, player-facing — best voice (NO interval)
            LLMClient.TaskModels["npc_to_npc"] = ModelNpcToNpcChat.Value;      // NPC-TO-NPC BANTER: ambient social dialogue — mid-tier
            LLMClient.TaskModels["adviser"] = ModelAdviser.Value;             // COLONY ADVISER: overseer narrative nudge — low-frequency
            LLMClient.TaskModels["planner"] = modelPlanner.Value;             // RESERVED — no LLM planner wired yet
            LLMClient.TaskModels["chronicle"] = modelChronicle.Value;         // RESERVED — death histories are deterministic today
            LogToFile($"[Plugin:SetupConfiguration] Provider={LLMClient.Provider} budget={LLMClient.MaxCallsPerHour}/hr keySet={LLMClient.OpenRouterApiKey.Length > 0} taskModels=[dec:{ModelNpcDecisions.Value}|chat:{ModelPlayerChat.Value}|n2n:{ModelNpcToNpcChat.Value}|adv:{ModelAdviser.Value}|plan(reserved):{modelPlanner.Value}|chron(reserved):{modelChronicle.Value}]");

            Temperature = Config.Bind(
                "LLM",
                "Temperature",
                0.7f,
                "Temperature for LLM responses (0.0 - 1.0)"
            );

            OpenRouterEnableProviderOverride = Config.Bind(
                "API",
                "OpenRouterEnableProviderOverride",
                false,
                "If true, includes provider.data_collection block in OpenRouter payloads to enforce privacy. Default is false."
            );

            OpenRouterDataCollectionMode = Config.Bind(
                "LLM",
                "OpenRouterDataCollectionMode",
                "allow",
                "OpenRouter provider.data_collection mode: allow or deny"
            );

            OpenRouterFallbackModels = Config.Bind(
                "LLM",
                "OpenRouterFallbackModels",
                string.Empty,
                "Comma-separated fallback model IDs to try on OpenRouter data policy mismatch (404)"
            );

            OpenRouterPolicyErrorLogCooldownSeconds = Config.Bind(
                "LLM",
                "OpenRouterPolicyErrorLogCooldownSeconds",
                30f,
                "Cooldown in seconds to suppress repeated identical OpenRouter policy mismatch logs"
            );

            DecisionInterval = Config.Bind(
                "Gameplay",
                "DecisionInterval",
                300f,
                "Seconds between routine LLM decisions for each NPC. Event triggers "
                + "(raids, etc.) still fire immediately. Each decision is several LLM "
                + "calls, so keep this high. 300s (5 min) per settler is a good default."
            );
            // Guard against cost-heavy values persisted in existing config files:
            // never let routine decisions run more than once every 2 minutes.
            if (DecisionInterval.Value < 120f)
            {
                Log.LogWarning($"[Config] DecisionInterval was {DecisionInterval.Value}s (too frequent/expensive); raising to 300s.");
                DecisionInterval.Value = 300f;
            }

            EnableFullAutonomy = Config.Bind(
                "Gameplay",
                "EnableFullAutonomy",
                true,   // default ON: the whole point is villagers building the village themselves (Strategic Model). Toggle off in the LLM NPCs Settings panel to make NPCs only reason/complain.
                "If true, LLM decisions AND the deterministic Strategic Model are executed as real blueprints/jobs (villagers build the village themselves). If false, NPCs only reason and complain via dialogue bubbles."
            );

            LogDecisions = Config.Bind(
                "Debug",
                "LogDecisions",
                true,
                "Log NPC decisions to console"
            );

            EnablePromptTracing = Config.Bind(
                "Debug",
                "EnablePromptTracing",
                true,
                "Enable detailed prompt/response tracing to dedicated trace logs and monitor"
            );

            PromptMonitorVisible = Config.Bind(
                "Debug",
                "ShowPromptDebugMonitor",
                false,
                "Show the prompt debug monitor window on startup"
            );

            DialogueHotkey = Config.Bind(
                "Hotkeys",
                "DialogueHotkey",
                new KeyboardShortcut(KeyCode.BackQuote),
                "Hotkey to open dialogue with nearest settler (default: ` backtick/tilde key)"
            );

            SocialHubHotkey = Config.Bind(
                "Hotkeys",
                "SocialHubHotkey",
                new KeyboardShortcut(KeyCode.Y),
                "Hotkey to open the Social Hub window (default: Y)"
            );

            PromptMonitorHotkey = Config.Bind(
                "Hotkeys",
                "PromptDebugMonitorHotkey",
                new KeyboardShortcut(KeyCode.F8),
                "Hotkey to toggle prompt debug monitor visibility (default: F8)"
            );

            LogToFile("[Plugin:SetupConfiguration] Complete");
        }

        /// <summary>Spawn the dashboard server if 8714 isn't answering. The
        /// health probe is quick and non-blocking beyond ~1.5s at startup.</summary>
        private void EnsureDashboardRunning()
        {
            try
            {
                var req = System.Net.WebRequest.Create("http://127.0.0.1:8714/health");
                req.Timeout = 1500;
                using (req.GetResponse()) { }
                LogToFile("[Plugin:Dashboard] already running on 8714");
            }
            catch
            {
                try
                {
                    string bat = @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\start_dashboard.bat";
                    if (System.IO.File.Exists(bat))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = "/c \"" + bat + "\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = System.IO.Path.GetDirectoryName(bat)
                        };
                        System.Diagnostics.Process.Start(psi);
                        LogToFile("[Plugin:Dashboard] 8714 not answering — spawned start_dashboard.bat");
                    }
                    else LogToFile("[Plugin:Dashboard] start_dashboard.bat not found");
                }
                catch (Exception ex) { LogToFile("[Plugin:Dashboard] spawn failed: " + ex.Message); }
            }
        }

        private void InitializeSystems()
        {
            LogToFile("[Plugin:InitializeSystems] Starting");
            try
            {
                MemoryManager = new MemoryManager();
                Log.LogInfo("[MemoryManager] Hierarchical SQLite memory system initialized");
                LogToFile("[Plugin:InitializeSystems] MemoryManager created");
            }
            catch (Exception ex)
            {
                Log.LogError($"[MemoryManager] Failed to initialize: {ex.Message}");
                Log.LogWarning("[MemoryManager] Continuing without persistent memory. Decisions will not be remembered.");
                LogToFile($"[Plugin:InitializeSystems] MemoryManager ERROR: {ex.Message}");
                MemoryManager = null; // Will be null-checked later
            }

            NPCRegistry = new NPCRegistry();
            LogToFile("[Plugin:InitializeSystems] NPCRegistry created");
            
            try
            {
                LLMClient = new LLMClient(
                    ApiKey.Value,
                    Model.Value,
                    Temperature.Value,
                    OpenRouterEnableProviderOverride.Value,
                    OpenRouterDataCollectionMode.Value,
                    OpenRouterFallbackModels.Value,
                    OpenRouterPolicyErrorLogCooldownSeconds.Value,
                    ModelNpcDecisions.Value,
                    ModelPlayerChat.Value,
                    ModelNpcToNpcChat.Value);

                if (string.IsNullOrEmpty(ApiKey.Value))
                {
                    Log.LogWarning("[LLMClient] API key not configured. Mod will not make LLM decisions.");
                    Log.LogWarning("[LLMClient] Click MOD CONFIG button in main menu to set up.");
                    LogToFile("[Plugin:InitializeSystems] LLMClient created but no API key");
                }
                else
                {
                    LogToFile("[Plugin:InitializeSystems] LLMClient created with API key");
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"[LLMClient] Failed to initialize: {ex.Message}");
                LogToFile($"[Plugin:InitializeSystems] LLMClient ERROR: {ex.Message}");
            }

            // Inject LLM client into memory manager so it can do LLM summarization
            MemoryManager?.SetLLMClient(LLMClient);

            DecisionEngine = new DecisionEngine(LLMClient, NPCRegistry, MemoryManager);
            LogToFile("[Plugin:InitializeSystems] DecisionEngine created");

            // Initialize dialogue system
            try
            {
                DialogueManager = new DialogueManager(LLMClient, MemoryManager, NPCRegistry, DialogueHotkey);
                Log.LogInfo($"[DialogueManager] Dialogue system initialized - Press {DialogueHotkey.Value.MainKey} to talk to settlers");
                LogToFile("[Plugin:InitializeSystems] DialogueManager created");
            }
            catch (Exception ex)
            {
                Log.LogError($"[DialogueManager] Failed to initialize: {ex.Message}");
                LogToFile($"[Plugin:InitializeSystems] DialogueManager ERROR: {ex.Message}");
            }

            // Initialize social systems
            try
            {
                RelationshipSystem = new RelationshipSystem(MemoryManager);
                Log.LogInfo("[RelationshipSystem] NPC relationship system initialized");
                LogToFile("[Plugin:InitializeSystems] RelationshipSystem created");
            }
            catch (Exception ex)
            {
                Log.LogError($"[RelationshipSystem] Failed to initialize: {ex.Message}");
                LogToFile($"[Plugin:InitializeSystems] RelationshipSystem ERROR: {ex.Message}");
            }

            try
            {
                ChatBubbleManager = new ChatBubbleManager(duration: 5f, maxDistance: 25f);
                Log.LogInfo("[ChatBubbleManager] 3D chat bubble system initialized");
                LogToFile("[Plugin:InitializeSystems] ChatBubbleManager created");
            }
            catch (Exception ex)
            {
                Log.LogError($"[ChatBubbleManager] Failed to initialize: {ex.Message}");
                LogToFile($"[Plugin:InitializeSystems] ChatBubbleManager ERROR: {ex.Message}");
            }

            try
            {
                NPCToNPCDialogueManager = new NPCToNPCDialogueManager(
                    LLMClient, RelationshipSystem, MemoryManager, ChatBubbleManager, NPCRegistry);
                Log.LogInfo("[NPCToNPCDialogueManager] NPC-to-NPC conversation system initialized");
                LogToFile("[Plugin:InitializeSystems] NPCToNPCDialogueManager created");
            }
            catch (Exception ex)
            {
                Log.LogError($"[NPCToNPCDialogueManager] Failed to initialize: {ex.Message}");
                LogToFile($"[Plugin:InitializeSystems] NPCToNPCDialogueManager ERROR: {ex.Message}");
            }

            try
            {
                SocialHubWindow = new SocialHubWindow(
                    DialogueManager, NPCToNPCDialogueManager, RelationshipSystem, ChatBubbleManager, SocialHubHotkey);
                Log.LogInfo($"[SocialHubWindow] Social hub UI initialized - Press {SocialHubHotkey.Value.MainKey} to open");
                LogToFile("[Plugin:InitializeSystems] SocialHubWindow created");
            }
            catch (Exception ex)
            {
                Log.LogError($"[SocialHubWindow] Failed to initialize: {ex.Message}");
                LogToFile($"[Plugin:InitializeSystems] SocialHubWindow ERROR: {ex.Message}");
            }

            try
            {
                PromptDebugMonitorWindow = new PromptDebugMonitorWindow(PromptMonitorVisible, PromptMonitorHotkey);
                Log.LogInfo($"[PromptDebugMonitor] Initialized - Press {PromptMonitorHotkey.Value.MainKey} to toggle");
                LogToFile("[Plugin:InitializeSystems] PromptDebugMonitorWindow created");
            }
            catch (Exception ex)
            {
                Log.LogError($"[PromptDebugMonitor] Failed to initialize: {ex.Message}");
                LogToFile($"[Plugin:InitializeSystems] PromptDebugMonitorWindow ERROR: {ex.Message}");
            }

            // Initialize menu integration
            try
            {
                var menuGO = new GameObject("LLM_NPCs_MenuIntegration");
                menuGO.transform.SetParent(transform);
                MenuIntegration = menuGO.AddComponent<MenuIntegration>();
                MenuIntegration.Initialize(
                    LLMClient,
                    Model,
                    ApiKey,
                    Temperature,
                    DecisionInterval,
                    EnableFullAutonomy,
                    EnableMod,
                    LogDecisions,
                    ModelNpcDecisions,
                    ModelPlayerChat,
                    ModelNpcToNpcChat,
                    ModelAdviser
                );
                Log.LogInfo("[MenuIntegration] Menu system initialized - Look for MOD CONFIG button in main menu");
                LogToFile("[Plugin:InitializeSystems] MenuIntegration created");
            }
            catch (Exception ex)
            {
                Log.LogError($"[MenuIntegration] Failed to initialize: {ex.Message}");
                LogToFile($"[Plugin:InitializeSystems] MenuIntegration ERROR: {ex.Message}");
            }
            LogToFile("[Plugin:InitializeSystems] Complete");
        }

        private IEnumerator MainLoop()
        {
            LogToFile("[Plugin:MainLoop] Starting, waiting 5 seconds for game init");
            yield return new WaitForSeconds(5f); // Wait for game to initialize
            LogToFile("[Plugin:MainLoop] Game init wait complete, entering main loop");

            while (true)
            {
                if (EnableMod.Value)
                {
                    try
                    {
                        ProcessNPCs();
                    }
                    catch (Exception ex)
                    {
                        Log.LogError($"Error in main loop: {ex}");
                        LogToFile($"[Plugin:MainLoop] ERROR: {ex}");
                    }
                }
                else
                {
                    LogToFile("[Plugin:MainLoop] Mod disabled, skipping NPC processing");
                }

                // Poll every 1 second instead of waiting the entire DecisionInterval.
                // This allows the random jitter in NPCRegistry to organically spread out requests.
                // REALTIME wait: WaitForSeconds freezes when the game auto-pauses
                // (events pause timeScale -> the whole mod stalled, telemetry
                // froze at 02:21 live). Realtime keeps the brain ticking through
                // pauses; orders simply execute when the game resumes.
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        private void ProcessNPCs()
        {
            // LOAD GATE — the game must report its load pipeline FULLY complete
            // before the mod touches anything. Settler objects exist mid-load,
            // and acting on them then (designations, blueprints, forced goals)
            // wedges the loader — the loading-screen hang that killed Libury and
            // reproduced on a fresh map. Fail-closed: not ready -> do nothing.
            if (!GameBridge.IsWorldReady())
                return;

            // Find all validated settlers via GameBridge (per-second; not logged
            // to avoid flooding the log window and burying builder telemetry).
            var settlers = GameBridge.GetValidatedSettlers();

            // SCHEDULE SANITIZER — FIRST, at per-second cadence (native crash
            // 2026-07-12: saves poisoned with RoleJob Work-hours by the old
            // mapper spin the goal loop on CURRENT_HOUR None from LOAD-IN and
            // crash within minutes; ColonyBuilder's 12s tick loses that race).
            // ApplyAll is internally once-per-settler-per-session — cheap.
            if (settlers.Count > 0) ScheduleRouter.ApplyAll(settlers);

            TryStartColonyInfluence(settlers);

            // Strategic Model: deterministic "villagers build their own village"
            // actuator. Censuses infrastructure gaps (storage, beds) and fires the
            // proven placers. Gated internally on the full-autonomy master switch,
            // so it only runs when the player has handed the colony to the AI.
            if (ColonyBuilder.ShouldTick())
            {
                ColonyBuilder.Tick(settlers);
            }

            // P3: execute queued player orders from the dashboard ai_orders queue.
            if (OrderExecutor.ShouldPoll() && MemoryManager != null)
            {
                OrderExecutor.PollAndExecute(settlers, MemoryManager.ActiveSaveId);
            }

            // B3 groundwork: dump the game's construction API surface once the
            // save is loaded so placement code can target real signatures.
            if (settlers.Count > 0 && MemoryManager != null)
            {
                GameApiScanner.ScanAndReport(MemoryManager.ActiveSaveId);
            }
            
            foreach (var settler in settlers)
            {
                if (settler == null || settler.gameObject == null)
                    continue;

                // Check if this settler should get an LLM decision
                if (NPCRegistry.ShouldProcess(settler, DecisionInterval.Value))
                {
                    var id = GameBridge.GetSettlerId(settler.gameObject) ?? settler.gameObject.GetInstanceID().ToString();
                    if (!_activeDecisionRequests.Add(id))
                    {
                        LogToFile($"[Plugin:ProcessNPCs] Decision already in flight for settler {id}, skipping duplicate coroutine.");
                        continue;
                    }

                    LogToFile($"[Plugin:ProcessNPCs] Starting coroutine for settler {id} ({settler.Name})");
                    StartCoroutine(ProcessSettlerTracked(settler, id));
                }
            }
        }

        /// <summary>
        /// Force ONE settler through a REAL Player2 decision cycle right now,
        /// bypassing the decision-interval gate. Player2 genuinely chooses the
        /// action (build_stockpile is one of its tools) and the settler acts on
        /// it. Used to demonstrate/verify the autonomous "villager builds because
        /// Player2 told them" loop on demand instead of waiting on the timer.
        /// </summary>
        public static void ForceProcessSettler(Settler settler)
        {
            if (Instance == null || settler == null || settler.gameObject == null)
                return;
            var id = GameBridge.GetSettlerId(settler.gameObject) ?? settler.gameObject.GetInstanceID().ToString();
            if (!Instance._activeDecisionRequests.Add(id))
            {
                LogToFile($"[Plugin:ForceProcessSettler] Decision already in flight for {id}, skipping.");
                return;
            }
            LogToFile($"[Plugin:ForceProcessSettler] Forcing a real Player2 decision for {id} ({settler.Name})");
            Instance.StartCoroutine(Instance.ProcessSettlerTracked(settler, id));
        }

        private void TryStartColonyInfluence(List<Settler> settlers)
        {
            if (settlers == null || settlers.Count == 0 || MemoryManager == null)
                return;

            if (_colonyInfluenceInFlight)
                return;

            var now = DateTime.UtcNow;
            if ((now - _lastColonyInfluenceUtc).TotalSeconds < ColonyInfluenceIntervalSeconds)
                return;

            _lastColonyInfluenceUtc = now;
            _colonyInfluenceInFlight = true;
            StartCoroutine(ProcessColonyInfluence(settlers.ToList()));
        }

        private IEnumerator ProcessColonyInfluence(List<Settler> settlers)
        {
            try
            {
                var contexts = new List<NPCContext>();
                foreach (var settler in settlers)
                {
                    if (settler == null || settler.gameObject == null)
                        continue;

                    var context = NPCContextExtractor.Extract(settler);
                    if (context != null)
                        contexts.Add(context);
                }

                if (contexts.Count == 0)
                {
                    LogToFile("[Plugin:ProcessColonyInfluence] No valid contexts available.");
                    yield break;
                }

                var engine = ColonyInfluenceEngine.Instance;
                var state = engine.ComputeColonyState(contexts);
                var recommendation = engine.GenerateRecommendations(state);
                string narrative = recommendation.Description;

                var narrativeTask = engine.AskLLMForNarrativeAsync(LLMClient, state, recommendation);
                while (!narrativeTask.IsCompleted)
                    yield return null;

                if (narrativeTask.Exception != null)
                {
                    LogToFile($"[Plugin:ProcessColonyInfluence] Narrative task failed: {narrativeTask.Exception.GetBaseException().Message}");
                }
                else if (!string.IsNullOrWhiteSpace(narrativeTask.Result))
                {
                    narrative = narrativeTask.Result;
                }

                MemoryManager.RecordColonyEvent(state, recommendation, narrative);
                LogToFile($"[Plugin:ProcessColonyInfluence] Recorded colony recommendation: {recommendation.Type} | {recommendation.Description} | {narrative}");
            }
            finally
            {
                _colonyInfluenceInFlight = false;
            }
        }

        private IEnumerator ProcessSettlerTracked(Settler settler, string npcId)
        {
            try
            {
                yield return ProcessSettler(settler);
            }
            finally
            {
                _activeDecisionRequests.Remove(npcId);
                LogToFile($"[Plugin:ProcessSettlerTracked] Cleared in-flight decision for {npcId}");
            }
        }

        private IEnumerator ProcessSettler(Settler settler)
        {
            if (settler == null || settler.gameObject == null)
            {
                LogToFile("[Plugin:ProcessSettler] Settler reference invalid, skipping");
                yield break;
            }

            var npcId = GameBridge.GetSettlerId(settler.gameObject) ?? settler.gameObject.GetInstanceID().ToString();
            LogToFile($"[Plugin:ProcessSettler] Processing settler {npcId} ({settler.Name})");
            
            // Extract context
            var context = NPCContextExtractor.Extract(settler);
            if (context == null)
            {
                LogToFile($"[Plugin:ProcessSettler] Context extraction failed for {npcId}");
                yield break;
            }

            // Early Out: Do not process LLM decisions while the NPC is sleeping.
            if (context.States.Contains("isSleeping"))
            {
                // We do not record a decision so their timer simply drifts until they wake up.
                LogToFile($"[Plugin:ProcessSettler] {npcId} is sleeping. Skipping LLM request.");
                yield break;
            }
            
            LogToFile($"[Plugin:ProcessSettler] Context extracted for {npcId}");
            MemoryManager?.SaveCharacterSheet(context);

            // Construct RAG query from current context
            var lowestNeed = "none";
            if (context.Needs != null)
            {
                var needsMap = new Dictionary<string, float>
                {
                    {"food", context.Needs.Food}, {"water", context.Needs.Water},
                    {"rest", context.Needs.Rest}, {"recreation", context.Needs.Recreation}
                };
                lowestNeed = needsMap.OrderBy(x => x.Value).First().Key;
            }
            var ragQuery = $"I am {npcId}. Activity: {context.CurrentActivity?.Type ?? "idle"}. Lowest need: {lowestNeed}.";
            var topSkillStats = BuildTopSkillStats(context);
            if (!string.IsNullOrWhiteSpace(topSkillStats))
            {
                var existingBinding = MemoryManager?.GetNpcBinding(npcId);
                if (!string.IsNullOrWhiteSpace(existingBinding))
                {
                    MemoryManager.SetNpcBinding(npcId, existingBinding, string.Join(", ", context.Traits ?? new List<string>()), topSkillStats, context.Name, context.Profession);
                }
            }

            var memoryTask = MemoryManager?.GetContextForPromptAsync(npcId, context.Profession, ragQuery);
            if (memoryTask != null)
            {
                while (!memoryTask.IsCompleted) yield return null;
                context.MemoryContext = memoryTask.Result;
            }
            else
            {
                context.MemoryContext = MemoryManager?.GetContextForPrompt(npcId, context.Profession);
            }
            LogToFile($"[Plugin:ProcessSettler] Memory context retrieved for {npcId}");

            // Get decision from LLM
            LogToFile($"[Plugin:ProcessSettler] Requesting decision from DecisionEngine for {npcId}");
            var decisionTask = DecisionEngine.GetDecisionAsync(context);
            
            while (!decisionTask.IsCompleted)
            {
                yield return null;
            }

            var decision = decisionTask.Result;
            LogToFile($"[Plugin:ProcessSettler] Decision received for {npcId}: {decision?.Action}");
            
            if (decision != null && decision.Success)
            {
                // Action Gateway: Check Autonomy Manager
                // For now, assume all settlers belong to Player Faction (Id: 1)
                int factionId = 1;
                
                if (AutonomyManager.Instance.IsFactionAutonomous(factionId))
                {
                    LogToFile($"[Plugin:ProcessSettler] Executing decision: {decision.Action}");
                    // Execute the decision
                    DecisionExecutor.Execute(settler, decision);
                    
                    // Show thought bubble with the NPC's reasoning (short duration)
                    var transform = settler.GetComponent<UnityEngine.Transform>();
                    var worldPos = transform != null ? transform.position : UnityEngine.Vector3.zero;
                    ChatBubbleManager?.ShowBubble(npcId, settler.Name, 
                        $"💭 {decision.Reasoning}", 
                        worldPos,
                        isMarried: false, 
                        relationshipIcon: null,
                        duration: 3.5f);
                }
                else
                {
                    LogToFile($"[Plugin:ProcessSettler] Autonomy Disabled. Discarding physical action: {decision.Action}");
                    
                    // Route to the complaint mechanic
                    var transform = settler.GetComponent<UnityEngine.Transform>();
                    var worldPos = transform != null ? transform.position : UnityEngine.Vector3.zero;
                    
                    string complaintText = string.IsNullOrWhiteSpace(decision.DialogueComplaint) 
                        ? $"💭 I wish I could {decision.Action}, but I must wait..." 
                        : decision.DialogueComplaint;

                    ChatBubbleManager?.ShowBubble(npcId, settler.Name, 
                        complaintText, 
                        worldPos,
                        isMarried: false, 
                        relationshipIcon: null,
                        duration: 5.0f);
                }

                if (LogDecisions.Value)
                {
                    Log.LogInfo($"[{settler.Name}] {decision.Action}: {decision.Reasoning}");
                }

                // Record decision in memory system (hierarchical SQLite)
                var importance = CalculateDecisionImportance(decision, context);
                MemoryManager?.RecordEvent(npcId, "decision",
                    $"Decided to {decision.Action}: {decision.Reasoning}",
                    importance,
                    new Dictionary<string, object> {
                        { "action", decision.Action },
                        { "target", (decision.Parameters != null && decision.Parameters.ContainsKey("target")) ? decision.Parameters["target"] : null },
                        { "location", (decision.Parameters != null && decision.Parameters.ContainsKey("location")) ? decision.Parameters["location"] : null }
                    });
                
                // Record significant state changes
                RecordStateChanges(npcId, context);
                
                // Legacy registry for compatibility + timer reset
                NPCRegistry.RecordDecision(settler, decision, DecisionInterval.Value);
                LogToFile($"[Plugin:ProcessSettler] Decision processing complete for {npcId}");
            }
            else
            {
                LogToFile($"[Plugin:ProcessSettler] Decision failed or null for {npcId}");
                
                // Even on failure, reset the timer so we don't spam the API infinitely
                var idStr = GameBridge.GetSettlerId(settler.gameObject) ?? settler.gameObject.GetInstanceID().ToString();
                NPCRegistry.RecordDecision(settler, new LLMDecision { Action = "Failed", Reasoning = "Request timed out or failed to parse." }, DecisionInterval.Value);
            }
        }


        private int CalculateDecisionImportance(LLMDecision decision, NPCContext context)
        {
            LogToFile($"[Plugin:CalculateDecisionImportance] Action: {decision.Action}");
            // Survival-critical decisions are most important
            if (decision.Action == "flee" || decision.Action == "defend" ||
                decision.Action == "seek_shelter" || decision.Action == "rest_when_critical")
            {
                LogToFile("[Plugin:CalculateDecisionImportance] Returning importance 10 (survival)");
                return 10;
            }
            
            // Health-related decisions
            if (context.Health?.Overall < 50)
            {
                LogToFile("[Plugin:CalculateDecisionImportance] Returning importance 8 (health)");
                return 8;
            }
            
            // Need-related decisions when critical
            var criticalNeeds = context.Needs?.GetCriticalNeeds();
            if (criticalNeeds?.Count > 0)
            {
                LogToFile("[Plugin:CalculateDecisionImportance] Returning importance 7 (critical needs)");
                return 7;
            }
            
            // Social decisions
            if (decision.Action == "socialize")
            {
                LogToFile("[Plugin:CalculateDecisionImportance] Returning importance 5 (social)");
                return 5;
            }
            
            LogToFile("[Plugin:CalculateDecisionImportance] Returning importance 3 (default)");
            // Default
            return 3;
        }

        private static string BuildTopSkillStats(NPCContext context)
        {
            if (context?.Skills == null || context.Skills.Count == 0) return null;
            return string.Join(", ", context.Skills
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Take(6)
                .Select(kv => $"{kv.Key}:{kv.Value}"));
        }

        private void RecordStateChanges(string npcId, NPCContext context)
        {
            LogToFile($"[Plugin:RecordStateChanges] Recording for {npcId}");
            // Record critical health changes
            if (context.Health?.Overall < 30)
            {
                MemoryManager?.RecordEvent(npcId, "health",
                    $"Health critical at {context.Health.Overall}%", 9);
                LogToFile($"[Plugin:RecordStateChanges] Recorded health critical for {npcId}");
            }

            // Record mood changes
            if (context.Mood == "very_unhappy")
            {
                MemoryManager?.RecordEvent(npcId, "mood",
                    "Experiencing severe distress", 8);
                LogToFile($"[Plugin:RecordStateChanges] Recorded mood distress for {npcId}");
            }

            // Record danger
            if (context.Environment?.HostilesNearby > 0)
            {
                MemoryManager?.RecordEvent(npcId, "danger",
                    $"Detected {context.Environment.HostilesNearby} enemies nearby", 10);
                LogToFile($"[Plugin:RecordStateChanges] Recorded danger for {npcId}");
            }
            LogToFile($"[Plugin:RecordStateChanges] Complete for {npcId}");
        }

        private void LogAssemblyFingerprint()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var location = asm.Location;
                var version = asm.GetName().Version?.ToString() ?? "unknown";
                var writeUtc = !string.IsNullOrWhiteSpace(location) && File.Exists(location)
                    ? File.GetLastWriteTimeUtc(location).ToString("O")
                    : "unknown";

                Log.LogInfo($"[Plugin] Assembly fingerprint: location='{location}', version='{version}', fileLastWriteUtc='{writeUtc}'");
                LogToFile($"[Plugin:AssemblyFingerprint] Location={location}");
                LogToFile($"[Plugin:AssemblyFingerprint] Version={version}");
                LogToFile($"[Plugin:AssemblyFingerprint] FileLastWriteUtc={writeUtc}");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Plugin] Failed to log assembly fingerprint: {ex.Message}");
                LogToFile($"[Plugin:AssemblyFingerprint] ERROR: {ex}");
            }
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.goingmedieval.llm_npcs";
        public const string PLUGIN_NAME = "LLM NPCs";
        public const string PLUGIN_VERSION = "1.0.0";
    }
}
