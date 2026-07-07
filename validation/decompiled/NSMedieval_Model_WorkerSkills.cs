using System;
using System.Collections.Generic;
using System.Linq;
using NSEipix.Base;
using NSMedieval.Manager;
using NSMedieval.Serialization;
using NSMedieval.State.WorkerJobs;
using NSMedieval.StatsSystem;
using UnityEngine;

namespace NSMedieval.Model;

[Serializable]
[FVSerializableKey("WorkerSkills", "")]
public class WorkerSkills : IFVSerializable, IDisposable
{
	[SerializeField]
	private List<WorkerSkill> skills;

	[NonSerialized]
	private bool listenersSet;

	public List<WorkerSkill> Skills
	{
		get
		{
			if (skills == null)
			{
				InitSkills();
			}
			if (listenersSet)
			{
				return skills;
			}
			foreach (WorkerSkill skill in skills)
			{
				skill.OnLevelChangedEvent += OnLevelChangedEvent;
			}
			listenersSet = true;
			return skills;
		}
	}

	public WorkerSkills(IEnumerable<WorkerSkill> newSkills)
	{
		if (newSkills == null)
		{
			return;
		}
		skills = new List<WorkerSkill>();
		foreach (WorkerSkill newSkill in newSkills)
		{
			skills.Add(new WorkerSkill(newSkill.Id, newSkill.Level, 0f, GoalPreferenceLevel.None));
		}
	}

	public WorkerSkills()
	{
	}

	public void Dispose()
	{
		if (skills == null)
		{
			return;
		}
		foreach (WorkerSkill skill in skills)
		{
			skill.Dispose();
		}
		skills.Clear();
	}

	public WorkerSkill GetSkill(SkillType skill)
	{
		int num = (int)skill;
		if (num < 0)
		{
			num = 0;
		}
		if (num < Skills.Count && skills.Count > 0)
		{
			WorkerSkill workerSkill = skills[num];
			if (workerSkill.Id == skill)
			{
				return workerSkill;
			}
		}
		int i = 0;
		for (int count = Skills.Count; i < count; i++)
		{
			WorkerSkill workerSkill2 = skills[i];
			if (workerSkill2.Id == skill)
			{
				return workerSkill2;
			}
		}
		return null;
	}

	internal bool AddExperience(SkillType skill, float amount)
	{
		return GetSkill(skill)?.AddExperience(amount) ?? false;
	}

	private void OnLevelChangedEvent(SkillType id)
	{
		MonoSingleton<ProductionManager>.Instance.UpdateAllProductionStates();
	}

	private void AddNewSkills()
	{
		if (skills == null)
		{
			return;
		}
		SkillType[] skillTypes = EnumValues.SkillTypes;
		foreach (SkillType skill in skillTypes)
		{
			if (!skills.Any((WorkerSkill workerSkill) => workerSkill.Id == skill))
			{
				WorkerSkill item = new WorkerSkill(skill);
				skills.Add(item);
			}
		}
	}

	private void InitSkills()
	{
		if (skills == null)
		{
			skills = new List<WorkerSkill>();
			SkillType[] skillTypes = EnumValues.SkillTypes;
			for (int i = 0; i < skillTypes.Length; i++)
			{
				WorkerSkill workerSkill = new WorkerSkill(skillTypes[i]);
				skills.Add(workerSkill);
				workerSkill.OnLevelChangedEvent += OnLevelChangedEvent;
			}
			listenersSet = true;
		}
	}

	public void Serialize(FVSerializer serializer)
	{
		serializer.Write("skills", skills);
	}

	public WorkerSkills(FVDeserializer deserializer)
	{
		skills = deserializer.ReadObjectList<WorkerSkill>("skills");
		if (skills?.Count != EnumValues.SkillTypes.Length)
		{
			AddNewSkills();
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
