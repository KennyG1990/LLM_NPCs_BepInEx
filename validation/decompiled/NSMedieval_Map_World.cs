using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Controllers;
using NSMedieval.Enums;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Model.MapNew;
using NSMedieval.Repository;
using NSMedieval.Sound;
using NSMedieval.Terrain;
using NSMedieval.Tools;
using NSMedieval.UI;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using NSMedieval.Village.Map.Pathfinding;
using NSMedieval.WorldMap;
using UnityEngine;

namespace NSMedieval.Map;

public class World : MonoSingleton<World>
{
	public delegate void OnMapLoadedEvent(bool wasLoadedFromSave);

	public static readonly int MapBlockHeight = 3;

	public readonly byte SpaceOccupied = 10;

	private int terrainLayer = -1;

	private bool allEdgeFrameChunksGenerated;

	[NonSerialized]
	private List<MapNode> borderNodes;

	[SerializeField]
	private ChunkGenerator chunkGenerator;

	private int worldSizeX;

	private int worldSizeY;

	private int worldSizeZ;

	private Vec3Int center;

	private float layerLevel;

	private int elevationLevel;

	private int maxElevation;

	[NonSerialized]
	private VillageMap villageMap;

	[NonSerialized]
	private List<FloodfillArea> largestAreas;

	[NonSerialized]
	private int minPlacementAreaSize = 15;

	public CustomAction<bool> MapLoadedCustomAction = new CustomAction<bool>();

	public static bool AllowEdgePlacement { get; set; }

	public ChunkGenerator ChunkGenerator => chunkGenerator;

	public int ElevationLevel => elevationLevel;

	public int MaxElevation => maxElevation;

	public float LayerLevel => layerLevel;

	public int SizeX => worldSizeX;

	public int SizeY => worldSizeY;

	public int SizeZ => worldSizeZ;

	public Vec3Int Center => center;

	public int TerrainLayer => terrainLayer;

	public bool IsLoaded { get; private set; }

	public List<MapNode> GetAllowedAreaBorderNodes
	{
		get
		{
			if (borderNodes == null || borderNodes.Count == 0)
			{
				borderNodes = CalculateAllowedAreaBorderNodes();
			}
			return borderNodes;
		}
	}

	public event Action<float> LayerDownConstructablesEvent;

	public event Action<float> LayerUpConstructablesEvent;

	public event Action<float, int> LayerChangeEvent;

	public event Action<int, int, int, byte> OnDataModifiedAt;

	public event OnMapLoadedEvent MapLoadedEvent;

	public event Action UIInitCompleteEvent;

	public bool InsideMap(Vec3Int gridPosition)
	{
		if (gridPosition.x >= 0 && gridPosition.y >= 0 && gridPosition.z >= 0 && gridPosition.x < SizeX && gridPosition.y < SizeY)
		{
			return gridPosition.z < SizeZ;
		}
		return false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int GetDistanceFromMapEdge(int x, int z)
	{
		World world = MonoSingleton<World>.Instance;
		int num = ((x < world.SizeX / 2) ? x : (world.SizeX - x - 1));
		int num2 = ((z < world.SizeZ / 2) ? z : (world.SizeZ - z - 1));
		if (num < num2)
		{
			return num;
		}
		return num2;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void SetData(int x, int y, int z, byte block)
	{
		if (GridDataIndexTools.InRange(x, y, z) && x != 0 && y != 0 && z != 0)
		{
			this.OnDataModifiedAt?.Invoke(x, y, z, block);
		}
	}

	private void OnAllMapChunkGenerated()
	{
		StartCoroutine(LoadWorld());
		chunkGenerator.SetMeshesForAllChunks(elevationLevel);
	}

	private void OnAllEdgeFrameChunksGeneratedEvent()
	{
		allEdgeFrameChunksGenerated = true;
	}

	private IEnumerator LoadWorld()
	{
		DebugTimer.StartTimer("LoadingTimer");
		MonoSingleton<LoadingController>.Instance.InvokeLoadingPhaseChanged("load_sequence_start");
		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();
		yield return WorldLoadHandler.LoadingSequence();
		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();
		IsLoaded = true;
		bool flag = !GlobalSaveController.CurrentVillageData.FirstEnter;
		this.MapLoadedEvent?.Invoke(flag);
		MapLoadedCustomAction?.Invoke(flag);
		MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.Data.GameLoaded(flag);
		MapNode.RefreshEnabled = true;
		yield return villageMap.IdlePointManager.GameLoadedCoroutine(flag);
		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();
		MonoSingleton<LoadingController>.Instance.InvokeLoadingPhaseChanged("loading_ui");
		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();
		this.UIInitCompleteEvent?.Invoke();
		MonoSingleton<UIController>.Instance.OnLoadingFinished();
		MonoSingleton<LoadingController>.Instance.InvokeLoadingPhaseChanged("load_sequence_complete");
		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();
		if (GlobalSaveController.CurrentVillageData.FirstEnter)
		{
			MonoSingleton<LoadingController>.Instance.InvokeLoadingPhaseChanged("load_saving");
			yield return new WaitForEndOfFrame();
			MonoSingleton<GlobalSaveController>.Instance.AutosaveCurrentVillage();
		}
		else
		{
			GlobalSaveController.CurrentVillageData.ZipClose();
			GlobalSaveController.CurrentVillageData.CacheClose();
			float currentLayer = GlobalSaveController.CurrentVillageData.SelectedLayer;
			if (currentLayer > 0f)
			{
				MonoSingleton<TaskController>.Instance.WaitForNextFrameUnscaled().Then(delegate
				{
					if (GlobalSaveController.CurrentVillageData.IsSecondMap)
					{
						SwitchToUpperLayer(SizeY);
					}
					else
					{
						SwitchToLowerLayer(currentLayer);
					}
				});
			}
			MonoSingleton<Heightmap>.Instance.RefreshWholeHeightmap();
			RecalculateLargestArea();
			MonoSingleton<StartPositionManager>.Instance.CalculateDistanceMap();
		}
		while (!allEdgeFrameChunksGenerated)
		{
			yield return new WaitForEndOfFrame();
		}
		ConstructionJobManager constructionManager = VillageManager.ActiveVillage.Map.BuildingsManagerMain.ConstructionJobManager;
		while (constructionManager.CreateVoxelManager.Version == 0 || constructionManager.DestroyVoxelManager.Version == 0 || constructionManager.DeliveryJobManager.Version == 0)
		{
			yield return new WaitForEndOfFrame();
		}
		MonoSingleton<LoadingController>.Instance.InvokeLoadingPhaseChanged("loading_complete");
		MonoSingleton<GlobalShaderVariables>.Instance.HideForbiddenZone();
		DebugTimer.EndTimer("LoadingTimer");
	}

	public void RecalculateLargestArea()
	{
		largestAreas = FindAreasOnMap(minPlacementAreaSize);
	}

	public List<FloodfillArea> FindAreasOnMap(int minAreaSize)
	{
		List<FloodfillArea> list = new List<FloodfillArea>();
		bool[,] processed = new bool[worldSizeX, worldSizeZ];
		Heightmap heightmap = MonoSingleton<Heightmap>.Instance;
		for (int i = 0; i < worldSizeX; i++)
		{
			for (int j = 0; j < worldSizeZ; j++)
			{
				if (!processed[i, j])
				{
					FloodfillArea areaFloodfill = GetAreaFloodfill(i, j, heightmap, ref processed);
					if (areaFloodfill.NodesCount > minAreaSize && areaFloodfill.WaterVoxelsCount <= 0)
					{
						areaFloodfill.Points.TrimExcess();
						list.Add(areaFloodfill);
					}
					else
					{
						areaFloodfill.Points.Clear();
						areaFloodfill.Points.TrimExcess();
					}
				}
			}
		}
		list.Sort((FloodfillArea x, FloodfillArea y) => y.NodesCountNonForbidden.CompareTo(x.NodesCountNonForbidden));
		return list;
	}

	public void OnDrawGizmos()
	{
		if (!MonoSingleton<VillageManager>.IsApplicationQuitting() && villageMap != null)
		{
			_ = MonoSingleton<PlayerVoxelInfo>.Instance.HoverGridPosition;
			villageMap.WaterManager?.DrawGizmos();
			villageMap.FireSimLogic?.DrawGizmos();
			villageMap.FireAudioAndLights?.DrawGizmos();
			villageMap.HomeArea?.DrawGizmos();
			villageMap.IdlePointManager?.DrawGizmos();
		}
	}

	public IEnumerator GenerateMap()
	{
		MonoSingleton<LoadingController>.Instance.InvokeLoadingPhaseChanged("loading_terrain");
		yield return new WaitForSeconds(0.2f);
		DebugTimer.StartTimer("GenerateMap");
		MapNode.RefreshFlammabilityEnabled = false;
		maxElevation = SizeY;
		elevationLevel = SizeY;
		layerLevel = SizeY;
		MapGenerationTextures mapGenerationTextures = MonoSingleton<GroundManager>.Instance.MapGenerationTextures;
		if (GlobalSaveController.CurrentVillageData.FirstEnter)
		{
			MapSize byID = Repository<MapSizeRepository, MapSize>.Instance.GetByID(GlobalSaveController.CurrentVillageData.MapSizeID);
			mapGenerationTextures.Init(VillageManager.ActiveVillage.Map, byID.Vec3Int);
			yield return GlobalSaveController.CurrentVillageData.PlayerVillage.InitializeNew(MonoSingleton<MapGenerationController>.Instance.MapGenerator);
			mapGenerationTextures.SetMasksOnTerrain();
			mapGenerationTextures.FillMapMaskWithGroundData();
			mapGenerationTextures.FillEffectMap(VillageManager.ActiveVillage.Map);
			mapGenerationTextures.MapMaskTexture.Apply();
			GlobalSaveController.CurrentVillageData.MapMaskTexture = mapGenerationTextures.MapMaskTexture;
			GlobalSaveController.CurrentVillageData.EffectMaskTexture = mapGenerationTextures.EffectMaskTexture;
			GlobalSaveController.CurrentVillageData.SelectedLayer = SizeY;
		}
		else
		{
			mapGenerationTextures.Init(villageMap, villageMap.Size);
			GlobalSaveController.CurrentVillageData.PlayerVillage.InitAfterLoad();
			GlobalSaveController.CurrentVillageData.WorldMapData.InitAfterLoad();
		}
		DebugTimer.EndTimer("GenerateMap");
		chunkGenerator.GenerateMapChunks(VillageManager.ActiveVillage.Map, mapGenerationTextures);
		if (GlobalSaveController.CurrentVillageData.FirstEnter)
		{
			MonoSingleton<MapGenerationController>.Instance.MapGenerator.ApplyUndergroundWaterStability();
		}
		MonoSingleton<MapGenerationController>.Instance.DisposeMapGenerator();
	}

	public void JumpToLayer(Vec3Int gridPos)
	{
		if (layerLevel > (float)gridPos.y)
		{
			SwitchToLowerLayer(gridPos);
		}
		else if (layerLevel < (float)gridPos.y)
		{
			SwitchToUpperLayer(gridPos);
		}
	}

	public void SwitchToLowerLayer(Vec3Int gridPos)
	{
		float num = gridPos.y;
		if (!(num >= layerLevel))
		{
			while (num < layerLevel)
			{
				OnLayerDown();
			}
		}
	}

	public void SwitchToLowerLayer(float targetLayer)
	{
		if (!(targetLayer >= layerLevel))
		{
			while (targetLayer < layerLevel)
			{
				OnLayerDown();
			}
		}
	}

	public void SwitchToUpperLayer(Vec3Int gridPos)
	{
		float num = gridPos.y;
		if (!(num <= layerLevel))
		{
			while (num > layerLevel)
			{
				OnLayerUp();
			}
		}
	}

	public void SwitchToUpperLayer(float targetLayer)
	{
		if (!(targetLayer <= layerLevel))
		{
			while (targetLayer > layerLevel)
			{
				OnLayerUp();
			}
		}
	}

	public void OnLayerDown()
	{
		if (!(layerLevel <= 1f))
		{
			layerLevel -= 0.5f;
			GlobalSaveController.CurrentVillageData.SelectedLayer = layerLevel;
			MonoSingleton<AudioManager>.Instance.PlaySound("UI_Layer", new Dictionary<string, float> { 
			{
				"SliderValue",
				layerLevel / 16f
			} });
			int num = (int)(layerLevel + 0.6f);
			VillageManager.ActiveVillage.Map.RoomDetection.RefreshRenderCulling(num);
			this.LayerDownConstructablesEvent?.Invoke(layerLevel);
			this.LayerChangeEvent?.Invoke(layerLevel, SizeY);
			Shader.SetGlobalFloat("_WorldLayer", layerLevel);
			chunkGenerator.SetupBottomBlackMesh(layerLevel);
			if (num != elevationLevel)
			{
				elevationLevel = num;
				chunkGenerator.SetMeshesForAllChunks(num);
				chunkGenerator.SetupTopTransparentMesh(elevationLevel);
			}
		}
	}

	public void OnLayerUp()
	{
		if (layerLevel >= (float)maxElevation)
		{
			return;
		}
		layerLevel += 0.5f;
		GlobalSaveController.CurrentVillageData.SelectedLayer = layerLevel;
		MonoSingleton<AudioManager>.Instance.PlaySound("UI_Layer", new Dictionary<string, float> { 
		{
			"SliderValue",
			layerLevel / 16f
		} });
		int num = (int)(layerLevel + 0.6f);
		VillageManager.ActiveVillage.Map.RoomDetection.RefreshRenderCulling(num);
		this.LayerUpConstructablesEvent?.Invoke(layerLevel);
		this.LayerChangeEvent?.Invoke(layerLevel, SizeY);
		Shader.SetGlobalFloat("_WorldLayer", layerLevel);
		if (elevationLevel < SizeY)
		{
			chunkGenerator.SetupBottomBlackMesh(layerLevel);
			if (num != elevationLevel)
			{
				elevationLevel = num;
				chunkGenerator.SetMeshesForAllChunks(num);
				chunkGenerator.SetupTopTransparentMesh(elevationLevel);
			}
		}
	}

	internal FloodfillArea GetLargestArea()
	{
		if (largestAreas.Count <= 0)
		{
			return null;
		}
		return largestAreas[0];
	}

	internal void LimitPositionToRange(ref Vector2 position)
	{
		position.x = Mathf.Min(Mathf.Max(position.x, 0f), SizeX);
		position.y = Mathf.Min(Mathf.Max(position.y, 0f), SizeZ);
	}

	internal void LimitPositionToRange(ref Vector3 position)
	{
		position.x = Mathf.Min(Mathf.Max(position.x, 0f), SizeX);
		position.y = Mathf.Min(Mathf.Max(position.y, 0f), SizeY * MapBlockHeight);
		position.z = Mathf.Min(Mathf.Max(position.z, 0f), SizeZ);
	}

	internal void LimitPositionToRange(ref Vec3Int position)
	{
		position.x = Math.Min(Math.Max(position.x, 0), SizeX - 1);
		position.y = Math.Min(Math.Max(position.y, 0), (SizeY - 1) * MapBlockHeight);
		position.z = Math.Min(Math.Max(position.z, 0), SizeZ - 1);
	}

	private void SetupCameraOnNewGame()
	{
		int num = 3;
		Vector3 vector = default(Vector3);
		vector.x = num;
		vector.y = 0f;
		vector.z = num;
		Vector3 min = vector;
		vector = default(Vector3);
		vector.x = SizeX - num;
		vector.y = 255f;
		vector.z = SizeZ - num;
		Vector3 max = vector;
		MonoSingleton<RtsCamera>.Instance.SetCameraBounds(min, max);
		Vector2 startPositionCenter = MonoSingleton<StartPositionManager>.Instance.GetStartPositionCenter();
		Vector3 initialPosition = new Vector3(startPositionCenter.x, MonoSingleton<Heightmap>.Instance.GetHeightAt((int)startPositionCenter.x, (int)startPositionCenter.y) * MapBlockHeight, startPositionCenter.y);
		MonoSingleton<RtsCamera>.Instance.InitialPosition = initialPosition;
	}

	private FloodfillArea GetAreaFloodfill(int i, int j, Heightmap heightmap, ref bool[,] processed)
	{
		int heightAt = heightmap.GetHeightAt(i, j);
		FloodfillArea floodfillArea = new FloodfillArea(new Vector2Int(i, j), heightAt);
		Vector2Int item = new Vector2Int(i, j);
		Stack<Vector2Int> stack = new Stack<Vector2Int>();
		stack.Push(item);
		while (stack.Count > 0)
		{
			Vector2Int vector2Int = stack.Pop();
			if (GridDataIndexTools.InRange(vector2Int.x, 1, vector2Int.y) && !processed[vector2Int.x, vector2Int.y])
			{
				int heightAt2 = heightmap.GetHeightAt(vector2Int.x, vector2Int.y);
				if (heightAt2 == heightAt)
				{
					processed[vector2Int.x, vector2Int.y] = true;
					floodfillArea.AddPoint(new Vec3Int(vector2Int.x, heightAt2, vector2Int.y), villageMap);
					stack.Push(new Vector2Int(vector2Int.x - 1, vector2Int.y));
					stack.Push(new Vector2Int(vector2Int.x + 1, vector2Int.y));
					stack.Push(new Vector2Int(vector2Int.x, vector2Int.y - 1));
					stack.Push(new Vector2Int(vector2Int.x, vector2Int.y + 1));
				}
			}
		}
		return floodfillArea;
	}

	protected override void Awake()
	{
		base.Awake();
		terrainLayer = LayerMask.NameToLayer("VoxelMap");
		PathfinderUtil.EnableCaching = false;
	}

	private void Start()
	{
		villageMap = VillageManager.ActiveVillage.Map;
		string mapSizeID = GlobalSaveController.CurrentVillageData.MapSizeID;
		MapSize byID = Repository<MapSizeRepository, MapSize>.Instance.GetByID(mapSizeID);
		worldSizeX = GlobalSaveController.CurrentVillageData.MapSize.x;
		worldSizeY = GlobalSaveController.CurrentVillageData.MapSize.y;
		worldSizeZ = GlobalSaveController.CurrentVillageData.MapSize.z;
		if (byID != null && (worldSizeX == 0 || worldSizeY == 0 || worldSizeZ == 0))
		{
			worldSizeX = byID.Width;
			worldSizeY = byID.Height;
			worldSizeZ = byID.Length;
		}
		Vec3Int a = new Vec3Int(worldSizeX, worldSizeY, worldSizeZ);
		center = a / 2;
		MonoSingleton<SceneController>.Instance.Tick += OnTick;
		chunkGenerator.AllMapChunksGeneratedEvent += OnAllMapChunkGenerated;
		chunkGenerator.AllEdgeFrameChunksGeneratedEvent += OnAllEdgeFrameChunksGeneratedEvent;
		MonoSingleton<StartPositionManager>.Instance.StartPositionsSetEvent += SetupCameraOnNewGame;
		StartCoroutine(GenerateMap());
	}

	protected override void OnDestroy()
	{
		MapLoadedCustomAction?.Clear();
		MapLoadedCustomAction = null;
		this.MapLoadedEvent = null;
		if (MonoSingleton<SceneController>.IsInstantiated())
		{
			MonoSingleton<SceneController>.Instance.Tick -= OnTick;
		}
		if (MonoSingleton<StartPositionManager>.IsInstantiated())
		{
			MonoSingleton<StartPositionManager>.Instance.StartPositionsSetEvent -= SetupCameraOnNewGame;
		}
		if (chunkGenerator != null)
		{
			chunkGenerator.AllMapChunksGeneratedEvent -= OnAllMapChunkGenerated;
			chunkGenerator.AllEdgeFrameChunksGeneratedEvent -= OnAllEdgeFrameChunksGeneratedEvent;
		}
		villageMap = null;
		chunkGenerator = null;
		base.OnDestroy();
		this.UIInitCompleteEvent = null;
		this.LayerChangeEvent = null;
		this.LayerDownConstructablesEvent = null;
		this.OnDataModifiedAt = null;
	}

	private void OnTick(float deltaTime)
	{
		using (ProfilerSampleJanitor.Begin("World.Tick"))
		{
			if (MonoSingleton<KeybindingManager>.Instance.IsKeybindingKeyDown(KeyInputEvent.LeftControl))
			{
				if (Input.mouseScrollDelta.y > 0f)
				{
					OnLayerDown();
				}
				else if (Input.mouseScrollDelta.y < 0f)
				{
					OnLayerUp();
				}
			}
		}
	}

	private List<MapNode> CalculateAllowedAreaBorderNodes()
	{
		List<MapNode> list = new List<MapNode>();
		int num = 16;
		int sizeX = MonoSingleton<World>.Instance.SizeX;
		int sizeZ = MonoSingleton<World>.Instance.SizeZ;
		MapNode[] gridSpaceData = GlobalSaveController.CurrentVillageData.PlayerVillage.Map.GridSpaceData;
		foreach (MapNode mapNode in gridSpaceData)
		{
			if (mapNode == null || !mapNode.IsWalkable)
			{
				continue;
			}
			int x = mapNode.Position.x;
			int z = mapNode.Position.z;
			if (!GridDataIndexTools.IsForbiddenEdge(x, z))
			{
				if ((x >= num && x <= num) || (z >= num && z <= num))
				{
					list.Add(mapNode);
				}
				else if ((x < sizeX - num && x >= sizeX - num - 1) || (z < sizeZ - num && z >= sizeZ - num - 1))
				{
					list.Add(mapNode);
				}
			}
		}
		return list;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
