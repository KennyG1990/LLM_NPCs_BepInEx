using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Colony Social Hub — simple procedural IMGUI version.
    /// Hosts player-NPC chat, NPC-NPC eavesdrop, relationships, and marriage.
    /// Asset-free placeholder to use while the new UI kit is being built.
    /// </summary>
    public class SocialHubWindow
    {
        private readonly DialogueManager _dialogueManager;
        private readonly NPCToNPCDialogueManager _npcDialogueManager;
        private readonly RelationshipSystem _relationshipSystem;
        private readonly ChatBubbleManager _chatBubbleManager;
        private readonly ConfigEntry<KeyboardShortcut> _hotkey;

        // ─── Window state ─────────────────────────────────────────────────────────
        private bool _isVisible;
        private Rect _windowRect = new Rect(60, 60, 780, 560);
        private int _selectedTab = 0;
        private readonly string[] _tabs = { "Conversations", "Relationships", "Marriage" };

        // ─── Conversation tab ─────────────────────────────────────────────────────
        private Vector2 _npcListScroll;
        private Vector2 _chatScroll;
        private string _selectedNPCId;
        private string _playerInput = "";
        private bool _inputFocusPending;
        private float _thinkingAnimTimer;
        private int _thinkingDotCount = 1;

        private readonly Dictionary<string, List<InlineMessage>> _chatHistories =
            new Dictionary<string, List<InlineMessage>>();
        private readonly Dictionary<string, bool> _waitingForResponse =
            new Dictionary<string, bool>();
        private readonly Dictionary<string, string> _lastErrors =
            new Dictionary<string, string>();

        // ─── Relationship tab ─────────────────────────────────────────────────────
        private Vector2 _relScroll;

        // ─── Marriage tab ─────────────────────────────────────────────────────────
        private Vector2 _marriageScroll;

        // ─── Settler cache ────────────────────────────────────────────────────────
        private readonly List<SettlerSnapshot> _settlers = new List<SettlerSnapshot>();
        private readonly Dictionary<string, SettlerSnapshot> _settlerById =
            new Dictionary<string, SettlerSnapshot>();
        private float _snapshotTimestamp = -999f;
        private const float SNAPSHOT_TTL = 0.5f;

        // ─── Styles ───────────────────────────────────────────────────────────────
        private GUIStyle _windowStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _npcBubbleStyle;
        private GUIStyle _playerBubbleStyle;
        private GUIStyle _inputStyle;
        private bool _stylesReady;

        // ─── Inner types ──────────────────────────────────────────────────────────
        private class InlineMessage
        {
            public string Text;
            public bool IsPlayer;
            public string SpeakerName;
        }

        private class SettlerSnapshot
        {
            public Settler Settler;
            public string Id;
            public string Name;
            public string Profession;
            public string Mood;
        }

        // ─────────────────────────────────────────────────────────────────────────
        public SocialHubWindow(
            DialogueManager dialogueManager,
            NPCToNPCDialogueManager npcDialogueManager,
            RelationshipSystem relationshipSystem,
            ChatBubbleManager chatBubbleManager,
            ConfigEntry<KeyboardShortcut> hotkey = null)
        {
            _dialogueManager    = dialogueManager;
            _npcDialogueManager = npcDialogueManager;
            _relationshipSystem = relationshipSystem;
            _chatBubbleManager  = chatBubbleManager;
            _hotkey             = hotkey;
        }

        public void Toggle()
        {
            _isVisible = !_isVisible;
            if (_isVisible) _snapshotTimestamp = -999f;
            LLMNPCsPlugin.LogToFile($"[SocialHubWindow] Toggled: {_isVisible}");
        }

        public void Update()
        {
            if (_hotkey != null && _hotkey.Value.IsDown())
                Toggle();

            if (_isVisible)
            {
                _thinkingAnimTimer += Time.deltaTime;
                if (_thinkingAnimTimer >= 0.45f)
                {
                    _thinkingAnimTimer = 0f;
                    _thinkingDotCount = (_thinkingDotCount % 3) + 1;
                }
            }
        }

        public void OnGUI()
        {
            if (!_isVisible) return;

            EnsureStyles();
            RefreshSettlers();

            _windowRect = GUI.Window(9001, _windowRect, DrawWindow, "", _windowStyle);

            // Only swallow actual input events (never Layout or Repaint — that corrupts IMGUI)
            var evt = Event.current;
            if (evt != null && _windowRect.Contains(evt.mousePosition))
            {
                var t = evt.type;
                if (t == EventType.MouseDown || t == EventType.MouseUp ||
                    t == EventType.MouseDrag || t == EventType.ScrollWheel ||
                    t == EventType.KeyDown   || t == EventType.KeyUp)
                    evt.Use();
            }
        }

        // ─── Window draw ──────────────────────────────────────────────────────────
        private void DrawWindow(int id)
        {
            GUI.DragWindow(new Rect(0, 0, _windowRect.width - 30, 22));

            // Close button
            if (GUI.Button(new Rect(_windowRect.width - 26, 3, 22, 18), "×"))
                _isVisible = false;

            GUILayout.Space(4);
            GUILayout.Label("Colony Social Hub", new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.95f, 0.82f, 0.48f) },
                alignment = TextAnchor.MiddleCenter
            });

            _selectedTab = GUILayout.Toolbar(_selectedTab, _tabs);
            GUILayout.Space(4);

            switch (_selectedTab)
            {
                case 0: DrawConversationsTab(); break;
                case 1: DrawRelationshipsTab(); break;
                case 2: DrawMarriageTab(); break;
            }

            // Pending focus
            if (_inputFocusPending && Event.current.type == EventType.Repaint)
            {
                GUI.FocusControl("ChatInput");
                _inputFocusPending = false;
            }
        }

        // ─── Conversations tab ────────────────────────────────────────────────────
        private void DrawConversationsTab()
        {
            GUILayout.BeginHorizontal();

            // NPC list (left panel)
            GUILayout.BeginVertical(GUILayout.Width(180));
            GUILayout.Label("Settlers", new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.88f, 0.82f, 0.70f) }
            });
            _npcListScroll = GUILayout.BeginScrollView(_npcListScroll);
            foreach (var s in _settlers)
            {
                var selected = s.Id == _selectedNPCId;
                var style = selected
                    ? new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold }
                    : GUI.skin.button;
                var label = $"{s.Name}\n<size=10>{s.Profession}</size>";
                if (GUILayout.Button(label, style))
                {
                    _selectedNPCId = s.Id;
                    if (!_chatHistories.ContainsKey(s.Id))
                        _chatHistories[s.Id] = new List<InlineMessage>();
                    _inputFocusPending = true;
                }
            }
            if (_settlers.Count == 0)
                GUILayout.Label("No settlers found", GUI.skin.label);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            // Chat panel (right)
            GUILayout.BeginVertical();

            if (_selectedNPCId == null || !_settlerById.ContainsKey(_selectedNPCId))
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label("Select a settler to chat", new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(0.62f, 0.56f, 0.44f) }
                });
                GUILayout.FlexibleSpace();
            }
            else
            {
                var s = _settlerById[_selectedNPCId];
                GUILayout.Label($"{s.Name}  ·  {s.Profession}  ·  {s.Mood}", GUI.skin.label);
                GUILayout.Space(4);

                // Error banner
                if (_lastErrors.TryGetValue(_selectedNPCId, out var err) && !string.IsNullOrEmpty(err))
                    GUILayout.Label($"⚠ {err}", new GUIStyle(GUI.skin.label)
                        { normal = { textColor = new Color(1f, 0.5f, 0.4f) }, wordWrap = true });

                // Chat history
                _chatScroll = GUILayout.BeginScrollView(_chatScroll, GUILayout.ExpandHeight(true));
                if (_chatHistories.TryGetValue(_selectedNPCId, out var msgs))
                {
                    foreach (var m in msgs)
                    {
                        var prefix = m.IsPlayer ? "You: " : $"{m.SpeakerName}: ";
                        GUILayout.Label(prefix + m.Text, m.IsPlayer ? _playerBubbleStyle : _npcBubbleStyle);
                        GUILayout.Space(2);
                    }
                }

                // Thinking indicator
                var waiting = _waitingForResponse.TryGetValue(_selectedNPCId, out var w) && w;
                if (waiting)
                {
                    var dots = new string('.', _thinkingDotCount);
                    GUILayout.Label($"{s.Name} is thinking{dots}", new GUIStyle(GUI.skin.label)
                        { normal = { textColor = new Color(0.62f, 0.56f, 0.44f) } });
                }
                GUILayout.EndScrollView();

                // Input row at bottom
                GUILayout.Space(2);
                GUILayout.BeginHorizontal(GUILayout.Height(40));
                
                var canSend = !waiting && !string.IsNullOrWhiteSpace(_playerInput);
                
                // Enter key handling MUST happen before the TextArea consumes the event
                if (Event.current.type == EventType.KeyDown && 
                    (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
                {
                    if (canSend)
                    {
                        SendMessage(s);
                        Event.current.Use();
                    }
                }

                GUI.SetNextControlName("ChatInput");
                
                // Use a custom style that ensures word-wrapping but limits height growth
                var areaStyle = new GUIStyle(GUI.skin.textArea) { wordWrap = true };
                _playerInput = GUILayout.TextArea(_playerInput, areaStyle, GUILayout.ExpandWidth(true), GUILayout.Height(36));
                
                GUI.enabled = canSend;
                if (GUILayout.Button("Send", GUILayout.Width(60), GUILayout.Height(36)))
                {
                    SendMessage(s);
                }
                GUI.enabled = true;
                
                GUILayout.EndHorizontal();
            }

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            // NPC-NPC eavesdrop section
            var activeConvos = _npcDialogueManager?.GetActiveConversations();
            if (activeConvos != null && activeConvos.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label("── Active NPC Conversations ──", new GUIStyle(GUI.skin.label)
                    { alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.62f, 0.56f, 0.44f) } });
                foreach (var c in activeConvos)
                {
                    var last = c.Exchanges?.LastOrDefault();
                    if (last != null)
                        GUILayout.Label($"{last.SpeakerName}: {last.Text}", _npcBubbleStyle);
                }
            }
        }

        private void SendMessage(SettlerSnapshot s)
        {
            var msg = _playerInput.Trim();
            _playerInput = "";
            if (string.IsNullOrEmpty(msg)) return;

            if (!_chatHistories.ContainsKey(s.Id))
                _chatHistories[s.Id] = new List<InlineMessage>();

            _chatHistories[s.Id].Add(new InlineMessage { Text = msg, IsPlayer = true, SpeakerName = "You" });
            _waitingForResponse[s.Id] = true;
            _lastErrors[s.Id] = null;

            _chatScroll.y = float.MaxValue;

            _dialogueManager?.SendInlineMessage(
                s.Settler,
                msg,
                onResponse: response =>
                {
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        _waitingForResponse[s.Id] = false;
                        _chatHistories[s.Id].Add(new InlineMessage
                            { Text = response, IsPlayer = false, SpeakerName = s.Name });
                        _lastErrors[s.Id] = null;
                        _chatScroll.y = float.MaxValue;
                    });
                },
                onError: err =>
                {
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        _waitingForResponse[s.Id] = false;
                        _lastErrors[s.Id] = err ?? "No response.";
                        _chatScroll.y = float.MaxValue;
                    });
                });

            _inputFocusPending = true;
        }

        // ─── Relationships tab ────────────────────────────────────────────────────
        private void DrawRelationshipsTab()
        {
            GUILayout.Label("NPC Relationships", new GUIStyle(GUI.skin.label)
                { fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.88f, 0.82f, 0.70f) } });
            GUILayout.Space(4);

            _relScroll = GUILayout.BeginScrollView(_relScroll);
            if (_settlers.Count < 2)
            {
                GUILayout.Label("Need at least 2 settlers.");
            }
            else
            {
                for (int i = 0; i < _settlers.Count; i++)
                {
                    for (int j = i + 1; j < _settlers.Count; j++)
                    {
                        var a = _settlers[i];
                        var b = _settlers[j];
                        var rel = _relationshipSystem?.GetRelationship(a.Id, b.Id);
                        if (rel == null) continue;

                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"{a.Name} ↔ {b.Name}", GUILayout.Width(220));
                        DrawBar("❤ ", rel.Friendship, new Color(0.28f, 0.68f, 0.30f));
                        if (rel.Romance > 0)
                            DrawBar("♥ ", rel.Romance, new Color(0.85f, 0.32f, 0.48f));
                        if (rel.Rivalry > 0)
                            DrawBar("⚔ ", rel.Rivalry, new Color(0.85f, 0.35f, 0.20f));
                        if (rel.IsMarried)
                            GUILayout.Label("💍 Married", GUILayout.Width(70));
                        GUILayout.EndHorizontal();
                    }
                }
            }
            GUILayout.EndScrollView();
        }

        private void DrawBar(string icon, float value, Color color)
        {
            GUILayout.Label(icon, GUILayout.Width(20));
            var rect = GUILayoutUtility.GetRect(140, 14, GUILayout.Width(140));
            GUI.Box(rect, "");
            var fill = new Rect(rect.x + 1, rect.y + 1, (rect.width - 2) * Mathf.Clamp01(value), rect.height - 2);
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(fill, Texture2D.whiteTexture);
            GUI.color = prev;
            GUILayout.Label($"{value:P0}", GUILayout.Width(44));
        }

        // ─── Marriage tab ─────────────────────────────────────────────────────────
        private void DrawMarriageTab()
        {
            GUILayout.Label("Marriage", new GUIStyle(GUI.skin.label)
                { fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.88f, 0.82f, 0.70f) } });
            GUILayout.Space(4);

            _marriageScroll = GUILayout.BeginScrollView(_marriageScroll);

            // Current marriages
            var married = GetMarriedPairs();
            if (married.Count == 0)
            {
                GUILayout.Label("No married couples.");
            }
            else
            {
                GUILayout.Label("Current marriages:");
                foreach (var (a, b) in married)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"💍  {a.Name}  &  {b.Name}", GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Divorce", GUILayout.Width(70)))
                    {
                        var rel = _relationshipSystem?.GetRelationship(a.Id, b.Id);
                        if (rel != null) { rel.IsMarried = false; rel.RelationshipType = "friends"; }
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Space(8);

            // Propose marriage between two settlers
            GUILayout.Label("Propose marriage:");
            for (int i = 0; i < _settlers.Count; i++)
            {
                for (int j = i + 1; j < _settlers.Count; j++)
                {
                    var a = _settlers[i];
                    var b = _settlers[j];
                    var rel = _relationshipSystem?.GetRelationship(a.Id, b.Id);
                    if (rel == null || rel.IsMarried) continue;

                    if (rel.Romance >= 0.6f)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"  {a.Name}  &  {b.Name}", GUILayout.ExpandWidth(true));
                        if (GUILayout.Button("Marry", GUILayout.Width(60)))
                            _relationshipSystem?.TryMarry(a.Id, b.Id);
                        GUILayout.EndHorizontal();
                    }
                }
            }

            GUILayout.EndScrollView();
        }

        private List<(SettlerSnapshot, SettlerSnapshot)> GetMarriedPairs()
        {
            var result = new List<(SettlerSnapshot, SettlerSnapshot)>();
            var seen = new HashSet<string>();
            for (int i = 0; i < _settlers.Count; i++)
            {
                for (int j = i + 1; j < _settlers.Count; j++)
                {
                    var a = _settlers[i];
                    var b = _settlers[j];
                    var key = $"{a.Id}:{b.Id}";
                    if (seen.Contains(key)) continue;
                    var rel = _relationshipSystem?.GetRelationship(a.Id, b.Id);
                    if (rel?.IsMarried == true)
                    {
                        result.Add((a, b));
                        seen.Add(key);
                    }
                }
            }
            return result;
        }

        // ─── Settler snapshot ─────────────────────────────────────────────────────
        private void RefreshSettlers()
        {
            if (Time.time - _snapshotTimestamp < SNAPSHOT_TTL) return;
            _snapshotTimestamp = Time.time;

            _settlers.Clear();
            _settlerById.Clear();

            var raw = GameBridge.GetValidatedSettlers();
            if (raw == null) return;

            foreach (var settler in raw)
            {
                if (settler == null) continue;
                try
                {
                    var ctx = NPCContextExtractor.Extract(settler);
                    if (ctx == null) continue;
                    var snap = new SettlerSnapshot
                    {
                        Settler    = settler,
                        Id         = ctx.Id,
                        Name       = ctx.Name ?? "Unknown",
                        Profession = ctx.Profession ?? "Worker",
                        Mood       = ctx.Mood ?? "neutral"
                    };
                    _settlers.Add(snap);
                    _settlerById[snap.Id] = snap;
                }
                catch { /* skip broken settler */ }
            }
        }

        // ─── Style init ───────────────────────────────────────────────────────────
        private void EnsureStyles()
        {
            if (_stylesReady) return;

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = MakeTex(4, 4, new Color(0.12f, 0.09f, 0.06f, 0.97f));
            _windowStyle.normal.textColor  = new Color(0.88f, 0.82f, 0.70f);

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true,
                normal = { textColor = new Color(0.88f, 0.82f, 0.70f) }
            };

            _npcBubbleStyle = new GUIStyle(GUI.skin.box)
            {
                wordWrap  = true,
                alignment = TextAnchor.UpperLeft,
                padding   = new RectOffset(8, 8, 6, 6)
            };
            _npcBubbleStyle.normal.background = MakeTex(2, 2, new Color(0.22f, 0.17f, 0.10f, 0.92f));
            _npcBubbleStyle.normal.textColor  = new Color(0.88f, 0.82f, 0.70f);

            _playerBubbleStyle = new GUIStyle(_npcBubbleStyle);
            _playerBubbleStyle.normal.background = MakeTex(2, 2, new Color(0.32f, 0.22f, 0.07f, 0.95f));
            _playerBubbleStyle.normal.textColor  = new Color(0.95f, 0.90f, 0.75f);
            _playerBubbleStyle.alignment         = TextAnchor.UpperRight;

            _inputStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 13,
                normal   = { textColor = new Color(0.88f, 0.82f, 0.70f) }
            };
            _inputStyle.normal.background = MakeTex(2, 2, new Color(0.15f, 0.11f, 0.06f, 1f));

            _stylesReady = true;
        }

        private static Texture2D MakeTex(int w, int h, Color c)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = c;
            var t = new Texture2D(w, h);
            t.SetPixels(pix);
            t.Apply();
            return t;
        }
    }
}
