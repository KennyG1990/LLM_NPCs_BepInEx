using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NSEipix;
using NSEipix.Base;
using NSMedieval.BuildingComponents;
using NSMedieval.Enums;
using NSMedieval.Manager;
using NSMedieval.MovableBuildings;
using NSMedieval.State;
using NSMedieval.Village;
using UnityEngine;

namespace NSMedieval.Construction;

public class ConstructionController : MonoSingleton<ConstructionController>
{
	private readonly List<BaseBuildingInstance> tempPlacedBlueprints = new List<BaseBuildingInstance>();

	public CustomAction<BaseBuildingInstance> AfterConstructionCompletedEvent = new CustomAction<BaseBuildingInstance>();

	public CustomAction<BaseBuildingInstance> DestroyBuildingEvent = new CustomAction<BaseBuildingInstance>();

	public event Action ChangeBuildingTypeToPlaceEvent;

	public event Action<BaseBuildingInstance> BlueprintPlacedEvent;

	public event Action<List<BaseBuildingInstance>> BlueprintPlacedCarveAreasEvent;

	public event Action<BaseBuildingInstance> ConstructionMaterialsDeliveredEvent;

	public event Action<BaseBuildingInstance> ShowFoundationEvent;

	public event Action<BaseBuildingInstance> ConstructionCompletedEvent;

	public event Action<BaseBuildingInstance> BuildingMovePlacedEvent;

	public event Action<BaseBuildingInstance> InstallBuildingPlacedEvent;

	public event Action<ResourcePileInstance> CancelPileInstallationEvent;

	public event Action<BaseBuildingInstance, Vector3, HumanoidInstance> BuildingUninstalledEvent;

	public event Action<BaseBuildingInstance> RefreshLadderFloorEvent;

	public event Action<BaseBuildingInstance> LadderConstructedEvent;

	public event Action<BaseBuildingInstance> LockStateChangedEvent;

	public event Action<Vec3Int> ObjectDestroyedCheckFallDownEvent;

	public event Action<bool> RefreshUsableWellsEvent;

	public event Action<RoofComponentInstance> RoofConstructedEvent;

	public event Action<RoofComponentInstance> RoofDestroyedEvent;

	public event Action<BaseBuildingInstance> BlobConstructionCompletedEvent;

	public event Action<BaseBuildingInstance> OnDoorLockStateChangedEvent;

	public event Action<DoorComponentInstance> DoorLockOrderChangedEvent;

	public event Action<ShelfComponentInstance> ShelfOrderChangedEvent;

	public event Action<WindowComponentInstance> WindowLockOrderChangedEvent;

	public event Action<FuelConsumerComponentInstance> FuelConsumerStateChangedEvent;

	public event Action<FactionOwnership, FactionOwnership, WorldObject> FactionOwnershipChangedEvent;

	public event Action<BaseBuildingInstance> ObjectDestroyedOnPassThroughEvent;

	public void ChangeBuildingTypeToPlace()
	{
		this.ChangeBuildingTypeToPlaceEvent?.Invoke();
	}

	public void BlueprintsPlacedCarveAreas(List<BaseBuildingInstance> buildings)
	{
		this.BlueprintPlacedCarveAreasEvent?.Invoke(buildings);
	}

	public void BlueprintPlaced(BaseBuildingInstance building, RelocateBuilding moveBuilding = RelocateBuilding.None, bool afterLoading = false)
	{
		this.BlueprintPlacedEvent?.Invoke(building);
		if (MonoSingleton<BuildingPlacementManager>.Instance.ConstructWithoutResource)
		{
			building.SetConstructionPhase(ConstructionPhase.Foundation);
		}
		if (moveBuilding == RelocateBuilding.Move)
		{
			this.BuildingMovePlacedEvent?.Invoke(building);
		}
		if (moveBuilding == RelocateBuilding.Install)
		{
			this.InstallBuildingPlacedEvent?.Invoke(building);
		}
		tempPlacedBlueprints.Add(building);
		if (!afterLoading)
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrameUnscaled().Then(delegate
			{
				VillageManager.ActiveVillage.Map.BuildingsManagerMain.RefreshPlacedBlueprints(tempPlacedBlueprints, MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys.ToArray());
				tempPlacedBlueprints.Clear();
			});
		}
	}

	public void RefreshBlueprintsAfterLoading()
	{
		VillageManager.ActiveVillage.Map.BuildingsManagerMain.RefreshPlacedBlueprints(tempPlacedBlueprints, MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys.ToArray());
		tempPlacedBlueprints.Clear();
	}

	public void ShowFoundation(BaseBuildingInstance building)
	{
		this.ShowFoundationEvent?.Invoke(building);
	}

	public void ConstructionMaterialsDelivered(BaseBuildingInstance building)
	{
		this.ConstructionMaterialsDeliveredEvent?.Invoke(building);
	}

	public void ConstructionCompleted(BaseBuildingInstance building)
	{
		this.ConstructionCompletedEvent?.Invoke(building);
		StartCoroutine(WaitForNavmeshUpdate(building));
	}

	public void BuildingDestroyed(BaseBuildingInstance building)
	{
		DestroyBuildingEvent?.Invoke(building);
		if (building.ConstructionPhase != ConstructionPhase.Blueprint)
		{
			MonoSingleton<TaskController>.Instance.OptimizedCall(this, "Refresh blueprints optimized call", delegate
			{
				StartCoroutine(BuildingDestroyedRefreshBlueprintReachability(building));
			});
		}
	}

	public void ObjectDestroyedCheckFallDown(Vec3Int gridPosition)
	{
		this.ObjectDestroyedCheckFallDownEvent?.Invoke(gridPosition);
	}

	public void RefreshUsableWells(bool hasUsableWells)
	{
		this.RefreshUsableWellsEvent?.Invoke(hasUsableWells);
	}

	public void BlobConstructionCompleted(BaseBuildingInstance building)
	{
		this.BlobConstructionCompletedEvent?.Invoke(building);
	}

	public void DoorLockOrderChanged(DoorComponentInstance doorComponentInstance)
	{
		this.DoorLockOrderChangedEvent?.Invoke(doorComponentInstance);
	}

	public void ShelfOrderChanged(ShelfComponentInstance shelfComponentInstance)
	{
		this.ShelfOrderChangedEvent?.Invoke(shelfComponentInstance);
	}

	public void DoorLockStateChanged(BaseBuildingInstance building)
	{
		if (building.BuildingType == BuildingType.Door)
		{
			this.OnDoorLockStateChangedEvent?.Invoke(building);
		}
	}

	public void WindowLockOrderChanged(WindowComponentInstance windowComponentInstance)
	{
		this.WindowLockOrderChangedEvent?.Invoke(windowComponentInstance);
	}

	public void FuelConsumerStateChanged(FuelConsumerComponentInstance fuelConsumerComponentInstance)
	{
		this.FuelConsumerStateChangedEvent?.Invoke(fuelConsumerComponentInstance);
	}

	public void LockStateChanged(BaseBuildingInstance building)
	{
		this.LockStateChangedEvent?.Invoke(building);
	}

	public void BuildingUninstalled(BaseBuildingInstance uninstalledBuilding, Vector3 resourceSpawnPosition, HumanoidInstance humanoidInstance)
	{
		this.BuildingUninstalledEvent?.Invoke(uninstalledBuilding, resourceSpawnPosition, humanoidInstance);
	}

	public void CancelPileInstallation(ResourcePileInstance resourcePileInstance)
	{
		this.CancelPileInstallationEvent?.Invoke(resourcePileInstance);
	}

	public void RefreshLadderFloor(BaseBuildingInstance ladder)
	{
		this.RefreshLadderFloorEvent?.Invoke(ladder);
	}

	public void LadderConstructed(BaseBuildingInstance ladder)
	{
		this.LadderConstructedEvent?.Invoke(ladder);
	}

	public void RoofConstructed(RoofComponentInstance roofComponentInstance)
	{
		this.RoofConstructedEvent?.Invoke(roofComponentInstance);
	}

	public void RoofDestroyed(RoofComponentInstance roofComponentInstance)
	{
		this.RoofDestroyedEvent?.Invoke(roofComponentInstance);
	}

	public void FactionOwnershipChanged(FactionOwnership oldFaction, FactionOwnership newFaction, WorldObject worldObject)
	{
		this.FactionOwnershipChangedEvent?.Invoke(oldFaction, newFaction, worldObject);
	}

	public void PassThroughDestroyed(BaseBuildingInstance building)
	{
		this.ObjectDestroyedOnPassThroughEvent?.Invoke(building);
	}

	private IEnumerator WaitForNavmeshUpdate(BaseBuildingInstance building)
	{
		yield return new WaitForSeconds(0.1f);
		AfterConstructionCompletedEvent?.Invoke(building);
		if (building.Blueprint.ConstructableBaseCategory == ConstructableBaseCategory.Building || building.Blueprint.ConstructableBaseCategory == ConstructableBaseCategory.Roof || building.Blueprint.ConstructableBaseCategory == ConstructableBaseCategory.Stairs)
		{
			VillageManager.ActiveVillage.Map.BuildingsManagerMain.WorldStateChangedRefreshBuildings();
		}
		if (building.Blueprint.ConstructableBaseCategory == ConstructableBaseCategory.Beam)
		{
			MonoSingleton<AchievementManager>.Instance.UnlockAchievement("BUILD_WOOD_BEAM");
		}
	}

	private IEnumerator BuildingDestroyedRefreshBlueprintReachability(BaseBuildingInstance destroyedBuilding)
	{
		yield return new WaitForSeconds(1f);
		VillageManager.ActiveVillage.Map.BuildingsManagerMain.WorldStateChangedRefreshBuildings();
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		tempPlacedBlueprints.Clear();
		this.ChangeBuildingTypeToPlaceEvent = null;
		this.BlueprintPlacedEvent = null;
		this.ConstructionMaterialsDeliveredEvent = null;
		this.ShowFoundationEvent = null;
		this.ConstructionCompletedEvent = null;
		AfterConstructionCompletedEvent?.Clear();
		AfterConstructionCompletedEvent = null;
		this.BuildingMovePlacedEvent = null;
		this.InstallBuildingPlacedEvent = null;
		this.CancelPileInstallationEvent = null;
		this.BuildingUninstalledEvent = null;
		DestroyBuildingEvent?.Clear();
		DestroyBuildingEvent = null;
		this.RefreshLadderFloorEvent = null;
		this.LadderConstructedEvent = null;
		this.LockStateChangedEvent = null;
		this.ObjectDestroyedCheckFallDownEvent = null;
		this.RefreshUsableWellsEvent = null;
		this.RoofConstructedEvent = null;
		this.RoofDestroyedEvent = null;
		this.BlobConstructionCompletedEvent = null;
		this.OnDoorLockStateChangedEvent = null;
		this.DoorLockOrderChangedEvent = null;
		this.ShelfOrderChangedEvent = null;
		this.WindowLockOrderChangedEvent = null;
		this.FuelConsumerStateChangedEvent = null;
		this.BlueprintPlacedCarveAreasEvent = null;
		this.FactionOwnershipChangedEvent = null;
		this.ObjectDestroyedOnPassThroughEvent = null;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
