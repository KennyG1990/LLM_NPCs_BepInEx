using System;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// RECAP AUTO-DISMISSER + RUN-IN-BACKGROUND (survival-critical, 2026-07-12:
    /// Dowsby fell to a winter famine while the story-recap screen held GAME
    /// TIME frozen — the mod ticks on game time, so it could never dismiss the
    /// screen that froze it. This runs on Plugin.Update (REAL time).
    ///
    /// Ground truth (GameApiIndex): NSMedieval.LoadingScreenFake (ClosableUIView)
    /// is the story/recap screen; its private 'continueButton' GameObject goes
    /// active when the story waits for input; OnContinueClick() is the game's
    /// own dismiss handler. LoadingScreenFake also re-applies
    /// GlobalSettings.RunInBackground on load — we assert
    /// Application.runInBackground=true so an unfocused window can never pause
    /// the colony again (the class of freeze behind most of tonight's stalls).
    /// </summary>
    public static class RecapDismisser
    {
        public static string LastResult = "(watching)";
        private static float _nextCheck;
        private static float _visibleSince = -1f;
        private static float _stuckSince = -1f;   // "Loading Complete", no button
        private static float _rescueClickAt = -1f; // rescue step 2 scheduled time
        private static Type _screenType;
        private static bool _ribLogged;

        public static void Tick()
        {
            try
            {
                if (UnityEngine.Time.realtimeSinceStartup < _nextCheck) return;
                _nextCheck = UnityEngine.Time.realtimeSinceStartup + 2f;

                _screenType = _screenType ?? FindType("NSMedieval.LoadingScreenFake");
                if (_screenType == null) { Trace("LoadingScreenFake type NOT FOUND"); return; }
                var screen = UnityEngine.Object.FindObjectOfType(_screenType) as UnityEngine.Component;
                if (screen == null || !screen.gameObject.activeInHierarchy)
                {
                    // The colony must never pause because the window lost focus —
                    // but ONLY outside the load screen. LoadingScreenFake.OnEnable
                    // sets runInBackground=false ON PURPOSE; fighting it during a
                    // load is the prime suspect for the AudioEventsHandler NRE
                    // that wedged 13+ loads at "Placing objects" (2026-07-12).
                    if (!UnityEngine.Application.runInBackground)
                    {
                        UnityEngine.Application.runInBackground = true;
                        if (!_ribLogged) { _ribLogged = true; LLMNPCsPlugin.LogToFile("[RecapDismisser] Application.runInBackground forced TRUE — unfocused window no longer pauses the colony"); }
                    }
                    Trace(screen == null ? "no screen instance" : "screen inactive"); _visibleSince = -1f; _stuckSince = -1f; _rescueClickAt = -1f; return;
                }
                Trace("screen ACTIVE — checking button");
                var btn = _screenType.GetField("continueButton", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(screen) as UnityEngine.GameObject;
                if (btn == null || !btn.activeInHierarchy)
                {
                    _visibleSince = -1f;
                    // STUCK-LOAD RESCUE (root-caused 2026-07-12 after 13 wedged
                    // boots): OnLoadingFinished waits on the private static
                    // AlmanacPanelManager.initDone — when the almanac init dies,
                    // the load sits at "Loading Complete" forever. If this
                    // screen is up with NO continue button for 30s, force the
                    // flag; the game's own WaitUntil poll then completes its
                    // chain and activates the button (which we then click).
                    if (_stuckSince < 0f) { _stuckSince = UnityEngine.Time.realtimeSinceStartup; return; }
                    if (UnityEngine.Time.realtimeSinceStartup - _stuckSince < 30f) return;
                    SingletonAudit();
                    var apm = FindType("NSMedieval.Almanac.AlmanacPanelManager");
                    var f = apm?.GetField("initDone", BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null && !(bool)f.GetValue(null))
                    {
                        // gate not passed: flip it and give the game's own chain 30s
                        f.SetValue(null, true);
                        LLMNPCsPlugin.LogToFile("[RecapDismisser] STUCK LOAD RESCUE: AlmanacPanelManager.initDone forced TRUE — the game's load chain resumes");
                        _stuckSince = -1f;
                        return;
                    }
                    // initDone already true and STILL no button after 30s: the
                    // game's TaskController chain died AFTER the gate (traced
                    // live 2026-07-12 12:58-13:03 — screen active, button never
                    // came). Replay the dead chain's two steps in order:
                    // 1) InvokeLoadingCompleteEvent — WorkerManager, Heightmap,
                    //    StabilityManager, GlobalStatManager etc. all finalize
                    //    on this event; skipping it leaves a half-initialized
                    //    world with frozen time (lived 2026-07-12 13:26).
                    //    Safe to re-fire: the game nulls the event after invoke.
                    // 2) a beat later, OnContinueClick — the player's click.
                    if (_rescueClickAt < 0f)
                    {
                        var lc = FindType("NSMedieval.Controllers.LoadingController");
                        var lcInst = lc == null ? null : UnityEngine.Object.FindObjectOfType(lc);
                        var inv = lc?.GetMethod("InvokeLoadingCompleteEvent", BindingFlags.Public | BindingFlags.Instance);
                        if (lcInst != null && inv != null)
                        {
                            inv.Invoke(lcInst, null);
                            LLMNPCsPlugin.LogToFile("[RecapDismisser] STUCK LOAD RESCUE 1/2: InvokeLoadingCompleteEvent fired — world finalizers run");
                        }
                        else LLMNPCsPlugin.LogToFile("[RecapDismisser] STUCK LOAD RESCUE 1/2 SKIPPED: LoadingController not reachable (type=" + (lc != null) + " inst=" + (lcInst != null) + ")");
                        _rescueClickAt = UnityEngine.Time.realtimeSinceStartup + 3f;
                        return;
                    }
                    if (UnityEngine.Time.realtimeSinceStartup < _rescueClickAt) return;
                    var click = _screenType.GetMethod("OnContinueClick", BindingFlags.Public | BindingFlags.Instance);
                    if (click != null)
                    {
                        click.Invoke(screen, null);
                        LLMNPCsPlugin.LogToFile("[RecapDismisser] STUCK LOAD RESCUE 2/2: OnContinueClick invoked directly — gameplay starts");
                    }
                    _rescueClickAt = -1f;
                    _stuckSince = -1f;
                    return;
                }
                _stuckSince = -1f;
                _rescueClickAt = -1f;

                // Linger a few real seconds: a watching human can read the story,
                // and we never race the load-complete transition.
                if (_visibleSince < 0f) { _visibleSince = UnityEngine.Time.realtimeSinceStartup; return; }
                if (UnityEngine.Time.realtimeSinceStartup - _visibleSince < 4f) return;

                var m = _screenType.GetMethod("OnContinueClick", BindingFlags.Public | BindingFlags.Instance);
                if (m == null) { LastResult = "no OnContinueClick"; return; }
                m.Invoke(screen, null);
                _visibleSince = -1f;
                LastResult = $"recap auto-dismissed {DateTime.Now:HH:mm:ss}";
                LLMNPCsPlugin.LogToFile("[RecapDismisser] " + LastResult + " — game time resumes");
            }
            catch (Exception ex)
            {
                LastResult = "EXC: " + (ex.InnerException?.Message ?? ex.Message);
                if (UnityEngine.Time.realtimeSinceStartup >= _nextExcLog)
                {
                    _nextExcLog = UnityEngine.Time.realtimeSinceStartup + 30f;
                    LLMNPCsPlugin.LogToFile("[RecapDismisser] Tick EXC: " + ex);
                }
            }
        }
        private static float _nextExcLog;

        // Throttled state trace so a silent detection failure is visible in the log.
        private static float _nextTrace;
        private static void Trace(string state)
        {
            if (UnityEngine.Time.realtimeSinceStartup < _nextTrace) return;
            _nextTrace = UnityEngine.Time.realtimeSinceStartup + 30f;
            LLMNPCsPlugin.LogToFile("[RecapDismisser] state: " + state);
        }

        // One-shot: when a load wedges, name exactly which scene singleton is
        // missing — AudioEventsHandler.OnMainSceneLoadedEvent NREs on a null
        // MonoSingleton<T>.Instance and that throw kills the whole SceneLoaded
        // task (root-caused from Player.log 2026-07-12). The types below are
        // its subscription list, in source order.
        private static bool _auditDone;
        private static readonly string[] _auditTypes = {
            "NSMedieval.Controllers.UIController", "NSMedieval.RtsCamera",
            "NSMedieval.Manager.WorldTimeManager", "NSMedieval.Weather.WeatherManager",
            "NSMedieval.GlobalShaderVariables", "NSMedieval.UI.WarningMessageController",
            "NSMedieval.UI.BlackBarMessageController", "NSMedieval.GameEventSystem.GameEventSystem",
            "NSMedieval.Controllers.RaidController", "NSMedieval.Controllers.NPCController",
            "NSMedieval.Controllers.OptionsController", "NSMedieval.Manager.GameSpeedManager",
            "NSMedieval.Controllers.ConstructionController", "NSMedieval.Controllers.ResourcePileController",
            "NSMedieval.Manager.SelectionManager", "NSMedieval.Combat.CombatController",
            "NSMedieval.Controllers.FactionsController"
        };
        private static void SingletonAudit()
        {
            if (_auditDone) return;
            _auditDone = true;
            var missing = "";
            foreach (var name in _auditTypes)
            {
                var t = FindTypeBySuffix(name);
                if (t == null) { missing += name + "(type?) "; continue; }
                var inst = UnityEngine.Object.FindObjectOfType(t);
                if (inst == null) missing += t.FullName + " ";
            }
            LLMNPCsPlugin.LogToFile("[RecapDismisser] SINGLETON AUDIT (stuck load): " +
                (missing.Length == 0 ? "all present — NRE source is elsewhere" : "MISSING: " + missing));
        }

        // Namespaces in the audit list are best-effort guesses; fall back to a
        // short-name sweep so a wrong namespace can't hide a result.
        private static Type FindTypeBySuffix(string fullName)
        {
            var t = FindType(fullName);
            if (t != null) return t;
            var shortName = fullName.Substring(fullName.LastIndexOf('.') + 1);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var ty in a.GetTypes())
                        if (ty.Name == shortName && ty.Namespace != null && ty.Namespace.StartsWith("NSMedieval"))
                            return ty;
                }
                catch { }
            }
            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { var t = a.GetType(fullName, false); if (t != null) return t; } catch { } }
            return null;
        }
    }
}
