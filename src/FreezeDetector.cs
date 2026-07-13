using System;
using System.IO;
using System.Threading;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// MAIN-THREAD FREEZE DETECTOR (Ken, 2026-07-12: "the game is freezing
    /// constantly, whatever is making it freeze we need to fix this").
    ///
    /// A real WATCHDOG THREAD (immune to main-thread stalls) watches a
    /// heartbeat that Plugin.Update writes every frame. When the beat stalls
    /// past the threshold it captures WHAT WAS RUNNING (the phase marker) at
    /// the moment the world stopped; when the beat resumes it logs duration +
    /// culprit to the mod log AND validation/freeze_log.txt.
    ///
    /// HONEST ATTRIBUTION: mod subsystems set their phase on entry and clear
    /// to "engine" when mod work ends — a freeze during "engine" is the
    /// GAME's own hitch (autosave, mesh gen, GC), not ours. No more guessing
    /// which side of the fence a stall lives on.
    /// </summary>
    public static class FreezeDetector
    {
        public static string LastResult = "(armed)";
        private const int StallThresholdMs = 2000;   // player-feelable freeze
        private const string FreezeLog = @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\freeze_log.txt";

        private static long _lastBeatMs = Environment.TickCount;
        private static volatile string _phase = "engine";
        private static Thread _watchdog;
        private static readonly object _fileLock = new object();
        private static int _freezeCount;

        /// <summary>Called by Plugin.Update EVERY frame, first thing.</summary>
        public static void Beat()
        {
            Interlocked.Exchange(ref _lastBeatMs, Environment.TickCount);
            if (_watchdog == null) Start();
        }

        /// <summary>Mod subsystems mark themselves on entry; ColonyBuilder's
        /// Phase() forwards here too. Call Clear() when mod work ends.</summary>
        public static void SetPhase(string phase) { _phase = phase ?? "mod:unknown"; }
        public static void Clear() { _phase = "engine"; }

        private static void Start()
        {
            _watchdog = new Thread(WatchLoop) { IsBackground = true, Name = "LLMNPCs-FreezeWatchdog" };
            _watchdog.Start();
            LLMNPCsPlugin.LogToFile("[FreezeDetector] watchdog thread armed (threshold " + StallThresholdMs + "ms)");
        }

        private static void WatchLoop()
        {
            bool inFreeze = false;
            string culprit = "";
            long freezeStart = 0;
            while (true)
            {
                Thread.Sleep(250);
                long beat = Interlocked.Read(ref _lastBeatMs);
                long gap = Environment.TickCount - beat;
                if (!inFreeze && gap > StallThresholdMs)
                {
                    inFreeze = true;
                    freezeStart = beat;
                    culprit = _phase;   // what was running when the beat stopped
                    // Log the START immediately — a PERMANENT hang must still
                    // be attributed (first live run: the recovery-only design
                    // missed a hard hang in storage-place).
                    string startLine = $"{DateTime.Now:HH:mm:ss} FREEZE ONGOING (>2s) during [{culprit}] …";
                    try
                    {
                        lock (_fileLock)
                            File.AppendAllText(FreezeLog, startLine + Environment.NewLine);
                    }
                    catch { }
                }
                else if (inFreeze && gap < 500)
                {
                    inFreeze = false;
                    long duration = Environment.TickCount - freezeStart;
                    _freezeCount++;
                    string line = $"{DateTime.Now:HH:mm:ss} FROZE {duration}ms during [{culprit}] (freeze #{_freezeCount})";
                    LastResult = line;
                    try { LLMNPCsPlugin.LogToFile("[FreezeDetector] " + line); } catch { }
                    try
                    {
                        lock (_fileLock)
                            File.AppendAllText(FreezeLog, line + Environment.NewLine);
                    }
                    catch { }
                }
            }
        }
    }
}
