using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Collections;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.Components;
using NSMedieval.Components.Base;
using NSMedieval.Construction;
using NSMedieval.Controllers;
using NSMedieval.Enums;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Resources;
using NSMedieval.Serialization;
using NSMedieval.StatsSystem;
using NSMedieval.Stockpiles;
using NSMedieval.StorageUniversal;
using NSMedieval.Terrain;
using NSMedieval.Tools;
using NSMedieval.Types;
using NSMedieval.UI.Utils;
using NSMedieval.View;
using NSMedieval.Views.Resources;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using NSMedieval.Water;
using UnityEngine;

namespace NSMedieval.State;

[Serializable]
[FVSerializableKey("ResourcePileInstance", "")]
public class ResourcePileInstance : WorldObject, IForbidable, IGameDisposable, IDisposable, IDamageTakingAgent, IDamageCommonAgent, IGoapTargetable, IStatsOwner
{
	public const float DefaultHaulingDisabledSeconds = 30f;

	[NonSerialized]
	private Storage storage;

	[SerializeField]
	private bool isForbidden;

	[SerializeField]
	private bool isUrgentHaul;

	[NonSerialized]
	private HumanoidInstance equipTarget;

	private bool doNotCreateView;

	private Resource blueprint;

	private bool isStoredOnStockpile;

	private IStorage placedOnStorage;

	private bool updateShelfView;

	private List<Vec3Int> positions = new List<Vec3Int>();

	private bool reserveAll;

	private volatile bool canBeHauled;

	public override bool BlueprintExists => Blueprint != null;

	[field: SerializeField]
	public bool PlacedOnAnimalFeeder { get; private set; }

	[field: SerializeField]
	public bool PlacedOnPrisonStash { get; private set; }

	public float HaulingDisabledSecondsRemaining { get; set; }

	public ResourcePileInstanceInfo Info { get; private set; }

	internal override ThermalModel ThermalModel => blueprint?.ThermalModel;

	public StockpileInstance InstanceStockpile { get; private set; }

	public UniversalStorage InstanceStorage { get; private set; }

	public override List<Vec3Int> Positions => positions;

	public override ushort PathfindingPenalty => 2000;

	public override float WalkSpeedMultiplier => Blueprint.WalkSpeedMultiplier;

	public override float Flammability => Blueprint.Flammability;

	public IStorage PlacedOnStorage => placedOnStorage;

	public bool IsPlacedOnStorageBuilding => placedOnStorage is ShelfComponentInstance;

	public ZonePriority StoragePriority => PlacedOnStorage?.Priority ?? ZonePriority.None;

	public bool HasDied => base.HasDisposed;

	public bool IsReserveAll => reserveAll;

	public bool DoNotCreateView
	{
		get
		{
			return doNotCreateView;
		}
		set
		{
			doNotCreateView = value;
		}
	}

	public override ConcurrentHashSet<Vec3Int> ReachablePositions
	{
		get
		{
			if (!IsPlacedOnStorageBuilding)
			{
				return base.ReachablePositions;
			}
			if (!base.Village.SavedObjectsSpawned)
			{
				return base.ReachablePositions;
			}
			if (!(placedOnStorage is ShelfComponentInstance shelfComponentInstance))
			{
				return base.ReachablePositions;
			}
			if (shelfComponentInstance.OwnerBuilding.ReachablePositions == null)
			{
				bool isEnabled;
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(80, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Pile triggered storage building reachability update. This should never happen. ");
					messageBuilder.AppendFormatted(blueprintId);
					messageBuilder.AppendLiteral("@");
					messageBuilder.AppendFormatted(base.GridDataPosition.ToString());
				}
				Log.Warning(messageBuilder);
				shelfComponentInstance.OwnerBuilding.UpdateReachability();
				return shelfComponentInstance.OwnerBuilding.ReachablePositions;
			}
			if (shelfComponentInstance.OwnerBuilding.ReachablePositions.Count <= 0)
			{
				return base.ReachablePositions;
			}
			return shelfComponentInstance.OwnerBuilding.ReachablePositions;
		}
	}

	public bool CanBeHauled
	{
		get
		{
			if (canBeHauled)
			{
				return HaulingDisabledSecondsRemaining <= 0f;
			}
			return false;
		}
	}

	public bool Frozen { get; set; }

	public bool IsForbidden
	{
		get
		{
			return isForbidden;
		}
		set
		{
			if (value != isForbidden)
			{
				this.ForbidStateWillChangeEvent?.Invoke(this);
				if (isUrgentHaul && value)
				{
					IsUrgentHaul = false;
				}
				isForbidden = value;
				MonoSingleton<ResourcePileHaulingManager>.Instance.OnPileForbidStateChanged(this);
				this.ForbidChangeEvent?.Invoke(this);
				base.Map.BuildingsManagerMain.ResourceChangedRefreshBlueprints(Blueprint);
				ForbidStatusChanged();
			}
		}
	}

	public bool IsUrgentHaul
	{
		get
		{
			return isUrgentHaul;
		}
		set
		{
			if (value == isUrgentHaul)
			{
				return;
			}
			if (isForbidden && value)
			{
				IsForbidden = false;
				MonoSingleton<ResourcePileHaulingManager>.Instance.ForceProcessPileState(this);
			}
			if (value && !CanBeHauled)
			{
				return;
			}
			bool isEnabled;
			FVLogTraceInterpolationHandler messageBuilder = new FVLogTraceInterpolationHandler(15, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendFormatted(this);
				messageBuilder.AppendLiteral(" -- isUrgent = ");
				messageBuilder.AppendFormatted(value);
			}
			Log.Trace(messageBuilder);
			isUrgentHaul = value;
			if (MonoSingleton<ResourcePileTracker>.IsInstantiated())
			{
				if (isUrgentHaul)
				{
					MonoSingleton<ResourcePileTracker>.Instance.UrgentHaulPileCount++;
				}
				else
				{
					MonoSingleton<ResourcePileTracker>.Instance.UrgentHaulPileCount--;
				}
			}
			this.UrgentHaulChangeEvent?.Invoke(this);
		}
	}

	public Resource Blueprint => blueprint = ((blueprint == null) ? Repository<ResourceRepository, Resource>.Instance.GetByID(blueprintId) : blueprint);

	public StatsInstance Stats
	{
		get
		{
			StatsInstance statsInstance = GetStoredResource()?.Stats;
			if (statsInstance == null)
			{
				return null;
			}
			if (statsInstance.Owner != this)
			{
				statsInstance.SetOwner(this);
			}
			return statsInstance;
		}
	}

	public DamageTakingAgentType DamageAgentType => DamageTakingAgentType.ResourcePile;

	public bool HasActivePath => false;

	private event Action DurabilityDepletedEvent;

	public event Action<IForbidable> ForbidChangeEvent;

	public event Action<IForbidable> ForbidStateWillChangeEvent;

	public event Action<ResourcePileInstance> UrgentHaulChangeEvent;

	public event Action<ResourcePileInstance, Resource, int> OnResourceAddedEvent;

	public event Action<ResourcePileInstance, Resource, int> OnResourceTakenEvent;

	public event Action<bool, StockpileInstance> ResourceStoredOnStockpileEvent;

	public event Action<bool, UniversalStorage> ResourceStoredOnStorageEvent;

	public event Action<ResourcePileInstance, bool> SetCanBeHauledEvent;

	public ResourcePileInstance(ResourceInstance resource, Vector3 worldPosition, FactionOwnership factionOwnership = FactionOwnership.Player)
		: base(WorldObjectType.ResourcePile, worldPosition, Vec3Int.one, 0f, GridDataType.None, factionOwnership)
	{
		blueprint = resource.Blueprint;
		blueprintId = blueprint.GetID();
		storage = new Storage(new StorageBase(blueprint.StackingLimit, ignoreWeigth: true), resource);
		IsForbidden = resource.ForbidOnInit;
		resource.SetFaction(factionOwnership);
		resource.ResourcePileInstance = this;
		Info = new ResourcePileInstanceInfo(this);
		PlacedOnAnimalFeeder = (InstanceStorage?.GetOwner?.AnimalFeeder).GetValueOrDefault();
		PlacedOnPrisonStash = (InstanceStorage?.GetOwner?.PrisonFeeder).GetValueOrDefault();
		SetCallbacks();
		MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent += new Action<BaseBuildingInstance>(OnBuildingFinished);
		MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent += new Action<BaseBuildingInstance>(OnBuildingDestroyed);
		if (Stats.GetStat(StatType.Health).Current <= 0f)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(62, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Pile ");
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral(" at grid position ");
				messageBuilder.AppendFormatted(base.GridDataPosition);
				messageBuilder.AppendLiteral(" HP is 0 before listener is registered.");
			}
			Log.Warning(messageBuilder);
		}
		Stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Health, OnHealthDepleted);
		Stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Freshness, OnFreshnessDepleted);
		Stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Fermentation, OnFermentationDepleted);
		MonoSingleton<CombatAgentManager>.Instance.RegisterCommonCombatAgent(this);
		RefreshModifiers();
		if (blueprint.Category.HasFlag(ResourceCategory.CtgItem))
		{
			MonoSingleton<ResourcePileController>.Instance.EquipOrderUpdateEvent += OnEquipOrder;
		}
		MapNode node = GetNode();
		node.OnWalkabilityChangedEvent += HandleFallDown;
		MapNode nodeAbove = node.GetNodeAbove();
		if (nodeAbove != null)
		{
			nodeAbove.OnWalkabilityChangedEvent += HandleFallDown;
		}
	}

	protected virtual void ForbidStatusChanged()
	{
	}

	public override string ToString()
	{
		string text = string.Empty;
		if (PlacedOnStorage is ShelfComponentInstance shelfComponentInstance)
		{
			text = shelfComponentInstance.OwnerBuilding.ToString();
		}
		return $"ResourcePile '{blueprintId}' x {storage?.GetSingleResource()?.Count} at {base.GridDataPosition}, IsForbidden: {IsForbidden}, IsUrgentHaul: {IsUrgentHaul}, placedOnShelfBuilding: {text}";
	}

	public void SetCanBeHauled(bool value)
	{
		canBeHauled = value;
		if (!canBeHauled)
		{
			IsUrgentHaul = false;
		}
		this.SetCanBeHauledEvent?.Invoke(this, canBeHauled);
	}

	public bool IsStoredOnStockpile()
	{
		return isStoredOnStockpile;
	}

	public bool IsPlacedOnStockpile()
	{
		MapNode node = GetNode();
		if (node == null)
		{
			return false;
		}
		GridDataType dataType = node.DataType;
		if ((dataType & GridDataType.Stockpile) != 0)
		{
			return true;
		}
		if ((dataType & (GridDataType.Furniture | GridDataType.SocketableItem)) != 0)
		{
			foreach (WorldObject worldObject in node.WorldObjects)
			{
				if (worldObject is IStorage && !worldObject.HasDisposed)
				{
					return true;
				}
			}
		}
		return false;
	}

	public int GetNutrition()
	{
		return (int)Blueprint.Nutrition * (GetStoredResource()?.Amount ?? 1);
	}

	public NSMedieval.StatsSystem.Attribute GetAttributeOverride(AttributeType type)
	{
		return null;
	}

	public Agent GetGoapAgent()
	{
		return null;
	}

	public string GetGoapAgentID()
	{
		return string.Empty;
	}

	public Transform GetTransform()
	{
		ResourcePileView view = MonoSingleton<ResourcePileManager>.Instance.GetView(this);
		if (!(view == null))
		{
			return view.transform;
		}
		return null;
	}

	public List<EquipmentInstance> GetEquipment()
	{
		return null;
	}

	public void ReserveAll()
	{
		reserveAll = true;
	}

	public override int GetMaxReservers()
	{
		if (reserveAll)
		{
			return 1;
		}
		if (blueprint.StackingLimit > 2)
		{
			return GetStoredResource()?.Amount ?? 0;
		}
		return 1;
	}

	public override SelectableObject GetView()
	{
		return MonoSingleton<ResourcePileManager>.Instance.GetView(this);
	}

	public StatInstance GetStat(StatType type)
	{
		return GetStoredResource()?.GetStat(type);
	}

	public float GetStatValue(StatType type)
	{
		return GetStoredResource().GetStatValue(type);
	}

	public Storage GetStorage()
	{
		return storage;
	}

	public ResourceInstance GetStoredResource()
	{
		if (storage == null || storage.ResourceCount == 0)
		{
			return null;
		}
		return storage.FirstResource;
	}

	public CarcassResourceInstance GetStoredCarcass()
	{
		if (storage == null || storage.ResourceCount == 0)
		{
			return null;
		}
		return storage.FirstResource as CarcassResourceInstance;
	}

	public AnimalCarcassResourceInstance GetStoredAnimalCarcass()
	{
		if (storage == null || storage.ResourceCount == 0)
		{
			return null;
		}
		return storage.FirstResource as AnimalCarcassResourceInstance;
	}

	public void SetPlacedOnStorage(IStorage storage, UniversalStorage universalStorage = null)
	{
		if (placedOnStorage == storage)
		{
			return;
		}
		if (placedOnStorage != null)
		{
			placedOnStorage.OnDisposedEvent -= OnPlacedStorageDestroyed;
		}
		placedOnStorage = storage;
		if (placedOnStorage != null)
		{
			placedOnStorage.OnDisposedEvent += OnPlacedStorageDestroyed;
		}
		if (placedOnStorage != null)
		{
			if (universalStorage != null && universalStorage.Blueprint.OverrideStackingLimit)
			{
				GetStoredResource()?.OverrideStackingLimit(universalStorage.Blueprint.MaxAmount);
				this.storage.SetStorageCapacity(universalStorage.Blueprint.MaxAmount);
			}
		}
		else
		{
			GetStoredResource()?.ResetStackingLimit();
			this.storage?.SetStorageCapacity(blueprint.StackingLimit);
		}
		if (placedOnStorage is ShelfComponentInstance shelfComponentInstance)
		{
			shelfComponentInstance.PileStored();
		}
		if (MonoSingleton<ResourcePileController>.IsInstantiated())
		{
			MonoSingleton<ResourcePileController>.Instance.StartCoroutine(RefreshModifiersDelayed());
		}
		if (MonoSingleton<ResourcePileManager>.IsInstantiated())
		{
			MonoSingleton<ResourcePileManager>.Instance.UpdatePlacedOnStorageCategoryDictionary(this, placedOnStorage);
			MonoSingleton<ResourcePileManager>.Instance.UpdatePlacedOnStorageBlueprintDictionary(this, placedOnStorage);
		}
	}

	private void OnPlacedStorageDestroyed(IDisposable disposable)
	{
		if (placedOnStorage == (IStorage)disposable)
		{
			SetPlacedOnStorage(null);
		}
	}

	public void SetDurabilityDepletedCallback(Action callback)
	{
		DurabilityDepletedEvent += callback;
	}

	public override void Dispose()
	{
		if (base.HasDisposed)
		{
			return;
		}
		MapNode node = GetNode();
		if (node != null)
		{
			node.OnWalkabilityChangedEvent -= HandleFallDown;
			MapNode nodeAbove = node.GetNodeAbove();
			if (nodeAbove != null)
			{
				nodeAbove.OnWalkabilityChangedEvent -= HandleFallDown;
			}
		}
		if (MonoSingleton<CombatAgentManager>.IsInstantiated())
		{
			MonoSingleton<CombatAgentManager>.Instance.RemoveCommonCombatAgent(this);
		}
		if (MonoSingleton<ConstructionController>.IsInstantiated())
		{
			MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent -= new Action<BaseBuildingInstance>(OnBuildingFinished);
			MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent -= new Action<BaseBuildingInstance>(OnBuildingDestroyed);
		}
		if (blueprint != null && blueprint.Category.HasFlag(ResourceCategory.CtgItem) && MonoSingleton<ResourcePileController>.IsInstantiated())
		{
			MonoSingleton<ResourcePileController>.Instance.EquipOrderUpdateEvent -= OnEquipOrder;
		}
		equipTarget?.Inventory?.RemoveEquipOrder(this);
		this.ForbidChangeEvent = null;
		base.Dispose();
		if (storage != null)
		{
			foreach (ResourceInstance resource in storage.Resources)
			{
				resource?.Dispose();
			}
		}
		storage?.Dispose();
		storage = null;
		InstanceStockpile = null;
		if (MonoSingleton<ResourcePileManager>.IsInstantiated())
		{
			MonoSingleton<ResourcePileManager>.Instance.UpdatePlacedOnStorageCategoryDictionary(this, null);
			MonoSingleton<ResourcePileManager>.Instance.UpdatePlacedOnStorageBlueprintDictionary(this, null);
		}
		if (isUrgentHaul && MonoSingleton<ResourcePileTracker>.IsInstantiated())
		{
			MonoSingleton<ResourcePileTracker>.Instance.UrgentHaulPileCount--;
		}
		this.DurabilityDepletedEvent = null;
		this.OnResourceAddedEvent = null;
		this.OnResourceTakenEvent = null;
		this.UrgentHaulChangeEvent = null;
		this.ForbidStateWillChangeEvent = null;
		this.ForbidChangeEvent = null;
		this.ResourceStoredOnStockpileEvent = null;
		this.ResourceStoredOnStorageEvent = null;
		equipTarget = null;
		placedOnStorage = null;
	}

	private void SetWetnessAround(MapNode node, float wetness, float radius)
	{
		if (node != null && !base.HasDisposed)
		{
			byte wetnessByte = (byte)Math.Clamp(wetness * 255f, 0f, 255f);
			FloodFillUtil.FloodFillConnections(node, radius, delegate(MapNode mapNode)
			{
				node.Map.SnowGrassWetnessManager.SetWetness(mapNode.Index, wetnessByte);
				return FloodFillUtil.ScanStatus.Continue;
			});
		}
	}

	public override void ReInstantiate()
	{
		blueprint = Repository<ResourceRepository, Resource>.Instance.GetByID(blueprintId);
		Info = new ResourcePileInstanceInfo(this);
		if (blueprint == null)
		{
			Log.Error("Tried to reinstantiate pile without blueprint. ID: " + blueprintId, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			return;
		}
		if (storage == null)
		{
			Log.Error("Tried to set reinstantiate on resource pile, but storage dose not exist. ID: " + blueprintId, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			return;
		}
		bool flag = false;
		foreach (ResourceInstance resource in storage.Resources)
		{
			resource.InitAfterLoadPile();
			if (resource.HasDisposed)
			{
				flag = true;
				break;
			}
			resource.SetFaction(base.FactionOwnership);
		}
		if (flag)
		{
			Dispose();
			return;
		}
		storage.SetResourcesOwner();
		base.ReInstantiate();
		SetCallbacks();
		MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent -= new Action<BaseBuildingInstance>(OnBuildingFinished);
		MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent += new Action<BaseBuildingInstance>(OnBuildingFinished);
		MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent -= new Action<BaseBuildingInstance>(OnBuildingDestroyed);
		MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent += new Action<BaseBuildingInstance>(OnBuildingDestroyed);
		if (blueprint.Category.HasFlag(ResourceCategory.CtgItem))
		{
			MonoSingleton<ResourcePileController>.Instance.EquipOrderUpdateEvent += OnEquipOrder;
		}
		Stats.Controller.RemoveListener(OnHealthDepleted);
		Stats.Controller.RemoveListener(OnFreshnessDepleted);
		Stats.Controller.RemoveListener(OnFermentationDepleted);
		if (Stats.GetStat(StatType.Health).Current <= 0f)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(62, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Pile ");
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral(" at grid position ");
				messageBuilder.AppendFormatted(base.GridDataPosition);
				messageBuilder.AppendLiteral(" HP is 0 before listener is registered.");
			}
			Log.Warning(messageBuilder);
		}
		Stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Health, OnHealthDepleted);
		Stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Freshness, OnFreshnessDepleted);
		Stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Fermentation, OnFermentationDepleted);
		Stats.Update();
		if (!base.HasDisposed)
		{
			MapNode node = GetNode();
			node.OnWalkabilityChangedEvent -= HandleFallDown;
			node.OnWalkabilityChangedEvent += HandleFallDown;
			MapNode nodeAbove = node.GetNodeAbove();
			if (nodeAbove != null)
			{
				nodeAbove.OnWalkabilityChangedEvent -= HandleFallDown;
				nodeAbove.OnWalkabilityChangedEvent += HandleFallDown;
			}
			MonoSingleton<CombatAgentManager>.Instance.RegisterCommonCombatAgent(this);
			MonoSingleton<ResourcePileController>.Instance.StartCoroutine(RefreshModifiersDelayed());
			if (isUrgentHaul)
			{
				MonoSingleton<ResourcePileTracker>.Instance.UrgentHaulPileCount++;
			}
		}
	}

	public float GetWealth()
	{
		if (storage == null)
		{
			return 0f;
		}
		return storage.GetTotalResourcesWealth();
	}

	public ProductQuality GetQuality()
	{
		return Blueprint.Quality;
	}

	public void ForceRecalculateReachablePositions()
	{
		CalculateReachability();
	}

	public float GetTotalDurability()
	{
		Equipment byID = Repository<EquipmentRepository, Equipment>.Instance.GetByID(base.BlueprintId);
		StatInstance stat = GetStat(StatType.Health);
		if (stat == null)
		{
			return byID.ArmorRating;
		}
		return Mathf.Clamp(byID.ArmorRating * (stat.Current / stat.Max), 0f, 1f);
	}

	public string GetProducerName()
	{
		if (GetStoredResource() == null)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(80, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Tried to get producer name for pile ");
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral(" at position ");
				messageBuilder.AppendFormatted(base.GridDataPosition);
				messageBuilder.AppendLiteral(", but stored resource is null. ");
			}
			Log.Warning(messageBuilder);
			return string.Empty;
		}
		return HumanoidUtils.GetProducerName(GetStoredResource().ProducerUniqueId);
	}

	public void WaterLevelChanged()
	{
		RefreshModifiers();
	}

	internal void SetIsStoredOnStockpile(bool value, UniversalStorage universalStorage)
	{
		isStoredOnStockpile = value;
		InstanceStorage = (value ? universalStorage : null);
		PlacedOnAnimalFeeder = (InstanceStorage?.GetOwner?.AnimalFeeder).GetValueOrDefault();
		PlacedOnPrisonStash = (InstanceStorage?.GetOwner?.PrisonFeeder).GetValueOrDefault();
		this.ResourceStoredOnStorageEvent?.Invoke(isStoredOnStockpile, InstanceStorage);
		Info.SetStorageId(this);
	}

	internal void SetIsStoredOnStockpile(bool value, StockpileInstance stockpileInstance)
	{
		isStoredOnStockpile = value;
		InstanceStockpile = (value ? stockpileInstance : null);
		this.ResourceStoredOnStockpileEvent?.Invoke(isStoredOnStockpile, InstanceStockpile);
		Info.SetStorageId(this);
	}

	protected override void CalculateReachability(Func<MapNode, bool> additionalCheck = null)
	{
		if (!IsPlacedOnStorageBuilding)
		{
			RemoveFromRegions();
			ReachablePositions.Clear();
			ReachablePositions.Add(base.GridDataPosition);
			RegisterInRegions();
		}
		else
		{
			RemoveFromRegions();
			RegisterInRegions();
		}
	}

	public override float GetBeautyInput()
	{
		if (Blueprint == null)
		{
			return 0f;
		}
		if (PlacedOnStorage is ShelfComponentInstance shelfComponentInstance)
		{
			if (!shelfComponentInstance.Blueprint.HideBeautyContent)
			{
				return Blueprint.BeautyInputOnShelf;
			}
			return 0f;
		}
		if (GetRoom() != null)
		{
			return Blueprint.BeautyInputInside;
		}
		return Blueprint.BeautyInput;
	}

	public override void OnReservationChanged(bool isReserved, IGoapAgentOwner agent)
	{
		base.OnReservationChanged(isReserved, agent);
		if (!isReserved)
		{
			reserveAll = false;
		}
	}

	protected virtual void OnHealthDepleted(object stat)
	{
		if (base.HasDisposed)
		{
			return;
		}
		if (placedOnStorage != null)
		{
			MonoSingleton<ResourcePileTracker>.Instance.ScheduleRecountPiles();
		}
		if (IsStoredOnStockpile())
		{
			string text = string.Empty;
			if (string.IsNullOrEmpty(Blueprint.BuildingBlueprintID))
			{
				if (!Blueprint.Category.HasFlag(ResourceCategory.CtgWaste))
				{
					text = ResourceUtils.GetLocalizedResourceName(Blueprint);
				}
			}
			else
			{
				text = BuildingUtils.GetLocalizedName(Blueprint.BuildingBlueprintID);
			}
			if (text != string.Empty)
			{
				MonoSingleton<BlackBarMessageController>.Instance.ShowClickableBlackBarMessage(TextFormatting.HighlightOrange(text) + " " + MonoSingleton<LocalizationController>.Instance.GetText("pile_has_decomposed"), base.WorldPosition);
			}
		}
		Stats.Controller.RemoveListener(OnHealthDepleted);
		Stats.Controller.RemoveListener(OnFreshnessDepleted);
		Stats.Controller.RemoveListener(OnFermentationDepleted);
		if (!base.HasDisposed)
		{
			Resource resource = blueprint;
			if ((object)resource != null && resource.WetnessOnDestroy > 0f)
			{
				MapNode node = GetNode();
				if (node != null)
				{
					SetWetnessAround(node, blueprint.WetnessOnDestroy, 3f);
				}
			}
			TrySpawnOil();
		}
		Dispose();
		this.DurabilityDepletedEvent?.Invoke();
	}

	private void TrySpawnOil()
	{
		if (!(blueprint.SpawnOilRadius >= 1f) || base.HasDisposed)
		{
			return;
		}
		foreach (MapNode item in FloodFillUtil.IterateFloodFillConnections(GetNode(), blueprint.SpawnOilRadius, (MapNode n) => (n.Tag & MapNodeTags.Wall) != 0 || n.VoxelType != null))
		{
			base.Map.FireSimLogic.SetOilBlobHealth(item.Index, 1f, blueprint.OilType);
		}
	}

	private void OnFreshnessDepleted(object stat)
	{
		if (!base.HasDisposed)
		{
			if (placedOnStorage != null)
			{
				MonoSingleton<ResourcePileTracker>.Instance.ScheduleRecountPiles();
			}
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(HandleRot);
		}
	}

	private void HandleRot()
	{
		if (LoadingController.IsSceneTransition || base.HasDisposed)
		{
			return;
		}
		if (IsStoredOnStockpile() && !Blueprint.Category.HasFlag(ResourceCategory.CtgWaste))
		{
			string input = (string.IsNullOrEmpty(Blueprint.RottenId) ? MonoSingleton<LocalizationController>.Instance.GetText("general_none") : ResourceUtils.GetLocalizedResourceName(Blueprint.RottenId));
			string messageText = MonoSingleton<LocalizationController>.Instance.GetText("pile_has_rotten").Replace("<resource_name>", TextFormatting.HighlightOrange(ResourceUtils.GetLocalizedResourceName(Blueprint))).Replace("<rotten_resource>", TextFormatting.HighlightOrange(input));
			MonoSingleton<BlackBarMessageController>.Instance.ShowClickableBlackBarMessage(messageText, base.WorldPosition);
			MonoSingleton<ResourcePileController>.Instance.OnPileRotten(this);
		}
		string rottenId = Blueprint.RottenId;
		if (string.IsNullOrEmpty(rottenId))
		{
			Dispose();
			return;
		}
		ResourceInstance storedResource = GetStoredResource();
		if (storedResource == null)
		{
			Dispose();
			return;
		}
		ResourceInstance newInstance = new ResourceInstance(Repository<ResourceRepository, Resource>.Instance.GetByID(rottenId), storedResource.Amount);
		newInstance.ForbidOnInit = isForbidden;
		Vector3 pos = GetPosition();
		newInstance.GetStat(StatType.Health).SetCurrent(GetStatValue(StatType.Health));
		IStorageBuilding storageBuilding = placedOnStorage as IStorageBuilding;
		if (IsPlacedOnStorageBuilding && storageBuilding != null)
		{
			foreach (UniversalStorage item in storageBuilding.AllStorage)
			{
				if (item?.StorageSlots == null || item.StorageSlots.Length == 0)
				{
					continue;
				}
				StorageSlot[] storageSlots = item.StorageSlots;
				foreach (StorageSlot storageSlot in storageSlots)
				{
					if (storageSlot.Pile == this)
					{
						Dispose();
						item.StoreResourcePile(newInstance, storageSlot);
						return;
					}
				}
			}
			Log.Warning("Something is wrong with piles rotting on storage buildings... ID: " + blueprintId + " POS: " + base.WorldPosition.ToString(), "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			Dispose();
		}
		else
		{
			Dispose();
			MonoSingleton<TaskController>.Instance.WaitFor(0.1f).Then(delegate
			{
				MonoSingleton<ResourcePileManager>.Instance.SpawnPile(newInstance, pos, newInstance.ForbidOnInit);
			});
		}
	}

	private void OnFermentationDepleted(object stat)
	{
		if (!base.HasDisposed)
		{
			if (placedOnStorage != null)
			{
				MonoSingleton<ResourcePileTracker>.Instance.ScheduleRecountPiles();
			}
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(HandleFermentation);
		}
	}

	private void HandleFermentation()
	{
		if (base.HasDisposed)
		{
			return;
		}
		if (IsStoredOnStockpile() && !Blueprint.Category.HasFlag(ResourceCategory.CtgWaste))
		{
			string input = (string.IsNullOrEmpty(Blueprint.FermentedId) ? MonoSingleton<LocalizationController>.Instance.GetText("general_none") : ResourceUtils.GetLocalizedResourceName(Blueprint.FermentedId));
			string messageText = MonoSingleton<LocalizationController>.Instance.GetText("pile_has_fermented").Replace("<resource_name>", TextFormatting.HighlightOrange(ResourceUtils.GetLocalizedResourceName(Blueprint))).Replace("<fermented_resource>", TextFormatting.HighlightOrange(input));
			MonoSingleton<BlackBarMessageController>.Instance.ShowClickableBlackBarMessage(messageText, base.WorldPosition);
		}
		string fermentedId = Blueprint.FermentedId;
		if (fermentedId.Equals(string.Empty))
		{
			Dispose();
			return;
		}
		ResourceInstance storedResource = GetStoredResource();
		if (storedResource == null)
		{
			Dispose();
			return;
		}
		ResourceInstance newInstance = new ResourceInstance(Repository<ResourceRepository, Resource>.Instance.GetByID(fermentedId), storedResource.Amount);
		Vector3 pos = GetPosition();
		newInstance.GetStat(StatType.Health).SetCurrent(GetStatValue(StatType.Health));
		IStorageBuilding storageBuilding = placedOnStorage as IStorageBuilding;
		if (IsPlacedOnStorageBuilding && storageBuilding != null)
		{
			foreach (UniversalStorage item in storageBuilding.AllStorage)
			{
				if (item?.StorageSlots == null || item.StorageSlots.Length == 0)
				{
					continue;
				}
				StorageSlot[] storageSlots = item.StorageSlots;
				foreach (StorageSlot storageSlot in storageSlots)
				{
					if (storageSlot.Pile == this)
					{
						Dispose();
						item.StoreResourcePile(newInstance, storageSlot);
						return;
					}
				}
			}
			Log.Warning("Something is wrong with piles fermenting on storage buildings... ID: " + blueprintId + " POS: " + base.WorldPosition.ToString(), "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			Dispose();
		}
		else
		{
			Dispose();
			MonoSingleton<TaskController>.Instance.WaitFor(0.1f).Then(delegate
			{
				MonoSingleton<ResourcePileManager>.Instance.SpawnPile(newInstance, pos);
			});
		}
	}

	private bool CheckShouldRefreshModifiers(WorldObject building)
	{
		if (LoadingController.IsLeavingMainScene)
		{
			return false;
		}
		if (building.Positions != null)
		{
			if (!building.Positions.Any((Vec3Int gridPos) => base.GridDataPosition.x == gridPos.x && base.GridDataPosition.z == gridPos.z))
			{
				return false;
			}
		}
		else if (base.GridDataPosition.x != building.GridDataPosition.x || base.GridDataPosition.z != building.GridDataPosition.z)
		{
			return false;
		}
		return true;
	}

	private void OnBuildingFinished(BaseBuildingInstance building)
	{
		if (building.Blueprint.PlaceableBellowOthers || building.BuildingType == BuildingType.Rug)
		{
			if (CheckShouldRefreshModifiers(building))
			{
				MonoSingleton<ResourcePileController>.Instance.StartCoroutine(RefreshModifiersDelayed());
			}
			return;
		}
		MapNode node = GetNode();
		foreach (Vec3Int position in building.Positions)
		{
			Vec3Int gridPosition = position;
			if (node == building.Map.GetNode(in gridPosition) && HandleTeleportation(GetNode()))
			{
				return;
			}
		}
		if (CheckShouldRefreshModifiers(building))
		{
			MonoSingleton<ResourcePileController>.Instance.StartCoroutine(RefreshModifiersDelayed());
		}
	}

	private void OnBuildingDestroyed(BaseBuildingInstance building)
	{
		if (CheckShouldRefreshModifiers(building))
		{
			MonoSingleton<ResourcePileController>.Instance.StartCoroutine(RefreshModifiersDelayed());
		}
	}

	private void SetCallbacks()
	{
		storage.ResourceAddedEvent += OnResourceAdded;
		storage.ResourceTakenEvent += OnResourceTaken;
	}

	private void OnResourceAdded(SimpleResourceCount count)
	{
		RefreshModifiers();
		MonoSingleton<ResourcePileTracker>.Instance.OnPileResourceAdded(this, count.Blueprint, count.Amount);
		this.OnResourceAddedEvent?.Invoke(this, count.Blueprint, count.Amount);
	}

	private void OnResourceTaken(SimpleResourceCount count)
	{
		if (storage == null)
		{
			return;
		}
		this.OnResourceTakenEvent?.Invoke(this, count.Blueprint, count.Amount);
		if (storage.IsEmpty())
		{
			Dispose();
			return;
		}
		RefreshModifiers();
		if (placedOnStorage != null)
		{
			MonoSingleton<ResourcePileHaulingManager>.Instance.ResourceTakenFromStorage(this);
		}
	}

	private bool IsOnGround()
	{
		VillageMap map = VillageManager.ActiveVillage.Map;
		Vec3Int gridPosition = base.GridDataPosition;
		MapNode node = map.GetNode(in gridPosition);
		if (node == null)
		{
			return false;
		}
		return !node.DataType.HasFlag(GridDataType.BuildingFinished);
	}

	private bool IsRoofed()
	{
		int num = GridDataIndexTools.FastTo1DIndex(base.GridDataPosition);
		if (num == -1)
		{
			return false;
		}
		if (placedOnStorage is ShelfComponentInstance shelfComponentInstance && shelfComponentInstance.Blueprint.Rainproof)
		{
			return true;
		}
		VillageMap map = base.Map;
		return ((map != null) ? map.GridSpaceData[num] : null).Coverage == CoverageType.Roofed;
	}

	private IEnumerator RefreshModifiersDelayed()
	{
		yield return 1;
		RefreshModifiers();
	}

	public void RefreshModifiers()
	{
		if (base.HasDisposed || LoadingController.IsLeavingMainScene)
		{
			return;
		}
		ResourceInstance storedResource = GetStoredResource();
		if (storedResource == null || storedResource.HasDisposed || storedResource.Stats == null)
		{
			return;
		}
		if (blueprint.DecomposeModifiers == null || (blueprint.DecomposeModifiers.GroundCoefficient <= 0f && blueprint.DecomposeModifiers.TemperatureCoefficients == null))
		{
			Stats.RemoveAttributeModifier(ModifierType.Decay, null);
			return;
		}
		if (Stats == null)
		{
			Log.Error("BUG HUNT: this.Stats is null!", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			if (storage == null)
			{
				Log.Error("BUG HUNT: this.storage is null when fetching stats in RefreshModifiers, so stats will be null; game will throw an exception.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			}
			else if (storage.ResourceCount == 0)
			{
				Log.Error("BUG HUNT: this.storage.ResourceCount is 0 when fetching stats in RefreshModifiers, so stats will be null; game will throw an exception.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			}
			else if (GetStoredResource()?.Stats == null)
			{
				Log.Error("BUG HUNT: Stored resource stats are null; game will throw an exception.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
			}
		}
		ModifierInstanceStack modifierInstanceStack = Stats.GetModifierInstanceStack(ModifierType.Decay);
		if (modifierInstanceStack != null && modifierInstanceStack.Instances?.Count > 2)
		{
			Log.Warning("More then 2 modifiers found on pile " + blueprintId + ". This should never happen!", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
		}
		bool flag = IsOnGround() && !IsPlacedOnStorageBuilding;
		bool flag2 = IsRoofed();
		bool flag3 = Blueprint.RottingModifiers != null;
		bool flag4 = Blueprint.FermentingModifiers != null;
		bool flag5 = base.Map.WaterManager.GetWaterLevelAsDepth(base.GridDataPosition) != WaterDepthLevel.None;
		if (GetNode().WaterDepthLevel == WaterDepthLevel.Low && IsPlacedOnStorageBuilding)
		{
			flag5 = false;
		}
		DecayModifier decayModifier = null;
		DecayModifier decayModifier2 = null;
		DecayModifier decayModifier3 = null;
		if (modifierInstanceStack?.Instances != null)
		{
			foreach (DecayModifier instance in modifierInstanceStack.Instances)
			{
				if (instance.AffectedAttributeType == AttributeType.DecomposeSpeed)
				{
					decayModifier = instance;
				}
				if (instance.AffectedAttributeType == AttributeType.RottingSpeed)
				{
					decayModifier2 = instance;
				}
				if (instance.AffectedAttributeType == AttributeType.FermentingSpeed)
				{
					decayModifier3 = instance;
				}
			}
		}
		bool num = decayModifier == null || decayModifier.IsOnGround != flag || decayModifier.IsUnderRoof != flag2 || decayModifier.UnderWater != flag5;
		bool flag6 = flag3 && (decayModifier2 == null || decayModifier2.IsOnGround != flag || decayModifier2.IsUnderRoof != flag2 || decayModifier2.UnderWater != flag5);
		bool flag7 = flag4 && (decayModifier3 == null || decayModifier3.IsOnGround != flag || decayModifier3.IsUnderRoof != flag2 || decayModifier3.UnderWater != flag5);
		if (num)
		{
			if (decayModifier == null)
			{
				DecayModifier modifier = new DecayModifier(AttributeType.DecomposeSpeed, Blueprint.DecomposeModifiers, base.Map, flag, flag2, flag5, "decompose");
				Stats.AddAttributeModifier(modifier);
				if (Stats == null)
				{
					return;
				}
			}
			else
			{
				if (!flag5)
				{
					decayModifier.SetIsOnGround(flag);
					decayModifier.SetUnderRoof(flag2);
				}
				else
				{
					decayModifier.SetIsOnGround(isOnGround: false);
					decayModifier.SetUnderRoof(isUnderRoof: false);
				}
				decayModifier.SetUnderWater(flag5);
			}
		}
		if (flag6)
		{
			if (decayModifier2 == null)
			{
				DecayModifier modifier2 = new DecayModifier(AttributeType.RottingSpeed, Blueprint.RottingModifiers, base.Map, flag, flag2, flag5, "rot");
				Stats.AddAttributeModifier(modifier2);
			}
			else
			{
				if (!flag5)
				{
					decayModifier2.SetIsOnGround(flag);
					decayModifier2.SetUnderRoof(flag2);
				}
				else
				{
					decayModifier2.SetIsOnGround(isOnGround: false);
					decayModifier2.SetUnderRoof(isUnderRoof: false);
				}
				decayModifier2.SetUnderWater(flag5);
			}
		}
		if (!flag7)
		{
			return;
		}
		if (decayModifier3 == null)
		{
			DecayModifier modifier3 = new DecayModifier(AttributeType.FermentingSpeed, Blueprint.FermentingModifiers, base.Map, flag, flag2, flag5, "ferment");
			Stats.AddAttributeModifier(modifier3);
			return;
		}
		if (!flag5)
		{
			decayModifier3.SetIsOnGround(flag);
			decayModifier3.SetUnderRoof(flag2);
		}
		else
		{
			decayModifier3.SetIsOnGround(isOnGround: false);
			decayModifier3.SetUnderRoof(isUnderRoof: false);
		}
		decayModifier3.SetUnderWater(flag5);
	}

	private void OnEquipOrder(ResourcePileInstance pile, HumanoidInstance humanoid, bool active)
	{
		if (active && this == pile && !base.HasDisposed)
		{
			IsForbidden = false;
			equipTarget = humanoid;
			humanoid.Inventory.AddEquipOrder(this);
		}
	}

	private void HandleFallDown(MapNode node)
	{
		if (node == null || node.IsWalkable || node.Map == null || base.GridDataPosition.y < node.Position.y || IsPlacedOnStorageBuilding)
		{
			return;
		}
		Vec3Int position = node.Position;
		for (int num = position.y - 1; num >= 0; num--)
		{
			MapNode node2 = node.Map.GetNode(position.x, num, position.z);
			if (node2 == null)
			{
				throw new Exception($"Pile fall down exception! Node not found at: {position.x}, {num}, {position.z}");
			}
			if (node2.IsWalkable)
			{
				bool forbidOnInit = IsForbidden;
				Storage storage = GetStorage();
				if (storage != null && storage.ResourceCount > 0)
				{
					ResourceInstance resource = storage.Take(GetStoredResource());
					MonoSingleton<ResourcePileManager>.Instance.SpawnPile(resource, node2.WorldPosition, forbidOnInit);
				}
				return;
			}
		}
		bool isEnabled;
		FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(100, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\Components\\ResourcePileInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Pile fall down could not find place to spawn pile. Resources lost. Pos: ");
			messageBuilder.AppendFormatted(base.GridDataPosition);
			messageBuilder.AppendLiteral(". Trying pile teleportation.");
		}
		Log.Warning(messageBuilder);
		bool forbidOnInit2 = IsForbidden;
		ResourceInstance resource2 = GetStorage().Take(GetStoredResource());
		MonoSingleton<ResourcePileManager>.Instance.TeleportPile(resource2, node.WorldPosition, forbidOnInit2);
	}

	public bool HandleTeleportation(MapNode node)
	{
		if (node.DataType.HasFlag(GridDataType.Stockpile) || node.DataType == GridDataType.None)
		{
			return false;
		}
		if (placedOnStorage is ShelfComponentInstance { HasDisposed: false, OwnerBuilding: not null } shelfComponentInstance && !shelfComponentInstance.OwnerBuilding.HasDisposed)
		{
			return false;
		}
		bool flag = false;
		foreach (WorldObject worldObject in node.WorldObjects)
		{
			if (worldObject is PlantMapResourceInstance)
			{
				flag = true;
				break;
			}
			if (worldObject is BaseBuildingInstance baseBuildingInstance && !baseBuildingInstance.Blueprint.PlaceableBellowOthers)
			{
				flag = true;
				break;
			}
		}
		if (MonoSingleton<GroundManager>.Instance.GroundExists(node.Position) || MonoSingleton<SlopeManager>.Instance.SlopeExists(node.Position))
		{
			flag = true;
		}
		if (!flag)
		{
			return false;
		}
		bool forbidOnInit = IsForbidden;
		ResourceInstance resource = GetStorage().Take(GetStoredResource());
		MonoSingleton<ResourcePileManager>.Instance.TeleportPile(resource, node.WorldPosition, forbidOnInit);
		return true;
	}

	public override void Serialize(FVSerializer serializer)
	{
		base.Serialize(serializer);
		serializer.Write("storage", storage);
		serializer.Write("isForbidden", isForbidden);
		serializer.Write("PlacedOnAnimalFeeder", PlacedOnAnimalFeeder);
		serializer.Write("PlacedOnPrisonStash", PlacedOnPrisonStash);
		serializer.Write("isUrgentHaul", isUrgentHaul);
		serializer.Write("haulingDisabledSecondsRemaining", HaulingDisabledSecondsRemaining);
	}

	public ResourcePileInstance(FVDeserializer deserializer)
		: base(deserializer)
	{
		storage = deserializer.ReadObject<Storage>("storage");
		isForbidden = deserializer.ReadBool("isForbidden");
		PlacedOnAnimalFeeder = deserializer.ReadBool("PlacedOnAnimalFeeder");
		PlacedOnPrisonStash = deserializer.ReadBool("PlacedOnPrisonStash");
		isUrgentHaul = deserializer.ReadBool("isUrgentHaul");
		HaulingDisabledSecondsRemaining = deserializer.ReadFloat("haulingDisabledSecondsRemaining");
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
