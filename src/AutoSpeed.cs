using System;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// OVERNIGHT AUTONOMY: game events (visits, wildlife, raids) AUTO-PAUSE the
    /// game; paused timeScale used to freeze the whole mod (fixed by realtime
    /// waits) but the WORLD still stands still until someone unpauses — fatal
    /// for "leave it running all night" (Ken's goal). When full autonomy is on,
    /// the colony unpauses ITSELF via the game's own GameSpeedManager
    /// (ground truth: SetSpeedFaster/SetSpeedNormal, CurrentSpeedIndex;
    /// IsFasterSpeedDisabled true during raids -> fall back to normal speed).
    /// </summary>
    public static class AutoSpeed
    {
        public static string LastResult = "(idle)";
        private static Type _mgrT;

        public static void EnsureRunning()
        {
            try
            {
                if (_mgrT == null)
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    { try { foreach (var t in a.GetTypes()) if (t.Name == "GameSpeedManager") { _mgrT = t; break; } } catch { } if (_mgrT != null) break; }
                object mgr = null;
                for (var x = _mgrT; x != null && mgr == null; x = x.BaseType)
                {
                    var p = x.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (p != null) { try { mgr = p.GetValue(null, null); } catch { } }
                }
                if (mgr == null) return;
                var idx = _mgrT.GetProperty("CurrentSpeedIndex")?.GetValue(mgr, null)?.ToString();
                if (idx == null || !idx.Equals("Pause", StringComparison.OrdinalIgnoreCase)) return; // running fine
                bool fasterBlocked = false;
                try { fasterBlocked = _mgrT.GetProperty("IsFasterSpeedDisabled")?.GetValue(mgr, null) is bool b && b; } catch { }
                var m = _mgrT.GetMethod(fasterBlocked ? "SetSpeedNormal" : "SetSpeedFaster");
                m?.Invoke(mgr, null);
                LastResult = $"auto-unpaused ({(fasterBlocked ? "normal — raid active" : "faster")})";
                LLMNPCsPlugin.LogToFile("[AutoSpeed] " + LastResult);
            }
            catch (Exception ex) { LastResult = "EXC: " + (ex.InnerException?.Message ?? ex.Message); }
        }
    }
}
