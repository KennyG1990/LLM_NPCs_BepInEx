using System;
using System.Collections.Generic;
using Managers.Selection.EventData;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Resources;
using NSMedieval.Serialization;
using NSMedieval.StatsSystem;
using NSMedieval.Types;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using NSMedieval.Water;
using UnityEngine;

namespace NSMedieval.State;

[Serializable]
[FVSerializableKey("MapResourceInstance", "")]
public abstract class MapResourceInstance : WorldObject, IStatsOwner, IGameDisposable, IDisposable
{
	[SerializeField]
	private string prefabId;

	[SerializeField]
	private StatsInstance stats;

	[SerializeField]
	private OrderType currentOrder;

	[SerializeField]
	private bool playerOrder;

	public override bool BlueprintExists => GetBlueprint() != null;

	public override ushort PathfindingPenalty => GetBlueprint().PathfindingPenalty;

	public override float WalkSpeedMultiplier => GetBlueprint().WalkSpeedMultiplier;

	public WaterDepthLevel WaterDepthLevel => base.Map.WaterManager.GetWaterLevelAsDepth(base.GridDataPosition);

	public GameObject Prefab => MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByAddress(prefabId);

	public StatsInstance Stats => stats;

	public OrderType CurrentOrder => currentOrder;

	public bool PlayerOrder => playerOrder;

	protected MapResourceInstance(string id, string prefabId, Vector3 worldPosition, GridDataType dataType)
		: base(WorldObjectType.MapResource, worldPosition, Vec3Int.one, 0f, dataType)
	{
		blueprintId = id;
		SetPrefabId(prefabId);
	}

	public void SetPrefabId(string prefabId)
	{
		this.prefabId = prefabId;
	}

	public override void Dispose()
	{
		if (!base.HasDisposed)
		{
			if (MonoSingleton<PlantResourceManager>.IsInstantiated() && MonoSingleton<PlantResourceManager>.Instance.ResourcesWithOrders.TryGetValue(currentOrder, out var value))
			{
				value.Remove(this);
			}
			SetCurrentOrder(OrderType.None);
			stats = null;
			base.Dispose();
		}
	}

	public virtual bool OnOrderFail(OrderType order)
	{
		return false;
	}

	public virtual bool ShouldFailHarvest(HarvestParametars parameters, IHarvestAgent harvestAgent)
	{
		AttributeType failStat = parameters.FailStat;
		float attributeValue = harvestAgent.GetAttributeValue(AttributeType.Clumsiness);
		return UnityEngine.Random.Range(0f, 1f) / attributeValue < harvestAgent.GetAttributeValue(failStat);
	}

	public abstract MapResource GetBlueprint();

	public NSMedieval.StatsSystem.Attribute GetAttributeOverride(AttributeType type)
	{
		return null;
	}

	public void SetPlayerOrder(bool playerOrder)
	{
		this.playerOrder = playerOrder;
	}

	public virtual void SetCurrentOrder(OrderType newOrder, bool afterLoading = false)
	{
		if (newOrder == OrderType.Cancel)
		{
			newOrder = OrderType.None;
		}
		if (currentOrder == newOrder && !afterLoading)
		{
			return;
		}
		Dictionary<OrderType, HashSet<MapResourceInstance>> resourcesWithOrders = MonoSingleton<PlantResourceManager>.Instance.ResourcesWithOrders;
		if (currentOrder != 0)
		{
			if ((currentOrder & (currentOrder - 1)) == 0)
			{
				if (resourcesWithOrders.TryGetValue(currentOrder, out var value))
				{
					value.Remove(this);
				}
			}
			else
			{
				OrderType[] orderTypes = EnumValues.OrderTypes;
				foreach (OrderType orderType in orderTypes)
				{
					if (CurrentOrder.HasFlag(orderType) && orderType != 0 && resourcesWithOrders.TryGetValue(orderType, out var value2))
					{
						value2.Remove(this);
					}
				}
			}
		}
		currentOrder = newOrder;
		if (currentOrder != 0)
		{
			if ((currentOrder & (currentOrder - 1)) == 0)
			{
				if (resourcesWithOrders.TryGetValue(currentOrder, out var value3))
				{
					value3.Add(this);
				}
			}
			else
			{
				OrderType[] orderTypes = EnumValues.OrderTypes;
				foreach (OrderType orderType2 in orderTypes)
				{
					if (CurrentOrder.HasFlag(orderType2) && orderType2 != 0 && resourcesWithOrders.TryGetValue(orderType2, out var value4))
					{
						value4.Add(this);
					}
				}
			}
		}
		base.GridDataType = WorldObjectTemporaryDataTypeSwitcher.GetWorldObjectDataType(this);
		MonoSingleton<ResourceCommonController>.Instance.OnOrderChanged(this);
		if (CurrentOrder == OrderType.None)
		{
			MonoSingleton<ReservationManager>.Instance.ReleaseAll(this);
		}
	}

	public override void ReInstantiate()
	{
		if (Stats.Owner == null)
		{
			Stats.SetOwner(this);
		}
		Stats.Initialize();
		base.ReInstantiate();
	}

	public abstract OrderType GetPossibleOrders();

	public abstract HarvestParametars GetMiningParameters();

	public abstract List<ResourceInstance> GetAvailableResources(OrderType orders = OrderType.None);

	public StatInstance GetStat(StatType type)
	{
		return Stats.GetStat(type);
	}

	public bool OrderShouldHighlightMe(OrderEventData eventData)
	{
		if (eventData.OrderType == OrderType.Cancel && CurrentOrder != 0)
		{
			return true;
		}
		OrderType possibleOrders = GetPossibleOrders();
		if (possibleOrders.HasFlag(eventData.OrderType))
		{
			return CurrentOrder != eventData.OrderType;
		}
		return false;
	}

	public bool IsRoofed()
	{
		VillageMap map = base.Map;
		object obj;
		if (map == null)
		{
			obj = null;
		}
		else
		{
			Vec3Int gridPosition = base.GridDataPosition;
			obj = map.GetNode(in gridPosition);
		}
		MapNode mapNode = (MapNode)obj;
		if (mapNode == null)
		{
			return false;
		}
		return mapNode.Coverage == CoverageType.Roofed;
	}

	public float GetSunIntensity()
	{
		return base.Map.TemperatureManager.GetLightIntensity(base.GridDataPosition);
	}

	public void CloneStatsCurrent(StatsInstance stats)
	{
		foreach (KeyValuePair<StatType, StatInstance> stat in stats.Stats)
		{
			GetStat(stat.Key)?.SetCurrent(stat.Value.Current);
		}
	}

	public float GetStatValue(StatType type)
	{
		return Stats.GetStat(type)?.Current ?? 0f;
	}

	protected void SetStats(StatsInstance stats)
	{
		this.stats = stats;
	}

	public override void Serialize(FVSerializer serializer)
	{
		base.Serialize(serializer);
		serializer.Write("prefabId", prefabId);
		serializer.Write("stats", stats);
		serializer.WriteEnum("currentOrder", currentOrder);
		serializer.Write("playerOrder", playerOrder);
	}

	public MapResourceInstance(FVDeserializer deserializer)
		: base(deserializer)
	{
		prefabId = deserializer.ReadString("prefabId");
		stats = deserializer.ReadObject<StatsInstance>("stats");
		currentOrder = deserializer.ReadEnum("currentOrder", OrderType.None);
		playerOrder = deserializer.ReadBool("playerOrder");
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
