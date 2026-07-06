using System;
using HarmonyLib;
using NSMedieval; // Note: Ensure this namespace is correct based on assembly inspection

namespace GoingMedieval.LLM_NPCs
{
    [HarmonyPatch(typeof(NSMedieval.RaidController))]
    public static class RaidControllerPatches
    {
        [HarmonyPatch("OnRaidSpawned")]
        [HarmonyPostfix]
        public static void OnRaidSpawned_Postfix()
        {
            LLMNPCsPlugin.Log.LogInfo("[Harmony] Target 'OnRaidSpawned' triggered! Initiating tactical combat mode.");
            LLMNPCsPlugin.LogToFile("[Harmony] RaidSpawned event intercepted.");
            
            // Trigger high priority immediate LLM evaluation for raid defense
            LLMNPCsPlugin.Instance.DecisionEngine.TriggerImmediateEvaluation("RaidSpawned");
        }

        [HarmonyPatch("OnRaidEnded")]
        [HarmonyPostfix]
        public static void OnRaidEnded_Postfix()
        {
            LLMNPCsPlugin.Log.LogInfo("[Harmony] Target 'OnRaidEnded' triggered! Initiating post-battle cleanup.");
            LLMNPCsPlugin.LogToFile("[Harmony] RaidEnded event intercepted.");
            
            // Trigger high priority immediate LLM evaluation for recovery
            LLMNPCsPlugin.Instance.DecisionEngine.TriggerImmediateEvaluation("RaidEnded");
        }
    }

    [HarmonyPatch(typeof(NSMedieval.BuildingComponents.StabilityManager), "PlaceBlueprint")]
    public static class BlueprintVerificationPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(object[] __args) // building
        {
            try
            {
                if (__args != null && __args.Length >= 1)
                {
                    // __args[0] is BaseBuildingViewComponent
                    var buildingComponent = __args[0];
                    if (buildingComponent != null)
                    {
                        var nameProp = buildingComponent.GetType().GetProperty("name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                        var transformProp = buildingComponent.GetType().GetProperty("transform", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                        
                        string buildingName = nameProp?.GetValue(buildingComponent) as string ?? "UnknownBlueprint";
                        object transformObj = transformProp?.GetValue(buildingComponent);
                        var posProp = transformObj?.GetType().GetProperty("position", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                        object positionBoxed = posProp?.GetValue(transformObj);

                        LLMNPCsPlugin.Log.LogInfo($"[Blueprint Verification] Intercepted blueprint placement request: {buildingName}");
                        
                        // Enforce stability physics checks
                        bool isStable = VerifyStability(buildingName, positionBoxed);
                        if (!isStable)
                        {
                            LLMNPCsPlugin.Log.LogWarning($"[Blueprint Verification] REJECTED: {buildingName} failed stability physics checks.");
                            return false; // block placement
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"[Blueprint Verification] Error in Prefix: {ex}");
            }
            
            return true;
        }

        private static bool VerifyStability(string buildingName, object positionBoxed)
        {
            if (buildingName != null && positionBoxed is UnityEngine.Vector3 pos)
            {
                // Stub: If trying to build a roof high up with no support, reject
                if (pos.y > 5f && buildingName.ToLower().Contains("roof"))
                {
                    // Advanced: Cross-reference with MapV2 Grid stability
                    LLMNPCsPlugin.Log.LogWarning($"[Blueprint Verification] Roof at y > 5 requires explicit support validation.");
                    // For demo/mod release, allow it but log the interaction
                    return true; 
                }
            }
            return true;
        }
    }
}
