using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Fire;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using Gameplay.Resource;
using NSEipix.Base;
using NSMedieval.BuildingComponents;
using NSMedieval.CommanderAI;
using NSMedieval.Construction;
using NSMedieval.Fire;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.RoomDetection;
using NSMedieval.Serialization;
using NSMedieval.State;
using NSMedieval.Tools;
using NSMedieval.Types;
using NSMedieval.Utils.Pool;
using NSMedieval.Village.Map.Pathfinding;
using NSMedieval.Water;
using Unity.Collections;
using UnityEngine;

namespace NSMedieval.Village.Map;

[Serializable]
[FVSerializableKey("VillageMap", "")]
public class VillageMap : IGameDisposable, IDisposable, IFVSerializable
{
	private object memProfileInstance;

	[NonSerialized]
	private MapNode[] gridData;

	[SerializeField]
	private MapNodesHolder nodesHolder;

	[SerializeField]
	private Vec3Int size;

	[SerializeField]
	private SerializableHashSet<Vec3Int> cropBlightPositions;

	[SerializeField]
	private float totalFireDamageSinceBurning;

	[SerializeField]
	private long fireStartTimeMinutes;

	private ConstructionJobManager.SaveData constructionJobManagerSaveData;

	private Dictionary<GridDataType, HashSet<WorldObject>> worldObjectsByType;

	[NonSerialized]
	private VillageInstance villageInstance;

	private Dictionary<int, HashSet<CreatureBase>> creaturesOnNodes;

	private VillageMapRegionManager regionManager;

	private RegionAreaManager regionAreaManager;

	private BeautyManager beautyManager;

	private TemperatureManager temperatureManager;

	private Effects3dTextureManager effects3dTextureManager;

	private SnowGrassWetnessManager snowGrassWetnessManager;

	private ProtectorCreatureManager protectorCreatureManager;

	private ProtectorBuildingManager protectorBuildingManager;

	private WaterManager waterManager;

	private BuildingsManagerMain buildingsManagerMain;

	private BeamComponentManager beamComponentManager;

	private LadderComponentManager ladderComponentManager;

	private BedComponentManager bedComponentManager;

	private SocketComponentManager socketComponentManager;

	private ChairComponentManager chairComponentManager;

	private DoorComponentManager doorComponentManager;

	private FuelConsumerComponentManager fuelConsumerComponentManager;

	private GraveComponentManager graveComponentManager;

	private PenMarkerComponentManager penMarkerComponentManager;

	private RallyPointMarkerComponentManager rallyPointMarkerComponentManager;

	private TrapComponentsManager trapComponentsManager;

	private ProductionComponentBuildingManager productionComponentBuildingManager;

	private RoofComponentManager roofComponentManager;

	private RugComponentManager rugComponentManager;

	private ShelfComponentManager shelfComponentManager;

	private ShrineComponentManager shrineComponentManager;

	private SignComponentManager signComponentManager;

	private StairsComponentManager stairsComponentManager;

	private TableComponentManager tableComponentManager;

	private WindowComponentManager windowComponentManager;

	private EntertainmentComponentManager entertainmentComponentManager;

	private DecorationComponentManager decorationComponentManager;

	private TradingPostComponentManager tradingPostComponentManager;

	private CaravanPostComponentManager caravanPostComponentManager;

	private MapTableComponentManager mapTableComponentManager;

	private GallowsComponentManager gallowsComponentManager;

	private WellComponentManager wellComponentManager;

	private SiegeWeaponComponentManager siegeWeaponComponentManager;

	private EnemyBuildingsManager enemyBuildingsManager;

	private GlobalEffectorsManager globalEffectorsManager;

	private IdlePoints idlePoints;

	private IdlePointManager idlePointManager;

	private NSMedieval.RoomDetection.RoomDetection roomDetection;

	private StabilityManager stabilityManager;

	private FireSimLogic fireSimLogic;

	private FireMeshLogic fireMeshLogic;

	private WaterDebugDrawLogic waterDebugDrawLogic;

	private AntiPathCrowdingManager antiPathCrowdingManager;

	private FireAudioAndLights fireAudioAndLights;

	private RaidManager raidManager;

	private HomeArea homeArea;

	private CommanderAIManager commanderAIManager;

	private FirePresenceGrid firePresenceGrid;

	private SecondMapLeaveManager secondMapLeaveManager;

	private DebugEventLog debugEventLog;

	private UniqueResourceTracker uniqueResourceTracker;

	private BellComponentManager bellComponentManager;

	private MerlonRotationManager merlonRotationManager;

	private VinesManager vinesManager;

	private RoofMeshVariationManager roofMeshVariationManager;

	private AnimatorDisableManager animatorDisableManager;

	private FloorAutomaticMeshVariationManager floorAutomaticMeshVariationManager;

	private WallAutomaticMeshVariationManager wallAutomaticMeshVariationManager;

	private FenceAutomaticMeshVariationManager fenceAutomaticMeshVariationManager;

	private PathLengthEstimator pathLengthEstimator;

	private bool hasIgnoredInitialWaterUpdate;

	public MapNodesHolder NodesHolder => nodesHolder;

	public bool HasDisposed { get; private set; }

	public Vec3Int Size => size;

	public VillageInstance VillageInstance => villageInstance;

	public VillageMapRegionManager RegionManager => regionManager;

	public RegionAreaManager RegionAreaManager => regionAreaManager;

	public NSMedieval.RoomDetection.RoomDetection RoomDetection => roomDetection;

	public BeautyManager BeautyManager => beautyManager;

	public TemperatureManager TemperatureManager => temperatureManager;

	public Effects3dTextureManager Effects3dTextureManager => effects3dTextureManager;

	public SnowGrassWetnessManager SnowGrassWetnessManager => snowGrassWetnessManager;

	public ProtectorCreatureManager ProtectorCreatureManager => protectorCreatureManager;

	public ProtectorBuildingManager ProtectorBuildingManager => protectorBuildingManager;

	public WaterManager WaterManager => waterManager;

	public BuildingsManagerMain BuildingsManagerMain => buildingsManagerMain;

	public BedComponentManager BedComponentManager => bedComponentManager;

	public BeamComponentManager BeamComponentManager => beamComponentManager;

	public LadderComponentManager LadderComponentManager => ladderComponentManager;

	public SocketComponentManager SocketComponentManager => socketComponentManager;

	public ChairComponentManager ChairComponentManager => chairComponentManager;

	public DoorComponentManager DoorComponentManager => doorComponentManager;

	public FuelConsumerComponentManager FuelConsumerComponentManager => fuelConsumerComponentManager;

	public GraveComponentManager GraveComponentManager => graveComponentManager;

	public ProductionComponentBuildingManager ProductionComponentBuildingManager => productionComponentBuildingManager;

	public PenMarkerComponentManager PenMarkerComponentManager => penMarkerComponentManager;

	public RallyPointMarkerComponentManager RallyPointMarkerComponentManager => rallyPointMarkerComponentManager;

	public TrapComponentsManager TrapComponentsManager => trapComponentsManager;

	public RoofComponentManager RoofComponentManager => roofComponentManager;

	public RugComponentManager RugComponentManager => rugComponentManager;

	public ShelfComponentManager ShelfComponentManager => shelfComponentManager;

	public ShrineComponentManager ShrineComponentManager => shrineComponentManager;

	public SignComponentManager SignComponentManager => signComponentManager;

	public StairsComponentManager StairsComponentManager => stairsComponentManager;

	public TableComponentManager TableComponentManager => tableComponentManager;

	public WindowComponentManager WindowComponentManager => windowComponentManager;

	public EntertainmentComponentManager EntertainmentComponentManager => entertainmentComponentManager;

	public DecorationComponentManager DecorationComponentManager => decorationComponentManager;

	public TradingPostComponentManager TradingPostComponentManager => tradingPostComponentManager;

	public CaravanPostComponentManager CaravanPostComponentManager => caravanPostComponentManager;

	public MapTableComponentManager MapTableComponentManager => mapTableComponentManager;

	public StabilityManager StabilityManager => stabilityManager;

	public GallowsComponentManager GallowsComponentManager => gallowsComponentManager;

	public WellComponentManager WellComponentManager => wellComponentManager;

	public EnemyBuildingsManager EnemyBuildingsManager => enemyBuildingsManager;

	public SiegeWeaponComponentManager SiegeWeaponComponentManager => siegeWeaponComponentManager;

	public BellComponentManager BellComponentManager => bellComponentManager;

	public MerlonRotationManager MerlonRotationManager => merlonRotationManager;

	public VinesManager VinesManager => vinesManager;

	public RoofMeshVariationManager RoofMeshVariationManager => roofMeshVariationManager;

	public FloorAutomaticMeshVariationManager FloorAutomaticMeshVariationManager => floorAutomaticMeshVariationManager;

	public WallAutomaticMeshVariationManager WallAutomaticMeshVariationManager => wallAutomaticMeshVariationManager;

	public FenceAutomaticMeshVariationManager FenceAutomaticMeshVariationManager => fenceAutomaticMeshVariationManager;

	public PathLengthEstimator PathLengthEstimator => pathLengthEstimator;

	public IdlePoints IdlePoints => idlePoints;

	public IdlePointManager IdlePointManager => idlePointManager;

	public FireSimLogic FireSimLogic => fireSimLogic;

	public FireMeshLogic FireMeshLogic => fireMeshLogic;

	public WaterDebugDrawLogic WaterDebugDrawLogic => waterDebugDrawLogic;

	public FireAudioAndLights FireAudioAndLights => fireAudioAndLights;

	public FirePresenceGrid FirePresenceGrid => firePresenceGrid;

	public SecondMapLeaveManager SecondMapLeaveManager => secondMapLeaveManager;

	public DebugEventLog DebugEventLog => debugEventLog;

	public UniqueResourceTracker UniqueResourceTracker => uniqueResourceTracker;

	public AnimatorDisableManager AnimatorDisableManager => animatorDisableManager;

	public float TotalFireDamageSinceBurning
	{
		get
		{
			return totalFireDamageSinceBurning;
		}
		set
		{
			totalFireDamageSinceBurning = value;
		}
	}

	public long FireStartTimeMinutes
	{
		get
		{
			return fireStartTimeMinutes;
		}
		set
		{
			fireStartTimeMinutes = value;
		}
	}

	public GlobalEffectorsManager GlobalEffectorsManager => globalEffectorsManager;

	public AntiPathCrowdingManager AntiPathCrowdingManager => antiPathCrowdingManager;

	public RaidManager RaidManager => raidManager;

	public CommanderAIManager CommanderAIManager => commanderAIManager;

	public MapNode[] GridSpaceData => gridData;

	public Dictionary<int, HashSet<CreatureBase>> CreaturesOnNodes => creaturesOnNodes;

	internal SerializableHashSet<Vec3Int> CropBlightPositions => cropBlightPositions;

	public HomeArea HomeArea => homeArea;

	public event Action<MapNode> NodeFlammabilityChangedEvent;

	public event Action<MapNode, MapNodeTags> NodeTagChangedEvent;

	public event Action<MapNode, bool> NodeIsShadowCasterChangedEvent;

	public event Action<int, CoverageType> CoverageChangedEvent;

	public event Action<IGameDisposable> OnDisposedEvent;

	public event Action OnVillageDisposeEvent;

	public event Action<WorldObject> ObjectPlacedEvent;

	public event Action<WorldObject> ObjectRemovedEvent;

	public event Action<Region, MapNode> OnNodeAddedToRegionEvent;

	public event Action<Region, MapNode> OnNodeRemovedFromRegionEvent;

	public event Action<MapNode> OnNodeVoxelTypeChangedEvent;

	public event Action<MapNode> NodeWalkabilityChangedEvent;

	public event Action<MapNode, GridDataType> NodeGridDataTypeChangedEvent;

	public event Action<Region, bool> OnRegionBecameRoofedEvent;

	public event Action<Region, Area> OnRegionRemovedFromAreaEvent;

	public event Action<MapNode> DrawbridgeOpenedEvent;

	public event Action<MapNode> DrawbridgeClosedEvent;

	public VillageMap()
	{
	}

	public void Dispose()
	{
		if (HasDisposed)
		{
			return;
		}
		this.OnDisposedEvent?.Invoke(this);
		this.OnDisposedEvent = null;
		this.OnVillageDisposeEvent?.Invoke();
		this.OnVillageDisposeEvent = null;
		HasDisposed = true;
		beautyManager?.Dispose();
		regionManager?.Dispose();
		regionAreaManager?.Dispose();
		roomDetection?.Dispose();
		temperatureManager?.Dispose();
		effects3dTextureManager?.Dispose();
		snowGrassWetnessManager?.Dispose();
		protectorCreatureManager?.Dispose();
		protectorBuildingManager?.Dispose();
		idlePoints?.Dispose();
		idlePointManager?.Dispose();
		if (waterManager != null)
		{
			waterManager.WaterLevelChangedEvent -= OnWaterLevelChanged;
			waterManager.Dispose();
		}
		if (MonoSingleton<FireController>.IsInstantiated())
		{
			MonoSingleton<FireController>.Instance.FireAddedEvent -= OnFireAdded;
			MonoSingleton<FireController>.Instance.FireRemovedEvent -= OnFireRemoved;
		}
		fireSimLogic?.Dispose();
		fireMeshLogic?.Dispose();
		waterDebugDrawLogic?.Dispose();
		firePresenceGrid?.Dispose();
		buildingsManagerMain?.Dispose();
		beamComponentManager?.Dispose();
		ladderComponentManager?.Dispose();
		bedComponentManager?.Dispose();
		socketComponentManager?.Dispose();
		chairComponentManager?.Dispose();
		doorComponentManager?.Dispose();
		fuelConsumerComponentManager?.Dispose();
		graveComponentManager?.Dispose();
		productionComponentBuildingManager?.Dispose();
		penMarkerComponentManager?.Dispose();
		rallyPointMarkerComponentManager?.Dispose();
		trapComponentsManager?.Dispose();
		roofComponentManager?.Dispose();
		rugComponentManager?.Dispose();
		shelfComponentManager?.Dispose();
		shrineComponentManager?.Dispose();
		signComponentManager?.Dispose();
		stairsComponentManager?.Dispose();
		tableComponentManager?.Dispose();
		windowComponentManager?.Dispose();
		entertainmentComponentManager?.Dispose();
		decorationComponentManager?.Dispose();
		tradingPostComponentManager?.Dispose();
		caravanPostComponentManager?.Dispose();
		mapTableComponentManager?.Dispose();
		gallowsComponentManager?.Dispose();
		wellComponentManager?.Dispose();
		enemyBuildingsManager?.Dispose();
		siegeWeaponComponentManager?.Dispose();
		stabilityManager?.Dispose();
		globalEffectorsManager?.Dispose();
		antiPathCrowdingManager?.Dispose();
		fireAudioAndLights?.Dispose();
		raidManager?.Dispose();
		homeArea?.Dispose();
		commanderAIManager?.Dispose();
		secondMapLeaveManager?.Dispose();
		debugEventLog?.Dispose();
		uniqueResourceTracker?.Dispose();
		animatorDisableManager?.Dispose();
		bellComponentManager?.Dispose();
		merlonRotationManager?.Dispose();
		vinesManager?.Dispose();
		roofMeshVariationManager?.Dispose();
		floorAutomaticMeshVariationManager?.Dispose();
		wallAutomaticMeshVariationManager?.Dispose();
		fenceAutomaticMeshVariationManager?.Dispose();
		pathLengthEstimator?.Dispose();
		this.ObjectRemovedEvent = null;
		beautyManager = null;
		regionManager = null;
		regionAreaManager = null;
		roomDetection = null;
		temperatureManager = null;
		effects3dTextureManager = null;
		snowGrassWetnessManager = null;
		protectorCreatureManager = null;
		protectorBuildingManager = null;
		idlePoints = null;
		idlePointManager = null;
		waterManager = null;
		fireSimLogic = null;
		fireMeshLogic = null;
		waterDebugDrawLogic = null;
		firePresenceGrid = null;
		buildingsManagerMain = null;
		beamComponentManager = null;
		ladderComponentManager = null;
		bedComponentManager = null;
		socketComponentManager = null;
		chairComponentManager = null;
		doorComponentManager = null;
		fuelConsumerComponentManager = null;
		graveComponentManager = null;
		productionComponentBuildingManager = null;
		penMarkerComponentManager = null;
		rallyPointMarkerComponentManager = null;
		trapComponentsManager = null;
		roofComponentManager = null;
		rugComponentManager = null;
		shelfComponentManager = null;
		shrineComponentManager = null;
		signComponentManager = null;
		stairsComponentManager = null;
		tableComponentManager = null;
		windowComponentManager = null;
		entertainmentComponentManager = null;
		decorationComponentManager = null;
		tradingPostComponentManager = null;
		caravanPostComponentManager = null;
		mapTableComponentManager = null;
		gallowsComponentManager = null;
		wellComponentManager = null;
		enemyBuildingsManager = null;
		siegeWeaponComponentManager = null;
		stabilityManager = null;
		globalEffectorsManager = null;
		antiPathCrowdingManager = null;
		fireAudioAndLights = null;
		raidManager = null;
		homeArea = null;
		commanderAIManager = null;
		secondMapLeaveManager = null;
		debugEventLog = null;
		uniqueResourceTracker = null;
		animatorDisableManager = null;
		bellComponentManager = null;
		merlonRotationManager = null;
		vinesManager = null;
		roofMeshVariationManager = null;
		floorAutomaticMeshVariationManager = null;
		wallAutomaticMeshVariationManager = null;
		fenceAutomaticMeshVariationManager = null;
		pathLengthEstimator = null;
		if (gridData != null)
		{
			MapNode[] array = gridData;
			for (int i = 0; i < array.Length; i++)
			{
				array[i].Destroy();
			}
		}
		gridData = null;
		villageInstance = null;
		if (creaturesOnNodes != null)
		{
			foreach (HashSet<CreatureBase> value in creaturesOnNodes.Values)
			{
				HashSetPool<CreatureBase>.Return(value);
			}
			creaturesOnNodes.Clear();
		}
		this.NodeWalkabilityChangedEvent = null;
		this.OnNodeVoxelTypeChangedEvent = null;
		this.CoverageChangedEvent = null;
		this.ObjectPlacedEvent = null;
		this.NodeFlammabilityChangedEvent = null;
		this.NodeTagChangedEvent = null;
		this.NodeWalkabilityChangedEvent = null;
		this.OnRegionBecameRoofedEvent = null;
		this.NodeIsShadowCasterChangedEvent = null;
		this.OnNodeAddedToRegionEvent = null;
		this.OnNodeRemovedFromRegionEvent = null;
		this.OnNodeVoxelTypeChangedEvent = null;
		this.OnRegionRemovedFromAreaEvent = null;
		this.DrawbridgeOpenedEvent = null;
		this.DrawbridgeClosedEvent = null;
		this.NodeGridDataTypeChangedEvent = null;
		nodesHolder?.Dispose();
		nodesHolder = null;
		foreach (HashSet<WorldObject> value2 in worldObjectsByType.Values)
		{
			value2.Clear();
		}
		worldObjectsByType?.Clear();
		worldObjectsByType = null;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public MapNode GetNode(in Vec3Int gridPosition)
	{
		return GetNode(gridPosition.x, gridPosition.y, gridPosition.z);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public MapNode GetNode(int x, int y, int z)
	{
		int num = GridDataIndexTools.FastTo1DIndex(x, y, z);
		if (num > -1 && num < gridData.Length)
		{
			return gridData[num];
		}
		return null;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public MapNode GetNode(int index)
	{
		return gridData[index];
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public MapNode GetNodeByWorldPos(Vector3 worldPos)
	{
		Vec3Int gridPosition = GridUtils.GetGridPosition(worldPos);
		return GetNode(in gridPosition);
	}

	public T GetWorldObject<T>(GridDataType dataType, Vec3Int position) where T : WorldObject
	{
		return (T)GetWorldObject(dataType, position);
	}

	public WorldObject GetWorldObject(GridDataType dataType, Vec3Int position)
	{
		return GetNode(in position)?.GetWorldObject(dataType);
	}

	public List<T> GetWorldObjectsList<T>(GridDataType dataType, bool distinct = false) where T : WorldObject
	{
		IEnumerable<T> source = GetWorldObjects(dataType).OfType<T>();
		if (distinct)
		{
			return source.Distinct().ToList();
		}
		return source.ToList();
	}

	public IEnumerable<WorldObject> GetWorldObjects(GridDataType dataType)
	{
		if ((dataType & (dataType - 1)) == 0)
		{
			return worldObjectsByType[dataType];
		}
		IEnumerable<WorldObject> enumerable = null;
		foreach (KeyValuePair<GridDataType, HashSet<WorldObject>> item in worldObjectsByType)
		{
			if ((dataType & item.Key) != 0)
			{
				enumerable = ((enumerable != null) ? enumerable.Concat(item.Value) : item.Value);
			}
		}
		return enumerable;
	}

	public int GetObjectCount(GridDataType dataType, Func<WorldObject, bool> condition = null)
	{
		if (dataType == GridDataType.None)
		{
			return 0;
		}
		if ((dataType & (dataType - 1)) == 0)
		{
			if (condition != null)
			{
				return worldObjectsByType[dataType].Count(condition.Invoke);
			}
			return worldObjectsByType[dataType].Count;
		}
		int num = 0;
		if (condition == null)
		{
			foreach (KeyValuePair<GridDataType, HashSet<WorldObject>> item in worldObjectsByType)
			{
				if ((dataType & item.Key) != 0)
				{
					num += item.Value.Count;
				}
			}
		}
		else
		{
			foreach (KeyValuePair<GridDataType, HashSet<WorldObject>> item2 in worldObjectsByType)
			{
				if ((dataType & item2.Key) != 0)
				{
					num += item2.Value.Count(condition.Invoke);
				}
			}
		}
		return num;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool IsBlockInRange(int x, int y, int z)
	{
		if (x >= 0 && x < Size.x && y >= 0 && y < Size.y && z >= 0)
		{
			return z < Size.z;
		}
		return false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool IsBlockInRange(Vec3Int gridPosition)
	{
		return IsBlockInRange(gridPosition.x, gridPosition.y, gridPosition.z);
	}

	public void AddToTheWorld(WorldObject occupant, bool isSilent = false)
	{
		if (occupant.GridDataType == GridDataType.None)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(74, 4, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Village\\Map\\VillageMap.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("AddToTheWorld failed on WorldObject: ");
				messageBuilder.AppendFormatted(occupant);
				messageBuilder.AppendLiteral(" (GridDataType: ");
				messageBuilder.AppendFormatted(occupant.GridDataType);
				messageBuilder.AppendLiteral(", Type: ");
				messageBuilder.AppendFormatted(occupant.Type);
				messageBuilder.AppendLiteral(", Position: ");
				messageBuilder.AppendFormatted(occupant.GridDataPosition);
				messageBuilder.AppendLiteral(")");
			}
			Log.Warning(messageBuilder);
			return;
		}
		villageInstance.WorldObjectStorage.WorldObjects.Add(occupant);
		if (!worldObjectsByType[occupant.GridDataType].Add(occupant))
		{
			return;
		}
		List<Vec3Int> positions = occupant.Positions;
		if ((positions == null || positions.Count <= 1) && occupant.Type != WorldObjectType.Stockpile && occupant.Type != WorldObjectType.Cropfield)
		{
			Vec3Int gridPosition = occupant.GridDataPosition;
			MapNode node = GetNode(in gridPosition);
			if (node == null)
			{
				Log.Error("Tried to add WorldObject on invalid position " + occupant.GridDataPosition.ToString() + " " + occupant, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Village\\Map\\VillageMap.cs");
				villageInstance.WorldObjectStorage.WorldObjects.Remove(occupant);
				worldObjectsByType[occupant.GridDataType].Remove(occupant);
				return;
			}
			node.AddObject(occupant, isSilent);
		}
		else
		{
			foreach (Vec3Int position in occupant.Positions)
			{
				Vec3Int gridPosition2 = position;
				GetNode(in gridPosition2)?.AddObject(occupant, isSilent);
			}
		}
		if (!isSilent)
		{
			this.ObjectPlacedEvent?.Invoke(occupant);
		}
	}

	public void AddAreaToTheWorld(WorldObject occupant, bool isSilent = false)
	{
		villageInstance.WorldObjectStorage.WorldObjects.Add(occupant);
		if (!worldObjectsByType[occupant.GridDataType].Add(occupant))
		{
			return;
		}
		foreach (Vec3Int position in occupant.Positions)
		{
			Vec3Int gridPosition = position;
			GetNode(in gridPosition)?.AddObject(occupant, isSilent);
		}
		if (!isSilent)
		{
			this.ObjectPlacedEvent?.Invoke(occupant);
		}
	}

	public void RemoveFromWorld(WorldObject occupant)
	{
		villageInstance.WorldObjectStorage.WorldObjects.Remove(occupant);
		GridDataType gridDataType = occupant.GridDataType;
		if (!worldObjectsByType.ContainsKey(gridDataType) || !worldObjectsByType[gridDataType].Remove(occupant))
		{
			return;
		}
		List<Vec3Int> positions = occupant.Positions;
		if ((positions == null || positions.Count <= 1) && occupant.Type != WorldObjectType.Stockpile && occupant.Type != WorldObjectType.Cropfield)
		{
			bool num = (occupant.GetNode().Tag & MapNodeTags.Ladder) != 0;
			bool flag = occupant is BaseBuildingInstance { ConstructionPhase: ConstructionPhase.Finished } baseBuildingInstance && baseBuildingInstance.Blueprint.IsWallTypeBuildingWithVerticalStability();
			occupant.GetNode()?.RemoveObject(occupant);
			this.ObjectRemovedEvent?.Invoke(occupant);
			if (num)
			{
				foreach (MapNode item in MapNodeUtils.IterateEachNeighbor(occupant.GetNode()))
				{
					item.ForceRefresh();
				}
			}
			if (flag)
			{
				occupant.GetNode()?.GetNodeAbove()?.ForceRefresh();
			}
			return;
		}
		occupant.GetNode()?.RemoveObject(occupant);
		foreach (Vec3Int position in occupant.Positions)
		{
			Vec3Int gridPosition = position;
			MapNode node = GetNode(in gridPosition);
			if (node != null && node.DataType.HasFlag(gridDataType))
			{
				node.RemoveObject(occupant);
				node.ForceRefreshWithNeighbours();
			}
		}
		this.ObjectRemovedEvent?.Invoke(occupant);
	}

	public void RemoveAreaFromWorld(WorldObject occupant)
	{
		villageInstance.WorldObjectStorage.WorldObjects.Remove(occupant);
		GridDataType gridDataType = occupant.GridDataType;
		if (!worldObjectsByType[gridDataType].Remove(occupant))
		{
			return;
		}
		if (occupant.Positions == null)
		{
			occupant.GetNode()?.RemoveObject(occupant);
			this.ObjectRemovedEvent?.Invoke(occupant);
			return;
		}
		foreach (Vec3Int position in occupant.Positions)
		{
			Vec3Int gridPosition = position;
			MapNode node = GetNode(in gridPosition);
			if (node != null && node.DataType.HasFlag(gridDataType))
			{
				node.RemoveObject(occupant);
			}
		}
		this.ObjectRemovedEvent?.Invoke(occupant);
	}

	public void OnWorldObjectDataTypeChanged(WorldObject occupant, GridDataType oldType)
	{
		if (oldType != 0 && !worldObjectsByType[oldType].Remove(occupant))
		{
			return;
		}
		worldObjectsByType[occupant.GridDataType].Add(occupant);
		if (occupant.Positions == null || occupant.Positions.Count <= 1)
		{
			occupant.GetNode().OnObjectDataTypeChanged(occupant, oldType);
			return;
		}
		foreach (Vec3Int position in occupant.Positions)
		{
			Vec3Int gridPosition = position;
			GetNode(in gridPosition)?.OnObjectDataTypeChanged(occupant, oldType);
		}
	}

	public void OnWorldObjectSizeChanged(WorldObject occupant)
	{
		int y = occupant.GridDataPosition.y;
		for (int i = 0; i < Size.x; i++)
		{
			for (int j = 0; j < Size.z; j++)
			{
				MapNode node = GetNode(i, y, j);
				if (node.CheckIsDataType(occupant.GridDataType) && node.WorldObjects.Contains(occupant))
				{
					node.RemoveObject(occupant);
				}
			}
		}
		if (occupant.Positions.Count == 1)
		{
			Vec3Int gridPosition = occupant.Positions.First();
			GetNode(in gridPosition).AddObject(occupant);
			return;
		}
		foreach (Vec3Int position in occupant.Positions)
		{
			GetNode(position.x, position.y, position.z).AddObject(occupant);
		}
	}

	public bool IsEmpty(Vec3Int gridPosition)
	{
		MapNode node = GetNode(in gridPosition);
		if (node == null)
		{
			return false;
		}
		return node.DataType == GridDataType.None;
	}

	public bool ResourceExists(Vec3Int gridPosition)
	{
		MapNode node = GetNode(in gridPosition);
		if (node == null)
		{
			return false;
		}
		GridDataType dataType = node.DataType;
		GridDataType gridDataType = GridDataType.DigMarkerResource | GridDataType.DigMarkerResourceToMine | GridDataType.PlantMapResource;
		return (dataType & gridDataType) != 0;
	}

	public bool ResourcePileExists(Vec3Int gridPosition)
	{
		return GetNode(in gridPosition).DataType.HasFlag(GridDataType.ResourcePile);
	}

	public void NodeAddedToRegion(Region region, MapNode node)
	{
		this.OnNodeAddedToRegionEvent?.Invoke(region, node);
	}

	public void NodeRemovedFromRegion(Region region, MapNode node)
	{
		this.OnNodeRemovedFromRegionEvent?.Invoke(region, node);
	}

	public void NodeVoxelTypeChanged(MapNode node)
	{
		this.OnNodeVoxelTypeChangedEvent?.Invoke(node);
	}

	public void NodeDrawbridgeOpened(MapNode node)
	{
		this.DrawbridgeOpenedEvent?.Invoke(node);
	}

	public void NodeDrawbridgeClosed(MapNode node)
	{
		this.DrawbridgeClosedEvent?.Invoke(node);
	}

	public void NodeWalkabilityChanged(MapNode node)
	{
		this.NodeWalkabilityChangedEvent?.Invoke(node);
	}

	public void NodeGridDataTypeChanged(MapNode node, GridDataType oldType)
	{
		this.NodeGridDataTypeChangedEvent?.Invoke(node, oldType);
	}

	public void RegionBecameRoofed(Region region, bool isRoofed)
	{
		this.OnRegionBecameRoofedEvent?.Invoke(region, isRoofed);
	}

	public void RegionRemovedFromArea(Region region, Area area)
	{
		this.OnRegionRemovedFromAreaEvent?.Invoke(region, area);
	}

	internal void GenerateEmptyData(Vec3Int mapSize, VillageInstance village)
	{
		villageInstance = village;
		if (village == null)
		{
			Log.Error("VillageMap can not ever be initialized after load without village", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Village\\Map\\VillageMap.cs");
			return;
		}
		GridDataIndexTools.InitialiseFastMethods(mapSize.x, mapSize.y, mapSize.z);
		creaturesOnNodes = new Dictionary<int, HashSet<CreatureBase>>();
		size = new Vec3Int(mapSize.x, mapSize.y, mapSize.z);
		gridData = new MapNode[mapSize.x * mapSize.y * mapSize.z];
		nodesHolder = new MapNodesHolder(gridData, size);
		cropBlightPositions = new SerializableHashSet<Vec3Int>();
		totalFireDamageSinceBurning = 0f;
		fireStartTimeMinutes = 0L;
		for (int i = 0; i < gridData.Length; i++)
		{
			gridData[i] = new MapNode(GridDataIndexTools.FastTo3DIndex(i));
			gridData[i].InitAllocations();
		}
		waterManager = new WaterManager(this);
		for (int j = 0; j < gridData.Length; j++)
		{
			gridData[j].Initialize(this);
			gridData[j].SetAboveBelowNodes();
		}
		worldObjectsByType = new Dictionary<GridDataType, HashSet<WorldObject>>();
		GridDataType[] gridDataTypes = EnumValues.GridDataTypes;
		foreach (GridDataType gridDataType in gridDataTypes)
		{
			if (gridDataType != 0 && gridDataType != GridDataType.All)
			{
				worldObjectsByType.Add(gridDataType, new HashSet<WorldObject>());
			}
		}
		beautyManager = new BeautyManager();
		beautyManager.Initialize(this);
		temperatureManager = new TemperatureManager();
		temperatureManager.Initialize(this);
		effects3dTextureManager = new Effects3dTextureManager();
		effects3dTextureManager.Initialize(this);
		snowGrassWetnessManager = new SnowGrassWetnessManager();
		snowGrassWetnessManager.Initialize(this);
		protectorCreatureManager = new ProtectorCreatureManager();
		protectorCreatureManager.Initialize(this);
		protectorBuildingManager = new ProtectorBuildingManager();
		protectorBuildingManager.Initialize(this);
		idlePoints = new IdlePoints();
		idlePoints.Initialize(this);
		idlePointManager = new IdlePointManager();
		regionManager = new VillageMapRegionManager(this);
		regionAreaManager = new RegionAreaManager(this);
		roomDetection = new NSMedieval.RoomDetection.RoomDetection(this);
		buildingsManagerMain = new BuildingsManagerMain(this);
		beamComponentManager = new BeamComponentManager(this);
		ladderComponentManager = new LadderComponentManager(this);
		bedComponentManager = new BedComponentManager(this);
		socketComponentManager = new SocketComponentManager(this);
		chairComponentManager = new ChairComponentManager(this);
		doorComponentManager = new DoorComponentManager(this);
		fuelConsumerComponentManager = new FuelConsumerComponentManager(this);
		graveComponentManager = new GraveComponentManager(this);
		productionComponentBuildingManager = new ProductionComponentBuildingManager(this);
		penMarkerComponentManager = new PenMarkerComponentManager(this);
		rallyPointMarkerComponentManager = new RallyPointMarkerComponentManager(this);
		trapComponentsManager = new TrapComponentsManager(this);
		roofComponentManager = new RoofComponentManager(this);
		rugComponentManager = new RugComponentManager(this);
		shelfComponentManager = new ShelfComponentManager(this);
		shrineComponentManager = new ShrineComponentManager(this);
		signComponentManager = new SignComponentManager(this);
		stairsComponentManager = new StairsComponentManager(this);
		tableComponentManager = new TableComponentManager(this);
		windowComponentManager = new WindowComponentManager(this);
		entertainmentComponentManager = new EntertainmentComponentManager(this);
		decorationComponentManager = new DecorationComponentManager(this);
		tradingPostComponentManager = new TradingPostComponentManager(this);
		caravanPostComponentManager = new CaravanPostComponentManager(this);
		mapTableComponentManager = new MapTableComponentManager(this);
		gallowsComponentManager = new GallowsComponentManager(this);
		wellComponentManager = new WellComponentManager(this);
		siegeWeaponComponentManager = new SiegeWeaponComponentManager(this);
		bellComponentManager = new BellComponentManager(this);
		merlonRotationManager = new MerlonRotationManager(this);
		vinesManager = new VinesManager(this);
		roofMeshVariationManager = new RoofMeshVariationManager(this);
		floorAutomaticMeshVariationManager = new FloorAutomaticMeshVariationManager(this);
		wallAutomaticMeshVariationManager = new WallAutomaticMeshVariationManager(this);
		fenceAutomaticMeshVariationManager = new FenceAutomaticMeshVariationManager(this);
		pathLengthEstimator = new PathLengthEstimator(this);
		enemyBuildingsManager = new EnemyBuildingsManager(this);
		stabilityManager = new StabilityManager(this);
		globalEffectorsManager = new GlobalEffectorsManager(this);
		antiPathCrowdingManager = new AntiPathCrowdingManager();
		raidManager = new RaidManager(this);
		commanderAIManager = new CommanderAIManager(this);
		homeArea = new HomeArea();
		homeArea.Initialize(this);
		waterManager.WaterLevelChangedEvent += OnWaterLevelChanged;
		fireSimLogic = new FireSimLogic(size.x, size.y, size.z, this);
		fireMeshLogic = new FireMeshLogic();
		fireMeshLogic.Initialize(fireSimLogic, this);
		InitNodesOnFire();
		fireAudioAndLights = new FireAudioAndLights();
		fireAudioAndLights.Initialize(this);
		firePresenceGrid = new FirePresenceGrid(size);
		idlePointManager.Initialize(this);
		waterDebugDrawLogic = new WaterDebugDrawLogic(this);
		secondMapLeaveManager = new SecondMapLeaveManager(this);
		debugEventLog = new DebugEventLog();
		uniqueResourceTracker = new UniqueResourceTracker();
		animatorDisableManager = new AnimatorDisableManager();
		MonoSingleton<FireController>.Instance.FireAddedEvent += OnFireAdded;
		MonoSingleton<FireController>.Instance.FireRemovedEvent += OnFireRemoved;
	}

	internal void InitAfterLoad(VillageInstance village)
	{
		villageInstance = village;
		if (village == null)
		{
			Log.Error("VillageMap can not ever be initialized after load without village", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Village\\Map\\VillageMap.cs");
			return;
		}
		GridDataIndexTools.InitialiseFastMethods(size.x, size.y, size.z);
		creaturesOnNodes = new Dictionary<int, HashSet<CreatureBase>>();
		worldObjectsByType = new Dictionary<GridDataType, HashSet<WorldObject>>();
		foreach (GridDataType value in Enum.GetValues(typeof(GridDataType)))
		{
			if (value != 0 && value != GridDataType.All)
			{
				worldObjectsByType.Add(value, new HashSet<WorldObject>());
			}
		}
		waterManager = new WaterManager(this);
		bool isEnabled;
		if (nodesHolder?.GridData != null)
		{
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(64, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Village\\Map\\VillageMap.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("AfterDeserialize: Loading new save, setting grid data. Length = ");
				messageBuilder.AppendFormatted(nodesHolder.GridData.Length);
			}
			Log.Info(messageBuilder);
			gridData = nodesHolder.GridData;
			MapNode[] array = gridData;
			foreach (MapNode obj in array)
			{
				obj.SetMap(this);
				obj.SetAboveBelowNodes();
			}
		}
		else
		{
			nodesHolder = new MapNodesHolder(gridData, size);
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(64, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Village\\Map\\VillageMap.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("AfterDeserialize: Loading old save, setting grid data. Length = ");
				messageBuilder.AppendFormatted(gridData);
			}
			Log.Info(messageBuilder);
			MapNode[] array = gridData;
			foreach (MapNode obj2 in array)
			{
				obj2.ReInitialize(this);
				obj2.SetAboveBelowNodes();
			}
		}
		beautyManager = new BeautyManager();
		beautyManager.Initialize(this);
		temperatureManager = new TemperatureManager();
		temperatureManager.Initialize(this);
		temperatureManager.ReadFromBinaryData(villageInstance.TemperatureBytesLoaded);
		villageInstance.TemperatureBytesLoaded = null;
		effects3dTextureManager = new Effects3dTextureManager();
		effects3dTextureManager.Initialize(this);
		fireSimLogic = new FireSimLogic(size.x, size.y, size.z, this);
		fireMeshLogic = new FireMeshLogic();
		fireMeshLogic.Initialize(fireSimLogic, this);
		fireSimLogic.ReadFromBinaryData(villageInstance.FireDataBytes);
		firePresenceGrid = new FirePresenceGrid(size);
		snowGrassWetnessManager = new SnowGrassWetnessManager();
		snowGrassWetnessManager.Initialize(this);
		snowGrassWetnessManager.ReadFromBinaryData(villageInstance.SnowGrassWetDataBytesLoaded);
		villageInstance.SnowGrassWetDataBytesLoaded = null;
		protectorCreatureManager = new ProtectorCreatureManager();
		protectorCreatureManager.Initialize(this);
		protectorBuildingManager = new ProtectorBuildingManager();
		protectorBuildingManager.Initialize(this);
		waterManager.WaterSimLogic.ReadFromBinaryData(villageInstance.WaterDataBytes);
		waterManager.WaterSimLogic.AfterDeserialize();
		waterManager.WaterLevelChangedEvent += OnWaterLevelChanged;
		MonoSingleton<FireController>.Instance.FireAddedEvent += OnFireAdded;
		MonoSingleton<FireController>.Instance.FireRemovedEvent += OnFireRemoved;
		InitNodesOnFire();
		hasIgnoredInitialWaterUpdate = true;
		villageInstance.WaterDataBytes = null;
		villageInstance.FireDataBytes = null;
		idlePoints = new IdlePoints();
		idlePoints.Initialize(this);
		idlePointManager = new IdlePointManager();
		idlePointManager.Initialize(this);
		regionManager = new VillageMapRegionManager(this);
		regionAreaManager = new RegionAreaManager(this);
		roomDetection = new NSMedieval.RoomDetection.RoomDetection(this);
		buildingsManagerMain = new BuildingsManagerMain(this);
		beamComponentManager = new BeamComponentManager(this);
		ladderComponentManager = new LadderComponentManager(this);
		bedComponentManager = new BedComponentManager(this);
		socketComponentManager = new SocketComponentManager(this);
		chairComponentManager = new ChairComponentManager(this);
		doorComponentManager = new DoorComponentManager(this);
		fuelConsumerComponentManager = new FuelConsumerComponentManager(this);
		graveComponentManager = new GraveComponentManager(this);
		productionComponentBuildingManager = new ProductionComponentBuildingManager(this);
		penMarkerComponentManager = new PenMarkerComponentManager(this);
		rallyPointMarkerComponentManager = new RallyPointMarkerComponentManager(this);
		trapComponentsManager = new TrapComponentsManager(this);
		roofComponentManager = new RoofComponentManager(this);
		rugComponentManager = new RugComponentManager(this);
		shelfComponentManager = new ShelfComponentManager(this);
		shrineComponentManager = new ShrineComponentManager(this);
		signComponentManager = new SignComponentManager(this);
		stairsComponentManager = new StairsComponentManager(this);
		tableComponentManager = new TableComponentManager(this);
		windowComponentManager = new WindowComponentManager(this);
		entertainmentComponentManager = new EntertainmentComponentManager(this);
		decorationComponentManager = new DecorationComponentManager(this);
		tradingPostComponentManager = new TradingPostComponentManager(this);
		caravanPostComponentManager = new CaravanPostComponentManager(this);
		mapTableComponentManager = new MapTableComponentManager(this);
		gallowsComponentManager = new GallowsComponentManager(this);
		bellComponentManager = new BellComponentManager(this);
		merlonRotationManager = new MerlonRotationManager(this);
		vinesManager = new VinesManager(this);
		roofMeshVariationManager = new RoofMeshVariationManager(this);
		floorAutomaticMeshVariationManager = new FloorAutomaticMeshVariationManager(this);
		wallAutomaticMeshVariationManager = new WallAutomaticMeshVariationManager(this);
		fenceAutomaticMeshVariationManager = new FenceAutomaticMeshVariationManager(this);
		pathLengthEstimator = new PathLengthEstimator(this);
		antiPathCrowdingManager = new AntiPathCrowdingManager();
		wellComponentManager = new WellComponentManager(this);
		enemyBuildingsManager = new EnemyBuildingsManager(this);
		siegeWeaponComponentManager = new SiegeWeaponComponentManager(this);
		raidManager = new RaidManager(this);
		commanderAIManager = new CommanderAIManager(this);
		homeArea = new HomeArea();
		homeArea.Initialize(this);
		stabilityManager = new StabilityManager(this);
		globalEffectorsManager = new GlobalEffectorsManager(this);
		globalEffectorsManager.InitAfterLoad();
		fireAudioAndLights = new FireAudioAndLights();
		fireAudioAndLights.Initialize(this);
		secondMapLeaveManager = new SecondMapLeaveManager(this);
		debugEventLog = new DebugEventLog();
		uniqueResourceTracker = new UniqueResourceTracker();
		animatorDisableManager = new AnimatorDisableManager();
		waterDebugDrawLogic = new WaterDebugDrawLogic(this);
		buildingsManagerMain.ConstructionJobManager.LoadSavedData(constructionJobManagerSaveData);
	}

	private void InitNodesOnFire()
	{
		foreach (int item in FireSimLogic.NodesOnFire())
		{
			gridData[item].IsFire = true;
		}
	}

	private void OnFireRemoved(ISet<int> indicesRemoved)
	{
		foreach (int item in indicesRemoved)
		{
			gridData[item].IsFire = false;
			gridData[item].ForceRefresh();
		}
	}

	private void OnFireAdded(NativeParallelHashSet<int> indicesAdded)
	{
		foreach (int item in indicesAdded)
		{
			gridData[item].IsFire = true;
			gridData[item].ForceRefresh();
		}
	}

	private void OnWaterLevelChanged(HashSet<int> nodesChanged, HashSet<int> nodesChangedNeighbors)
	{
		if (!hasIgnoredInitialWaterUpdate)
		{
			hasIgnoredInitialWaterUpdate = true;
			return;
		}
		foreach (int item in nodesChanged)
		{
			GridSpaceData[item].DisableRecursion = true;
		}
		foreach (int item2 in nodesChanged)
		{
			MapNode mapNode = GridSpaceData[item2];
			mapNode.ForceRefresh();
			regionManager.MapNodeStateChanged(mapNode);
		}
		foreach (int nodesChangedNeighbor in nodesChangedNeighbors)
		{
			MapNode mapNode2 = GridSpaceData[nodesChangedNeighbor];
			mapNode2.ForceRefresh();
			regionManager.MapNodeStateChanged(mapNode2);
		}
		foreach (int item3 in nodesChanged)
		{
			GridSpaceData[item3].DisableRecursion = false;
		}
	}

	internal void RefreshAllNodes()
	{
		Log.Info("RefreshAllNodes", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Village\\Map\\VillageMap.cs");
		for (int i = 0; i < GridSpaceData.Length; i++)
		{
			GridSpaceData[i].ForceRefresh();
		}
	}

	public void BeforeSerialize()
	{
		GlobalSaveController.CurrentVillageData.CommanderAICommanders.Clear();
		GlobalSaveController.CurrentVillageData.CommanderAICommanders.AddRange(commanderAIManager.Commanders);
	}

	public void AfterDeserialize()
	{
		if (nodesHolder?.GridData != null)
		{
			gridData = nodesHolder.GridData;
		}
	}

	public void Serialize(FVSerializer serializer)
	{
		BeforeSerialize();
		serializer.Write("nodesHolder", nodesHolder);
		serializer.Write("size", size);
		serializer.Write("cropBlightPositions", cropBlightPositions.Set);
		serializer.Write("globalEffectorsManager", globalEffectorsManager);
		serializer.Write("totalFireDamageSinceBurning", totalFireDamageSinceBurning);
		serializer.Write("fireStartTimeMinutes", fireStartTimeMinutes);
		if (constructionJobManagerSaveData == null)
		{
			constructionJobManagerSaveData = new ConstructionJobManager.SaveData();
		}
		buildingsManagerMain.ConstructionJobManager.WriteSaveData(constructionJobManagerSaveData);
		serializer.Write("constructionJobManagerSaveData", constructionJobManagerSaveData);
	}

	public VillageMap(FVDeserializer deserializer)
	{
		nodesHolder = deserializer.ReadObject<MapNodesHolder>("nodesHolder");
		size = deserializer.ReadVec3Int("size");
		cropBlightPositions = new SerializableHashSet<Vec3Int>
		{
			Set = deserializer.ReadObjectHashSet<Vec3Int>("cropBlightPositions")
		};
		globalEffectorsManager = deserializer.ReadObject<GlobalEffectorsManager>("globalEffectorsManager");
		totalFireDamageSinceBurning = deserializer.ReadFloat("totalFireDamageSinceBurning");
		fireStartTimeMinutes = deserializer.ReadLong("fireStartTimeMinutes", 0L);
		constructionJobManagerSaveData = deserializer.ReadObject<ConstructionJobManager.SaveData>("constructionJobManagerSaveData") ?? new ConstructionJobManager.SaveData();
		AfterDeserialize();
	}

	public void NodeFlammabilityChanged(MapNode mapNode)
	{
		this.NodeFlammabilityChangedEvent?.Invoke(mapNode);
	}

	public void NodeTagChanged(MapNode mapNode, MapNodeTags oldTag)
	{
		this.NodeTagChangedEvent?.Invoke(mapNode, oldTag);
	}

	public void NodeIsShadowCasterChanged(MapNode mapNode, bool isShadowCaster)
	{
		this.NodeIsShadowCasterChangedEvent?.Invoke(mapNode, isShadowCaster);
	}

	public void CoverageChanged(int index, CoverageType coverage)
	{
		this.CoverageChangedEvent?.Invoke(index, coverage);
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
