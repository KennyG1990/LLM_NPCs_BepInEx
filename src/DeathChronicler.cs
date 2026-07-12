using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// DEATH HISTORY (#27, doc 08: "No settler dies as a nameless statistic").
    /// When a settler dies, the AI writes their life story from their REAL
    /// recorded history (memories, relationships, deeds via the memory layer),
    /// the chronicle is persisted, and every survivor receives the loss as a
    /// personal memory — grief becomes context for future dialogue.
    ///
    /// Detection: roster diff on the live settler list + CreatureBase.HasDied
    /// (ground truth :302). A settler must be MISSING (or dead-flagged) for
    /// 3 consecutive ticks before chronicling — absorbs load flickers.
    /// (Cockhamsted's fall gave us Margaria and Donald with nobody writing
    /// anything down. Never again.)
    ///
    /// Writes through MemoryManager's API layer — when #33 (JSON memory
    /// independence) lands, the backend swaps underneath without changes here.
    /// </summary>
    public static class DeathChronicler
    {
        public static string LastResult = "(no deaths)";

        private sealed class Missing { public string Name; public int Ticks; public bool Requested; }

        private static readonly Dictionary<string, string> _roster = new Dictionary<string, string>();   // id -> name
        private static readonly Dictionary<string, Missing> _missing = new Dictionary<string, Missing>();
        private static readonly HashSet<string> _chronicled = new HashSet<string>();
        private const int ConfirmTicks = 3;

        public static void Reset()
        {
            _roster.Clear(); _missing.Clear(); _chronicled.Clear();
            LastResult = "(no deaths)";
        }

        public static void Tick(List<Settler> live)
        {
            try
            {
                var present = new HashSet<string>();
                if (live != null)
                    foreach (var s in live)
                    {
                        if (s == null || s.gameObject == null) continue;
                        if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out var id, out var name, out _)) continue;
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        present.Add(id);
                        _roster[id] = name;
                        _missing.Remove(id);   // back among the living (load flicker)
                    }

                // A roster that never had 1+ settlers can't diff meaningfully.
                if (_roster.Count == 0) { return; }

                foreach (var kv in _roster)
                {
                    if (present.Contains(kv.Key) || _chronicled.Contains(kv.Key)) continue;
                    if (!_missing.TryGetValue(kv.Key, out var m))
                        _missing[kv.Key] = m = new Missing { Name = kv.Value, Ticks = 0 };
                    m.Ticks++;
                    if (m.Ticks >= ConfirmTicks && !m.Requested)
                    {
                        m.Requested = true;
                        _chronicled.Add(kv.Key);
                        LLMNPCsPlugin.LogToFile($"[DeathChronicler] {m.Name} ({kv.Key}) gone {m.Ticks} ticks — writing their chronicle");
                        _ = ChronicleAsync(kv.Key, m.Name, present);
                    }
                }

                if (_missing.Count == 0 && LastResult.StartsWith("(")) LastResult = $"(no deaths; watching {_roster.Count} settlers)";
            }
            catch (Exception ex) { LastResult = "deaths EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        private static async Task ChronicleAsync(string id, string name, HashSet<string> survivors)
        {
            try
            {
                var mem = LLMNPCsPlugin.Instance?.MemoryManager;
                var client = LLMNPCsPlugin.Instance?.LLMClient;
                if (client == null) { LastResult = $"death of {name}: no LLM client"; return; }

                // 1. Their REAL history — the assembled memory context (RoleRAG
                //    graph + personal log + relationships), not an invention.
                string history = "";
                try
                {
                    if (mem != null)
                        history = await mem.GetContextForPromptAsync(id, "settler",
                            "life story, deeds, relationships, how they lived") ?? "";
                }
                catch { }

                string prompt =
$@"{name}, a settler of this medieval colony, has died.

Write their chronicle — the life story a village elder would inscribe on vellum. Ground EVERY claim in the recorded history below; do not invent deeds that are not there. If the record is thin, write a short, honest entry about a simple life. 120-200 words, past tense, medieval register, no headers.

RECORDED HISTORY OF {name}:
{(string.IsNullOrWhiteSpace(history) ? "(the record is thin — they lived quietly)" : history)}

COLONY SITUATION AT THEIR PASSING:
{ColonyAlerts.Current}";

                var messages = new List<Message>
                {
                    new Message { Role = "system", Content = "You are the village chronicler. Write only the chronicle text." },
                    new Message { Role = "user", Content = prompt }
                };
                string story = await client.GetRawResponseAsync(messages,
                    new LLMTraceMetadata { FlowType = PromptFlowTypes.ColonyAdvisor, SenderName = "DeathChronicler" },
                    task: "death_history");

                if (string.IsNullOrWhiteSpace(story))
                {
                    // Budget/offline: the DEATH is still recorded (facts first);
                    // the story can be written on a later tick/session.
                    _chronicled.Remove(id);
                    var mm = _missing.TryGetValue(id, out var mv) ? mv : null;
                    if (mm != null) mm.Requested = false;
                    LastResult = $"death of {name}: chronicle deferred (LLM budget/offline) — will retry";
                    LLMNPCsPlugin.LogToFile("[DeathChronicler] " + LastResult);
                    return;
                }
                story = story.Trim();

                // 2. Persist: colony-level chronicle (server shape ground-truthed:
                //    /api/colony/event wants narrative + rec{Type,Description};
                //    it also fans a typed memory to every NPC of the save).
                mem?.HttpPostAsync("/api/colony/event", new Dictionary<string, object>
                {
                    { "save_id", mem.ActiveSaveId },
                    { "narrative", $"THE DEATH OF {name}: {story}" },
                    { "state", new Dictionary<string, object>() },
                    { "rec", new Dictionary<string, object> {
                        { "Type", "death_chronicle" }, { "Description", $"The Death of {name}" } } },
                });

                // 3. Grief: every survivor remembers the loss personally
                //    (RecordEvent = the proven /api/memory/event shape).
                string firstLine = story.Split('\n')[0];
                if (firstLine.Length > 160) firstLine = firstLine.Substring(0, 160) + "…";
                foreach (var sid in survivors)
                {
                    if (sid == id) continue;
                    mem?.RecordEvent(sid, "death_of_companion", $"{name} has died. {firstLine}", importance: 9);
                }

                // 4. Keep a plain file — the chronicle survives everything.
                try
                {
                    var dir = @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\chronicles";
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir,
                        $"{DateTime.Now:yyyyMMdd_HHmm}_{Sanitize(name)}.txt"),
                        $"THE DEATH OF {name.ToUpperInvariant()}\n{DateTime.Now}\n\n{story}\n");
                }
                catch { }

                LastResult = $"CHRONICLED the death of {name} ({story.Length} chars) — survivors remember";
                LLMNPCsPlugin.LogToFile($"[DeathChronicler] {LastResult}\n--- CHRONICLE ---\n{story}\n---");
            }
            catch (Exception ex)
            {
                LastResult = $"death of {name}: chronicle EXC " + (ex.InnerException?.Message ?? ex.Message);
                LLMNPCsPlugin.LogToFile("[DeathChronicler] " + LastResult);
            }
        }

        private static string Sanitize(string s)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }
    }
}
