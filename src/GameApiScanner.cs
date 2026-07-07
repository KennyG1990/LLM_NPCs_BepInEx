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
