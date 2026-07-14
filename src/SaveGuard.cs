using System;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// PROGRAMMATIC SAVE (kills the UI-click save protocol, which misfired 5×
    /// on 2026-07-11 — the story-recap screen and open panels eat ESC, and
    /// stray world-clicks select entities). External tools request a save by
    /// writing the flag file; we call the game's own
    /// GlobalSaveController.AutosaveCurrentVillage() on the next main-thread
    /// tick (full managed path: water-thread wait, autosave_N naming+rotation,
    /// serialization; village NAME stays unchanged so BuiltState sidecar
    /// adoption is unaffected).
    ///
    /// NOTE (HOW_THINGS_WORK §11): QuicksaveCurrentVillage() is an EMPTY STUB
    /// in the shipped assembly — invoking it is a silent no-op. Use
    /// AutosaveCurrentVillage() or SaveCurrentVillage(filename).
    /// </summary>
    public static class SaveGuard
    {
        public static string LastResult = "(idle)";
        private const string FlagPath =
            @"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\save_request.txt";
        // PERIODIC AUTO-BANK (2026-07-12: three host hangs in one night — a
        // crash must never cost more than a few minutes of colony history).
        private static DateTime _lastPeriodic = DateTime.MinValue;
        // 8 -> 20 min (2026-07-13, autonomous): each autosave is the GAME'S world
        // serialization = an inherent ~2.7s freeze (any save, player or mod, does
        // this). At 8 min it fired ~9x/hr = repeated [mod:save-guard] freezes that
        // fail Gate 1's no-mod-freeze bar. Crashes are fixed + the game autosaves
        // natively, so bank less often. The freeze is game-save cost, not mod logic.
        private const int PeriodicMinutes = 20;

        public static void Tick()
        {
            try
            {
                if (!System.IO.File.Exists(FlagPath))
                {
                    if (_lastPeriodic == DateTime.MinValue) { _lastPeriodic = DateTime.UtcNow; return; }
                    if ((DateTime.UtcNow - _lastPeriodic).TotalMinutes < PeriodicMinutes) return;
                    _lastPeriodic = DateTime.UtcNow;
                    InvokeAutosave("periodic bank");
                    return;
                }
                try { System.IO.File.Delete(FlagPath); } catch { }
                _lastPeriodic = DateTime.UtcNow;   // a flag save resets the periodic clock
                InvokeAutosave("flag consumed");
            }
            catch (Exception ex)
            {
                LastResult = "save EXC: " + (ex.InnerException?.Message ?? ex.Message);
                LLMNPCsPlugin.LogToFile("[SaveGuard] " + LastResult);
            }
        }

        private static void InvokeAutosave(string why)
        {
            var t = FindTypeByName("GlobalSaveController");
            var gsc = t?.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null);
            var m = gsc?.GetType().GetMethod("AutosaveCurrentVillage", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) { LastResult = "save: GlobalSaveController.AutosaveCurrentVillage not found"; return; }
            m.Invoke(gsc, null);
            LastResult = $"save: autosave requested at {DateTime.Now:HH:mm:ss} ({why})";
            LLMNPCsPlugin.LogToFile("[SaveGuard] " + LastResult);
        }

        private static Type FindTypeByName(string n)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in a.GetTypes()) if (t.Name == n) return t; } catch { } }
            return null;
        }
    }
}
