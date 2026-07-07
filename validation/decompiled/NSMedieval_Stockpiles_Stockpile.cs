using System;
using System.Collections.Generic;
using NSEipix.Base;
using NSMedieval.Components.Base;
using NSMedieval.Model;
using NSMedieval.State;
using NSMedieval.Types;
using UnityEngine;

namespace NSMedieval.Stockpiles;

[Serializable]
public class Stockpile : NSEipix.Base.Model
{
	[SerializeField]
	private string id;

	[SerializeField]
	private StorageBase storage;

	[SerializeField]
	private List<ResourceGroups> resourceGroups;

	[SerializeField]
	private float layerHideOffset;

	[SerializeField]
	private float layerShadowOffset;

	[SerializeField]
	private BuildingCategoryUI buildingCategoryUI;

	[SerializeField]
	private BuildingSubCategoryUI buildingSubCategoryUI;

	[SerializeField]
	private LocKeys[] locKeys;

	[SerializeField]
	private string iconPath;

	[SerializeField]
	private string iconColorOverlay;

	[SerializeField]
	private ZonePriority zonePriority;

	public StorageBase StorageBase => storage;

	public List<ResourceGroups> ResourceGroups => resourceGroups;

	public float LayerHideOffset => layerHideOffset;

	public float LayerShadowOffset => layerShadowOffset;

	public BuildingCategoryUI BuildingCategoryUI => buildingCategoryUI;

	public BuildingSubCategoryUI BuildingSubCategoryUI => buildingSubCategoryUI;

	public LocKeys[] LocKeys => locKeys;

	public string IconPath => iconPath;

	public ZonePriority ZonePriority => zonePriority;

	public string IconColorOverlay => iconColorOverlay;

	public override string GetID()
	{
		return id;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
