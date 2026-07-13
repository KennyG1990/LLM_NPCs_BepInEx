using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// P3 (09 - AI Actions System) in-game execution bridge.
    ///
    /// Polls the dashboard's ai_orders queue and executes the current step of
    /// each queued/active order by mapping bounded order verbs onto the proven
    /// DecisionExecutor actions. Reports per-step status back so the dashboard
    /// order inspection panel is the living source of truth.
    ///
    /// Player orders are explicit player agency, so they execute even when the
    /// AutonomyManager master switch is off (that switch gates NPC-initiated
    /// actions, not commands the player gave through dialogue).
    /// </summary>
    public static class OrderExecutor
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private const string ServerUrl = "http://127.0.0.1:8714";
        private static bool _pollInFlight;
        private static DateTime _lastPollUtc = DateTime.MinValue;
        public const double PollIntervalSeconds = 10d;

        public static bool ShouldPoll()
        {
            return !_pollInFlight
                && (DateTime.UtcNow - _lastPollUtc).TotalSeconds >= PollIntervalSeconds;
        }

        public static async void PollAndExecute(List<Settler> settlers, string saveId)
        {
            if (_pollInFlight || string.IsNullOrWhiteSpace(saveId) || settlers == null)
                return;
            // At the main menu / during loading there are no settlers; polling
            // then would fail every step with "settler not present". Wait.
            if (settlers.Count == 0)
                return;
            _pollInFlight = true;
            _lastPollUtc = DateTime.UtcNow;
            try
            {
                foreach (var status in new[] { "active", "queued" })
                {
                    var url = $"{ServerUrl}/api/orders?save_id={Uri.EscapeDataString(saveId)}&status={status}";
                    var response = await _http.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                        return;
                    var body = await response.Content.ReadAsStringAsync();
                    var parsed = JObject.Parse(body);
                    var orders = parsed["orders"] as JArray;
                    if (orders == null)
                        continue;
                    foreach (var order in orders.OfType<JObject>())
                    {
                        await ExecuteOrderStep(order, settlers, saveId);
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[OrderExecutor] Poll failed: {ex.Message}");
            }
            finally
            {
                _pollInFlight = false;
            }
        }

        private static async Task ExecuteOrderStep(JObject order, List<Settler> settlers, string saveId)
        {
            var orderId = order.Value<long?>("id");
            var settlerId = order.Value<string>("settler_id");
            var currentStep = order.Value<int?>("current_step") ?? 0;
            var steps = order["steps"] as JArray;
            if (orderId == null || steps == null || currentStep >= steps.Count)
                return;

            var step = steps[currentStep] as JObject;
            if (step == null)
                return;
            var stepStatus = step.Value<string>("status") ?? "pending";
            if (stepStatus != "pending")
                return;

            var settler = FindSettler(settlers, settlerId);
            if (settler == null)
            {
                await ReportStep(orderId.Value, currentStep, "failed",
                    $"settler {settlerId} not present in game session");
                return;
            }

            var action = step.Value<string>("action") ?? "";
            var target = step.Value<string>("target") ?? "";
            var job = step.Value<string>("job") ?? "";
            string failure = null;
            string note = null;

            try
            {
                switch (action)
                {
                    case "prioritize_job":
                        DecisionExecutor.Execute(settler, MakeDecision("switch_job",
                            new Dictionary<string, object> { { "job", job } },
                            $"Player order: prioritize {job}"));
                        note = $"switched job priority to {job}";
                        break;
                    case "return_to_work":
                        DecisionExecutor.Execute(settler, MakeDecision("continue_job", null,
                            "Player order: return to work"));
                        note = "returned to work";
                        break;
                    case "move_to":
                        // REAL movement (MovementOrders, 2026-07-12): the game's
                        // own MoveOrder channel. Target resolution: "home"/empty
                        // → colony home; "x,z" → explicit cell.
                        {
                            if (ResolveTargetCell(target, out int mx, out int my, out int mz) &&
                                MovementOrders.TryMoveTo(settler.gameObject, mx, my, mz, 2f))
                                note = $"MoveOrder issued toward '{target}' ({mx},{mz}) — walking validates live";
                            else
                                failure = $"move_to '{target}': {MovementOrders.LastResult}";
                        }
                        break;
                    case "patrol":
                        // Patrol = MoveOrder with a WIDE guard radius (the game's
                        // guard mode holds the area after arriving).
                        {
                            if (ResolveTargetCell(target, out int px, out int py, out int pz) &&
                                MovementOrders.TryMoveTo(settler.gameObject, px, py, pz, 10f))
                                note = $"patrol order issued around '{target}' ({px},{pz}) guard r=10";
                            else
                                failure = $"patrol '{target}': {MovementOrders.LastResult}";
                        }
                        break;
                    case "attack_target":
                        DecisionExecutor.Execute(settler, MakeDecision("defend", null,
                            $"Player order: engage {target}"));
                        note = $"drafted to defensive combat stance against '{target}'";
                        break;
                    case "hold_position":
                        // Hold = MoveOrder to the settler's OWN current cell with
                        // a small guard radius: stand fast, right here.
                        {
                            var node = StockpilePlacer.SettlerNode(settler.gameObject);
                            if (node != null &&
                                MovementOrders.TryMoveTo(settler.gameObject, node[0], node[1], node[2], 3f))
                                note = "holding position (guard r=3 at own cell)";
                            else
                                failure = $"hold_position: {MovementOrders.LastResult}";
                        }
                        break;
                    case "return_to_player":
                        // No player avatar in this game — "return" = come home.
                        {
                            if (ResolveTargetCell("home", out int rx, out int ry, out int rz) &&
                                MovementOrders.TryMoveTo(settler.gameObject, rx, ry, rz, 2f))
                                note = "returning to the colony home";
                            else
                                failure = $"return: {MovementOrders.LastResult}";
                        }
                        break;
                    case "follow_player":
                        failure = "no player avatar exists in this game — use move_to/hold/patrol";
                        break;
                    case "prioritize_construction":
                        DecisionExecutor.Execute(settler, MakeDecision("prioritize_construction",
                            new Dictionary<string, object> { { "building_type", step.Value<string>("building") ?? "" } },
                            "Approved construction proposal"));
                        note = "construction priority raised";
                        break;
                    case "place_stockpile":
                        var placement = StockpilePlacer.TryPlaceStockpileNear(settler.gameObject, 2);
                        if (placement.StartsWith("ok:"))
                            note = placement;
                        else
                            failure = placement;
                        break;
                    case "probe_direct":
                        note = StockpilePlacer.ProbeDirectSpawn(settler.gameObject);
                        break;
                    case "player2_decide":
                        LLMNPCsPlugin.ForceProcessSettler(settler);
                        note = "forced a real Player2 decision cycle (settler chooses + acts autonomously)";
                        break;
                    case "dump_character":
                        note = NPCContextExtractor.DumpCharacter(settler.gameObject);
                        break;
                    case "build_building":
                        var bldg = StockpilePlacer.TryPlaceBuildingNear(settler.gameObject, step.Value<string>("building") ?? "hay_sleeping_spot");
                        if (bldg.StartsWith("ok")) note = bldg; else failure = bldg;
                        break;
                    case "build_special":
                        DecisionExecutor.Execute(settler, MakeDecision("build_special",
                            new Dictionary<string, object> { { "building_name", step.Value<string>("building") ?? "" } },
                            "Approved construction proposal"));
                        note = $"proposed blueprint '{step.Value<string>("building")}' to the game";
                        break;
                    default:
                        failure = $"unsupported action '{action}'";
                        break;
                }
            }
            catch (Exception ex)
            {
                failure = $"execution error: {ex.Message}";
            }

            if (failure != null)
            {
                await ReportStep(orderId.Value, currentStep, "failed", failure);
            }
            else
            {
                LLMNPCsPlugin.LogToFile($"[OrderExecutor] Order {orderId} step {currentStep} ({action}) executed for {settlerId}: {note}");
                await ReportStep(orderId.Value, currentStep, "completed", note ?? "");
            }
        }

        private static Settler FindSettler(List<Settler> settlers, string settlerId)
        {
            if (string.IsNullOrWhiteSpace(settlerId))
                return null;
            foreach (var settler in settlers)
            {
                if (settler == null || settler.gameObject == null)
                    continue;
                var id = GameBridge.GetSettlerId(settler.gameObject);
                if (string.Equals(id, settlerId, StringComparison.OrdinalIgnoreCase))
                    return settler;
                // orders issued against the stable name-hash also match by name
                var stable = GameBridge.ComputeStableSettlerId(settler.Name);
                if (stable != null && string.Equals(stable, settlerId, StringComparison.OrdinalIgnoreCase))
                    return settler;
            }
            return null;
        }

        /// <summary>Resolve an order's target text to a grid cell. Known names
        /// (home/house/village/plaza/empty) anchor on colony state; "x,z"
        /// parses explicit coordinates. Unresolvable targets return false —
        /// the step fails HONESTLY instead of walking somewhere random.</summary>
        private static bool ResolveTargetCell(string target, out int x, out int y, out int z)
        {
            x = y = z = 0;
            var t = (target ?? "").Trim().ToLowerInvariant();
            if (t == "" || t == "home" || t == "camp" || t == "the settlement" || t == "settlement")
            {
                if (!ColonyHome.Established) return false;
                x = ColonyHome.X; y = ColonyHome.Y; z = ColonyHome.Z;
                return true;
            }
            if (t == "house" || t == "the house" || t == "village" || t == "plaza" || t == "the square")
            {
                var hc = HouseBuilder.HouseCenter;
                if (hc != null) { x = hc[0]; y = hc[1]; z = hc[2]; return true; }
                if (ColonyHome.Established) { x = ColonyHome.X; y = ColonyHome.Y; z = ColonyHome.Z; return true; }
                return false;
            }
            var parts = t.Split(',');
            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out var px)
                                  && int.TryParse(parts[parts.Length - 1].Trim(), out var pz))
            {
                x = px; z = pz;
                y = ColonyHome.Established ? ColonyHome.Y : 5;
                return true;
            }
            return false;
        }

        private static LLMDecision MakeDecision(string action, Dictionary<string, object> parameters, string reasoning)
        {
            return new LLMDecision
            {
                Success = true,
                Action = action,
                Parameters = parameters ?? new Dictionary<string, object>(),
                Reasoning = reasoning,
            };
        }

        private static async Task ReportStep(long orderId, int stepIndex, string stepStatus, string reason)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "order_id", orderId },
                    { "step_index", stepIndex },
                    { "step_status", stepStatus },
                    { "reason", reason ?? "" },
                };
                var content = new StringContent(JsonConvert.SerializeObject(payload),
                    Encoding.UTF8, "application/json");
                await _http.PostAsync($"{ServerUrl}/api/orders/update", content);
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[OrderExecutor] ReportStep failed: {ex.Message}");
            }
        }
    }
}
