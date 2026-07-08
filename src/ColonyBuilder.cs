using System;
using System.Collections.Generic;
using System.Linq;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Strategic Model — the deterministic "villagers build their own village"
    /// actuator. Every tick it takes a PHYSICAL census of the colony's
    /// infrastructure from game truth (storage zones, beds), finds the single
    /// highest-priority gap, and fires the PROVEN placement path for it. The
    /// game's own CanPlace* gates reject water / invalid terrain, so nothing
    /// lands somewhere that "doesn't make sense".
    ///
    /// Three-layer firewall (per the RTS reference docs):
    ///   Read Model         -> census via StockpilePlacer.Count* (game truth)
    ///   Strategic Model     -> Decide() below (deterministic priority, no LLM)
    ///   Validator/Actuator  -> StockpilePlacer.TryPlace* (game CanPlace + Spawn)
    ///
    /// Builds ONE thing per tick then re-censuses next tick, so it never spams
    /// and stops on its own once needs are met — idempotent against GAME TRUTH,
    /// not internal flags. Placement counts are additionally capped per session
    /// as a belt-and-suspenders guard against a census that lags a frame.
    ///
    /// Gated on the full-autonomy master switch: this is a colony-INITIATED
    /// action, not a player order, so it only runs when the player has handed the
    /// colony over to the AI (EnableFullAutonomy = true).
    /// </summary>
    public static class ColonyBuilder
    {
        private static DateTime _lastTickUtc = DateTime.MinValue;
        private static DateTime _backoffUntilUtc = DateTime.MinValue;

        // Deterministic and free (no LLM), but placement is heavy and settlers
        // need time to actually build the last thing before we queue the next.
        public static double TickIntervalSeconds = 12d;

        // Cheapest sleeping spot from BaseBuildingRepository. Counted by this
        // same id, so bed placement self-limits at one-per-settler.
        private const string BedId = "hay_sleeping_spot";
        private const string CookId = "camp_fire";

        // Belt-and-suspenders caps so a lagging census can't spawn infinite
        // objects. Real bound still comes from the game-truth census each tick.
        private const int MaxStockpiles = 2;
        private static int _stockpilesPlaced = 0;
        private static int _cookPlaced = 0;
        private static int _bedsPlaced = 0;
        private static bool _dumpedIds = false;

        public static string LastAction = "(idle)";
        public static string LastCensus = "";

        // Timer only — the autonomy gate is checked INSIDE Tick so every tick
        // logs a heartbeat (autonomy state + census), making the Strategic Model
        // observable in the mod log even when it decides to do nothing.
        public static bool ShouldTick()
        {
            var now = DateTime.UtcNow;
            return now >= _backoffUntilUtc
                && (now - _lastTickUtc).TotalSeconds >= TickIntervalSeconds;
        }

        public static void Tick(List<Settler> settlers)
        {
            _lastTickUtc = DateTime.UtcNow;
            try
            {
                // FIRST: detect a world (re)load and reset ALL per-session builder
                // state before acting on stale flags — otherwise every reload
                // re-places a house + stockpile into the save (the bloat bug).
                BuiltState.OnTick();

                bool autonomyOn = AutonomyManager.Instance.IsFullAutonomyEnabled;
                var live = settlers?.Where(s => s != null && s.gameObject != null).ToList();
                int pop = live?.Count ?? 0;

                // ── Read Model: census the world (same game truth we place against) ──
                // VERIFIED census: counts only stockpiles the manager confirms exist
                // at their own Start cell — the raw count returned a phantom
                // 'stockpiles=1' from a pooled/dead instance, which is what forced
                // the old session-flag gate (and with it the reload duplicates).
                int stockpiles = pop > 0 ? StockpilePlacer.CountVerifiedStockpiles() : -1;
                int beds = pop > 0 ? StockpilePlacer.CountBuildings(BedId) : -1;
                int cookfires = pop > 0 ? StockpilePlacer.CountBuildings(CookId) : -1;
                LastCensus = $"autonomy={autonomyOn} pop={pop} stockpiles={stockpiles} cookfire={cookfires} beds={beds} " +
                             $"house={(HouseBuilder.Complete ? "done" : "…")} [placed sp={_stockpilesPlaced} cook={_cookPlaced} bed={_bedsPlaced}]";
                // Heartbeat EVERY tick so autonomy state + gaps are visible.
                LLMNPCsPlugin.LogToFile($"[ColonyBuilder] heartbeat {LastCensus}");

                // Refresh the colony-level PRIORITY alerts (food/storage/beds/cook)
                // every tick so the Player2 LLM decisions see the settlement's urgent
                // needs — computed even when autonomy is off (it feeds decision-making).
                ColonyAlerts.Compute(pop);

                if (pop == 0) { LastAction = "no settlers"; return; }
                if (!autonomyOn) { LastAction = "autonomy OFF (enable Full AI Autonomy)"; return; }

                // Fix the colony HOME waypoint once (settler-cluster centroid). All
                // placement + tree designation then anchor here so the village stays
                // compact instead of sprawling across the map.
                if (!ColonyHome.Established) ColonyHome.Establish(live);

                // ── P0: SURVIVAL UNBLOCK — allow the colony's forbidden ground
                // food/supplies so the hauling AI can store them and settlers can
                // eat. Runs every tick; idempotent (already-allowed piles are skipped).
                int allowed = ResourceUnforbidder.UnforbidAll();
                if (allowed > 0)
                    LLMNPCsPlugin.LogToFile($"[ColonyBuilder] {ResourceUnforbidder.LastResult}");

                // One-time: dump the real building ids so the plan can target the
                // cooking station / walls / roof / door by their true ids.
                if (!_dumpedIds) { _dumpedIds = true; StockpilePlacer.DumpBuildingIds(); }

                // ── P0.5: GATHER WOOD — designate nearby trees for chopping so the
                // settlers have MATERIALS to construct the blueprints. Without wood
                // they stand idle next to unbuilt walls. Bounded (won't clear-cut).
                int trees = WoodGatherer.DesignateTreesNear(live[0].gameObject, 14, 8);
                if (trees > 0) LLMNPCsPlugin.LogToFile($"[ColonyBuilder] wood: {WoodGatherer.LastResult}");

                // ── P0.6: FOOD — hunt wild animals + forage wild food plants near
                // HOME so the colony has a renewable-ish food supply and stops
                // starving. Bounded to home radius + per-session caps.
                if (ColonyHome.Established)
                {
                    var food = FoodGatherer.ProduceFoodNear(ColonyHome.X, ColonyHome.Y, ColonyHome.Z, ColonyHome.WorkRadius);
                    if (food.Contains("+") && !food.Contains("+0 forage+0"))
                        LLMNPCsPlugin.LogToFile($"[ColonyBuilder] food: {food}");
                }

                // Managers not ready yet (save still loading) — wait, don't act.
                if (stockpiles < 0) { LastAction = "waiting for game (stockpile mgr not ready)"; return; }

                var builder = PickBuilder(live);

                // ── Strategic decision: ONE build per tick, highest priority first ──

                // Priority 1: STORAGE. A colony with nowhere to store resources
                // stalls hauling and everything downstream of it. Gated on the
                // VERIFIED world census (phantom instances filtered out), so a
                // reload with an existing stockpile places NOTHING — the session
                // cap is only a belt-and-suspenders bound within one session.
                if (stockpiles == 0 && _stockpilesPlaced < MaxStockpiles)
                {
                    var r = StockpilePlacer.TryPlaceStockpileNear(builder.gameObject, 4);
                    Record("STORAGE", r, ref _stockpilesPlaced);
                    return;
                }

                // Priority 2: a COOKING STATION (camp_fire) so raw food becomes
                // meals — settlers can't eat cooked meals without one.
                if (cookfires >= 0 && cookfires < 1 && _cookPlaced < 1)
                {
                    var r = StockpilePlacer.TryPlaceBuildingNear(builder.gameObject, CookId);
                    Record("COOKFIRE", r, ref _cookPlaced);
                    return;
                }

                // Priority 3: BEDS placed INSIDE the house rooms (on the interior
                // floor cells), one per settler — not scattered outside. Plan the
                // house footprint first so interior cells exist to place them in.
                if (beds >= 0 && beds < pop && _bedsPlaced < pop)
                {
                    if (!HouseBuilder.IsPlanned) HouseBuilder.Plan(builder.gameObject);
                    // Skip interior cells that ALREADY hold a bed in the loaded
                    // save (idempotent against world truth, not the session index).
                    while (HouseBuilder.IsPlanned && _bedsPlaced < HouseBuilder.InteriorCells.Count &&
                           StockpilePlacer.BuildingExistsAt(
                               HouseBuilder.InteriorCells[_bedsPlaced][0], HouseBuilder.Level,
                               HouseBuilder.InteriorCells[_bedsPlaced][1], BedId))
                        _bedsPlaced++;
                    if (HouseBuilder.IsPlanned && _bedsPlaced < HouseBuilder.InteriorCells.Count)
                    {
                        var c = HouseBuilder.InteriorCells[_bedsPlaced];
                        var r = StockpilePlacer.TryPlaceBuildingAt(c[0], HouseBuilder.Level, c[1], BedId);
                        Record("BED-inside", r, ref _bedsPlaced);
                        return;
                    }
                    var rf = StockpilePlacer.TryPlaceBuildingNear(builder.gameObject, BedId); // fallback
                    Record("BED", rf, ref _bedsPlaced);
                    return;
                }

                // Priority 4: build the settlers their own HOUSE (walls+door+roof),
                // one piece per tick, once survival needs (storage/cook/beds) are met.
                if (!HouseBuilder.Complete)
                {
                    var r = HouseBuilder.Step(builder.gameObject);
                    LLMNPCsPlugin.LogToFile($"[ColonyBuilder] {r}");
                    return;
                }

                // Priority 4.5: RESEARCH TABLE — the road from 3 settlers to a
                // TOWN runs through research (stairs, better buildings, farming
                // tech). The "Research table missing" alert sat unactioned for
                // days; a forward-planning colony builds it as soon as shelter
                // exists. Census-gated like the cookfire (id: research_table).
                int research = StockpilePlacer.CountBuildings("research_table");
                if (research == 0)
                {
                    var rr = StockpilePlacer.TryPlaceBuildingNear(builder.gameObject, "research_table");
                    int dummy = 0; Record("RESEARCH-TABLE", rr, ref dummy);
                    return;
                }

                // Priority 5: UNDERGROUND — dig a food CELLAR into a nearby hill
                // (food keeps better underground; stable temperature). One per save.
                if (ColonyHome.Established && !BuiltState.CellarMarked)
                {
                    // Cellar may search FARTHER than the work radius — a settler
                    // will walk to the nearest hill for a proper cold cellar.
                    var c = CellarBuilder.DigCellarNear(ColonyHome.X, ColonyHome.Y, ColonyHome.Z, 60);
                    LLMNPCsPlugin.LogToFile($"[ColonyBuilder] {c}");
                    return;
                }

                LastAction = $"stable {LastCensus}";
            }
            catch (Exception ex)
            {
                LastAction = "EXC: " + ex.Message;
                LLMNPCsPlugin.LogToFile($"[ColonyBuilder] {LastAction}");
                Backoff();
            }
            finally { WriteStatus(); }
        }

        // Write current status to a file in the mod folder — reliable telemetry
        // that survives the flooded in-game log window.
        private static void WriteStatus()
        {
            try
            {
                System.IO.File.WriteAllText(
                    @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\colony_status.txt",
                    $"time: {DateTime.Now:HH:mm:ss}\n" +
                    $"census: {LastCensus}\n" +
                    $"action: {LastAction}\n" +
                    $"house:  {HouseBuilder.LastStep}\n" +
                    $"unforbid: {ResourceUnforbidder.LastResult}\n" +
                    $"wood:   {WoodGatherer.LastResult}\n" +
                    $"food:   {FoodGatherer.LastResult}\n" +
                    $"cellar: {CellarBuilder.LastResult}\n" +
                    $"home:   {ColonyHome.LastResult}\n" +
                    $"alerts: {ColonyAlerts.Current.Replace("\n", " | ")}\n");
            }
            catch { }
        }

        private static void Record(string need, string result, ref int placedCounter)
        {
            bool ok = result != null && result.StartsWith("ok");
            if (ok) placedCounter++;
            else Backoff(); // on failure, wait longer before retrying a broken path
            LastAction = $"need={need} {LastCensus} -> {result}";
            LLMNPCsPlugin.LogToFile($"[ColonyBuilder] {LastAction}");
        }

        // Placement is world-anchored on the settler's own map node, so any
        // settler serves as the site anchor; prefer whoever the game lists first.
        private static Settler PickBuilder(List<Settler> settlers) => settlers[0];

        private static void Backoff() => _backoffUntilUtc = DateTime.UtcNow.AddSeconds(120);

        /// <summary>Reset ALL per-session placement caps. Called by BuiltState
        /// when a world (re)load is detected.</summary>
        public static void Reset()
        {
            _stockpilesPlaced = 0;
            _cookPlaced = 0;
            _bedsPlaced = 0;
            _backoffUntilUtc = DateTime.MinValue;
            LastAction = "(idle)";
            LastCensus = "";
        }
    }
}
