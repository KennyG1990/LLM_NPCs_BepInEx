using System;
using System.Collections.Generic;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Serialization;
using UnityEngine;

namespace NSMedieval.Research;

[Serializable]
[FVSerializableKey("ResearchNodeInstance", "")]
public class ResearchNodeInstance : IFVSerializable
{
	[SerializeField]
	private string id;

	[NonSerialized]
	private ResearchModel blueprint;

	[NonSerialized]
	private List<ResearchNodeInstance> parents = new List<ResearchNodeInstance>();

	[NonSerialized]
	private List<ResearchNodeInstance> children = new List<ResearchNodeInstance>();

	[NonSerialized]
	private Dictionary<Resource, int> requiredResources = new Dictionary<Resource, int>();

	[SerializeField]
	private ResearchState researchState;

	[SerializeField]
	private bool activeByDefault;

	private bool unlockedUsingDebug;

	public ResearchModel Blueprint
	{
		get
		{
			if (blueprint == null)
			{
				blueprint = Repository<ResearchRepository, ResearchModel>.Instance.GetByID(id);
			}
			return blueprint;
		}
	}

	public bool Root => parents.Count == 0;

	public ResearchState ResearchState => researchState;

	public List<ResearchNodeInstance> Parents => parents;

	public List<ResearchNodeInstance> Children => children;

	public Dictionary<Resource, int> RequiredResources => requiredResources;

	public bool ActiveByDefault => activeByDefault;

	public bool UnlockedUsingDebug => unlockedUsingDebug;

	public bool IsActivated => researchState == ResearchState.Activated;

	public ResearchNodeInstance(ResearchModel blueprint)
	{
		this.blueprint = blueprint;
		id = blueprint.GetID();
		foreach (KeyValuePair<string, int> item in this.blueprint.RequiredResources.Dictionary)
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(item.Key);
			if (!(byID == null))
			{
				if (requiredResources.ContainsKey(byID))
				{
					requiredResources[byID] += item.Value;
				}
				else
				{
					requiredResources.Add(byID, item.Value);
				}
			}
		}
	}

	public ResearchNodeInstance()
	{
	}

	public void SetUnlockedUsingDebug(bool unlockedUsingDebug)
	{
		this.unlockedUsingDebug = unlockedUsingDebug;
	}

	public void SetInitialState()
	{
		researchState = ResearchState.Locked;
		ResearchNodeView view = MonoSingleton<ResearchManager>.Instance.GetView(this);
		if (view != null)
		{
			view.Locked();
		}
	}

	public void Unlock()
	{
		researchState = ResearchState.Unlocked;
		ResearchNodeView view = MonoSingleton<ResearchManager>.Instance.GetView(this);
		if (view != null)
		{
			view.Unlocked();
		}
	}

	public void Lock()
	{
		researchState = ResearchState.Locked;
		ResearchNodeView view = MonoSingleton<ResearchManager>.Instance.GetView(this);
		if (view != null)
		{
			view.Locked();
		}
	}

	public void Activate()
	{
		researchState = ResearchState.Activated;
		ResearchNodeView view = MonoSingleton<ResearchManager>.Instance.GetView(this);
		if (view != null)
		{
			view.Activated();
		}
	}

	public void SetActiveByDefault()
	{
		activeByDefault = true;
	}

	public void Deactivate()
	{
	}

	public void Serialize(FVSerializer serializer)
	{
		serializer.Write("id", id);
		serializer.WriteEnum("researchState", researchState);
		serializer.Write("activeByDefault", activeByDefault);
	}

	public ResearchNodeInstance(FVDeserializer deserializer)
	{
		id = deserializer.ReadString("id");
		researchState = deserializer.ReadEnum("researchState", ResearchState.Unlocked);
		activeByDefault = deserializer.ReadBool("activeByDefault");
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
