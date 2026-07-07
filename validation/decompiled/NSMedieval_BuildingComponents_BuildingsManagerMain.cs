using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Constructables.Managers;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using JetBrains.Annotations;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Construction;
using NSMedieval.Controllers;
using NSMedieval.DebugEvents;
using NSMedieval.Dictionary;
using NSMedieval.Enums;
using NSMedieval.Fire;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.MovableBuildings;
using NSMedieval.Repository;
using NSMedieval.Resources;
using NSMedieval.RoomDetection;
using NSMedieval.State;
using NSMedieval.StatsSystem;
using NSMedieval.Terrain;
using NSMedieval.Tools;
using NSMedieval.Types;
using NSMedieval.UI.Utils;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using NSMedieval.Village.Map.Pathfinding;
using NSMedieval.Water;
using UnityEngine;

namespace NSMedieval.BuildingComponents;

public class BuildingsManagerMain
{
	public readonly ConstructionJobManager ConstructionJobManager;

	private readonly Dictionary<BuildingType, Dictionary<BaseBuildingInstance, BaseBuildingViewComponent>> typeInstanceView = new Dictionary<BuildingType, Dictionary<BaseBuildingInstance, BaseBuildingViewComponent>>();

	private readonly Dictionary<BuildingType, Func<BaseBuildingBlueprint, PooledList<Vec3Int>, bool, bool>> canPlaceDictionary = new Dictionary<BuildingType, Func<BaseBuildingBlueprint, PooledList<Vec3Int>, bool, bool>>();

	private readonly Dictionary<BuildingType, Dictionary<Vec3Int, List<BaseBuildingInstance>>> typePositionListDictionary = new Dictionary<BuildingType, Dictionary<Vec3Int, List<BaseBuildingInstance>>>();

	private readonly Dictionary<BuildingType, Dictionary<Vec3Int, BaseBuildingInstance>> typePositionInstanceDictionary = new Dictionary<BuildingType, Dictionary<Vec3Int, BaseBuildingInstance>>();

	private readonly Dictionary<Vec3Int, List<BaseBuildingInstance>> positionInstanceListDictionary = new Dictionary<Vec3Int, List<BaseBuildingInstance>>();

	private readonly Dictionary<int, BaseBuildingInstance> uniqueIdBuildingDictionary = new Dictionary<int, BaseBuildingInstance>();

	private readonly Dictionary<Vec3Int, BaseBuildingInstance> forbiddenAreaPositionBuildings = new Dictionary<Vec3Int, BaseBuildingInstance>();

	private readonly Dictionary<Vec3Int, Dictionary<BuildingType, BaseBuildingInstance>> positionBuildingTypeInstanceDictionary = new Dictionary<Vec3Int, Dictionary<BuildingType, BaseBuildingInstance>>();

	private readonly Dictionary<HumanoidInstance, Dictionary<BuildingType, List<BaseBuildingInstance>>> ownedBuildings = new Dictionary<HumanoidInstance, Dictionary<BuildingType, List<BaseBuildingInstance>>>();

	private readonly HashSet<BaseBuildingInstance> playerTriggeredEventHolders = new HashSet<BaseBuildingInstance>();

	private readonly HashSet<BaseBuildingInstance> damagedBuildings = new HashSet<BaseBuildingInstance>();

	private readonly HashSet<BaseBuildingInstance> placedBlueprints = new HashSet<BaseBuildingInstance>();

	private readonly HashSet<Resource> refreshForResources = new HashSet<Resource>();

	private readonly Dictionary<Type, Func<BaseBuildingInstance, BaseComponentInstance>> componentsGetterCache = new Dictionary<Type, Func<BaseBuildingInstance, BaseComponentInstance>>();

	private readonly Dictionary<string, List<BaseBuildingInstance>> buildingsById = new Dictionary<string, List<BaseBuildingInstance>>();

	private readonly Dictionary<BuildingType, Queue<Vec3Int>> buildingDestroyedMeshRefreshCache = new Dictionary<BuildingType, Queue<Vec3Int>>();

	private readonly Dictionary<BuildingType, Action<Vec3Int>> meshRefreshCallbacks = new Dictionary<BuildingType, Action<Vec3Int>>();

	private VillageMap map;

	private BaseBuildingInstance buildingToCopy;

	private Vec3Int worldDataSize;

	private VillageSaveData villageSaveData;

	private ThreadingJobSystem.ThreadedTaskData refreshBlueprintsWorldStateChanged;

	private ThreadingJobSystem.ThreadedTaskData refreshBlueprintsResourceChanged;

	private ThreadingJobSystem.ThreadedTaskData refreshBlueprintsBuildingPlaced;

	private bool showBlockedByMessage = true;

	private bool showErrorPlacementMessage = true;

	private bool showMessageDefaultSpaceNotBuildable = true;

	private float timeAccumulator;

	private float stepTime;

	private int frameCounter;

	private int buildingsCounter;

	private List<BaseBuildingInstance> blueprintsToCarveAreas = new List<BaseBuildingInstance>();

	private List<BaseBuildingInstance> blueprintOverlappingBuildingToCancelList = new List<BaseBuildingInstance>();

	private List<BaseBuildingInstance> blueprintOverlappingForbiddenAreaToCancelList = new List<BaseBuildingInstance>();

	public float TotalBuildingWealth { get; private set; }

	public Dictionary<int, BaseBuildingInstance> UniqueIdBuildingDictionary => uniqueIdBuildingDictionary;

	public BaseBuildingInstance LastPlacedBuilding { get; private set; }

	public HashSet<BaseBuildingInstance> PlayerTriggeredEventHolders => playerTriggeredEventHolders;

	public BaseBuildingInstance BuildingToCopy => buildingToCopy;

	public Dictionary<BuildingType, Dictionary<Vec3Int, BaseBuildingInstance>> TypePositionInstanceDictionary => typePositionInstanceDictionary;

	public Dictionary<BuildingType, Dictionary<BaseBuildingInstance, BaseBuildingViewComponent>> TypeInstanceView => typeInstanceView;

	public Dictionary<Vec3Int, List<BaseBuildingInstance>> PositionInstanceListDictionary => positionInstanceListDictionary;

	public event Action<BaseBuildingInstance> LoadSavedBuildingEvent;

	public event Action<BaseBuildingInstance> BuildingDestroyedEvent;

	public event Action<BaseBuildingInstance> BeforeBuildingDestroyedEvent;

	public event Action<BaseBuildingInstance, bool> StabilityCarrierDestroyedEvent;

	public BuildingsManagerMain(VillageMap map)
	{
		this.map = map;
		ConstructionJobManager = new ConstructionJobManager(map);
		VillageManager.ActiveVillage.Map.ObjectRemovedEvent += OnObjectRemoved;
		MonoSingleton<WorkerController>.Instance.RemoveWorkerEvent += OnWorkerRemoved;
		MonoSingleton<GroundController>.Instance.OnGroundDestroyedSingleEvent += OnGroundDestroyedSingle;
		MonoSingleton<GroundController>.Instance.OnGroundDestroyedEvent += OnGroundDestroyed;
		MonoSingleton<GroundController>.Instance.NewVoxelSavedEvent += OnNewVoxelSaved;
		MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent += new Action<BaseBuildingInstance>(OnAfterConstructionCompleted);
		MonoSingleton<World>.Instance.MapLoadedEvent += OnMapLoaded;
		MonoSingleton<RoomDetectionController>.Instance.RoomAddedEvent += OnRoomAdded;
		MonoSingleton<RoomDetectionController>.Instance.RoomTypeChangedEvent += OnRoomTypeChanged;
		MonoSingleton<RoomDetectionController>.Instance.RoomRemovedEvent += OnRoomRemoved;
		ConstructionJobManager.MarkedForDeconstructionEvent += OnMarkedForDeconstruction;
		ConstructionJobManager.UnMarkedForDeconstructionEvent += OnUnMarkedForDeconstruction;
		InitializeCachingDictionaries();
	}

	private void OnRoomAdded(Room room, RoomType previousType)
	{
		RefreshBuildingsOnRoomChanged(room);
	}

	private void OnRoomTypeChanged(Room room, RoomType previousType)
	{
		RefreshBuildingsOnRoomChanged(room);
	}

	private void OnRoomRemoved(Room room)
	{
		RefreshBuildingsOnRoomChanged(room);
	}

	private void RefreshBuildingsOnRoomChanged(Room room)
	{
		foreach (WorldObject item in room.IterateRoomContent())
		{
			if (item is BaseBuildingInstance baseBuildingInstance)
			{
				baseBuildingInstance.RefreshRoomChanged();
			}
		}
	}

	public T GetComponentInstance<T>(BaseBuildingInstance baseBuildingInstance) where T : BaseComponentInstance
	{
		Type typeFromHandle = typeof(T);
		if (componentsGetterCache.TryGetValue(typeFromHandle, out var value))
		{
			return (T)(value?.Invoke(baseBuildingInstance));
		}
		return null;
	}

	public int GetBuildingsCount(string id)
	{
		if (!buildingsById.TryGetValue(id, out var value))
		{
			return 0;
		}
		return value.Count;
	}

	public void SetBuildingToCopy(BaseBuildingInstance buildingToCopy)
	{
		this.buildingToCopy = buildingToCopy;
	}

	public void GetBasicBuildingsNonAlloc(Vec3Int position, IList<BaseBuildingInstance> list)
	{
		if (!positionInstanceListDictionary.TryGetValue(position, out var value))
		{
			return;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (((BuildingType.Wall | BuildingType.Floor | BuildingType.Voxel | BuildingType.Window | BuildingType.Door | BuildingType.Merlon | BuildingType.BarnDoor | BuildingType.Ladder) & item.BuildingType) != 0)
			{
				list.Add(item);
			}
		}
	}

	public BaseBuildingViewComponent GetView(BaseBuildingInstance buildingInstance)
	{
		if (!TypeInstanceView.TryGetValue(buildingInstance.BuildingType, out var value))
		{
			return null;
		}
		if (value.TryGetValue(buildingInstance, out var value2))
		{
			return value2;
		}
		return null;
	}

	public BaseBuildingInstance GetByTypeAndPosition(BuildingType buildingType, Vec3Int gridPosition, Func<BaseBuildingInstance, bool> condition = null)
	{
		if (!TypePositionInstanceDictionary.TryGetValue(buildingType, out var value))
		{
			return null;
		}
		if (!value.TryGetValue(gridPosition, out var value2))
		{
			return null;
		}
		if (condition == null)
		{
			return value2;
		}
		if (!condition(value2))
		{
			return null;
		}
		return value2;
	}

	public void Dispose()
	{
		this.StabilityCarrierDestroyedEvent = null;
		this.BuildingDestroyedEvent = null;
		LastPlacedBuilding = null;
		blueprintOverlappingBuildingToCancelList.Clear();
		blueprintOverlappingBuildingToCancelList = null;
		blueprintOverlappingForbiddenAreaToCancelList.Clear();
		blueprintOverlappingForbiddenAreaToCancelList = null;
		if (ConstructionJobManager != null)
		{
			ConstructionJobManager.MarkedForDeconstructionEvent -= OnMarkedForDeconstruction;
			ConstructionJobManager.UnMarkedForDeconstructionEvent -= OnUnMarkedForDeconstruction;
			ConstructionJobManager.Dispose();
		}
		foreach (Dictionary<BaseBuildingInstance, BaseBuildingViewComponent> value in TypeInstanceView.Values)
		{
			value.Clear();
		}
		TypeInstanceView.Clear();
		foreach (Dictionary<Vec3Int, BaseBuildingInstance> value2 in TypePositionInstanceDictionary.Values)
		{
			value2.Clear();
		}
		TypePositionInstanceDictionary.Clear();
		foreach (List<BaseBuildingInstance> value3 in positionInstanceListDictionary.Values)
		{
			value3.Clear();
		}
		positionInstanceListDictionary.Clear();
		foreach (Dictionary<BuildingType, BaseBuildingInstance> value4 in positionBuildingTypeInstanceDictionary.Values)
		{
			value4.Clear();
		}
		positionBuildingTypeInstanceDictionary.Clear();
		foreach (Dictionary<Vec3Int, List<BaseBuildingInstance>> value5 in typePositionListDictionary.Values)
		{
			foreach (List<BaseBuildingInstance> value6 in value5.Values)
			{
				value6.Clear();
			}
			value5.Clear();
		}
		typePositionListDictionary.Clear();
		foreach (Dictionary<BuildingType, List<BaseBuildingInstance>> value7 in ownedBuildings.Values)
		{
			foreach (List<BaseBuildingInstance> value8 in value7.Values)
			{
				value8.Clear();
			}
			value7.Clear();
		}
		ownedBuildings.Clear();
		canPlaceDictionary.Clear();
		uniqueIdBuildingDictionary.Clear();
		forbiddenAreaPositionBuildings.Clear();
		playerTriggeredEventHolders.Clear();
		damagedBuildings.Clear();
		uniqueIdBuildingDictionary.Clear();
		buildingToCopy = null;
		refreshForResources.Clear();
		placedBlueprints.Clear();
		componentsGetterCache.Clear();
		ClearMeshRefreshCallbacks();
		if (MonoSingleton<SceneController>.IsInstantiated())
		{
			MonoSingleton<SceneController>.Instance.Tick -= OnTick;
			MonoSingleton<SceneController>.Instance.LateTick -= OnLateTick;
		}
		if (MonoSingleton<VillageManager>.IsInstantiated())
		{
			VillageManager.ActiveVillage.Map.ObjectRemovedEvent -= OnObjectRemoved;
		}
		if (MonoSingleton<ConstructionController>.IsInstantiated())
		{
			MonoSingleton<ConstructionController>.Instance.DoorLockOrderChangedEvent -= OnDoorLockStateChanged;
			MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent -= new Action<BaseBuildingInstance>(OnBuildingStateChanged);
			MonoSingleton<ConstructionController>.Instance.ConstructionMaterialsDeliveredEvent -= OnBuildingStateChanged;
			MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent -= new Action<BaseBuildingInstance>(OnBuildingStateChanged);
			MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent -= new Action<BaseBuildingInstance>(OnAfterConstructionCompleted);
		}
		if (MonoSingleton<GroundController>.IsInstantiated())
		{
			MonoSingleton<GroundController>.Instance.OnGroundDestroyedSingleEvent -= OnGroundDestroyedSingle;
			MonoSingleton<GroundController>.Instance.OnGroundDestroyedEvent -= OnGroundDestroyed;
			MonoSingleton<GroundController>.Instance.NewVoxelSavedEvent -= OnNewVoxelSaved;
		}
		if (MonoSingleton<World>.IsInstantiated())
		{
			MonoSingleton<World>.Instance.MapLoadedEvent -= OnMapLoaded;
		}
		if (MonoSingleton<ResourcePileController>.IsInstantiated())
		{
			MonoSingleton<ResourcePileController>.Instance.ResourceCountChangeEvent -= OnAvailableResourcesChanged;
			MonoSingleton<ResourcePileController>.Instance.SpawnPileEvent -= OnPileSpawned;
		}
		if (MonoSingleton<WorkerController>.IsInstantiated())
		{
			MonoSingleton<WorkerController>.Instance.WorkerCountChangedEvent -= OnWorkerCountChanged;
			MonoSingleton<WorkerController>.Instance.RemoveWorkerEvent -= OnWorkerRemoved;
		}
		if (MonoSingleton<RoomDetectionController>.IsInstantiated())
		{
			MonoSingleton<RoomDetectionController>.Instance.RoomAddedEvent -= OnRoomAdded;
			MonoSingleton<RoomDetectionController>.Instance.RoomTypeChangedEvent -= OnRoomTypeChanged;
			MonoSingleton<RoomDetectionController>.Instance.RoomRemovedEvent -= OnRoomRemoved;
		}
		if (MonoSingleton<BuildingPlacementManager>.IsInstantiated())
		{
			MonoSingleton<BuildingPlacementManager>.Instance.SelectionCanceledEvent -= OnCancelPlacement;
		}
		if (map?.WaterManager != null)
		{
			map.WaterManager.WaterLevelChangedEvent -= OnWaterLevelChanged;
		}
	}

	private void OnMarkedForDeconstruction(BaseBuildingInstance buildingInstance)
	{
		if (typeInstanceView.TryGetValue(buildingInstance.BuildingType, out var value) && value.TryGetValue(buildingInstance, out var value2))
		{
			value2?.OnMarkedForDeconstruction();
		}
	}

	private void OnUnMarkedForDeconstruction(BaseBuildingInstance buildingInstance)
	{
		if (typeInstanceView.TryGetValue(buildingInstance.BuildingType, out var value) && value.TryGetValue(buildingInstance, out var value2))
		{
			value2?.OnUnMarkedForDeconstruction();
		}
	}

	private void OnCancelPlacement()
	{
		buildingToCopy = null;
	}

	private void OnWorkerRemoved(HumanoidInstance humanoidInstance)
	{
		if ((!humanoidInstance.HasDisposed && (humanoidInstance.WorkerBehaviour == null || !humanoidInstance.WorkerBehaviour.IsBanished)) || !ownedBuildings.TryGetValue(humanoidInstance, out var value))
		{
			return;
		}
		BuildingType[] array = value.Keys.ToArray();
		foreach (BuildingType key in array)
		{
			List<BaseBuildingInstance> list = value[key];
			for (int num = list.Count - 1; num >= 0; num--)
			{
				if (num < list.Count)
				{
					list[num].BuildingOwnershipInfo.ClearOwner();
				}
			}
		}
	}

	public void CreateBuildingInstanceAndBindToView(BaseBuildingBlueprint baseBuildingBlueprint, BaseBuildingViewComponent baseBuildingViewComponent, Vector3 worldPos, int angleY)
	{
		BaseBuildingInstance baseBuildingInstance = new BaseBuildingInstance(baseBuildingBlueprint, worldPos, angleY);
		baseBuildingViewComponent.SetBaseBuildingInstance(baseBuildingInstance);
	}

	public BaseBuildingInstance CreateAndReturnBuildingInstance(BaseBuildingBlueprint baseBuildingBlueprint, BaseBuildingViewComponent baseBuildingViewComponent, Vector3 worldPos, int angleY, FactionOwnership factionOwnership = FactionOwnership.Player)
	{
		BaseBuildingInstance baseBuildingInstance = new BaseBuildingInstance(baseBuildingBlueprint, worldPos, angleY, factionOwnership);
		baseBuildingViewComponent.SetBaseBuildingInstance(baseBuildingInstance);
		return baseBuildingInstance;
	}

	public void CacheBuildingInstance(BaseBuildingViewComponent baseBuildingViewComponent, bool afterLoading = false)
	{
		BaseBuildingBlueprint blueprint = baseBuildingViewComponent.BaseBuildingInstance.Blueprint;
		BaseBuildingInstance baseBuildingInstance = baseBuildingViewComponent.BaseBuildingInstance;
		BuildingType buildingType = blueprint.BuildingType;
		if (!uniqueIdBuildingDictionary.TryAdd(baseBuildingInstance.UniqueId, baseBuildingInstance))
		{
			return;
		}
		string iD = blueprint.GetID();
		if (!buildingsById.ContainsKey(iD))
		{
			buildingsById.Add(iD, new List<BaseBuildingInstance>());
		}
		buildingsById[iD].Add(baseBuildingInstance);
		TypeInstanceView[buildingType].TryAdd(baseBuildingInstance, baseBuildingViewComponent);
		foreach (Vec3Int position in baseBuildingInstance.Positions)
		{
			typePositionListDictionary[buildingType].TryAdd(position, new List<BaseBuildingInstance>());
			typePositionListDictionary[buildingType][position].AddUnique(baseBuildingInstance);
			positionInstanceListDictionary.TryAdd(position, new List<BaseBuildingInstance>());
			positionInstanceListDictionary[position].AddUnique(baseBuildingInstance);
			positionBuildingTypeInstanceDictionary.TryAdd(position, new Dictionary<BuildingType, BaseBuildingInstance>());
			positionBuildingTypeInstanceDictionary[position].TryAdd(buildingType, baseBuildingInstance);
			TypePositionInstanceDictionary[buildingType].TryAdd(position, baseBuildingInstance);
		}
		List<string> playerTriggeredEvents = blueprint.PlayerTriggeredEvents;
		if (playerTriggeredEvents != null && playerTriggeredEvents.Count > 0)
		{
			playerTriggeredEventHolders.Add(baseBuildingInstance);
		}
		if (baseBuildingInstance.ConstructionPhase == ConstructionPhase.Blueprint)
		{
			MonoSingleton<ConstructionController>.Instance.BlueprintPlaced(baseBuildingInstance, MonoSingleton<BuildingPlacementManager>.Instance.MoveBuilding, afterLoading);
			blueprintsToCarveAreas.Add(baseBuildingInstance);
		}
		AddNewForbiddenArea(baseBuildingInstance);
		if ((baseBuildingInstance.Blueprint.TransfersStability() || baseBuildingInstance.Blueprint.HasVerticalStability()) && baseBuildingInstance.Blueprint.BuildingType != BuildingType.Beam)
		{
			if (!afterLoading)
			{
				map.StabilityManager.AddBlueprintBuilding(baseBuildingInstance);
			}
			else
			{
				map.StabilityManager.AddBuildingAfterLoading(baseBuildingInstance);
			}
		}
		if (baseBuildingInstance.FactionOwnership == FactionOwnership.Enemy)
		{
			map.EnemyBuildingsManager.CacheEnemyBuilding(baseBuildingInstance);
		}
		else
		{
			float wealth = baseBuildingInstance.GetWealth();
			TotalBuildingWealth += wealth;
			bool isEnabled;
			FVLogTraceInterpolationHandler messageBuilder = new FVLogTraceInterpolationHandler(38, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingsManagerMain.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("TotalBuildingWealth += ");
				messageBuilder.AppendFormatted(wealth);
				messageBuilder.AppendLiteral(" -> ");
				messageBuilder.AppendFormatted(TotalBuildingWealth);
				messageBuilder.AppendLiteral(", building ");
				messageBuilder.AppendFormatted(baseBuildingInstance);
			}
			Log.Trace(messageBuilder);
		}
		LastPlacedBuilding = baseBuildingInstance;
		if (!afterLoading)
		{
			MonoSingleton<BuildingPlacementManager>.Instance.SetMoveOrInstallPlacedSuccess(moveOrInstallPlacedSuccess: true);
		}
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "Area Carving", delegate
		{
			if (MonoSingleton<ConstructionController>.IsInstantiated())
			{
				MonoSingleton<ConstructionController>.Instance.BlueprintsPlacedCarveAreas(blueprintsToCarveAreas);
				blueprintsToCarveAreas.Clear();
			}
		});
	}

	public void CacheBuildingCopySettings(BaseBuildingInstance newBuilding)
	{
		if (buildingToCopy != null)
		{
			ShelfComponentInstance componentInstance = buildingToCopy.GetComponentInstance<ShelfComponentInstance>();
			if (componentInstance != null)
			{
				ShelfCopySettingsData copyData = componentInstance.GetCopyData(newBuilding);
				villageSaveData.SaveShelfCopyData(copyData);
			}
			FuelConsumerComponentInstance componentInstance2 = buildingToCopy.GetComponentInstance<FuelConsumerComponentInstance>();
			if (componentInstance2 != null)
			{
				FuelConsumerCopySettingsData copyData2 = componentInstance2.GetCopyData(newBuilding);
				villageSaveData.SaveFuelConsumerCopyData(copyData2);
			}
			SiegeWeaponComponentInstance componentInstance3 = buildingToCopy.GetComponentInstance<SiegeWeaponComponentInstance>();
			if (componentInstance3 != null)
			{
				SiegeWeaponCopySettingsData copyData3 = componentInstance3.GetCopyData(newBuilding);
				villageSaveData.SaveSiegeWeaponCopyData(copyData3);
			}
		}
	}

	private void AddNewForbiddenArea(BaseBuildingInstance baseBuildingInstance)
	{
		if (!baseBuildingInstance.Blueprint.ForbiddenAreaInfo.HasForbiddenArea)
		{
			return;
		}
		foreach (Vec3Int item in baseBuildingInstance.ForbiddenArea)
		{
			Vec3Int gridPosition = item;
			if (forbiddenAreaPositionBuildings.TryGetValue(gridPosition, out var value))
			{
				bool isEnabled;
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(119, 4, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingsManagerMain.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Trying to place ");
					messageBuilder.AppendFormatted(baseBuildingInstance.GetBuildingName());
					messageBuilder.AppendLiteral(" at grid position ");
					messageBuilder.AppendFormatted(gridPosition);
					messageBuilder.AppendLiteral(". Forbidden area exists, ");
					messageBuilder.AppendLiteral("area owner ");
					messageBuilder.AppendFormatted(value.GetBuildingName());
					messageBuilder.AppendLiteral(", owner grid position ");
					messageBuilder.AppendFormatted(value.GridDataPosition);
					messageBuilder.AppendLiteral(". This should never happen!");
				}
				Log.Warning(messageBuilder);
			}
			else
			{
				forbiddenAreaPositionBuildings.Add(gridPosition, baseBuildingInstance);
				baseBuildingInstance.Map.GetNode(in gridPosition)?.AddForbiddenGridData();
			}
		}
	}

	private void RemoveForbiddenArea(BaseBuildingInstance baseBuildingInstance)
	{
		if (!baseBuildingInstance.Blueprint.ForbiddenAreaInfo.HasForbiddenArea)
		{
			return;
		}
		foreach (Vec3Int item in baseBuildingInstance.ForbiddenArea)
		{
			Vec3Int gridPosition = item;
			if (forbiddenAreaPositionBuildings.Remove(gridPosition))
			{
				baseBuildingInstance.Map.GetNode(in gridPosition)?.RemoveForbiddenGridData();
			}
		}
	}

	private void ShowMessageDefaultSpaceNotBuildable()
	{
		if (showMessageDefaultSpaceNotBuildable)
		{
			showMessageDefaultSpaceNotBuildable = false;
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("default_error_space_not_buildable"));
			MonoSingleton<TaskController>.Instance.WaitForUnscaled(4f).Then(delegate
			{
				showMessageDefaultSpaceNotBuildable = true;
			});
		}
	}

	private void ShowMessageBlockedByBuilding(string locKey)
	{
		if (showErrorPlacementMessage)
		{
			showErrorPlacementMessage = false;
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText(locKey));
			MonoSingleton<TaskController>.Instance.WaitForUnscaled(4f).Then(delegate
			{
				showErrorPlacementMessage = true;
			});
		}
	}

	private void ShowMessageBlockedByBuilding(BaseBuildingInstance blockerBuilding, PlacementType placementType)
	{
		if (placementType == PlacementType.SinglePlacement || placementType == PlacementType.WallSocket)
		{
			ShowMessageBlockedByBuilding(blockerBuilding);
		}
		else
		{
			ShowMessageBlockedByBuildingMultiCallCheck(blockerBuilding);
		}
	}

	public void ShowMessageBlockedByBuilding(BaseBuildingInstance blockerBuilding)
	{
		if (blockerBuilding != null)
		{
			string localizedName = BuildingUtils.GetLocalizedName(blockerBuilding.Blueprint.GetID());
			string text = MonoSingleton<LocalizationController>.Instance.GetText("error_socketable_blocking");
			text = text.Replace("{0}", localizedName);
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(text);
		}
	}

	private void ShowMessageBlockedByBuildingMultiCallCheck(BaseBuildingInstance blockerBuilding)
	{
		if (showBlockedByMessage)
		{
			showBlockedByMessage = false;
			ShowMessageBlockedByBuilding(blockerBuilding);
			MonoSingleton<TaskController>.Instance.WaitForUnscaled(2f).Then(delegate
			{
				showBlockedByMessage = true;
			});
		}
	}

	public void ShowMessageBlockedBySlope()
	{
		string text = MonoSingleton<LocalizationController>.Instance.GetText("info_natural_slope");
		string text2 = MonoSingleton<LocalizationController>.Instance.GetText("error_socketable_blocking");
		text2 = text2.Replace("{0}", text);
		MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(text2);
	}

	private void LogPrintInvalidPlacementPosition(string buildingType, Vec3Int pos)
	{
		MapNode node = map.GetNode(in pos);
		if (node != null)
		{
			bool isEnabled;
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(50, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingsManagerMain.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Cannot place ");
				messageBuilder.AppendFormatted(buildingType);
				messageBuilder.AppendLiteral(" at position ");
				messageBuilder.AppendFormatted(pos);
				messageBuilder.AppendLiteral(". Node is occupied by: ");
				messageBuilder.AppendFormatted(node.GetGridFlagsAsStringArray());
				messageBuilder.AppendLiteral(".");
			}
			Log.Info(messageBuilder);
		}
	}

	public bool CanPlaceRoof(RoofViewComponent roofViewComponent, bool showMessage = true)
	{
		if (!GridDataIndexTools.IsForbiddenEdge(roofViewComponent.Positions[0]))
		{
			List<Vec3Int> positions = roofViewComponent.Positions;
			if (!GridDataIndexTools.IsForbiddenEdge(positions[positions.Count - 1]))
			{
				goto IL_0055;
			}
		}
		if (!World.AllowEdgePlacement)
		{
			if (showMessage)
			{
				MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("default_error_space_not_buildable"));
			}
			return false;
		}
		goto IL_0055;
		IL_0055:
		foreach (Vec3Int position in roofViewComponent.Positions)
		{
			Vec3Int a = position;
			if (MonoSingleton<GroundManager>.Instance.GroundExists(a))
			{
				ShowMessageBlockedByBuilding("default_error_space_not_buildable");
				return false;
			}
			Vec3Int b = Vec3Int.down;
			Vec3Int vec3Int = a + b;
			if (MonoSingleton<SlopeManager>.Instance.SlopeExists(vec3Int))
			{
				if (showMessage)
				{
					ShowMessageBlockedBySlope();
				}
				return false;
			}
			BaseBuildingInstance firstBuilding = GetFirstBuilding(BuildingType.Stairs | BuildingType.Ladder, vec3Int);
			if (firstBuilding != null)
			{
				if (showMessage)
				{
					ShowMessageBlockedByBuildingMultiCallCheck(firstBuilding);
				}
				return false;
			}
			BaseBuildingInstance firstBuilding2 = GetFirstBuilding(~(BuildingType.Default | BuildingType.Floor | BuildingType.Rug), a);
			if (firstBuilding2 == null)
			{
				if (MonoSingleton<SlopeManager>.Instance.SlopeExists(a))
				{
					if (showMessage)
					{
						ShowMessageBlockedBySlope();
					}
					return false;
				}
				continue;
			}
			if (showMessage)
			{
				ShowMessageBlockedByBuildingMultiCallCheck(firstBuilding2);
			}
			return false;
		}
		Vec3Int v2 = roofViewComponent.Positions[0];
		List<Vec3Int> positions2 = roofViewComponent.Positions;
		Vec3Int v3 = positions2[positions2.Count - 1];
		Vec3Int v4 = new Vec3Int(v2.x, v2.y - 1, v2.z);
		Vec3Int v5 = new Vec3Int(v3.x, v3.y - 1, v3.z);
		bool flag = MonoSingleton<GroundManager>.Instance.GroundExists(v4);
		bool flag2 = MonoSingleton<GroundManager>.Instance.GroundExists(v5);
		if ((RoofHasBuildingToStandOn(v4) || RoofHasFloorToStandOn(v2) || flag) && (RoofHasBuildingToStandOn(v5) || RoofHasFloorToStandOn(v3) || flag2))
		{
			return true;
		}
		return false;
		bool RoofHasBuildingToStandOn(Vec3Int v)
		{
			BuildingType checkFor = BuildingType.Wall | BuildingType.Voxel | BuildingType.Beam | BuildingType.Window | BuildingType.Door | BuildingType.BarnDoor;
			return BuildingExists(checkFor, v);
		}
		bool RoofHasFloorToStandOn(Vec3Int v)
		{
			return BuildingExists(BuildingType.Floor, v);
		}
	}

	public bool CanPlaceEnemyBuilding(BaseBuildingBlueprint blueprint, Vec3Int gridPosition, int angle)
	{
		if (LoadingController.IsLeavingMainScene || MonoSingleton<LoadingController>.IsApplicationQuitting() || !MonoSingleton<SlopeManager>.IsInstantiated())
		{
			return false;
		}
		if (gridPosition.x < 0 || gridPosition.y < 0 || gridPosition.z < 0 || gridPosition.x >= worldDataSize.x || gridPosition.y >= worldDataSize.y || gridPosition.z >= worldDataSize.z)
		{
			return false;
		}
		using PooledList<Vec3Int> pooledList = Singleton<GridTools>.Instance.GetPositionsJanitor(gridPosition, blueprint.Size, angle);
		foreach (Vec3Int item in pooledList)
		{
			Vec3Int a = item;
			SlopeManager instance = MonoSingleton<SlopeManager>.Instance;
			Vector3 b = Vector3.down;
			if (instance.SlopeExists(a + b))
			{
				return false;
			}
			if (MonoSingleton<SlopeManager>.Instance.SlopeExists(a))
			{
				return false;
			}
			if (MonoSingleton<GroundManager>.Instance.GroundExists(a))
			{
				return false;
			}
			if (blueprint.BuildingType != BuildingType.Ladder && blueprint.BuildingType != BuildingType.Well)
			{
				if (blueprint.PlacementType != PlacementType.WallSocket)
				{
					BuildingType checkFor = BuildingType.Stairs | BuildingType.Ladder;
					Vec3Int b2 = Vec3Int.down;
					if (GetFirstBuilding(checkFor, a + b2) != null)
					{
						return false;
					}
				}
			}
			else
			{
				BuildingType checkFor2 = BuildingType.Stairs;
				Vec3Int b2 = Vec3Int.down;
				if (GetFirstBuilding(checkFor2, a + b2) != null)
				{
					return false;
				}
			}
		}
		using PooledList<Vec3Int> forbiddenArea = Singleton<GridTools>.Instance.GetForbiddenPositions(blueprint, pooledList, gridPosition, angle);
		if (IsBlueprintInAreaForbiddenByBuildings(blueprint, pooledList))
		{
			return false;
		}
		if (IsBlueprintForbiddenAreaOverlappingWithExistingForbiddenArea(blueprint, forbiddenArea))
		{
			return false;
		}
		if (IsBlueprintForbiddenAreaOverlappingWithExistingBuildings(forbiddenArea, blueprint))
		{
			return false;
		}
		bool result = false;
		if (canPlaceDictionary.TryGetValue(blueprint.BuildingType, out var value))
		{
			result = value(blueprint, pooledList, arg3: false);
		}
		return result;
	}

	public bool CanPlace(BaseBuildingBlueprint blueprint, Vec3Int gridPosition, int angle, bool silentLogs = false)
	{
		if (gridPosition.x < 0 || gridPosition.y < 0 || gridPosition.z < 0 || gridPosition.x >= worldDataSize.x || gridPosition.y >= worldDataSize.y || gridPosition.z >= worldDataSize.z)
		{
			ShowMessageBlockedByBuilding("default_error_space_not_buildable");
			return false;
		}
		using PooledList<Vec3Int> pooledList = Singleton<GridTools>.Instance.GetPositionsJanitor(gridPosition, blueprint.Size, angle);
		foreach (Vec3Int item in pooledList)
		{
			Vec3Int a = item;
			if (GridDataIndexTools.IsForbiddenEdge(a.x, a.z))
			{
				ShowMessageBlockedByBuilding("default_error_space_not_buildable");
				return false;
			}
			SlopeManager instance = MonoSingleton<SlopeManager>.Instance;
			Vector3 b = Vector3.down;
			if (instance.SlopeExists(a + b) && blueprint.PlacementType != PlacementType.WallSocket)
			{
				MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("default_error_space_taken"));
				if (!silentLogs)
				{
					LogPrintInvalidPlacementPosition(blueprint.GetID(), gridPosition);
				}
				return false;
			}
			if (MonoSingleton<SlopeManager>.Instance.SlopeExists(a))
			{
				MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("default_error_space_taken"));
				return false;
			}
			if (MonoSingleton<GroundManager>.Instance.GroundExists(a))
			{
				return false;
			}
			Vec3Int b2;
			if (blueprint.BuildingType != BuildingType.Ladder && blueprint.BuildingType != BuildingType.Well)
			{
				if (blueprint.PlacementType == PlacementType.WallSocket)
				{
					continue;
				}
				BuildingType checkFor = BuildingType.Stairs | BuildingType.Ladder;
				b2 = Vec3Int.down;
				BaseBuildingInstance firstBuilding = GetFirstBuilding(checkFor, a + b2);
				if (firstBuilding != null)
				{
					ShowMessageBlockedByBuilding(firstBuilding, blueprint.PlacementType);
					if (!silentLogs)
					{
						LogPrintInvalidPlacementPosition(blueprint.GetID(), gridPosition);
					}
					return false;
				}
				continue;
			}
			BuildingType checkFor2 = BuildingType.Stairs;
			b2 = Vec3Int.down;
			BaseBuildingInstance firstBuilding2 = GetFirstBuilding(checkFor2, a + b2);
			if (firstBuilding2 != null)
			{
				ShowMessageBlockedByBuilding(firstBuilding2, blueprint.PlacementType);
				if (!silentLogs)
				{
					LogPrintInvalidPlacementPosition(blueprint.GetID(), gridPosition);
				}
				return false;
			}
		}
		blueprintOverlappingBuildingToCancelList.Clear();
		blueprintOverlappingForbiddenAreaToCancelList.Clear();
		bool flag = false;
		if (canPlaceDictionary.TryGetValue(blueprint.BuildingType, out var value))
		{
			flag = value(blueprint, pooledList, arg3: true);
		}
		if (flag)
		{
			using PooledList<Vec3Int> forbiddenArea = Singleton<GridTools>.Instance.GetForbiddenPositions(blueprint, pooledList, gridPosition, angle);
			bool flag2 = IsBlueprintInAreaForbiddenByBuildings(blueprint, pooledList);
			bool flag3 = IsBlueprintForbiddenAreaOverlappingWithExistingForbiddenArea(blueprint, forbiddenArea);
			bool flag4 = IsBlueprintForbiddenAreaOverlappingWithExistingBuildings(forbiddenArea, blueprint);
			if (!flag2 && !flag3 && !flag4)
			{
				BaseBuildingInstance[] array = blueprintOverlappingForbiddenAreaToCancelList.ToArray();
				foreach (BaseBuildingInstance baseBuildingInstance in array)
				{
					DestroyBuilding(baseBuildingInstance);
				}
				array = blueprintOverlappingBuildingToCancelList.ToArray();
				foreach (BaseBuildingInstance baseBuildingInstance2 in array)
				{
					DestroyBuilding(baseBuildingInstance2);
				}
				blueprintOverlappingForbiddenAreaToCancelList.Clear();
				blueprintOverlappingBuildingToCancelList.Clear();
				return true;
			}
			if (flag2)
			{
				MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("Can't place in area forbidden by existing buildings"));
				return false;
			}
			if (flag3)
			{
				MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("Forbidden areas are overlapping!"));
				return false;
			}
			if (flag4)
			{
				MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("Forbidden areas are overlapping!"));
				return false;
			}
		}
		return flag;
	}

	private bool IsBlueprintInAreaForbiddenByBuildings(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> positions)
	{
		using PooledList<BaseBuildingInstance> pooledList = ListPool<BaseBuildingInstance>.GetJanitor();
		foreach (Vec3Int item in positions)
		{
			BaseBuildingInstance forbiddenAreaOwner = GetForbiddenAreaOwner(item);
			if (forbiddenAreaOwner != null && !pooledList.Contains(forbiddenAreaOwner))
			{
				pooledList.Add(forbiddenAreaOwner);
			}
		}
		if (pooledList.All((BaseBuildingInstance x) => x.ConstructionPhase == ConstructionPhase.Blueprint && x.BuildingType == blueprint.BuildingType))
		{
			blueprintOverlappingForbiddenAreaToCancelList.AddRange(pooledList);
			return false;
		}
		foreach (Vec3Int item2 in positions)
		{
			if (IsBlueprintInAreaForbiddenByBuildings(blueprint, item2))
			{
				return true;
			}
		}
		return false;
	}

	public bool IsBlueprintInAreaForbiddenByBuildings(BaseBuildingBlueprint blueprint, Vec3Int v)
	{
		if (!forbiddenAreaPositionBuildings.TryGetValue(v, out var value))
		{
			return false;
		}
		if (blueprint.BuildingType == BuildingType.Rug)
		{
			return false;
		}
		if (blueprint.PlacementType == PlacementType.WallSocket)
		{
			DoorComponentBlueprint byID = Repository<DoorComponentRepository, DoorComponentBlueprint>.Instance.GetByID(value.Blueprint.DoorComponentID);
			if (byID == null)
			{
				return false;
			}
			if (byID.DoorType == DoorType.Regular)
			{
				return false;
			}
		}
		if (MonoSingleton<MoveBuildingsManager>.Instance.BuildingToMove == value)
		{
			return false;
		}
		if (!blueprint.PlaceableBellowOthers)
		{
			return true;
		}
		return v.y > value.GridDataPosition.y;
	}

	public BaseBuildingInstance GetForbiddenAreaOwner(Vec3Int gridPos)
	{
		return forbiddenAreaPositionBuildings.GetValueOrDefault(gridPos);
	}

	private bool IsBlueprintForbiddenAreaOverlappingWithExistingBuildings(PooledList<Vec3Int> forbiddenArea, BaseBuildingBlueprint blueprint)
	{
		using PooledList<BaseBuildingInstance> pooledList = ListPool<BaseBuildingInstance>.GetJanitor();
		if (blueprint.BuildingType == BuildingType.FenceGate)
		{
			foreach (Vec3Int item in forbiddenArea)
			{
				BaseBuildingInstance firstBuilding = GetFirstBuilding(item, (BaseBuildingInstance x) => x.BuildingType != BuildingType.Floor && x.BuildingType != BuildingType.Rug);
				if (firstBuilding != null && !pooledList.Contains(firstBuilding))
				{
					pooledList.Add(firstBuilding);
				}
			}
		}
		else
		{
			foreach (Vec3Int item2 in forbiddenArea)
			{
				BaseBuildingInstance firstBuilding2 = GetFirstBuilding(item2, (BaseBuildingInstance x) => x.BuildingType != BuildingType.Floor && x.BuildingType != BuildingType.Rug && x.Blueprint.PlacementType != PlacementType.WallSocket);
				if (firstBuilding2 != null && !pooledList.Contains(firstBuilding2))
				{
					pooledList.Add(firstBuilding2);
				}
			}
		}
		if (pooledList.All((BaseBuildingInstance x) => x.ConstructionPhase == ConstructionPhase.Blueprint && x.BuildingType == blueprint.BuildingType))
		{
			blueprintOverlappingForbiddenAreaToCancelList.AddRange(pooledList);
			return false;
		}
		BaseBuildingInstance buildingToMove = MonoSingleton<MoveBuildingsManager>.Instance.BuildingToMove;
		foreach (Vec3Int item3 in forbiddenArea)
		{
			using PooledList<BaseBuildingInstance> pooledList2 = GetBuildings(item3, (BaseBuildingInstance x) => x.BuildingType != BuildingType.Floor && x.BuildingType != BuildingType.Rug);
			if (pooledList2.Count == 0)
			{
				continue;
			}
			if (pooledList2.Count == 1)
			{
				BaseBuildingInstance baseBuildingInstance = pooledList2[0];
				if (baseBuildingInstance.BuildingType != BuildingType.Beam && baseBuildingInstance != buildingToMove)
				{
					MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(string.Format("build_area_overlap_warning".ToLocalized(), BuildingUtils.GetLocalizedName(baseBuildingInstance.BlueprintId)));
					return true;
				}
				if (blueprint.Size.y < 2 || baseBuildingInstance.GridDataPosition.y >= forbiddenArea.Max((Vec3Int pos) => pos.y))
				{
					continue;
				}
				MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(string.Format("build_area_overlap_warning".ToLocalized(), BuildingUtils.GetLocalizedName(baseBuildingInstance.BlueprintId)));
				return true;
			}
			foreach (BaseBuildingInstance item4 in pooledList2)
			{
				if (item4.BuildingType != BuildingType.Beam && item4 != buildingToMove)
				{
					MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(string.Format("build_area_overlap_warning".ToLocalized(), BuildingUtils.GetLocalizedName(item4.BlueprintId)));
					return true;
				}
			}
		}
		return false;
	}

	private bool IsBlueprintForbiddenAreaOverlappingWithExistingForbiddenArea(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> forbiddenArea)
	{
		return IsBlueprintInAreaForbiddenByBuildings(blueprint, forbiddenArea);
	}

	private bool IsInForbiddenArea(BaseBuildingBlueprint blueprint, Vec3Int gridPos)
	{
		BuildingType buildingType = blueprint.BuildingType;
		if (buildingType == BuildingType.Floor || buildingType == BuildingType.Rug)
		{
			return false;
		}
		MapNode node = map.GetNode(in gridPos);
		if (node == null)
		{
			return false;
		}
		return node.DataType.HasFlag(GridDataType.ForbiddenByBuilding);
	}

	public BaseBuildingInstance FindWouldReplaceBuilding(BaseBuildingInstance newBuilding)
	{
		if (newBuilding.ConstructionPhase == ConstructionPhase.Foundation)
		{
			return null;
		}
		if (!positionInstanceListDictionary.TryGetValue(newBuilding.GridDataPosition, out var value))
		{
			return null;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (!newBuilding.Blueprint.ReplacementFlag.HasFlag(item.BuildingType))
			{
				continue;
			}
			bool flag = false;
			if (newBuilding.ConstructionPhase == ConstructionPhase.Blueprint)
			{
				if (item.ConstructionPhase == ConstructionPhase.Blueprint)
				{
					flag = true;
				}
			}
			else if (newBuilding.ConstructionPhase == ConstructionPhase.Finished)
			{
				flag = true;
			}
			if (flag)
			{
				return item;
			}
		}
		return null;
	}

	public void TryReplaceBuilding(BaseBuildingInstance newBuilding)
	{
		BaseBuildingInstance baseBuildingInstance = FindWouldReplaceBuilding(newBuilding);
		if (baseBuildingInstance != null)
		{
			if (baseBuildingInstance.DeliveryJobPriority.HasValue)
			{
				newBuilding.DeliveryJobPriority = baseBuildingInstance.DeliveryJobPriority.Value;
			}
			DestroyBuilding(baseBuildingInstance, replaced: true);
			map.StabilityManager.BuildingReplaced(newBuilding);
		}
	}

	private bool Exists(BuildingType buildingType, Vec3Int gridPosition)
	{
		if (!typePositionListDictionary.TryGetValue(buildingType, out var value))
		{
			return false;
		}
		if (!value.ContainsKey(gridPosition))
		{
			return false;
		}
		foreach (BaseBuildingInstance item in typePositionListDictionary[buildingType][gridPosition])
		{
			if (item.Blueprint.BuildingType == buildingType)
			{
				return true;
			}
		}
		return false;
	}

	public List<BaseBuildingInstance> GetBuildings(Vec3Int gridPosition)
	{
		positionInstanceListDictionary.TryGetValue(gridPosition, out var value);
		return value;
	}

	[MustDisposeResource]
	public PooledList<BaseBuildingInstance> GetBuildings(Vec3Int gridPosition, Func<BaseBuildingInstance, bool> condition)
	{
		PooledList<BaseBuildingInstance> janitor = ListPool<BaseBuildingInstance>.GetJanitor();
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return janitor;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (condition(item))
			{
				janitor.Add(item);
			}
		}
		return janitor;
	}

	public void GetAllBuildings(Queue<BaseBuildingInstance> output)
	{
		output.Clear();
		foreach (Dictionary<BaseBuildingInstance, BaseBuildingViewComponent> value in typeInstanceView.Values)
		{
			foreach (BaseBuildingInstance key in value.Keys)
			{
				output.Enqueue(key);
			}
		}
	}

	public void GetAllBuildings(List<BaseBuildingInstance> output)
	{
		output.Clear();
		foreach (Dictionary<BaseBuildingInstance, BaseBuildingViewComponent> value in typeInstanceView.Values)
		{
			foreach (BaseBuildingInstance key in value.Keys)
			{
				output.Add(key);
			}
		}
	}

	public void GetBuildings(BuildingType buildingType, List<BaseBuildingInstance> output)
	{
		if (TypeInstanceView.TryGetValue(buildingType, out var value))
		{
			output.AddRange(value.Keys);
		}
	}

	[MustDisposeResource]
	public PooledList<BaseBuildingInstance> GetBuildings(BuildingType buildingType)
	{
		if (!TypeInstanceView.TryGetValue(buildingType, out var value))
		{
			return default(PooledList<BaseBuildingInstance>);
		}
		PooledList<BaseBuildingInstance> janitor = ListPool<BaseBuildingInstance>.GetJanitor(value.Count);
		janitor.AddRange(value.Keys);
		return janitor;
	}

	[MustDisposeResource]
	public PooledList<BaseBuildingViewComponent> GetBuildingViews(BuildingType buildingType)
	{
		if (!TypeInstanceView.TryGetValue(buildingType, out var value))
		{
			return default(PooledList<BaseBuildingViewComponent>);
		}
		PooledList<BaseBuildingViewComponent> janitor = ListPool<BaseBuildingViewComponent>.GetJanitor(value.Count);
		janitor.AddRange(value.Values);
		return janitor;
	}

	public IEnumerable<BaseBuildingViewComponent> IterateBuildingViews()
	{
		foreach (var (_, dictionary2) in TypeInstanceView)
		{
			foreach (BaseBuildingViewComponent value in dictionary2.Values)
			{
				yield return value;
			}
		}
	}

	public BaseBuildingInstance GetBuilding(Vec3Int gridPos, Func<BaseBuildingInstance, bool> condition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPos, out var value))
		{
			return null;
		}
		if (condition == null)
		{
			if (value.Count == 0)
			{
				return null;
			}
			return value[0];
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (condition(item))
			{
				return item;
			}
		}
		return null;
	}

	public BaseBuildingInstance TryGetBasicBuilding(Vec3Int gridPosition, Func<BaseBuildingInstance, bool> condition)
	{
		BuildingType buildingType = BuildingType.Wall | BuildingType.Floor | BuildingType.Window | BuildingType.Door | BuildingType.Merlon | BuildingType.BarnDoor;
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return null;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (buildingType.HasFlag(item.BuildingType) && (condition == null || condition(item)))
			{
				return item;
			}
		}
		return null;
	}

	public bool CollidesWithProjectile(Vector3 point, BaseBuildingInstance sourceBuilding)
	{
		Vec3Int key = point.ToGridVec3Int();
		if (!positionInstanceListDictionary.TryGetValue(key, out var value))
		{
			return false;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (item == null || item.HasDisposed || item.ConstructionPhase == ConstructionPhase.Blueprint || item == sourceBuilding)
			{
				continue;
			}
			Vector3 worldPosition = item.WorldPosition;
			BuildingType buildingType = item.BuildingType;
			if (buildingType == BuildingType.Floor || buildingType == BuildingType.Trap || buildingType == BuildingType.Rug)
			{
				if (worldPosition.y + 0.1f >= point.y)
				{
					return true;
				}
			}
			else if (point.y > worldPosition.y && point.y < worldPosition.y + item.Blueprint.BoxColliderSettings.SizeOffset.y + 0.2f)
			{
				return true;
			}
		}
		return false;
	}

	public bool BuildingExists(BuildingType checkFor, Vec3Int gridPosition, Func<BaseBuildingInstance, bool> condition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return false;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (condition(item) && checkFor.HasFlag(item.BuildingType))
			{
				return true;
			}
		}
		return false;
	}

	public bool BuildingExists(Vec3Int gridPosition, Func<BaseBuildingInstance, bool> condition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return false;
		}
		if (value.Count == 0)
		{
			return false;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (!condition(item))
			{
				return false;
			}
		}
		return true;
	}

	public bool SocketableBlockerExists(Vec3Int gridPosition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return false;
		}
		if (value.Count == 0)
		{
			return false;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (item.BlocksSocketablePlacement())
			{
				return true;
			}
		}
		return false;
	}

	public bool BuildingExists(Vec3Int gridPos)
	{
		if (positionInstanceListDictionary.TryGetValue(gridPos, out var value))
		{
			return value.Count > 0;
		}
		return false;
	}

	public bool CanPlaceCropBuildingCheck(Vec3Int gridPos)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPos, out var value))
		{
			return true;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (item.Blueprint.PlacementType != PlacementType.WallSocket)
			{
				return false;
			}
		}
		return true;
	}

	public bool BuildingExists(BuildingType checkFor, Vec3Int gridPosition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return false;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (checkFor.HasFlag(item.BuildingType))
			{
				return true;
			}
		}
		return false;
	}

	public bool HasAtLeastOne(Predicate<BaseBuildingInstance> predicate)
	{
		foreach (List<BaseBuildingInstance> value in buildingsById.Values)
		{
			foreach (BaseBuildingInstance item in value)
			{
				if (predicate(item))
				{
					return true;
				}
			}
		}
		return false;
	}

	public BaseBuildingInstance GetFirstBuilding(BuildingType checkFor, Vec3Int gridPosition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return null;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (checkFor.HasFlag(item.BuildingType))
			{
				return item;
			}
		}
		return null;
	}

	public BaseBuildingInstance GetFirstBuilding(BuildingType checkFor, Vec3Int gridPosition, Func<BaseBuildingInstance, bool> condition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return null;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (condition(item) && checkFor.HasFlag(item.BuildingType))
			{
				return item;
			}
		}
		return null;
	}

	public BaseBuildingInstance GetFirstBuilding(Vec3Int gridPosition, Func<BaseBuildingInstance, bool> condition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return null;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (condition(item))
			{
				return item;
			}
		}
		return null;
	}

	private bool CanPlaceWall(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		BuildingType checkFor = BuildingType.AllBuildings & ~blueprint.ReplacementFlag & ~BuildingType.Beam;
		foreach (Vec3Int item in gridPositions)
		{
			if (BuildingExists(checkFor, item))
			{
				return false;
			}
		}
		return true;
	}

	private bool CanPlaceMerlon(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		BuildingType checkFor = BuildingType.AllBuildings & ~blueprint.ReplacementFlag & ~BuildingType.Beam;
		foreach (Vec3Int item in gridPositions)
		{
			if (BuildingExists(checkFor, item, (BaseBuildingInstance x) => x.Blueprint.PlacementType != PlacementType.WallSocket))
			{
				return false;
			}
		}
		return true;
	}

	private bool CanPlaceDoor(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		BuildingType checkFor = BuildingType.AllBuildings & ~blueprint.ReplacementFlag;
		foreach (Vec3Int item in gridPositions)
		{
			if (BuildingExists(checkFor, item))
			{
				return false;
			}
		}
		return true;
	}

	private bool CanPlaceWindow(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		BuildingType checkFor = BuildingType.AllBuildings & ~blueprint.ReplacementFlag;
		foreach (Vec3Int item in gridPositions)
		{
			if (BuildingExists(checkFor, item))
			{
				return false;
			}
		}
		return true;
	}

	private bool CanPlaceFloor(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		BuildingType checkFor = BuildingType.Wall | BuildingType.Floor | BuildingType.Voxel | BuildingType.Window | BuildingType.Door | BuildingType.Merlon | BuildingType.BarnDoor | BuildingType.Ladder;
		foreach (Vec3Int item in gridPositions)
		{
			if (map.GraveComponentManager.DiggableGraveExists(item))
			{
				return false;
			}
			if (BuildingExists(checkFor, item))
			{
				return false;
			}
			BaseBuildingInstance building = GetBuilding(item, (BaseBuildingInstance x) => x.BuildingType == BuildingType.FenceGate);
			if (building != null && !building.HasDisposed && building.Blueprint.Size.y > 1 && building.Positions.Min((Vec3Int pos) => pos.y) < item.y)
			{
				return false;
			}
		}
		return true;
	}

	private bool CanPlaceGrave(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		GraveComponentBlueprint graveComponentBlueprint = Repository<GraveComponentRepository, GraveComponentBlueprint>.Instance.GetByID(blueprint.GraveComponentID);
		if (graveComponentBlueprint == null)
		{
			return false;
		}
		if (!graveComponentBlueprint.Diggable)
		{
			return CanPlaceSarcophagus();
		}
		return CanPlaceDiggableGrave();
		bool CanPlaceDiggableGrave()
		{
			foreach (Vec3Int item in gridPositions)
			{
				Vec3Int a = item;
				GraveComponentManager graveComponentManager = map.GraveComponentManager;
				GraveComponentBlueprint grave = graveComponentBlueprint;
				Vec3Int b = Vec3Int.down;
				if (!graveComponentManager.ValidGround(grave, a + b, showMessage: true))
				{
					return false;
				}
			}
			using PooledList<BaseBuildingInstance> overlappingBuildings = ListPool<BaseBuildingInstance>.GetJanitor();
			foreach (Vec3Int item2 in gridPositions)
			{
				BaseBuildingInstance firstBuilding = GetFirstBuilding(item2, (BaseBuildingInstance x) => x.Blueprint.PlacementType != PlacementType.WallSocket);
				if (firstBuilding != null)
				{
					overlappingBuildings.Add(firstBuilding);
				}
			}
			return CanPlaceAndCancel(overlappingBuildings, blueprint, gridPositions.Count > 1);
		}
		bool CanPlaceSarcophagus()
		{
			BuildingType checkFor = ~(BuildingType.Default | BuildingType.Floor | BuildingType.Beam | BuildingType.Rug);
			using PooledList<BaseBuildingInstance> overlappingBuildings2 = ListPool<BaseBuildingInstance>.GetJanitor();
			foreach (Vec3Int item3 in gridPositions)
			{
				BaseBuildingInstance firstBuilding2 = GetFirstBuilding(checkFor, item3);
				if (firstBuilding2 != null)
				{
					overlappingBuildings2.Add(firstBuilding2);
				}
			}
			return CanPlaceAndCancel(overlappingBuildings2, blueprint, gridPositions.Count > 1);
		}
	}

	private bool CanPlaceStairs(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		BuildingType checkFor = ~(BuildingType.Default | BuildingType.Floor);
		foreach (Vec3Int item in gridPositions)
		{
			Vec3Int a = item;
			Vec3Int b = Vec3Int.up;
			Vec3Int vec3Int = a + b;
			BaseBuildingInstance building = GetBuilding(vec3Int, (BaseBuildingInstance x) => !x.HasDisposed && x.Blueprint.PlacementType != PlacementType.WallSocket);
			if (building != null)
			{
				ShowMessageBlockedByBuildingMultiCallCheck(building);
				return false;
			}
			if (MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int))
			{
				ShowMessageBlockedByBuilding("default_error_space_not_buildable");
				return false;
			}
			b = Vec3Int.down;
			Vec3Int vec3Int2 = a + b;
			if (!BuildingExists(BuildingType.Floor, a) && GetWallTypeBuildingWithVerticalStability(vec3Int2) == null && !MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int2))
			{
				return false;
			}
		}
		using PooledList<BaseBuildingInstance> overlappingBuildings = ListPool<BaseBuildingInstance>.GetJanitor();
		foreach (Vec3Int item2 in gridPositions)
		{
			BaseBuildingInstance firstBuilding = GetFirstBuilding(checkFor, item2);
			if (firstBuilding != null && !overlappingBuildings.Contains(firstBuilding))
			{
				overlappingBuildings.Add(firstBuilding);
			}
		}
		return CanPlaceAndCancel(overlappingBuildings, blueprint, gridPositions.Count > 1);
	}

	private bool CanPlaceFenceGate(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		BuildingType checkFor = ~(BuildingType.Default | BuildingType.Floor | BuildingType.Beam | BuildingType.Fence | BuildingType.Rug);
		if (blueprint.Size.y == 1)
		{
			foreach (Vec3Int item in gridPositions)
			{
				BaseBuildingInstance firstBuilding = GetFirstBuilding(checkFor, item, (BaseBuildingInstance x) => x.Blueprint.BuildingType != BuildingType.Beam);
				if (firstBuilding != null)
				{
					ShowMessageBlockedByBuildingMultiCallCheck(firstBuilding);
					return false;
				}
			}
		}
		else
		{
			int num = gridPositions.Max((Vec3Int pos) => pos.y);
			BuildingType checkFor2 = BuildingType.AllBuildings;
			foreach (Vec3Int item2 in gridPositions)
			{
				BaseBuildingInstance baseBuildingInstance = ((item2.y != num) ? GetFirstBuilding(checkFor2, item2, (BaseBuildingInstance x) => x.Blueprint.BuildingType != BuildingType.Floor) : GetFirstBuilding(checkFor2, item2, (BaseBuildingInstance x) => x.Blueprint.BuildingType != BuildingType.Beam));
				if (baseBuildingInstance != null)
				{
					ShowMessageBlockedByBuildingMultiCallCheck(baseBuildingInstance);
					return false;
				}
			}
		}
		return true;
	}

	private bool CanPlaceRug(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		BuildingType checkFor = BuildingType.Wall | BuildingType.Voxel | BuildingType.Window | BuildingType.Door | BuildingType.Merlon | BuildingType.BarnDoor | BuildingType.Rug | BuildingType.Ladder;
		foreach (Vec3Int item in gridPositions)
		{
			Vec3Int a = item;
			BaseBuildingInstance firstBuilding = GetFirstBuilding(checkFor, a);
			if (firstBuilding != null)
			{
				ShowMessageBlockedByBuilding(firstBuilding);
				return false;
			}
			if (!BuildingExists(BuildingType.Floor, a))
			{
				GraveComponentInstance graveComponentInstance = GetBuilding(a, (BaseBuildingInstance x) => x.BuildingType == BuildingType.Grave)?.GetComponentInstance<GraveComponentInstance>();
				if (graveComponentInstance != null && graveComponentInstance.Blueprint.Diggable)
				{
					ShowMessageBlockedByBuilding(graveComponentInstance.OwnerBuilding);
					return false;
				}
				Vec3Int b = Vec3Int.down;
				Vec3Int vec3Int = a + b;
				if (GetWallTypeBuildingWithVerticalStability(vec3Int) == null && !MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int))
				{
					MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("default_error_space_not_buildable"));
					return false;
				}
			}
		}
		return true;
	}

	private bool CanPlaceLadder(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		foreach (Vec3Int item in gridPositions)
		{
			Vec3Int a = item;
			Vector3 b = Vector3.up;
			Vec3Int lhs = a + b;
			BaseBuildingInstance building = GetBuilding(lhs, (BaseBuildingInstance x) => x.BuildingType != BuildingType.Ladder && x.BuildingType != BuildingType.Beam && x.Blueprint.PlacementType != PlacementType.WallSocket);
			if (building != null)
			{
				bool flag = true;
				WellComponentInstance componentInstance = building.GetComponentInstance<WellComponentInstance>();
				if (componentInstance != null)
				{
					Vec3Int rhs = componentInstance.Center;
					if (lhs == rhs)
					{
						flag = false;
					}
				}
				if (flag)
				{
					if (showBbt)
					{
						ShowMessageBlockedByBuilding(building, blueprint.PlacementType);
					}
					return false;
				}
			}
			if (MonoSingleton<GroundManager>.Instance.GroundExists(lhs))
			{
				if (showBbt)
				{
					ShowMessageBlockedByBuilding("default_error_space_not_buildable");
				}
				return false;
			}
			BaseBuildingInstance building2 = GetBuilding(a, (BaseBuildingInstance x) => x.BuildingType != BuildingType.Floor);
			if (building2 != null)
			{
				if (showBbt)
				{
					ShowMessageBlockedByBuilding(building2, blueprint.PlacementType);
				}
				return false;
			}
		}
		return true;
	}

	private bool CanPlaceInteriorCommon(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		if (blueprint.PlacementType == PlacementType.WallSocket)
		{
			BuildingType checkFor = BuildingType.Roof | BuildingType.Stairs;
			foreach (Vec3Int item in gridPositions)
			{
				BaseBuildingInstance firstBuilding = GetFirstBuilding(checkFor, item);
				if (firstBuilding != null)
				{
					ShowMessageBlockedByBuilding(firstBuilding, blueprint.PlacementType);
					return false;
				}
			}
			return true;
		}
		int num = gridPositions.Min((Vec3Int pos) => pos.y);
		bool flag = blueprint.Size.y > 1;
		BaseBuildingInstance buildingToMove = MonoSingleton<MoveBuildingsManager>.Instance.BuildingToMove;
		foreach (Vec3Int item2 in gridPositions)
		{
			Vec3Int a = item2;
			if (!flag || a.y == num)
			{
				Vec3Int b = Vec3Int.down;
				Vec3Int vec3Int = a + b;
				if (!BuildingExists(BuildingType.Floor, a) && GetWallTypeBuildingWithVerticalStability(vec3Int) == null && !MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int))
				{
					ShowMessageDefaultSpaceNotBuildable();
					return false;
				}
			}
		}
		using (PooledList<BaseBuildingInstance> overlappingBuildings = ListPool<BaseBuildingInstance>.GetJanitor())
		{
			foreach (Vec3Int item3 in gridPositions)
			{
				BaseBuildingInstance baseBuildingInstance = GetBlockerBuilding(item3);
				if (baseBuildingInstance != null && baseBuildingInstance != buildingToMove && !overlappingBuildings.Contains(baseBuildingInstance))
				{
					overlappingBuildings.Add(baseBuildingInstance);
				}
			}
			return CanPlaceAndCancel(overlappingBuildings, blueprint, gridPositions.Count > 1);
		}
		BaseBuildingInstance GetBlockerBuilding(Vec3Int gridPos)
		{
			if (!positionInstanceListDictionary.TryGetValue(gridPos, out var value))
			{
				return null;
			}
			foreach (BaseBuildingInstance item4 in value)
			{
				if (!item4.HasDisposed)
				{
					BuildingType buildingType = item4.BuildingType;
					if (buildingType != BuildingType.Floor && buildingType != BuildingType.Rug && buildingType != BuildingType.Beam && item4.Blueprint.PlacementType != PlacementType.WallSocket)
					{
						return item4;
					}
				}
			}
			return null;
		}
	}

	private bool CanPlaceAndCancel(PooledList<BaseBuildingInstance> overlappingBuildings, BaseBuildingBlueprint blueprint, bool shouldCheckForBlueprintCancellation)
	{
		if (overlappingBuildings.Count > 0)
		{
			BaseBuildingInstance blockerBuilding = overlappingBuildings[0];
			if (!shouldCheckForBlueprintCancellation)
			{
				ShowMessageBlockedByBuilding(blockerBuilding, blueprint.PlacementType);
				return false;
			}
			if (overlappingBuildings.All((BaseBuildingInstance x) => x.ConstructionPhase == ConstructionPhase.Blueprint && x.Blueprint == blueprint))
			{
				blueprintOverlappingBuildingToCancelList.AddRange(overlappingBuildings);
				return true;
			}
			foreach (BaseBuildingInstance item in overlappingBuildings)
			{
				if (item.Blueprint != blueprint || item.ConstructionPhase != ConstructionPhase.Blueprint)
				{
					ShowMessageBlockedByBuilding(item, blueprint.PlacementType);
					return false;
				}
			}
		}
		return true;
	}

	private bool CanPlaceOilBlob(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		BuildingType checkFor = ~(BuildingType.Default | BuildingType.Floor | BuildingType.Beam | BuildingType.Rug);
		foreach (Vec3Int item in gridPositions)
		{
			Vec3Int a = item;
			BaseBuildingInstance firstBuilding = GetFirstBuilding(checkFor, a);
			if (firstBuilding != null && firstBuilding.Blueprint.PlacementType != PlacementType.WallSocket)
			{
				ShowMessageBlockedByBuilding(firstBuilding, blueprint.PlacementType);
				return false;
			}
			BaseBuildingInstance firstBuilding2 = GetFirstBuilding(BuildingType.Floor, a);
			Vec3Int b;
			if (firstBuilding2 != null)
			{
				if (!firstBuilding2.Blueprint.PassthroughFloor)
				{
					return true;
				}
				b = Vec3Int.down;
				Vec3Int vec3Int = a + b;
				if (GetWallTypeBuildingWithVerticalStability(vec3Int) == null && !MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int))
				{
					MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("default_error_space_not_buildable"));
					return false;
				}
			}
			b = Vec3Int.down;
			Vec3Int vec3Int2 = a + b;
			if (GetWallTypeBuildingWithVerticalStability(vec3Int2) == null && !MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int2))
			{
				MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("default_error_space_not_buildable"));
				return false;
			}
			WaterDepthLevel waterLevelAsDepth = map.WaterManager.GetWaterLevelAsDepth(a);
			if (waterLevelAsDepth != WaterDepthLevel.None)
			{
				if (!blueprint.CanPlaceOnWater)
				{
					MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("default_error_space_not_buildable"));
					return false;
				}
				if (waterLevelAsDepth != WaterDepthLevel.Low)
				{
					MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("default_error_space_not_buildable"));
					return false;
				}
			}
		}
		return true;
	}

	private bool CanPlaceWell(BaseBuildingBlueprint blueprint, PooledList<Vec3Int> gridPositions, bool showBbt = true)
	{
		Vec3Int a = gridPositions[0];
		Vec3Int a2 = gridPositions[2];
		Vec3Int b = Vec3Int.down;
		BaseBuildingInstance firstBuilding = GetFirstBuilding(BuildingType.Stairs | BuildingType.Ladder, a + b);
		if (firstBuilding != null)
		{
			ShowMessageBlockedByBuilding(firstBuilding, blueprint.PlacementType);
			LogPrintInvalidPlacementPosition(blueprint.GetID(), a);
			return false;
		}
		b = Vec3Int.down;
		firstBuilding = GetFirstBuilding(BuildingType.Stairs | BuildingType.Ladder, a2 + b);
		if (firstBuilding != null)
		{
			ShowMessageBlockedByBuilding(firstBuilding, blueprint.PlacementType);
			LogPrintInvalidPlacementPosition(blueprint.GetID(), a2);
			return false;
		}
		using PooledList<Vec3Int> gridPositions2 = ListPool<Vec3Int>.GetJanitor();
		gridPositions2.Add(a);
		gridPositions2.Add(a2);
		if (!CanPlaceInteriorCommon(blueprint, gridPositions2))
		{
			return false;
		}
		Vec3Int a3 = gridPositions[1];
		BuildingType checkFor = ~(BuildingType.Default | BuildingType.Floor);
		if (BuildingExists(checkFor, a3))
		{
			return false;
		}
		if (MonoSingleton<GroundManager>.Instance.GroundExists(a3))
		{
			return false;
		}
		b = Vec3Int.down;
		firstBuilding = GetFirstBuilding(BuildingType.Stairs, a3 + b);
		if (firstBuilding != null)
		{
			ShowMessageBlockedByBuilding(firstBuilding, blueprint.PlacementType);
			LogPrintInvalidPlacementPosition(blueprint.GetID(), a3);
			return false;
		}
		return true;
	}

	public void RemoveBuggyRoof(RoofComponentInstance buggyRoof)
	{
		BuildingType key = BuildingType.Roof;
		foreach (Vec3Int position in buggyRoof.Positions)
		{
			if (typePositionListDictionary.TryGetValue(key, out var value))
			{
				value.TryGetValue(position, out var value2);
				value2?.Remove(buggyRoof.OwnerBuilding);
			}
			if (TypePositionInstanceDictionary.TryGetValue(key, out var value3))
			{
				value3.Remove(position);
			}
			if (positionInstanceListDictionary.TryGetValue(position, out var value4))
			{
				value4.Remove(buggyRoof.OwnerBuilding);
			}
		}
	}

	public void ReCacheFixedRoof(RoofComponentInstance fixedRoof)
	{
		BuildingType key = BuildingType.Roof;
		foreach (Vec3Int position in fixedRoof.Positions)
		{
			typePositionListDictionary[key].TryAdd(position, new List<BaseBuildingInstance>());
			typePositionListDictionary[key][position].AddUnique(fixedRoof.OwnerBuilding);
			positionInstanceListDictionary.TryAdd(position, new List<BaseBuildingInstance>());
			positionInstanceListDictionary[position].AddUnique(fixedRoof.OwnerBuilding);
			positionBuildingTypeInstanceDictionary.TryAdd(position, new Dictionary<BuildingType, BaseBuildingInstance>());
			positionBuildingTypeInstanceDictionary[position].TryAdd(key, fixedRoof.OwnerBuilding);
			TypePositionInstanceDictionary[key].TryAdd(position, fixedRoof.OwnerBuilding);
		}
	}

	private void RemoveCachedBuilding(BaseBuildingInstance baseBuildingInstance)
	{
		if (baseBuildingInstance == null)
		{
			Debug.LogError("Cannot remove null building from cache.");
			return;
		}
		damagedBuildings.Remove(baseBuildingInstance);
		placedBlueprints.Remove(baseBuildingInstance);
		uniqueIdBuildingDictionary.Remove(baseBuildingInstance.UniqueId);
		string blueprintId = baseBuildingInstance.BlueprintId;
		if (buildingsById.TryGetValue(blueprintId, out var value))
		{
			value.Remove(baseBuildingInstance);
		}
		BuildingType buildingType = baseBuildingInstance.BuildingType;
		if (TypeInstanceView.TryGetValue(buildingType, out var value2))
		{
			value2.Remove(baseBuildingInstance);
		}
		blueprintsToCarveAreas.Remove(baseBuildingInstance);
		playerTriggeredEventHolders.Remove(baseBuildingInstance);
		foreach (Vec3Int position in baseBuildingInstance.Positions)
		{
			if (typePositionListDictionary.TryGetValue(buildingType, out var value3))
			{
				value3.TryGetValue(position, out var value4);
				value4?.Remove(baseBuildingInstance);
			}
			if (TypePositionInstanceDictionary.TryGetValue(buildingType, out var value5))
			{
				value5.Remove(position);
			}
			if (positionInstanceListDictionary.TryGetValue(position, out var value6))
			{
				value6.Remove(baseBuildingInstance);
			}
		}
		if (baseBuildingInstance.FactionOwnership == FactionOwnership.Player)
		{
			float wealth = baseBuildingInstance.GetWealth();
			TotalBuildingWealth -= wealth;
			bool isEnabled;
			FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(38, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingsManagerMain.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("TotalBuildingWealth -= ");
				messageBuilder.AppendFormatted(wealth);
				messageBuilder.AppendLiteral(" -> ");
				messageBuilder.AppendFormatted(TotalBuildingWealth);
				messageBuilder.AppendLiteral(", building ");
				messageBuilder.AppendFormatted(baseBuildingInstance);
			}
			Log.Debug(messageBuilder);
		}
	}

	public void BuildingDeconstructed(BaseBuildingInstance baseBuildingInstance)
	{
		DestroyBuilding(baseBuildingInstance);
	}

	public void DestroyVoxelBuilding(BaseBuildingInstance voxelBuilding)
	{
		if (voxelBuilding != null && !voxelBuilding.HasDisposed)
		{
			RemoveCachedBuilding(voxelBuilding);
			DestroyBuilding(voxelBuilding, replaced: true);
		}
	}

	public void DestroySocketBuilding(BaseBuildingInstance socketedBuilding)
	{
		switch (socketedBuilding.ConstructionPhase)
		{
		case ConstructionPhase.Blueprint:
			socketedBuilding.DropConstructionResources();
			break;
		case ConstructionPhase.Foundation:
			socketedBuilding.DropConstructionResources();
			break;
		case ConstructionPhase.Finished:
			if (socketedBuilding.Blueprint.SpawnStructurePileOnStabilityLoss && !LoadingController.IsLeavingMainScene)
			{
				SpawnBuildingPile(socketedBuilding);
			}
			break;
		}
		DestroyBuilding(socketedBuilding);
	}

	public void DestroyBuilding(BaseBuildingInstance baseBuildingInstance, bool replaced = false, bool skipStabilityCheck = false)
	{
		if (baseBuildingInstance == null || baseBuildingInstance.HasDisposed || LoadingController.IsLeavingMainScene)
		{
			return;
		}
		DebugEventLog.Write(new BuildingRemoved(baseBuildingInstance));
		this.BeforeBuildingDestroyedEvent?.Invoke(baseBuildingInstance);
		if (villageSaveData == null)
		{
			villageSaveData = GlobalSaveController.CurrentVillageData;
		}
		villageSaveData.RemoveCopyData(baseBuildingInstance);
		MonoSingleton<MoveBuildingsManager>.Instance.SourceBuildingDestroyed(baseBuildingInstance);
		ConstructionJobManager.RemoveDeconstructJobs(baseBuildingInstance);
		RemoveForbiddenArea(baseBuildingInstance);
		RemoveCachedBuilding(baseBuildingInstance);
		baseBuildingInstance.Dispose();
		if (baseBuildingInstance.FactionOwnership == FactionOwnership.Enemy)
		{
			map.EnemyBuildingsManager.RemoveEnemyBuilding(baseBuildingInstance);
		}
		if (!replaced && !skipStabilityCheck)
		{
			map.StabilityManager.BuildingDestroyed(baseBuildingInstance);
		}
		this.BuildingDestroyedEvent?.Invoke(baseBuildingInstance);
		baseBuildingInstance.Map.RemoveFromWorld(baseBuildingInstance);
		MonoSingleton<ConstructionController>.Instance.BuildingDestroyed(baseBuildingInstance);
		if (baseBuildingInstance.Blueprint.TransfersStability())
		{
			Vec3Int a = baseBuildingInstance.GridDataPosition;
			List<BaseBuildingInstance> value2;
			if (baseBuildingInstance.Blueprint.BuildingType != BuildingType.Floor)
			{
				Vector3 b = Vector3.up;
				Vec3Int vec3Int = a + b;
				if (positionInstanceListDictionary.TryGetValue(vec3Int, out var value))
				{
					if (value == null || value.Count == 0)
					{
						ClearOilBlob(vec3Int);
					}
					else
					{
						BaseBuildingInstance[] array = value.ToArray();
						foreach (BaseBuildingInstance baseBuildingInstance2 in array)
						{
							BaseBuildingBlueprint blueprint = baseBuildingInstance2.Blueprint;
							if ((blueprint.TransfersStability() && !blueprint.PassthroughFloor) || blueprint.PlacementType == PlacementType.WallSocket || blueprint.BuildingType == BuildingType.Roof || blueprint.BuildingType == BuildingType.Well || GetWallTypeBuildingWithVerticalStability(a) != null || MonoSingleton<GroundManager>.Instance.GroundExists(a))
							{
								continue;
							}
							if (!BuildingExists(BuildingType.Floor, vec3Int))
							{
								if (baseBuildingInstance2.ConstructionPhase == ConstructionPhase.Blueprint)
								{
									baseBuildingInstance2.ForceSetHealthDepleted();
									DestroyBuilding(baseBuildingInstance2);
								}
								else
								{
									baseBuildingInstance2.BuildingRemovedSpawnResources(baseBuildingInstance2.WorldPosition, BuildingResourceSpawnType.StabilityLoss);
									baseBuildingInstance2.ForceSetHealthDepleted();
									DestroyBuilding(baseBuildingInstance2);
								}
							}
							if (!BuildingExists(BuildingType.Floor, vec3Int, (BaseBuildingInstance x) => !x.Blueprint.PassthroughFloor))
							{
								ClearOilBlob(vec3Int);
							}
						}
					}
				}
				else
				{
					ClearOilBlob(vec3Int);
				}
			}
			else if (positionInstanceListDictionary.TryGetValue(a, out value2))
			{
				Vec3Int b2 = Vec3Int.down;
				Vec3Int vec3Int2 = a + b2;
				if (value2 == null || value2.Count == 0)
				{
					ClearOilBlob(a);
				}
				else
				{
					BaseBuildingInstance[] array = value2.ToArray();
					foreach (BaseBuildingInstance baseBuildingInstance3 in array)
					{
						BaseBuildingBlueprint blueprint2 = baseBuildingInstance3.Blueprint;
						if ((!blueprint2.TransfersStability() || blueprint2.PassthroughFloor) && blueprint2.PlacementType != PlacementType.WallSocket && GetWallTypeBuildingWithVerticalStability(vec3Int2) == null && !MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int2))
						{
							if (baseBuildingInstance3.ConstructionPhase == ConstructionPhase.Blueprint)
							{
								DestroyBuilding(baseBuildingInstance3);
							}
							else
							{
								baseBuildingInstance3.BuildingRemovedSpawnResources(baseBuildingInstance3.WorldPosition, BuildingResourceSpawnType.StabilityLoss);
								DestroyBuilding(baseBuildingInstance3);
							}
							ClearOilBlob(a);
						}
					}
				}
			}
			else
			{
				ClearOilBlob(a);
			}
			this.StabilityCarrierDestroyedEvent?.Invoke(baseBuildingInstance, replaced);
		}
		if (!baseBuildingInstance.HealthDepleted)
		{
			if (baseBuildingInstance.Blueprint.BuildingType == BuildingType.Roof)
			{
				map.RoofMeshVariationManager.RefreshNeighbors(baseBuildingInstance);
			}
			else
			{
				AddToMeshRefreshCache(baseBuildingInstance);
			}
		}
		MonoSingleton<ConstructionController>.Instance.ObjectDestroyedCheckFallDown(baseBuildingInstance.GridDataPosition);
		MonoSingleton<TaskController>.Instance.WaitForNextFrameUnscaled().Then(TriggerMeshRefresh);
		void AddToMeshRefreshCache(BaseBuildingInstance buildingInstance)
		{
			if (buildingDestroyedMeshRefreshCache.TryGetValue(buildingInstance.BuildingType, out var value4))
			{
				value4.Enqueue(buildingInstance.GridDataPosition);
			}
		}
		void ClearOilBlob(Vec3Int position)
		{
			FireSimLogic fireSimLogic = map.FireSimLogic;
			if (fireSimLogic != null)
			{
				MapNode node = map.GetNode(in position);
				if (node != null)
				{
					fireSimLogic.SetOilBlobHealth(node.Index, 0f, 0);
					fireSimLogic.SetOilBlobHealth(node.Index, 0f, 1);
				}
			}
		}
		void TriggerMeshRefresh()
		{
			foreach (KeyValuePair<BuildingType, Queue<Vec3Int>> item in buildingDestroyedMeshRefreshCache)
			{
				while (item.Value.Count > 0)
				{
					if (item.Value.TryDequeue(out var result) && meshRefreshCallbacks.TryGetValue(item.Key, out var value3))
					{
						value3(result);
					}
				}
			}
		}
	}

	public BaseBuildingInstance GetBuilding(Vec3Int position, ConstructionPhase constructionPhase)
	{
		if (!positionInstanceListDictionary.TryGetValue(position, out var value))
		{
			return null;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (item.ConstructionPhase == constructionPhase && item.Blueprint.TransfersStability())
			{
				return item;
			}
		}
		return null;
	}

	private Vec3Int[] GetStabilityCandidatesPositions(Vec3Int input)
	{
		Vec3Int[] array = new Vec3Int[5];
		Vec3Int b = Vec3Int.left;
		array[0] = input + b;
		Vec3Int b2 = Vec3Int.right;
		array[1] = input + b2;
		Vec3Int b3 = new Vec3Int(0, 0, 1);
		array[2] = input + b3;
		Vec3Int b4 = new Vec3Int(0, 0, -1);
		array[3] = input + b4;
		Vec3Int b5 = Vec3Int.down;
		array[4] = input + b5;
		return array;
	}

	public bool HasStabilityToBuild(BaseBuildingInstance instance)
	{
		DoorComponentBlueprint byID = Repository<DoorComponentRepository, DoorComponentBlueprint>.Instance.GetByID(instance.Blueprint.DoorComponentID);
		if (byID != null)
		{
			if (byID.DoorType == DoorType.Regular)
			{
				return HasStabilityToBuildBuildingBlocks(instance);
			}
			return HasStabilityToBuildInterior(instance);
		}
		if (instance.Blueprint.UseBasicHasStabilityCheck())
		{
			return HasStabilityToBuildBuildingBlocks(instance);
		}
		if (instance.Blueprint.BuildingType == BuildingType.Well)
		{
			return HasStabilityToBuildWell(instance);
		}
		return HasStabilityToBuildInterior(instance);
	}

	private bool HasStabilityToBuildWell(BaseBuildingInstance instance)
	{
		BuildingType checkFor = BuildingType.Wall | BuildingType.Window | BuildingType.Door | BuildingType.BarnDoor;
		PooledList<Vec3Int> janitor = ListPool<Vec3Int>.GetJanitor();
		try
		{
			janitor.Add(instance.Positions[0]);
			List<Vec3Int> positions = instance.Positions;
			janitor.Add(positions[positions.Count - 1]);
			foreach (Vec3Int item in janitor)
			{
				Vec3Int a = item;
				Vector3 b = Vector3.down;
				Vec3Int vec3Int = a + b;
				if (!BuildingExists(BuildingType.Floor, a, (BaseBuildingInstance x) => x.ConstructionPhase == ConstructionPhase.Finished) && !BuildingExists(checkFor, vec3Int, (BaseBuildingInstance x) => x.ConstructionPhase == ConstructionPhase.Finished) && !MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int) && map.StabilityManager.GetFinishedStability(a) < 1)
				{
					return false;
				}
			}
			return true;
		}
		finally
		{
			((IDisposable)janitor).Dispose();
		}
	}

	private bool HasStabilityToBuildInterior(BaseBuildingInstance instance)
	{
		BuildingType checkFor = BuildingType.Wall | BuildingType.Window | BuildingType.Door | BuildingType.BarnDoor;
		int num = instance.Positions.Min((Vec3Int item) => item.y);
		int num2 = 0;
		int num3 = 0;
		foreach (Vec3Int position in instance.Positions)
		{
			Vec3Int a = position;
			if (a.y <= num)
			{
				num3++;
				Vector3 b = Vector3.down;
				Vec3Int vec3Int = a + b;
				if (BuildingExists(BuildingType.Floor, a, (BaseBuildingInstance x) => x.ConstructionPhase == ConstructionPhase.Finished) && map.StabilityManager.GetFinishedStability(a) > 0)
				{
					num2++;
				}
				else if (BuildingExists(checkFor, vec3Int, (BaseBuildingInstance x) => x.ConstructionPhase == ConstructionPhase.Finished) && map.StabilityManager.GetFinishedStability(vec3Int) > 0)
				{
					num2++;
				}
				else if (MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int) && map.StabilityManager.GetFinishedStability(vec3Int) > 0)
				{
					num2++;
				}
			}
		}
		return num2 == num3;
	}

	private bool HasStabilityToBuildBuildingBlocks(BaseBuildingInstance instance)
	{
		Vec3Int a = instance.GridDataPosition;
		Vec3Int b = Vec3Int.down;
		if (BuildingExists(BuildingType.Beam, a + b, (BaseBuildingInstance x) => x.ConstructionPhase == ConstructionPhase.Finished))
		{
			return true;
		}
		a = instance.GridDataPosition;
		b = Vec3Int.down;
		Vec3Int vec3Int = a + b;
		if (MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int) && map.StabilityManager.GetFinishedStability(vec3Int) > 0)
		{
			return true;
		}
		Vec3Int[] stabilityCandidatesPositions = GetStabilityCandidatesPositions(instance.GridDataPosition);
		for (int i = 0; i < stabilityCandidatesPositions.Length; i++)
		{
			if (MonoSingleton<GroundManager>.Instance.GroundExists(stabilityCandidatesPositions[i]) && map.StabilityManager.GetFinishedStability(stabilityCandidatesPositions[i]) > 1)
			{
				return true;
			}
			MapNode node = map.GetNode(in stabilityCandidatesPositions[i]);
			if (!(node.GetWorldObject(GridDataType.BuildingFinished) is BaseBuildingInstance { ConstructionPhase: var constructionPhase } baseBuildingInstance) || !constructionPhase.Equals(ConstructionPhase.Finished))
			{
				continue;
			}
			if (node.Position.y == instance.GridDataPosition.y)
			{
				if (baseBuildingInstance.Stability > 1)
				{
					return true;
				}
				continue;
			}
			if (baseBuildingInstance.GridDataPosition.y < instance.GridDataPosition.y)
			{
				if (baseBuildingInstance.Blueprint.HasVerticalStability())
				{
					return baseBuildingInstance.Stability > 0;
				}
				return false;
			}
			if (baseBuildingInstance.Blueprint.TransfersStability())
			{
				return baseBuildingInstance.Stability > 1;
			}
			return false;
		}
		return false;
	}

	private void OnObjectRemoved(WorldObject obj)
	{
		switch (obj.Type)
		{
		case WorldObjectType.Building:
			if (((BaseBuildingInstance)obj).ConstructionPhase == ConstructionPhase.Finished)
			{
				RecalculateReachabilityForNeighbors(obj);
			}
			break;
		case WorldObjectType.Slope:
			RecalculateReachabilityForNeighbors(obj);
			break;
		case WorldObjectType.MapResource:
			if (obj is DigMarkerResourceInstance)
			{
				RecalculateReachabilityForNeighbors(obj);
			}
			break;
		case WorldObjectType.Cropfield:
		case WorldObjectType.ResourcePile:
			break;
		}
	}

	[MustDisposeResource]
	public PooledList<WorldObject> GetNeighboursForReachabilityUpdate(WorldObject obj)
	{
		if (obj == null || obj.HasDisposed)
		{
			return default(PooledList<WorldObject>);
		}
		PooledList<WorldObject> threadLocalSmallJanitor = ListPool<WorldObject>.GetThreadLocalSmallJanitor();
		PooledHashSet<Vec3Int> janitor = HashSetPool<Vec3Int>.GetJanitor();
		try
		{
			List<Vec3Int> positions = obj.Positions;
			if (positions != null && positions.Count > 1)
			{
				for (int i = 0; i < obj.Positions.Count; i++)
				{
					Vec3Int a = obj.Positions[i];
					for (int j = 0; j < MapNodeUtils.NeighborsXZ.Length; j++)
					{
						janitor.Add(a + MapNodeUtils.NeighborsXZ[j]);
					}
				}
			}
			else
			{
				for (int k = 0; k < MapNodeUtils.NeighborsXZ.Length; k++)
				{
					Vec3Int a2 = obj.GridDataPosition;
					janitor.Add(a2 + MapNodeUtils.NeighborsXZ[k]);
				}
			}
			VillageMap villageMap = obj.Map;
			foreach (Vec3Int item in janitor)
			{
				Vec3Int gridPosition = item;
				MapNode node = villageMap.GetNode(in gridPosition);
				if (node == null)
				{
					continue;
				}
				MapNode mapNode = node.GetNodeAbove()?.GetNodeAbove();
				if (mapNode != null && mapNode.CheckIsDataType(GridDataType.DigMarkerResource | GridDataType.DigMarkerResourceToMine))
				{
					foreach (WorldObject worldObject in mapNode.WorldObjects)
					{
						if (!worldObject.HasDisposed)
						{
							threadLocalSmallJanitor.Add(worldObject);
						}
					}
				}
				foreach (WorldObject worldObject2 in node.WorldObjects)
				{
					if (!worldObject2.HasDisposed)
					{
						threadLocalSmallJanitor.Add(worldObject2);
					}
				}
			}
			return threadLocalSmallJanitor;
		}
		finally
		{
			((IDisposable)janitor).Dispose();
		}
	}

	internal void RecalculateReachabilityForNeighbors(WorldObject obj)
	{
		using PooledList<WorldObject> pooledList = GetNeighboursForReachabilityUpdate(obj);
		for (int i = 0; i < pooledList.Count; i++)
		{
			pooledList[i].UpdateReachability();
		}
	}

	private void OnMapLoaded(bool afterLoad)
	{
		MonoSingleton<ConstructionController>.Instance.RefreshBlueprintsAfterLoading();
		InitializeCanPlace();
		worldDataSize = VillageManager.ActiveVillage.Map.Size;
		villageSaveData = GlobalSaveController.CurrentVillageData;
		map = VillageManager.ActiveVillage.Map;
		MonoSingleton<ResourcePileController>.Instance.ResourceCountChangeEvent += OnAvailableResourcesChanged;
		MonoSingleton<ResourcePileController>.Instance.SpawnPileEvent += OnPileSpawned;
		MonoSingleton<WorkerController>.Instance.WorkerCountChangedEvent += OnWorkerCountChanged;
		MonoSingleton<ConstructionController>.Instance.DoorLockOrderChangedEvent += OnDoorLockStateChanged;
		MonoSingleton<BuildingPlacementManager>.Instance.SelectionCanceledEvent += OnCancelPlacement;
		MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent += new Action<BaseBuildingInstance>(OnBuildingStateChanged);
		MonoSingleton<ConstructionController>.Instance.ConstructionMaterialsDeliveredEvent += OnBuildingStateChanged;
		MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent += new Action<BaseBuildingInstance>(OnBuildingStateChanged);
		MonoSingleton<BuildingsManagerCommon>.Instance.StartResourceChangedRefreshBlueprintsCoroutine(GetBlueprints(), MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys.ToArray());
		map.WaterManager.WaterLevelChangedEvent += OnWaterLevelChanged;
		InitializeComponentManagersCache();
		MonoSingleton<SceneController>.Instance.Tick += OnTick;
		MonoSingleton<SceneController>.Instance.LateTick += OnLateTick;
		float pileTrackerRecountTime = MonoSingleton<ResourcePileTracker>.Instance.LastRecountTime;
		MonoSingleton<TaskController>.Instance.WaitUntil((float time) => MonoSingleton<ResourcePileTracker>.Instance.LastRecountTime > pileTrackerRecountTime && !MonoSingleton<BuildingsManagerCommon>.Instance.CoroutineRunning).Then(WorldStateChangedRefreshBuildings);
		TryAttachToTheGround();
		AddMeshRefreshCallbacks();
	}

	private void AddMeshRefreshCallbacks()
	{
		meshRefreshCallbacks.Add(BuildingType.Wall, map.WallAutomaticMeshVariationManager.RefreshNeighbors);
		meshRefreshCallbacks.Add(BuildingType.Voxel, map.WallAutomaticMeshVariationManager.RefreshNeighbors);
		meshRefreshCallbacks.Add(BuildingType.Floor, map.FloorAutomaticMeshVariationManager.RefreshNeighbors);
		meshRefreshCallbacks.Add(BuildingType.Merlon, map.MerlonRotationManager.RefreshNeighbors);
		meshRefreshCallbacks.Add(BuildingType.Fence, map.FenceAutomaticMeshVariationManager.RefreshNeighbors);
		meshRefreshCallbacks.Add(BuildingType.FenceGate, map.FenceAutomaticMeshVariationManager.RefreshNeighbors);
	}

	private void ClearMeshRefreshCallbacks()
	{
		meshRefreshCallbacks.Clear();
	}

	private void InitializeComponentManagersCache()
	{
		componentsGetterCache.Add(typeof(BeamComponentInstance), map.BeamComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(LadderComponentInstance), map.LadderComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(BedComponentInstance), map.BedComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(ChairComponentInstance), map.ChairComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(DoorComponentInstance), map.DoorComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(FuelConsumerComponentInstance), map.FuelConsumerComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(GraveComponentInstance), map.GraveComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(ProductionComponentInstance), map.ProductionComponentBuildingManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(PenMarkerComponentInstance), map.PenMarkerComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(TrapComponentInstance), map.TrapComponentsManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(RoofComponentInstance), map.RoofComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(RugComponentInstance), map.RugComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(ShelfComponentInstance), map.ShelfComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(ShrineComponentInstance), map.ShrineComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(SignComponentInstance), map.SignComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(StairsComponentInstance), map.StairsComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(WindowComponentInstance), map.WindowComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(EntertainmentComponentInstance), map.EntertainmentComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(DecorationComponentInstance), map.DecorationComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(TradingPostComponentInstance), map.TradingPostComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(CaravanPostComponentInstance), map.CaravanPostComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(MapTableComponentInstance), map.MapTableComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(GallowsComponentInstance), map.GallowsComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(WellComponentInstance), map.WellComponentManager.GetComponentInstance);
		componentsGetterCache.Add(typeof(SiegeWeaponComponentInstance), map.SiegeWeaponComponentManager.GetComponentInstance);
	}

	private void OnAvailableResourcesChanged(Resource resource, ResourcePileCount pileCount)
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "Resource changed optimize blueprint refresh", delegate
		{
			ResourceChangedRefreshBlueprints(resource);
		});
	}

	private void OnPileSpawned(ResourcePileInstance pile)
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "Resource changed optimize blueprint refresh", delegate
		{
			ResourceChangedRefreshBlueprints(pile.Blueprint);
		});
	}

	private void OnWorkerCountChanged()
	{
		MonoSingleton<TaskController>.Instance.WaitForUnscaled(0.1f).Then(WorldStateChangedRefreshBuildings);
	}

	private void OnDoorLockStateChanged(DoorComponentInstance doorBuildingInstance)
	{
		MonoSingleton<TaskController>.Instance.WaitForNextFrameUnscaled().Then(WorldStateChangedRefreshBuildings);
	}

	public void BuildingConstructionCompleted(BaseBuildingInstance instance)
	{
		placedBlueprints.Remove(instance);
	}

	public HashSet<BaseBuildingInstance> GetDamagedBuildings()
	{
		return damagedBuildings;
	}

	public bool HasDamagedBuildings()
	{
		return damagedBuildings.Count > 0;
	}

	public void OnBuildingHealthStatUpdated(BaseBuildingInstance building, float value, float max)
	{
		if (value < max - 0.5f)
		{
			damagedBuildings.Add(building);
		}
		else
		{
			damagedBuildings.Remove(building);
		}
	}

	public bool HasEnoughAllowedResources(BaseBuildingBlueprint model, int totalItemCount = 1)
	{
		StringIntDictionary materials = model.Materials;
		if (materials == null)
		{
			return true;
		}
		foreach (string key in materials.Dictionary.Keys)
		{
			if (MonoSingleton<ResourcePileTracker>.Instance.GetCount(Repository<ResourceRepository, Resource>.Instance.GetByID(key)).AllowedCount < materials.Dictionary[key] * totalItemCount)
			{
				return false;
			}
		}
		return true;
	}

	public bool StabilityBuildingExists(Vec3Int gridPos, ConstructionPhase constructionPhase, BuildingType buildingTypeFlag)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPos, out var value))
		{
			return false;
		}
		if (value.Count == 0)
		{
			return false;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (item.ConstructionPhase != constructionPhase || (item.BuildingType & buildingTypeFlag) == 0)
			{
				return false;
			}
		}
		return true;
	}

	public BaseBuildingInstance GetBuildingInstance(Vec3Int gridPosition)
	{
		MapNode node = map.GetNode(in gridPosition);
		if (node == null)
		{
			return null;
		}
		GridDataType gridDataType = GridDataType.AnyBuildPhase | GridDataType.OthersBlueprint | GridDataType.OthersUnfinished | GridDataType.FurnitureGate;
		if ((node.DataType & gridDataType) == 0)
		{
			return null;
		}
		foreach (WorldObject worldObject in node.WorldObjects)
		{
			if ((worldObject.GridDataType & gridDataType) != 0 && worldObject is BaseBuildingInstance result)
			{
				return result;
			}
		}
		return null;
	}

	public BaseBuildingInstance GetBuildingInstance(Vec3Int gridPosition, BuildingType buildingType)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return null;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (buildingType.HasFlag(item.BuildingType))
			{
				return item;
			}
		}
		return null;
	}

	public BaseBuildingInstance GetBuildingInstance(Vec3Int gridPos, Func<BaseBuildingInstance, bool> condition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPos, out var value))
		{
			return null;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (condition(item))
			{
				return item;
			}
		}
		return null;
	}

	public bool VerticalStabilityCarrierExitsForBlueprints(Vec3Int position)
	{
		if (map.GetNode(in position) == null)
		{
			return false;
		}
		if (!positionInstanceListDictionary.TryGetValue(position, out var value))
		{
			return false;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (item.BuildingType.HasFlag(BuildingType.Wall) || item.BuildingType.HasFlag(BuildingType.Window) || item.BuildingType.HasFlag(BuildingType.Door) || item.BuildingType.HasFlag(BuildingType.BarnDoor) || item.BuildingType.HasFlag(BuildingType.Ladder))
			{
				return true;
			}
		}
		if (TryGetWallForBeam(position) != null)
		{
			return true;
		}
		return false;
	}

	public BaseBuildingInstance TryGetWallForBeam(Vec3Int gridPosition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return null;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if ((item.BuildingType & (BuildingType.Wall | BuildingType.Voxel)) != 0 && item.Blueprint.BuildingType != BuildingType.Ladder)
			{
				return item;
			}
		}
		return null;
	}

	public bool VerticalStabilityCarrierExitsForFinishedAndFoundations(Vec3Int position)
	{
		if (map.GetNode(in position) == null)
		{
			return false;
		}
		if (!positionInstanceListDictionary.TryGetValue(position, out var value))
		{
			return false;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (item.BuildingType.HasFlag(BuildingType.Wall) || item.BuildingType.HasFlag(BuildingType.Window) || item.BuildingType.HasFlag(BuildingType.Door) || item.BuildingType.HasFlag(BuildingType.BarnDoor))
			{
				return true;
			}
		}
		return false;
	}

	public bool BuildingExistsForBlueprint(Vec3Int gridPosition)
	{
		return BuildingExists(gridPosition);
	}

	public bool BuildingExistsForFinished(Vec3Int gridPosition)
	{
		return StabilityBuildingExists(gridPosition, onlyConstructed: true);
	}

	public bool StabilityBuildingExists(Vec3Int gridPos, Func<BaseBuildingInstance, bool> condition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPos, out var value))
		{
			return false;
		}
		if (value.Count == 0)
		{
			return false;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (!condition(item))
			{
				return false;
			}
		}
		return true;
	}

	public bool StabilityBuildingExists(Vec3Int gridPosition, bool onlyConstructed = false)
	{
		MapNode node = map.GetNode(in gridPosition);
		if (node == null)
		{
			return false;
		}
		GridDataType gridDataType = (onlyConstructed ? GridDataType.BuildingFinished : GridDataType.AnyBuildPhase);
		if ((node.DataType & gridDataType) == 0)
		{
			return false;
		}
		foreach (WorldObject worldObject in node.WorldObjects)
		{
			if ((worldObject.GridDataType & gridDataType) != 0 && worldObject is BaseBuildingInstance)
			{
				return true;
			}
		}
		return false;
	}

	public bool BuildingVerticalStabilityCarrierExits(Vec3Int gridPosition)
	{
		if (GetBuildingInstance(gridPosition, BuildingType.Wall) == null && GetBuildingInstance(gridPosition, BuildingType.Window) == null && GetBuildingInstance(gridPosition, BuildingType.Door) == null)
		{
			return GetBuildingInstance(gridPosition, BuildingType.BarnDoor) != null;
		}
		return true;
	}

	public bool FinishedBuildingVerticalStabilityCarrierExits(Vec3Int gridPosition)
	{
		BaseBuildingInstance buildingInstance = GetBuildingInstance(gridPosition, BuildingType.Wall);
		if (buildingInstance == null || buildingInstance.ConstructionPhase != ConstructionPhase.Finished)
		{
			BaseBuildingInstance buildingInstance2 = GetBuildingInstance(gridPosition, BuildingType.Window);
			if (buildingInstance2 == null || buildingInstance2.ConstructionPhase != ConstructionPhase.Finished)
			{
				BaseBuildingInstance buildingInstance3 = GetBuildingInstance(gridPosition, BuildingType.Door);
				if (buildingInstance3 == null || buildingInstance3.ConstructionPhase != ConstructionPhase.Finished)
				{
					BaseBuildingInstance buildingInstance4 = GetBuildingInstance(gridPosition, BuildingType.BarnDoor);
					if (buildingInstance4 == null)
					{
						return false;
					}
					return buildingInstance4.ConstructionPhase == ConstructionPhase.Finished;
				}
			}
		}
		return true;
	}

	public void CheckForDestruction(Vec3Int gridPosition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return;
		}
		for (int num = value.Count - 1; num >= 0; num--)
		{
			if (num < value.Count)
			{
				BaseBuildingInstance baseBuildingInstance = value[num];
				if (baseBuildingInstance.Blueprint.TransfersStability() && (baseBuildingInstance.GridDataPosition.Equals(gridPosition) || baseBuildingInstance.Positions.Contains(gridPosition)))
				{
					DestroyBuilding(baseBuildingInstance);
				}
			}
		}
	}

	public bool WallTypeBuildingExists(Vec3Int gridPos)
	{
		BuildingType checkFor = BuildingType.Wall | BuildingType.Voxel | BuildingType.Window | BuildingType.Door | BuildingType.BarnDoor;
		return BuildingExists(checkFor, gridPos);
	}

	public BaseBuildingInstance GetWallTypeBuildingWithVerticalStability(Vec3Int gridPosition)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPosition, out var value))
		{
			return null;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (item.Blueprint.IsWallTypeBuildingWithVerticalStability())
			{
				return item;
			}
		}
		return null;
	}

	public bool BuildingExists(Vec3Int gridPosition, BuildingType buildingType, bool onlyConstructed = false)
	{
		MapNode node = map.GetNode(in gridPosition);
		if (node == null)
		{
			return false;
		}
		GridDataType gridDataType = GridDataType.BuildingFinished | GridDataType.FurnitureGate;
		if (!onlyConstructed)
		{
			gridDataType |= GridDataType.BuildingBlueprint | GridDataType.BuildingUnfinished;
		}
		if ((node.DataType & gridDataType) == 0)
		{
			return false;
		}
		foreach (WorldObject worldObject in node.WorldObjects)
		{
			if ((worldObject.GridDataType & gridDataType) != 0 && worldObject is BaseBuildingInstance baseBuildingInstance && baseBuildingInstance.Blueprint.BuildingType.Equals(buildingType))
			{
				return true;
			}
		}
		return false;
	}

	public void RegisterOwnedBuilding(HumanoidInstance owner, BaseBuildingInstance building)
	{
		if (owner != null)
		{
			if (!ownedBuildings.ContainsKey(owner))
			{
				ownedBuildings.Add(owner, new Dictionary<BuildingType, List<BaseBuildingInstance>>());
			}
			BuildingType buildingType = building.BuildingType;
			if (!ownedBuildings[owner].ContainsKey(buildingType))
			{
				ownedBuildings[owner].Add(buildingType, new List<BaseBuildingInstance>());
			}
			for (int num = ownedBuildings[owner][buildingType].Count - 1; num >= 0; num--)
			{
				ownedBuildings[owner][buildingType][num].BuildingOwnershipInfo?.ClearOwner();
			}
			ownedBuildings[owner][buildingType].Add(building);
			MonoSingleton<BuildingOwnershipController>.Instance.BuildingOwnerRegistered(building);
		}
	}

	public void UnregisterOwnedBuilding(HumanoidInstance owner, BaseBuildingInstance building)
	{
		if (owner != null && building != null)
		{
			BuildingType buildingType = building.BuildingType;
			if (ownedBuildings.ContainsKey(owner) && ownedBuildings[owner].ContainsKey(buildingType))
			{
				ownedBuildings[owner][buildingType].Remove(building);
				MonoSingleton<BuildingOwnershipController>.Instance.BuildingOwnerUnregistered(building, owner);
			}
		}
	}

	public bool BuildingTypesExist(Vec3Int gridPosition, BuildingType buildingTypesPacked, bool onlyConstructed = false)
	{
		MapNode node = map.GetNode(in gridPosition);
		if (node == null || !node.IsVoxelAir())
		{
			return false;
		}
		GridDataType type = (onlyConstructed ? GridDataType.BuildingFinished : GridDataType.AnyBuildPhase);
		foreach (WorldObject worldObject in node.GetWorldObjects(type))
		{
			if (worldObject is BaseBuildingInstance baseBuildingInstance && (baseBuildingInstance.Blueprint.BuildingType & buildingTypesPacked) != 0)
			{
				return true;
			}
		}
		return false;
	}

	public void LoadSavedBeams()
	{
		foreach (WorldObject worldObject in VillageManager.ActiveVillage.WorldObjectStorage.WorldObjects)
		{
			if (worldObject is BaseBuildingInstance { BuildingType: BuildingType.Beam } baseBuildingInstance)
			{
				LoadSavedBuilding(baseBuildingInstance);
			}
		}
	}

	public void LoadSavedBuildings(bool placedOnBeam)
	{
		WorldObject[] array = VillageManager.ActiveVillage.WorldObjectStorage.WorldObjects.ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			if (array[i] is BaseBuildingInstance { HasDisposed: false, BuildingType: not BuildingType.Beam, Loaded: false } baseBuildingInstance)
			{
				if (!baseBuildingInstance.Blueprint.CanPlaceOnBeam)
				{
					LoadSavedBuilding(baseBuildingInstance);
				}
				else if (baseBuildingInstance.PlacedOnBeam == placedOnBeam)
				{
					LoadSavedBuilding(baseBuildingInstance);
				}
			}
		}
	}

	private void LoadSavedBuilding(BaseBuildingInstance baseBuildingInstance)
	{
		BaseBuildingBlueprint blueprint = baseBuildingInstance.Blueprint;
		if (blueprint == null)
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				map.RemoveFromWorld(baseBuildingInstance);
			});
			return;
		}
		BaseBuildingViewComponent baseBuildingViewComponent = MonoSingleton<BuildingPlacementManager>.Instance.SpawnBaseBuildingViewComponent(blueprint, baseBuildingInstance.GridDataPosition, (int)baseBuildingInstance.Angle);
		baseBuildingViewComponent.SetBaseBuildingInstance(baseBuildingInstance);
		switch (baseBuildingInstance.ConstructionPhase)
		{
		case ConstructionPhase.Blueprint:
			baseBuildingViewComponent.PreObjectPlacedOnMap();
			baseBuildingInstance.ObjectPlacedOnMap(afterLoading: true);
			baseBuildingViewComponent.ObjectPlacedOnMap(afterLoading: true);
			break;
		case ConstructionPhase.Foundation:
			baseBuildingViewComponent.PreObjectPlacedOnMap();
			baseBuildingInstance.ObjectPlacedOnMap(afterLoading: true);
			baseBuildingViewComponent.ObjectPlacedOnMap(afterLoading: true);
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				baseBuildingInstance.EnterFoundationState(afterLoading: true);
			});
			break;
		case ConstructionPhase.Finished:
			baseBuildingViewComponent.PreObjectPlacedOnMap();
			baseBuildingInstance.ObjectPlacedOnMap(afterLoading: true);
			baseBuildingViewComponent.ObjectPlacedOnMap(afterLoading: true);
			baseBuildingViewComponent.BuildProgress.enabled = false;
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				baseBuildingInstance.EnterFoundationState(afterLoading: true);
				baseBuildingInstance.EnterFinishedState(afterLoading: true);
			});
			break;
		}
		baseBuildingInstance.SetupAfterLoading();
		baseBuildingInstance.ReInstantiate();
		CacheBuildingInstance(baseBuildingViewComponent, afterLoading: true);
		this.LoadSavedBuildingEvent?.Invoke(baseBuildingInstance);
	}

	private void TryAttachToTheGround()
	{
		using PooledList<int> pooledList = uniqueIdBuildingDictionary.Keys.ToPooledListJanitor();
		foreach (int item in pooledList)
		{
			BaseBuildingInstance baseBuildingInstance = uniqueIdBuildingDictionary[item];
			if (baseBuildingInstance != null && !baseBuildingInstance.HasDisposed && !baseBuildingInstance.AttachedToSocketComponent)
			{
				TryAttachToTheGround(baseBuildingInstance);
			}
		}
	}

	private void TryAttachToTheGround(BaseBuildingInstance building)
	{
		if (building.Blueprint.PlacementType != PlacementType.WallSocket || building.BuildingType == BuildingType.Beam)
		{
			return;
		}
		if (building.ObjectSide != ObjectSide.None)
		{
			Vec3Int lhs = building.VoxelHolderPosition;
			Vec3Int rhs = Vec3Int.zero;
			if (lhs != rhs)
			{
				MonoSingleton<GroundManager>.Instance.AttachToVoxelSocket(building, building.ObjectSide, building.VoxelHolderPosition);
				return;
			}
		}
		int num = (int)building.Angle;
		Vec3Int a = building.GridDataPosition;
		ObjectSide voxelSide = ObjectSide.None;
		switch (num)
		{
		case 0:
		{
			Vec3Int lhs = Vec3Int.left;
			a += lhs;
			voxelSide = ObjectSide.Right;
			break;
		}
		case 90:
		{
			Vec3Int lhs = Vec3Int.forward;
			a += lhs;
			voxelSide = ObjectSide.Back;
			break;
		}
		case 180:
		{
			Vec3Int lhs = Vec3Int.right;
			a += lhs;
			voxelSide = ObjectSide.Left;
			break;
		}
		case 270:
		{
			Vec3Int lhs = Vec3Int.back;
			a += lhs;
			voxelSide = ObjectSide.Front;
			break;
		}
		}
		if (MonoSingleton<GroundManager>.Instance.GroundExists(a))
		{
			MonoSingleton<GroundManager>.Instance.AttachToVoxelSocket(building, voxelSide, a);
		}
		else if (map.SocketComponentManager.GetSocketComponentInstance(a) == null)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(41, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingsManagerMain.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Destroying floating building at position ");
				messageBuilder.AppendFormatted(building.GridDataPosition);
			}
			Log.Warning(messageBuilder);
			DestroyBuilding(building);
		}
	}

	public bool RoofSupportExists(Vec3Int pos, bool onlyFinished)
	{
		if (!positionInstanceListDictionary.TryGetValue(pos, out var value))
		{
			return false;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if ((!onlyFinished || item.ConstructionPhase == ConstructionPhase.Finished) && (item.Blueprint.BuildingType & (BuildingType.Wall | BuildingType.Voxel | BuildingType.Beam | BuildingType.Window | BuildingType.Door | BuildingType.BarnDoor)) != 0)
			{
				return true;
			}
		}
		Vec3Int b = Vec3Int.up;
		Vec3Int key = pos + b;
		if (positionInstanceListDictionary.TryGetValue(key, out var value2))
		{
			foreach (BaseBuildingInstance item2 in value2)
			{
				if ((!onlyFinished || item2.ConstructionPhase == ConstructionPhase.Finished) && item2.Blueprint.BuildingType == BuildingType.Floor)
				{
					return true;
				}
			}
		}
		return false;
	}

	public BaseBuildingInstance GetAlignmentBuilding(Vec3Int position)
	{
		BaseBuildingInstance buildingInstance = GetBuildingInstance(position, BuildingType.Door);
		if (buildingInstance != null && !buildingInstance.HasDisposed)
		{
			return buildingInstance;
		}
		buildingInstance = GetBuildingInstance(position, BuildingType.BarnDoor);
		if (buildingInstance != null && !buildingInstance.HasDisposed)
		{
			return buildingInstance;
		}
		buildingInstance = GetBuildingInstance(position, BuildingType.Window);
		if (buildingInstance != null && !buildingInstance.HasDisposed)
		{
			return buildingInstance;
		}
		buildingInstance = GetBuildingInstance(position, BuildingType.FenceGate);
		if (buildingInstance != null && !buildingInstance.HasDisposed)
		{
			return buildingInstance;
		}
		buildingInstance = GetBuildingInstance(position, BuildingType.Fence);
		if (buildingInstance != null && !buildingInstance.HasDisposed)
		{
			return buildingInstance;
		}
		buildingInstance = GetBuildingInstance(position, BuildingType.Merlon);
		if (buildingInstance != null && !buildingInstance.HasDisposed)
		{
			return buildingInstance;
		}
		buildingInstance = GetBuildingInstance(position, BuildingType.Wall);
		if (buildingInstance != null && !buildingInstance.HasDisposed)
		{
			return buildingInstance;
		}
		return null;
	}

	private void OnBuildingStateChanged(BaseBuildingInstance changedInstance)
	{
		Vec3Int gridDataPosition = changedInstance.GridDataPosition;
		for (int i = gridDataPosition.x - 1; i <= gridDataPosition.x + 1; i++)
		{
			for (int j = gridDataPosition.y - 1; j <= gridDataPosition.y + 1; j++)
			{
				for (int k = gridDataPosition.z - 1; k <= gridDataPosition.z + 1; k++)
				{
					Vec3Int key = new Vec3Int(i, j, k);
					if (!positionInstanceListDictionary.TryGetValue(key, out var value))
					{
						continue;
					}
					foreach (BaseBuildingInstance item in value)
					{
						if (changedInstance != item && !(Vector3.Distance(changedInstance.WorldPosition, item.WorldPosition) > changedInstance.Size.magnitude + 1.5f))
						{
							item.RefreshBuilding();
						}
					}
				}
			}
		}
	}

	private void OnGroundDestroyed(List<Vec3Int> positions)
	{
		foreach (Vec3Int position in positions)
		{
			Vec3Int a = position;
			Vec3Int b = Vec3Int.down;
			RefreshWalkableColliders(a + b);
			GroundDestroyedCheckBuilding(a);
		}
	}

	private void OnGroundDestroyedSingle(Vec3Int position)
	{
		Vec3Int b = Vec3Int.down;
		RefreshWalkableColliders(position + b);
		GroundDestroyedCheckBuilding(position);
		using PooledList<Vec3Int> pooledList = position.GetPositionsInRange(new Vec3Int(2, 1, 2));
		foreach (Vec3Int item in pooledList)
		{
			if (!positionInstanceListDictionary.TryGetValue(item, out var value))
			{
				continue;
			}
			foreach (BaseBuildingInstance item2 in value)
			{
				item2.GroundDestroyedRefreshReachability();
			}
		}
	}

	private void GroundDestroyedCheckBuilding(Vec3Int gridPos)
	{
		Vector3 b = Vector3.up;
		Vec3Int vec3Int = gridPos + b;
		if (!positionInstanceListDictionary.TryGetValue(vec3Int, out var value))
		{
			return;
		}
		BaseBuildingInstance[] array = value.ToArray();
		foreach (BaseBuildingInstance baseBuildingInstance in array)
		{
			BaseBuildingBlueprint blueprint = baseBuildingInstance.Blueprint;
			if (!blueprint.TransfersStability() && blueprint.PlacementType != PlacementType.WallSocket && blueprint.BuildingType != BuildingType.Roof && blueprint.BuildingType != BuildingType.Well && !BuildingExists(BuildingType.Floor, vec3Int) && GetWallTypeBuildingWithVerticalStability(gridPos) == null && !MonoSingleton<GroundManager>.Instance.GroundExists(gridPos))
			{
				if (baseBuildingInstance.Blueprint.SpawnStructurePileOnStabilityLoss)
				{
					SpawnBuildingPile(baseBuildingInstance);
				}
				DestroyBuilding(baseBuildingInstance);
			}
		}
	}

	public void SpawnBuildingPile(BaseBuildingInstance building)
	{
		string blueprintId = building.BlueprintId;
		Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(blueprintId);
		if (byID == null)
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(37, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingsManagerMain.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Building resource with id ");
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral(" not found.");
			}
			Log.Error(messageBuilder);
		}
		else if (!LoadingController.IsLeavingMainScene)
		{
			MonoSingleton<ResourcePileManager>.Instance.TeleportPile(new ResourceInstance(byID, 1), building.GridDataPosition);
		}
	}

	private void OnNewVoxelSaved(BaseBuildingInstance voxelBuilding)
	{
		if (voxelBuilding != null)
		{
			Vec3Int a = voxelBuilding.GridDataPosition;
			Vec3Int b = Vec3Int.down;
			RefreshWalkableColliders(a + b);
		}
	}

	private void OnAfterConstructionCompleted(BaseBuildingInstance baseBuildingInstance)
	{
		if (baseBuildingInstance != null)
		{
			Vec3Int a = baseBuildingInstance.GridDataPosition;
			Vec3Int b = Vec3Int.down;
			RefreshWalkableColliders(a + b);
		}
	}

	private void RefreshWalkableColliders(Vec3Int gridPos)
	{
		if (!positionInstanceListDictionary.TryGetValue(gridPos, out var value))
		{
			return;
		}
		foreach (BaseBuildingInstance item in value)
		{
			item.RefreshWalkableCollider();
		}
	}

	private void OnBeamPlacedUpdateStability(Vec3Int position, int stability, BaseBuildingInstance beamInstance)
	{
		for (int i = position.x - 3; i <= position.x + 3; i++)
		{
			for (int j = position.z - 3; j <= position.z + 3; j++)
			{
				for (int k = position.y - 1; k <= position.y + 1; k++)
				{
					Vec3Int key = new Vec3Int(i, k, j);
					if (!positionInstanceListDictionary.TryGetValue(key, out var value))
					{
						continue;
					}
					BaseBuildingInstance[] array = value.ToArray();
					foreach (BaseBuildingInstance baseBuildingInstance in array)
					{
						if (!baseBuildingInstance.Blueprint.TransfersStability())
						{
							continue;
						}
						ConstructionPhase constructionPhase = beamInstance.ConstructionPhase;
						if (constructionPhase == ConstructionPhase.Blueprint || constructionPhase == ConstructionPhase.Foundation)
						{
							if (baseBuildingInstance.ConstructionPhase == ConstructionPhase.Blueprint)
							{
								baseBuildingInstance.RefreshHasStabilityToBuildAndReachability();
							}
						}
						else
						{
							baseBuildingInstance.RefreshHasStabilityToBuildAndReachability();
						}
					}
				}
			}
		}
	}

	private void OnWaterLevelChanged(HashSet<int> nodeIndexes, HashSet<int> nodeNeighborsIndices)
	{
		foreach (int nodeIndex in nodeIndexes)
		{
			OnWaterLevelChanged(nodeIndex);
		}
	}

	private void OnWaterLevelChanged(int nodeIndex)
	{
		if (map == null || nodeIndex < 0 || nodeIndex >= map.GridSpaceData.Length)
		{
			return;
		}
		MapNode mapNode = map.GridSpaceData[nodeIndex];
		if (mapNode == null)
		{
			return;
		}
		Vec3Int position = mapNode.Position;
		if (!positionInstanceListDictionary.TryGetValue(position, out var value))
		{
			return;
		}
		foreach (BaseBuildingInstance item in value)
		{
			if (item != null && !item.HasDisposed)
			{
				item.WaterLevelChanged();
			}
		}
	}

	private void InitializeCachingDictionaries()
	{
		BuildingType[] buildingTypes = EnumValues.BuildingTypes;
		for (int i = 0; i < buildingTypes.Length; i++)
		{
			BuildingType key = buildingTypes[i];
			if (!key.Equals(BuildingType.Default))
			{
				TypeInstanceView.Add(key, new Dictionary<BaseBuildingInstance, BaseBuildingViewComponent>());
				typePositionListDictionary.Add(key, new Dictionary<Vec3Int, List<BaseBuildingInstance>>());
				TypePositionInstanceDictionary.Add(key, new Dictionary<Vec3Int, BaseBuildingInstance>());
			}
		}
		buildingDestroyedMeshRefreshCache.Add(BuildingType.Wall, new Queue<Vec3Int>());
		buildingDestroyedMeshRefreshCache.Add(BuildingType.Voxel, new Queue<Vec3Int>());
		buildingDestroyedMeshRefreshCache.Add(BuildingType.Floor, new Queue<Vec3Int>());
		buildingDestroyedMeshRefreshCache.Add(BuildingType.Merlon, new Queue<Vec3Int>());
		buildingDestroyedMeshRefreshCache.Add(BuildingType.Fence, new Queue<Vec3Int>());
		buildingDestroyedMeshRefreshCache.Add(BuildingType.FenceGate, new Queue<Vec3Int>());
	}

	private void InitializeCanPlace()
	{
		canPlaceDictionary.Add(BuildingType.Wall, CanPlaceWall);
		canPlaceDictionary.Add(BuildingType.Door, CanPlaceDoor);
		canPlaceDictionary.Add(BuildingType.Floor, CanPlaceFloor);
		canPlaceDictionary.Add(BuildingType.Voxel, CanPlaceWall);
		canPlaceDictionary.Add(BuildingType.Stairs, CanPlaceStairs);
		canPlaceDictionary.Add(BuildingType.Window, CanPlaceWindow);
		canPlaceDictionary.Add(BuildingType.ProductionBuilding, CanPlaceInteriorCommon);
		canPlaceDictionary.Add(BuildingType.Chair, CanPlaceInteriorCommon);
		canPlaceDictionary.Add(BuildingType.Table, CanPlaceInteriorCommon);
		canPlaceDictionary.Add(BuildingType.Bed, CanPlaceInteriorCommon);
		canPlaceDictionary.Add(BuildingType.Decoration, CanPlaceInteriorCommon);
		canPlaceDictionary.Add(BuildingType.Shrine, CanPlaceInteriorCommon);
		canPlaceDictionary.Add(BuildingType.Merlon, CanPlaceMerlon);
		canPlaceDictionary.Add(BuildingType.Trap, CanPlaceInteriorCommon);
		canPlaceDictionary.Add(BuildingType.Grave, CanPlaceGrave);
		canPlaceDictionary.Add(BuildingType.Fence, CanPlaceInteriorCommon);
		canPlaceDictionary.Add(BuildingType.BarnDoor, CanPlaceDoor);
		canPlaceDictionary.Add(BuildingType.FenceGate, CanPlaceFenceGate);
		canPlaceDictionary.Add(BuildingType.Rug, CanPlaceRug);
		canPlaceDictionary.Add(BuildingType.Ladder, CanPlaceLadder);
		canPlaceDictionary.Add(BuildingType.PenMarker, CanPlaceInteriorCommon);
		canPlaceDictionary.Add(BuildingType.OilBlob, CanPlaceOilBlob);
		canPlaceDictionary.Add(BuildingType.Well, CanPlaceWell);
		canPlaceDictionary.Add(BuildingType.SiegeWeapon, CanPlaceInteriorCommon);
	}

	public void DestroyBuildingStabilityZero(int x, int y, int z)
	{
		Vec3Int key = new Vec3Int(x, y, z);
		if (!positionInstanceListDictionary.TryGetValue(key, out var value))
		{
			return;
		}
		BaseBuildingInstance[] array = value.ToArray();
		foreach (BaseBuildingInstance baseBuildingInstance in array)
		{
			if (baseBuildingInstance.Blueprint.BuildingType != BuildingType.Beam && baseBuildingInstance.Blueprint.BuildingType != BuildingType.Roof && (baseBuildingInstance.Blueprint.TransfersStabilityIncludeBeams() || baseBuildingInstance.Blueprint.BuildingType == BuildingType.Ladder))
			{
				villageSaveData.DestructionQueue.Enqueue(baseBuildingInstance);
			}
		}
	}

	public void SetStability(int x, int y, int z, int stability)
	{
		Vec3Int vec3Int = new Vec3Int(x, y, z);
		if (!positionInstanceListDictionary.TryGetValue(vec3Int, out var value))
		{
			return;
		}
		BaseBuildingInstance[] array = value.ToArray();
		foreach (BaseBuildingInstance baseBuildingInstance in array)
		{
			if (baseBuildingInstance.Blueprint.BuildingType != BuildingType.Beam)
			{
				baseBuildingInstance.SetStability(stability);
				baseBuildingInstance.HasStabilityToBuild = HasStabilityToBuild(baseBuildingInstance);
			}
		}
		OnUpdateStability(vec3Int, stability);
	}

	private void OnUpdateStability(Vec3Int position, int stability)
	{
		for (int i = position.x - 3; i <= position.x + 3; i++)
		{
			for (int j = position.z - 3; j <= position.z + 3; j++)
			{
				for (int k = position.y - 1; k <= position.y + 1; k++)
				{
					Vec3Int lhs = new Vec3Int(i, k, j);
					if (positionInstanceListDictionary.ContainsKey(lhs) && !(lhs == position))
					{
						BaseBuildingInstance[] array = positionInstanceListDictionary[lhs].ToArray();
						for (int l = 0; l < array.Length; l++)
						{
							array[l].RefreshHasStabilityToBuildAndReachability();
						}
					}
				}
			}
		}
	}

	private void OnTick(float deltaTime)
	{
		using (ProfilerSampleJanitor.Begin("BuildingsManagerMain.Tick"))
		{
			if (villageSaveData.DestructionQueue.Count == 0)
			{
				return;
			}
			frameCounter++;
			if (frameCounter != 6)
			{
				return;
			}
			frameCounter = 0;
			while (villageSaveData.DestructionQueue.Count > 0)
			{
				buildingsCounter++;
				if (buildingsCounter == 10)
				{
					buildingsCounter = 0;
					break;
				}
				villageSaveData.DestructionQueue.Dequeue().DestroyBuildingStabilityZero(replaced: false, skipStabilityCheck: true);
			}
			buildingsCounter = 0;
		}
	}

	private void OnLateTick(float deltaTime)
	{
		ConstructionJobManager.OnLateTick();
	}

	public void WorldStateChangedRefreshBuildings()
	{
		if (refreshBlueprintsWorldStateChanged != null)
		{
			MonoSingleton<TaskController>.Instance.OptimizedCall(this, "WorldStateChangedRefreshBuildings", delegate
			{
				MonoSingleton<TaskController>.Instance.WaitUntil((float time) => refreshBlueprintsWorldStateChanged == null).Then(delegate
				{
					HumanoidInstance[] workers2 = MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys.ToArray();
					refreshBlueprintsWorldStateChanged = CalculateBuildingInfo(GetBlueprints(), workers2, GetWorkerAreas(workers2), delegate
					{
						refreshBlueprintsWorldStateChanged = null;
					});
				});
			});
			return;
		}
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "WorldStateChangedRefreshBuildings", delegate
		{
			HumanoidInstance[] workers = MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys.ToArray();
			refreshBlueprintsWorldStateChanged = CalculateBuildingInfo(GetBlueprints(), workers, GetWorkerAreas(workers), delegate
			{
				refreshBlueprintsWorldStateChanged = null;
			});
		});
	}

	public void ResourceDeliveredRefreshBlueprint(BaseBuildingInstance targetBlueprint)
	{
		if (refreshBlueprintsWorldStateChanged != null)
		{
			MonoSingleton<TaskController>.Instance.OptimizedCall(this, "WorldStateChangedRefreshBuildings", delegate
			{
				MonoSingleton<TaskController>.Instance.WaitUntil((float time) => refreshBlueprintsWorldStateChanged == null).Then(delegate
				{
					HumanoidInstance[] workers2 = MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys.ToArray();
					refreshBlueprintsWorldStateChanged = CalculateBuildingInfo(new List<BaseBuildingInstance> { targetBlueprint }, workers2, GetWorkerAreas(workers2), delegate
					{
						refreshBlueprintsWorldStateChanged = null;
					});
				});
			});
			return;
		}
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "WorldStateChangedRefreshBuildings", delegate
		{
			HumanoidInstance[] workers = MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys.ToArray();
			refreshBlueprintsWorldStateChanged = CalculateBuildingInfo(new List<BaseBuildingInstance> { targetBlueprint }, workers, GetWorkerAreas(workers), delegate
			{
				refreshBlueprintsWorldStateChanged = null;
			});
		});
	}

	public void ResourceChangedRefreshBlueprints(Resource resource)
	{
		List<BaseBuildingInstance> buildingBlueprints = GetBlueprints();
		bool flag = true;
		for (int num = buildingBlueprints.Count - 1; num >= 0; num--)
		{
			if (num < buildingBlueprints.Count && buildingBlueprints[num].ConstructionCost.ContainsKey(resource.GetID()))
			{
				flag = false;
				break;
			}
		}
		if (flag)
		{
			return;
		}
		refreshForResources.Add(resource);
		if (refreshBlueprintsResourceChanged != null)
		{
			MonoSingleton<TaskController>.Instance.WaitUntil((float time) => refreshBlueprintsResourceChanged == null).Then(delegate
			{
				List<BaseBuildingInstance> list = new List<BaseBuildingInstance>();
				foreach (BaseBuildingInstance blueprint in GetBlueprints())
				{
					foreach (Resource refreshForResource in refreshForResources)
					{
						if (blueprint.ConstructionCost.ContainsKey(refreshForResource.GetID()))
						{
							list.Add(blueprint);
							break;
						}
					}
				}
				refreshForResources.Clear();
				IEnumerable<HumanoidInstance> keys2 = MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys;
				refreshBlueprintsResourceChanged = CalculateBuildingInfo(list, keys2, GetWorkerAreas(keys2), delegate
				{
					refreshBlueprintsResourceChanged = null;
				});
			});
			return;
		}
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "ResourceChangesForbidAllow", delegate
		{
			IEnumerable<HumanoidInstance> keys = MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys;
			refreshBlueprintsResourceChanged = CalculateBuildingInfo(buildingBlueprints, keys, GetWorkerAreas(keys), delegate
			{
				refreshBlueprintsResourceChanged = null;
			});
		});
	}

	public void RefreshPlacedBlueprints(IEnumerable<BaseBuildingInstance> buildingBlueprints, IEnumerable<HumanoidInstance> workers)
	{
		placedBlueprints.UnionWith(buildingBlueprints);
		if (refreshBlueprintsBuildingPlaced != null)
		{
			MonoSingleton<TaskController>.Instance.WaitUntil((float time) => refreshBlueprintsBuildingPlaced == null).Then(delegate
			{
				List<BaseBuildingInstance> buildingBlueprints3 = placedBlueprints.ToList();
				placedBlueprints.Clear();
				refreshBlueprintsBuildingPlaced = CalculateBuildingInfo(buildingBlueprints3, workers, GetWorkerAreas(workers), delegate
				{
					refreshBlueprintsBuildingPlaced = null;
				});
			});
		}
		else
		{
			List<BaseBuildingInstance> buildingBlueprints2 = placedBlueprints.ToList();
			refreshBlueprintsBuildingPlaced = CalculateBuildingInfo(buildingBlueprints2, workers, GetWorkerAreas(workers), delegate
			{
				refreshBlueprintsBuildingPlaced = null;
			});
		}
	}

	private HashSet<uint> GetWorkerAreas(IEnumerable<HumanoidInstance> workers)
	{
		HashSet<uint> hashSet = new HashSet<uint>();
		foreach (HumanoidInstance worker in workers)
		{
			if (!CombatUtils.IsNullOrDisposed(worker))
			{
				uint num = worker.GetNode()?.Area ?? 0;
				if (num != 0)
				{
					hashSet.Add(num);
				}
			}
		}
		return hashSet;
	}

	private List<BaseBuildingInstance> GetBlueprints()
	{
		GridDataType dataType = GridDataType.BuildingBlueprint | GridDataType.OthersBlueprint | GridDataType.BeamBlueprint | GridDataType.SocketableBlueprint;
		if (!MonoSingleton<VillageManager>.IsInstantiated())
		{
			Debug.LogWarning("VillageManager is not instantiated.");
			return null;
		}
		return map.GetWorldObjectsList<BaseBuildingInstance>(dataType);
	}

	private ThreadingJobSystem.ThreadedTaskData CalculateBuildingInfo(IList<BaseBuildingInstance> buildingBlueprints, IEnumerable<HumanoidInstance> workers, HashSet<uint> workerAreas, Action callback)
	{
		WalkableModel workerWalkableModel = Repository<WorkerBaseRepository, Worker>.Instance.BaseWorker.DefaultHumanType.WalkableModelFriendly;
		return MonoSingleton<ThreadingJobSystem>.Instance.QueueTask(delegate
		{
			using PooledHashSet<uint> pooledHashSet = HashSetPool<uint>.GetJanitor();
			for (int num = buildingBlueprints.Count; num >= 0; num--)
			{
				if (num < buildingBlueprints.Count)
				{
					BaseBuildingInstance building = buildingBlueprints[num];
					if (!building.HasDisposed)
					{
						SpinWait.SpinUntil(() => !building.IsReachabilityUpdateInProgress, 1000);
						bool flag = false;
						bool flag2 = false;
						StringIntDictionary materials = building.Blueprint.Materials;
						ResourceRepository instance = Repository<ResourceRepository, Resource>.Instance;
						ResourcePileTracker instance2 = MonoSingleton<ResourcePileTracker>.Instance;
						ResourcePileManager instance3 = MonoSingleton<ResourcePileManager>.Instance;
						foreach (uint workerArea in workerAreas)
						{
							if (PathfinderUtil.IsPathPossible(workerWalkableModel, workerArea, building))
							{
								flag = true;
								if (building.IsMoveBlueprint)
								{
									if (building.MovableBuildingPileInstance != null)
									{
										flag2 = PathfinderUtil.IsPathPossible(workerWalkableModel, workerArea, building.MovableBuildingPileInstance) && !building.MovableBuildingPileInstance.IsForbidden;
									}
									else
									{
										BaseBuildingInstance sourceBuilding = MonoSingleton<MoveBuildingsManager>.Instance.GetSourceBuilding(building);
										if (sourceBuilding != null)
										{
											flag2 = PathfinderUtil.IsPathPossible(workerWalkableModel, workerArea, sourceBuilding);
										}
									}
								}
								else if (materials.Dictionary.Count == 0)
								{
									flag2 = true;
								}
								else
								{
									foreach (string key in materials.Dictionary.Keys)
									{
										Resource byID = instance.GetByID(key);
										ResourcePileCount count = instance2.GetCount(byID);
										int requiredAmount = building.GetRequiredAmount(byID);
										if (count.AllowedCount < requiredAmount)
										{
											flag2 = false;
											break;
										}
										ISet<uint> pilesAreaIDs = pooledHashSet;
										instance3.GetAllowedPilesAreaIDs(byID, in pilesAreaIDs);
										foreach (uint item in pooledHashSet)
										{
											if (PathfinderUtil.IsPathPossible(workerWalkableModel, workerArea, item, building.Village.Map))
											{
												flag2 = true;
												break;
											}
										}
									}
								}
								if (flag2)
								{
									break;
								}
							}
						}
						if (!building.HasDisposed)
						{
							if (!flag && !flag2)
							{
								if (building.IsMoveBlueprint)
								{
									if (building.MovableBuildingPileInstance != null)
									{
										uint num2 = building.MovableBuildingPileInstance.GetNode()?.Area ?? 0;
										flag2 = !building.MovableBuildingPileInstance.IsForbidden && num2 != 0 && PathfinderUtil.IsPathPossible(workerWalkableModel, num2, building);
									}
									else
									{
										BaseBuildingInstance sourceBuilding2 = MonoSingleton<MoveBuildingsManager>.Instance.GetSourceBuilding(building);
										uint num3 = building.GetNode()?.Area ?? 0;
										if (sourceBuilding2 != null)
										{
											flag2 = num3 != 0 && PathfinderUtil.IsPathPossible(workerWalkableModel, num3, sourceBuilding2);
										}
									}
								}
								else if (materials.Dictionary.Count == 0)
								{
									flag2 = true;
								}
								else
								{
									foreach (string key2 in materials.Dictionary.Keys)
									{
										Resource byID2 = instance.GetByID(key2);
										ResourcePileCount count2 = instance2.GetCount(byID2);
										int requiredAmount2 = building.GetRequiredAmount(byID2);
										if (count2.AllowedCount < requiredAmount2)
										{
											flag2 = false;
											break;
										}
										ISet<uint> pilesAreaIDs = pooledHashSet;
										instance3.GetAllowedPilesAreaIDs(byID2, in pilesAreaIDs);
										foreach (uint item2 in pooledHashSet)
										{
											if (PathfinderUtil.IsPathPossible(workerWalkableModel, item2, building))
											{
												flag2 = true;
												break;
											}
										}
									}
								}
							}
							building.Reachable = flag;
							building.ResourcesAvailable = flag2;
						}
					}
				}
			}
			return true;
		}, delegate
		{
			callback?.Invoke();
			MonoSingleton<BuildingsManagerCommon>.Instance.StartResourceChangedRefreshBlueprintsCoroutine(buildingBlueprints, workers);
		});
	}

	public void WorldStateChangedRefreshBuilding(BaseBuildingInstance buildableObject)
	{
		CheckBlueprintStatus(buildableObject, MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys.ToArray());
	}

	public void CheckBlueprintStatus(BaseBuildingInstance buildableObject, IEnumerable<HumanoidInstance> workers)
	{
		if (buildableObject == null || buildableObject.HasDisposed || !TypeInstanceView.TryGetValue(buildableObject.BuildingType, out var value))
		{
			return;
		}
		value.TryGetValue(buildableObject, out var value2);
		if (value2 == null)
		{
			return;
		}
		bool flag = false;
		int minBuildSkillRequired = buildableObject.Blueprint.MinBuildSkillRequired;
		if (buildableObject.IsMoveBlueprint)
		{
			flag = true;
		}
		else
		{
			foreach (HumanoidInstance worker in workers)
			{
				if (worker.GetSkillLevel(SkillType.Construction) >= minBuildSkillRequired)
				{
					flag = true;
					break;
				}
			}
		}
		bool resourcesAvailable = buildableObject.ResourcesAvailable;
		buildableObject.SkilledConstructionWorkerExists = flag;
		bool flag2 = !resourcesAvailable || !flag || !buildableObject.Reachable;
		value2.ColorBlueprint(flag2 ? 0f : 2f);
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
