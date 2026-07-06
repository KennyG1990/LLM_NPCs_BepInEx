using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Manages 3D chat bubbles above NPC heads for dialogue display.
    /// Shows NPC-NPC conversations and player-NPC dialogue in world space.
    /// </summary>
    public class ChatBubbleManager
    {
        private readonly Dictionary<string, ChatBubble> _activeBubbles;
        private readonly Dictionary<string, ChatBubble> _npcToBubble;
        private readonly float _bubbleDuration;
        private readonly float _maxBubbleDistance;

        // GUI Styles
        private GUIStyle _bubbleStyle;
        private GUIStyle _nameStyle;
        private GUIStyle _marriedBubbleStyle;
        private bool _stylesInitialized;

        public ChatBubbleManager(float duration = 5f, float maxDistance = 30f)
        {
            _activeBubbles = new Dictionary<string, ChatBubble>();
            _npcToBubble = new Dictionary<string, ChatBubble>();
            _bubbleDuration = duration;
            _maxBubbleDistance = maxDistance;
            LLMNPCsPlugin.LogToFile("[ChatBubbleManager:Constructor] Initialized");
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;

            // Load the new NPC thought bubble art asset, falling back to procedural grey box
            var bubbleTex   = UIAssetLoader.OrFallback(
                UIAssetLoader.Load("npc_thought_bubble.png"),
                () => MakeTexture(2, 2, new Color(0.1f, 0.1f, 0.1f, 0.85f)));

            var marriedTex  = UIAssetLoader.OrFallback(
                UIAssetLoader.Load("npc_thought_bubble.png"), // Same art for both right now
                () => MakeTexture(2, 2, new Color(0.4f, 0.2f, 0.4f, 0.9f)));

            _bubbleStyle = new GUIStyle();
            _bubbleStyle.normal.background = bubbleTex;
            _bubbleStyle.border = new RectOffset(20, 20, 20, 30); // account for the connecting dots tail
            _bubbleStyle.padding = new RectOffset(15, 15, 15, 25);
            _bubbleStyle.wordWrap = true;
            _bubbleStyle.fontSize = 13;
            // Use dark ink color (#2F1B14) to match the parchment aesthetic
            _bubbleStyle.normal.textColor = new Color(0.18f, 0.11f, 0.08f, 1f); 
            _bubbleStyle.alignment = TextAnchor.UpperCenter;

            _marriedBubbleStyle = new GUIStyle(_bubbleStyle);
            _marriedBubbleStyle.normal.background = marriedTex;

            _nameStyle = new GUIStyle();
            _nameStyle.fontSize = 11;
            _nameStyle.fontStyle = FontStyle.Bold;
            // Darker red/brown for names to match the parchment
            _nameStyle.normal.textColor = new Color(0.4f, 0.15f, 0.1f, 1f); 
            _nameStyle.alignment = TextAnchor.UpperCenter;

            _stylesInitialized = true;
        }

        private Texture2D MakeTexture(int width, int height, Color color)
        {
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            Texture2D result = new Texture2D(width, height);
            result.SetPixels(pixels);
            result.Apply();
            return result;
        }

        /// <summary>
        /// Shows a chat bubble above an NPC.
        /// </summary>
        public void ShowBubble(string npcId, string npcName, string message, 
            Vector3 worldPosition, bool isMarried = false, string relationshipIcon = null, float duration = -1f)
        {
            var bubbleId = Guid.NewGuid().ToString();
            
            // Remove existing bubble for this NPC
            if (_npcToBubble.TryGetValue(npcId, out var existing))
            {
                RemoveBubble(existing.Id);
            }

            var bubble = new ChatBubble
            {
                Id = bubbleId,
                NPCId = npcId,
                NPCName = npcName,
                Message = message,
                WorldPosition = worldPosition,
                CreatedAt = Time.time,
                Duration = duration > 0 ? duration : _bubbleDuration,
                IsMarried = isMarried,
                RelationshipIcon = relationshipIcon
            };

            _activeBubbles[bubbleId] = bubble;
            _npcToBubble[npcId] = bubble;

            LLMNPCsPlugin.LogToFile($"[ChatBubbleManager] Show: {npcName}: {message}");
        }

        /// <summary>
        /// Updates all bubbles - call from Plugin.Update()
        /// </summary>
        public void Update()
        {
            var currentTime = Time.time;
            var expired = _activeBubbles.Values
                .Where(b => currentTime - b.CreatedAt > b.Duration)
                .Select(b => b.Id)
                .ToList();

            foreach (var id in expired)
            {
                RemoveBubble(id);
            }
        }

        /// <summary>
        /// Renders all chat bubbles - call from Plugin.OnGUI()
        /// </summary>
        public void OnGUI()
        {
            if (_activeBubbles.Count == 0) return;

            InitializeStyles();

            var camera = Camera.main;
            if (camera == null) return;

            // Sort by distance (furthest first for proper overlap)
            var sortedBubbles = _activeBubbles.Values
                .OrderBy(b => Vector3.Distance(camera.transform.position, b.WorldPosition))
                .ToList();

            foreach (var bubble in sortedBubbles)
            {
                DrawBubble(bubble, camera);
            }
        }

        private void DrawBubble(ChatBubble bubble, Camera camera)
        {
            // Check distance
            var distance = Vector3.Distance(camera.transform.position, bubble.WorldPosition);
            if (distance > _maxBubbleDistance) return;

            // Convert world position to screen position (above NPC head)
            var screenPos = camera.WorldToScreenPoint(bubble.WorldPosition + Vector3.up * 2.5f);
            
            // Check if behind camera
            if (screenPos.z < 0) return;

            // Flip Y for GUI coordinates
            screenPos.y = Screen.height - screenPos.y;

            // Calculate bubble size based on text
            var content = new GUIContent(bubble.Message);
            var textSize = _bubbleStyle.CalcSize(content);
            var bubbleWidth = Mathf.Min(Mathf.Max(textSize.x + 30, 160), 320);
            var bubbleHeight = _bubbleStyle.CalcHeight(content, bubbleWidth) + 40; // account for tail

            var age = Time.time - bubble.CreatedAt;
            var alpha = 1f;
            float yOffset = 0f;

            // Fade in + slide up over 0.3s (ease-out)
            if (age < 0.3f)
            {
                float t = age / 0.3f;
                float easeOut = 1f - Mathf.Pow(1f - t, 3); // cubic ease-out
                alpha = easeOut;
                yOffset = (1f - easeOut) * 20f; // slide up by 20px
            }
            // Fade out near end of duration
            else if (age > bubble.Duration - 1f)
            {
                alpha = 1f - (age - (bubble.Duration - 1f));
            }

            // Scale based on distance
            var scale = Mathf.Lerp(1f, 0.5f, distance / _maxBubbleDistance);
            bubbleWidth *= scale;
            bubbleHeight *= scale;

            var bubbleRect = new Rect(
                screenPos.x - bubbleWidth / 2,
                (screenPos.y - bubbleHeight) + yOffset,
                bubbleWidth,
                bubbleHeight
            );

            // Store screen rect for click detection
            bubble.ScreenRect = bubbleRect;

            // Draw bubble background
            var style = bubble.IsMarried ? _marriedBubbleStyle : _bubbleStyle;
            var prevColor = GUI.color;
            GUI.color = new Color(1, 1, 1, alpha);
            GUI.Box(bubbleRect, "", style);

            // Draw name with relationship icon
            var nameText = bubble.NPCName;
            if (!string.IsNullOrEmpty(bubble.RelationshipIcon))
            {
                nameText = $"{bubble.RelationshipIcon} {nameText}";
            }

            GUI.Label(
                new Rect(bubbleRect.x, bubbleRect.y + 10 * scale, bubbleRect.width, 20 * scale),
                nameText,
                new GUIStyle(_nameStyle) { fontSize = Mathf.RoundToInt(11 * scale) }
            );

            // Draw message
            GUI.Label(
                new Rect(bubbleRect.x + 15 * scale, bubbleRect.y + 28 * scale, 
                    bubbleRect.width - 30 * scale, bubbleRect.height - 45 * scale),
                bubble.Message,
                new GUIStyle(_bubbleStyle) { fontSize = Mathf.RoundToInt(13 * scale) }
            );

            GUI.color = prevColor;
        }

        /// <summary>
        /// Checks if a bubble was clicked (for interaction).
        /// </summary>
        public bool IsBubbleClicked(Vector2 mousePosition, out string npcId)
        {
            npcId = null;
            
            foreach (var bubble in _activeBubbles.Values)
            {
                if (bubble.ScreenRect.Contains(mousePosition))
                {
                    npcId = bubble.NPCId;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Clears all bubbles.
        /// </summary>
        public void ClearAll()
        {
            _activeBubbles.Clear();
            _npcToBubble.Clear();
        }

        private void RemoveBubble(string bubbleId)
        {
            if (_activeBubbles.TryGetValue(bubbleId, out var bubble))
            {
                _npcToBubble.Remove(bubble.NPCId);
                _activeBubbles.Remove(bubbleId);
            }
        }

        /// <summary>
        /// Gets the current bubble message for an NPC (if any).
        /// </summary>
        public string GetCurrentMessage(string npcId)
        {
            if (_npcToBubble.TryGetValue(npcId, out var bubble))
            {
                return bubble.Message;
            }
            return null;
        }
    }

    public class ChatBubble
    {
        public string Id { get; set; }
        public string NPCId { get; set; }
        public string NPCName { get; set; }
        public string Message { get; set; }
        public Vector3 WorldPosition { get; set; }
        public Rect ScreenRect { get; set; }
        public float CreatedAt { get; set; }
        public float Duration { get; set; }
        public bool IsMarried { get; set; }
        public string RelationshipIcon { get; set; }
    }
}
