using System;
using System.Collections.Generic;
using NSEipix.Base;
using NSEipix.Model;
using NSEipix.Repository;
using NSMedieval.Construction;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Resources;
using NSMedieval.Serialization;
using NSMedieval.StatsSystem;
using NSMedieval.Types;
using NSMedieval.Views.Resources;
using NSMedieval.Village.Map;
using NSMedieval.Water;
using UnityEngine;

namespace NSMedieval.State;

[Serializable]
[FVSerializableKey("PlantMapResourceInstance", "")]
public class PlantMapResourceInstance : MapResourceInstance, IDamageTakingAgent, IDamageCommonAgent, IGoapTargetable, IGameDisposable, IDisposable
{
	[SerializeField]
	private int currentlifeCycles;

	[SerializeField]
	private int currentPhase;

	[SerializeField]
	private float currentPhaseHours;

	[SerializeField]
	private bool domestic;

	[SerializeField]
	private int harvestPhase;

	[SerializeField]
	private int cutPhase;

	[SerializeField]
	private ZonePriority priority;

	[SerializeField]
	private List<Vec3Int> positions = new List<Vec3Int>();

	[SerializeField]
	private bool isStunted;

	private Shaker shaker;

	private PlantMapResource blueprint;

	private bool disposed = true;

	private OrderType cachedPossibleOrders;

	[NonSerialized]
	private PlantMapResourceView view;

	[NonSerialized]
	private List<EquipmentInstance> equipment;

	[NonSerialized]
	private List<Vec3Int> shadowCasterPositions = new List<Vec3Int>();

	public override bool BlueprintExists => Blueprint != null;

	private PlantMapResourceView View
	{
		get
		{
			if (!base.HasDisposed && view == null)
			{
				view = MonoSingleton<PlantResourceManager>.Instance.GetView(this);
			}
			return view;
		}
	}

	public override List<Vec3Int> Positions => positions;

	public int CurrentPhase => currentPhase;

	public PlantLifePhaseType CurrentPhaseType => Blueprint.LifePhases[currentPhase].PhaseType;

	public bool Domestic => domestic;

	public ZonePriority Priority => priority;

	public int HarvestPhase => harvestPhase;

	public PlantMapResource Blueprint => blueprint = ((blueprint == null) ? Repository<PlantMapResourceRepository, PlantMapResource>.Instance.GetByID(blueprintId) : blueprint);

	public bool GrowthHaltedTemperature { get; private set; }

	public bool HasDied => base.HasDisposed;

	public bool IsDyingLowTemperature { get; private set; }

	public bool IsDyingHighTemperature { get; private set; }

	public bool IsDyingLowSunlight { get; private set; }

	public bool IsDyingFlooded { get; private set; }

	public bool IsDyingNoWater { get; private set; }

	public bool IsStunted => isStunted;

	public List<Vec3Int> ShadowCasterPositions => shadowCasterPositions;

	public override float Flammability
	{
		get
		{
			if (currentPhase != -1)
			{
				return Blueprint.LifePhases[currentPhase].Flammability;
			}
			if (Blueprint.Flammability >= 0f)
			{
				return Blueprint.Flammability;
			}
			return 0f;
		}
	}

	internal override ThermalModel ThermalModel
	{
		get
		{
			if (HasDied || base.HasDisposed)
			{
				return null;
			}
			if (currentPhase == -1 || currentPhase >= Blueprint.LifePhases.Count)
			{
				return null;
			}
			return Blueprint.LifePhases[currentPhase].ThermalModel;
		}
	}

	public bool HasActivePath => false;

	public DamageTakingAgentType DamageAgentType => DamageTakingAgentType.Plant;

	public event Action EnterBurntPhaseEvent;

	public PlantMapResourceInstance(PlantMapResource blueprint, string prefabId, Vector3 worldPosition, bool domestic, int currentPhase = 0, bool randomPhaseHours = false)
		: base(blueprint.GetID(), prefabId, worldPosition, GridDataType.PlantMapResource)
	{
		SetPositions();
		disposed = false;
		this.blueprint = blueprint;
		this.currentPhase = currentPhase;
		currentPhaseHours = (randomPhaseHours ? UnityEngine.Random.Range(0, (int)Blueprint.LifePhases[this.currentPhase].DurationHours()) : 0);
		currentlifeCycles = -1;
		this.domestic = domestic;
		harvestPhase = Blueprint.HarvestPhase;
		UpdateCachedOrders();
		SetStats(ResourceStatsProducer.ProduceMapResourceStats(this, blueprint.LifePhases[currentPhase].BaseHealth));
		MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent += new Action(OnHourUpdate);
		MonoSingleton<WorldTimeManager>.Instance.SeasonUpdateEvent += new Action(OnSeasonUpdate);
		MonoSingleton<FloraRegrowController>.Instance.OnPlantInstantiated(this);
		equipment = new List<EquipmentInstance>();
		MonoSingleton<CombatAgentManager>.Instance.RegisterCommonCombatAgent(this);
		base.Stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Health, HealthDepletedListener);
		base.Stats.Controller.RegisterListener(StatEventType.ValueUpdated, StatType.Health, HealthChangedListener);
		ForceRefreshShadowCasterInput();
		MonoSingleton<FixedCountTicker<PlantMapResourceInstance>>.Instance.Attach(this, OnPlantUpdate);
	}

	public override MapResource GetBlueprint()
	{
		return Blueprint;
	}

	public void SetDomestic(bool domestic)
	{
		this.domestic = domestic;
	}

	public PlantAppearance GetAppearance()
	{
		if (Blueprint == null || Blueprint.LifePhases == null || Blueprint.LifePhases.Count <= currentPhase || currentPhase < 0)
		{
			return null;
		}
		return Blueprint.LifePhases[currentPhase]?.Appearance;
	}

	public void SetStunted(bool stunted)
	{
		isStunted = stunted;
		ForceRefreshShadowCasterInput();
	}

	public PlantAppearance GetStuntedAppearance()
	{
		return Blueprint?.StuntedAppearance;
	}

	public override OrderType GetPossibleOrders()
	{
		if (currentPhase != -1)
		{
			return cachedPossibleOrders;
		}
		return OrderType.None;
	}

	public override bool OnOrderFail(OrderType order)
	{
		return true;
	}

	public override HarvestParametars GetMiningParameters()
	{
		return Blueprint?.GetMiningParameters();
	}

	public override List<ResourceInstance> GetAvailableResources(OrderType orders = OrderType.None)
	{
		if (currentPhase == -1)
		{
			return null;
		}
		List<ResourceInstance> list = new List<ResourceInstance>();
		for (int i = 0; i < Blueprint.StorableResources.Count; i++)
		{
			if (Blueprint.LifePhases[currentPhase].ResourcesRange[i].Max > 0 && (Blueprint.StorableResources[i].Orders & orders) != 0)
			{
				IntRange intRange = Blueprint.LifePhases[currentPhase].ResourcesRange[i];
				string resourceId = Blueprint.StorableResources[i].ResourceId;
				Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(resourceId);
				list.Add(new ResourceInstance(byID, (int)((float)(intRange.Max - intRange.Min) * GetPhaseProgress()) + intRange.Min));
			}
		}
		return list;
	}

	public override float GetBeautyInput()
	{
		if (currentPhase >= 0 && currentPhase < Blueprint.LifePhases.Count)
		{
			return Blueprint.LifePhases[currentPhase].BeautyInput;
		}
		return 0f;
	}

	public void Harvested()
	{
		MonoSingleton<ReservationManager>.Instance.ReleaseAll(this);
		if (Blueprint.DestroyOnHarvest)
		{
			StartPhase(-1);
		}
		else
		{
			StartPhase(GetNewPhaseIndex(harvested: true));
		}
	}

	public override void ReInstantiate()
	{
		MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(OnHourUpdate);
		MonoSingleton<WorldTimeManager>.Instance.SeasonUpdateEvent -= new Action(OnSeasonUpdate);
		MonoSingleton<FloraRegrowController>.Instance.OnPlantInstantiated(this);
		ResourceStatsProducer.ProduceMapResourceStats(this, Blueprint.LifePhases[currentPhase].BaseHealth, base.Stats);
		base.ReInstantiate();
		SetPositions();
		disposed = false;
		MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent += new Action(OnHourUpdate);
		MonoSingleton<WorldTimeManager>.Instance.SeasonUpdateEvent += new Action(OnSeasonUpdate);
		base.Stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Health, HealthDepletedListener);
		base.Stats.Controller.RegisterListener(StatEventType.ValueUpdated, StatType.Health, HealthChangedListener);
		UpdateCachedOrders();
		SetCurrentOrder(base.CurrentOrder, afterLoading: true);
		MonoSingleton<CombatAgentManager>.Instance.RegisterCommonCombatAgent(this);
		MonoSingleton<FixedCountTicker<PlantMapResourceInstance>>.Instance.Attach(this, OnPlantUpdate);
		ForceRefreshShadowCasterInput();
	}

	public float GetPhaseProgress()
	{
		return currentPhaseHours / Blueprint.LifePhases[currentPhase].DurationHours();
	}

	public void SetPhaseProgress(float phasePercentage)
	{
		float b = Blueprint.LifePhases[currentPhase].DurationHours();
		currentPhaseHours = Mathf.Lerp(0f, b, Mathf.Clamp01(phasePercentage));
	}

	public int GetHoursUntilHarvest()
	{
		float num = 0f;
		float num2 = Blueprint.LifePhases[CurrentPhase].DurationDays * GetPhaseProgress();
		for (int i = 0; i < GetHarvestPhaseIndex(); i++)
		{
			num += Blueprint.LifePhases[i].DurationDays;
			if (i < CurrentPhase)
			{
				num2 += Blueprint.LifePhases[i].DurationDays;
			}
		}
		return (int)((num - num2) * (float)GlobalSaveController.CurrentVillageData.DateAndTime.HoursInDay);
	}

	public void SetLastPhase()
	{
		if (currentPhase != -1)
		{
			StartPhase(-1);
		}
	}

	public override void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			if (MonoSingleton<FixedCountTicker<PlantMapResourceInstance>>.IsInstantiated())
			{
				MonoSingleton<FixedCountTicker<PlantMapResourceInstance>>.Instance.Detach(this);
			}
			if (MonoSingleton<CombatAgentManager>.IsInstantiated())
			{
				MonoSingleton<CombatAgentManager>.Instance.RemoveCommonCombatAgent(this);
			}
			if (MonoSingleton<FloraRegrowController>.IsInstantiated())
			{
				MonoSingleton<FloraRegrowController>.Instance.OnPlantDestroyed(this);
			}
			if (MonoSingleton<FloraController>.IsInstantiated())
			{
				MonoSingleton<FloraController>.Instance.DestroyResource(this);
			}
			if (MonoSingleton<WorldTimeManager>.IsInstantiated())
			{
				MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(OnHourUpdate);
				MonoSingleton<WorldTimeManager>.Instance.SeasonUpdateEvent -= new Action(OnSeasonUpdate);
			}
			base.Map.EnemyBuildingsManager.RemovePlantToChop(this);
			base.Stats.Controller.RemoveListener(HealthDepletedListener);
			base.Stats.Controller.RemoveListener(HealthChangedListener);
			ForceRefreshShadowCasterInput();
			this.EnterBurntPhaseEvent = null;
			equipment?.Clear();
			equipment = null;
			shaker = null;
			base.Dispose();
			view = null;
		}
	}

	private void HealthDepletedListener(object stat)
	{
		OnHealthDepleted();
	}

	private void HealthChangedListener(object stat)
	{
		OnHealthChanged();
	}

	public void RemovedFromCropfield()
	{
		SetHarvestPhase(-1);
		SetCutPhase(-1);
		SetDomestic(domestic: false);
	}

	public void SetPriority(ZonePriority priority)
	{
		this.priority = priority;
	}

	public void SetPositions()
	{
		if (positions == null)
		{
			positions = new List<Vec3Int>();
		}
		positions.Clear();
		positions.Add(base.GridDataPosition);
	}

	public int GetHarvestPhaseIndex()
	{
		List<PlantLifePhases> lifePhases = Blueprint.LifePhases;
		int result = 0;
		int num = 0;
		for (int i = 0; i < lifePhases.Count; i++)
		{
			if (lifePhases[i].ResourcesRange.Count <= 0)
			{
				return result;
			}
			int max = lifePhases[i].ResourcesRange[0].Max;
			if (max >= num)
			{
				num = max;
				result = i;
			}
		}
		return result;
	}

	public void SetHarvestPhase(int harvestPhase)
	{
		if (Blueprint.HarvestPhase < 0)
		{
			this.harvestPhase = Blueprint.HarvestPhase;
			return;
		}
		this.harvestPhase = harvestPhase;
		if (domestic)
		{
			CheckForAutoOrders();
		}
	}

	public void SetCutPhase(int cutPhase)
	{
		if (Blueprint.CutPhase < 0)
		{
			this.cutPhase = Blueprint.CutPhase;
			return;
		}
		this.cutPhase = cutPhase;
		if (domestic)
		{
			CheckForAutoOrders();
		}
	}

	public void ForceHarvestPhase()
	{
		StartPhase(harvestPhase);
	}

	public void ForceCutPhase()
	{
		StartPhase(cutPhase);
	}

	public bool CanAutoHarvest()
	{
		if (domestic && harvestPhase > 0 && Blueprint.LifePhases[harvestPhase].IsHarvestablePhase)
		{
			return currentPhase == harvestPhase;
		}
		return false;
	}

	public bool CanAutoCut()
	{
		if (domestic && cutPhase > 0 && Blueprint.LifePhases[cutPhase].IsCutPhase)
		{
			return currentPhase == cutPhase;
		}
		return false;
	}

	private void CheckForAutoOrders()
	{
		if (!domestic || base.PlayerOrder)
		{
			return;
		}
		if (harvestPhase >= 0 && harvestPhase < Blueprint.LifePhases.Count)
		{
			if (Blueprint.LifePhases[harvestPhase].IsHarvestablePhase && currentPhase == harvestPhase)
			{
				base.SetCurrentOrder(OrderType.Harvesting);
			}
			else
			{
				base.SetCurrentOrder(base.CurrentOrder & ~OrderType.Harvesting);
			}
		}
		if (cutPhase >= 0 && cutPhase < Blueprint.LifePhases.Count)
		{
			if (Blueprint.LifePhases[cutPhase].IsCutPhase && currentPhase == cutPhase)
			{
				base.SetCurrentOrder(OrderType.CutAllVegetation);
			}
			else
			{
				base.SetCurrentOrder(base.CurrentOrder & ~OrderType.CutAllVegetation);
			}
		}
	}

	public void StartNextPhaseDebug()
	{
		if (currentPhase != -1)
		{
			StartPhase(GetNewPhaseIndex());
		}
	}

	public void StartMaturePhaseDebug()
	{
		if (currentPhase == -1)
		{
			return;
		}
		PlantLifePhases plantLifePhases = null;
		foreach (PlantLifePhases lifePhase in Blueprint.LifePhases)
		{
			LocKeys[] locKeys = lifePhase.LocKeys;
			foreach (LocKeys locKeys2 in locKeys)
			{
				if (locKeys2.Name.ToLower().Contains("mature") || locKeys2.Name.ToLower().Contains("ripe"))
				{
					plantLifePhases = lifePhase;
					break;
				}
			}
		}
		if (plantLifePhases != null)
		{
			StartPhase(blueprint.LifePhases.IndexOf(plantLifePhases));
		}
	}

	public List<EquipmentInstance> GetEquipment()
	{
		return equipment;
	}

	public Transform GetTransform()
	{
		if (View == null)
		{
			return null;
		}
		return View.transform;
	}

	public override void SetCurrentOrder(OrderType newOrder, bool afterLoading = false)
	{
		if ((newOrder & (newOrder - 1)) != 0)
		{
			if (newOrder.HasFlag(base.CurrentOrder))
			{
				return;
			}
			OrderType[] orderTypes = EnumValues.OrderTypes;
			foreach (OrderType orderType in orderTypes)
			{
				if ((newOrder & orderType) != 0)
				{
					newOrder = orderType;
					break;
				}
			}
		}
		base.SetCurrentOrder(newOrder, afterLoading);
		if (base.CurrentOrder == OrderType.None && !base.PlayerOrder)
		{
			CheckForAutoOrders();
		}
	}

	private int GetNewPhaseIndex(bool harvested = false)
	{
		currentPhaseHours = 0f;
		int result = (harvested ? Blueprint.CycleStarterIndex : Blueprint.LifePhases[currentPhase].NextPhaseIndex);
		if (!result.Equals(Blueprint.CycleStarterIndex))
		{
			return result;
		}
		if (currentlifeCycles + 1 >= Blueprint.LifeCyclesCount)
		{
			return Blueprint.LifePhases[currentPhase].DeathPhaseIndex;
		}
		return result;
	}

	private void StartPhase(int phaseIndex)
	{
		if (currentPhase == -1)
		{
			return;
		}
		if (phaseIndex == -1 || Blueprint.LifePhases[phaseIndex].BaseHealth <= 0f)
		{
			currentPhase = -1;
			Dispose();
			return;
		}
		CheckSetStunted();
		float baseHealth = Blueprint.LifePhases[phaseIndex].BaseHealth;
		StatInstance stat = GetStat(StatType.Health);
		if (stat != null && Math.Abs(stat.Max - baseHealth) > 0.001f)
		{
			float normalizedPercentage = stat.GetNormalizedPercentage();
			Stat durabilityBlueprintMapResource = ResourceStatsProducer.GetDurabilityBlueprintMapResource(this, baseHealth);
			stat.SetBlueprint(durabilityBlueprintMapResource);
			stat.SetNormalizedPercentage(normalizedPercentage);
		}
		if (currentPhase != phaseIndex)
		{
			currentPhase = phaseIndex;
			GetNode()?.ForceRefreshBeautyInput();
			ForceRefreshShadowCasterInput();
		}
		currentPhaseHours = 0f;
		if (currentPhase.Equals(Blueprint.CycleStarterIndex))
		{
			currentlifeCycles++;
		}
		MonoSingleton<FloraController>.Instance.ChangeLifePhase(this);
		UpdateCachedOrders();
		CheckForAutoOrders();
	}

	private void UpdateCachedOrders()
	{
		if (Blueprint.PlantType != PlantType.Tree)
		{
			cachedPossibleOrders |= OrderType.CutAllVegetation;
		}
		for (int i = 0; i < Blueprint.StorableResources.Count; i++)
		{
			if (Blueprint.LifePhases[currentPhase].ResourcesRange[i].Max > 0)
			{
				cachedPossibleOrders |= Blueprint.StorableResources[i].Orders;
			}
		}
	}

	private void CheckSetStunted()
	{
		if (!Blueprint.UseSunlight || isStunted || currentPhase < 0 || currentPhase >= Blueprint.LifePhases.Count || !Blueprint.LifePhases[currentPhase].TurningPhase)
		{
			return;
		}
		if ((double)base.Stats.GetStat(StatType.SunLight).GetNormalizedPercentage() < Blueprint.StuntedSunlightThreshold)
		{
			isStunted = true;
		}
		if (!isStunted && Blueprint.StuntedIfBlockedByAbove && CheckBlockingGrowthAbove(GetNode().GetNodeAbove()))
		{
			isStunted = true;
		}
		if (isStunted || !Blueprint.StuntedIfBlockedBySides)
		{
			return;
		}
		int num = 0;
		MapNode node = GetNode();
		if (node == null)
		{
			return;
		}
		foreach (MapNode neighbour in node.GetNeighbours())
		{
			if (neighbour.Position.y == node.Position.y && CheckBlockingGrowthSides(neighbour))
			{
				num++;
				if (num >= 3)
				{
					isStunted = true;
					break;
				}
			}
		}
		static bool CheckBlockingGrowthAbove(MapNode mapNode)
		{
			if (mapNode != null)
			{
				if (!(mapNode.VoxelType != null) && (mapNode.Tag & (MapNodeTags.Wall | MapNodeTags.Floor)) == 0)
				{
					return (mapNode.DataType & GridDataType.Roof) != 0;
				}
				return true;
			}
			return false;
		}
		static bool CheckBlockingGrowthSides(MapNode mapNode)
		{
			if (mapNode != null && (mapNode.VoxelType != null || (mapNode.Tag & MapNodeTags.Wall) != 0 || (mapNode.DataType & GridDataType.Roof) != 0))
			{
				return true;
			}
			if ((mapNode.DataType & GridDataType.PlantMapResource) != 0)
			{
				PlantMapResourceInstance plantMapResourceInstance = (PlantMapResourceInstance)mapNode.GetWorldObject(GridDataType.PlantMapResource);
				if (plantMapResourceInstance != null && plantMapResourceInstance.Blueprint != null && plantMapResourceInstance.Blueprint.PlantType == PlantType.Tree)
				{
					return true;
				}
			}
			return false;
		}
	}

	private PlantShape GetTempShadowCaster(PlantLifePhases currentPhase)
	{
		bool flag = GlobalSaveController.CurrentVillageData.DateAndTime.Season.Index == 3;
		if (flag && isStunted && currentPhase.TempShadowCasterWinterStunted != null)
		{
			return currentPhase.TempShadowCasterWinterStunted;
		}
		if (!flag && isStunted && currentPhase.TempShadowCasterStunted != null)
		{
			return currentPhase.TempShadowCasterStunted;
		}
		if (flag && !isStunted && currentPhase.TempShadowCasterWinter != null)
		{
			return currentPhase.TempShadowCasterWinter;
		}
		return currentPhase.TempShadowCaster;
	}

	private void GenerateShadowCasterPositions()
	{
		shadowCasterPositions.Clear();
		if (disposed || HasDied || currentPhase < 0 || currentPhase >= Blueprint.LifePhases.Count)
		{
			return;
		}
		PlantLifePhases plantLifePhases = Blueprint.LifePhases[currentPhase];
		if (plantLifePhases == null)
		{
			return;
		}
		PlantShape tempShadowCaster = GetTempShadowCaster(plantLifePhases);
		if (tempShadowCaster == null || tempShadowCaster.Positions == null)
		{
			return;
		}
		foreach (Vec3Int position in tempShadowCaster.Positions)
		{
			Vec3Int b = position;
			List<Vec3Int> list = shadowCasterPositions;
			Vec3Int a = base.GridDataPosition;
			list.Add(a + b);
		}
	}

	private void RemoveShadowCaster()
	{
		foreach (Vec3Int shadowCasterPosition in shadowCasterPositions)
		{
			Vec3Int gridPosition = shadowCasterPosition;
			MapNode mapNode = base.Map?.GetNode(in gridPosition);
			if (mapNode != null)
			{
				mapNode.RemoveShadowCasterPlant(this);
				mapNode.ForceRefreshShadowCasterData();
				mapNode.ForceRefreshFlammability();
			}
		}
	}

	public void ForceRefreshShadowCasterInput()
	{
		if (shadowCasterPositions == null)
		{
			shadowCasterPositions = new List<Vec3Int>();
		}
		RemoveShadowCaster();
		GenerateShadowCasterPositions();
		foreach (Vec3Int shadowCasterPosition in shadowCasterPositions)
		{
			Vec3Int gridPosition = shadowCasterPosition;
			MapNode node = base.Map.GetNode(in gridPosition);
			if (node != null)
			{
				node.AddShadowCasterPlant(this);
				node.ForceRefreshShadowCasterData();
				node.ForceRefreshFlammability();
			}
		}
	}

	public bool IsDyingByTemperature()
	{
		if (base.Map?.TemperatureManager == null || currentPhase < 0 || Blueprint == null || Blueprint.LifePhases == null || currentPhase >= Blueprint.LifePhases.Count)
		{
			return false;
		}
		return base.Map.TemperatureManager.GetTemperature(base.GridDataPosition) < Blueprint.LifePhases[currentPhase].TemperatureFreeze;
	}

	protected override void CalculateReachability(Func<MapNode, bool> additionalCheck = null)
	{
		base.CalculateReachability((MapNode node) => node != null && (node.DataType & GridDataType.SlopeOrStairs) == 0);
	}

	public override bool DestroyByFire()
	{
		base.DestroyByFire();
		if (Blueprint.SpawnPileAmountWhenDestroyedByFire > 0 && Blueprint.SpawnPileWhenDestroyedByFire != null)
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(Blueprint.SpawnPileWhenDestroyedByFire);
			if (byID != null)
			{
				MonoSingleton<ResourcePileManager>.Instance.SpawnPile(new ResourceInstance(byID, Blueprint.SpawnPileAmountWhenDestroyedByFire), GetPosition(), forbidOnInit: true);
			}
		}
		if (currentPhase != -1 && currentPhase != Blueprint.LifePhases[currentPhase].DeathPhaseIndexFire && Blueprint.LifePhases[currentPhase].DeathPhaseIndexFire >= 0)
		{
			StartPhase(Blueprint.LifePhases[currentPhase].DeathPhaseIndexFire);
			StatInstance stat = base.Stats.GetStat(StatType.Health);
			stat?.SetCurrent(stat.Max);
			this.EnterBurntPhaseEvent?.Invoke();
			return false;
		}
		return true;
	}

	private void OnHourUpdate()
	{
		if (Blueprint == null)
		{
			return;
		}
		if (CurrentPhase >= Blueprint.LifePhases.Count)
		{
			SetLastPhase();
		}
		else
		{
			if (currentPhase == -1 || base.Map?.TemperatureManager == null)
			{
				return;
			}
			float temperature = base.Map.TemperatureManager.GetTemperature(base.GridDataPosition);
			if (temperature < Blueprint.LifePhases[currentPhase].TemperatureFreeze)
			{
				return;
			}
			if (temperature < Blueprint.PhaseSkipTemperature)
			{
				StartPhase(Blueprint.CycleStarterIndex);
				return;
			}
			if (!GrowthHaltedTemperature)
			{
				PlantLifePhases plantLifePhases = Blueprint.LifePhases[currentPhase];
				float num = 1f;
				if (plantLifePhases != null && plantLifePhases.UseSunLight && GetSunIntensity() <= 0f)
				{
					num = Blueprint.NoLightGrowSpeed;
				}
				currentPhaseHours += num;
			}
			if (GetPhaseProgress() >= 1f)
			{
				StartPhase(GetNewPhaseIndex());
			}
		}
	}

	private void OnPlantUpdate()
	{
		if (!base.HasDisposed && !(Blueprint == null) && currentPhase != -1 && base.Map?.TemperatureManager != null)
		{
			_ = IsDyingLowSunlight;
			bool isDyingLowSunlight = false;
			if (Blueprint.UseSunlight && currentPhase >= 0 && currentPhase < Blueprint.LifePhases.Count && Blueprint.LifePhases[currentPhase].UseSunLight)
			{
				isDyingLowSunlight = base.Stats.GetStat(StatType.SunLight).Current <= 0f;
			}
			IsDyingLowSunlight = isDyingLowSunlight;
			float temperature = base.Map.TemperatureManager.GetTemperature(base.GridDataPosition);
			_ = IsDyingLowTemperature;
			IsDyingLowTemperature = temperature < Blueprint.LifePhases[currentPhase].TemperatureDeath;
			_ = IsDyingHighTemperature;
			IsDyingHighTemperature = temperature > Blueprint.TemperatureOverheat;
			_ = IsDyingFlooded;
			_ = IsDyingNoWater;
			WaterDepthLevel waterLevelAsDepth = base.Map.WaterManager.GetWaterLevelAsDepth(base.GridDataPosition);
			float hpLossOnWaterLevel = Blueprint.GetHpLossOnWaterLevel((int)waterLevelAsDepth);
			IsDyingFlooded = false;
			IsDyingNoWater = false;
			if (hpLossOnWaterLevel > 0f)
			{
				IsDyingFlooded = Blueprint.GetIsLosingHpFlooded(waterLevelAsDepth);
				IsDyingNoWater = !Blueprint.GetIsLosingHpFlooded(waterLevelAsDepth);
			}
			_ = GrowthHaltedTemperature;
			GrowthHaltedTemperature = temperature < Blueprint.LifePhases[currentPhase].TemperatureFreeze;
		}
	}

	private void OnSeasonUpdate()
	{
		PlantLifePhases plantLifePhases = Blueprint.LifePhases[currentPhase];
		if (plantLifePhases != null && !(plantLifePhases.TempShadowCasterWinter == null))
		{
			ForceRefreshShadowCasterInput();
		}
	}

	private void OnHealthDepleted()
	{
		SetLastPhase();
	}

	private void OnHealthChanged()
	{
		if (IsDyingFlooded || IsDyingLowSunlight || IsDyingLowTemperature || IsDyingHighTemperature || IsDyingNoWater || base.HasDisposed || base.Stats == null || base.Stats.HasDisposed)
		{
			return;
		}
		StatInstance stat = base.Stats.GetStat(StatType.Health);
		if (stat != null && !stat.DisableShaker)
		{
			if (shaker == null && GetTransform().GetComponent<Shaker>() != null)
			{
				shaker = GetTransform().GetComponent<Shaker>();
			}
			shaker.Shake(base.Stats, View);
		}
	}

	private void DyingLowTempDecreaseHealth()
	{
	}

	public override void Serialize(FVSerializer serializer)
	{
		base.Serialize(serializer);
		serializer.Write("currentlifeCycles", currentlifeCycles);
		serializer.Write("currentPhase", currentPhase);
		serializer.Write("currentPhaseHours", currentPhaseHours);
		serializer.Write("domestic", domestic);
		serializer.Write("harvestPhase", harvestPhase);
		serializer.Write("cutPhase", cutPhase);
		serializer.WriteEnum("priority", priority);
		serializer.Write("positions", positions);
		serializer.Write("isStunted", isStunted);
	}

	public PlantMapResourceInstance(FVDeserializer deserializer)
		: base(deserializer)
	{
		currentlifeCycles = deserializer.ReadInt("currentlifeCycles");
		currentPhase = deserializer.ReadInt("currentPhase");
		currentPhaseHours = deserializer.ReadFloat("currentPhaseHours");
		domestic = deserializer.ReadBool("domestic");
		harvestPhase = deserializer.ReadInt("harvestPhase");
		cutPhase = deserializer.ReadInt("cutPhase");
		priority = deserializer.ReadEnum("priority", ZonePriority.None);
		positions = deserializer.ReadObjectList<Vec3Int>("positions");
		isStunted = deserializer.ReadBool("isStunted");
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
