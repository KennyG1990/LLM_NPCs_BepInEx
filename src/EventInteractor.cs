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
            public int ChosenIndex = -1;         // set by the async decision
            public string Reason = "";
            public bool DecisionRequested, Applied, Failed;
        }

        // key = instance hash — one decision per event instance
        private static readonly Dictionary<int, Pending> _seen = new Dictionary<int, Pending>();

        public static void Reset() { _seen.Clear(); LastResult = "(no events)"; }

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
                        LLMNPCsPlugin.LogToFile($"[EventInteractor] EVENT DETECTED '{p.Title}' options=[{string.Join(" | ", p.Options)}] body: {Trunc(p.Body, 300)}");
                    }

                    if (p.Options.Count >= 2 && !p.DecisionRequested && !p.Applied)
                    {
                        p.DecisionRequested = true;
                        _ = DecideAsync(p);   // async; applied next tick on main thread
                    }
                    // APPLY on the main thread once the decision has landed.
                    if (p.ChosenIndex >= 0 && !p.Applied)
                    {
                        p.Applied = ApplyChoice(p);
                        LLMNPCsPlugin.LogToFile($"[EventInteractor] CHOSE option {p.ChosenIndex} ('{At(p.Options, p.ChosenIndex)}') on '{p.Title}' — {p.Reason} | applied={p.Applied}");
                    }

                    lines.Add($"'{p.Title}' opts={p.Options.Count} " +
                              (p.Applied ? $"CHOSE {p.ChosenIndex}:'{At(p.Options, p.ChosenIndex)}'"
                              : p.Failed ? "LLM unavailable — NEEDS PLAYER (no blind default)"
                              : p.ChosenIndex >= 0 ? "applying…"
                              : p.DecisionRequested ? "deciding…" : "info-only"));
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
                var utilT = FindTypeByName("GameEventUtil");
                var build = utilT?.GetMethod("BuildDialogContent",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { inst.GetType(), typeof(int) }, null)
                    ?? FirstStatic(utilT, "BuildDialogContent", 2);
                var dc = build?.Invoke(null, new[] { inst, (object)0 });
                if (dc == null) return p;
                var dt = dc.GetType();
                p.Title = (dt.GetProperty("WindowTitle") ?? dt.GetProperty("ContentTitle"))?.GetValue(dc, null) as string ?? "(untitled)";
                p.Body = dt.GetProperty("ContentBodyText")?.GetValue(dc, null) as string ?? "";
                if (dt.GetProperty("Options")?.GetValue(dc, null) is IEnumerable opts)
                    foreach (var o in opts)
                    {
                        var txt = o?.GetType().GetProperty("Text")?.GetValue(o, null) as string;
                        if (!string.IsNullOrEmpty(txt)) p.Options.Add(txt);
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
                if (string.IsNullOrWhiteSpace(raw)) { p.Failed = true; return; }   // budget/offline: report, never guess
                int idx = ParseOption(raw, out string reason);
                if (idx < 0 || idx >= p.Options.Count) { p.Failed = true; return; }
                p.Reason = reason;
                p.ChosenIndex = idx;   // applied on next main-thread tick
            }
            catch (Exception ex) { p.Failed = true; LLMNPCsPlugin.LogToFile("[EventInteractor] decide EXC: " + ex.Message); }
        }

        private static bool ApplyChoice(Pending p)
        {
            try
            {
                var ctrl = Singleton("GameEventSystemController");
                var m = ctrl?.GetType().GetMethod("EventOptionChosen", BindingFlags.Public | BindingFlags.Instance);
                if (m == null) return false;
                m.Invoke(ctrl, new[] { p.Instance, (object)p.ChosenIndex });
                return true;
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile("[EventInteractor] apply EXC: " + (ex.InnerException?.Message ?? ex.Message));
                return false;
            }
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

        private static string At(List<string> l, int i) => i >= 0 && i < l.Count ? l[i] : "?";
        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";

        private static object Singleton(string typeName)
        {
            var t = FindTypeByName(typeName);
            return t?.GetProperty("Instance",
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
