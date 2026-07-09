using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    public class PromptDebugMonitorWindow
    {
        private readonly ConfigEntry<bool> _visibleConfig;
        private readonly ConfigEntry<KeyboardShortcut> _hotkeyConfig;

        private bool _visible;
        private Rect _windowRect = new Rect(20, 20, 760, 360);
        private Vector2 _scroll;
        private float _lastRefreshTime = -99f;
        private List<PromptTraceEvent> _cachedEvents = new List<PromptTraceEvent>();
        private PromptTraceCounters _cachedCounters;
        private string _tracePath;

        private const float RefreshInterval = 0.25f;
        private const int MaxDisplayedEvents = 80;

        public PromptDebugMonitorWindow(ConfigEntry<bool> visibleConfig, ConfigEntry<KeyboardShortcut> hotkeyConfig)
        {
            _visibleConfig = visibleConfig;
            _hotkeyConfig = hotkeyConfig;
            _visible = _visibleConfig?.Value ?? false;
            _tracePath = PromptTrace.GetTraceFilePath() ?? string.Empty;
        }

        /// <summary>Toolbar entry point (Ken: windows had no obvious opener).</summary>
        public void Toggle()
        {
            _visible = !_visible;
            if (_visibleConfig != null)
                _visibleConfig.Value = _visible;
        }

        public void Update()
        {
            if (_hotkeyConfig != null && _hotkeyConfig.Value.IsDown())
            {
                Toggle();
            }

            if (!_visible)
                return;

            if (Time.realtimeSinceStartup - _lastRefreshTime < RefreshInterval)
                return;

            _cachedEvents = PromptTrace.GetRecentSnapshot(MaxDisplayedEvents);
            _cachedCounters = PromptTrace.GetCounters();
            _tracePath = PromptTrace.GetTraceFilePath() ?? string.Empty;
            _lastRefreshTime = Time.realtimeSinceStartup;
        }

        public void OnGUI()
        {
            if (!_visible)
                return;

            _windowRect = GUI.Window(1507, _windowRect, DrawWindow, "LLM Prompt Debug Monitor");
        }

        private void DrawWindow(int windowId)
        {
            GUI.DragWindow(new Rect(0, 0, _windowRect.width - 85, 20));

            if (GUI.Button(new Rect(_windowRect.width - 80, 2, 75, 20), "Hide"))
            {
                _visible = false;
                if (_visibleConfig != null)
                    _visibleConfig.Value = false;
                return;
            }

            GUI.Label(new Rect(10, 24, _windowRect.width - 20, 20),
                $"Sent: {_cachedCounters.Sent}    Success: {_cachedCounters.Success}    Error: {_cachedCounters.Error}");

            GUI.Label(new Rect(10, 42, _windowRect.width - 20, 18),
                string.IsNullOrEmpty(_tracePath) ? "Trace file: (not initialized)" : $"Trace file: {_tracePath}");

            if (GUI.Button(new Rect(_windowRect.width - 165, 40, 70, 18), "Refresh"))
            {
                _cachedEvents = PromptTrace.GetRecentSnapshot(MaxDisplayedEvents);
                _cachedCounters = PromptTrace.GetCounters();
                _tracePath = PromptTrace.GetTraceFilePath() ?? string.Empty;
                _lastRefreshTime = Time.realtimeSinceStartup;
            }

            if (GUI.Button(new Rect(_windowRect.width - 90, 40, 70, 18), "Clear"))
            {
                PromptTrace.ClearRecent();
                _cachedEvents = PromptTrace.GetRecentSnapshot(MaxDisplayedEvents);
                _cachedCounters = PromptTrace.GetCounters();
            }

            var listRect = new Rect(10, 64, _windowRect.width - 20, _windowRect.height - 74);
            GUI.Box(listRect, "");

            var contentHeight = Math.Max(1, _cachedEvents.Count) * 36;
            _scroll = GUI.BeginScrollView(
                new Rect(listRect.x + 2, listRect.y + 2, listRect.width - 4, listRect.height - 4),
                _scroll,
                new Rect(0, 0, listRect.width - 22, contentHeight));

            float y = 0;
            foreach (var evt in _cachedEvents)
            {
                DrawTraceRow(evt, y, listRect.width - 26);
                y += 36;
            }

            GUI.EndScrollView();
        }

        private void DrawTraceRow(PromptTraceEvent evt, float y, float width)
        {
            var statusColor = new Color(0.9f, 0.9f, 0.9f, 0.2f);
            if (evt.Status == "success") statusColor = new Color(0.2f, 0.6f, 0.2f, 0.25f);
            if (evt.Status == "error") statusColor = new Color(0.75f, 0.2f, 0.2f, 0.3f);

            var oldColor = GUI.color;
            GUI.color = statusColor;
            GUI.Box(new Rect(0, y, width, 34), "");
            GUI.color = oldColor;

            var ts = evt.TimestampUtc;
            if (!string.IsNullOrEmpty(ts) && ts.Length > 19)
                ts = ts.Substring(11, 8);

            var line1 =
                $"[{ts}] {evt.Status?.ToUpperInvariant()} {evt.FlowType}  {evt.SenderName}->{evt.TargetName}  {evt.ModelId}  {evt.LatencyMs}ms";
            
            string line2 = "";
            if (evt.Status == "success")
            {
                line2 = $"response({evt.ResponseLength}): {evt.ResponsePreview}";
            }
            else if (evt.Status == "error")
            {
                line2 = $"error: {evt.ErrorText}";
                if (!string.IsNullOrEmpty(evt.ResponsePreview))
                    line2 += $" | partial response: {evt.ResponsePreview}";
            }
            else
            {
                var preview = evt.PromptPreview ?? "";
                preview = preview.Replace("[primary_model_only] true [current-attempt] 1/1 [fallback-attempts] none [system] You are controlling a settler in a medieval colony simulation game.  Your job is to make decisions for this character based on their curre…", "...");
                preview = preview.Replace("[system] You are controlling a settler in a medieval colony simulation game.", "");
                preview = preview.Replace("[system] You are generating dialogue for a settler in a medieval colony game.", "");
                line2 = $"prompt({evt.PromptLength}): {preview.Trim()}";
            }

            GUI.Label(new Rect(4, y + 1, width - 8, 16), line1,
                new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Bold });
            GUI.Label(new Rect(4, y + 16, width - 8, 16), line2,
                new GUIStyle(GUI.skin.label) { fontSize = 9 });
        }
    }
}
