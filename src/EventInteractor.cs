using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// EVENT INTERACTOR (#34, Ken: "the game injects NPCs and events... the
    /// engine needs to either stop these events or interact with them").
    ///
    /// Cockhamsted died for the lack of this: the Donald/Disciples event ("he
    /// may be pursued") was blind-clicked through while the colony had zero
    /// weapons and zero food. These dialogs are THE GAME TALKING TO THE PLAYER,
    /// and the LLM is the player.
    ///
    /// Ground truth (validation/event_api.txt + decompiles):
    ///   READ   GameEventSystem (MonoSingleton).RunningEvents : List&lt;GameEventInstance&gt;
    ///          GameEventUtil.BuildDialogContent(instance, dialogIndex) →
    ///          DialogContent { WindowTitle, ContentTitle, ContentBodyText,
    ///                          Options: List&lt;DialogOption&gt; { Text } }
    ///   APPLY  GameEventSystemController (MonoSingleton).EventOptionChosen(
    ///          GameEventInstance, Int32 optionIndex) — the delegate the
    ///          branching phase listens on (the UI merely raises this).
    ///
    /// Discipline: the LLM decides or NOBODY does — no guessed defaults, no
    /// blind clicks (that's what killed the last colony). Decision runs async;
    /// the choice is APPLIED on the next main-thread tick.
    /// </summary>
    public static class EventInteractor
    {
        public static string LastResult = "(no events)";

        private sealed class Pending
        {
            public object Instance;
            public string Title, Body;
            public List<string> Options = new List<string>();
            public int DialogCount;              // blueprint dialogs found (diagnosis)
            public int ApplyAttempts;            // give up after 3 (no dialog phase)
            public DateTime RetryAfter = DateTime.MinValue;   // budget-deferred decision retry
            public object AppliedPhase;          // phase we answered — a NEW phase means a NEW dialog
            public int ChosenIndex = -1;         // set by the async decision
            public string Reason = "";
            public bool DecisionRequested, Applied, Failed;
        }

        // key = instance hash — one decision per event instance
        private static readonly Dictionary<int, Pending> _seen = new Dictionary<int, Pending>();

        public static void Reset() { _seen.Clear(); LastResult = "(no events)"; }

        // REAL-TIME PUMP (2026-07-12): a BLOCKING event dialog pauses GAME time,
        // and this module's normal tick rides ColonyBuilder (game-time) — the
        // same deadlock shape the recap screen had. Plugin.Update calls this on
        // REAL time; Tick()'s per-event state guards (DecisionRequested/Applied/
        // RetryAfter) make the double-driving safe and idempotent.
        private static float _nextRtTick;
        public static void RealTimePump()
        {
            if (UnityEngine.Time.realtimeSinceStartup < _nextRtTick) return;
            _nextRtTick = UnityEngine.Time.realtimeSinceStartup + 5f;
            Tick();
        }

        public static void Tick()
        {
            try
            {
                var ges = Singleton("GameEventSystem");
                if (ges == null) { LastResult = "(event system not ready)"; return; }
                var running = ges.GetType().GetProperty("RunningEvents")?.GetValue(ges, null) as IEnumerable;
                if (running == null) { LastResult = "(no RunningEvents)"; return; }

                int live = 0; var lines = new List<string>();
                foreach (var inst in running)
                {
                    if (inst == null) continue;
                    bool ended = inst.GetType().GetProperty("HasEnded")?.GetValue(inst, null) is bool e && e;
                    if (ended) continue;
                    live++;
                    int key = inst.GetHashCode();
                    if (!_seen.TryGetValue(key, out var p))
                    {
                        p = ReadEvent(inst);
                        _seen[key] = p;
                        LLMNPCsPlugin.LogToFile($"[EventInteractor] EVENT DETECTED '{p.Title}' dialogs={p.DialogCount} options=[{string.Join(" | ", p.Options)}] body: {Trunc(p.Body, 300)}");
                        ReportWorldEvent(p, decided: false);   // P5: settlers now KNOW this happened
                        GameTruthBridge.ReportRaidIfRaid(p.Title, p.Body);   // Gate 3: raids feed diplomacy
                    }

                    // ANSWERABILITY GATE (coherence, 2026-07-11): decide ONLY
                    // when the event's CURRENT phase can receive an answer
                    // (has OnClose(int) — e.g. ShowDialogPhaseBranching).
                    // News/ambient events (TraderVisitPhase, AnimalVisitPhase)
                    // carry "options" that are mere UI conveniences (OK / Jump
                    // to Location) — the polecat raid burned a critical-lane
                    // LLM call choosing between OK and a camera pan. A player
                    // wouldn't deliberate there; neither does the leader.
                    // Re-checked every tick: multi-phase events can move INTO
                    // a dialog phase later.
                    bool answerable = HasAnswerablePhase(p.Instance);

                    // MULTI-DIALOG EVENTS (raid, live 23:35: 'take arms' dialog
                    // answered → the AFTERMATH dialog appeared and sat as a
                    // zombie because decisions were keyed per event INSTANCE).
                    // A new answerable phase after an apply = a NEW dialog:
                    // re-read content, re-arm the decision machinery, and the
                    // fresh content becomes colony lore.
                    if (p.Applied && answerable)
                    {
                        var curPhase = CurrentPhaseOf(p.Instance);
                        if (curPhase != null && !ReferenceEquals(curPhase, p.AppliedPhase))
                        {
                            var fresh = ReadEvent(inst);
                            p.Title = fresh.Title; p.Body = fresh.Body;
                            p.Options = fresh.Options; p.DialogCount = fresh.DialogCount;
                            p.Applied = false; p.DecisionRequested = false; p.Failed = false;
                            p.ChosenIndex = -1; p.ApplyAttempts = 0; p.Reason = "";
                            LLMNPCsPlugin.LogToFile($"[EventInteractor] NEW DIALOG on '{p.Title}' (phase advanced) — re-armed, options=[{string.Join(" | ", p.Options)}]");
                            ReportWorldEvent(p, decided: false);   // aftermath text is lore too
                        }
                    }

                    if (answerable && p.Options.Count >= 2 && !p.DecisionRequested && !p.Applied
                        && DateTime.UtcNow >= p.RetryAfter)
                    {
                        p.DecisionRequested = true;
                        _ = DecideAsync(p);   // async; applied next tick on main thread
                    }
                    else if (answerable && p.Options.Count == 1 && p.ChosenIndex < 0 && !p.Applied && !p.Failed)
                    {
                        // A sole option is not a choice — acknowledging it is
                        // deterministic, not a blind click (the ban is on
                        // GUESSING among alternatives).
                        p.ChosenIndex = 0;
                        p.Reason = "sole option — deterministic acknowledge";
                    }
                    // APPLY on the main thread once the decision has landed.
                    if (p.ChosenIndex >= 0 && !p.Applied && !p.Failed)
                    {
                        p.ApplyAttempts++;
                        p.Applied = ApplyChoice(p);
                        if (p.Applied)
                        {
                            LLMNPCsPlugin.LogToFile($"[EventInteractor] CHOSE option {p.ChosenIndex} ('{At(p.Options, p.ChosenIndex)}') on '{p.Title}' — {p.Reason} | applied={p.Applied}");
                            ReportWorldEvent(p, decided: true);   // P5: the DECISION becomes colony lore
                        }
                        else if (p.ApplyAttempts >= 3)
                        {
                            p.Failed = true;
                            p.Reason = "no dialog phase awaiting an answer";
                            LLMNPCsPlugin.LogToFile($"[EventInteractor] GIVING UP applying option {p.ChosenIndex} on '{p.Title}' after {p.ApplyAttempts} attempts — {p.Reason}");
                        }
                    }

                    lines.Add($"'{p.Title}' opts={p.Options.Count} " +
                              (p.Applied ? $"CHOSE {p.ChosenIndex}:'{At(p.Options, p.ChosenIndex)}'"
                              : p.Failed ? $"NEEDS PLAYER ({(string.IsNullOrEmpty(p.Reason) ? "LLM unavailable — no blind default" : p.Reason)})"
                              : p.ChosenIndex >= 0 ? "applying…"
                              : p.DecisionRequested ? "deciding…"
                              : p.RetryAfter > DateTime.UtcNow ? $"deferred (budget) — retry {p.RetryAfter:HH:mm}"
                              : answerable ? "awaiting decision" : "news (no answer needed)"));
                }
                LastResult = live == 0 ? "(no events)" : $"{live} running: " + string.Join(" ; ", lines);
            }
            catch (Exception ex) { LastResult = "events EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }

        private static Pending ReadEvent(object inst)
        {
            var p = new Pending { Instance = inst, Title = "(unknown)", Body = "" };
            try
            {
                // Ground truth v2: an event's dialogs live on its BLUEPRINT
                // (GameEvent.Dialogs, List<DialogContent>); index 0 is NOT
                // guaranteed to be the choice dialog (Llangefni's event read
                // "(untitled) opts=0" from a blind index-0 read). Name the
                // blueprint and walk EVERY dialog; prefer the first with options.
                var bp = inst.GetType().GetProperty("Blueprint")?.GetValue(inst, null);
                string bpId = null;
                try { bpId = bp?.GetType().GetMethod("GetID")?.Invoke(bp, null) as string; } catch { }
                if (!string.IsNullOrEmpty(bpId)) p.Title = bpId;   // fallback title = event id

                int dialogCount = 1;
                try
                {
                    object dialogs = null;
                    for (var t = bp?.GetType(); t != null && dialogs == null; t = t.BaseType)
                    {
                        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                        dialogs = t.GetProperty("Dialogs", F)?.GetValue(bp, null)
                                  ?? t.GetField("Dialogs", F)?.GetValue(bp)
                                  ?? t.GetField("dialogs", F)?.GetValue(bp);
                    }
                    if (dialogs is ICollection col) dialogCount = Math.Max(1, col.Count);
                }
                catch { }
                p.DialogCount = dialogCount;

                var utilT = FindTypeByName("GameEventUtil");
                var build = FirstStatic(utilT, "BuildDialogContent", 2);
                for (int i = 0; i < dialogCount && i < 8; i++)
                {
                    object dc;
                    try { dc = build?.Invoke(null, new[] { inst, (object)i }); } catch { continue; }
                    if (dc == null) continue;
                    // Ground truth (decompiled NSMedieval.Dialogs.Data.DialogContent):
                    // WindowTitle/ContentTitle/ContentBodyText/Options and
                    // DialogOption.Text are public FIELDS, not properties — a
                    // GetProperty-only read returns null for ALL of them (that
                    // is why the trader visit read as blank on 2026-07-11).
                    string title = StripTags((Member(dc, "WindowTitle") as string) is string wt && !string.IsNullOrEmpty(wt)
                                   ? wt : Member(dc, "ContentTitle") as string);
                    string body = StripTags(Member(dc, "ContentBodyText") as string ?? "");
                    var opts = new List<string>();
                    if (Member(dc, "Options") is IEnumerable oe)
                        foreach (var o in oe)
                        {
                            var txt = StripTags(Member(o, "Text") as string);
                            if (!string.IsNullOrEmpty(txt)) opts.Add(txt);
                        }
                    bool better = opts.Count > p.Options.Count ||
                                  (p.Options.Count == 0 && !string.IsNullOrEmpty(body) && string.IsNullOrEmpty(p.Body));
                    if (better || i == 0)
                    {
                        if (!string.IsNullOrEmpty(title)) p.Title = $"{title}" + (bpId != null ? $" [{bpId}]" : "");
                        if (!string.IsNullOrEmpty(body)) p.Body = body;
                        if (opts.Count > p.Options.Count) p.Options = opts;
                    }
                    if (p.Options.Count >= 2) break;   // found the choice dialog
                }
            }
            catch (Exception ex) { LLMNPCsPlugin.LogToFile("[EventInteractor] read EXC: " + (ex.InnerException?.Message ?? ex.Message)); }
            return p;
        }

        private static async Task DecideAsync(Pending p)
        {
            try
            {
                var client = LLMNPCsPlugin.Instance?.LLMClient;
                if (client == null) { p.Failed = true; return; }
                string opts = "";
                for (int i = 0; i < p.Options.Count; i++) opts += $"\n  {i}: {p.Options[i]}";
                string prompt =
$@"You are the leader of a small medieval colony. The following event demands YOUR decision as the player.

EVENT: {p.Title}
{Trunc(p.Body, 600)}

OPTIONS:{opts}

COLONY REALITY (weigh it hard — a colony with no weapons cannot fight, a colony with no food cannot feed guests):
{ColonyAlerts.Current}

Respond ONLY with JSON: {{""option"": <index>, ""reason"": ""one sentence""}}";
                var messages = new List<Message>
                {
                    new Message { Role = "system", Content = "You are a pragmatic medieval colony leader. Survival first. Output only the JSON object." },
                    new Message { Role = "user", Content = prompt }
                };
                string raw = await client.GetRawResponseAsync(messages,
                    new LLMTraceMetadata { FlowType = PromptFlowTypes.ColonyAdvisor, SenderName = "EventInteractor" }, task: "story_event");
                if (string.IsNullOrWhiteSpace(raw))
                {
                    // Budget/offline: DEFER, don't die — a blocking event (e.g.
                    // a hungry recruit at the gate) persists until answered; a
                    // sticky Failed left the 21:31 recruitment event permanently
                    // unanswered after one exhausted-window attempt. Retry when
                    // the budget window has likely rolled. Never guess meanwhile.
                    p.DecisionRequested = false;
                    p.RetryAfter = DateTime.UtcNow.AddMinutes(10);
                    p.Reason = "LLM budget exhausted — retrying when the window rolls";
                    LLMNPCsPlugin.LogToFile($"[EventInteractor] decision DEFERRED (budget) for '{p.Title}' — retry after {p.RetryAfter:HH:mm:ss}");
                    return;
                }
                int idx = ParseOption(raw, out string reason);
                if (idx < 0 || idx >= p.Options.Count) { p.Failed = true; return; }
                p.Reason = reason;
                p.ChosenIndex = idx;   // applied on next main-thread tick
            }
            catch (Exception ex) { p.Failed = true; LLMNPCsPlugin.LogToFile("[EventInteractor] decide EXC: " + ex.Message); }
        }

        private static object CurrentPhaseOf(object inst)
        {
            try { return GetFieldValue(GetFieldValue(inst, "stateMachine"), "currentPhase"); }
            catch { return null; }
        }

        /// <summary>The event's CURRENT phase can receive an answer (OnClose(int)
        /// exists — dialog phases only). News/visit phases return false.</summary>
        private static bool HasAnswerablePhase(object inst)
        {
            try
            {
                var phase = CurrentPhaseOf(inst);
                return phase != null && FindMethod(phase.GetType(), "OnClose", typeof(int)) != null;
            }
            catch { return false; }
        }

        private static bool ApplyChoice(Pending p)
        {
            try
            {
                // Ground truth (decompiled ShowDialogPhaseBranching): the UI
                // registers a choice by calling the CURRENT PHASE's
                // OnClose(selectedOptionIndex), which sets
                // switchPhaseIndexNextTick — the next Tick advances into
                // choiceDestinationPhases[chosen]. OnClose itself fires the
                // GameEventSystemController.EventOptionChosen notification
                // (whose int is the DIALOG index, NOT the option — calling
                // that relay directly, as v1 did, applies NOTHING).
                var sm = GetFieldValue(p.Instance, "stateMachine");
                var phase = sm == null ? null : GetFieldValue(sm, "currentPhase");
                if (phase == null)
                {
                    LLMNPCsPlugin.LogToFile("[EventInteractor] apply: no current phase on the event — nothing awaiting an answer");
                    return false;
                }
                var onClose = FindMethod(phase.GetType(), "OnClose", typeof(int));
                if (onClose == null)
                {
                    LLMNPCsPlugin.LogToFile($"[EventInteractor] apply: current phase {phase.GetType().Name} has no OnClose(int) — no dialog awaiting an answer");
                    return false;
                }
                onClose.Invoke(phase, new object[] { p.ChosenIndex });
                p.AppliedPhase = phase;   // a DIFFERENT phase later = a new dialog (re-arm)
                LLMNPCsPlugin.LogToFile($"[EventInteractor] apply: {phase.GetType().Name}.OnClose({p.ChosenIndex}) invoked — the game's own choice path");
                // EXPERIENCE fix (zombie window, seen live 18:56 — Lee refused
                // in STATE but the NEW SETTLER dialog stayed on SCREEN): the
                // headless OnClose advances the phase but only a UI click tears
                // down the window. CloseSilent() is the game's own no-event
                // teardown — close the view so the player SEES the decision.
                try
                {
                    var dvm = Singleton("DialogViewManager");
                    var view = dvm == null ? null : GetFieldValue(dvm, "view");
                    if (view != null)
                    {
                        dvm.GetType().GetMethod("CloseSilent", BindingFlags.Public | BindingFlags.Instance)?.Invoke(dvm, null);
                        LLMNPCsPlugin.LogToFile("[EventInteractor] dialog view closed (CloseSilent)");
                    }
                }
                catch (Exception vex) { LLMNPCsPlugin.LogToFile("[EventInteractor] view close: " + (vex.InnerException?.Message ?? vex.Message)); }
                return true;
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile("[EventInteractor] apply EXC: " + (ex.InnerException?.Message ?? ex.Message));
                return false;
            }
        }

        private static object GetFieldValue(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        private static MethodInfo FindMethod(Type t, string name, params Type[] args)
        {
            for (; t != null; t = t.BaseType)
            {
                var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, args, null);
                if (m != null) return m;
            }
            return null;
        }

        private static int ParseOption(string raw, out string reason)
        {
            reason = "";
            try
            {
                int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
                if (a < 0 || b <= a) return -1;
                var obj = Newtonsoft.Json.Linq.JObject.Parse(raw.Substring(a, b - a + 1));
                reason = obj.Value<string>("reason") ?? "";
                return obj.Value<int?>("option") ?? -1;
            }
            catch { return -1; }
        }

        /// <summary>Read a public property OR field (game data classes use bare public fields).</summary>
        private static object Member(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(o, null);
            return t.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);
        }

        // ── P5 slice 1: lived game events become WORLD EVENTS the settlers
        // know and can discuss (doc 02). Fire-and-forget HTTP (PlanManager
        // pattern); create → propagate to ALL profiled settlers with
        // rumor_state "knows" (they lived it). Server: gm_systems /api/events.
        private static readonly System.Net.Http.HttpClient _http =
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(3000) };

        private static void ReportWorldEvent(Pending p, bool decided)
        {
            try
            {
                string desc = Trunc(p.Body, 400);
                if (decided)
                    desc += $" — The colony decided: \"{At(p.Options, p.ChosenIndex)}\" ({p.Reason})";
                string title = (decided ? "" : "News: ") + p.Title;
                var body = "{\"save_id\":" + J(MemoryManager.GetActiveSaveId() ?? "unknown")
                         + ",\"event_type\":\"social\",\"title\":" + J(Trunc(title, 120))
                         + ",\"description\":" + J(desc)
                         + ",\"origin_entity\":\"Dowsby (lived event)\",\"confidence\":1.0}";
                _http.PostAsync("http://127.0.0.1:8714/api/events/create",
                        new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"))
                    .ContinueWith(t =>
                    {
                        try
                        {
                            if (!t.IsCompleted || t.IsFaulted || !t.Result.IsSuccessStatusCode) return;
                            var resp = t.Result.Content.ReadAsStringAsync().Result;
                            var id = Newtonsoft.Json.Linq.JObject.Parse(resp).Value<long?>("event_id");
                            if (id == null) return;
                            var prop = "{\"save_id\":" + J(MemoryManager.GetActiveSaveId() ?? "unknown")
                                     + ",\"event_id\":" + id + ",\"rumor_state\":\"knows\"}";
                            _http.PostAsync("http://127.0.0.1:8714/api/events/propagate",
                                new System.Net.Http.StringContent(prop, System.Text.Encoding.UTF8, "application/json"));
                            LLMNPCsPlugin.LogToFile($"[EventInteractor] world event #{id} recorded+propagated: {Trunc(title, 80)}");
                        }
                        catch { }
                    });
            }
            catch { }
        }

        private static string J(string s) =>
            "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ") + "\"";

        private static string At(List<string> l, int i) => i >= 0 && i < l.Count ? l[i] : "?";
        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";

        /// <summary>Remove TMP rich-text markup (&lt;style=…&gt;, &lt;color=…&gt;, …) —
        /// event bodies carry it and it pollutes LLM prompts and logs.</summary>
        private static string StripTags(string s) =>
            string.IsNullOrEmpty(s) ? s : System.Text.RegularExpressions.Regex.Replace(s, "<[^<>]{1,60}>", "");

        // POISON-PROOF singleton access (root cause of the 13-load wedge streak,
        // 2026-07-12): MonoSingleton&lt;T&gt;.Instance caches instanceInitialized=true
        // even when the scene object doesn't exist yet (main menu / load screen).
        // After one early access, the REAL instance self-destructs in Awake
        // (delete flag) and every consumer NREs — AudioEventsHandler's NRE then
        // wedges the whole load at "Placing objects". RealTimePump made this
        // helper run at the menu; probe IsInstantiated() (reads the field, never
        // caches) and only then touch the getter.
        private static object Singleton(string typeName)
        {
            var t = FindTypeByName(typeName);
            if (t == null) return null;
            var isInst = t.GetMethod("IsInstantiated",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (isInst != null && !(bool)isInst.Invoke(null, null)) return null;
            return t.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
        }

        private static MethodInfo FirstStatic(Type t, string name, int argc)
        {
            if (t == null) return null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == name && m.GetParameters().Length == argc && m.GetParameters()[1].ParameterType == typeof(int)) return m;
            return null;
        }

        private static Type FindTypeByName(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }
    }
}
