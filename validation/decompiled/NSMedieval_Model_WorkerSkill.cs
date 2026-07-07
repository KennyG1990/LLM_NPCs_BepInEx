using System;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Manager;
using NSMedieval.Repository;
using NSMedieval.Serialization;
using NSMedieval.State.WorkerJobs;
using NSMedieval.StatsSystem;
using UnityEngine;

namespace NSMedieval.Model;

[Serializable]
[FVSerializableKey("WorkerSkill", "")]
public class WorkerSkill : IFVSerializable, IDisposable
{
	private const int MaxLevel = 50;

	[SerializeField]
	private SkillType skillId;

	[SerializeField]
	private int level;

	[SerializeField]
	private float experience;

	[SerializeField]
	private GoalPreferenceLevel goalPreferenceLevel;

	[SerializeField]
	private float experienceAddedToday;

	private string skillNameTextKey = string.Empty;

	public SkillType Id => skillId;

	public int Level => level;

	public float Experience => experience;

	public float ExperienceAddedToday => experienceAddedToday;

	public event Action<SkillType> OnLevelChangedEvent;

	public WorkerSkill(SkillType id, int level, float experience, GoalPreferenceLevel goalPreferenceLevel)
	{
		skillId = id;
		this.level = level;
		this.experience = experience;
		this.goalPreferenceLevel = goalPreferenceLevel;
	}

	public WorkerSkill(SkillType id)
	{
		skillId = id;
	}

	public void Dispose()
	{
		this.OnLevelChangedEvent = null;
	}

	public int GetMaxLevel()
	{
		return 50;
	}

	public int GetGoalPreferenceLevel()
	{
		if (goalPreferenceLevel == GoalPreferenceLevel.None)
		{
			goalPreferenceLevel = GoalPreferenceLevel.Indifferent;
		}
		return (int)goalPreferenceLevel;
	}

	public void SetGoalPreferenceLevel(int level)
	{
		goalPreferenceLevel = (GoalPreferenceLevel)level;
	}

	public void SetExperience(float experience)
	{
		this.experience = experience;
		int currentLevel = Repository<SkillLevelsRepository, SkillLevels>.Instance.GetCurrentLevel(skillId, this.experience);
		if (currentLevel != level)
		{
			level = Mathf.Clamp(currentLevel, 0, 50);
		}
	}

	public void ResetExperienceAddedToday()
	{
		experienceAddedToday = 0f;
	}

	public bool AddExperience(float amount)
	{
		float num = amount * Repository<GoalPreferenceLevelRepository, GoalPreferenceLevelData>.Instance.GetDataByPreferenceLevel(GetGoalPreferenceLevel()).RelatedSkillMultiplier;
		experienceAddedToday += num;
		experience += num;
		int currentLevel = Repository<SkillLevelsRepository, SkillLevels>.Instance.GetCurrentLevel(skillId, experience);
		currentLevel = Mathf.Clamp(currentLevel, 0, 50);
		if (currentLevel != level)
		{
			level = currentLevel;
			this.OnLevelChangedEvent?.Invoke(Id);
			return true;
		}
		return false;
	}

	public void SetLevel(int value)
	{
		int num = value;
		if (num < 0)
		{
			num = 0;
		}
		int num2 = Repository<SkillLevelsRepository, SkillLevels>.Instance.GetXpRequirements(skillId).Length - 1;
		if (num >= num2)
		{
			num = num2;
		}
		experience = Repository<SkillLevelsRepository, SkillLevels>.Instance.GetXpRequirement(skillId, num);
		int currentLevel = Repository<SkillLevelsRepository, SkillLevels>.Instance.GetCurrentLevel(skillId, experience);
		bool num3 = MonoSingleton<ProductionManager>.IsInstantiated() && currentLevel != level;
		level = currentLevel;
		if (num3)
		{
			this.OnLevelChangedEvent?.Invoke(Id);
		}
	}

	public void AddLevels(int levels)
	{
		for (int i = 0; i < levels; i++)
		{
			experience += Repository<SkillLevelsRepository, SkillLevels>.Instance.GetXpRequirement(skillId, level + 1) - Repository<SkillLevelsRepository, SkillLevels>.Instance.GetXpRequirement(skillId, level);
			level = Repository<SkillLevelsRepository, SkillLevels>.Instance.GetCurrentLevel(skillId, experience);
		}
	}

	public string GetSkillTextKey()
	{
		if (skillNameTextKey == null || skillNameTextKey.Equals(string.Empty))
		{
			skillNameTextKey = "skill_name_" + Id;
		}
		return skillNameTextKey;
	}

	public bool IsMaxLevelReached()
	{
		return level >= 50;
	}

	public void Serialize(FVSerializer serializer)
	{
		serializer.WriteEnum("skillId", skillId);
		serializer.Write("level", level);
		serializer.Write("experience", experience);
		serializer.Write("experienceAddedToday", experienceAddedToday);
		serializer.WriteEnum("goalPreferenceLevel", goalPreferenceLevel);
	}

	public WorkerSkill(FVDeserializer deserializer)
	{
		skillId = deserializer.ReadEnum("skillId", SkillType.None);
		level = deserializer.ReadInt("level");
		experience = deserializer.ReadFloat("experience");
		experienceAddedToday = deserializer.ReadFloat("experienceAddedToday");
		goalPreferenceLevel = deserializer.ReadEnum("goalPreferenceLevel", GoalPreferenceLevel.None);
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
