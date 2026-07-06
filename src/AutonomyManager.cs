using System.Collections.Generic;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Manages whether the LLM is allowed to take physical actions in the game world.
    /// Acts as the Master Switch for the mod's autonomy, allowing the LLM to complain
    /// without executing actions if autonomy is off.
    /// </summary>
    public class AutonomyManager
    {
        private static AutonomyManager _instance;
        public static AutonomyManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new AutonomyManager();
                }
                return _instance;
            }
        }

        /// <summary>
        /// Master switch for full autonomy. If false, NPCs will only reason and generate
        /// dialogue complaints about their intended actions, but will not actually execute them.
        /// </summary>
        public bool IsFullAutonomyEnabled { get; set; } = false;

        /// <summary>
        /// Tracks autonomy state per faction ID (for future multiple AI colonies support).
        /// Faction 1 = Player Faction (usually).
        /// </summary>
        public Dictionary<int, bool> FactionAutonomyStates { get; private set; } = new Dictionary<int, bool>();

        public void SetFactionAutonomy(int factionId, bool isAutonomous)
        {
            FactionAutonomyStates[factionId] = isAutonomous;
        }

        public bool IsFactionAutonomous(int factionId)
        {
            if (FactionAutonomyStates.TryGetValue(factionId, out bool isAutonomous))
            {
                return isAutonomous;
            }
            return IsFullAutonomyEnabled;
        }
    }
}
