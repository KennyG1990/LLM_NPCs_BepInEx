using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Extracts all relevant context from a Going Medieval Settler for LLM decision-making.
    /// Uses reflection to access private fields and properties.
    /// </summary>
    public static class NPCContextExtractor
    {
        private static readonly HashSet<string> _loggedInvalidFindTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Extracts complete context from a settler.
        /// </summary>
        public static NPCContext Extract(Settler settler)
        {
            if (settler == null)
            {
                LLMNPCsPlugin.LogToFile("[NPCContextExtractor:Extract] settler is NULL");
                return null;
            }

            LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:Extract] Starting for settler ID: {settler.GetInstanceID()}");

            try
            {
                if (!GameBridge.TryGetValidatedSettlerIdentity(settler.gameObject, out var validatedId, out var validatedName, out var runtimeWorkerComponent))
                {
                    LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:Extract] Validation failed for GO '{settler.gameObject?.name ?? "<null>"}' - skipping context extraction");
                    return null;
                }

                var source = (object)runtimeWorkerComponent;

                var context = new NPCContext
                {
                    Id = validatedId,
                    Name = string.IsNullOrWhiteSpace(validatedName) ? "Unknown" : validatedName,
                    Age = GetFirstInt(source, "age", "Age", "biologicalAge"),
                    Gender = GetFirstString(source, "gender", "Gender", "sex", "Sex", "genderLabel") ?? "unknown",
                    BackgroundOrRole = GetFirstString(source, "background", "Background", "role", "Role", "profession", "Profession"),
                    Pseudonym = GetFirstString(source, "pseudonym", "Pseudonym", "nickname", "Nickname", "title", "Title"),
                    Skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    SkillExperience = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase),
                    Traits = new List<string>(),
                    Perks = new List<string>(),
                    BackgroundTags = new List<string>(),
                    Inventory = new List<string>(),
                    Relationships = new Dictionary<string, RelationshipContext>(StringComparer.OrdinalIgnoreCase),
                    WorkPriorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    Vitals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    States = new List<string>(),
                    Reputation = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase),
                    MoodLogs = new List<string>(),
                    ColonyWealth = GameBridge.GetColonyWealth()
                };

                LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:Extract] Basic info - Name: {context.Name}, Age: {context.Age}");

                // Health & Status
                ExtractHealth(source, context);
                
                // Needs
                ExtractNeeds(source, context);
                
                // Skills & Profession
                ExtractSkills(source, context);
                
                // Equipment
                ExtractEquipment(source, context);
                
                // Current Activity
                ExtractCurrentActivity(source, context);
                
                // Environment
                ExtractEnvironment(source, context);
                
                // Social
                ExtractSocial(source, context);

                LogExtractionDiagnostics(context);

                LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:Extract] Complete for {context.Name}");
                return context;
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"Error extracting context: {ex}");
                LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:Extract] ERROR: {ex}");
                return null;
            }
        }

        private static void ExtractHealth(object source, NPCContext context)
        {
            LLMNPCsPlugin.LogToFile("[NPCContextExtractor:ExtractHealth] Starting");
            try
            {
                var humanoidInstance = GetPropertyValue<object>(source, "HumanoidInstance") ?? source;
                
                var statsInstance = GetPropertyValue<object>(humanoidInstance, "Stats") ?? GetFieldValue<object>(humanoidInstance, "stats");
                if (statsInstance != null)
                {
                    var attributes = GetPropertyValue<object>(statsInstance, "Attributes") ?? GetFieldValue<object>(statsInstance, "attributes");
                    if (attributes is System.Collections.IDictionary dict)
                    {
                        foreach (var key in dict.Keys)
                        {
                            var attrInstance = dict[key];
                            var attrValue = GetPropertyValue<object>(attrInstance, "Value") ?? GetFieldValue<object>(attrInstance, "value");
                            var val = ToSingle(attrValue);
                            context.Vitals[key.ToString()] = val.ToString("F2");
                        }
                    }
                }

                var health = GetFieldValue<object>(source, "health");
                if (health != null)
                {
                    context.Health = new HealthContext
                    {
                        Current = GetFieldValue<float>(health, "currentValue"),
                        Max = GetFieldValue<float>(health, "maxValue"),
                        StatusEffects = GetStatusEffects(health)
                    };
                }

                context.Mood = GetFieldValue<string>(source, "moodState") ?? "neutral";
                context.MoodScore = GetFieldValue<float>(source, "mood");
                if (!context.Vitals.ContainsKey("pain"))
                    context.Vitals["pain"] = GetFirstFloat(source, "pain", "Pain", "painLevel").ToString("F1");
                if (!context.Vitals.ContainsKey("bleeding"))
                    context.Vitals["bleeding"] = GetFirstFloat(source, "bleeding", "Bleeding", "bleedingRate").ToString("F1");

                AppendStateFlags(humanoidInstance, context.States,
                    "isInjured", "isSick", "isStarving", "isThirsty", "isExhausted", "isSleeping", "isDead", "isDowned", "isDrafted");
                
                // Extract Mood Effectors
                var workerMood = GetPropertyValue<object>(humanoidInstance, "WorkerMood") ?? GetFieldValue<object>(humanoidInstance, "workerMood");
                if (workerMood != null)
                {
                    var moodEffectorsLog = GetPropertyValue<object>(workerMood, "MoodEffectorsLog") ?? GetFieldValue<object>(workerMood, "moodEffectorsLog") ?? 
                                           GetPropertyValue<object>(workerMood, "EffectorsLog") ?? GetFieldValue<object>(workerMood, "effectorsLog");
                    
                    if (moodEffectorsLog is System.Collections.IEnumerable moodLogs)
                    {
                        foreach (var log in moodLogs)
                        {
                            if (log != null)
                            {
                                var txt = log.ToString();
                                if (!string.IsNullOrWhiteSpace(txt)) context.MoodLogs.Add(txt);
                            }
                        }
                    }
                }

                LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractHealth] Mood: {context.Mood}, Score: {context.MoodScore}, Effectors: {context.MoodLogs.Count}");
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log?.LogDebug($"[ExtractHealth] Failed: {ex.Message}");
                LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractHealth] ERROR: {ex.Message}");
            }
        }

        private static void ExtractNeeds(object source, NPCContext context)
        {
            LLMNPCsPlugin.LogToFile("[NPCContextExtractor:ExtractNeeds] Starting");
            try
            {
                var statsObj = GetPropertyValue<object>(source, "Stats") ?? GetFieldValue<object>(source, "stats");
                if (statsObj == null)
                {
                    var humanoidInstance = GetPropertyValue<object>(source, "HumanoidInstance") ?? source;
                    statsObj = GetPropertyValue<object>(humanoidInstance, "Stats") ?? GetFieldValue<object>(humanoidInstance, "stats");
                }

                if (statsObj != null)
                {
                    System.Collections.IDictionary statsDict = null;
                    var statsFieldVal = GetFieldValue<object>(statsObj, "stats");
                    if (statsFieldVal != null)
                    {
                        statsDict = GetPropertyValue<System.Collections.IDictionary>(statsFieldVal, "Dictionary") ??
                                    GetFieldValue<System.Collections.IDictionary>(statsFieldVal, "dictionary");
                    }

                    if (statsDict == null)
                    {
                        var statsEnum = GetPropertyValue<System.Collections.IEnumerable>(statsObj, "Stats") ??
                                        GetFieldValue<System.Collections.IEnumerable>(statsObj, "stats");
                        if (statsEnum != null)
                        {
                            var dict = new Dictionary<int, object>();
                            foreach (var item in statsEnum)
                            {
                                if (item != null)
                                {
                                    var keyObj = GetPropertyValue<object>(item, "Key") ?? GetFieldValue<object>(item, "key");
                                    var valObj = GetPropertyValue<object>(item, "Value") ?? GetFieldValue<object>(item, "value");
                                    if (keyObj != null && valObj != null)
                                    {
                                        dict[Convert.ToInt32(keyObj)] = valObj;
                                    }
                                }
                            }
                            statsDict = dict;
                        }
                    }

                    if (statsDict != null)
                    {
                        float foodVal = GetStatValueFromDict(statsDict, 3); // Hunger
                        float sleepVal = GetStatValueFromDict(statsDict, 2); // Sleep
                        float recVal = GetStatValueFromDict(statsDict, 13); // Entertaiment
                        float comfortVal = GetStatValueFromDict(statsDict, 23); // Comfort
                        float beautyVal = GetStatValueFromDict(statsDict, 22); // Beauty

                        context.Needs = new NeedsContext
                        {
                            Food = foodVal,
                            Water = GetStatValueFromDict(statsDict, 12), // Alcohol/Drink need
                            Rest = sleepVal,
                            Recreation = recVal,
                            Comfort = comfortVal,
                            Beauty = beautyVal,
                            Privacy = 100f
                        };

                        LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractNeeds] Extracted from Stats: Food={foodVal:F1}, Rest={sleepVal:F1}, Recreation={recVal:F1}, Comfort={comfortVal:F1}, Beauty={beautyVal:F1}");
                        return;
                    }
                }

                var needs = GetFieldValue<object>(source, "needs");
                if (needs != null)
                {
                    context.Needs = new NeedsContext
                    {
                        Food = GetNeedValue(needs, "food"),
                        Water = GetNeedValue(needs, "water"),
                        Rest = GetNeedValue(needs, "rest"),
                        Recreation = GetNeedValue(needs, "recreation"),
                        Comfort = GetNeedValue(needs, "comfort"),
                        Beauty = GetNeedValue(needs, "beauty"),
                        Privacy = GetNeedValue(needs, "privacy")
                    };
                }
                LLMNPCsPlugin.LogToFile("[NPCContextExtractor:ExtractNeeds] Complete");
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log?.LogDebug($"[ExtractNeeds] Failed: {ex.Message}");
                LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractNeeds] ERROR: {ex.Message}");
            }
        }

        private static void ExtractSkills(object source, NPCContext context)
        {
            LLMNPCsPlugin.LogToFile("[NPCContextExtractor:ExtractSkills] Starting");
            try
            {
                var humanoidInstance = GetPropertyValue<object>(source, "HumanoidInstance") ?? source;

                context.Profession = GetFirstString(humanoidInstance, "profession", "Profession", "job", "Job", "role") ?? "unemployed";
                if (string.IsNullOrWhiteSpace(context.BackgroundOrRole))
                    context.BackgroundOrRole = context.Profession;
                
                var skills = GetPropertyValue<object>(humanoidInstance, "Skills") ?? GetFieldValue<object>(humanoidInstance, "skills");
                if (skills == null) skills = GetFirstObject(humanoidInstance, "skillTracker", "skillSet");
                if (skills != null)
                {
                    if (skills is System.Collections.IDictionary dict)
                    {
                        foreach (var key in dict.Keys)
                        {
                            var value = dict[key];
                            var skillName = key?.ToString() ?? GetFirstString(value, "Name", "name", "Id", "id") ?? "unknown_skill";
                            var level = GetFirstInt(value, "Level", "level", "Value", "value");
                            context.Skills[skillName] = level;
                            var xp = GetFirstFloat(value, "Experience", "experience", "Xp", "xp", "Progress", "progress");
                            if (xp > 0f) context.SkillExperience[skillName] = xp;
                        }
                    }
                    else if (skills is System.Collections.IEnumerable skillList)
                    {
                        foreach (var skill in skillList)
                        {
                            if (skill == null) continue;
                            var skillName = GetFirstString(skill, "Name", "name", "Id", "id") ?? skill.GetType().Name;
                            var level = GetFirstInt(skill, "Level", "level", "Value", "value");
                            context.Skills[skillName] = level;
                            var xp = GetFirstFloat(skill, "Experience", "experience", "Xp", "xp", "Progress", "progress");
                            if (xp > 0f) context.SkillExperience[skillName] = xp;
                        }
                    }
                }

                var traits = GetFirstObject(humanoidInstance, "traits", "Traits", "personalityTraits");
                if (traits is System.Collections.IEnumerable traitList)
                {
                    foreach (var t in traitList)
                    {
                        var traitName = GetFirstString(t, "Name", "name", "Id", "id", "Label", "label");
                        if (!string.IsNullOrEmpty(traitName) && !context.Traits.Contains(traitName))
                            context.Traits.Add(traitName);
                    }
                }

                var perkIds = GetPropertyValue<object>(humanoidInstance, "Perks") ?? GetFieldValue<object>(humanoidInstance, "perkIds");
                if (perkIds is System.Collections.IEnumerable pList)
                {
                    foreach (var p in pList)
                    {
                        var perkName = p?.ToString();
                        if (!string.IsNullOrEmpty(perkName) && !context.Perks.Contains(perkName))
                            context.Perks.Add(perkName);
                    }
                }
                else
                {
                    var perks = GetFirstObject(humanoidInstance, "perks", "Perks", "passives", "Passives");
                    if (perks is System.Collections.IEnumerable perkList)
                    {
                        foreach (var p in perkList)
                        {
                            var perkName = GetFirstString(p, "Name", "name", "Id", "id", "Label", "label");
                            if (!string.IsNullOrEmpty(perkName) && !context.Perks.Contains(perkName))
                                context.Perks.Add(perkName);
                        }
                    }
                }

                var ageEffectors = GetFieldValue<object>(humanoidInstance, "ageEffectors");
                if (ageEffectors is System.Collections.IEnumerable ageList && !(ageEffectors is string))
                {
                    foreach (var ag in ageList)
                    {
                        var agStr = ag?.ToString();
                        if (!string.IsNullOrEmpty(agStr) && !context.Traits.Contains(agStr))
                            context.Traits.Add(agStr);
                    }
                }

                var tags = GetFirstObject(humanoidInstance, "tags", "Tags", "backgroundTags", "BackgroundTags");
                if (tags is System.Collections.IEnumerable tagList)
                {
                    foreach (var tag in tagList)
                    {
                        var label = tag?.ToString();
                        if (!string.IsNullOrWhiteSpace(label) && !context.BackgroundTags.Contains(label))
                            context.BackgroundTags.Add(label);
                    }
                }

                ExtractWorkPriorities(source, context);
                ApplySkillDerivedProfession(context);
                LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractSkills] Profession: {context.Profession}");
            }
            catch (Exception ex) { LLMNPCsPlugin.Log?.LogDebug($"[ExtractSkills] Failed: {ex.Message}"); LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractSkills] ERROR: {ex.Message}"); }
        }

        private static void ApplySkillDerivedProfession(NPCContext context)
        {
            if (context?.Skills == null || context.Skills.Count == 0) return;

            var current = context.Profession ?? string.Empty;
            var currentLower = current.Trim().ToLowerInvariant();
            var isGeneric = string.IsNullOrWhiteSpace(current) ||
                            currentLower == "none" ||
                            currentLower == "no role" ||
                            currentLower == "unemployed" ||
                            currentLower == "worker" ||
                            currentLower == "settler" ||
                            currentLower == "unknown";
            if (!isGeneric) return;

            var top = context.Skills
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .First();

            var inferred = InferProfessionFromSkill(top.Key);
            if (string.IsNullOrWhiteSpace(inferred)) return;

            context.Profession = inferred;
            context.BackgroundOrRole = inferred;
            LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractSkills] Inferred profession {inferred} from top skill {top.Key}:{top.Value}");
        }

        private static string InferProfessionFromSkill(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName)) return null;
            var key = skillName.Trim().ToLowerInvariant();
            if (key.Contains("intellectual")) return "Scholar";
            if (key.Contains("botany")) return "Farmer";
            if (key.Contains("culinary")) return "Cook";
            if (key.Contains("carpentry") || key.Contains("construction")) return "Builder";
            if (key.Contains("mining")) return "Miner";
            if (key.Contains("smithing")) return "Smith";
            if (key.Contains("tailoring")) return "Tailor";
            if (key.Contains("animal")) return "Animal Handler";
            if (key.Contains("marksman") || key.Contains("melee")) return "Guard";
            if (key.Contains("speechcraft")) return "Steward";
            if (key.Contains("art")) return "Artisan";
            return null;
        }

        private static void ExtractEquipment(object source, NPCContext context)
        {
            LLMNPCsPlugin.LogToFile("[NPCContextExtractor:ExtractEquipment] Starting");
            try
            {
                var equipment = GetFirstObject(source, "equipment", "Equipment", "gear", "Gear", "apparel", "Apparel");
                if (equipment != null)
                {
                    context.Equipment = new EquipmentContext
                    {
                        Weapon = GetItemName(GetFirstObject(equipment, "weapon", "Weapon", "mainHand", "MainHand")),
                        Armor = GetItemName(GetFirstObject(equipment, "armor", "Armor", "body", "Body")),
                        Helmet = GetItemName(GetFirstObject(equipment, "helmet", "Helmet", "head", "Head")),
                        Clothing = GetItemName(GetFirstObject(equipment, "clothing", "Clothing", "outfit", "Outfit"))
                    };
                }

                var inventory = GetFirstObject(source, "inventory", "Inventory", "backpack", "Backpack", "carriedItems", "CarriedItems");
                if (inventory is System.Collections.IEnumerable items)
                {
                    foreach (var item in items)
                    {
                        var name = GetItemName(item);
                        if (!string.IsNullOrEmpty(name))
                            context.Inventory.Add(name);
                    }
                }
                LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractEquipment] Inventory count: {context.Inventory?.Count ?? 0}");
            }
            catch (Exception ex) { LLMNPCsPlugin.Log?.LogDebug($"[ExtractEquipment] Failed: {ex.Message}"); LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractEquipment] ERROR: {ex.Message}"); }
        }

        private static void ExtractCurrentActivity(object source, NPCContext context)
        {
            LLMNPCsPlugin.LogToFile("[NPCContextExtractor:ExtractCurrentActivity] Starting");
            try
            {
                var currentJob = GetFirstObject(source, "currentJob", "CurrentJob", "job", "Job", "activeTask", "ActiveTask", "currentTask", "CurrentTask");
                if (currentJob != null)
                {
                    context.CurrentActivity = new ActivityContext
                    {
                        Type = GetFirstString(currentJob, "JobType", "jobType", "Type", "type", "TaskType", "taskType") ?? "idle",
                        Description = GetFirstString(currentJob, "Description", "description", "Label", "label") ?? "doing nothing",
                        Target = GetItemName(GetFirstObject(currentJob, "target", "Target", "destination", "Destination")),
                        Progress = GetFirstFloat(currentJob, "progress", "Progress", "completion", "Completion")
                    };
                    LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractCurrentActivity] Activity: {context.CurrentActivity.Type}");
                }
            }
            catch (Exception ex) { LLMNPCsPlugin.Log?.LogDebug($"[ExtractCurrentActivity] Failed: {ex.Message}"); LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractCurrentActivity] ERROR: {ex.Message}"); }
        }

        private static void ExtractEnvironment(object source, NPCContext context)
        {
            LLMNPCsPlugin.LogToFile("[NPCContextExtractor:ExtractEnvironment] Starting");
            try
            {
                var sourceComponent = source as Component;
                var position = sourceComponent?.transform?.position ?? Vector3.zero;
                
                context.Environment = new EnvironmentContext
                {
                    Position = new PositionContext
                    {
                        X = position.x,
                        Y = position.y,
                        Z = position.z
                    },
                    Room = GetCurrentRoom(source),
                    TimeOfDay = GetGameTime(),
                    Weather = GetWeather(),
                    NearbyThreats = GetNearbyThreats(sourceComponent)
                };
                LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractEnvironment] Weather: {context.Environment.Weather}");
            }
            catch (Exception ex) { LLMNPCsPlugin.Log?.LogDebug($"[ExtractEnvironment] Failed: {ex.Message}"); LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractEnvironment] ERROR: {ex.Message}"); }
        }

        private static void ExtractSocial(object source, NPCContext context)
        {
            LLMNPCsPlugin.LogToFile("[NPCContextExtractor:ExtractSocial] Starting");
            try
            {
                var humanoidInstance = GetPropertyValue<object>(source, "HumanoidInstance") ?? source;
                
                context.SocialLogs = new List<string>();
                context.BeliefLogs = new List<string>();

                var workerBehaviour = GetPropertyValue<object>(humanoidInstance, "WorkerBehaviour") ?? GetFieldValue<object>(humanoidInstance, "workerBehaviour");
                if (workerBehaviour != null)
                {
                    var workerSocial = GetPropertyValue<object>(workerBehaviour, "WorkerSocial") ?? GetFieldValue<object>(workerBehaviour, "workerSocial");
                    if (workerSocial != null)
                    {
                        var affectionEffectorsLog = GetPropertyValue<object>(workerSocial, "AffectionEffectorsLog") ?? GetFieldValue<object>(workerSocial, "affectionEffectorsLog");
                        if (affectionEffectorsLog is System.Collections.IEnumerable effLogs)
                        {
                            foreach (var log in effLogs)
                            {
                                if (log != null)
                                {
                                    var txt = log.ToString();
                                    if (!string.IsNullOrWhiteSpace(txt)) context.SocialLogs.Add(txt);
                                }
                            }
                        }
                    }
                }

                var humanoidBelief = GetPropertyValue<object>(humanoidInstance, "HumanoidBelief") ?? GetFieldValue<object>(humanoidInstance, "humanoidBelief");
                if (humanoidBelief != null)
                {
                    var religiousEffectorsLog = GetPropertyValue<object>(humanoidBelief, "ReligiousEffectorsLog") ?? GetFieldValue<object>(humanoidBelief, "religiousEffectorsLog");
                    if (religiousEffectorsLog is System.Collections.IEnumerable belLogs)
                    {
                        foreach (var log in belLogs)
                        {
                            if (log != null)
                            {
                                var txt = log.ToString();
                                if (!string.IsNullOrWhiteSpace(txt)) context.BeliefLogs.Add(txt);
                            }
                        }
                    }
                }

                var opinions = GetFirstObject(humanoidInstance, "opinions", "Opinions", "relationships", "Relationships", "social", "Social");
                if (opinions is System.Collections.IDictionary dict)
                {
                    foreach (var key in dict.Keys)
                    {
                        var value = dict[key];
                        var opinion = GetFirstFloat(value, "value", "Value", "opinion", "Opinion", "friendship", "Friendship");
                        var trust = GetFirstFloat(value, "trust", "Trust");
                        var relationKey = key?.ToString() ?? GetFirstString(value, "id", "Id", "name", "Name") ?? "unknown";
                        context.Relationships[relationKey] = new RelationshipContext
                        {
                            NPCId = relationKey,
                            Opinion = opinion,
                            Type = GetRelationshipType(opinion),
                            Trust = trust
                        };
                    }
                }

                var reputation = GetFirstObject(source, "reputation", "Reputation", "factionReputation", "FactionReputation");
                if (reputation is System.Collections.IDictionary repDict)
                {
                    foreach (var key in repDict.Keys)
                    {
                        var repVal = ToSingle(repDict[key]);
                        var repKey = key?.ToString() ?? "unknown";
                        context.Reputation[repKey] = repVal;
                    }
                }
                LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractSocial] Relationships count: {context.Relationships?.Count ?? 0}");
            }
            catch (Exception ex) { LLMNPCsPlugin.Log?.LogDebug($"[ExtractSocial] Failed: {ex.Message}"); LLMNPCsPlugin.LogToFile($"[NPCContextExtractor:ExtractSocial] ERROR: {ex.Message}"); }
        }

        private static void ExtractWorkPriorities(object source, NPCContext context)
        {
            var prioritiesObj = GetFirstObject(source, "workPriorities", "WorkPriorities", "jobPriorities", "JobPriorities", "priorities", "Priorities");
            if (prioritiesObj is System.Collections.IDictionary dict)
            {
                foreach (var key in dict.Keys)
                {
                    var label = key?.ToString() ?? "unknown";
                    context.WorkPriorities[label] = ToInt32(dict[key]);
                }
            }
            else if (prioritiesObj is System.Collections.IEnumerable list)
            {
                foreach (var item in list)
                {
                    if (item == null) continue;
                    var label = GetFirstString(item, "Name", "name", "job", "Job", "type", "Type") ?? item.GetType().Name;
                    var prio = GetFirstInt(item, "Priority", "priority", "Value", "value", "Level", "level");
                    context.WorkPriorities[label] = prio;
                }
            }
        }

        private static void LogExtractionDiagnostics(NPCContext context)
        {
            LLMNPCsPlugin.LogToFile(
                $"[NPCContextExtractor:Diagnostics] npc={context.Name}; skills={context.Skills?.Count ?? 0}; perks={context.Perks?.Count ?? 0}; states={context.States?.Count ?? 0}; moodLogs={context.MoodLogs?.Count ?? 0}; socLogs={context.SocialLogs?.Count ?? 0}");
        }

        private static void AppendStateFlags(object source, List<string> states, params string[] flagNames)
        {
            if (states == null || flagNames == null)
                return;

            foreach (var flagName in flagNames)
            {
                if (GetFirstBool(source, flagName))
                    states.Add(flagName);
            }
        }


        // Helper methods
        internal static T GetFieldValue<T>(object obj, string fieldName)
        {
            if (obj == null) return default;
            var field = obj.GetType().GetField(fieldName, 
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                return (T)field.GetValue(obj);
            }
            return default;
        }

        internal static T GetPropertyValue<T>(object obj, string propertyName)
        {
            if (obj == null) return default;
            var prop = obj.GetType().GetProperty(propertyName, 
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null)
            {
                return (T)prop.GetValue(obj);
            }
            return default;
        }

        internal static bool SetFieldValue(object obj, string fieldName, object value)
        {
            if (obj == null) return false;
            var field = obj.GetType().GetField(fieldName, 
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(obj, value);
                return true;
            }
            return false;
        }

        internal static bool SetPropertyValue(object obj, string propertyName, object value)
        {
            if (obj == null) return false;
            var prop = obj.GetType().GetProperty(propertyName, 
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(obj, value, null);
                return true;
            }
            return false;
        }

        internal static object GetFirstObject(object obj, params string[] memberNames)
        {
            if (obj == null || memberNames == null) return null;
            foreach (var name in memberNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var fieldValue = GetFieldValue<object>(obj, name);
                if (fieldValue != null) return fieldValue;
                var propValue = GetPropertyValue<object>(obj, name);
                if (propValue != null) return propValue;
            }
            return null;
        }

        internal static string GetFirstString(object obj, params string[] memberNames)
        {
            var value = GetFirstObject(obj, memberNames);
            if (value == null) return null;
            var text = value.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        internal static int GetFirstInt(object obj, params string[] memberNames)
        {
            var value = GetFirstObject(obj, memberNames);
            return ToInt32(value);
        }

        internal static float GetFirstFloat(object obj, params string[] memberNames)
        {
            var value = GetFirstObject(obj, memberNames);
            return ToSingle(value);
        }

        internal static bool GetFirstBool(object obj, params string[] memberNames)
        {
            var value = GetFirstObject(obj, memberNames);
            if (value == null) return false;
            if (value is bool b) return b;
            if (bool.TryParse(value.ToString(), out var parsed)) return parsed;
            return false;
        }

        internal static int ToInt32(object value)
        {
            if (value == null) return 0;
            if (value is int i) return i;
            if (value is long l) return (int)l;
            if (value is short s) return s;
            if (value is byte b) return b;
            if (value is float f) return (int)f;
            if (value is double d) return (int)d;
            if (int.TryParse(value.ToString(), out var parsed)) return parsed;
            return 0;
        }

        internal static float ToSingle(object value)
        {
            if (value == null) return 0f;
            if (value is float f) return f;
            if (value is double d) return (float)d;
            if (value is decimal m) return (float)m;
            if (value is int i) return i;
            if (value is long l) return l;
            if (float.TryParse(value.ToString(), out var parsed)) return parsed;
            return 0f;
        }

        private static float GetNeedValue(object needs, string needName)
        {
            var need = GetFieldValue<object>(needs, needName);
            if (need != null)
            {
                return GetFieldValue<float>(need, "value");
            }
            return 50f;
        }

        private static float GetStatValueFromDict(System.Collections.IDictionary statsDict, int statTypeInt)
        {
            try
            {
                if (statsDict == null) return 50f;
                foreach (var key in statsDict.Keys)
                {
                    if (key != null && Convert.ToInt32(key) == statTypeInt)
                    {
                        var statInstance = statsDict[key];
                        if (statInstance != null)
                        {
                            var currentObj = GetPropertyValue<object>(statInstance, "Current") ?? GetFieldValue<object>(statInstance, "current");
                            return ToSingle(currentObj);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log?.LogDebug($"[GetStatValueFromDict] Error for stat {statTypeInt}: {ex.Message}");
            }
            return 50f;
        }

        internal static bool SetStatValue(object source, int statTypeInt, float value)
        {
            try
            {
                var statsObj = GetPropertyValue<object>(source, "Stats") ?? GetFieldValue<object>(source, "stats");
                if (statsObj == null)
                {
                    var humanoidInstance = GetPropertyValue<object>(source, "HumanoidInstance") ?? source;
                    statsObj = GetPropertyValue<object>(humanoidInstance, "Stats") ?? GetFieldValue<object>(humanoidInstance, "stats");
                }

                if (statsObj != null)
                {
                    System.Collections.IDictionary statsDict = null;
                    var statsFieldVal = GetFieldValue<object>(statsObj, "stats");
                    if (statsFieldVal != null)
                    {
                        statsDict = GetPropertyValue<System.Collections.IDictionary>(statsFieldVal, "Dictionary") ??
                                    GetFieldValue<System.Collections.IDictionary>(statsFieldVal, "dictionary");
                    }

                    if (statsDict == null)
                    {
                        var statsEnum = GetPropertyValue<System.Collections.IEnumerable>(statsObj, "Stats") ??
                                        GetFieldValue<System.Collections.IEnumerable>(statsObj, "stats");
                        if (statsEnum != null)
                        {
                            foreach (var item in statsEnum)
                            {
                                if (item != null)
                                {
                                    var keyObj = GetPropertyValue<object>(item, "Key") ?? GetFieldValue<object>(item, "key");
                                    var valObj = GetPropertyValue<object>(item, "Value") ?? GetFieldValue<object>(item, "value");
                                    if (keyObj != null && Convert.ToInt32(keyObj) == statTypeInt && valObj != null)
                                    {
                                        var method = valObj.GetType().GetMethod("SetCurrent", BindingFlags.Public | BindingFlags.Instance);
                                        if (method != null)
                                        {
                                            method.Invoke(valObj, new object[] { value });
                                            LLMNPCsPlugin.LogToFile($"[SetStatValue] Successfully set stat {statTypeInt} to {value} via SetCurrent (fallback enum)");
                                            return true;
                                        }
                                        else
                                        {
                                            var setSuccess = SetFieldValue(valObj, "current", value);
                                            LLMNPCsPlugin.LogToFile($"[SetStatValue] Set stat {statTypeInt} field 'current' directly to {value}: {setSuccess} (fallback enum)");
                                            return setSuccess;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (statsDict != null)
                    {
                        foreach (var key in statsDict.Keys)
                        {
                            if (key != null && Convert.ToInt32(key) == statTypeInt)
                            {
                                var statInstance = statsDict[key];
                                if (statInstance != null)
                                {
                                    var method = statInstance.GetType().GetMethod("SetCurrent", BindingFlags.Public | BindingFlags.Instance);
                                    if (method != null)
                                    {
                                        method.Invoke(statInstance, new object[] { value });
                                        LLMNPCsPlugin.LogToFile($"[SetStatValue] Successfully set stat {statTypeInt} to {value} via SetCurrent");
                                        return true;
                                    }
                                    else
                                    {
                                        var setSuccess = SetFieldValue(statInstance, "current", value);
                                        LLMNPCsPlugin.LogToFile($"[SetStatValue] Set stat {statTypeInt} field 'current' directly to {value}: {setSuccess}");
                                        return setSuccess;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log?.LogError($"[SetStatValue] Error setting stat {statTypeInt} to {value}: {ex}");
            }
            return false;
        }

        private static List<string> GetStatusEffects(object health)
        {
            var effects = new List<string>();
            try
            {
                var statusEffects = GetFieldValue<object>(health, "statusEffects");
                if (statusEffects is System.Collections.IEnumerable list)
                {
                    foreach (var effect in list)
                    {
                        var name = GetPropertyValue<string>(effect, "Name");
                        if (!string.IsNullOrEmpty(name))
                            effects.Add(name);
                    }
                }
            }
            catch (Exception ex) { LLMNPCsPlugin.Log?.LogDebug($"[GetStatusEffects] Failed: {ex.Message}"); }
            return effects;
        }

        private static string GetItemName(object item)
        {
            if (item == null) return null;
            return GetPropertyValue<string>(item, "ItemName") ??
                   GetPropertyValue<string>(item, "Name") ??
                   GetFieldValue<string>(item, "name");
        }

        private static string GetCurrentRoom(object source)
        {
            try
            {
                var room = GetFieldValue<object>(source, "currentRoom");
                return GetPropertyValue<string>(room, "RoomName") ?? "outside";
            }
            catch (Exception ex) { LLMNPCsPlugin.Log?.LogDebug($"[GetCurrentRoom] Failed: {ex.Message}"); }
            return "unknown";
        }

        private static string GetGameTime()
        {
            try
            {
                // Try to access game time manager
                var timeManagerType = Type.GetType("TimeManager, Assembly-CSharp");
                var timeManager = SafeFindSingleByType(timeManagerType, "GetGameTime: TimeManager");
                if (timeManager != null)
                {
                    var hour = GetPropertyValue<int>(timeManager, "Hour");
                    var minute = GetPropertyValue<int>(timeManager, "Minute");
                    return $"{hour:D2}:{minute:D2}";
                }
            }
            catch (Exception ex) { LLMNPCsPlugin.Log?.LogDebug($"[GetGameTime] Failed: {ex.Message}"); }
            return "unknown";
        }

        private static string GetWeather()
        {
            try
            {
                var weatherType = Type.GetType("WeatherSystem, Assembly-CSharp");
                var weather = SafeFindSingleByType(weatherType, "GetWeather: WeatherSystem");
                if (weather != null)
                {
                    return GetPropertyValue<string>(weather, "CurrentWeather") ?? "clear";
                }
            }
            catch (Exception ex) { LLMNPCsPlugin.Log?.LogDebug($"[GetWeather] Failed: {ex.Message}"); }
            return "clear";
        }

        private static UnityEngine.Object SafeFindSingleByType(Type targetType, string context)
        {
            if (targetType == null)
            {
                LogInvalidFindTargetTypeOnce(targetType, context, "target type is null");
                return null;
            }

            if (!typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                LogInvalidFindTargetTypeOnce(targetType, context, "target type is not assignable to UnityEngine.Object");
                return null;
            }

            if (!typeof(Component).IsAssignableFrom(targetType) && !typeof(GameObject).IsAssignableFrom(targetType))
            {
                LogInvalidFindTargetTypeOnce(targetType, context, "target type is not Component/GameObject");
                return null;
            }

            if (targetType.IsAbstract)
            {
                LogInvalidFindTargetTypeOnce(targetType, context, "target type is abstract");
                return null;
            }

            if (targetType.ContainsGenericParameters)
            {
                LogInvalidFindTargetTypeOnce(targetType, context, "target type has unbound generic parameters");
                return null;
            }

            try
            {
                return UnityEngine.Object.FindObjectOfType(targetType);
            }
            catch (Exception ex)
            {
                LogInvalidFindTargetTypeOnce(targetType, context, $"FindObjectOfType threw: {ex.Message}");
                return null;
            }
        }

        private static void LogInvalidFindTargetTypeOnce(Type targetType, string context, string reason)
        {
            var typeName = targetType?.FullName ?? "<null>";
            var caller = GetCallingMethodName();
            var key = $"{typeName}|{context}|{reason}";
            if (!_loggedInvalidFindTypes.Add(key))
                return;

            LLMNPCsPlugin.LogToFile($"[NPCContextExtractor] Rejected dynamic find target type '{typeName}' in {context} (caller: {caller}): {reason}");
        }

        private static string GetCallingMethodName()
        {
            try
            {
                var frames = new StackTrace(2, false).GetFrames();
                if (frames == null) return "unknown";

                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    if (method == null) continue;
                    var declaringType = method.DeclaringType;
                    if (declaringType == typeof(NPCContextExtractor)) continue;
                    return $"{declaringType?.FullName ?? "unknown"}.{method.Name}";
                }
            }
            catch { }

            return "unknown";
        }

        private static List<string> GetNearbyThreats(Component settlerComponent)
        {
            var threats = new List<string>();
            try
            {
                var position = settlerComponent?.transform?.position ?? Vector3.zero;
                var radius = 50f; // Check 50 unit radius
                
                // Look for enemies, fire, etc.
                var colliders = Physics.OverlapSphere(position, radius);
                foreach (var col in colliders)
                {
                    // Using string-based type check to avoid compile errors if types aren't available
                    var typeName = col.gameObject.name.ToLower();
                    if (typeName.Contains("raider") || typeName.Contains("enemy"))
                        threats.Add("raider");
                    if (typeName.Contains("fire"))
                        threats.Add("fire");
                }
            }
            catch (Exception ex) { LLMNPCsPlugin.Log?.LogDebug($"[GetNearbyThreats] Failed: {ex.Message}"); }
            return threats.Distinct().ToList();
        }

        private static string GetRelationshipType(float opinion)
        {
            if (opinion > 70) return "friend";
            if (opinion > 30) return "acquaintance";
            if (opinion < -30) return "rival";
            if (opinion < -70) return "enemy";
            return "neutral";
        }
    }

    // Context classes for serialization
    public class NPCContext
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("age")] public int Age { get; set; }
        [JsonProperty("gender")] public string Gender { get; set; }
        [JsonProperty("background_or_role")] public string BackgroundOrRole { get; set; }
        [JsonProperty("pseudonym")] public string Pseudonym { get; set; }
        [JsonProperty("health")] public HealthContext Health { get; set; }
        [JsonProperty("mood")] public string Mood { get; set; }
        [JsonProperty("mood_score")] public float MoodScore { get; set; }
        [JsonProperty("vitals")] public Dictionary<string, string> Vitals { get; set; }
        [JsonProperty("states")] public List<string> States { get; set; }
        [JsonProperty("needs")] public NeedsContext Needs { get; set; }
        [JsonProperty("profession")] public string Profession { get; set; }
        [JsonProperty("skills")] public Dictionary<string, int> Skills { get; set; }
        [JsonProperty("skill_experience")] public Dictionary<string, float> SkillExperience { get; set; }
        [JsonProperty("traits")] public List<string> Traits { get; set; }
        [JsonProperty("perks")] public List<string> Perks { get; set; }
        [JsonProperty("background_tags")] public List<string> BackgroundTags { get; set; }
        [JsonProperty("equipment")] public EquipmentContext Equipment { get; set; }
        [JsonProperty("inventory")] public List<string> Inventory { get; set; }
        [JsonProperty("current_activity")] public ActivityContext CurrentActivity { get; set; }
        [JsonProperty("work_priorities")] public Dictionary<string, int> WorkPriorities { get; set; }
        [JsonProperty("environment")] public EnvironmentContext Environment { get; set; }
        [JsonProperty("relationships")] public Dictionary<string, RelationshipContext> Relationships { get; set; }
        [JsonProperty("reputation")] public Dictionary<string, float> Reputation { get; set; }
        [JsonProperty("mood_logs")] public List<string> MoodLogs { get; set; }
        [JsonProperty("social_logs")] public List<string> SocialLogs { get; set; }
        [JsonProperty("belief_logs")] public List<string> BeliefLogs { get; set; }
        [JsonProperty("colony_wealth")] public float ColonyWealth { get; set; }
        
        // Hierarchical memory context from SQLite (not serialized to JSON)
        [JsonIgnore] public string MemoryContext { get; set; }
    }

    public class HealthContext
    {
        [JsonProperty("current")] public float Current { get; set; }
        [JsonProperty("max")] public float Max { get; set; }
        [JsonProperty("status_effects")] public List<string> StatusEffects { get; set; }
        
        [JsonIgnore] public float Overall => Max > 0 ? (Current / Max) * 100f : 0f;
    }

    public class NeedsContext
    {
        [JsonProperty("food")] public float Food { get; set; }
        [JsonProperty("water")] public float Water { get; set; }
        [JsonProperty("rest")] public float Rest { get; set; }
        [JsonProperty("recreation")] public float Recreation { get; set; }
        [JsonProperty("comfort")] public float Comfort { get; set; }
        [JsonProperty("beauty")] public float Beauty { get; set; }
        [JsonProperty("privacy")] public float Privacy { get; set; }

        public List<string> GetCriticalNeeds()
        {
            var critical = new List<string>();
            if (Food < 20) critical.Add("food");
            if (Water < 20) critical.Add("water");
            if (Rest < 20) critical.Add("rest");
            return critical;
        }
    }

    public class EquipmentContext
    {
        [JsonProperty("weapon")] public string Weapon { get; set; }
        [JsonProperty("armor")] public string Armor { get; set; }
        [JsonProperty("helmet")] public string Helmet { get; set; }
        [JsonProperty("clothing")] public string Clothing { get; set; }
    }

    public class ActivityContext
    {
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("progress")] public float Progress { get; set; }
    }

    public class EnvironmentContext
    {
        [JsonProperty("position")] public PositionContext Position { get; set; }
        [JsonProperty("room")] public string Room { get; set; }
        [JsonProperty("time_of_day")] public string TimeOfDay { get; set; }
        [JsonProperty("weather")] public string Weather { get; set; }
        [JsonProperty("nearby_threats")] public List<string> NearbyThreats { get; set; }
        
        [JsonIgnore] public int HostilesNearby => NearbyThreats?.Count ?? 0;
    }

    public class PositionContext
    {
        [JsonProperty("x")] public float X { get; set; }
        [JsonProperty("y")] public float Y { get; set; }
        [JsonProperty("z")] public float Z { get; set; }
    }

    public class RelationshipContext
    {
        [JsonProperty("npc_id")] public string NPCId { get; set; }
        [JsonProperty("opinion")] public float Opinion { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("trust")] public float Trust { get; set; }
    }

    // Placeholder types for compilation (will be resolved from game DLLs at runtime)
    public class Settler : MonoBehaviour 
    { 
        public string Name { get; set; }
    }
    public class Raider : MonoBehaviour { }
    public class Fire : MonoBehaviour { }
}
