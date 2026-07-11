using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// B3 groundwork: dumps the game's construction/zone/mining API surface to
    /// the dashboard so placement code can be written against REAL signatures
    /// instead of guesses. Runs once per session after the save is loaded.
    /// </summary>
    public static class GameApiScanner
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private const string ServerUrl = "http://127.0.0.1:8714";
        private static bool _sent;

        private static readonly string[] Keywords =
        {
            "Blueprint", "Stockpile", "Zone", "Placement", "Construction",
            "BuildingsManager", "BuildingManager", "Designat", "RoomManager",
            "MineTask", "BuildTask", "GridObject", "PlaceObject", "FarmField",
            "Agriculture", "ResourcePile",
        };

        private static bool _eventScanDone;

        /// <summary>EVENT INTERACTOR groundwork (#34, Ken: "the game injects NPCs
        /// and events... the engine needs to interact with them"). Cockhamsted
        /// was WIPED after a story event ("he may be pursued") was blind-clicked
        /// through with no weapons in the colony. Dump every type that smells
        /// like the story/event/incident system so the interactor is written
        /// against REAL signatures, not guesses (the ilspycmd name-guess missed).</summary>
        public static void ScanEventSystem()
        {
            if (_eventScanDone) return;
            _eventScanDone = true;
            try
            {
                var kw = new[] { "StoryEvent", "GameEvent", "Incident", "Visitor", "Narrat", "Scenario",
                                 "Raid", "EventsController", "EventController", "EventManager", "EventUI",
                                 "EventPopup", "EventChoice", "Decision", "Salvation" };
                var sb = new StringBuilder();
                sb.AppendLine("EVENT SYSTEM TYPE SCAN — " + DateTime.Now);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = assembly.GetName().Name;
                    if (name == null || (!name.StartsWith("Assembly-CSharp") && !name.StartsWith("NSMedieval")))
                        continue;
                    Type[] ts;
                    try { ts = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { ts = ex.Types.Where(t => t != null).ToArray(); }
                    foreach (var t in ts)
                    {
                        if (t?.FullName == null) continue;
                        if (!kw.Any(k => t.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                        sb.AppendLine(t.FullName);
                        try
                        {
                            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Take(40))
                                sb.AppendLine("    " + m.ReturnType.Name + " " + m.Name + "(" +
                                              string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
                            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Take(25))
                                sb.AppendLine("    PROP " + p.PropertyType.Name + " " + p.Name);
                        }
                        catch { }
                    }
                }
                System.IO.File.WriteAllText(
                    @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\event_api.txt", sb.ToString());
                LLMNPCsPlugin.LogToFile("[GameApiScanner] event system scan -> validation/event_api.txt");
            }
            catch (Exception ex) { LLMNPCsPlugin.LogToFile("[GameApiScanner] event scan EXC: " + ex.Message); }
        }

        public static async void ScanAndReport(string saveId)
        {
            if (_sent) return;
            _sent = true;
            // Off the main thread: the reflection sweep over Assembly-CSharp
            // is expensive and caused a visible freeze right after save load.
            await System.Threading.Tasks.Task.Run(() => { });
            try
            {
                var types = new List<Dictionary<string, object>>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = assembly.GetName().Name;
                    if (name == null) continue;
                    if (!name.StartsWith("Assembly-CSharp") && !name.StartsWith("NSMedieval"))
                        continue;
                    Type[] assemblyTypes;
                    try { assemblyTypes = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { assemblyTypes = ex.Types.Where(t => t != null).ToArray(); }

                    foreach (var type in assemblyTypes)
                    {
                        if (type == null || type.Name == null) continue;
                        if (!Keywords.Any(k => type.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                            continue;
                        if (types.Count >= 250) break;

                        var methods = new List<string>();
                        try
                        {
                            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Take(50))
                            {
                                var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                                methods.Add($"{(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({ps})");
                            }
                        }
                        catch { }
                        var props = new List<string>();
                        try
                        {
                            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Take(30))
                                props.Add($"{p.PropertyType.Name} {p.Name}{(p.CanWrite ? " rw" : " ro")}");
                        }
                        catch { }

                        types.Add(new Dictionary<string, object>
                        {
                            { "type", type.FullName },
                            { "base", type.BaseType?.Name },
                            { "methods", methods },
                            { "properties", props },
                        });
                    }
                }

                var payload = new Dictionary<string, object>
                {
                    { "save_id", saveId ?? "" },
                    { "scanned_at", DateTime.UtcNow.ToString("o") },
                    { "type_count", types.Count },
                    { "types", types },
                };
                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                var response = await _http.PostAsync($"{ServerUrl}/api/dev/api_surface", content);
                LLMNPCsPlugin.LogToFile($"[GameApiScanner] Reported {types.Count} types, status {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[GameApiScanner] Scan failed: {ex.Message}");
            }
        }
    }
}
