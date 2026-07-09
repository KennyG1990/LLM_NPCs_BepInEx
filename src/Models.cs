using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using Newtonsoft.Json;

namespace GoingMedieval.LLM_NPCs
{
    public class Message
    {
        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }
    }

    public class OpenRouterModel
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("context_length")]
        public int ContextLength { get; set; } = 4096;

        [JsonProperty("pricing_prompt")]
        public string PricingPrompt { get; set; } = "0.00";

        [JsonProperty("pricing_completion")]
        public string PricingCompletion { get; set; } = "0.00";

        /// <summary>"$0.60/$1.20 per 1M" (prompt/completion) from OpenRouter's
        /// per-token USD strings — the number a human can actually budget with.</summary>
        public string PricePerMillion
        {
            get
            {
                double p = 0, c = 0;
                double.TryParse(PricingPrompt, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out p);
                double.TryParse(PricingCompletion, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out c);
                if (p <= 0 && c <= 0) return "FREE";
                return $"${p * 1_000_000:0.##}/${c * 1_000_000:0.##} per 1M";
            }
        }

        [JsonProperty("is_free")]
        public bool IsFree { get; set; } = true;
    }

    public class LLMDecision
    {
        public bool Success { get; set; }
        public string Action { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public string Reasoning { get; set; }
        public string DialogueComplaint { get; set; }
    }

    /// <summary>
    /// Holds all 8 influence-domain pressure values for a settler.
    /// Computed deterministically in Stage 1 of the influence engine.
    /// Higher values = more pressure = higher priority for that domain's actions.
    /// </summary>
    public class SettlerPressures
    {
        // Domain 1: Survival
        public float Hunger     { get; set; }
        public float Thirst     { get; set; }
        public float Exhaustion { get; set; }

        // Domain 2: Health
        public float Injury        { get; set; }
        public float IllnessPenalty { get; set; }

        // Domain 3: Threat
        public float ThreatLevel { get; set; }
        public float RaidAlert   { get; set; }

        // Domain 4: Mood
        public float MoodDistress   { get; set; }
        public float RecreationLag  { get; set; }

        // Domain 5: Work
        public float WorkSkillMismatch { get; set; }
        public float IdlePressure      { get; set; }

        // Domain 6: Social
        public float SocialDebt          { get; set; }
        public float RelationshipTension  { get; set; }

        // Domain 7: Construction
        public float ColonyNeedScore { get; set; }
        public float HaulNeed        { get; set; }

        // Domain 8: Attire
        public float AttireMismatch { get; set; }

        /// <summary>The single highest domain pressure — used for quick priority checks.</summary>
        public float DomainMax => Math.Max(
            Math.Max(Math.Max(Hunger, Math.Max(Exhaustion, Thirst)),
                     Math.Max(ThreatLevel, Injury)),
            Math.Max(MoodDistress, Math.Max(ColonyNeedScore, RelationshipTension)));
    }

    /// <summary>Colony-level aggregate state — used by ColonyInfluenceEngine (Phase 3).</summary>
    public class ColonyState
    {
        public float AverageMood    { get; set; }
        public float AverageHunger  { get; set; }
        public float AverageHealth  { get; set; }
        public int   TotalSettlers  { get; set; }
        public int   IdleSettlers   { get; set; }
        public float ColonyWealth   { get; set; }
        public float ThreatLevel    { get; set; }
        public int   PendingBlueprints { get; set; }
        public List<string> ActiveTensions { get; set; } = new List<string>();
    }

    /// <summary>A colony-level recommendation produced by ColonyInfluenceEngine (Phase 3).</summary>
    public class ColonyRecommendation
    {
        public string Type        { get; set; }   // e.g. "reassign_job", "build_priority"
        public string Description { get; set; }
        public float  Score       { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public static class PromptBuilder
    {
        public static string BuildRichContextSummary(NPCContext context, bool includeMemory = false)
        {
            if (context == null) return "No context available.";

            var sb = new StringBuilder();
            sb.AppendLine($"Name: {context.Name} (Age: {context.Age}, Gender: {context.Gender})");
            sb.AppendLine($"Profession/Role: {context.Profession} ({context.BackgroundOrRole})");
            if (!string.IsNullOrEmpty(context.Pseudonym))
            {
                sb.AppendLine($"Title/Pseudonym: {context.Pseudonym}");
            }
            
            sb.AppendLine($"Mood: {context.Mood} (Score: {context.MoodScore:F0}/100)");
            if (!string.IsNullOrEmpty(context.Religion) || context.ReligiousAlignment != 0f)
                sb.AppendLine($"Faith: {context.Religion} (alignment {context.ReligiousAlignment:F1} — devout vs skeptic shapes worldview, arguments, and alliances)");
            if (!string.IsNullOrEmpty(context.Weapon))
                sb.AppendLine($"Weapon: {context.Weapon}" + (context.Equipment != null
                    ? $" | Armor: {context.Equipment.Armor ?? "none"} | Helmet: {context.Equipment.Helmet ?? "none"} | Clothing: {context.Equipment.Clothing ?? "none"}"
                    : " | Equipped: nothing"));
            if (!string.IsNullOrEmpty(context.ScheduleSummary))
                sb.AppendLine($"Daily schedule: {context.ScheduleSummary}");
            if (context.Health != null)
            {
                sb.AppendLine($"Health: {context.Health.Current:F0}/{context.Health.Max:F0}");
                if (context.Health.StatusEffects != null && context.Health.StatusEffects.Count > 0)
                {
                    sb.AppendLine($"- Status Effects: {string.Join(", ", context.Health.StatusEffects)}");
                }
            }

            if (context.Needs != null)
            {
                sb.AppendLine("Needs:");
                sb.AppendLine($"- Food: {context.Needs.Food:F0}/100, Water: {context.Needs.Water:F0}/100, Rest: {context.Needs.Rest:F0}/100");
                sb.AppendLine($"- Recreation: {context.Needs.Recreation:F0}/100, Comfort: {context.Needs.Comfort:F0}/100");
            }

            if (context.CurrentActivity != null)
            {
                sb.AppendLine($"Current Activity: {context.CurrentActivity.Description} ({context.CurrentActivity.Type})");
            }

            if (context.Environment != null)
            {
                sb.AppendLine($"Location: {context.Environment.Room}");
                sb.AppendLine($"Time: {context.Environment.TimeOfDay}, Weather: {context.Environment.Weather}");
            }

            if (context.Traits != null && context.Traits.Count > 0)
            {
                sb.AppendLine($"Traits: {string.Join(", ", context.Traits)}");
            }

            if (context.Skills != null && context.Skills.Count > 0)
            {
                var topSkills = context.Skills.OrderByDescending(x => x.Value).Take(5).Select(x => $"{x.Key}:{x.Value}");
                sb.AppendLine($"Top Skills: {string.Join(", ", topSkills)}");
            }

            if (includeMemory && !string.IsNullOrEmpty(context.MemoryContext))
            {
                sb.AppendLine("Memory Context:");
                sb.AppendLine(context.MemoryContext);
            }

            return sb.ToString();
        }
    }
}
