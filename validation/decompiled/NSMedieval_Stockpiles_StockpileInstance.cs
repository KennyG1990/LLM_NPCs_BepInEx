using System;
using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix;
using NSEipix.Base;
using NSEipix.Model;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.Components;
using NSMedieval.Enums;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Resources;
using NSMedieval.Serialization;
using NSMedieval.State;
using NSMedieval.StorageUniversal;
using NSMedieval.Types;
using NSMedieval.UI.Utils;
using NSMedieval.View;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using NSMedieval.Water;
using UnityEngine;

namespace NSMedieval.Stockpiles;

[Serializable]
[FVSerializableKey("StockpileInstance", "")]
public class StockpileInstance : WorldObject, IStorage, IGameDisposable, IDisposable, IGoapTargetable
{
	[SerializeField]
	private string stockpileName;

	[SerializeField]
	private BuildingSubCategoryUI buildingSubcategoryUI;

	[SerializeField]
	private Vec3Int start;

	[SerializeField]
	private Vec3Int end;

	[SerializeField]
	private StockpileSpaceDataDictionary grid;

	[SerializeField]
	private ResourcesFilter resourcesFilter;

	[SerializeField]
	private ZonePriority priority;

	[SerializeField]
	private bool canBeUsedInProduction = true;

	[NonSerialized]
	private bool blockReachabilityUpdate;

	private bool underWater;

	private List<Vec3Int> worldPositions;

	private bool refreshWorldPositions;

	private VillageMap villageMapCache;

	private object reservationLock = new object();

	public override bool BlueprintExists => Blueprint != null;

	public override ushort PathfindingPenalty => 2000;

	public List<ResourceGroups> DefaultResourceGroups => Repository<ResourceGroupsRepository, ResourceGroupsModel>.Instance.GetByID("all_resource_groups").ResourceGroups;

	public ZonePriority Priority => priority;

	public bool AnimalFeeder => false;

	public bool PrisonFeeder => false;

	public Stockpile Blueprint => Repository<StockpileRepository, Stockpile>.Instance.GetByID(blueprintId);

	public string StorageName => stockpileName;

	public float RefillPercentageThreshold => 0f;

	public override List<Vec3Int> Positions
	{
		get
		{
			if (grid == null)
			{
				return null;
			}
			if (worldPositions != null && !refreshWorldPositions)
			{
				return worldPositions;
			}
			worldPositions = Grid.Keys.ToList();
			return worldPositions;
		}
	}

	public string ObjectId => blueprintId;

	public BuildingSubCategoryUI BuildingSubcategoryUI => buildingSubcategoryUI;

	public Dictionary<Vec3Int, StockpileSpaceData> Grid => grid?.Dictionary;

	public ResourcesFilter ResourcesFilter => resourcesFilter;

	public Vec3Int Start
	{
		get
		{
			return start;
		}
		set
		{
			start = value;
		}
	}

	public Vec3Int End
	{
		get
		{
			return end;
		}
		set
		{
			end = value;
		}
	}

	public bool Underwater
	{
		get
		{
			return underWater;
		}
		set
		{
			underWater = value;
		}
	}

	public int StoredResourcesCount => Grid?.Values.Count((StockpileSpaceData item) => item.Pile != null) ?? 0;

	public bool IsPlayerOwned => OwnedByPlayer();

	private VillageMap VillageMapCached => villageMapCache;

	public bool CanBeUsedInProduction => canBeUsedInProduction;

	public event Action OnPileAddedToGridEvent;

	public StockpileInstance(Stockpile model, VillageMap map, int stockpileNumber, Vector3 worldPosition, Vec3Int[,] stockpileGridSpaces, Vec3Int start, Vec3Int end)
		: base(WorldObjectType.Stockpile, worldPosition, Vec3Int.one)
	{
		villageMapCache = map;
		blueprintId = model.GetID();
		stockpileName = $"{BuildingUtils.GetLocalizedName(model.GetID())} ({stockpileNumber})";
		buildingSubcategoryUI = model.BuildingSubCategoryUI;
		this.start = start;
		this.end = end;
		grid = new StockpileSpaceDataDictionary();
		resourcesFilter = new ResourcesFilter();
		resourcesFilter.OnParamsChangedEvent += OnResourceFilterParametersChanged;
		priority = model.ZonePriority;
		InitAllowedResourcesFromBlueprint();
		InitGridSpaces(stockpileGridSpaces);
		CalculateReachability();
		MonoSingleton<ResourcePileController>.Instance.PreSpawnPileEvent += OnPilePreSpawn;
		MonoSingleton<ResourcePileController>.Instance.DestroyPileEvent += OnPileDestroy;
		underWater = UnderWater();
	}

	public void SetMap(VillageMap map)
	{
		villageMapCache = map;
	}

	public override string ToString()
	{
		return $"'Stockpile:{base.GridDataPosition} Priority: {Priority} Disposed:{base.HasDisposed}'";
	}

	public void WaterLevelChanged()
	{
		underWater = UnderWater();
	}

	public void PasteStorageSettings(IStorage original)
	{
		canBeUsedInProduction = original.CanBeUsedInProduction;
		if (original is StockpileInstance)
		{
			resourcesFilter.SetAllowedResourceTypes(original.ResourcesFilter.AllowedResourceTypes);
			resourcesFilter.SetQuality(original.ResourcesFilter.Quality);
			resourcesFilter.SetHitPointsPercent(original.ResourcesFilter.HitPointsPercent);
			priority = original.Priority;
		}
		else if (original is ShelfComponentInstance shelfComponentInstance)
		{
			PasteResourceFilter(original, shelfComponentInstance.AllStorage);
		}
	}

	private void PasteResourceFilter(IStorage original, List<UniversalStorage> originalStorages)
	{
		HashSet<Resource> hashSet = new HashSet<Resource>();
		this.resourcesFilter.SetAllowedResourceTypes(hashSet);
		foreach (UniversalStorage originalStorage in originalStorages)
		{
			ResourcesFilter resourcesFilter = originalStorage.ResourcesFilter;
			hashSet.UnionWith(resourcesFilter.AllowedResourceTypes);
		}
		this.resourcesFilter.SetQuality(original.ResourcesFilter.Quality);
		this.resourcesFilter.SetHitPointsPercent(original.ResourcesFilter.HitPointsPercent);
		priority = original.Priority;
		foreach (Resource item in hashSet)
		{
			this.resourcesFilter.AddAllowedResource(item);
		}
	}

	public bool ContainsPile(ResourcePileInstance pile)
	{
		if (pile == null)
		{
			return false;
		}
		Vec3Int key = pile.GridDataPosition;
		if (!Grid.TryGetValue(key, out var value))
		{
			return false;
		}
		return value.Pile == pile;
	}

	public bool ReserveStorage(ResourceInstance resource, CreatureBase agent, out SimpleResourceCount storedAmount, out Vec3Int position)
	{
		StockpileSpaceData stockpileSpaceData = Grid.Values.FirstOrDefault(delegate(StockpileSpaceData item)
		{
			if (item == null || !item.HasAnyReservations())
			{
				return CanStore(resource, item.Position);
			}
			StockpileReservationInfo reservationInfo = item.GetReservationInfo(agent);
			if (reservationInfo.Agent != null && resource.Blueprint != reservationInfo.Blueprint)
			{
				return false;
			}
			if (item.ReservationInfos.First().Blueprint != resource.Blueprint)
			{
				return false;
			}
			if (!CanStore(resource, item.Position))
			{
				return false;
			}
			if (item.Pile == null)
			{
				return item.GetTotalReservedResourceCount() < resource.Blueprint.StackingLimit;
			}
			ResourceInstance storedResource = item.Pile.GetStoredResource();
			return storedResource.Amount + item.GetTotalReservedResourceCount() < storedResource.Blueprint.StackingLimit;
		});
		if (stockpileSpaceData == null)
		{
			storedAmount = default(SimpleResourceCount);
			position = default(Vec3Int);
			return false;
		}
		position = stockpileSpaceData.Position;
		ResourcePileInstance resourcePileGridPosition = GetResourcePileGridPosition(position);
		storedAmount = new SimpleResourceCount(resource.Blueprint, resource.Amount);
		if (resourcePileGridPosition == null && !stockpileSpaceData.HasAnyReservations())
		{
			lock (reservationLock)
			{
				stockpileSpaceData.Reserve(new StockpileReservationInfo(storedAmount, agent));
			}
			return true;
		}
		int num = stockpileSpaceData.GetTotalReservedResourceCount() + resource.Amount;
		if (resourcePileGridPosition != null)
		{
			num += resourcePileGridPosition.GetStoredResource().Amount;
		}
		int num2 = num - resource.Blueprint.StackingLimit;
		if (num2 > 0)
		{
			storedAmount = new SimpleResourceCount(resource.Blueprint, resource.Amount - num2);
		}
		else
		{
			num2 = 0;
		}
		lock (reservationLock)
		{
			stockpileSpaceData.Reserve(new StockpileReservationInfo(new SimpleResourceCount(resource.Blueprint, resource.Amount - num2), agent));
		}
		return true;
	}

	public void ReleaseReservations(CreatureBase agent)
	{
		lock (reservationLock)
		{
			if (Grid == null)
			{
				return;
			}
			foreach (KeyValuePair<Vec3Int, StockpileSpaceData> item in Grid)
			{
				item.Value?.ReleaseReservation(agent);
			}
		}
	}

	public List<ResourcePileInstance> GetStoredPiles()
	{
		List<ResourcePileInstance> list = new List<ResourcePileInstance>();
		foreach (Vec3Int key in Grid.Keys)
		{
			if (Grid[key].Pile != null)
			{
				list.Add(Grid[key].Pile);
			}
		}
		return list;
	}

	public void SetCanBeUsedInProduction(bool allowed)
	{
		canBeUsedInProduction = allowed;
	}

	public void SetHitPointsPercent(IntRange range)
	{
		resourcesFilter.SetHitPointsPercent(range);
	}

	public void SetQuality(IntRange range)
	{
		resourcesFilter.SetQuality(range);
	}

	public bool IsBlueprintAllowed(Resource blueprint)
	{
		return resourcesFilter.IsBlueprintAllowed(blueprint);
	}

	public void SetName(string name)
	{
		stockpileName = name;
	}

	public void SetPriority(ZonePriority priority)
	{
		ZonePriority oldPriority = this.priority;
		this.priority = priority;
		MonoSingleton<StorageCommonManager>.Instance.OnPriorityChanged(this, oldPriority);
	}

	public ResourcePileInstance GetResourcePileGridPosition(Vec3Int gridPosition)
	{
		if (!grid.Dictionary.TryGetValue(gridPosition, out var value))
		{
			return null;
		}
		return value?.Pile;
	}

	public bool TryStore(ResourceInstance resourceInstance)
	{
		bool isEnabled;
		foreach (Vec3Int position in Positions)
		{
			ResourcePileInstance resourcePileGridPosition = GetResourcePileGridPosition(position);
			if (resourcePileGridPosition == null || resourcePileGridPosition.Blueprint != resourceInstance.Blueprint)
			{
				continue;
			}
			Storage storage = resourcePileGridPosition.GetStorage();
			if (storage.GetFreeSpace() > 0f)
			{
				int t = storage.Transfer(resourceInstance);
				FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(51, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Stockpile\\StockpileInstance.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Stored ");
					messageBuilder.AppendFormatted(t);
					messageBuilder.AppendLiteral(" of '");
					messageBuilder.AppendFormatted(resourceInstance.BlueprintId);
					messageBuilder.AppendLiteral("' into existing pile on a stockpile at ");
					messageBuilder.AppendFormatted(position);
				}
				Log.Debug(messageBuilder);
				if (resourceInstance.Amount <= 0)
				{
					isEnabled = true;
					return isEnabled;
				}
			}
		}
		foreach (Vec3Int position2 in Positions)
		{
			if (GetResourcePileGridPosition(position2) == null)
			{
				int num = Math.Min(resourceInstance.Amount, resourceInstance.Blueprint.StackingLimit);
				MonoSingleton<ResourcePileManager>.Instance.SpawnPile(resourceInstance.Clone(num), position2.ToVector3World());
				resourceInstance.Sub(num);
				bool isEnabled2;
				FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(48, 3, out isEnabled2, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Stockpile\\StockpileInstance.cs");
				if (isEnabled2)
				{
					messageBuilder.AppendLiteral("Stored ");
					messageBuilder.AppendFormatted(num);
					messageBuilder.AppendLiteral(" of '");
					messageBuilder.AppendFormatted(resourceInstance.BlueprintId);
					messageBuilder.AppendLiteral("' into a new pile on a stockpile at ");
					messageBuilder.AppendFormatted(position2);
				}
				Log.Debug(messageBuilder);
				if (resourceInstance.Amount <= 0)
				{
					isEnabled = true;
					return isEnabled;
				}
			}
		}
		return false;
	}

	public void SetupAfterLoading()
	{
		reservationLock = new object();
		if (grid == null)
		{
			grid = new StockpileSpaceDataDictionary();
			Log.Error("Stockpile grid is null! This should never happen", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Stockpile\\StockpileInstance.cs");
		}
		blockReachabilityUpdate = true;
		SetupWorldObject(base.WorldPosition);
		blockReachabilityUpdate = false;
		resourcesFilter.OnParamsChangedEvent += OnResourceFilterParametersChanged;
		SetupDefaultAllowedByBlueprint();
		MonoSingleton<ResourcePileController>.Instance.PreSpawnPileEvent += OnPilePreSpawn;
		MonoSingleton<ResourcePileController>.Instance.DestroyPileEvent += OnPileDestroy;
		MonoSingleton<ResourcePileController>.Instance.AllSavedPilesSpawnedEvent += OnResourceFilterParametersChanged;
		underWater = UnderWater();
	}

	public override void Dispose()
	{
		if (resourcesFilter != null)
		{
			resourcesFilter.OnParamsChangedEvent -= OnResourceFilterParametersChanged;
		}
		if (MonoSingleton<ResourcePileController>.IsInstantiated())
		{
			MonoSingleton<ResourcePileController>.Instance.PreSpawnPileEvent -= OnPilePreSpawn;
			MonoSingleton<ResourcePileController>.Instance.DestroyPileEvent -= OnPileDestroy;
			MonoSingleton<ResourcePileController>.Instance.AllSavedPilesSpawnedEvent -= OnResourceFilterParametersChanged;
		}
		if (MonoSingleton<StockpileManager>.IsInstantiated())
		{
			MonoSingleton<StockpileManager>.Instance.Destroy(this);
		}
		foreach (StockpileSpaceData value in Grid.Values)
		{
			RemovePileFromGrid(value);
		}
		base.Dispose();
		if (MonoSingleton<ResourcePileTracker>.IsInstantiated())
		{
			MonoSingleton<ResourcePileTracker>.Instance.ScheduleRecountPiles();
		}
		this.OnPileAddedToGridEvent = null;
		resourcesFilter = null;
		grid?.Dictionary.Clear();
		grid = null;
		villageMapCache = null;
	}

	public override SelectableObject GetView()
	{
		return MonoSingleton<StockpileManager>.Instance.GetView(this);
	}

	public bool ContainsGridPosition(Vec3Int worldPos)
	{
		return Grid?.ContainsKey(worldPos) ?? false;
	}

	public bool CanStore(ResourceInstance resource, CreatureBase creatureBase = null)
	{
		if (base.HasDisposed || resource == null || Grid == null)
		{
			return false;
		}
		if (!resourcesFilter.IsValid(resource))
		{
			return false;
		}
		foreach (KeyValuePair<Vec3Int, StockpileSpaceData> item in Grid)
		{
			StockpileSpaceData value = item.Value;
			if (value == null || !value.HasAnyReservations())
			{
				if (CanStore(resource, item.Key, skipFilter: true))
				{
					return true;
				}
				continue;
			}
			if (value.GetReservationInfo(creatureBase).Agent != null)
			{
				if (CanStore(resource, item.Key, skipFilter: true))
				{
					return true;
				}
				continue;
			}
			Resource blueprint;
			int num;
			if (value.Pile == null)
			{
				blueprint = value.ReservationInfos.First().Blueprint;
				num = value.GetTotalReservedResourceCount();
			}
			else
			{
				blueprint = value.Pile.Blueprint;
				num = (value.Pile.GetStoredResource()?.Amount ?? 0) + value.GetTotalReservedResourceCount();
			}
			if (num < blueprint.StackingLimit && CanStore(resource, item.Key, skipFilter: true))
			{
				return true;
			}
		}
		return false;
	}

	public void Carve(Vec3Int gridPosition)
	{
		if (Grid != null && Grid.TryGetValue(gridPosition, out var value))
		{
			RemovePileFromGrid(value);
			Grid.Remove(gridPosition);
			refreshWorldPositions = true;
		}
	}

	public void AllowResource(Resource resource, bool allowed)
	{
		if (allowed)
		{
			resourcesFilter.AddAllowedResource(resource);
		}
		else
		{
			resourcesFilter.RemoveAllowedResource(resource);
		}
	}

	public bool IsExpandValid(Vec3Int[,] newStockpileGridSpaces)
	{
		List<Vec3Int> list = new List<Vec3Int>();
		for (int i = 0; i < newStockpileGridSpaces.GetLength(0); i++)
		{
			for (int j = 0; j < newStockpileGridSpaces.GetLength(1); j++)
			{
				Vec3Int item = newStockpileGridSpaces[i, j];
				if (!item.Equals(Vec3Int.zero))
				{
					list.Add(item);
				}
			}
		}
		foreach (Vec3Int key in Grid.Keys)
		{
			if (list.Contains(key))
			{
				return true;
			}
		}
		return false;
	}

	public void RefreshGridSpaces(Vec3Int[,] newStockpileGridSpaces)
	{
		Dictionary<Vec3Int, ResourcePileInstance> pilesByGridPosOnGround = MonoSingleton<ResourcePileManager>.Instance.PilesByGridPosOnGround;
		List<Vec3Int> newPositions = new List<Vec3Int>();
		for (int i = 0; i < newStockpileGridSpaces.GetLength(0); i++)
		{
			for (int j = 0; j < newStockpileGridSpaces.GetLength(1); j++)
			{
				Vec3Int vec3Int = newStockpileGridSpaces[i, j];
				if (vec3Int.Equals(Vec3Int.zero))
				{
					continue;
				}
				newPositions.Add(vec3Int);
				if (!Grid.ContainsKey(vec3Int))
				{
					Grid.Add(vec3Int, new StockpileSpaceData(vec3Int));
					refreshWorldPositions = true;
					if (pilesByGridPosOnGround.ContainsKey(vec3Int))
					{
						AddPileToGrid(pilesByGridPosOnGround[vec3Int]);
					}
				}
			}
		}
		foreach (Vec3Int item in Grid.Keys.Where((Vec3Int item) => !newPositions.Contains(item)).ToList())
		{
			if (Grid.ContainsKey(item))
			{
				Carve(item);
			}
			refreshWorldPositions = true;
		}
		VillageManager.ActiveVillage.Map.OnWorldObjectSizeChanged(this);
		if (MonoSingleton<ResourcePileTracker>.IsInstantiated())
		{
			MonoSingleton<ResourcePileTracker>.Instance.ScheduleRecountPiles();
			MonoSingleton<ResourcePileHaulingManager>.Instance.TriggerLazyReProcessAll();
		}
		underWater = UnderWater();
	}

	protected override List<Vec3Int> GatherReachabilityNodePositions()
	{
		if (Positions == null || blockReachabilityUpdate)
		{
			return base.GatherReachabilityNodePositions();
		}
		List<Vec3Int> list = new List<Vec3Int>();
		for (int i = 0; i < Positions.Count; i++)
		{
			Vec3Int item = Positions[i];
			MapNode node = base.Map.GetNode(item.x, item.y, item.z);
			if (node != null && node.IsWalkable)
			{
				if (list.Count > 10)
				{
					break;
				}
				list.Add(item);
			}
		}
		return list;
	}

	public override float GetBeautyInput()
	{
		return 0f;
	}

	private void AddPileToGrid(ResourcePileInstance pile)
	{
		if (Grid != null && pile != null && !pile.IsPlacedOnStorageBuilding && resourcesFilter.IsValid(pile.GetStoredResource()))
		{
			if (Grid.ContainsKey(pile.GridDataPosition))
			{
				Grid[pile.GridDataPosition].SetPile(pile);
			}
			else
			{
				Grid.Add(pile.GridDataPosition, new StockpileSpaceData(pile.GridDataPosition, pile));
			}
			pile.SetPlacedOnStorage(this);
			pile.SetIsStoredOnStockpile(value: true, this);
			this.OnPileAddedToGridEvent?.Invoke();
		}
	}

	private void RemovePileFromGrid(StockpileSpaceData data)
	{
		if (data.Pile != null && !data.Pile.HasDisposed)
		{
			data.Pile.SetPlacedOnStorage(null);
			data.Pile.SetIsStoredOnStockpile(value: false, this);
			MonoSingleton<ResourcePileHaulingManager>.Instance.QueueForReProcess(data.Pile);
		}
		data.SetPile(null);
	}

	private bool CanStore(ResourceInstance resource, Vec3Int gridSpace, bool skipFilter = false)
	{
		if (resource.Blueprint == null)
		{
			return true;
		}
		if (!skipFilter && (!resourcesFilter.IsValid(resource) || Grid == null))
		{
			return false;
		}
		MapNode node = VillageMapCached.GetNode(in gridSpace);
		if (!node.IsWalkable)
		{
			return false;
		}
		if (node.GetWorldObject(GridDataType.Stockpile) != this)
		{
			return false;
		}
		GridDataType dataType = node.DataType;
		if ((dataType & GridDataType.PlantMapResource) != 0)
		{
			return false;
		}
		List<WorldObject> worldObjects = node.WorldObjects;
		ResourcePileInstance resourcePileInstance = null;
		for (int i = 0; i < worldObjects.Count; i++)
		{
			if ((worldObjects[i].GridDataType & GridDataType.ResourcePile) != 0 && worldObjects[i] is ResourcePileInstance { InstanceStorage: null } resourcePileInstance2)
			{
				resourcePileInstance = resourcePileInstance2;
				break;
			}
		}
		ResourceInstance resourceInstance = resourcePileInstance?.GetStoredResource();
		if (resourceInstance == null)
		{
			if ((dataType & GridDataType.BuildingFinished) != 0)
			{
				BaseBuildingInstance obj = node.GetWorldObject(GridDataType.BuildingFinished) as BaseBuildingInstance;
				if (obj == null)
				{
					return false;
				}
				return obj.BuildingType == BuildingType.Floor;
			}
			return true;
		}
		if (resourcePileInstance.IsForbidden)
		{
			return false;
		}
		if (!resourceInstance.BlueprintId.Equals(resource.BlueprintId))
		{
			return false;
		}
		if (resourceInstance.Amount < resource.Blueprint.StackingLimit)
		{
			return true;
		}
		return false;
	}

	private void OnPilePreSpawn(ResourcePileInstance pile)
	{
		if (pile != null && Grid != null && Grid.TryGetValue(pile.GridDataPosition, out var value) && !(pile.PlacedOnStorage is ShelfComponentInstance))
		{
			if (value.Pile != null && value.Pile != pile)
			{
				Log.Warning("Stockpile instance OnPilePreSpawn problem. This should never happen. Old: " + value.Pile.BlueprintId + " New: " + pile.BlueprintId + " POS " + pile.GetPosition().ToString(), "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Stockpile\\StockpileInstance.cs");
			}
			else
			{
				AddPileToGrid(pile);
			}
		}
	}

	private void OnPileDestroy(ResourcePileInstance pile)
	{
		if (Grid != null && Grid.TryGetValue(pile.GridDataPosition, out var value) && value.Pile != null && value.Pile == pile)
		{
			Grid[pile.GridDataPosition].SetPile(null);
		}
	}

	private void InitAllowedResourcesFromBlueprint()
	{
		if (Blueprint == null)
		{
			return;
		}
		foreach (Resource allItem in Repository<ResourceRepository, Resource>.Instance.GetAllItems())
		{
			if (!(allItem == null))
			{
				if (Blueprint.ResourceGroups.Select((ResourceGroups item) => item.GetID()).Contains(allItem.SortingGroup))
				{
					resourcesFilter.AddAllowedResource(allItem);
				}
				resourcesFilter.CacheDefaultAllowedResources(allItem);
			}
		}
	}

	private void SetupDefaultAllowedByBlueprint()
	{
		if (Blueprint == null)
		{
			return;
		}
		foreach (Resource allItem in Repository<ResourceRepository, Resource>.Instance.GetAllItems())
		{
			if (!(allItem == null))
			{
				resourcesFilter.CacheDefaultAllowedResources(allItem);
			}
		}
	}

	private void InitGridSpaces(Vec3Int[,] stockpileGridSpaces)
	{
		if (grid == null)
		{
			grid = new StockpileSpaceDataDictionary();
		}
		for (int i = 0; i < stockpileGridSpaces.GetLength(0); i++)
		{
			for (int j = 0; j < stockpileGridSpaces.GetLength(1); j++)
			{
				Vec3Int gridPosition = stockpileGridSpaces[i, j];
				if (gridPosition.Equals(Vec3Int.zero))
				{
					continue;
				}
				MapNode node = base.Map.GetNode(in gridPosition);
				if (node == null || !node.IsWalkable)
				{
					bool isEnabled;
					FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(27, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Stockpile\\StockpileInstance.cs");
					if (isEnabled)
					{
						messageBuilder.AppendLiteral("Stockpile ");
						messageBuilder.AppendFormatted(base.GridDataPosition);
						messageBuilder.AppendLiteral(" invalid node @(");
						messageBuilder.AppendFormatted(gridPosition);
						messageBuilder.AppendLiteral(")");
					}
					Log.Warning(messageBuilder);
					return;
				}
				Grid.Add(gridPosition, new StockpileSpaceData(gridPosition));
				refreshWorldPositions = true;
				if (node.GetWorldObject(GridDataType.ResourcePile) is ResourcePileInstance pile)
				{
					AddPileToGrid(pile);
				}
			}
		}
	}

	private void OnResourceFilterParametersChanged()
	{
		if (grid == null)
		{
			return;
		}
		bool flag = false;
		foreach (StockpileSpaceData value in Grid.Values)
		{
			while (value.HasAnyReservations())
			{
				foreach (StockpileReservationInfo reservationInfo in value.ReservationInfos)
				{
					if (!resourcesFilter.IsBlueprintAllowed(reservationInfo.Blueprint))
					{
						value.ReleaseReservation(reservationInfo.Agent);
						goto IL_0029;
					}
				}
				break;
				IL_0029:;
			}
			if (value.Pile == null)
			{
				ResourcePileInstance worldObject = VillageMapCached.GetWorldObject<ResourcePileInstance>(GridDataType.ResourcePile, value.Position);
				if (worldObject != null)
				{
					AddPileToGrid(worldObject);
					flag = true;
				}
			}
			else if (!resourcesFilter.IsValid(value.Pile.GetStoredResource()))
			{
				RemovePileFromGrid(value);
				flag = true;
			}
		}
		if (flag)
		{
			MonoSingleton<ResourcePileTracker>.Instance.ScheduleRecountPiles();
		}
	}

	private bool UnderWater()
	{
		WaterDepthLevel waterDepthLevel = WaterDepthLevel.None;
		if (Positions == null || Positions.Count <= 1)
		{
			waterDepthLevel = base.Map.WaterManager.GetWaterLevelAsDepth(base.GridDataPosition);
		}
		else
		{
			foreach (Vec3Int position in Positions)
			{
				WaterDepthLevel waterLevelAsDepth = base.Map.WaterManager.GetWaterLevelAsDepth(position);
				if (waterLevelAsDepth > waterDepthLevel)
				{
					waterDepthLevel = waterLevelAsDepth;
				}
			}
		}
		if (waterDepthLevel != WaterDepthLevel.Medium)
		{
			return waterDepthLevel == WaterDepthLevel.High;
		}
		return true;
	}

	public override void Serialize(FVSerializer serializer)
	{
		base.Serialize(serializer);
		serializer.Write("stockpileName", stockpileName);
		serializer.WriteEnum("buildingSubcategoryUI", buildingSubcategoryUI);
		serializer.Write("start", start);
		serializer.Write("end", end);
		serializer.Write("grid", grid);
		serializer.Write("resourcesFilter", resourcesFilter);
		serializer.WriteEnum("priority", priority);
		serializer.Write("canBeUsedInProduction", canBeUsedInProduction);
	}

	public StockpileInstance(FVDeserializer deserializer)
		: base(deserializer)
	{
		stockpileName = deserializer.ReadString("stockpileName");
		buildingSubcategoryUI = deserializer.ReadEnum("buildingSubcategoryUI", BuildingSubCategoryUI.None);
		start = deserializer.ReadVec3Int("start");
		end = deserializer.ReadVec3Int("end");
		grid = deserializer.ReadObject<StockpileSpaceDataDictionary>("grid");
		resourcesFilter = deserializer.ReadObject<ResourcesFilter>("resourcesFilter");
		priority = deserializer.ReadEnum("priority", ZonePriority.None);
		canBeUsedInProduction = deserializer.ReadBool("canBeUsedInProduction", defaultValue: true);
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
