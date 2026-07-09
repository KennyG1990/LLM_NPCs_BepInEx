using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Integrates LLM NPC settings into Going Medieval's Options menu.
    /// Adds a "LLM NPCs" button to the Options menu that opens mod settings.
    /// </summary>
    public class MenuIntegration : MonoBehaviour
    {
        private LLMClient _llmClient;
        private ConfigEntry<string> _modelConfig;
        private ConfigEntry<string> _apiKeyConfig;
        private ConfigEntry<float> _temperatureConfig;
        private ConfigEntry<float> _decisionIntervalConfig;
        private ConfigEntry<bool> _enableFullAutonomyConfig;
        private ConfigEntry<bool> _enableModConfig;
        private ConfigEntry<bool> _logDecisionsConfig;

        // Menu state
        private GameObject _modMenuPanel;
        private GameObject _settingsPanel;
        private bool _menuOpen = false;
        private List<OpenRouterModel> _models = new List<OpenRouterModel>();
        private bool _isLoadingModels = false;
        private Vector2 _modelScrollPosition;
        private string _modelSearchFilter = "";
        private bool _showFreeOnly = true;
        private string _modelFetchStatus = "";
        private bool _hasFetchedModelsThisSession = false;
        private GUIStyle _safeBoldLabelStyle;

        // Button injection tracking
        private GameObject _optionsMenuButton = null;

        public void Initialize(
            LLMClient llmClient,
            ConfigEntry<string> modelConfig,
            ConfigEntry<string> apiKeyConfig,
            ConfigEntry<float> temperatureConfig,
            ConfigEntry<float> decisionIntervalConfig,
            ConfigEntry<bool> enableFullAutonomyConfig,
            ConfigEntry<bool> enableModConfig,
            ConfigEntry<bool> logDecisionsConfig)
        {
            _llmClient = llmClient;
            _modelConfig = modelConfig;
            _apiKeyConfig = apiKeyConfig;
            _temperatureConfig = temperatureConfig;
            _decisionIntervalConfig = decisionIntervalConfig;
            _enableFullAutonomyConfig = enableFullAutonomyConfig;
            _enableModConfig = enableModConfig;
            _logDecisionsConfig = logDecisionsConfig;

            LLMNPCsPlugin.Log.LogInfo("[MenuIntegration] Initialized");
            LLMNPCsPlugin.LogToFile("[MenuIntegration] Initialized");
        }

        // IMGUI overlay state for options menu button
        private bool _optionsMenuVisible = false;
        private Rect _imguiButtonRect = new Rect(0, 0, 160, 36);

        public void Update()
        {
            // HOME key toggles our built-in mod settings panel
            if (Input.GetKeyDown(KeyCode.Home))
            {
                ToggleModMenu();
            }

            // Detect if the game's Options/Pause menu is open by checking for active canvases
            _optionsMenuVisible = IsOptionsMenuOpen();

            // Check for Escape to close mod menu
            if (_menuOpen && Input.GetKeyDown(KeyCode.Escape))
            {
                CloseModMenu();
            }
        }

        private bool IsOptionsMenuOpen()
        {
            try
            {
                var canvases = FindObjectsOfType<Canvas>(true);
                foreach (var canvas in canvases)
                {
                    if (!canvas.gameObject.activeInHierarchy) continue;
                    if (canvas.name.Contains("Options") || canvas.name.Contains("Settings") ||
                        canvas.name.Contains("Option") || canvas.name.Contains("Pause") ||
                        canvas.name.Contains("Menu"))
                    {
                        // Verify it has buttons (not just any menu-named canvas)
                        var buttons = canvas.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                        if (buttons.Length >= 3)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// IMGUI rendering for the options menu overlay button.
        /// Called by Unity's OnGUI system.
        /// </summary>
        private int _lastDrawFrame = -1;
        private EventType _lastDrawEvent;

        public void DrawOptionsOverlay()
        {
            // MOD TOOLBAR (Ken: windows had NO obvious opener — the old button
            // only rendered while the game's Options menu was open). Always
            // visible, top-right, one labeled button per mod window. Vanilla
            // tones: near-black panel, parchment text (full GM skin = #30 ph2).
            // Called from BOTH MenuIntegration.OnGUI and Plugin.OnGUI (belt +
            // suspenders — one path proved dead in-game); guard double-draw.
            if (_menuOpen) return;
            if (Time.frameCount == _lastDrawFrame && Event.current.type == _lastDrawEvent) return;
            _lastDrawFrame = Time.frameCount;
            _lastDrawEvent = Event.current.type;
            GUI.depth = -1000;

            var panelBg = new Color(0.07f, 0.07f, 0.09f, 0.92f);
            var parchment = new Color(0.87f, 0.82f, 0.70f);
            float w = 170f, h = 26f, pad = 4f, x = Screen.width - w - 12f, y = 12f;

            var old = GUI.backgroundColor;
            var oldC = GUI.contentColor;
            GUI.backgroundColor = panelBg;
            GUI.Box(new Rect(x - pad, y - pad, w + pad * 2, (h + pad) * 4 + pad * 2), "");
            GUI.contentColor = parchment;

            if (GUI.Button(new Rect(x, y, w, h), "LLM Settings")) ToggleModMenu();
            y += h + pad;
            if (GUI.Button(new Rect(x, y, w, h), "Talk to Settler"))
                LLMNPCsPlugin.Instance?.DialogueManager?.OpenDialogueWithSelected();
            y += h + pad;
            if (GUI.Button(new Rect(x, y, w, h), "Social Hub"))
                LLMNPCsPlugin.Instance?.SocialHubWindow?.Toggle();
            y += h + pad;
            if (GUI.Button(new Rect(x, y, w, h), "Prompt Log"))
                LLMNPCsPlugin.Instance?.PromptDebugMonitorWindow?.Toggle();

            GUI.backgroundColor = old;
            GUI.contentColor = oldC;
        }

        private void TryInjectButtonIntoOptionsMenu()
        {
            try
            {
                // Find the Options menu - look for canvas with "Options" or "Settings" in name
                var canvases = FindObjectsOfType<Canvas>(true);
                Canvas optionsCanvas = null;

                foreach (var canvas in canvases)
                {
                    if (canvas.name.Contains("Options") || 
                        canvas.name.Contains("Settings") ||
                        canvas.name.Contains("Option"))
                    {
                        optionsCanvas = canvas;
                        LLMNPCsPlugin.LogToFile($"[MenuIntegration] Found canvas: {canvas.name}");
                        break;
                    }
                }

                if (optionsCanvas == null)
                {
                    // Check for active panels that might be the options menu
                    foreach (var canvas in canvases)
                    {
                        if (canvas.gameObject.activeInHierarchy && canvas.GetComponentInChildren<Button>(true) != null)
                        {
                            // Check if this has buttons like "Graphics", "Audio", "Game"
                            var buttons = canvas.GetComponentsInChildren<Button>(true);
                            bool hasGameButton = buttons.Any(b => {
                                var text = b.GetComponentInChildren<Text>(true);
                                return text != null && (text.text.Contains("Game") || text.text.Contains("Graphics"));
                            });

                            if (hasGameButton)
                            {
                                optionsCanvas = canvas;
                                LLMNPCsPlugin.LogToFile($"[MenuIntegration] Found options canvas by buttons: {canvas.name}");
                                break;
                            }
                        }
                    }
                }

                if (optionsCanvas != null && optionsCanvas.gameObject.activeInHierarchy)
                {
                    // Find the button container
                    Transform buttonContainer = FindButtonContainer(optionsCanvas.transform);
                    
                    if (buttonContainer != null)
                    {
                        LLMNPCsPlugin.LogToFile($"[MenuIntegration] Found button container: {buttonContainer.name}");
                        InjectButton(buttonContainer);
                    }
                    else
                    {
                        LLMNPCsPlugin.LogToFile("[MenuIntegration] Button container not found");
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[MenuIntegration] Error in TryInject: {ex.Message}");
            }
        }

        private Transform FindButtonContainer(Transform canvasTransform)
        {
            // Look for common container names
            string[] containerNames = { "Buttons", "ButtonContainer", "MenuButtons", "Content", "Panel" };
            
            foreach (var name in containerNames)
            {
                var container = canvasTransform.Find(name);
                if (container != null)
                {
                    // Verify it has multiple buttons
                    var buttons = container.GetComponentsInChildren<Button>(true);
                    if (buttons.Length >= 3)
                    {
                        return container;
                    }
                }
            }

            // Search all children for one with multiple buttons
            foreach (Transform child in canvasTransform)
            {
                var buttons = child.GetComponentsInChildren<Button>(true);
                if (buttons.Length >= 4) // Options menu has Graphics, Audio, Game, etc.
                {
                    return child;
                }

                // Recursively search
                var result = FindButtonContainer(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private void InjectButton(Transform container)
        {
            try
            {
                // Find the "Game" or "Graphics" button to clone
                var existingButtons = container.GetComponentsInChildren<Button>(true);
                Button templateButton = null;

                foreach (var btn in existingButtons)
                {
                    var text = btn.GetComponentInChildren<Text>(true);
                    if (text != null && (text.text == "Game" || text.text == "Graphics" || text.text == "Audio"))
                    {
                        templateButton = btn;
                        break;
                    }
                }

                if (templateButton == null)
                {
                    templateButton = existingButtons.FirstOrDefault();
                }

                if (templateButton == null)
                {
                    LLMNPCsPlugin.LogToFile("[MenuIntegration] No template button found");
                    return;
                }

                // Clone the button
                _optionsMenuButton = Instantiate(templateButton.gameObject, container);
                _optionsMenuButton.name = "LLM_NPCs_OptionsButton";
                _optionsMenuButton.SetActive(true);

                // Update text
                var buttonText = _optionsMenuButton.GetComponentInChildren<Text>(true);
                if (buttonText != null)
                {
                    buttonText.text = "LLM NPCs";
                }

                // Update button click handler
                var button = _optionsMenuButton.GetComponent<Button>();
                if (button != null)
                {
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(() => OpenModMenu());
                }

                // Position after Game button or at the end
                int gameIndex = -1;
                for (int i = 0; i < container.childCount; i++)
                {
                    var child = container.GetChild(i);
                    var txt = child.GetComponentInChildren<Text>(true);
                    if (txt != null && txt.text == "Game")
                    {
                        gameIndex = i;
                        break;
                    }
                }

                if (gameIndex >= 0)
                {
                    _optionsMenuButton.transform.SetSiblingIndex(gameIndex + 1);
                }

                LLMNPCsPlugin.Log.LogInfo("[MenuIntegration] Successfully injected LLM NPCs button into Options menu");
                LLMNPCsPlugin.LogToFile("[MenuIntegration] Successfully injected LLM NPCs button into Options menu");
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[MenuIntegration] Error injecting button: {ex}");
            }
        }

        public void ToggleModMenu()
        {
            _menuOpen = !_menuOpen;
            LLMNPCsPlugin.LogToFile($"[MenuIntegration] Toggled mod menu: {_menuOpen}");

            if (_menuOpen && !_hasFetchedModelsThisSession && !_isLoadingModels)
            {
                FetchModelsFromUI();
                _hasFetchedModelsThisSession = true;
            }
        }

        private void OpenModMenu()
        {
            if (_modMenuPanel != null)
            {
                Destroy(_modMenuPanel);
            }

            // Create main menu panel
            _modMenuPanel = new GameObject("LLM_NPCs_ModMenu");
            _modMenuPanel.transform.SetParent(transform, false);

            // Add canvas renderer
            var canvas = _modMenuPanel.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100; // On top of everything

            var scaler = _modMenuPanel.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            _modMenuPanel.AddComponent<GraphicRaycaster>();

            // Create background
            CreateMenuBackground();

            // Create settings panel
            CreateSettingsPanel();

            // Create navigation buttons
            CreateNavigationButtons();

            _menuOpen = true;

            LLMNPCsPlugin.Log.LogInfo("[MenuIntegration] Mod menu opened");
            LLMNPCsPlugin.LogToFile("[MenuIntegration] Mod menu opened");
        }

        private void CreateMenuBackground()
        {
            var bgGO = new GameObject("Background");
            bgGO.transform.SetParent(_modMenuPanel.transform, false);

            var rect = bgGO.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var image = bgGO.AddComponent<Image>();
            image.color = new Color(0, 0, 0, 0.85f);
        }

        private void CreateSettingsPanel()
        {
            _settingsPanel = new GameObject("SettingsPanel");
            _settingsPanel.transform.SetParent(_modMenuPanel.transform, false);

            var rect = _settingsPanel.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(800, 600);
            rect.anchoredPosition = Vector2.zero;

            // Title
            var titleGO = new GameObject("Title");
            titleGO.transform.SetParent(_settingsPanel.transform, false);

            var titleRect = titleGO.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(1, 1);
            titleRect.sizeDelta = new Vector2(0, 60);
            titleRect.anchoredPosition = new Vector2(0, -30);

            var titleText = titleGO.AddComponent<Text>();
            titleText.text = "LLM NPCs Settings";
            titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleText.fontSize = 32;
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = Color.white;

            // Scrollable content area
            var scrollGO = new GameObject("ScrollView");
            scrollGO.transform.SetParent(_settingsPanel.transform, false);

            var scrollRect = scrollGO.AddComponent<RectTransform>();
            scrollRect.anchorMin = new Vector2(0, 0);
            scrollRect.anchorMax = new Vector2(1, 1);
            scrollRect.offsetMin = new Vector2(20, 80);
            scrollRect.offsetMax = new Vector2(-20, -70);

            var scrollRectComp = scrollGO.AddComponent<ScrollRect>();

            // Viewport
            var viewportGO = new GameObject("Viewport");
            viewportGO.transform.SetParent(scrollGO.transform, false);

            var viewportRect = viewportGO.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.sizeDelta = Vector2.zero;

            viewportGO.AddComponent<Mask>();
            var viewportImage = viewportGO.AddComponent<Image>();
            viewportImage.color = new Color(0.1f, 0.1f, 0.1f, 0.5f);

            scrollRectComp.viewport = viewportRect;

            // Content
            var contentGO = new GameObject("Content");
            contentGO.transform.SetParent(viewportGO.transform, false);

            var contentRect = contentGO.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.sizeDelta = new Vector2(0, 800); // Will expand

            var contentLayout = contentGO.AddComponent<VerticalLayoutGroup>();
            contentLayout.padding = new RectOffset(10, 10, 10, 10);
            contentLayout.spacing = 10;
            contentLayout.childAlignment = TextAnchor.UpperCenter;
            contentLayout.childControlHeight = false;
            contentLayout.childForceExpandHeight = false;

            var contentFitter = contentGO.AddComponent<ContentSizeFitter>();
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRectComp.content = contentRect;
            scrollRectComp.vertical = true;
            scrollRectComp.horizontal = false;

            // Add settings
            AddSettingsToContent(contentGO.transform);
        }

        private void AddSettingsToContent(Transform content)
        {
            // Enable/Disable mod
            AddToggleSetting(content, "Enable Mod", _enableModConfig.Value, (value) => {
                _enableModConfig.Value = value;
            });

            // Enable/Disable Autonomy
            AddToggleSetting(content, "Enable Full AI Autonomy (Allow physical actions)", _enableFullAutonomyConfig.Value, (value) => {
                _enableFullAutonomyConfig.Value = value;
            });

            // API Key
            AddTextSetting(content, "API Key", _apiKeyConfig.Value, (value) => {
                _apiKeyConfig.Value = value;
            }, isPassword: true);

            // Model Selection
            AddModelSelectionSetting(content);

            // Temperature
            AddSliderSetting(content, "Temperature", _temperatureConfig.Value, 0f, 1f, (value) => {
                _temperatureConfig.Value = value;
            });

            // Decision Interval
            AddSliderSetting(content, "Decision Interval (seconds)", _decisionIntervalConfig.Value, 5f, 60f, (value) => {
                _decisionIntervalConfig.Value = value;
            });

            // Log Decisions
            AddToggleSetting(content, "Log Decisions", _logDecisionsConfig.Value, (value) => {
                _logDecisionsConfig.Value = value;
            });

            // Save Config Button
            AddButton(content, "Save & Close", () => {
                SaveConfig();
                CloseModMenu();
            });
        }

        private void AddSettingLabel(Transform parent, string label)
        {
            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(parent, false);

            var labelRect = labelGO.AddComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(700, 30);

            var text = labelGO.AddComponent<Text>();
            text.text = label;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 18;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;
        }

        private void AddToggleSetting(Transform parent, string label, bool value, Action<bool> onChange)
        {
            AddSettingLabel(parent, label);

            var toggleGO = new GameObject("Toggle");
            toggleGO.transform.SetParent(parent, false);

            var toggleRect = toggleGO.AddComponent<RectTransform>();
            toggleRect.sizeDelta = new Vector2(700, 40);

            var toggle = toggleGO.AddComponent<Toggle>();
            toggle.isOn = value;
            toggle.onValueChanged.AddListener((v) => onChange(v));

            // Background
            var bgGO = new GameObject("Background");
            bgGO.transform.SetParent(toggleGO.transform, false);

            var bgRect = bgGO.AddComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(1, 0.5f);
            bgRect.anchorMax = new Vector2(1, 0.5f);
            bgRect.sizeDelta = new Vector2(40, 40);
            bgRect.anchoredPosition = new Vector2(-20, 0);

            var bgImage = bgGO.AddComponent<Image>();
            bgImage.color = new Color(0.2f, 0.2f, 0.2f);

            // Checkmark
            var checkGO = new GameObject("Checkmark");
            checkGO.transform.SetParent(bgGO.transform, false);

            var checkRect = checkGO.AddComponent<RectTransform>();
            checkRect.anchorMin = Vector2.zero;
            checkRect.anchorMax = Vector2.one;
            checkRect.offsetMin = new Vector2(5, 5);
            checkRect.offsetMax = new Vector2(-5, -5);

            var checkImage = checkGO.AddComponent<Image>();
            checkImage.color = new Color(0.2f, 0.8f, 0.2f);

            toggle.targetGraphic = bgImage;
            toggle.graphic = checkImage;
        }

        private void AddTextSetting(Transform parent, string label, string value, Action<string> onChange, bool isPassword = false)
        {
            AddSettingLabel(parent, label);

            var inputGO = new GameObject("InputField");
            inputGO.transform.SetParent(parent, false);

            var inputRect = inputGO.AddComponent<RectTransform>();
            inputRect.sizeDelta = new Vector2(700, 40);

            var inputField = inputGO.AddComponent<InputField>();
            inputField.text = value;
            inputField.contentType = isPassword ? InputField.ContentType.Password : InputField.ContentType.Standard;
            inputField.onValueChanged.AddListener((v) => onChange(v));

            // Background
            var bgGO = new GameObject("Background");
            bgGO.transform.SetParent(inputGO.transform, false);

            var bgRect = bgGO.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            var bgImage = bgGO.AddComponent<Image>();
            bgImage.color = new Color(0.1f, 0.1f, 0.1f);

            // Text
            var textGO = new GameObject("Text");
            textGO.transform.SetParent(inputGO.transform, false);

            var textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10, 5);
            textRect.offsetMax = new Vector2(-10, -5);

            var text = textGO.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 16;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;

            inputField.textComponent = text;
        }

        private void AddSliderSetting(Transform parent, string label, float value, float min, float max, Action<float> onChange)
        {
            AddSettingLabel(parent, $"{label}: {value:F2}");

            var sliderGO = new GameObject("Slider");
            sliderGO.transform.SetParent(parent, false);

            var sliderRect = sliderGO.AddComponent<RectTransform>();
            sliderRect.sizeDelta = new Vector2(700, 40);

            var slider = sliderGO.AddComponent<Slider>();
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = value;
            slider.onValueChanged.AddListener((v) => {
                onChange(v);
                // Update label
                var txt = parent.GetComponentInChildren<Text>();
                if (txt != null && txt.text.Contains(label))
                {
                    txt.text = $"{label}: {v:F2}";
                }
            });

            // Background
            var bgGO = new GameObject("Background");
            bgGO.transform.SetParent(sliderGO.transform, false);

            var bgRect = bgGO.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(0, 15);
            bgRect.offsetMax = new Vector2(0, -15);

            var bgImage = bgGO.AddComponent<Image>();
            bgImage.color = new Color(0.2f, 0.2f, 0.2f);

            // Fill
            var fillGO = new GameObject("Fill");
            fillGO.transform.SetParent(sliderGO.transform, false);

            var fillRect = fillGO.AddComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = new Vector2(0, 15);
            fillRect.offsetMax = new Vector2(0, -15);

            var fillImage = fillGO.AddComponent<Image>();
            fillImage.color = new Color(0.2f, 0.6f, 0.9f);

            slider.fillRect = fillRect;
            slider.targetGraphic = fillImage;
        }

        private void AddModelSelectionSetting(Transform parent)
        {
            AddSettingLabel(parent, "Model Selection");

            // Current model display
            var currentModelGO = new GameObject("CurrentModel");
            currentModelGO.transform.SetParent(parent, false);

            var currentModelRect = currentModelGO.AddComponent<RectTransform>();
            currentModelRect.sizeDelta = new Vector2(700, 30);

            var currentModelText = currentModelGO.AddComponent<Text>();
            currentModelText.text = $"Current: {_modelConfig.Value}";
            currentModelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            currentModelText.fontSize = 14;
            currentModelText.alignment = TextAnchor.MiddleLeft;
            currentModelText.color = Color.yellow;

            // Free models dropdown
            AddSettingLabel(parent, "Select Free Model:");

            foreach (var model in LLMClient.FreeModels)
            {
                AddButton(parent, model, () => {
                    _modelConfig.Value = model;
                    currentModelText.text = $"Current: {model}";
                    // Model will be updated on next LLM call
                }, small: true);
            }
        }

        private void AddButton(Transform parent, string text, Action onClick, bool small = false)
        {
            var buttonGO = new GameObject($"Button_{text}");
            buttonGO.transform.SetParent(parent, false);

            var buttonRect = buttonGO.AddComponent<RectTransform>();
            buttonRect.sizeDelta = small ? new Vector2(700, 35) : new Vector2(300, 50);

            var button = buttonGO.AddComponent<Button>();
            button.onClick.AddListener(() => onClick());

            // Background
            var bgGO = new GameObject("Background");
            bgGO.transform.SetParent(buttonGO.transform, false);

            var bgRect = bgGO.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            var bgImage = bgGO.AddComponent<Image>();
            bgImage.color = new Color(0.2f, 0.4f, 0.6f);

            button.targetGraphic = bgImage;

            // Text
            var textGO = new GameObject("Text");
            textGO.transform.SetParent(buttonGO.transform, false);

            var textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10, 5);
            textRect.offsetMax = new Vector2(-10, -5);

            var btnText = textGO.AddComponent<Text>();
            btnText.text = text;
            btnText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            btnText.fontSize = small ? 14 : 18;
            btnText.alignment = TextAnchor.MiddleCenter;
            btnText.color = Color.white;
        }

        private void CreateNavigationButtons()
        {
            // Close button at bottom
            var closeGO = new GameObject("CloseButton");
            closeGO.transform.SetParent(_modMenuPanel.transform, false);

            var closeRect = closeGO.AddComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(0.5f, 0);
            closeRect.anchorMax = new Vector2(0.5f, 0);
            closeRect.sizeDelta = new Vector2(200, 50);
            closeRect.anchoredPosition = new Vector2(0, 40);

            var closeButton = closeGO.AddComponent<Button>();
            closeButton.onClick.AddListener(() => CloseModMenu());

            // Background
            var bgGO = new GameObject("Background");
            bgGO.transform.SetParent(closeGO.transform, false);

            var bgRect = bgGO.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            var bgImage = bgGO.AddComponent<Image>();
            bgImage.color = new Color(0.6f, 0.2f, 0.2f);

            closeButton.targetGraphic = bgImage;

            // Text
            var textGO = new GameObject("Text");
            textGO.transform.SetParent(closeGO.transform, false);

            var textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10, 5);
            textRect.offsetMax = new Vector2(-10, -5);

            var text = textGO.AddComponent<Text>();
            text.text = "Close";
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 20;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
        }

        public void CloseModMenu()
        {
            if (_modMenuPanel != null)
            {
                Destroy(_modMenuPanel);
                _modMenuPanel = null;
            }
            _menuOpen = false;

            LLMNPCsPlugin.Log.LogInfo("[MenuIntegration] Mod menu closed");
            LLMNPCsPlugin.LogToFile("[MenuIntegration] Mod menu closed");
        }

        private void SaveConfig()
        {
            try
            {
                LLMNPCsPlugin.Instance.Config.Save();
                LLMNPCsPlugin.Log.LogInfo("[MenuIntegration] Configuration saved");
                LLMNPCsPlugin.LogToFile("[MenuIntegration] Configuration saved");
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"[MenuIntegration] Failed to save config: {ex}");
                LLMNPCsPlugin.LogToFile($"[MenuIntegration] Failed to save config: {ex}");
            }
        }

        /// <summary>Renamed from OnGUI: Unity's auto-call on this MonoBehaviour
        /// proved dead during gameplay. Plugin.OnGUI (proven alive everywhere)
        /// is now the single render driver.</summary>
        public void DrawGUI()
        {
            // Draw the mod toolbar (self-guards against double draw)
            DrawOptionsOverlay();

            if (!_menuOpen) return;

            float windowWidth = 650;
            float windowHeight = 700;
            float x = (Screen.width - windowWidth) / 2;
            float y = (Screen.height - windowHeight) / 2;

            GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.95f);

            GUILayout.BeginArea(new Rect(x, y, windowWidth, windowHeight), "LLM NPCs Settings", "window");

            GUILayout.Label("API Key:");
            _apiKeyConfig.Value = GUILayout.PasswordField(_apiKeyConfig.Value, '*', GUILayout.Height(30));

            GUILayout.Space(10);

            // --- Model Selection with Dynamic Fetch ---
            GUILayout.Label($"Current Model: {_modelConfig.Value}", GetSafeBoldLabelStyle());

            GUILayout.BeginHorizontal();
            GUI.enabled = !_isLoadingModels && !string.IsNullOrEmpty(_apiKeyConfig.Value);
            if (GUILayout.Button(_isLoadingModels ? "Fetching..." : "Fetch Models from OpenRouter", GUILayout.Height(30)))
            {
                FetchModelsFromUI();
            }
            GUI.enabled = true;
            _showFreeOnly = GUILayout.Toggle(_showFreeOnly, "Free Only", GUILayout.Width(80));
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_modelFetchStatus))
            {
                GUILayout.Label(_modelFetchStatus);
            }

            if (_models == null || _models.Count == 0)
            {
                GUILayout.Label("No models loaded yet. Click 'Fetch Models from OpenRouter' to continue (Godot-style flow).");
            }

            // Search filter
            if (_models.Count > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Search:", GUILayout.Width(50));
                _modelSearchFilter = GUILayout.TextField(_modelSearchFilter, GUILayout.Height(22));
                if (GUILayout.Button("X", GUILayout.Width(25), GUILayout.Height(22)))
                {
                    _modelSearchFilter = "";
                }
                GUILayout.EndHorizontal();

                // Scrollable model list
                var filtered = _models.Where(m =>
                    (!_showFreeOnly || m.IsFree) &&
                    (string.IsNullOrEmpty(_modelSearchFilter) ||
                     m.Id.IndexOf(_modelSearchFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     (m.Name != null && m.Name.IndexOf(_modelSearchFilter, StringComparison.OrdinalIgnoreCase) >= 0))
                ).ToList();

                GUILayout.Label($"Showing {filtered.Count} of {_models.Count} models:");

                _modelScrollPosition = GUILayout.BeginScrollView(_modelScrollPosition, GUILayout.Height(200));
                foreach (var model in filtered)
                {
                    string label = model.IsFree
                        ? $"[FREE] {model.Name ?? model.Id}  (ctx: {model.ContextLength})"
                        : $"{model.Name ?? model.Id}  (ctx: {model.ContextLength}, {model.PricePerMillion})";

                    bool isSelected = _modelConfig.Value == model.Id;
                    GUI.backgroundColor = isSelected ? new Color(0.2f, 0.6f, 0.2f) : new Color(0.2f, 0.2f, 0.3f);

                    if (GUILayout.Button(label, GUILayout.Height(22)))
                    {
                        _modelConfig.Value = model.Id;
                        _llmClient?.SetModel(model.Id);
                    }
                }
                GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.95f);
                GUILayout.EndScrollView();
            }
            else
            {
                GUILayout.Label("Model list unavailable. Use Fetch Models after API key is configured.");
            }

            GUILayout.Space(10);

            GUILayout.Label($"Temperature: {_temperatureConfig.Value:F2}");
            _temperatureConfig.Value = GUILayout.HorizontalSlider(_temperatureConfig.Value, 0f, 1f);

            GUILayout.Space(10);

            // Range is 60–600s (not 5–60): HorizontalSlider clamps the value into
            // its range on EVERY render, so a 5–60 range silently forced the
            // cost-tuned 300s interval back down to 60s just by opening this panel.
            GUILayout.Label($"Decision Interval: {_decisionIntervalConfig.Value:F0}s  (higher = fewer LLM calls / lower cost)");
            _decisionIntervalConfig.Value = GUILayout.HorizontalSlider(_decisionIntervalConfig.Value, 60f, 600f);

            GUILayout.Space(10);

            _enableModConfig.Value = GUILayout.Toggle(_enableModConfig.Value, "Enable Mod");

            // The master switch for autonomous behavior — including the Strategic
            // Model that builds the village on its own (stockpiles, beds). Off =
            // NPCs only think/talk; On = they physically act on the world.
            _enableFullAutonomyConfig.Value = GUILayout.Toggle(_enableFullAutonomyConfig.Value,
                "Enable Full AI Autonomy (villagers build the village themselves)");
            // Keep the runtime master switch in lock-step with the config so the
            // Strategic Model reacts to this toggle immediately (no restart).
            AutonomyManager.Instance.IsFullAutonomyEnabled = _enableFullAutonomyConfig.Value;

            _logDecisionsConfig.Value = GUILayout.Toggle(_logDecisionsConfig.Value, "Log Decisions");

            GUILayout.Space(20);

            if (GUILayout.Button("Save & Close", GUILayout.Height(40)))
            {
                SaveConfig();
                CloseModMenu();
            }

            GUILayout.EndArea();
        }

        private GUIStyle GetSafeBoldLabelStyle()
        {
            if (_safeBoldLabelStyle != null)
                return _safeBoldLabelStyle;

            var baseStyle = (GUI.skin != null && GUI.skin.label != null)
                ? GUI.skin.label
                : GUIStyle.none;

            _safeBoldLabelStyle = new GUIStyle(baseStyle)
            {
                fontStyle = FontStyle.Bold
            };

            return _safeBoldLabelStyle;
        }

        private async void FetchModelsFromUI()
        {
            if (_llmClient == null)
            {
                _modelFetchStatus = "Error: LLM client not initialized";
                return;
            }

            _isLoadingModels = true;
            _modelFetchStatus = "Fetching models from OpenRouter...";

            try
            {
                var models = await _llmClient.FetchAvailableModelsAsync();
                if (models != null && models.Count > 0)
                {
                    _models = models;
                    var freeCount = models.Count(m => m.IsFree);
                    _modelFetchStatus = $"Loaded {models.Count} models ({freeCount} free)";
                }
                else
                {
                    _modelFetchStatus = "Failed to fetch models. Check API key and connection.";
                }
            }
            catch (Exception ex)
            {
                _modelFetchStatus = $"Error: {ex.Message}";
                LLMNPCsPlugin.Log.LogError($"[MenuIntegration] Model fetch error: {ex}");
            }
            finally
            {
                _isLoadingModels = false;
            }
        }
    }

    /// <summary>
    /// Helper class to receive input and show the menu
    /// </summary>
    public class StandaloneMenuButton : MonoBehaviour
    {
        private MenuIntegration _menuIntegration;

        public void Initialize(MenuIntegration menuIntegration)
        {
            _menuIntegration = menuIntegration;
        }

        void OnGUI()
        {
            // Draw a small button in the corner as fallback
            if (GUI.Button(new Rect(Screen.width - 120, 10, 110, 30), "LLM NPCs"))
            {
                _menuIntegration?.ToggleModMenu();
            }
        }
    }
}
