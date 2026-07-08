using System;
using System.Collections.Generic;
using Managers.Selection.EventData;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.Controllers;
using NSMedieval.Crops;
using NSMedieval.Managers.Selection;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Resources;
using NSMedieval.State;
using NSMedieval.StatsSystem;
using NSMedieval.Stockpiles;
using NSMedieval.Terrain;
using NSMedieval.Tutorial;
using NSMedieval.Types;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Views.Resources;
using NSMedieval.Village;
using UnityEngine;

namespace NSMedieval.Manager;

public class PlantResourceManager : MapResourceManager<PlantResourceManager, PlantMapResourceInstance, PlantMapResourceView>
{
	[NonSerialized]
	public readonly Dictionary<OrderType, HashSet<MapResourceInstance>> ResourcesWithOrders = new Dictionary<OrderType, HashSet<MapResourceInstance>>();

	private bool forceHarvestPhase;

	private bool instantCut;

	public bool ForceHarvestPhase
	{
		get
		{
			return forceHarvestPhase;
		}
		set
		{
			forceHarvestPhase = value;
		}
	}

	public bool InstantCut
	{
		get
		{
			return instantCut;
		}
		set
		{
			instantCut = value;
		}
	}

	public bool ForceCutPhase { get; set; }

	public void ForceHarvestPhaseToggle()
	{
		forceHarvestPhase = !forceHarvestPhase;
	}

	public void InstantCutToggle()
	{
		instantCut = !instantCut;
	}

	public PlantMapResourceView GetView(PlantMapResourceInstance instance)
	{
		return base.ResourcesInstanceViewDictionary.GetValueOrDefault(instance);
	}

	public PlantMapResourceInstance GetPlant(Vec3Int gridPos)
	{
		return base.PositionInstanceDictionary.GetValueOrDefault(gridPos);
	}

	private void ShowTrees()
	{
		GlobalSaveController.CurrentVillageData.TreesVisible = true;
		Shader.SetGlobalFloat("_TreesHidden", 0f);
		foreach (PlantMapResourceInstance key in base.ResourcesInstanceViewDictionary.Keys)
		{
			if (key.Blueprint.PlantType.Equals(PlantType.Tree))
			{
				base.ResourcesInstanceViewDictionary[key].LayerObjectHide.SetBlockActivatingColliders(blockActivatingColliders: false);
				base.ResourcesInstanceViewDictionary[key].LayerObjectHide.ForceActivateColliders();
			}
		}
	}

	private void HideTrees()
	{
		GlobalSaveController.CurrentVillageData.TreesVisible = false;
		Shader.SetGlobalFloat("_TreesHidden", 1f);
		foreach (PlantMapResourceInstance key in base.ResourcesInstanceViewDictionary.Keys)
		{
			if (key.Blueprint.PlantType.Equals(PlantType.Tree))
			{
				base.ResourcesInstanceViewDictionary[key].LayerObjectHide.SetBlockActivatingColliders(blockActivatingColliders: true);
				base.ResourcesInstanceViewDictionary[key].LayerObjectHide.ForceDeactivateColliders();
			}
		}
	}

	public void ShowHideTrees()
	{
		if (GlobalSaveController.CurrentVillageData.TreesVisible)
		{
			HideTrees();
		}
		else
		{
			ShowTrees();
		}
	}

	public IEnumerable<PlantMapResourceInstance> IteratePlants(string blueprintId, int minimumPhase)
	{
		if (!InstancesByBlueprintId.TryGetValue(blueprintId, out var value))
		{
			yield break;
		}
		foreach (PlantMapResourceInstance item in value)
		{
			if (item.CurrentPhase >= minimumPhase)
			{
				yield return item;
			}
		}
	}

	public int GetPlantCount(string blueprintId)
	{
		if (InstancesByBlueprintId.TryGetValue(blueprintId, out var value))
		{
			return value.Count;
		}
		return 0;
	}

	public int GetTotalPlantCount()
	{
		return base.InstanceView.Count;
	}

	protected override PlantMapResourceInstance CreateInstance(string modelId, string prefabId, Vector3 position)
	{
		return new PlantMapResourceInstance(Repository<PlantMapResourceRepository, PlantMapResource>.Instance.GetByID(modelId), prefabId, position, domestic: false);
	}

	protected override GridDataType GetGridTypeData()
	{
		return GridDataType.PlantMapResource;
	}

	protected override void OnResourceDestroyed(PlantMapResourceInstance resourceInstance)
	{
		VillageManager.ActiveVillage.Map.RemoveFromWorld(resourceInstance);
		if (!resourceInstance.HasDisposed)
		{
			resourceInstance.Dispose();
		}
	}

	protected override void ResourceInstantiated(PlantMapResourceInstance resourceInstance)
	{
		MonoSingleton<FloraController>.Instance.ChangeLifePhase(resourceInstance);
	}

	protected override void ResourceInstantiated(PlantMapResourceInstance resourceInstance, PlantMapResourceView resourceView)
	{
	}

	public PlantMapResourceInstance SpawnPlantMapResource(string modelId, Vector3 position, string prefabId, int currentPhase, bool domestic, bool randomPhaseHours)
	{
		PlantMapResourceInstance plantMapResourceInstance = CreateInstance(modelId, prefabId, position, currentPhase, domestic, randomPhaseHours);
		InstantiateResource(plantMapResourceInstance);
		MonoSingleton<FloraController>.Instance.SpawnPlantMapResourceInstance(plantMapResourceInstance);
		return plantMapResourceInstance;
	}

	public void SpawnMatureNonStuntedPlantMapResource(string modelId, Vector3 position, string prefabId, int currentPhase, bool domestic, bool randomPhaseHours)
	{
		PlantMapResourceInstance plantMapResourceInstance = CreateInstance(modelId, prefabId, position, currentPhase, domestic, randomPhaseHours);
		InstantiateResource(plantMapResourceInstance);
		MonoSingleton<FloraController>.Instance.SpawnPlantMapResourceInstance(plantMapResourceInstance);
		plantMapResourceInstance.SetStunted(stunted: false);
		plantMapResourceInstance.StartMaturePhaseDebug();
	}

	public void SpawnPlantMapResourceInstance(PlantMapResourceInstance instance)
	{
		using (ProfilerSampleJanitor.Begin("SpawnPlantMapResourceInstance"))
		{
			InstantiateResource(instance);
			MonoSingleton<FloraController>.Instance.SpawnPlantMapResourceInstance(instance);
		}
	}

	private PlantMapResourceInstance CreateInstance(string modelId, string prefabId, Vector3 position, int currentPhase, bool domestic, bool randomPhaseHours)
	{
		PlantMapResource byID = Repository<PlantMapResourceRepository, PlantMapResource>.Instance.GetByID(modelId);
		int currentPhase2 = ((currentPhase != -1) ? currentPhase : UnityEngine.Random.Range(0, byID.LifePhases.Count));
		return new PlantMapResourceInstance(byID, prefabId, position, domestic, currentPhase2, randomPhaseHours);
	}

	private void OnChangeLifePhase(PlantMapResourceInstance plantMapResourceInstance)
	{
		if (base.ResourcesInstanceViewDictionary.TryGetValue(plantMapResourceInstance, out var value))
		{
			value.ChangeLifePhase();
		}
	}

	private void OnHarvestFinished(PlantMapResourceInstance plantResourceInstance)
	{
		if (base.ResourcesInstanceViewDictionary.ContainsKey(plantResourceInstance))
		{
			plantResourceInstance.Harvested();
			plantResourceInstance.SetCurrentOrder(OrderType.None);
			if (plantResourceInstance.Blueprint.DestroyOnHarvest)
			{
				VillageManager.ActiveVillage.Map.RemoveFromWorld(plantResourceInstance);
			}
			else
			{
				plantResourceInstance.SetPlayerOrder(playerOrder: false);
			}
		}
	}

	private void Start()
	{
		MonoSingleton<FloraController>.Instance.CreateResourceEvent += base.OnCreateResource;
		MonoSingleton<FloraController>.Instance.CreateResourceListEvent += base.OnCreateResourceList;
		MonoSingleton<FloraController>.Instance.DestroyResourceEvent += base.OnDestroyResource;
		MonoSingleton<FloraController>.Instance.ReinstanceResourceEvent += base.OnReinstanceResource;
		MonoSingleton<FloraController>.Instance.ChangeLifePhaseEvent += OnChangeLifePhase;
		MonoSingleton<FloraController>.Instance.HarvestFinishedEvent += OnHarvestFinished;
		MonoSingleton<FloraController>.Instance.CuttingFinishedEvent += base.OnDestroyResource;
		MonoSingleton<CropsController>.Instance.CropfieldPlantTypeChangedEvent += OnCropfieldPlantTypeChanged;
		MonoSingleton<CropsController>.Instance.CropfieldHarvestPhaseChangedEvent += OnCropfieldHarvestPhaseChanged;
		MonoSingleton<CropsController>.Instance.CropfieldCutPhaseChangedEvent += OnCropfieldCutPhaseChanged;
		MonoSingleton<CropsController>.Instance.CropfieldDestroyedEvent += OnCropfieldDestroyed;
		MonoSingleton<GroundController>.Instance.OnGroundDestroyedSingleEvent += OnGroundDestroyedSingle;
		MonoSingleton<GroundController>.Instance.OnGroundDestroyedEvent += OnGroundDestroyed;
		MonoSingleton<StockpileController>.Instance.StockpilePlacedEvent += OnStockpilePlaced;
		MonoSingleton<CropsController>.Instance.CropfieldPlacedEvent += OnCropfieldPlaced;
		MonoSingleton<World>.Instance.MapLoadedEvent += OnGameLoaded;
		MonoSingleton<CombatController>.Instance.DealDrawbridgeDamageEvent += OnDealDrawbridgeDamage;
		MonoSingleton<SelectionManager>.Instance.OrderChopEvent += OnOrderChopEvent;
		MonoSingleton<SelectionManager>.Instance.SelectionChopEvent += OnChopSelectionHighlight;
		ResourcesWithOrders.Add(OrderType.CutAllVegetation, new HashSet<MapResourceInstance>());
		ResourcesWithOrders.Add(OrderType.Harvesting, new HashSet<MapResourceInstance>());
		ResourcesWithOrders.Add(OrderType.Chopping, new HashSet<MapResourceInstance>());
	}

	private void OnGameLoaded(bool fromSave)
	{
		if (GlobalSaveController.CurrentVillageData.FirstEnter)
		{
			GlobalSaveController.CurrentVillageData.TreesVisible = true;
		}
		else if (GlobalSaveController.CurrentVillageData.TreesVisible)
		{
			ShowTrees();
		}
		else
		{
			HideTrees();
		}
	}

	protected override void OnDestroy()
	{
		if (MonoSingleton<FloraController>.IsInstantiated())
		{
			MonoSingleton<FloraController>.Instance.CreateResourceEvent -= base.OnCreateResource;
			MonoSingleton<FloraController>.Instance.CreateResourceListEvent -= base.OnCreateResourceList;
			MonoSingleton<FloraController>.Instance.DestroyResourceEvent -= base.OnDestroyResource;
			MonoSingleton<FloraController>.Instance.ReinstanceResourceEvent -= base.OnReinstanceResource;
			MonoSingleton<FloraController>.Instance.ChangeLifePhaseEvent -= OnChangeLifePhase;
			MonoSingleton<FloraController>.Instance.HarvestFinishedEvent -= OnHarvestFinished;
			MonoSingleton<FloraController>.Instance.CuttingFinishedEvent -= base.OnDestroyResource;
		}
		if (MonoSingleton<CropsController>.IsInstantiated())
		{
			MonoSingleton<CropsController>.Instance.CropfieldPlantTypeChangedEvent -= OnCropfieldPlantTypeChanged;
			MonoSingleton<CropsController>.Instance.CropfieldHarvestPhaseChangedEvent -= OnCropfieldHarvestPhaseChanged;
			MonoSingleton<CropsController>.Instance.CropfieldCutPhaseChangedEvent -= OnCropfieldCutPhaseChanged;
			MonoSingleton<CropsController>.Instance.CropfieldDestroyedEvent -= OnCropfieldDestroyed;
		}
		if (MonoSingleton<StockpileController>.IsInstantiated())
		{
			MonoSingleton<StockpileController>.Instance.StockpilePlacedEvent -= OnStockpilePlaced;
		}
		if (MonoSingleton<CropsController>.IsInstantiated())
		{
			MonoSingleton<CropsController>.Instance.CropfieldPlacedEvent -= OnCropfieldPlaced;
		}
		if (MonoSingleton<GroundController>.IsInstantiated())
		{
			MonoSingleton<GroundController>.Instance.OnGroundDestroyedSingleEvent -= OnGroundDestroyedSingle;
			MonoSingleton<GroundController>.Instance.OnGroundDestroyedEvent -= OnGroundDestroyed;
		}
		if (MonoSingleton<World>.IsInstantiated())
		{
			MonoSingleton<World>.Instance.MapLoadedEvent -= OnGameLoaded;
		}
		if (MonoSingleton<CombatController>.IsInstantiated())
		{
			MonoSingleton<CombatController>.Instance.DealDrawbridgeDamageEvent -= OnDealDrawbridgeDamage;
		}
		if (MonoSingleton<SelectionManager>.IsInstantiated())
		{
			MonoSingleton<SelectionManager>.Instance.OrderChopEvent -= OnOrderChopEvent;
			MonoSingleton<SelectionManager>.Instance.SelectionChopEvent += OnChopSelectionHighlight;
		}
		foreach (HashSet<MapResourceInstance> value in ResourcesWithOrders.Values)
		{
			value.Clear();
		}
		ResourcesWithOrders.Clear();
		base.OnDestroy();
	}

	private void OnGroundDestroyed(List<Vec3Int> positions)
	{
		foreach (Vec3Int position in positions)
		{
			Vec3Int a = position;
			Vec3Int b = Vec3Int.up;
			Vec3Int key = a + b;
			if (base.PositionInstanceDictionary.ContainsKey(key))
			{
				PlantMapResourceInstance plantMapResourceInstance = base.PositionInstanceDictionary[key];
				plantMapResourceInstance.SetLastPhase();
				MonoSingleton<FloraController>.Instance.DestroyResource(plantMapResourceInstance);
			}
		}
	}

	private void OnGroundDestroyedSingle(Vec3Int position)
	{
		Vec3Int b = Vec3Int.up;
		Vec3Int key = position + b;
		if (base.PositionInstanceDictionary.ContainsKey(key))
		{
			PlantMapResourceInstance plantMapResourceInstance = base.PositionInstanceDictionary[key];
			plantMapResourceInstance.SetLastPhase();
			MonoSingleton<FloraController>.Instance.DestroyResource(plantMapResourceInstance);
		}
	}

	private void OnStockpilePlaced(StockpileInstance stockpileInstance)
	{
		if (TutorialManager.IsTutorialActive)
		{
			return;
		}
		foreach (Vec3Int position in stockpileInstance.Positions)
		{
			OnForceOrderOnResource(position);
		}
	}

	private void OnCropfieldPlaced(CropfieldInstance cropfieldInstance)
	{
		foreach (Vec3Int position in cropfieldInstance.Positions)
		{
			if (!base.PositionInstanceDictionary.ContainsKey(position))
			{
				continue;
			}
			PlantMapResourceInstance plantMapResourceInstance = base.PositionInstanceDictionary[position];
			if (plantMapResourceInstance != null)
			{
				if (cropfieldInstance.CultivablePlant.Equals(plantMapResourceInstance.Blueprint))
				{
					plantMapResourceInstance.SetDomestic(domestic: true);
					plantMapResourceInstance.SetHarvestPhase(cropfieldInstance.HarvestPhase);
					plantMapResourceInstance.SetCutPhase(cropfieldInstance.CutPhase);
					cropfieldInstance.CropCreatedOverExistingPlant(plantMapResourceInstance);
				}
				else
				{
					OnForceOrderOnResource(position);
				}
			}
		}
	}

	private void OnCropfieldPlantTypeChanged(CropfieldInstance cropfieldInstance)
	{
		foreach (Vec3Int position in cropfieldInstance.Positions)
		{
			if (!base.PositionInstanceDictionary.ContainsKey(position))
			{
				continue;
			}
			PlantMapResourceInstance plantMapResourceInstance = base.PositionInstanceDictionary[position];
			if (plantMapResourceInstance != null)
			{
				if (plantMapResourceInstance.Blueprint != cropfieldInstance.CultivablePlant)
				{
					OnForceOrderOnResource(position);
				}
				else
				{
					plantMapResourceInstance.SetCurrentOrder(OrderType.None);
				}
				plantMapResourceInstance.SetPlayerOrder(playerOrder: false);
			}
		}
	}

	private void OnCropfieldHarvestPhaseChanged(CropfieldInstance cropfieldInstance)
	{
		foreach (Vec3Int position in cropfieldInstance.Positions)
		{
			if (!base.PositionInstanceDictionary.ContainsKey(position))
			{
				continue;
			}
			PlantMapResourceInstance plantMapResourceInstance = base.PositionInstanceDictionary[position];
			if (plantMapResourceInstance != null && !(plantMapResourceInstance.Blueprint != cropfieldInstance.CultivablePlant) && !plantMapResourceInstance.PlayerOrder)
			{
				plantMapResourceInstance.SetHarvestPhase(cropfieldInstance.HarvestPhase);
				if (plantMapResourceInstance.CurrentOrder.Equals(OrderType.None) && base.ResourcesInstanceViewDictionary.ContainsKey(plantMapResourceInstance) && cropfieldInstance.HarvestPhase == -1)
				{
					plantMapResourceInstance.SetCurrentOrder(OrderType.None);
				}
			}
		}
	}

	private void OnCropfieldCutPhaseChanged(CropfieldInstance cropfieldInstance)
	{
		foreach (Vec3Int position in cropfieldInstance.Positions)
		{
			if (!base.PositionInstanceDictionary.ContainsKey(position))
			{
				continue;
			}
			PlantMapResourceInstance plantMapResourceInstance = base.PositionInstanceDictionary[position];
			if (plantMapResourceInstance != null && !(plantMapResourceInstance.Blueprint != cropfieldInstance.CultivablePlant) && !plantMapResourceInstance.PlayerOrder)
			{
				plantMapResourceInstance.SetCutPhase(cropfieldInstance.CutPhase);
				if (plantMapResourceInstance.CurrentOrder.Equals(OrderType.None) && base.ResourcesInstanceViewDictionary.ContainsKey(plantMapResourceInstance) && cropfieldInstance.CutPhase == -1)
				{
					plantMapResourceInstance.SetCurrentOrder(OrderType.None);
				}
			}
		}
	}

	private void OnCropfieldDestroyed(CropfieldInstance cropfieldInstance)
	{
		foreach (Vec3Int position in cropfieldInstance.Positions)
		{
			if (base.PositionInstanceDictionary.ContainsKey(position))
			{
				base.PositionInstanceDictionary[position]?.RemovedFromCropfield();
			}
		}
	}

	private void OnDealDrawbridgeDamage(DrawbridgeComponent drawbridgeComponent)
	{
		if (drawbridgeComponent == null)
		{
			return;
		}
		DoorComponentInstance componentInstance = drawbridgeComponent.DoorComponent.ComponentInstance;
		if (componentInstance == null || componentInstance.HasDisposed || componentInstance.OwnerBuilding == null || componentInstance.OwnerBuilding.HasDisposed)
		{
			return;
		}
		float damagePercent = componentInstance.DamagePercent;
		DoorComponentBlueprint blueprint = componentInstance.Blueprint;
		using PooledList<PlantMapResourceInstance> pooledList = ListPool<PlantMapResourceInstance>.GetJanitor(base.ResourcesInstanceViewDictionary.Keys);
		for (int num = pooledList.Count - 1; num >= 0; num--)
		{
			PlantMapResourceInstance plantMapResourceInstance = pooledList[num];
			Vec3Int gridPosition = plantMapResourceInstance.GetGridPosition();
			if (drawbridgeComponent.DrawbridgePositions.Contains(gridPosition) && !(UnityEngine.Random.value > blueprint.ChanceToHurt))
			{
				StatInstance statInstance = plantMapResourceInstance.Stats?.GetStat(StatType.Health);
				if (statInstance != null)
				{
					float current = statInstance.Current - blueprint.FloraDamage * damagePercent;
					statInstance.SetCurrent(current);
					if (statInstance.Current <= 0f)
					{
						plantMapResourceInstance.SetLastPhase();
					}
				}
			}
		}
	}

	protected override void OnForceOrderOnResource(Vec3Int position)
	{
		if (!base.PositionInstanceDictionary.TryGetValue(position, out var value) || value.HasDisposed)
		{
			return;
		}
		PlantMapResourceView value2;
		if (MonoSingleton<PlantResourceManager>.Instance.InstantCut)
		{
			value.SetLastPhase();
		}
		else if (base.InstanceView.TryGetValue(value, out value2))
		{
			if ((value.GetPossibleOrders() & OrderType.Chopping) == OrderType.Chopping)
			{
				value2.SelectItem(OrderType.Chopping);
			}
			else
			{
				value2.SelectItem(OrderType.CutAllVegetation);
			}
		}
	}

	protected override void OnOrderResourceCollectionEventCallback(OrderEventData eventData, PooledList<PlantMapResourceInstance> instancesToProcess)
	{
		if (!eventData.OrderType.Equals(OrderType.CutAllVegetation) || !instantCut)
		{
			return;
		}
		foreach (PlantMapResourceInstance item in instancesToProcess)
		{
			item.SetLastPhase();
		}
	}

	private void OnOrderChopEvent(PlantOrderEventData eventData)
	{
		using PooledList<PlantMapResourceView> pooledList2 = ListPool<PlantMapResourceView>.GetJanitor();
		using PooledList<PlantMapResourceInstance> pooledList3 = ListPool<PlantMapResourceInstance>.GetJanitor();
		bool flag = eventData.OrderType.Equals(OrderType.Chopping) && MonoSingleton<PlantResourceManager>.Instance.InstantCut;
		using PooledList<PlantMapResourceInstance> pooledList = ListPool<PlantMapResourceInstance>.GetJanitor(base.InstanceView.Keys);
		foreach (PlantMapResourceInstance item in pooledList)
		{
			if (item.HasDisposed || !base.InstanceView.TryGetValue(item, out var value) || (int)(value.transform.position.y / (float)World.MapBlockHeight) > MonoSingleton<World>.Instance.ElevationLevel || !SelectionManager.IsWithinSelectionBounds(value.transform.position, eventData.MinPoint.x, eventData.MaxPoint.x, eventData.MinPoint.y, eventData.MaxPoint.y) || (eventData.AffectOnlyOneLayer && (int)value.transform.position.y != (int)eventData.WorldSpaceY))
			{
				continue;
			}
			if (eventData.OrderType.Equals(OrderType.Cancel))
			{
				pooledList2.Add(value);
			}
			else if (eventData.PlantLifePhase == PlantLifePhaseType.None || item.CurrentPhaseType == eventData.PlantLifePhase)
			{
				if (flag)
				{
					pooledList3.Add(item);
				}
				else
				{
					value.SelectItem(eventData.OrderType);
				}
			}
		}
		foreach (PlantMapResourceInstance item2 in pooledList3)
		{
			item2.SetLastPhase();
		}
		foreach (PlantMapResourceView item3 in pooledList2)
		{
			item3.CancelOrder(eventData.OrderType);
		}
	}

	private void OnChopSelectionHighlight(PlantOrderEventData eventData)
	{
		foreach (KeyValuePair<PlantMapResourceInstance, PlantMapResourceView> item in base.InstanceView)
		{
			PlantMapResourceInstance key = item.Key;
			PlantMapResourceView value = item.Value;
			if (!key.HasDisposed && (int)(value.transform.position.y / (float)World.MapBlockHeight) <= MonoSingleton<World>.Instance.ElevationLevel)
			{
				if (!SelectionManager.IsWithinSelectionBounds(value.transform.position, eventData.MinPoint.x, eventData.MaxPoint.x, eventData.MinPoint.y, eventData.MaxPoint.y))
				{
					value.HideHighlight();
				}
				else if (!key.OrderShouldHighlightMe(eventData))
				{
					value.HideHighlight();
				}
				else if ((!eventData.AffectOnlyOneLayer || (int)value.transform.position.y == (int)eventData.WorldSpaceY) && (eventData.PlantLifePhase == PlantLifePhaseType.None || key.CurrentPhaseType == eventData.PlantLifePhase))
				{
					value.ShowHighlight();
				}
			}
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
