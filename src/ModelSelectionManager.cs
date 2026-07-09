using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx.Configuration;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// In-game model selection UI for choosing OpenRouter models.
    /// Similar to Godot agent's model selection interface.
    /// Press F1 to open the model selection window.
    /// </summary>
    public class ModelSelectionManager
    {
        private readonly LLMClient _llmClient;
        private readonly ConfigEntry<string> _modelConfig;

        // UI State
        private bool _showWindow = false;
        private Rect _windowRect = new Rect(50, 50, 600, 500);
        private Vector2 _scrollPosition;
        private string _searchFilter = "";
        private List<OpenRouterModel> _models = new List<OpenRouterModel>();
        private bool _isLoading = false;
        private string _statusMessage = "";
        private bool _showFreeOnly = true;

        // Hotkey
        private const KeyCode OpenKey = KeyCode.F1;

        public ModelSelectionManager(LLMClient llmClient, ConfigEntry<string> modelConfig)
        {
            _llmClient = llmClient;
            _modelConfig = modelConfig;
        }

        public void Update()
        {
            if (Input.GetKeyDown(OpenKey))
            {
                ToggleWindow();
            }
        }

        public void OnGUI()
        {
            if (!_showWindow) return;

            _windowRect = GUI.Window(
                998, // Window ID
                _windowRect,
                DrawWindow,
                "OpenRouter Model Selection"
            );
        }

        private void DrawWindow(int windowID)
        {
            // Make window draggable
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 20));

            // Close button
            if (GUI.Button(new Rect(_windowRect.width - 25, 2, 23, 18), "X"))
            {
                ToggleWindow();
                return;
            }

            float y = 25;

            // Current model display
            GUI.Label(new Rect(10, y, _windowRect.width - 20, 20), 
                $"Current: {_llmClient.GetCurrentModel()}", 
                new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });
            y += 25;

            // Controls row
            // Free only toggle
            GUI.Label(new Rect(10, y, 80, 25), "Free only:");
            _showFreeOnly = GUI.Toggle(new Rect(90, y, 20, 25), _showFreeOnly, "");

            // Refresh button
            if (GUI.Button(new Rect(120, y, 100, 25), _isLoading ? "Loading..." : "Refresh"))
            {
                if (!_isLoading)
                {
                    RefreshModels();
                }
            }

            // Search field
            GUI.Label(new Rect(230, y, 50, 25), "Search:");
            _searchFilter = GUI.TextField(new Rect(285, y, 200, 25), _searchFilter);
            y += 35;

            // Status message
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUI.Label(new Rect(10, y, _windowRect.width - 20, 20), _statusMessage);
                y += 25;
            }

            // Model list header
            GUI.Label(new Rect(10, y, 300, 20), "Model Name", new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });
            GUI.Label(new Rect(320, y, 80, 20), "Context", new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });
            GUI.Label(new Rect(410, y, 80, 20), "Price", new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });
            y += 25;

            // Model list
            float listHeight = _windowRect.height - y - 40;
            var filteredModels = GetFilteredModels();

            _scrollPosition = GUI.BeginScrollView(
                new Rect(10, y, _windowRect.width - 20, listHeight),
                _scrollPosition,
                new Rect(0, 0, _windowRect.width - 40, filteredModels.Count * 30)
            );

            float itemY = 0;
            foreach (var model in filteredModels)
            {
                DrawModelRow(model, ref itemY);
            }

            GUI.EndScrollView();

            // Bottom info
            GUI.Label(new Rect(10, _windowRect.height - 30, _windowRect.width - 20, 20),
                $"{filteredModels.Count} models shown | Press F1 to toggle | Free models marked with [*]");
        }

        private void DrawModelRow(OpenRouterModel model, ref float y)
        {
            float rowHeight = 30;
            bool isSelected = model.Id == _llmClient.GetCurrentModel();

            // Background color
            if (isSelected)
            {
                GUI.backgroundColor = new Color(0.2f, 0.6f, 0.2f, 0.8f);
            }
            else if (model.IsFree)
            {
                GUI.backgroundColor = new Color(0.2f, 0.4f, 0.6f, 0.3f);
            }
            else
            {
                GUI.backgroundColor = new Color(0.3f, 0.3f, 0.3f, 0.3f);
            }

            // Row background
            GUI.Box(new Rect(0, y, _windowRect.width - 40, rowHeight), "");

            // Model name (truncated if too long)
            string displayName = model.Name;
            if (model.IsFree) displayName = "[*] " + displayName;
            if (displayName.Length > 40) displayName = displayName.Substring(0, 37) + "...";

            GUI.Label(new Rect(5, y + 5, 300, 20), displayName);

            // Context length
            GUI.Label(new Rect(310, y + 5, 80, 20), $"{model.ContextLength}");

            // Price per 1M tokens (prompt/completion) — budgetable numbers
            GUI.Label(new Rect(400, y + 5, 140, 20), model.PricePerMillion);

            // Select button
            if (!isSelected)
            {
                if (GUI.Button(new Rect(480, y + 2, 60, 24), "Select"))
                {
                    SelectModel(model);
                }
            }
            else
            {
                GUI.Label(new Rect(480, y + 5, 60, 20), "Active");
            }

            GUI.backgroundColor = Color.white;
            y += rowHeight;
        }

        private List<OpenRouterModel> GetFilteredModels()
        {
            var filtered = _models.AsEnumerable();

            // Free only filter
            if (_showFreeOnly)
            {
                filtered = filtered.Where(m => m.IsFree);
            }

            // Search filter
            if (!string.IsNullOrEmpty(_searchFilter))
            {
                var search = _searchFilter.ToLower();
                filtered = filtered.Where(m => 
                    m.Name.ToLower().Contains(search) || 
                    m.Id.ToLower().Contains(search));
            }

            return filtered.ToList();
        }

        private void ToggleWindow()
        {
            _showWindow = !_showWindow;

            if (_showWindow && _models.Count == 0)
            {
                RefreshModels();
            }
        }

        private async void RefreshModels()
        {
            _isLoading = true;
            _statusMessage = "Fetching models from OpenRouter...";

            try
            {
                var models = await _llmClient.FetchAvailableModelsAsync();
                if (models != null && models.Count > 0)
                {
                    _models = models;
                    var freeCount = models.Count(m => m.IsFree);
                    _statusMessage = $"Loaded {models.Count} models ({freeCount} free)";
                }
                else
                {
                    _statusMessage = "Failed to fetch models. Check API key.";
                }
            }
            catch (Exception ex)
            {
                _statusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void SelectModel(OpenRouterModel model)
        {
            _llmClient.SetModel(model.Id);
            _modelConfig.Value = model.Id;
            _statusMessage = $"Selected: {model.Name}";
            LLMNPCsPlugin.Log.LogInfo($"[ModelSelection] Changed model to: {model.Id}");
        }
    }
}
