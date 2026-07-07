using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix.Repository;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.State.WorkerJobs;
using NSMedieval.StatsSystem;
using NSMedieval.Types;
using UnityEngine;

namespace NSMedieval.State;

public class HumanoidInstanceGoalPreferences
{
	private HumanoidInstance humanoid;

	private Dictionary<GoalPreference, GoalPreferenceLevelData> defaultGoalPrefDictionary;

	private Dictionary<GoalPreference, GoalPreferenceLevelData> secondaryGoalPrefDictionary;

	public Dictionary<GoalPreference, GoalPreferenceLevelData> DefaultGoalPrefDictionary => defaultGoalPrefDictionary;

	public Dictionary<GoalPreference, GoalPreferenceLevelData> SecondaryGoalPrefDictionary => secondaryGoalPrefDictionary;

	public bool HasGoalPreferences
	{
		get
		{
			Dictionary<GoalPreference, GoalPreferenceLevelData> dictionary = defaultGoalPrefDictionary;
			if (dictionary == null || dictionary.Count <= 0)
			{
				dictionary = secondaryGoalPrefDictionary;
				if (dictionary != null)
				{
					return dictionary.Count > 0;
				}
				return false;
			}
			return true;
		}
	}

	public HumanoidInstanceGoalPreferences(HumanoidInstance humanoidOwner)
	{
		SetOwner(humanoidOwner);
	}

	public Dictionary<GoalPreference, GoalPreferenceLevelData> GetGoalPrefDictionary()
	{
		if (!SecondaryActive())
		{
			return defaultGoalPrefDictionary;
		}
		return secondaryGoalPrefDictionary;
	}

	public void SetDefault(Dictionary<GoalPreference, GoalPreferenceLevelData> dictionary)
	{
		defaultGoalPrefDictionary = dictionary;
	}

	public void SetSecondary(Dictionary<GoalPreference, GoalPreferenceLevelData> dictionary)
	{
		secondaryGoalPrefDictionary = dictionary;
	}

	private bool SecondaryActive()
	{
		HumanoidInstance humanoidInstance = humanoid;
		if (humanoidInstance != null && humanoidInstance.ActiveBehaviour is WorkerBehaviour workerBehaviour)
		{
			return workerBehaviour.HumanoidRoleOwner.AssignedRole;
		}
		return false;
	}

	public void SetOwner(HumanoidInstance humanoid)
	{
		this.humanoid = humanoid;
		if (humanoid == null)
		{
			defaultGoalPrefDictionary = null;
			secondaryGoalPrefDictionary = null;
		}
		else
		{
			defaultGoalPrefDictionary = new Dictionary<GoalPreference, GoalPreferenceLevelData>();
			secondaryGoalPrefDictionary = new Dictionary<GoalPreference, GoalPreferenceLevelData>();
		}
	}

	public IEnumerable<GoalPreferenceLevelData> GetGoalPrefLevelByGoalId(string goalId)
	{
		AttackType attackType = CombatUtils.GetAttackType(humanoid);
		List<GoalPreferenceLevelData> list = new List<GoalPreferenceLevelData>();
		foreach (KeyValuePair<GoalPreference, GoalPreferenceLevelData> item in GetGoalPrefDictionary())
		{
			if (!item.Key.IsRelatedToGoal(goalId))
			{
				continue;
			}
			if (goalId.Equals("AttackGoal"))
			{
				if (item.Key.RelatedSkill == SkillType.Marksman && (attackType == AttackType.RangeChargeBefore || attackType == AttackType.RangeChargeAfter))
				{
					int goalPreferenceLevel = humanoid.Skills.GetSkill(SkillType.Marksman).GetGoalPreferenceLevel();
					list.Add(Repository<GoalPreferenceLevelRepository, GoalPreferenceLevelData>.Instance.GetDataByPreferenceLevel(goalPreferenceLevel));
				}
				else if (item.Key.RelatedSkill == SkillType.Melee && attackType == AttackType.Melee)
				{
					int goalPreferenceLevel = humanoid.Skills.GetSkill(SkillType.Melee).GetGoalPreferenceLevel();
					list.Add(Repository<GoalPreferenceLevelRepository, GoalPreferenceLevelData>.Instance.GetDataByPreferenceLevel(goalPreferenceLevel));
				}
			}
			else if (item.Key.RelatedSkill == SkillType.None)
			{
				list.Add(item.Value);
			}
			else
			{
				int goalPreferenceLevel = humanoid.Skills.GetSkill(item.Key.RelatedSkill).GetGoalPreferenceLevel();
				list.Add(Repository<GoalPreferenceLevelRepository, GoalPreferenceLevelData>.Instance.GetDataByPreferenceLevel(goalPreferenceLevel));
			}
		}
		return list;
	}

	public List<GoalPreference> GetGoalPrefByEffectorId(string effectorId)
	{
		List<GoalPreference> list = new List<GoalPreference>();
		foreach (KeyValuePair<GoalPreference, GoalPreferenceLevelData> item in GetGoalPrefDictionary())
		{
			if (item.Value.Effectors.Contains(effectorId))
			{
				list.Add(item.Key);
			}
		}
		return list;
	}

	public Dictionary<SkillType, int> GetGoalPrefSkillModifiers(Dictionary<GoalPreference, GoalPreferenceLevelData> goalPrefDictionary)
	{
		Dictionary<SkillType, int> dictionary = new Dictionary<SkillType, int>();
		foreach (KeyValuePair<GoalPreference, GoalPreferenceLevelData> item in goalPrefDictionary)
		{
			if (item.Key.RelatedSkill != 0)
			{
				if (dictionary.TryGetValue(item.Key.RelatedSkill, out var value))
				{
					GoalPreferenceLevelData cumulativeLevelData = Repository<GoalPreferenceLevelRepository, GoalPreferenceLevelData>.Instance.GetCumulativeLevelData(value, (int)item.Value.PreferenceLevel);
					dictionary[item.Key.RelatedSkill] = (int)cumulativeLevelData.PreferenceLevel;
				}
				else
				{
					dictionary.Add(item.Key.RelatedSkill, (int)item.Value.PreferenceLevel);
				}
			}
		}
		return dictionary;
	}

	public void SetGoalPreferenceSkillModifier(SkillType skill, int goalPreferenceLevel)
	{
		humanoid.Skills.GetSkill(skill).SetGoalPreferenceLevel(goalPreferenceLevel);
	}

	public void SetDefaultGoalPreferenceSkillModifiers()
	{
		SetGoalPreferenceSkillModifiers(defaultGoalPrefDictionary);
	}

	private void SetGoalPreferenceSkillModifiers(Dictionary<GoalPreference, GoalPreferenceLevelData> goalPreferences)
	{
		foreach (WorkerSkill skill in humanoid.Skills.Skills)
		{
			skill.SetGoalPreferenceLevel(0);
		}
		foreach (KeyValuePair<SkillType, int> goalPrefSkillModifier in GetGoalPrefSkillModifiers(goalPreferences))
		{
			SetGoalPreferenceSkillModifier(goalPrefSkillModifier.Key, goalPrefSkillModifier.Value);
		}
	}

	public void RecalculateDefaultGoalPreferences()
	{
		defaultGoalPrefDictionary.Clear();
		foreach (Perk perk in humanoid.Perks)
		{
			AddToGoalPrefDictionary(perk.GoalPreferences, defaultGoalPrefDictionary);
		}
		if (Repository<BackStoryRepository, BackStory>.Instance.TryGetValue(humanoid.Info.BackStoryId, out var model))
		{
			AddToGoalPrefDictionary(model.GoalPreferences, defaultGoalPrefDictionary);
		}
		if (Repository<BackgroundRepository, Background>.Instance.TryGetValue(humanoid.Info.BackgroundId, out var model2))
		{
			AddToGoalPrefDictionary(model2.GoalPreferences, defaultGoalPrefDictionary);
		}
		if (Repository<PseudonymRepository, Pseudonym>.Instance.TryGetValue(humanoid.Info.PseudonymId, out var model3))
		{
			AddToGoalPrefDictionary(model3.GoalPreferences, defaultGoalPrefDictionary);
		}
	}

	public void SetSecondaryGoalPreferences(List<StringIntPair> goalPreferences)
	{
		secondaryGoalPrefDictionary.Clear();
		foreach (var (key, value) in defaultGoalPrefDictionary)
		{
			secondaryGoalPrefDictionary.Add(key, value);
		}
		AddToGoalPrefDictionary(goalPreferences, secondaryGoalPrefDictionary);
		SetGoalPreferenceSkillModifiers(secondaryGoalPrefDictionary);
	}

	private void AddToGoalPrefDictionary(List<StringIntPair> goalPreferences, Dictionary<GoalPreference, GoalPreferenceLevelData> goalPrefDictionary)
	{
		foreach (StringIntPair goalPreference in goalPreferences)
		{
			GoalPreference byID = Repository<GoalPreferenceRepository, GoalPreference>.Instance.GetByID(goalPreference.Key);
			if (byID == null)
			{
				continue;
			}
			bool isEnabled;
			FVLogTraceInterpolationHandler messageBuilder;
			if (goalPrefDictionary.ContainsKey(byID))
			{
				messageBuilder = new FVLogTraceInterpolationHandler(28, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstanceGoalPreferences.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Modifying Goal Preference ");
					messageBuilder.AppendFormatted(byID.GetID());
					messageBuilder.AppendLiteral(": ");
					messageBuilder.AppendFormatted(goalPrefDictionary[byID].PreferenceLevel);
				}
				Log.Trace(messageBuilder);
				goalPrefDictionary[byID] = Repository<GoalPreferenceLevelRepository, GoalPreferenceLevelData>.Instance.GetCumulativeLevelData((int)goalPrefDictionary[byID].PreferenceLevel, goalPreference.Value);
			}
			else
			{
				goalPrefDictionary.Add(byID, Repository<GoalPreferenceLevelRepository, GoalPreferenceLevelData>.Instance.GetDataByPreferenceLevel(goalPreference.Value));
			}
			messageBuilder = new FVLogTraceInterpolationHandler(22, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstanceGoalPreferences.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("New Goal Preference ");
				messageBuilder.AppendFormatted(byID.GetID());
				messageBuilder.AppendLiteral(": ");
				messageBuilder.AppendFormatted(goalPrefDictionary[byID].PreferenceLevel);
			}
			Log.Trace(messageBuilder);
		}
	}

	public void ModifyGoalPreference(StringIntPair goalPreferencePair)
	{
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(22, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstanceGoalPreferences.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Add Goal Preference ");
			messageBuilder.AppendFormatted(goalPreferencePair.Key);
			messageBuilder.AppendLiteral(": ");
			messageBuilder.AppendFormatted(goalPreferencePair.Value);
		}
		Log.Info(messageBuilder);
		GoalPreference byID = Repository<GoalPreferenceRepository, GoalPreference>.Instance.GetByID(goalPreferencePair.Key);
		if (byID == null)
		{
			FVLogErrorInterpolationHandler messageBuilder2 = new FVLogErrorInterpolationHandler(65, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstanceGoalPreferences.cs");
			if (isEnabled)
			{
				messageBuilder2.AppendFormatted(goalPreferencePair.Key);
				messageBuilder2.AppendLiteral(" is not in GoalPreferenceRepository. Check if the id is correct. ");
			}
			Log.Error(messageBuilder2);
			return;
		}
		GoalPreferenceLevel goalPreferenceLevel = GoalPreferenceLevel.Indifferent;
		if (defaultGoalPrefDictionary.TryGetValue(byID, out var value))
		{
			goalPreferenceLevel = value.PreferenceLevel;
		}
		int modifiedLevel = GetModifiedLevel(goalPreferenceLevel, goalPreferencePair.Value);
		GoalPreferenceLevelData dataByPreferenceLevel = Repository<GoalPreferenceLevelRepository, GoalPreferenceLevelData>.Instance.GetDataByPreferenceLevel(modifiedLevel);
		defaultGoalPrefDictionary[byID] = dataByPreferenceLevel;
		FVLogTraceInterpolationHandler messageBuilder3 = new FVLogTraceInterpolationHandler(45, 4, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstanceGoalPreferences.cs");
		if (isEnabled)
		{
			messageBuilder3.AppendLiteral("New Goal Preference ");
			messageBuilder3.AppendFormatted(byID.GetID());
			messageBuilder3.AppendLiteral(": Old:");
			messageBuilder3.AppendFormatted(goalPreferenceLevel);
			messageBuilder3.AppendLiteral(", Modified: ");
			messageBuilder3.AppendFormatted(modifiedLevel);
			messageBuilder3.AppendLiteral(", new: ");
			messageBuilder3.AppendFormatted(dataByPreferenceLevel.PreferenceLevel);
		}
		Log.Trace(messageBuilder3);
		SetDefaultGoalPreferenceSkillModifiers();
	}

	private int GetModifiedLevel(GoalPreferenceLevel currentLevel, int valueToAdd)
	{
		return Mathf.Clamp((int)(currentLevel + valueToAdd), 1, 5);
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
