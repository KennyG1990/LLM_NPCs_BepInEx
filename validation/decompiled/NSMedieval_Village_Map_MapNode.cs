using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FoxyVoxel.Collections;
using FoxyVoxel.Logging;
using JetBrains.Annotations;
using NSEipix;
using NSEipix.Base;
using NSMedieval.BuildingComponents;
using NSMedieval.Construction;
using NSMedieval.Controllers;
using NSMedieval.Enums;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.Model.MapNew;
using NSMedieval.Repository;
using NSMedieval.State;
using NSMedieval.Tools;
using NSMedieval.Types;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Water;
using Unity.Mathematics;
using UnityEngine;

namespace NSMedieval.Village.Map;

[Serializable]
public class MapNode
{
	public delegate bool ConnectionSearchOperation(MapNode node);

	public static bool RefreshEnabled;

	public static bool RefreshFlammabilityEnabled;

	private static readonly WorldDirection[] NonDiagonalDirections = new WorldDirection[6]
	{
		WorldDirection.N,
		WorldDirection.S,
		WorldDirection.E,
		WorldDirection.W,
		WorldDirection.UC,
		WorldDirection.DC
	};

	private static readonly WorldDirection[] HorizontalNonDiagonalDirections = new WorldDirection[4]
	{
		WorldDirection.N,
		WorldDirection.S,
		WorldDirection.E,
		WorldDirection.W
	};

	[NonSerialized]
	private bool allocationsInitialized;

	[NonSerialized]
	public bool DisableRecursion;

	[NonSerialized]
	private const float WaterBeauty = 2f;

	private const GridDataType AllowBeautyDataType = GridDataType.BuildingFinished | GridDataType.Furniture | GridDataType.ProductionBuilding | GridDataType.Stairs | GridDataType.Roof | GridDataType.SocketableItem | GridDataType.Trap | GridDataType.Grave | GridDataType.RugFinished;

	[SerializeField]
	private readonly Vec3Int position;

	[SerializeField]
	private GridDataType dataType;

	[SerializeField]
	private BuildingType buildingType;

	[SerializeField]
	private CoverageType coverage;

	[SerializeField]
	private byte voxelTypeIdByte;

	[SerializeField]
	private byte digAmount;

	[SerializeField]
	private short health;

	[SerializeField]
	private bool drawbridgePlatform;

	[NonSerialized]
	private int index;

	[NonSerialized]
	private Vector3 worldPosition;

	[NonSerialized]
	private VoxelType voxelType;

	[NonSerialized]
	private Region region;

	[NonSerialized]
	private VillageMap map;

	[NonSerialized]
	private WaterSimLogic waterSimLogic;

	[NonSerialized]
	private bool isWalkable;

	[NonSerialized]
	private WorldDirection neighbours;

	[NonSerialized]
	private MapNodeConnections connectionsHolder;

	[NonSerialized]
	private List<WorldObject> worldObjects;

	[NonSerialized]
	private ushort creaturesCount;

	[NonSerialized]
	private MapNodeTags tag;

	[NonSerialized]
	private bool regionProcessingPending;

	[NonSerialized]
	private ushort[] penalties;

	[NonSerialized]
	private float beautyInput;

	[NonSerialized]
	private float creaturesBeauty;

	[NonSerialized]
	private float creaturesHeat;

	[NonSerialized]
	private bool beautyBlocker;

	[NonSerialized]
	private byte temperatureInput;

	[NonSerialized]
	private byte insulationInput;

	[NonSerialized]
	private byte verticalInsulationInput;

	[NonSerialized]
	private bool isGrass;

	[NonSerialized]
	private int lastUpdateFrame;

	[NonSerialized]
	private List<PlantMapResourceInstance> shadowCasterPlants;

	[NonSerialized]
	private MapNode nodeBelow;

	[NonSerialized]
	private MapNode nodeAbove;

	public bool HasFakeLadderFloor { get; set; }

	public bool ReachedMaxFire { get; set; }

	public bool IsFire { get; set; }

	public int Index => index;

	public Vec3Int Position => position;

	public Vector3 WorldPosition => GridUtils.GetWorldPosition(position);

	public GridDataType DataType => dataType;

	public BuildingType BuildingType => buildingType;

	public CoverageType Coverage => coverage;

	public bool IsWalkable => isWalkable;

	public bool IsWater
	{
		get
		{
			WaterSimLogic waterSimLogic = this.waterSimLogic;
			if (voxelTypeIdByte == 0 && waterSimLogic != null)
			{
				return waterSimLogic.IsWaterAt(index);
			}
			return false;
		}
	}

	public WaterDepthLevel WaterDepthLevel
	{
		get
		{
			WaterSimLogic waterSimLogic = this.waterSimLogic;
			if (waterSimLogic != null)
			{
				return waterSimLogic.GetWaterLevelAsDepth(index) | waterSimLogic.GetWaterDepthLevel(index);
			}
			return WaterDepthLevel.None;
		}
	}

	public WaterDepthLevel WaterLevel => waterSimLogic?.GetWaterLevelAsDepth(index) ?? WaterDepthLevel.None;

	public Region Region => region;

	public uint Area => region?.Area ?? 0;

	public VillageMap Map => map;

	public short Health => health;

	public bool DrawbridgePlatform => drawbridgePlatform;

	public byte DigAmount => digAmount;

	public bool IsDeadEnd { get; private set; }

	public ushort CreaturesCount => creaturesCount;

	public VoxelType VoxelType => voxelType;

	public byte VoxelTypeIdByte => voxelTypeIdByte;

	public MapNodeTags Tag => tag;

	public float BeautyInput => beautyInput;

	public float CreaturesBeauty => creaturesBeauty;

	public bool RegionProcessingPending
	{
		get
		{
			return regionProcessingPending;
		}
		internal set
		{
			regionProcessingPending = value;
		}
	}

	internal List<WorldObject> WorldObjects => worldObjects;

	public bool IsGrass => isGrass;

	public bool HasShadowCasterPlants => shadowCasterPlants.Count > 0;

	public int ConnectionsCount => connectionsHolder.ConnectionCount;

	public float Flammability { get; set; }

	public bool HasWaterTag => (tag & (MapNodeTags.WaterLevelLow | MapNodeTags.WaterLevelMedium | MapNodeTags.WaterLevelHigh | MapNodeTags.WaterDepthHigh)) != 0;

	public int ShadowCasterPlantsCount => shadowCasterPlants.Count;

	public List<PlantMapResourceInstance> ShadowCasterPlants => shadowCasterPlants;

	public event Action<MapNode> OnWalkabilityChangedEvent;

	public event Action<MapNode> OnMapNodeUpdatedEvent;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void OnDomainReload()
	{
		RefreshEnabled = false;
		RefreshFlammabilityEnabled = false;
	}

	public MapNode(Vec3Int position)
		: this()
	{
		this.position = position;
	}

	public MapNode(Vec3Int position, VoxelType voxelType, short health, byte digAmount, CoverageType coverageType, GridDataType gridDataType)
		: this()
	{
		this.position = position;
		SetVoxelType(voxelType);
		this.health = health;
		this.digAmount = digAmount;
		coverage = coverageType;
		index = GridDataIndexTools.FastTo1DIndex(this.position);
		worldPosition = GridUtils.GetWorldPosition(this.position);
		InitAllocations();
	}

	private MapNode()
	{
	}

	public void SetAboveBelowNodes()
	{
		int x = position.x;
		int y = position.y;
		int z = position.z;
		if (y < map.Size.y - 1)
		{
			int num = GridDataIndexTools.FastTo1DIndexNoCheck(x, y + 1, z);
			nodeAbove = map.GridSpaceData[num];
		}
		if (y > 0)
		{
			int num2 = GridDataIndexTools.FastTo1DIndexNoCheck(x, y - 1, z);
			nodeBelow = map.GridSpaceData[num2];
		}
	}

	[MustDisposeResource]
	public PooledList<MapNode> GetConnections()
	{
		if (!connectionsHolder.HasConnections)
		{
			return default(PooledList<MapNode>);
		}
		List<MapNode> threadLocalSmall = ListPool<MapNode>.GetThreadLocalSmall();
		GetConnections(threadLocalSmall);
		return new PooledList<MapNode>(threadLocalSmall, isThreadLocalSmall: true);
	}

	public void GetConnections(List<MapNode> output)
	{
		output.Clear();
		if (!connectionsHolder.HasConnections || map == null)
		{
			return;
		}
		WorldDirection connections = connectionsHolder.Connections;
		if (connections != 0)
		{
			foreach (KeyValuePair<WorldDirection, Vec3Int> item in MapNodeUtils.DirectionToVector)
			{
				var (worldDirection2, b) = (KeyValuePair<WorldDirection, Vec3Int>)(ref item);
				if ((worldDirection2 & connections) != 0)
				{
					Vec3Int gridPosition = position + b;
					MapNode node = map.GetNode(in gridPosition);
					if (node != null)
					{
						output.Add(node);
					}
				}
			}
		}
		WorldDirection connectionsHorizontalDist = connectionsHolder.ConnectionsHorizontalDist2;
		if (connectionsHorizontalDist == WorldDirection.None)
		{
			return;
		}
		WorldDirection[] horizontalNonDiagonalDirections = HorizontalNonDiagonalDirections;
		foreach (WorldDirection worldDirection3 in horizontalNonDiagonalDirections)
		{
			if ((worldDirection3 & connectionsHorizontalDist) != 0)
			{
				Vec3Int a = MapNodeUtils.DirectionToVector[worldDirection3];
				a *= 2;
				Vec3Int gridPosition2 = position + a;
				MapNode node2 = map.GetNode(in gridPosition2);
				if (node2 != null)
				{
					output.Add(node2);
				}
			}
		}
	}

	public FixedArray6<MapNode> GetNeighbours()
	{
		FixedArray6<MapNode> result = new FixedArray6<MapNode>();
		int num = 0;
		WorldDirection[] nonDiagonalDirections = NonDiagonalDirections;
		foreach (WorldDirection worldDirection in nonDiagonalDirections)
		{
			if ((worldDirection & neighbours) != 0)
			{
				result[num] = MapNodeUtils.GetNodeAtOffset(this, worldDirection);
				num++;
			}
		}
		result.Length = num;
		return result;
	}

	public void GetNeighboursAll(List<MapNode> output)
	{
		output.Clear();
		for (int i = -1; i <= 1; i++)
		{
			for (int j = -1; j <= 1; j++)
			{
				for (int k = -1; k <= 1; k++)
				{
					if (i != 0 || j != 0 || k != 0)
					{
						Vec3Int a = Position;
						Vec3Int b = new Vec3Int(i, j, k);
						Vec3Int gridPosition = a + b;
						MapNode node = map.GetNode(in gridPosition);
						if (node != null)
						{
							output.Add(node);
						}
					}
				}
			}
		}
	}

	public FixedArray6<MapNode> GetNeighboursHorizontal()
	{
		FixedArray6<MapNode> result = new FixedArray6<MapNode>();
		int num = 0;
		WorldDirection[] horizontalNonDiagonalDirections = HorizontalNonDiagonalDirections;
		foreach (WorldDirection worldDirection in horizontalNonDiagonalDirections)
		{
			if ((worldDirection & neighbours) != 0)
			{
				result[num] = MapNodeUtils.GetNodeAtOffset(this, worldDirection);
				num++;
			}
		}
		result.Length = num;
		return result;
	}

	public MapNode ConnectionsSafeSearch(ConnectionSearchOperation operation)
	{
		if (!connectionsHolder.HasConnections)
		{
			return null;
		}
		using PooledList<MapNode> pooledList = GetConnections();
		foreach (MapNode item in pooledList)
		{
			if (operation(item))
			{
				return item;
			}
		}
		return null;
	}

	public bool AnyConnection(Predicate<MapNode> condition)
	{
		MapNode connection;
		return AnyConnection(condition, out connection);
	}

	public bool AnyConnection(Predicate<MapNode> condition, out MapNode connection)
	{
		using PooledList<MapNode> pooledList = GetConnections();
		foreach (MapNode item in pooledList)
		{
			if (condition(item))
			{
				connection = item;
				return true;
			}
		}
		connection = null;
		return false;
	}

	public string GetGridFlagsAsStringArray()
	{
		return string.Join(", ", dataType.ToString());
	}

	public ushort GetPenalty(PathfindingPenalty penalty)
	{
		if (IsFire)
		{
			return 20000;
		}
		if (penalties == null || penalty == null)
		{
			return 1000;
		}
		if (region != null && region.Attribute != 0 && region.PenaltiesCalculated != null)
		{
			return (ushort)(region.PenaltiesCalculated[penalty.Index] + penalties[penalty.Index]);
		}
		return penalties[penalty.Index];
	}

	public MapNode GetNodeBelow()
	{
		return nodeBelow;
	}

	public MapNode GetNodeAbove()
	{
		return nodeAbove;
	}

	public void UpdateVoxelType(VoxelType voxelType, bool refreshPenalty = true, bool refreshNeighbours = true)
	{
		if (!(this.voxelType == voxelType))
		{
			if (voxelType != null)
			{
				health = voxelType.Health;
				digAmount = voxelType.DigAmount;
			}
			SetVoxelType(voxelType);
			ParameterChanged();
			if (refreshNeighbours)
			{
				ForceRefreshWithNeighbours(voxelType == null);
			}
			map.RegionManager.MapNodeStateChanged(this);
			if (refreshPenalty)
			{
				CalculatePenalty();
			}
			map.NodeVoxelTypeChanged(this);
		}
	}

	public void AddShadowCasterPlant(PlantMapResourceInstance plant)
	{
		shadowCasterPlants.Add(plant);
		if (shadowCasterPlants.Count == 1)
		{
			map.NodeIsShadowCasterChanged(this, isShadowCaster: true);
		}
	}

	public void RemoveShadowCasterPlant(PlantMapResourceInstance plant)
	{
		if (shadowCasterPlants.Count == 0)
		{
			Log.Error("Error in RemoveShadowCasterPlant: shadowCasterPlants.Count < 0.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Village\\Map\\MapNode.cs");
			return;
		}
		shadowCasterPlants.Remove(plant);
		if (shadowCasterPlants.Count == 0)
		{
			map.NodeIsShadowCasterChanged(this, isShadowCaster: false);
		}
	}

	public void ForceRefreshShadowCasterData()
	{
		bool flag = shadowCasterPlants.Count > 0 || ((tag & MapNodeTags.Wall) != 0 && (tag & MapNodeTags.OpenWindow) == 0);
		bool flag2 = flag;
		bool flag3 = (tag & (MapNodeTags.DoorWorkerWalkable | MapNodeTags.DoorCompletelyLocked | MapNodeTags.Wall)) != 0;
		bool flag4 = (tag & MapNodeTags.Floor) != 0 || (dataType & GridDataType.Roof) != 0 || (dataType & GridDataType.Drawbridge) != 0;
		bool flag5 = (Tag & MapNodeTags.DoorAlwaysOpen) != 0 || (tag & MapNodeTags.OpenWindow) != 0;
		flag = flag || (!flag5 && flag3);
		flag2 = flag2 || flag4;
		Map.TemperatureManager.SetShadowCaster(position, flag, flag2);
	}

	public MapNode GetClosestConnectedNode(Vec3Int pos, Func<MapNode, bool> validator = null)
	{
		using PooledList<MapNode> pooledList = GetConnections();
		return pooledList.GetClosestNode(pos, validator);
	}

	public float Distance(MapNode otherNode)
	{
		Vec3Int a = Position;
		Vec3Int b = otherNode.Position;
		return (a - b).magnitude;
	}

	public float DistanceSquared(MapNode otherNode)
	{
		Vec3Int a = Position;
		Vec3Int b = otherNode.Position;
		return (a - b).sqrMagnitude;
	}

	private void CalculateTemperatureInput()
	{
		int num = 0;
		float num2 = 0f;
		float num3 = 0f;
		int num4 = 0;
		byte b = 4;
		if (voxelType?.ThermalModel != null)
		{
			num = voxelType.ThermalModel.Emission;
			num2 = voxelType.ThermalModel.Insulation;
			num3 = voxelType.ThermalModel.InsulationVertical;
			num4 = voxelType.ThermalModel.EmissionRange;
			b = 0;
		}
		if (IsFire)
		{
			ThermalModel fireThermalModel = map.FireSimLogic.GetFireThermalModel(index);
			if (fireThermalModel != null)
			{
				num = fireThermalModel.Emission;
				num4 = fireThermalModel.EmissionRange;
			}
		}
		foreach (WorldObject worldObject in worldObjects)
		{
			if (!(worldObject.ThermalModel == null) && !worldObject.HasDisposed && !(worldObject is BaseBuildingInstance { ConstructionPhase: not ConstructionPhase.Finished }))
			{
				num4 = math.max(num4, worldObject.ThermalModel.EmissionRange);
				num += worldObject.ThermalModel.Emission;
				num2 += worldObject.ThermalModel.Insulation;
				num3 += worldObject.ThermalModel.InsulationVertical;
				b = Math.Min(b, worldObject.ThermalModel.LightTransmission);
			}
		}
		if (map.CreaturesOnNodes.ContainsKey(Index))
		{
			foreach (CreatureBase item in map.CreaturesOnNodes[Index])
			{
				if (item.ThermalModel != null)
				{
					num4 = Math.Max(num4, item.ThermalModel.EmissionRange);
				}
			}
		}
		num += (int)creaturesHeat;
		temperatureInput = (byte)Math.Clamp(num + 128, 0, 255);
		insulationInput = (byte)Math.Clamp(num2 * 255f, 0f, 250f);
		verticalInsulationInput = (byte)Math.Clamp(num3 * 255f, 0f, 250f);
		bool isWall = (tag & MapNodeTags.Wall) != 0;
		map.TemperatureManager.SetInputData(position, temperatureInput, insulationInput, verticalInsulationInput, isWalkable, voxelTypeIdByte != 0, isWall, num4, b);
		ForceRefreshShadowCasterData();
	}

	private void CalculateBeautyInput()
	{
		beautyInput = creaturesBeauty;
		beautyBlocker = VoxelTypeIdByte != 0;
		if (!beautyBlocker)
		{
			if (IsWater)
			{
				beautyInput += 2f;
			}
			else if (position.y > 0 && nodeBelow.IsWater && ((tag & MapNodeTags.Floor) == 0 || (tag & MapNodeTags.FloorPassthrough) != 0))
			{
				beautyInput += 2f;
			}
			foreach (WorldObject worldObject in worldObjects)
			{
				if ((worldObject.GridDataType & (GridDataType.BuildingFinished | GridDataType.Furniture | GridDataType.ProductionBuilding | GridDataType.Stairs | GridDataType.Roof | GridDataType.SocketableItem | GridDataType.Trap | GridDataType.Grave | GridDataType.RugFinished)) != 0 || worldObject.Type == WorldObjectType.ResourcePile || worldObject.Type == WorldObjectType.MapResource)
				{
					beautyInput += worldObject.GetBeautyInput();
					beautyBlocker |= worldObject.BeautyBlocker();
					if (beautyBlocker)
					{
						break;
					}
				}
			}
		}
		map.BeautyManager.SetCombinedBufferData(this, beautyBlocker, beautyInput);
	}

	private void SetVoxelType(VoxelType newVoxelType)
	{
		bool flag = newVoxelType != null;
		voxelTypeIdByte = (byte)(flag ? newVoxelType.ByteId : 0);
		voxelType = newVoxelType;
		isGrass = flag && voxelType.CanGrowGrass;
		if (flag && newVoxelType.IsOverride)
		{
			VoxelType byID = VoxelTypeRepository.FastInstance.GetByID(newVoxelType.OverrideBy);
			if (byID != null)
			{
				voxelType = byID;
				isGrass = newVoxelType.OverrideIsGrass;
			}
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool CheckIsDataType(GridDataType type)
	{
		return (dataType & type) != 0;
	}

	public bool CheckBuildingType(BuildingType type)
	{
		return (buildingType & type) != 0;
	}

	public bool ContainsSleepingOrFaintedCreature()
	{
		if (!Map.CreaturesOnNodes.TryGetValue(Index, out var value))
		{
			return false;
		}
		foreach (CreatureBase item in value)
		{
			if (item.IsSleeping || item.HasFainted)
			{
				return true;
			}
		}
		return false;
	}

	public void AddCreature(CreatureBase creature)
	{
		if (!Map.CreaturesOnNodes.TryGetValue(index, out var value))
		{
			value = HashSetPool<CreatureBase>.Get();
			value.Add(creature);
			Map.CreaturesOnNodes.Add(index, value);
			creaturesBeauty += creature.GetBeauty();
			RefreshCreaturesHeat(value);
			creaturesCount++;
		}
		else if (value.Add(creature))
		{
			creaturesBeauty += creature.GetBeauty();
			RefreshCreaturesHeat(value);
			creaturesCount++;
		}
	}

	private void RefreshCreaturesHeat(HashSet<CreatureBase> set)
	{
		if (set == null)
		{
			creaturesHeat = 0f;
			return;
		}
		bool flag = true;
		float num = 0f;
		float num2 = 0f;
		foreach (CreatureBase item in set)
		{
			if (flag)
			{
				flag = false;
				num2 = item.GetHeat();
				num = item.GetHeat();
			}
			else
			{
				num2 = math.min(item.GetHeat(), num2);
				num = math.max(item.GetHeat(), num);
			}
		}
		if (num2 < 0f)
		{
			creaturesHeat = num2 + num;
		}
		else
		{
			creaturesHeat = num;
		}
	}

	public void RemoveCreature(CreatureBase creature)
	{
		if (Map == null)
		{
			return;
		}
		if (!Map.CreaturesOnNodes.TryGetValue(index, out var value))
		{
			creaturesBeauty = 0f;
			creaturesHeat = 0f;
			creaturesCount = 0;
			return;
		}
		if (value.Remove(creature))
		{
			creaturesCount--;
			creaturesBeauty -= creature.GetBeauty();
		}
		if (creaturesCount == 0)
		{
			creaturesBeauty = 0f;
			creaturesHeat = 0f;
			Map.CreaturesOnNodes.Remove(index);
			HashSetPool<CreatureBase>.Return(value);
		}
		else
		{
			RefreshCreaturesHeat(value);
		}
	}

	public void ForceRefreshPenalty()
	{
		CalculatePenalty();
	}

	public void DirectIncrementPenalties(Predicate<PathfindingPenalty> filter, int amount)
	{
		if (penalties == null)
		{
			return;
		}
		for (int i = 0; i < penalties.Length; i++)
		{
			PathfindingPenalty obj = PathfindingPenaltyRepository.FastRepo[i];
			if (filter(obj))
			{
				if (amount < 0 && -amount > penalties[i])
				{
					penalties[i] = 0;
				}
				else
				{
					penalties[i] += (ushort)amount;
				}
			}
		}
	}

	public void ForceRefreshBeautyInput()
	{
		CalculateBeautyInput();
	}

	public void ForceRefreshTemperatureInput()
	{
		CalculateTemperatureInput();
	}

	public void AddForbiddenGridData()
	{
		dataType |= GridDataType.ForbiddenByBuilding;
	}

	public void RemoveForbiddenGridData()
	{
		dataType &= ~GridDataType.ForbiddenByBuilding;
	}

	public bool IsHorizontallyDiagonalTo(MapNode other)
	{
		Vec3Int vec3Int = Position;
		Vec3Int vec3Int2 = other.Position;
		if (vec3Int.x != vec3Int2.x)
		{
			return vec3Int.z != vec3Int2.z;
		}
		return false;
	}

	internal void SetMap(VillageMap map)
	{
		this.map = map;
		waterSimLogic = map.WaterManager.WaterSimLogic;
	}

	internal void AddDrawbridge(BaseBuildingInstance ownerBuilding)
	{
		drawbridgePlatform = true;
		dataType |= GridDataType.Drawbridge;
		if (!worldObjects.Contains(ownerBuilding))
		{
			worldObjects.Add(ownerBuilding);
		}
		buildingType |= ownerBuilding.BuildingType;
		RefreshInternal(recursionEnabled: true, refreshPenalty: true, refreshTags: false);
		RefreshTagsDrawbridge(remove: false);
		nodeBelow?.RefreshCoverage();
		CalculatePenalty();
		map.NodeDrawbridgeOpened(this);
	}

	internal void RemoveDrawbridge(BaseBuildingInstance ownerBuilding)
	{
		drawbridgePlatform = false;
		dataType &= GridDataType.AnyBuildPhase | GridDataType.SlopeOrStairs | GridDataType.DigMarkerResource | GridDataType.DigMarkerResourceToMine | GridDataType.ResourcePile | GridDataType.Stockpile | GridDataType.PlantMapResource | GridDataType.OthersBlueprint | GridDataType.OthersUnfinished | GridDataType.Furniture | GridDataType.ProductionBuilding | GridDataType.Cropfield | GridDataType.BeamBlueprint | GridDataType.BeamUnfinished | GridDataType.BeamFinished | GridDataType.Roof | GridDataType.SocketableItem | GridDataType.SocketableBlueprint | GridDataType.SocketableUnfinished | GridDataType.Trap | GridDataType.Grave | GridDataType.FurnitureGate | GridDataType.RugBlueprint | GridDataType.RugFoundation | GridDataType.RugFinished | GridDataType.FishMapResource | GridDataType.ForbiddenByBuilding | GridDataType.PathfindingPoint;
		worldObjects.Remove(ownerBuilding);
		buildingType &= ~ownerBuilding.BuildingType;
		RefreshInternal(recursionEnabled: true, refreshPenalty: true, refreshTags: false);
		RefreshTagsDrawbridge(remove: true);
		nodeBelow?.RefreshCoverage();
		CalculatePenalty();
		map.NodeDrawbridgeClosed(this);
	}

	public void RefreshTagsDrawbridge(bool remove)
	{
		MapNodeTags mapNodeTags = tag;
		mapNodeTags = ((!remove) ? (mapNodeTags | MapNodeTags.DrawbridgePlatform) : (mapNodeTags & ~MapNodeTags.DrawbridgePlatform));
		if (mapNodeTags != tag)
		{
			MapNodeTags mapNodeTags2 = tag;
			tag = mapNodeTags;
			if (mapNodeTags2 != mapNodeTags)
			{
				map.NodeTagChanged(this, mapNodeTags2);
			}
		}
	}

	internal void AddObject(WorldObject instance, bool silent = false)
	{
		dataType |= instance.GridDataType;
		if (instance is BaseBuildingInstance baseBuildingInstance)
		{
			buildingType |= baseBuildingInstance.BuildingType;
		}
		worldObjects.Add(instance);
		if (!silent)
		{
			ParameterChanged();
		}
		nodeBelow?.RefreshCoverage();
		if (!silent)
		{
			CalculatePenalty();
		}
		if (this.IsLayerRamp())
		{
			foreach (MapNode item in MapNodeUtils.IterateEachNeighbor(this))
			{
				item.ForceRefresh();
			}
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				if (region != null && region.RefreshNeighbourConnections())
				{
					map.RegionAreaManager.QueueForRecalculation(Area);
				}
			});
		}
		if (MapNodeWalkabilityLogic.CanSupportWalkabilityAbove(this, nodeAbove))
		{
			nodeAbove?.RefreshInternal();
		}
	}

	internal void RemoveObject(WorldObject instance)
	{
		dataType &= ~instance.GridDataType;
		worldObjects.Remove(instance);
		if (instance is BaseBuildingInstance baseBuildingInstance)
		{
			buildingType &= ~baseBuildingInstance.BuildingType;
		}
		foreach (WorldObject worldObject in worldObjects)
		{
			dataType |= worldObject.GridDataType;
			if (worldObject is BaseBuildingInstance baseBuildingInstance2)
			{
				buildingType |= baseBuildingInstance2.BuildingType;
			}
		}
		if (instance.Map.StairsComponentManager.GetComponentInstance(instance) != null || instance is SlopeInstance)
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				region?.RefreshNeighbourConnections();
				map.RegionAreaManager.QueueForRecalculation(Area);
			});
		}
		ParameterChanged();
		nodeBelow?.RefreshCoverage();
		CalculatePenalty();
	}

	internal void OnObjectDataTypeChanged(WorldObject instance, GridDataType oldDataType)
	{
		if (!worldObjects.Contains(instance) && instance.GridDataType != 0)
		{
			worldObjects.Add(instance);
		}
		bool num = this.IsLayerRamp();
		dataType &= ~oldDataType;
		dataType |= instance.GridDataType;
		ParameterChanged();
		CalculatePenalty();
		region?.RefreshGridDataTypeOptimized();
		if (num || this.IsLayerRamp())
		{
			foreach (MapNode item in MapNodeUtils.IterateEachNeighbor(this))
			{
				item.ForceRefresh();
			}
		}
		if (this.IsLayerRamp())
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				region?.RefreshNeighbourConnections();
				map.RegionAreaManager.QueueForRecalculation(Area);
			});
		}
		nodeBelow?.RefreshCoverage();
		if (MapNodeWalkabilityLogic.CanSupportWalkabilityAbove(this, nodeAbove))
		{
			nodeAbove?.RefreshInternal();
		}
		if (oldDataType != dataType)
		{
			map.NodeGridDataTypeChanged(this, oldDataType);
		}
	}

	internal void RemoveGround()
	{
		UpdateVoxelType(null);
	}

	internal bool DecreaseDigAmount()
	{
		if (voxelTypeIdByte == 0)
		{
			return false;
		}
		if (digAmount > 0)
		{
			digAmount--;
		}
		short num = (short)Mathf.RoundToInt((float)voxelType.Health / (float)(int)voxelType.DigAmount);
		if (num < 0)
		{
			num = health;
		}
		if (health > num)
		{
			health -= num;
		}
		else
		{
			health = 0;
		}
		if (digAmount <= 0 || health <= 0)
		{
			RemoveGround();
			return true;
		}
		return false;
	}

	internal bool DamageVoxel(short healthDamage)
	{
		health -= healthDamage;
		ParameterChanged();
		return (float)health <= 0f;
	}

	internal void SetRegion(Region region, bool silent = false)
	{
		if (this.region != region)
		{
			this.region = region;
			if (!silent && !LoadingController.IsSceneTransition && !MonoSingleton<LoadingController>.IsApplicationQuitting())
			{
				ParameterChanged();
			}
		}
	}

	internal void Initialize(VillageMap map)
	{
		ReInitialize(map);
	}

	public void InitAllocations()
	{
		if (!allocationsInitialized)
		{
			allocationsInitialized = true;
			penalties = new ushort[PathfindingPenaltyRepository.FastRepo.Count];
			shadowCasterPlants = new List<PlantMapResourceInstance>();
			worldObjects = new List<WorldObject>();
		}
	}

	internal void ReInitialize(VillageMap map)
	{
		if (voxelTypeIdByte != 0 && voxelType == null)
		{
			SetVoxelType(VoxelTypeRepository.FastInstance.GetByByteId(voxelTypeIdByte));
		}
		index = GridDataIndexTools.FastTo1DIndex(position);
		this.map = map;
		waterSimLogic = map.WaterManager.WaterSimLogic;
		worldPosition = GridUtils.GetWorldPosition(position);
	}

	internal void ForceRefresh()
	{
		RefreshInternal();
	}

	public void RefreshAfterLoad()
	{
		RefreshInternal(recursionEnabled: false, refreshPenalty: false, refreshTags: false);
	}

	public void RefreshOnNewGame()
	{
		RefreshInternal(recursionEnabled: false, refreshPenalty: false);
	}

	internal void ForceRefreshWithNeighbours(bool skipFrameCheck = false)
	{
		if (!LoadingController.IsLeavingMainScene)
		{
			if (skipFrameCheck)
			{
				MapNodeUtils.ForEachNeighbour(this, ForceRefresh);
			}
			else
			{
				MapNodeUtils.ForEachNeighbour(this, ForceRefreshWithFrameCheck);
			}
		}
	}

	private bool ForceRefreshWithFrameCheck(MapNode node)
	{
		if (node.lastUpdateFrame != Time.frameCount)
		{
			node.ForceRefresh();
		}
		return true;
	}

	private bool ForceRefresh(MapNode node)
	{
		node.ForceRefresh();
		return true;
	}

	public void ForceUpdateConnections()
	{
		PopulateConnections();
		RefreshIsDeadEnd();
	}

	internal void RefreshIsWalkable()
	{
		isWalkable = false;
		if (!this.IsVoxelAir() || MapNodeWalkabilityLogic.IsOccupiedByUnWalkableBuilding(this))
		{
			return;
		}
		if (IsWater && WaterDepthLevel > WaterDepthLevel.Low)
		{
			WaterManager waterManager = VillageManager.ActiveVillage.Map.WaterManager;
			if (nodeAbove != null && nodeAbove.IsWater && (nodeAbove.WaterLevel & WaterDepthLevel.Low) == 0 && !waterManager.IsWaterEnclosed(nodeAbove))
			{
				isWalkable = false;
			}
			else if (!waterManager.IsWaterEnclosed(this))
			{
				isWalkable = false;
			}
			else
			{
				isWalkable = true;
			}
		}
		else if (MapNodeWalkabilityLogic.IsOccupiedByWalkableBuilding(this))
		{
			isWalkable = true;
		}
		else
		{
			MapNode mapNode = nodeBelow;
			if (mapNode != null && MapNodeWalkabilityLogic.CanSupportWalkabilityAbove(mapNode, this))
			{
				isWalkable = true;
			}
		}
	}

	private void ParameterChanged()
	{
		RefreshInternal();
	}

	private bool RefreshInternal(bool recursionEnabled = true, bool refreshPenalty = true, bool refreshTags = true)
	{
		if (!RefreshEnabled || LoadingController.IsLeavingMainScene)
		{
			return false;
		}
		bool num = (tag & MapNodeTags.Fire) != 0;
		bool flag = false;
		if (refreshTags)
		{
			flag |= RefreshTags();
		}
		bool flag2 = IsWalkable;
		RefreshIsWalkable();
		flag |= isWalkable != flag2;
		flag |= PopulateConnections();
		RefreshIsDeadEnd();
		PopulateNeighbors();
		float flammability = Flammability;
		RefreshFlammability();
		bool flag3 = Math.Abs(flammability - Flammability) > 0.01f;
		RefreshCoverage();
		bool flag4 = num != ((tag & MapNodeTags.Fire) != 0);
		if (flag)
		{
			lastUpdateFrame = Time.frameCount;
			this.OnMapNodeUpdatedEvent?.Invoke(this);
		}
		if (IsWalkable)
		{
			if (!flag2)
			{
				if (recursionEnabled)
				{
					nodeAbove?.RefreshInternal();
				}
				flag4 = true;
				this.OnWalkabilityChangedEvent?.Invoke(this);
				Map.NodeWalkabilityChanged(this);
				if (!DisableRecursion && recursionEnabled && connectionsHolder.HasConnections)
				{
					using PooledList<MapNode> pooledList = GetConnections();
					foreach (MapNode item in pooledList)
					{
						item.RefreshInternal();
					}
				}
			}
			else if (recursionEnabled && MapNodeWalkabilityLogic.CanSupportWalkabilityAbove(this, nodeAbove))
			{
				nodeAbove?.RefreshInternal();
			}
		}
		else if (flag2)
		{
			if (!DisableRecursion && recursionEnabled)
			{
				nodeAbove?.RefreshInternal();
				if (connectionsHolder.HasConnections)
				{
					using PooledList<MapNode> pooledList2 = GetConnections();
					foreach (MapNode item2 in pooledList2)
					{
						item2.RefreshInternal();
					}
				}
			}
			flag4 = true;
			this.OnWalkabilityChangedEvent?.Invoke(this);
			Map.NodeWalkabilityChanged(this);
		}
		if (refreshPenalty)
		{
			CalculatePenalty();
		}
		CalculateBeautyInput();
		CalculateTemperatureInput();
		if (!flag4)
		{
			if (RegionBridgeLogic.IsBridgeNode(this))
			{
				flag4 = region == null || !region.IsBridge;
			}
			else if (region != null && region.IsBridge)
			{
				flag4 = true;
			}
		}
		if (flag4)
		{
			map.RegionManager.MapNodeStateChanged(this);
		}
		if (flag3 && RefreshFlammabilityEnabled)
		{
			map.NodeFlammabilityChanged(this);
		}
		return flag;
	}

	public void ForceRefreshFlammability()
	{
		float flammability = Flammability;
		RefreshFlammability();
		if (Math.Abs(flammability - Flammability) > 0.01f)
		{
			map.NodeFlammabilityChanged(this);
		}
	}

	public void RefreshFlammability()
	{
		if (VoxelTypeIdByte != 0)
		{
			Flammability = -1f;
			return;
		}
		float num = 0f;
		foreach (WorldObject worldObject in WorldObjects)
		{
			if (!worldObject.HasDisposed && worldObject.Flammability > 0f)
			{
				num = math.max(num, worldObject.Flammability);
			}
		}
		Flammability = num;
	}

	private void PopulateNeighbors()
	{
		neighbours = MapNodeConnectionLogic.GenerateNeighborsNonDiagonal(this);
	}

	private void CalculatePenalty()
	{
		if (penalties != null)
		{
			for (int i = 0; i < penalties.Length; i++)
			{
				penalties[i] = PathfindingPenalty.GetPathfindingPenalty(PathfindingPenaltyRepository.FastRepo[i], this, map);
			}
		}
	}

	private void RefreshIsDeadEnd()
	{
		IsDeadEnd = false;
		if (!connectionsHolder.HasConnections)
		{
			return;
		}
		int num = 0;
		using PooledList<MapNode> pooledList = GetConnections();
		foreach (MapNode item in pooledList)
		{
			if (num > 1)
			{
				break;
			}
			if (item.IsWalkable)
			{
				num++;
			}
		}
		IsDeadEnd = num == 1;
	}

	private bool PopulateConnections()
	{
		MapNodeConnectionLogic.GenerateConnections(this, out var connections, out var connectionsHorizontalDist, out var connectionsCount);
		if (connections == connectionsHolder.Connections && connectionsHorizontalDist == connectionsHolder.ConnectionsHorizontalDist2 && connectionsCount == connectionsHolder.ConnectionCount)
		{
			return false;
		}
		connectionsHolder = new MapNodeConnections
		{
			Connections = connections,
			ConnectionsHorizontalDist2 = connectionsHorizontalDist,
			ConnectionCount = connectionsCount
		};
		return true;
	}

	public bool RefreshTags()
	{
		MapNodeTags mapNodeTags = MapNodeTaggingLogic.CalculateNodeTagsRefactored(this);
		if (mapNodeTags == tag)
		{
			return false;
		}
		MapNodeTags mapNodeTags2 = tag;
		tag = mapNodeTags;
		if (mapNodeTags2 != mapNodeTags)
		{
			map.NodeTagChanged(this, mapNodeTags2);
		}
		return true;
	}

	public bool HasFirePresence()
	{
		return map.FirePresenceGrid.HasFirePresence(this);
	}

	public override string ToString()
	{
		return $"GridPos: {position.ToStringWithLink()}, Index: {index}, VoxelTypeId: '{voxelTypeIdByte}', Tags: {Tag}.";
	}

	private bool AnyAboveNode(Predicate<MapNode> condition)
	{
		for (MapNode mapNode = nodeAbove; mapNode != null; mapNode = mapNode.nodeAbove)
		{
			if (condition(mapNode))
			{
				return true;
			}
		}
		return false;
	}

	private void RefreshCoverage()
	{
		CoverageType coverageType = coverage;
		if ((dataType & GridDataType.Slope) != 0)
		{
			coverage = CoverageType.Roofed;
		}
		else if (AnyAboveNode((MapNode node) => !node.IsVoxelAir() || (node.dataType & GridDataType.Stairs) != 0 || ((uint)node.DataType & 0x80000000u) != 0 || (node.dataType & GridDataType.Roof) != 0 || ((node.DataType & GridDataType.BuildingFinished) != 0 && (node.tag & MapNodeTags.FloorPassthrough) == 0)))
		{
			coverage = CoverageType.Roofed;
		}
		else
		{
			coverage = CoverageType.Outside;
		}
		if (coverage != coverageType)
		{
			region?.CoverageChanged(this, coverageType);
			nodeBelow?.RefreshCoverage();
			map.CoverageChanged(index, coverage);
		}
	}

	public void Destroy()
	{
		worldObjects?.Clear();
		shadowCasterPlants?.Clear();
		map = null;
		region = null;
		waterSimLogic = null;
		connectionsHolder = default(MapNodeConnections);
		neighbours = WorldDirection.None;
		penalties = null;
		worldObjects = null;
		shadowCasterPlants = null;
		nodeBelow = null;
		nodeAbove = null;
		this.OnWalkabilityChangedEvent = null;
		this.OnMapNodeUpdatedEvent = null;
	}

	public void SetAsWaterVoxel(float waterLevel)
	{
		if (!(waterLevel <= 0f))
		{
			voxelType = null;
			voxelTypeIdByte = 0;
			Map.WaterManager.WaterSimLogic.SetWaterAt(index, waterLevel);
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
