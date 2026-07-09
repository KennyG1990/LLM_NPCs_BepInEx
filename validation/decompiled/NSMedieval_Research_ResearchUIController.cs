using System;
using System.Collections.Generic;
using NSEipix.Base;
using NSMedieval.Model;
using NSMedieval.Types;

namespace NSMedieval.Research;

public class ResearchUIController : MonoSingleton<ResearchUIController>
{
	public event Action<ResearchNodeInstance> ResearchNodeSelectedEvent;

	public event Action<string> ResearchNodeSelectedExternallyEvent;

	public event Action<Dictionary<Resource, int>> UpdateResourcesEvent;

	public event Action<BuildingCategoryUI> ShowBaseConstructionButtonEvent;

	public event Action<BuildingCategoryUI> HideBaseConstructionButtonEvent;

	public void SelectNodeExternal(string nodeId)
	{
		this.ResearchNodeSelectedExternallyEvent?.Invoke(nodeId);
	}

	public void NodeSelected(ResearchNodeInstance node)
	{
		this.ResearchNodeSelectedEvent?.Invoke(node);
	}

	public void UpdateResources(Dictionary<Resource, int> allocatedResources)
	{
		this.UpdateResourcesEvent?.Invoke(allocatedResources);
	}

	public void ShowBaseConstructionButton(BuildingCategoryUI buildingCategoryUI)
	{
		this.ShowBaseConstructionButtonEvent?.Invoke(buildingCategoryUI);
	}

	public void HideBaseConstructionButton(BuildingCategoryUI buildingCategoryUI)
	{
		this.HideBaseConstructionButtonEvent?.Invoke(buildingCategoryUI);
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		this.ResearchNodeSelectedEvent = null;
		this.UpdateResourcesEvent = null;
		this.ShowBaseConstructionButtonEvent = null;
		this.HideBaseConstructionButtonEvent = null;
		this.ResearchNodeSelectedExternallyEvent = null;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
