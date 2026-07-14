using System;
using System.Collections.Generic;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// GAME-TRUTH BRIDGE + HEARTBEAT (goal: close the AI-Influence doc gap).
    /// Reconciliation 2026-07-12 found the missing link between layers: the
    /// dashboard carries FULL P3-P10 backends (diplomacy rounds, romance
    /// decay, disease ticks, death records, combat incidents) behind REST
    /// routes — but NOTHING DRIVES THEM and the game never feeds them truth.
    /// The mod already owns real-time cadence (Plugin/ColonyBuilder ticks) and
    /// the POST channel (MemoryManager.HttpPostAsync), so the mod becomes:
    ///   1. the HEARTBEAT — diplomacy rounds every 20 real minutes; romance
    ///      decay + disease season-tick hourly
    ///   2. the TRUTH FEED — real season from WorldTimeManager (reflection);
    ///      deaths are fed by DeathChronicler (facts before stories)
    /// Fire-and-forget posts; the dashboard is the state owner. All cadences
    /// real-time (game pauses must not starve politics — doc 03's "rounds…
    /// at a believable pace").
    /// </summary>
    public static class GameTruthBridge
    {
        public static string LastResult = "(idle)";
        private const float DiplomacyRoundMinutes = 20f;
        private const float SlowTickMinutes = 60f;   // romance decay + disease tick
        private static float _nextDiplomacy = 0f;
        private static float _nextSlow = 0f;

        public static void Reset() { _nextDiplomacy = 0f; _nextSlow = 0f; _factionsSeeded = false; _knownFactionNames.Clear(); LastResult = "(idle)"; }

        public static void Tick()
        {
            try
            {
                var mem = LLMNPCsPlugin.Instance?.MemoryManager;
                if (mem == null || string.IsNullOrEmpty(mem.ActiveSaveId)) return;
                float now = UnityEngine.Time.realtimeSinceStartup;

                SeedFactionsOnce();   // roster before rounds — rounds need players

                if (now >= _nextDiplomacy)
                {
                    _nextDiplomacy = now + DiplomacyRoundMinutes * 60f;
                    mem.HttpPostAsync("/api/diplomacy/round",
                        new Dictionary<string, object> { { "save_id", mem.ActiveSaveId } });
                    LastResult = $"diplomacy round requested {DateTime.Now:HH:mm:ss}";
                    LLMNPCsPlugin.LogToFile("[GameTruthBridge] " + LastResult);
                }

                if (now >= _nextSlow)
                {
                    _nextSlow = now + SlowTickMinutes * 60f;
                    string season = ReadSeason();
                    mem.HttpPostAsync("/api/romance/tick",   // full pass: decay + autonomous bonds + milestones
                        new Dictionary<string, object> { { "save_id", mem.ActiveSaveId } });
                    mem.HttpPostAsync("/api/disease/tick",
                        new Dictionary<string, object> {
                            { "save_id", mem.ActiveSaveId }, { "season", season } });
                    mem.HttpPostAsync("/api/events/evolve",   // events age: evolve → resolve → expire
                        new Dictionary<string, object> { { "save_id", mem.ActiveSaveId } });
                    LLMNPCsPlugin.LogToFile($"[GameTruthBridge] slow tick: romance decay + disease tick (season={season})");
                }
            }
            catch (Exception ex)
            {
                LastResult = "bridge EXC: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        /// <summary>FACTION ROSTER FEED (Chronicle Test Gate 2): read the game's
        /// real faction roster + player friendliness via reflection
        /// (WorldMap.Data.FactionInstances — decompile-grounded) and seed the
        /// diplomacy engine. Once per session, from the bridge tick.</summary>
        private static bool _factionsSeeded;
        // Roster names cached at seed time — the ONLY source raid attribution
        // may match against (law: never act on guessed ids).
        private static readonly List<string> _knownFactionNames = new List<string>();

        public static void SeedFactionsOnce()
        {
            if (_factionsSeeded) return;
            try
            {
                var mem = LLMNPCsPlugin.Instance?.MemoryManager;
                if (mem == null || string.IsNullOrEmpty(mem.ActiveSaveId)) return;
                Type wmT = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                { try { wmT = a.GetType("NSMedieval.WorldMap.WorldMap", false); if (wmT != null) break; } catch { } }
                if (wmT == null) return;
                var wm = UnityEngine.Object.FindObjectOfType(wmT);
                if (wm == null) return;   // not ready yet — retry next tick
                var data = wmT.GetField("Data")?.GetValue(wm)
                           ?? wmT.GetProperty("Data")?.GetValue(wm, null);
                var list = data?.GetType().GetProperty("FactionInstances")?.GetValue(data, null)
                           as System.Collections.IEnumerable;
                if (list == null) return;
                var factions = new List<object>();
                foreach (var fi in list)
                {
                    if (fi == null) continue;
                    var name = fi.GetType().GetProperty("NameLocalized")?.GetValue(fi, null) as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    float friendliness = 50f;
                    try { friendliness = Convert.ToSingle(fi.GetType().GetProperty("PlayerFriendliness")?.GetValue(fi, null) ?? 50f); } catch { }
                    factions.Add(new Dictionary<string, object> { { "name", name }, { "friendliness", friendliness } });
                    if (!_knownFactionNames.Contains(name)) _knownFactionNames.Add(name);
                }
                if (factions.Count == 0) return;
                mem.HttpPostAsync("/api/diplomacy/seed", new Dictionary<string, object>
                {
                    { "save_id", mem.ActiveSaveId },
                    { "player_faction", mem.ActiveSaveId },
                    { "factions", factions },
                });
                _factionsSeeded = true;
                LLMNPCsPlugin.LogToFile($"[GameTruthBridge] faction roster seeded: {factions.Count} factions posted to diplomacy");
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile("[GameTruthBridge] SeedFactions EXC: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        /// <summary>GATE 3 LINK (doc-11 scenario 3, "the raid remembered"): a real
        /// in-game raid event feeds AI Diplomacy. Called by EventInteractor at
        /// EVENT DETECTED with the dialog title+body. Classification = the word
        /// "raid" in the text; attribution = EXACTLY ONE seeded faction name
        /// appearing verbatim (roster-matched, never guessed — animal raids name
        /// no faction and fall out naturally). Escalation, war declarations,
        /// proclamations and rumor propagation all run downstream in gm_systems
        /// (report_raid, offline-tested).</summary>
        public static void ReportRaidIfRaid(string title, string body)
        {
            try
            {
                string text = (title ?? "") + " " + (body ?? "");
                if (text.IndexOf("raid", StringComparison.OrdinalIgnoreCase) < 0) return;
                var mem = LLMNPCsPlugin.Instance?.MemoryManager;
                if (mem == null || string.IsNullOrEmpty(mem.ActiveSaveId)) return;
                var hits = new List<string>();
                foreach (var n in _knownFactionNames)
                    if (text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(n);
                if (hits.Count != 1)
                {
                    LLMNPCsPlugin.LogToFile($"[GameTruthBridge] raid text detected but raider not attributable ({hits.Count} roster matches) — diplomacy feed skipped honestly");
                    return;
                }
                mem.HttpPostAsync("/api/diplomacy/raid", new Dictionary<string, object>
                {
                    { "save_id", mem.ActiveSaveId },
                    { "raider", hits[0] },
                    { "target", mem.ActiveSaveId },
                });
                LLMNPCsPlugin.LogToFile($"[GameTruthBridge] RAID reported to diplomacy: {hits[0]} raids {mem.ActiveSaveId} (Gate 3 chain link 1→2)");
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile("[GameTruthBridge] ReportRaid EXC: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        /// <summary>Real death → P8 facts record (cause included). Called by
        /// DeathChronicler the moment a death is detected — BEFORE any story
        /// is written, so the record exists even when the LLM budget is dry.</summary>
        public static void ReportDeath(string settlerId, string cause)
        {
            try
            {
                var mem = LLMNPCsPlugin.Instance?.MemoryManager;
                if (mem == null || string.IsNullOrEmpty(mem.ActiveSaveId)) return;
                mem.HttpPostAsync("/api/death/record", new Dictionary<string, object>
                {
                    { "save_id", mem.ActiveSaveId },
                    { "settler_id", settlerId },
                    { "cause", cause ?? "" },
                });
                LLMNPCsPlugin.LogToFile($"[GameTruthBridge] death record posted for {settlerId} (cause: {cause})");
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile("[GameTruthBridge] ReportDeath EXC: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        /// <summary>Current in-game season via WorldTimeManager reflection.
        /// Falls back to "winter" (the harshest disease odds — fail-safe).</summary>
        private static string ReadSeason()
        {
            try
            {
                Type t = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                { try { t = a.GetType("NSMedieval.Manager.WorldTimeManager", false); if (t != null) break; } catch { } }
                if (t == null)
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    { try { foreach (var ty in a.GetTypes()) if (ty.Name == "WorldTimeManager") { t = ty; break; } } catch { } if (t != null) break; }
                if (t == null) return "winter";
                var inst = UnityEngine.Object.FindObjectOfType(t);
                if (inst == null) return "winter";
                foreach (var name in new[] { "CurrentSeason", "Season", "season" })
                {
                    var p = t.GetProperty(name);
                    var v = p?.GetValue(inst, null) ?? t.GetField(name)?.GetValue(inst);
                    if (v != null) return v.ToString().ToLowerInvariant();
                }
            }
            catch { }
            return "winter";
        }
    }
}
