using System;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Repository;
using UnityEngine;

namespace NSMedieval.Model;

public class HumanoidBlueprint : NSEipix.Base.Model
{
	[SerializeField]
	private string id;

	[SerializeField]
	private string humanTypeId;

	[SerializeField]
	private float raidBattleScalesPointsMultiplier = 1f;

	[SerializeField]
	private float buildablePassThroughDestroyChance;

	[SerializeField]
	private string selectionName;

	[NonSerialized]
	private HumanType defaultHumanTypeCached;

	public float RaidBattleScalesPointsMultiplier => raidBattleScalesPointsMultiplier;

	public HumanType DefaultHumanType
	{
		get
		{
			if (defaultHumanTypeCached == null)
			{
				defaultHumanTypeCached = Repository<HumanTypeRepository, HumanType>.Instance.GetByID(humanTypeId);
			}
			return defaultHumanTypeCached;
		}
	}

	public float BuildablePassThroughDestroyChance => buildablePassThroughDestroyChance;

	public string SelectionName => selectionName;

	public override string GetID()
	{
		return id;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
