using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.Construction;
using NSMedieval.Enums;
using NSMedieval.Extensions;
using NSMedieval.Managers.Selection;
using NSMedieval.Map;
using NSMedieval.MeshTools;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.State;
using NSMedieval.StorageUniversal;
using NSMedieval.Terrain;
using NSMedieval.Tools;
using NSMedieval.Tutorial;
using NSMedieval.Types;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using UnityEngine;

namespace NSMedieval.Stockpiles;

public class StockpileManager : MonoSingleton<StockpileManager>, IObserver
{
	private const string StockpileID = "Stockpile";

	private const string ColorsPath = "Stockpile/StockpileColor.json";

	[NonSerialized]
	private MeshAreaMaker meshAreaMaker;

	[NonSerialized]
	private Dictionary<StockpileInstance, StockpileView> instanceToView;

	[NonSerialized]
	private StockpileColors stockpileColorsData;

	[NonSerialized]
	private StockpileInstance stockpileToModify;

	[NonSerialized]
	private VillageMap villageMap;

	public IEnumerable<StockpileInstance> Stockpiles => instanceToView.Keys;

	public void SpawnProfileStockpiles()
	{
		Log.Info("SpawnProfileStockpiles", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\SelectableAreas\\Stockpile\\StockpileManager.cs");
		if (villageMap == null)
		{
			villageMap = VillageManager.ActiveVillage.Map;
		}
		foreach (StockpileInstance worldObjects in VillageManager.ActiveVillage.Map.GetWorldObjectsList<StockpileInstance>(GridDataType.Stockpile))
		{
			if (!Stockpiles.Contains(worldObjects))
			{
				Load(worldObjects, VillageManager.ActiveVillage.Map);
			}
		}
	}

	public StockpileView GetView(StockpileInstance instance)
	{
		instanceToView.TryGetValue(instance, out var value);
		return value;
	}

	public void Destroy(StockpileInstance instance)
	{
		if (MonoSingleton<StockpileController>.IsInstantiated())
		{
			MonoSingleton<StockpileController>.Instance.StockpileDestroyed(instance);
		}
		if (instanceToView.ContainsKey(instance))
		{
			UnityEngine.Object.Destroy(instanceToView[instance].gameObject);
		}
		RemoveFromSave(instance);
	}

	public void SpawnStockpile(Stockpile blueprint, Vec3Int start, Vec3Int end)
	{
		if (stockpileToModify != null)
		{
			StockpileInstance adjacentStockpileForExpansion = GetAdjacentStockpileForExpansion(start, end);
			if (stockpileToModify == adjacentStockpileForExpansion && stockpileToModify.Blueprint.Equals(blueprint))
			{
				ExpandStockpile(stockpileToModify, start, end, GetExpandValidPositions(start, end));
				return;
			}
		}
		Vec3Int[,] meshArea = meshAreaMaker.GetMeshArea(start, end, CanPlaceStockpile);
		if (!meshArea.AllEquals(Vec3Int.zero))
		{
			Vec3Int vec3Int = Vec3Int.Min(in start, in end);
			Vec3Int end2 = Vec3Int.Max(in start, in end);
			GameObject obj = UnityEngine.Object.Instantiate(MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByID("Stockpile").Value, (Vector3)vec3Int + new Vector3(-0.5f, 0.03f, -0.5f), Quaternion.identity);
			StockpileView component = obj.GetComponent<StockpileView>();
			Vector3 position = obj.transform.position;
			position.y += 0.01f;
			StockpileInstance stockpileInstance = new StockpileInstance(blueprint, villageMap, instanceToView.Count, position.ToGridXZ(), meshArea, start, end);
			Mesh mesh2D = Singleton<GreedyMeshGenerator>.Instance.GetMesh2D(vec3Int, end2, meshArea, Vec3Int.zero);
			component.Setup(mesh2D);
			component.Setup(stockpileInstance, stockpileColorsData.GetNextColor());
			Save(stockpileInstance, component);
			MonoSingleton<StockpileController>.Instance.StockpilePlaced(stockpileInstance);
			stockpileToModify = stockpileInstance;
			MonoSingleton<StorageCommonManager>.Instance.PasteStorageSettingsTo(stockpileInstance);
		}
	}

	public bool StockpileExists(Vec3Int gridPosition)
	{
		GridDataType gridDataType = VillageManager.ActiveVillage.Map.GetNode(in gridPosition)?.DataType ?? GridDataType.None;
		return gridDataType.HasFlag(GridDataType.Stockpile);
	}

	public bool CanPlaceStockpile(Vec3Int v, bool ignoreExistingStockpileCheck = false)
	{
		if (!HasSomethingToStandOn(v))
		{
			return false;
		}
		MapNode node = VillageManager.ActiveVillage.Map.GetNode(in v);
		if (node == null || !node.IsVoxelAir())
		{
			return false;
		}
		if (!ignoreExistingStockpileCheck && node.DataType.HasFlag(GridDataType.Stockpile))
		{
			return false;
		}
		bool flag = (node.DataType & ~GridDataType.Stockpile) == 0;
		bool flag2 = (node.DataType & ~GridDataType.Stockpile & ~GridDataType.ResourcePile) == 0;
		if (!node.HasWorldObjects() || ((flag || flag2) && ignoreExistingStockpileCheck))
		{
			MapNode nodeBelow = node.GetNodeBelow();
			if (!nodeBelow.HasWorldObjects() && nodeBelow.IsVoxelAir())
			{
				return false;
			}
			if (villageMap.BuildingsManagerMain.BuildingExists(nodeBelow.Position, BuildingType.Ladder))
			{
				return false;
			}
			BuildingType buildingType = ~(BuildingType.Default | BuildingType.Wall | BuildingType.Window | BuildingType.Door | BuildingType.BarnDoor);
			foreach (WorldObject worldObject in nodeBelow.WorldObjects)
			{
				if (worldObject is DigMarkerResourceInstance)
				{
					continue;
				}
				if (!(worldObject is BaseBuildingInstance baseBuildingInstance))
				{
					return false;
				}
				if ((baseBuildingInstance.BuildingType & buildingType) != 0)
				{
					if (baseBuildingInstance.BuildingType == BuildingType.Beam)
					{
						return false;
					}
					if (!baseBuildingInstance.Blueprint.Socketable)
					{
						return false;
					}
				}
				else if (baseBuildingInstance.ConstructionPhase != ConstructionPhase.Finished)
				{
					return false;
				}
			}
			return true;
		}
		BuildingType allExceptFloor = ~(BuildingType.Default | BuildingType.Floor | BuildingType.Rug);
		if (node.WorldObjects.Any((WorldObject x) => x is BaseBuildingInstance baseBuildingInstance3 && (baseBuildingInstance3.BuildingType & allExceptFloor) != 0 && !baseBuildingInstance3.Blueprint.Socketable))
		{
			return false;
		}
		StairsComponentManager stairsComponentManager = node.Map.StairsComponentManager;
		Vec3Int b = Vec3Int.down;
		if (stairsComponentManager.GetComponentInstance(v + b) != null)
		{
			return false;
		}
		foreach (WorldObject worldObject2 in node.WorldObjects)
		{
			if (!(worldObject2 is BaseBuildingInstance baseBuildingInstance2))
			{
				WorldObjectType type = worldObject2.Type;
				return type == WorldObjectType.ResourcePile || type == WorldObjectType.MapResource || (ignoreExistingStockpileCheck && type == WorldObjectType.Stockpile);
			}
			if (baseBuildingInstance2.BuildingType == BuildingType.Beam)
			{
				continue;
			}
			if (baseBuildingInstance2.ConstructionPhase == ConstructionPhase.Finished && baseBuildingInstance2.BuildingType == BuildingType.Floor)
			{
				return true;
			}
			if (baseBuildingInstance2.ConstructionPhase != ConstructionPhase.Finished)
			{
				GroundManager groundManager = MonoSingleton<GroundManager>.Instance;
				b = node.Position;
				Vec3Int b2 = Vec3Int.down;
				int result;
				if (!groundManager.GroundExists(b + b2))
				{
					BuildingsManagerMain buildingsManagerMain = villageMap.BuildingsManagerMain;
					Vec3Int a = node.Position;
					Vec3Int b3 = Vec3Int.down;
					result = (buildingsManagerMain.FinishedBuildingVerticalStabilityCarrierExits(a + b3) ? 1 : 0);
				}
				else
				{
					result = 1;
				}
				return (byte)result != 0;
			}
		}
		return true;
		bool HasSomethingToStandOn(Vec3Int gridPos)
		{
			bool num = villageMap.BuildingsManagerMain.GetBuilding(gridPos, (BaseBuildingInstance x) => x.BuildingType == BuildingType.Floor && x.ConstructionPhase == ConstructionPhase.Finished) != null;
			GroundManager groundManager2 = MonoSingleton<GroundManager>.Instance;
			Vec3Int b4 = Vec3Int.down;
			bool flag3 = groundManager2.GroundExists(gridPos + b4);
			BuildingsManagerMain buildingsManagerMain2 = villageMap.BuildingsManagerMain;
			b4 = Vec3Int.down;
			bool flag4 = buildingsManagerMain2.FinishedBuildingVerticalStabilityCarrierExits(gridPos + b4);
			return num || flag3 || flag4;
		}
	}

	public void SetStockpileToModify(StockpileInstance stockpileToExpand)
	{
		stockpileToModify = stockpileToExpand;
	}

	public void ModifyStockpile(Vec3Int start, Vec3Int end, OrderType orderType)
	{
		if (stockpileToModify == null)
		{
			return;
		}
		StockpileInstance adjacentStockpileForExpansion = GetAdjacentStockpileForExpansion(start, end);
		if (stockpileToModify == adjacentStockpileForExpansion)
		{
			if (orderType.Equals(OrderType.ShrinkZone))
			{
				ShrinkStockpile(stockpileToModify, stockpileToModify.Start, stockpileToModify.End, GetShrinkValidPositions(start, end));
			}
			else
			{
				ExpandStockpile(stockpileToModify, start, end, GetExpandValidPositions(start, end));
			}
		}
	}

	private void ExpandStockpile(StockpileInstance stockpileInstance, Vec3Int newAreaStart, Vec3Int newAreaEnd, List<Vec3Int> validPositions)
	{
		Vec3Int vec3Int = validPositions.MinX();
		Vec3Int vec3Int2 = validPositions.MaxX();
		Vec3Int vec3Int3 = validPositions.MinZ();
		Vec3Int vec3Int4 = validPositions.MaxZ();
		int y = vec3Int.y * World.MapBlockHeight;
		Vec3Int lhs = new Vec3Int(vec3Int.x, y, vec3Int3.z);
		Vec3Int rhs = new Vec3Int(vec3Int2.x, y, vec3Int4.z);
		Vec3Int vec3Int5 = Vec3Int.Min(in lhs, in rhs);
		Vec3Int vec3Int6 = Vec3Int.Max(in lhs, in rhs);
		Vec3Int[,] modifiedMeshArea = meshAreaMaker.GetModifiedMeshArea(vec3Int5, vec3Int6, validPositions, CanPlaceStockpile);
		if (modifiedMeshArea.AllEquals(Vec3Int.zero))
		{
			stockpileInstance.Dispose();
		}
		else if (stockpileInstance.IsExpandValid(modifiedMeshArea))
		{
			stockpileInstance.Start = vec3Int5;
			stockpileInstance.End = vec3Int6;
			stockpileInstance.RefreshGridSpaces(modifiedMeshArea);
			StockpileView stockpileView = instanceToView[stockpileInstance];
			Mesh mesh2D = Singleton<GreedyMeshGenerator>.Instance.GetMesh2D(stockpileInstance.Start, stockpileInstance.End, modifiedMeshArea, Vec3Int.zero);
			stockpileView.Setup(mesh2D);
			Transform obj = stockpileView.transform;
			Vec3Int lhs2 = stockpileInstance.Start;
			Vec3Int rhs2 = stockpileInstance.End;
			obj.position = (Vector3)Vec3Int.Min(in lhs2, in rhs2) + new Vector3(-0.5f, 0.03f, -0.5f);
			VillageManager.ActiveVillage.Map.OnWorldObjectSizeChanged(stockpileInstance);
			VillageManager.ActiveVillage.Map.AddToTheWorld(stockpileInstance);
			MonoSingleton<StockpileController>.Instance.StockpilePlaced(stockpileInstance);
		}
	}

	private List<Vec3Int> GetShrinkValidPositions(Vec3Int start, Vec3Int end)
	{
		int num = Mathf.Min(start.x, end.x);
		int num2 = Mathf.Max(start.x, end.x);
		int num3 = Mathf.Min(start.z, end.z);
		int num4 = Mathf.Max(start.z, end.z);
		int y = start.y / World.MapBlockHeight;
		List<Vec3Int> list = new List<Vec3Int>();
		for (int i = num; i <= num2; i++)
		{
			for (int j = num3; j <= num4; j++)
			{
				list.Add(new Vec3Int(i, y, j));
			}
		}
		foreach (Vec3Int item in list.IterateInReverseDynamic())
		{
			if (!stockpileToModify.ContainsGridPosition(item))
			{
				list.Remove(item);
			}
		}
		List<Vec3Int> list2 = new List<Vec3Int>();
		foreach (Vec3Int position in stockpileToModify.Positions)
		{
			if (!list.Contains(position))
			{
				list2.Add(new Vec3Int(position.x, position.y, position.z));
			}
		}
		return list2;
	}

	private List<Vec3Int> GetExpandValidPositions(Vec3Int newAreaStart, Vec3Int newAreaEnd)
	{
		List<Vec3Int> list = new List<Vec3Int>();
		int num = Mathf.Min(newAreaStart.x, newAreaEnd.x);
		int num2 = Mathf.Max(newAreaStart.x, newAreaEnd.x);
		int num3 = Mathf.Min(newAreaStart.z, newAreaEnd.z);
		int num4 = Mathf.Max(newAreaStart.z, newAreaEnd.z);
		for (int i = num; i <= num2; i++)
		{
			for (int j = num3; j <= num4; j++)
			{
				list.Add(new Vec3Int(i, newAreaStart.y / World.MapBlockHeight, j));
			}
		}
		List<Vec3Int> list2 = new List<Vec3Int>();
		Vec3Int[] array = list.ToArray();
		for (int k = 0; k < array.Length; k++)
		{
			Vec3Int vec3Int = array[k];
			if (!stockpileToModify.ContainsGridPosition(vec3Int) && CanPlaceStockpile(new Vec3Int(vec3Int.x, vec3Int.y, vec3Int.z)))
			{
				list2.Add(vec3Int);
			}
		}
		foreach (Vec3Int position in stockpileToModify.Positions)
		{
			list2.Add(position);
		}
		return list2;
	}

	private void ShrinkStockpile(StockpileInstance stockpileInstance, Vec3Int start, Vec3Int end, List<Vec3Int> validPositions)
	{
		if (stockpileInstance != null && instanceToView.ContainsKey(stockpileInstance))
		{
			Vec3Int[,] modifiedMeshArea = meshAreaMaker.GetModifiedMeshArea(stockpileInstance.Start, stockpileInstance.End, validPositions, CanPlaceStockpile);
			OnRemovedDisconnectedArea(stockpileInstance, modifiedMeshArea);
			stockpileInstance.RefreshGridSpaces(modifiedMeshArea);
			if (modifiedMeshArea.AllEquals(Vec3Int.zero))
			{
				stockpileInstance.Dispose();
				return;
			}
			StockpileView stockpileView = instanceToView[stockpileInstance];
			Mesh mesh2D = Singleton<GreedyMeshGenerator>.Instance.GetMesh2D(stockpileInstance.Start, stockpileInstance.End, modifiedMeshArea, Vec3Int.zero);
			stockpileView.Setup(mesh2D);
		}
	}

	private StockpileInstance GetAdjacentStockpileForExpansion(Vec3Int start, Vec3Int end)
	{
		int y = start.y / World.MapBlockHeight;
		int num = Mathf.Min(start.x, end.x);
		int num2 = Mathf.Max(start.x, end.x);
		int num3 = Mathf.Min(start.z, end.z);
		int num4 = Mathf.Max(start.z, end.z);
		for (int i = num - 1; i <= num2 + 1; i++)
		{
			for (int j = num3; j <= num4; j++)
			{
				StockpileInstance byPosition = GetByPosition(new Vec3Int(i, y, j));
				if (stockpileToModify == byPosition)
				{
					return byPosition;
				}
			}
		}
		for (int k = num; k <= num2; k++)
		{
			for (int l = num3 - 1; l <= num4 + 1; l++)
			{
				StockpileInstance byPosition2 = GetByPosition(new Vec3Int(k, y, l));
				if (stockpileToModify == byPosition2)
				{
					return byPosition2;
				}
			}
		}
		return null;
	}

	private StockpileInstance GetByPosition(Vec3Int position)
	{
		return VillageManager.ActiveVillage.Map.GetNode(in position)?.GetWorldObject(GridDataType.Stockpile) as StockpileInstance;
	}

	private void Load(StockpileInstance stockpileInstance, VillageMap map)
	{
		if (Stockpiles.Contains(stockpileInstance) || stockpileInstance.Positions == null)
		{
			return;
		}
		if (stockpileInstance.Positions == null || stockpileInstance.Positions.Count == 0)
		{
			stockpileInstance.Dispose();
			return;
		}
		stockpileInstance.SetMap(map);
		List<Vec3Int> list = new List<Vec3Int>();
		foreach (Vec3Int position in stockpileInstance.Positions)
		{
			list.Add(position);
		}
		Vec3Int vec3Int = list.MinX();
		Vec3Int vec3Int2 = list.MaxX();
		Vec3Int vec3Int3 = list.MinZ();
		Vec3Int vec3Int4 = list.MaxZ();
		int y = vec3Int.y * World.MapBlockHeight;
		Vec3Int lhs = new Vec3Int(vec3Int.x, y, vec3Int3.z);
		Vec3Int rhs = new Vec3Int(vec3Int2.x, y, vec3Int4.z);
		stockpileInstance.Start = Vec3Int.Min(in lhs, in rhs);
		stockpileInstance.End = Vec3Int.Max(in lhs, in rhs);
		Vec3Int[,] modifiedMeshArea = meshAreaMaker.GetModifiedMeshArea(stockpileInstance.Start, stockpileInstance.End, list, CanPlaceStockpile);
		if (modifiedMeshArea.AllEquals(Vec3Int.zero))
		{
			stockpileInstance.Dispose();
			return;
		}
		KeyGameObjectPair byID = MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByID("Stockpile");
		if (byID?.Value == null)
		{
			Log.Error("Stockpile prefab 'Stockpile' could not be found in PrefabRepository. Skipping stockpile load.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\SelectableAreas\\Stockpile\\StockpileManager.cs");
			stockpileInstance.Dispose();
			return;
		}
		stockpileInstance.ReInstantiate();
		stockpileInstance.SetupAfterLoading();
		Vec3Int lhs2 = stockpileInstance.Start;
		Vec3Int rhs2 = stockpileInstance.End;
		Vec3Int vec3Int5 = Vec3Int.Min(in lhs2, in rhs2);
		StockpileView component = UnityEngine.Object.Instantiate(byID.Value, (Vector3)vec3Int5 + new Vector3(-0.5f, 0.03f, -0.5f), Quaternion.identity).GetComponent<StockpileView>();
		Mesh mesh2D = Singleton<GreedyMeshGenerator>.Instance.GetMesh2D(stockpileInstance.Start, stockpileInstance.End, modifiedMeshArea, Vec3Int.zero);
		component.Setup(mesh2D);
		component.Setup(stockpileInstance, stockpileColorsData.GetNextColor());
		instanceToView.Add(stockpileInstance, component);
		MonoSingleton<StorageCommonManager>.Instance.RegisterStorage(stockpileInstance);
	}

	private void OnRemovedDisconnectedArea(StockpileInstance stockpileInstance, Vec3Int[,] validPositions)
	{
		if (stockpileInstance == null)
		{
			return;
		}
		List<Vec3Int> validPositions2 = validPositions.ConvertToList();
		int y = stockpileInstance.Start.y / World.MapBlockHeight;
		Vec3Int lhs = stockpileInstance.Start;
		Vec3Int rhs = stockpileInstance.End;
		Vec3Int vec3Int = Vec3Int.Min(in lhs, in rhs);
		lhs = stockpileInstance.Start;
		rhs = stockpileInstance.End;
		Vec3Int vec3Int2 = Vec3Int.Max(in lhs, in rhs);
		for (int i = vec3Int.x; i <= vec3Int2.x; i++)
		{
			for (int j = vec3Int.z; j <= vec3Int2.z; j++)
			{
				CheckWhatToCarve(stockpileInstance, validPositions2, i, y, j);
			}
		}
		VillageManager.ActiveVillage.Map.OnWorldObjectSizeChanged(stockpileInstance);
	}

	private void CheckWhatToCarve(StockpileInstance stockpileInstance, List<Vec3Int> validPositions, int x, int y, int z)
	{
		Vec3Int vec3Int = new Vec3Int(x, y, z);
		if (!validPositions.Contains(vec3Int))
		{
			stockpileInstance.Carve(vec3Int);
		}
	}

	protected override void Awake()
	{
		base.Awake();
		stockpileColorsData = LoadStockpileColors();
		meshAreaMaker = new MeshAreaMaker();
	}

	private static StockpileColors LoadStockpileColors()
	{
		string path = Path.Combine(Application.streamingAssetsPath, "Stockpile/StockpileColor.json");
		bool isEnabled;
		try
		{
			if (!File.Exists(path))
			{
				FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(90, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\SelectableAreas\\Stockpile\\StockpileManager.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Stockpile color data missing at '");
					messageBuilder.AppendFormatted(FilePathUtils.RemoveUserFromPath(path));
					messageBuilder.AppendLiteral("'. The game installation is likely corrupt or incomplete.");
				}
				Log.Error(messageBuilder);
				return new StockpileColors();
			}
			return JsonUtility.FromJson<StockpileColors>(FileUtils.SafeReadAllText(path)) ?? new StockpileColors();
		}
		catch (Exception)
		{
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(90, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\SelectableAreas\\Stockpile\\StockpileManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Stockpile color data missing at '");
				messageBuilder.AppendFormatted(FilePathUtils.RemoveUserFromPath(path));
				messageBuilder.AppendLiteral("'. The game installation is likely corrupt or incomplete.");
			}
			Log.Error(messageBuilder);
			return new StockpileColors();
		}
	}

	private void Save(StockpileInstance stockpile, StockpileView view)
	{
		VillageManager.ActiveVillage.Map.AddAreaToTheWorld(stockpile);
		instanceToView.Add(stockpile, view);
		MonoSingleton<StorageCommonManager>.Instance.RegisterStorage(stockpile);
	}

	private void RemoveFromSave(StockpileInstance stockpile)
	{
		VillageManager.ActiveVillage.Map.RemoveAreaFromWorld(stockpile);
		instanceToView.Remove(stockpile);
	}

	private void Start()
	{
		instanceToView = new Dictionary<StockpileInstance, StockpileView>();
		MonoSingleton<ConstructionController>.Instance.ObjectDestroyedCheckFallDownEvent += OnObjectDestroyedCheckFallDown;
		MonoSingleton<GroundController>.Instance.OnGroundDestroyedEvent += OnGroundDestroyed;
		MonoSingleton<GroundController>.Instance.OnGroundDestroyedSingleEvent += OnGroundDestroyedSingle;
		MonoSingleton<SelectionManager>.Instance.ResetOrderEvent += OnSelectionToolReset;
		MonoSingleton<World>.Instance.MapLoadedEvent += OnMapLoaded;
	}

	protected override void OnDestroy()
	{
		if (MonoSingleton<ConstructionController>.IsInstantiated())
		{
			MonoSingleton<ConstructionController>.Instance.BlueprintPlacedCarveAreasEvent -= OnBlueprintPlacedCarveStockpiles;
			MonoSingleton<ConstructionController>.Instance.ObjectDestroyedCheckFallDownEvent -= OnObjectDestroyedCheckFallDown;
		}
		if (MonoSingleton<GroundController>.IsInstantiated())
		{
			MonoSingleton<GroundController>.Instance.OnGroundDestroyedEvent -= OnGroundDestroyed;
			MonoSingleton<GroundController>.Instance.OnGroundDestroyedSingleEvent -= OnGroundDestroyedSingle;
		}
		if (MonoSingleton<SelectionManager>.IsInstantiated())
		{
			MonoSingleton<SelectionManager>.Instance.ResetOrderEvent -= OnSelectionToolReset;
		}
		if (MonoSingleton<World>.IsInstantiated())
		{
			MonoSingleton<World>.Instance.MapLoadedEvent -= OnMapLoaded;
		}
		instanceToView.Clear();
		if (villageMap?.WaterManager != null)
		{
			villageMap.WaterManager.WaterLevelChangedEvent -= OnWaterLevelChanged;
		}
		villageMap = null;
		stockpileToModify = null;
		stockpileColorsData = null;
		meshAreaMaker = null;
		base.OnDestroy();
	}

	private void OnObjectDestroyedCheckFallDown(Vec3Int gridPosition)
	{
		using PooledList<StockpileInstance> pooledList = ListPool<StockpileInstance>.GetJanitor(instanceToView.Keys);
		foreach (StockpileInstance item in pooledList)
		{
			Vec3Int b;
			if (item.ContainsGridPosition(gridPosition))
			{
				b = Vec3Int.down;
				Vec3Int vec3Int = gridPosition + b;
				if (!villageMap.BuildingsManagerMain.StabilityBuildingExists(vec3Int, ConstructionPhase.Finished, ~(BuildingType.Floor | BuildingType.Roof)) && !MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int))
				{
					RefreshCarvedStockpile(item);
					continue;
				}
			}
			b = Vec3Int.up;
			Vec3Int vec3Int2 = gridPosition + b;
			if (item.ContainsGridPosition(vec3Int2) && !villageMap.BuildingsManagerMain.StabilityBuildingExists(vec3Int2, ConstructionPhase.Finished, BuildingType.AllBuildings))
			{
				RefreshCarvedStockpile(item);
			}
		}
	}

	private void RefreshCarvedStockpile(StockpileInstance stockpileInstance)
	{
		if (stockpileInstance == null || !instanceToView.ContainsKey(stockpileInstance))
		{
			return;
		}
		List<Vec3Int> list = new List<Vec3Int>();
		foreach (Vec3Int position in stockpileInstance.Positions)
		{
			list.Add(position);
		}
		Vec3Int[,] modifiedMeshArea = meshAreaMaker.GetModifiedMeshArea(stockpileInstance.Start, stockpileInstance.End, list, CanPlaceStockpile);
		if (modifiedMeshArea.AllEquals(Vec3Int.zero))
		{
			stockpileInstance.Dispose();
			return;
		}
		OnRemovedDisconnectedArea(stockpileInstance, modifiedMeshArea);
		StockpileView stockpileView = instanceToView[stockpileInstance];
		Mesh mesh2D = Singleton<GreedyMeshGenerator>.Instance.GetMesh2D(stockpileInstance.Start, stockpileInstance.End, modifiedMeshArea, Vec3Int.zero);
		stockpileView.Setup(mesh2D);
	}

	private void OnBlueprintPlacedCarveStockpiles(List<BaseBuildingInstance> buildings)
	{
		if (TutorialManager.IsTutorialActive)
		{
			return;
		}
		using PooledList<StockpileInstance> pooledList = ListPool<StockpileInstance>.GetJanitor();
		foreach (BaseBuildingInstance building in buildings)
		{
			foreach (Vec3Int position in building.Positions)
			{
				StockpileInstance byPosition = GetByPosition(position);
				if (!pooledList.Contains(byPosition))
				{
					pooledList.Add(byPosition);
				}
			}
		}
		foreach (StockpileInstance item in pooledList)
		{
			RefreshCarvedStockpile(item);
		}
	}

	private void OnCarveStockpile(List<Vec3Int> gridPositions)
	{
		foreach (Vec3Int gridPosition in gridPositions)
		{
			RefreshCarvedStockpile(GetByPosition(gridPosition));
		}
	}

	private void OnGroundDestroyedSingle(Vec3Int position)
	{
		Vec3Int b = Vec3Int.up;
		if (StockpileExists(position + b))
		{
			RefreshCarvedStockpile(GetByPosition(position));
		}
	}

	private void OnGroundDestroyed(List<Vec3Int> positions)
	{
		HashSet<StockpileInstance> hashSet = new HashSet<StockpileInstance>();
		foreach (Vec3Int position in positions)
		{
			Vec3Int a = position;
			Vec3Int b = Vec3Int.up;
			if (StockpileExists(a + b))
			{
				b = Vec3Int.up;
				hashSet.Add(GetByPosition(a + b));
			}
		}
		foreach (StockpileInstance item in hashSet)
		{
			RefreshCarvedStockpile(item);
		}
	}

	private void OnSelectionToolReset()
	{
		stockpileToModify = null;
	}

	private void OnMapLoaded(bool wasLoadedFromSave)
	{
		villageMap = VillageManager.ActiveVillage.Map;
		villageMap.WaterManager.WaterLevelChangedEvent += OnWaterLevelChanged;
		MonoSingleton<ConstructionController>.Instance.BlueprintPlacedCarveAreasEvent += OnBlueprintPlacedCarveStockpiles;
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
		if (nodeIndex < 0 || nodeIndex >= villageMap.GridSpaceData.Length)
		{
			return;
		}
		MapNode mapNode = villageMap.GridSpaceData[nodeIndex];
		if (mapNode == null)
		{
			return;
		}
		Vec3Int position = mapNode.Position;
		foreach (StockpileInstance key in instanceToView.Keys)
		{
			if (key.Grid.ContainsKey(position))
			{
				key.WaterLevelChanged();
			}
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
