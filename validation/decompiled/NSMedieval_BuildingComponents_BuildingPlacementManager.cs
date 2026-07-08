using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Effects;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NGS.MeshFusionPro;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Construction;
using NSMedieval.Construction.PreviewGridRulers;
using NSMedieval.Controllers;
using NSMedieval.DebugEvents;
using NSMedieval.Enums;
using NSMedieval.Extensions;
using NSMedieval.Manager;
using NSMedieval.Managers.Selection;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.MovableBuildings;
using NSMedieval.Repository;
using NSMedieval.State;
using NSMedieval.Terrain;
using NSMedieval.Tools;
using NSMedieval.Types;
using NSMedieval.UI;
using NSMedieval.UI.Utils;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using UnityEngine;
using UnityEngine.Rendering;

namespace NSMedieval.BuildingComponents;

public class BuildingPlacementManager : MonoSingleton<BuildingPlacementManager>
{
	private const string DefaultMeshVariationID = "default";

	private readonly int maximumGridSize = 20;

	private readonly RaycastHit[] raycastHits = new RaycastHit[64];

	private RaycastHit hit;

	private Ray ray;

	private int voxelMapLayer;

	private int buildableSurfaceLayer;

	private int raycastPlaneHelperLayer;

	private int raycastMask;

	private int defaultRaycastMask;

	private Vec3Int raycastGridStart;

	private Vec3Int raycastGridCurrent;

	private Vec3Int raycastGridPrevious;

	private Vec3Int difference;

	private bool cancelPlacement;

	private DragDirection dragDirection;

	private DragDirection previousDragDirection;

	private ObjectSide hitSide;

	private float diffX;

	private float diffZ;

	[NonSerialized]
	private BaseBuildingBlueprint baseBuildingBlueprint;

	[NonSerialized]
	private RoofComponentBlueprint roofComponentBlueprint;

	private Bounds bounds;

	[NonSerialized]
	private Camera mainCamera;

	[NonSerialized]
	private World world;

	private bool setupCompleted;

	private bool hasSelectedItem;

	private readonly Dictionary<Vec3Int, BaseBuildingViewComponent> buildingsDictionary = new Dictionary<Vec3Int, BaseBuildingViewComponent>();

	private readonly Dictionary<Vec3Int, RoofViewComponent> roofPositionView = new Dictionary<Vec3Int, RoofViewComponent>();

	private readonly Dictionary<string, Dictionary<string, ShadowCastingMode>> shadowCastingModes = new Dictionary<string, Dictionary<string, ShadowCastingMode>>();

	[SerializeField]
	private GameObject raycastHeplerPlane;

	private GameObject preview;

	[NonSerialized]
	private BaseBuildablePreview baseBuildablePreview;

	[NonSerialized]
	private UIController uicontroller;

	[NonSerialized]
	private WellPreviewView wellPreviewView;

	private bool draggingRoof;

	private bool twoAxisDrag;

	private bool singleAxisDrag;

	private bool canShowPlacementError = true;

	[SerializeField]
	private GameObject dragGrid;

	[SerializeField]
	private BuildingPreviewGridRuler dragRulers;

	[SerializeField]
	private SocketablePreviewGridRuler socketablePreviewGridRuler;

	[SerializeField]
	private SinglePlacementPreviewGridRuler singlePlacementPreviewGridRuler;

	[SerializeField]
	private GameObject forbiddenAreaIndicatorFront;

	[SerializeField]
	private GameObject forbiddenAreaIndicatorBack;

	[SerializeField]
	private GameObject forbiddenAreaIndicatorLeft;

	[SerializeField]
	private GameObject forbiddenAreaIndicatorRight;

	[NonSerialized]
	private List<GameObject> forbiddenAreaIndicators;

	private string localizedBuildingName;

	private List<TooltipResourcesInfo> tooltipResourcesInfos = new List<TooltipResourcesInfo>();

	private List<string> localizedInfoCursorData = new List<string>();

	private Vec3Int adjustedWorldPosition;

	private ObjectSide tempSide;

	private Vector3 gridResetPos;

	private bool showCantDigTopLayer;

	private bool mouseDown;

	private bool wasForbiddenEdge;

	private readonly int buildingReplacementLayer = 21;

	private int pooledObjectLayer;

	private int originalPreviewLayer;

	private bool moveOrInstallPlacedSuccess;

	private bool skipAutomaticRotation;

	private RelocateBuilding moveBuilding;

	private bool canShowBeamPlacementError = true;

	private bool userForceMerlonRotation;

	[NonSerialized]
	private MovableBuildingPileInstance pileToInstall;

	private int previewAngle;

	[NonSerialized]
	private VillageMap map;

	[NonSerialized]
	private BeamPreviewView beamPreview;

	private bool placeable;

	private bool objectsInTheWay;

	private bool startSocketOccupied;

	private bool endSocketOccupied;

	private bool beamIntersection;

	private bool beamTooLong;

	private bool beamBlockedBySlope;

	private bool beamBlockedByBuildingForbiddenArea;

	[NonSerialized]
	private BaseBuildingInstance beamBlockerBuilding;

	private Vec3Int startVoxelPos;

	private Vec3Int endVoxelPos;

	[NonSerialized]
	private BaseBuildingInstance startWall;

	[NonSerialized]
	private BaseBuildingInstance endWall;

	private int minLength;

	private int maxLength;

	[SerializeField]
	private bool autoconstruct;

	[NonSerialized]
	private List<BaseBuildingInstance> buildingsToAutoConstruct = new List<BaseBuildingInstance>();

	[SerializeField]
	private bool variantsUnlocked;

	[SerializeField]
	private bool spawnMaterialsWithBuilding;

	[SerializeField]
	private bool craftableBuildingsEnabled;

	private readonly ObjectSide validSockets = ObjectSide.Left | ObjectSide.Right | ObjectSide.Front | ObjectSide.Back;

	private bool socketPlaceable;

	[NonSerialized]
	private BaseBuildingInstance buildingInstance;

	private Vec3Int objectHolderPos;

	private Vec3Int adjacent;

	private ObjectSide socket;

	public Dictionary<string, Dictionary<string, ShadowCastingMode>> ShadowCastingModes => shadowCastingModes;

	public bool ConstructWithoutResource { get; set; }

	public bool HasSelectedItem => hasSelectedItem;

	public bool MouseDown => mouseDown;

	public bool CraftableBuildingsEnabled
	{
		get
		{
			return craftableBuildingsEnabled;
		}
		set
		{
			craftableBuildingsEnabled = value;
		}
	}

	public bool SpawnMaterialsWithBuilding
	{
		get
		{
			return spawnMaterialsWithBuilding;
		}
		set
		{
			spawnMaterialsWithBuilding = value;
		}
	}

	public bool VariantsUnlocked
	{
		get
		{
			return variantsUnlocked;
		}
		set
		{
			variantsUnlocked = value;
		}
	}

	public bool Autoconstruct
	{
		get
		{
			return autoconstruct;
		}
		set
		{
			autoconstruct = value;
		}
	}

	public bool UserForceMerlonRotation => userForceMerlonRotation;

	private VillageMap Map => map;

	public RelocateBuilding MoveBuilding => moveBuilding;

	public event Action SelectionCanceledEvent;

	public event Action EmptyClickEvent;

	public event Action CraftableBuildingsToggledEvent;

	public event Action StabilityBuildingRemovedEvent;

	public void CraftableBuildingsEnabledToggle()
	{
		craftableBuildingsEnabled = !craftableBuildingsEnabled;
		this.CraftableBuildingsToggledEvent?.Invoke();
	}

	public void SpawnMaterialsWithBuildingToggle()
	{
		spawnMaterialsWithBuilding = !spawnMaterialsWithBuilding;
	}

	public void VariantsUnlockedToggle()
	{
		variantsUnlocked = !variantsUnlocked;
	}

	public void AutoconstructToggle()
	{
		autoconstruct = !autoconstruct;
	}

	public void SetupCamera()
	{
		mainCamera = Camera.main;
	}

	private void Start()
	{
		minLength = Repository<StabilityRepository, Stability>.Instance.GetByID("basic_stability").MinBeamLength;
		maxLength = Repository<StabilityRepository, Stability>.Instance.GetByID("basic_stability").MaxBeamLength;
		forbiddenAreaIndicators = new List<GameObject> { forbiddenAreaIndicatorFront, forbiddenAreaIndicatorBack, forbiddenAreaIndicatorLeft, forbiddenAreaIndicatorRight };
		MonoSingleton<SelectionManager>.Instance.AssignOrderEvent += OnSelectionAssignOrder;
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		beamBlockerBuilding = null;
		startWall = null;
		endWall = null;
		this.SelectionCanceledEvent = null;
		this.EmptyClickEvent = null;
		this.CraftableBuildingsToggledEvent = null;
		if (MonoSingleton<GlobalSaveController>.IsInstantiated())
		{
			MonoSingleton<GlobalSaveController>.Instance.AutosaveStartEvent -= OnSaveStart;
			MonoSingleton<GlobalSaveController>.Instance.QuicksaveStartEvent -= OnSaveStart;
		}
		if (MonoSingleton<SceneController>.IsInstantiated())
		{
			MonoSingleton<SceneController>.Instance.Tick -= OnTick;
		}
		if (MonoSingleton<SelectionManager>.IsInstantiated())
		{
			MonoSingleton<SelectionManager>.Instance.AssignOrderEvent -= OnSelectionAssignOrder;
		}
		if (MonoSingleton<GlobalKeybindingManager>.IsInstantiated())
		{
			MonoSingleton<GlobalKeybindingManager>.Instance.UnsubscribeFromEscapeKey(OnRightMouseUp, IsPlacementActive);
		}
		if (preview != null)
		{
			UnityEngine.Object.Destroy(preview);
			preview = null;
		}
		baseBuildablePreview = null;
		uicontroller = null;
	}

	public void CachePileToInstall(MovableBuildingPileInstance pileToInstall)
	{
		this.pileToInstall = pileToInstall;
		this.pileToInstall.PlacementModeActive = true;
	}

	public void ClearPileToInstall()
	{
		if (pileToInstall != null)
		{
			pileToInstall.PlacementModeActive = false;
			pileToInstall = null;
		}
	}

	public void Setup(Vector3 scale, Vector3 position)
	{
		raycastHeplerPlane.GetComponent<BoxCollider>().size = scale;
		raycastHeplerPlane.transform.position = position;
		gridResetPos = position;
		mainCamera = Camera.main;
		world = MonoSingleton<World>.Instance;
		uicontroller = MonoSingleton<UIController>.Instance;
		voxelMapLayer = 1 << LayerMask.NameToLayer("VoxelMap");
		buildableSurfaceLayer = 1 << LayerMask.NameToLayer("BuildableSurface");
		raycastPlaneHelperLayer = 1 << LayerMask.NameToLayer("RaycastPlaneHelper");
		raycastMask = voxelMapLayer | buildableSurfaceLayer;
		defaultRaycastMask = raycastMask;
		MonoSingleton<SceneController>.Instance.Tick += OnTick;
		MonoSingleton<KeybindingManager>.Instance.SubscribeToEvent(KeyInputEvent.RotateLeft, OnRotateLeft);
		MonoSingleton<KeybindingManager>.Instance.SubscribeToEvent(KeyInputEvent.RotateRight, OnRotateRight);
		MonoSingleton<GlobalKeybindingManager>.Instance.SubscribeToEscapeKey(OnRightMouseUp, IsPlacementActive);
		MonoSingleton<GlobalSaveController>.Instance.AutosaveStartEvent += OnSaveStart;
		MonoSingleton<GlobalSaveController>.Instance.QuicksaveStartEvent += OnSaveStart;
		map = VillageManager.ActiveVillage.Map;
		setupCompleted = true;
	}

	public void EmptyClick()
	{
		this.EmptyClickEvent?.Invoke();
	}

	public void MouseEventDown(int button, Vector3 position)
	{
		if (button != 0)
		{
			_ = 1;
		}
		else
		{
			OnLeftMouseDown();
		}
	}

	public void OnRightMouseUp()
	{
		CancelSelection(resetCancelPlacement: true);
		if (hasSelectedItem)
		{
			uicontroller.ToggleInfoCursor(active: false);
		}
	}

	private void MouseUpSocketable()
	{
		if (baseBuildingBlueprint.BuildingType == BuildingType.Beam)
		{
			TrySpawnBeam();
		}
		else
		{
			TryPlaceSocketable(raycastGridCurrent, objectHolderPos, socket, previewAngle);
		}
	}

	private void MouseUpSpawnInitializeBuildings(int angleY)
	{
		if (buildingsDictionary.Count == 0)
		{
			BaseBuildingViewComponent baseBuildingViewComponent = SpawnFromPool(baseBuildingBlueprint, this.raycastGridStart, angleY);
			if (baseBuildingViewComponent == null)
			{
				bool isEnabled;
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(32, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingPlacementManager.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Cannot spawn object at position ");
					messageBuilder.AppendFormatted(this.raycastGridStart);
				}
				Log.Warning(messageBuilder);
				return;
			}
			buildingsDictionary.TryAdd(this.raycastGridStart, baseBuildingViewComponent);
		}
		bool flag = baseBuildingBlueprint.TransfersStability();
		if (buildingsDictionary.Count > 0)
		{
			foreach (Vec3Int key in buildingsDictionary.Keys)
			{
				if ((bool)buildingsDictionary[key])
				{
					int angleY2 = angleY;
					float y = buildingsDictionary[key].transform.eulerAngles.y;
					if (!Mathf.Approximately(y, angleY))
					{
						angleY2 = (int)y;
					}
					Map.BuildingsManagerMain.CreateBuildingInstanceAndBindToView(baseBuildingBlueprint, buildingsDictionary[key], GridUtils.GetWorldPosition(key), angleY2);
				}
			}
			if (flag)
			{
				Map.StabilityManager.PlaceBlueprints(buildingsDictionary.Values.ToList());
			}
		}
		if (flag)
		{
			Vec3Int[] array = buildingsDictionary.Keys.ToArray();
			bool flag2 = true;
			for (int i = 0; i < array.Length; i++)
			{
				if (Map.StabilityManager.GetBlueprintStability(array[i]) == 0)
				{
					Map.StabilityManager.ClearBlueprint(array[i], baseBuildingBlueprint);
					RemoveObject(array[i]);
					if (flag2)
					{
						MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("error_place_stability"));
						this.StabilityBuildingRemovedEvent?.Invoke();
						flag2 = false;
					}
				}
			}
		}
		using PooledList<BaseBuildingViewComponent> pooledList = ListPool<BaseBuildingViewComponent>.GetJanitor();
		pooledList.AddRange(buildingsDictionary.Values);
		if (pooledList.Count > 1)
		{
			Vec3Int raycastGridStart = this.raycastGridStart;
			Vec3Int a = GetGridPositionFromRaycast();
			Vector3 rhs = (Vector3)(a - raycastGridStart);
			rhs.Normalize();
			bool isZMain = Mathf.Abs(Vector3.Dot(Vector3.forward, rhs)) > Mathf.Cos(MathF.PI * 23f / 90f);
			pooledList.Sort(delegate(BaseBuildingViewComponent viewA, BaseBuildingViewComponent viewB)
			{
				float num = 0f;
				float num2 = 0f;
				Vec3Int a2 = viewA.BaseBuildingInstance.GridDataPosition;
				Vec3Int vec3Int = a2 - raycastGridStart;
				a2 = viewB.BaseBuildingInstance.GridDataPosition;
				Vec3Int vec3Int2 = a2 - raycastGridStart;
				if (isZMain)
				{
					num = Mathf.Abs(vec3Int.z) * 1000 + Mathf.Abs(vec3Int.x);
					num2 = Mathf.Abs(vec3Int2.z) * 1000 + Mathf.Abs(vec3Int2.x);
				}
				else
				{
					num = Mathf.Abs(vec3Int.x) * 1000 + Mathf.Abs(vec3Int.z);
					num2 = Mathf.Abs(vec3Int2.x) * 1000 + Mathf.Abs(vec3Int2.z);
				}
				return num.CompareTo(num2);
			});
		}
		if (baseBuildingBlueprint.BuildingType == BuildingType.Fence)
		{
			using PooledList<BaseBuildingInstance> pooledList2 = ListPool<BaseBuildingInstance>.GetJanitor();
			foreach (BaseBuildingViewComponent item in pooledList)
			{
				pooledList2.Add(item.BaseBuildingInstance);
				ObjectPlacedOnMap(item);
			}
			map.FenceAutomaticMeshVariationManager.Run(pooledList2.GetRawList());
		}
		else if (baseBuildingBlueprint.BuildingType == BuildingType.Merlon)
		{
			using PooledList<BaseBuildingInstance> pooledList3 = ListPool<BaseBuildingInstance>.GetJanitor();
			foreach (BaseBuildingViewComponent item2 in pooledList)
			{
				pooledList3.Add(item2.BaseBuildingInstance);
				ObjectPlacedOnMap(item2);
			}
			map.MerlonRotationManager.Run(pooledList3.GetRawList());
		}
		else
		{
			BuildingType buildingType = baseBuildingBlueprint.BuildingType;
			if (buildingType == BuildingType.FenceGate || buildingType == BuildingType.BarnDoor || buildingType == BuildingType.Door)
			{
				foreach (BaseBuildingViewComponent item3 in pooledList)
				{
					ObjectPlacedOnMap(item3);
					map.FloorAutomaticMeshVariationManager.RefreshNeighbors(item3.BaseBuildingInstance.GridDataPosition);
					map.FenceAutomaticMeshVariationManager.RefreshNeighbors(item3.BaseBuildingInstance.GridDataPosition);
				}
			}
			else if (baseBuildingBlueprint.BuildingType == BuildingType.Wall)
			{
				using PooledList<BaseBuildingInstance> pooledList4 = ListPool<BaseBuildingInstance>.GetJanitor();
				foreach (BaseBuildingViewComponent item4 in pooledList)
				{
					pooledList4.Add(item4.BaseBuildingInstance);
					ObjectPlacedOnMap(item4);
				}
				map.WallAutomaticMeshVariationManager.Run(pooledList4.GetRawList());
			}
			else if (baseBuildingBlueprint.BuildingType == BuildingType.Voxel)
			{
				using PooledList<BaseBuildingInstance> pooledList5 = ListPool<BaseBuildingInstance>.GetJanitor();
				foreach (BaseBuildingViewComponent item5 in pooledList)
				{
					pooledList5.Add(item5.BaseBuildingInstance);
					ObjectPlacedOnMap(item5);
					map.WallAutomaticMeshVariationManager.RefreshNeighbors(item5.BaseBuildingInstance.GridDataPosition);
				}
			}
			else if (baseBuildingBlueprint.BuildingType == BuildingType.Floor)
			{
				using PooledList<BaseBuildingInstance> pooledList6 = ListPool<BaseBuildingInstance>.GetJanitor();
				foreach (BaseBuildingViewComponent item6 in pooledList)
				{
					pooledList6.Add(item6.BaseBuildingInstance);
					ObjectPlacedOnMap(item6);
				}
				map.FloorAutomaticMeshVariationManager.Run(pooledList6.GetRawList());
			}
			else
			{
				foreach (BaseBuildingViewComponent item7 in pooledList)
				{
					ObjectPlacedOnMap(item7);
				}
			}
		}
		userForceMerlonRotation = false;
		if (!autoconstruct)
		{
			return;
		}
		foreach (BaseBuildingViewComponent item8 in pooledList)
		{
			buildingsToAutoConstruct.Add(item8.BaseBuildingInstance);
		}
		AutoConstructBuildInOrder(buildingsToAutoConstruct);
	}

	private void AutoConstructBuildInOrder(List<BaseBuildingInstance> buildingsToConstruct)
	{
		for (int num = buildingsToConstruct.Count - 1; num >= 0; num--)
		{
			if (buildingsToConstruct[num].HasStabilityToBuild)
			{
				buildingsToConstruct[num].AutoConstructSequence();
				buildingsToConstruct.RemoveAt(num);
			}
		}
		if (buildingsToConstruct.Count > 0)
		{
			MonoSingleton<TaskController>.Instance.WaitForUnscaled(0.1f).Then(delegate
			{
				AutoConstructBuildInOrder(buildingsToConstruct);
			});
		}
	}

	public void SetMoveOrInstallPlacedSuccess(bool moveOrInstallPlacedSuccess)
	{
		this.moveOrInstallPlacedSuccess = moveOrInstallPlacedSuccess;
	}

	public void OnLeftMouseUp()
	{
		if (!hasSelectedItem || baseBuildingBlueprint == null)
		{
			return;
		}
		if (baseBuildingBlueprint.PlacementType == PlacementType.SinglePlacement)
		{
			OnLeftMouseDown(skip: true);
		}
		mouseDown = false;
		if (preview != null)
		{
			preview.SetActive(value: true);
		}
		Vec3Int gridPositionFromRaycast = GetGridPositionFromRaycast();
		raycastMask = defaultRaycastMask;
		raycastHeplerPlane.SetActive(value: false);
		cancelPlacement = false;
		moveOrInstallPlacedSuccess = false;
		switch (baseBuildingBlueprint.PlacementType)
		{
		case PlacementType.SinglePlacement:
			MouseUpSpawnInitializeBuildings(previewAngle);
			break;
		case PlacementType.SingleAxisDrag:
			MouseUpSpawnInitializeBuildings(previewAngle);
			break;
		case PlacementType.TwoAxisDrag:
			MouseUpSpawnInitializeBuildings(previewAngle);
			break;
		case PlacementType.WallSocket:
			MouseUpSocketable();
			break;
		case PlacementType.Roof:
			MouseUpRoofs();
			break;
		default:
			Debug.LogError("Building " + baseBuildingBlueprint.GetID() + " has an invalid placement type.");
			ClearAllObjects();
			buildingsDictionary.Clear();
			return;
		}
		MonoSingleton<BlueprintPlaceVisualsManager>.Instance.SortBlueprints(gridPositionFromRaycast);
		UpdateDragGridRulers();
		buildingsDictionary.Clear();
		MonoSingleton<BuildingsPool>.Instance.Refill(baseBuildingBlueprint);
		dragDirection = DragDirection.None;
		twoAxisDrag = false;
		singleAxisDrag = false;
		draggingRoof = false;
		UpdateInfoCursor();
		if (moveBuilding == RelocateBuilding.None)
		{
			return;
		}
		if (moveOrInstallPlacedSuccess)
		{
			moveBuilding = RelocateBuilding.None;
			MonoSingleton<TaskController>.Instance.WaitForNextFrameUnscaled().Then(delegate
			{
				CancelSelection(resetCancelPlacement: true);
			});
		}
		moveOrInstallPlacedSuccess = false;
	}

	private void CheckSpawnBuildingMaterials()
	{
		if (!spawnMaterialsWithBuilding || autoconstruct)
		{
			return;
		}
		string[] array = baseBuildingBlueprint.Materials.Dictionary.Keys.ToArray();
		foreach (string text in array)
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(text);
			if (byID == null)
			{
				continue;
			}
			int amount = baseBuildingBlueprint.Materials.Dictionary[text];
			Vector3 zero = Vector3.zero;
			if (buildingsDictionary.Count == 0)
			{
				Vector2Int nextPosition = MonoSingleton<StartPositionManager>.Instance.GetNextPosition();
				zero = new Vector3(nextPosition.x, MonoSingleton<Heightmap>.Instance.GetHeightAt(nextPosition) * World.MapBlockHeight, nextPosition.y);
				MonoSingleton<ResourcePileManager>.Instance.SpawnPile(new ResourceInstance(byID, amount), zero);
				continue;
			}
			foreach (Vec3Int key in buildingsDictionary.Keys)
			{
				_ = key;
				zero = GridUtils.GetWorldPosition(buildingsDictionary.Keys.First());
				MonoSingleton<ResourcePileManager>.Instance.SpawnPile(new ResourceInstance(byID, amount), zero);
			}
		}
	}

	private void OnLeftMouseDown(bool skip = false)
	{
		if (hasSelectedItem && (skip || baseBuildingBlueprint.PlacementType != 0))
		{
			ClearPileToInstall();
			raycastGridStart = GetGridPositionFromRaycast();
			if (MonoSingleton<KeybindingManager>.Instance.IsKeybindingKeyDown(KeyInputEvent.LeftControl))
			{
				MonoSingleton<World>.Instance.SwitchToLowerLayer(raycastGridStart);
			}
			MonoSingleton<BlueprintPlaceVisualsManager>.Instance.StartSetup(raycastGridStart);
			mouseDown = true;
			wasForbiddenEdge = false;
			raycastMask = defaultRaycastMask;
			raycastGridCurrent = raycastGridStart;
			raycastGridPrevious = raycastGridStart;
			raycastHeplerPlane.SetActive(value: true);
			int num = raycastGridCurrent.y * World.MapBlockHeight;
			Vector3 position = raycastHeplerPlane.transform.position;
			position = new Vector3(position.x, num, position.z);
			raycastHeplerPlane.transform.position = position;
			raycastMask |= raycastPlaneHelperLayer;
		}
	}

	private bool IsPlacementActive()
	{
		if (!(preview != null) || !preview.gameObject.activeSelf)
		{
			return mouseDown;
		}
		return true;
	}

	private void MouseUpRoofs()
	{
		int eulerAngleY = preview.transform.GetEulerAngleY();
		CreateRoofs(eulerAngleY);
	}

	private void CreateRoofs(int angleY)
	{
		if (buildingsDictionary.Count == 0)
		{
			DragSpawnRoof(raycastGridStart);
			foreach (Vec3Int key in roofPositionView.Keys)
			{
				roofPositionView[key].Scale(Vec3Int.one);
				roofPositionView[key].ClearPositions();
				roofPositionView[key].AddPosition(raycastGridStart);
			}
		}
		Vec3Int[] array = roofPositionView.Keys.ToArray();
		foreach (Vec3Int vec3Int in array)
		{
			if (!Map.BuildingsManagerMain.CanPlaceRoof(roofPositionView[vec3Int]))
			{
				RemoveRoof(vec3Int);
				continue;
			}
			Map.BuildingsManagerMain.CreateBuildingInstanceAndBindToView(baseBuildingBlueprint, buildingsDictionary[vec3Int], roofPositionView[vec3Int].transform.position, angleY);
			RoofComponent component = buildingsDictionary[vec3Int].GetComponent<RoofComponent>();
			RoofViewComponent roofViewComponent = roofPositionView[vec3Int];
			Map.RoofComponentManager.CreateAndCacheRoofComponentInstance(roofComponentBlueprint, component, buildingsDictionary[vec3Int].BaseBuildingInstance, roofViewComponent.GetScale);
			ObjectPlacedOnMap(buildingsDictionary[vec3Int]);
		}
		using PooledList<RoofComponentInstance> pooledList = ListPool<RoofComponentInstance>.GetJanitor();
		foreach (RoofViewComponent value in roofPositionView.Values)
		{
			pooledList.Add(value.RoofComponentInstance);
		}
		using PooledList<BaseBuildingInstance> pooledList2 = ListPool<BaseBuildingInstance>.GetJanitor();
		foreach (BaseBuildingViewComponent value2 in buildingsDictionary.Values)
		{
			pooledList2.Add(value2.BaseBuildingInstance);
		}
		map.RoofMeshVariationManager.Run(pooledList2);
		if (autoconstruct)
		{
			BaseBuildingViewComponent[] array2 = buildingsDictionary.Values.ToArray();
			foreach (BaseBuildingViewComponent baseBuildingViewComponent in array2)
			{
				buildingsToAutoConstruct.Add(baseBuildingViewComponent.BaseBuildingInstance);
			}
			AutoConstructBuildInOrder(buildingsToAutoConstruct);
		}
		roofPositionView.Clear();
		buildingsDictionary.Clear();
	}

	private void CreateRoofs(int angleY, Vec3Int scale, List<Vec3Int> positions)
	{
		if (buildingsDictionary.Count == 0)
		{
			DragSpawnRoof(raycastGridStart);
			foreach (Vec3Int key in roofPositionView.Keys)
			{
				roofPositionView[key].Scale(Vec3Int.one);
				roofPositionView[key].ClearPositions();
				roofPositionView[key].AddPosition(raycastGridStart);
			}
		}
		Vec3Int[] array = roofPositionView.Keys.ToArray();
		foreach (Vec3Int vec3Int in array)
		{
			if (!Map.BuildingsManagerMain.CanPlaceRoof(roofPositionView[vec3Int]))
			{
				RemoveRoof(vec3Int);
				continue;
			}
			Map.BuildingsManagerMain.CreateBuildingInstanceAndBindToView(baseBuildingBlueprint, buildingsDictionary[vec3Int], roofPositionView[vec3Int].transform.position, angleY);
			RoofComponent component = buildingsDictionary[vec3Int].GetComponent<RoofComponent>();
			RoofViewComponent roofViewComponent = roofPositionView[vec3Int];
			roofViewComponent.AddPositions(positions);
			roofViewComponent.Scale(scale);
			Map.RoofComponentManager.CreateAndCacheRoofComponentInstance(roofComponentBlueprint, component, buildingsDictionary[vec3Int].BaseBuildingInstance, scale.ToVector3());
			ObjectPlacedOnMap(buildingsDictionary[vec3Int]);
		}
		if (autoconstruct)
		{
			BaseBuildingViewComponent[] array2 = buildingsDictionary.Values.ToArray();
			foreach (BaseBuildingViewComponent baseBuildingViewComponent in array2)
			{
				buildingsToAutoConstruct.Add(baseBuildingViewComponent.BaseBuildingInstance);
			}
			AutoConstructBuildInOrder(buildingsToAutoConstruct);
		}
		roofPositionView.Clear();
		buildingsDictionary.Clear();
	}

	private void ObjectPlacedOnMap(BaseBuildingViewComponent baseBuildingViewComponent)
	{
		BaseBuildingInstance baseBuildingInstance = baseBuildingViewComponent.BaseBuildingInstance;
		baseBuildingViewComponent.PreObjectPlacedOnMap();
		baseBuildingInstance.ObjectPlacedOnMap();
		baseBuildingViewComponent.ObjectPlacedOnMap();
		if (autoconstruct && baseBuildingInstance.Blueprint.PlacementType == PlacementType.WallSocket)
		{
			baseBuildingInstance.AutoConstructSequence();
		}
		DebugEventLog.Write(new BuildingPlaced(baseBuildingInstance));
	}

	private Vec3Int GetGridPositionFromRaycast(bool adjustY = true)
	{
		ray = mainCamera.ScreenPointToRay(Input.mousePosition);
		int num = Physics.RaycastNonAlloc(ray, raycastHits, 1000f, raycastMask);
		if (num > 0)
		{
			hit = GetClosestRaycastHit(raycastHits, num);
			Vec3Int position = GetAdjustedWorldPosition(hit, adjustY);
			if (ForcePreviewObjectDownOneLevel(position))
			{
				position.y -= World.MapBlockHeight;
			}
			return new Vec3Int(position.x, (int)((float)position.y + 0.5f) / World.MapBlockHeight, position.z);
		}
		Vec3Int result = new Vec3Int((int)ray.GetPoint(1000f).x, (int)ray.GetPoint(1000f).y, (int)ray.GetPoint(1000f).z);
		Vec3Int min = Vec3Int.zero;
		Vec3Int max = new Vec3Int(world.SizeX, world.SizeY, world.SizeZ);
		result.Clamp(in min, in max);
		return result;
	}

	private bool ForcePreviewObjectDownOneLevel(Vec3Int position)
	{
		position.y /= World.MapBlockHeight;
		Vec3Int b;
		if (hitSide == ObjectSide.Top)
		{
			BuildingsManagerMain buildingsManagerMain = Map.BuildingsManagerMain;
			b = Vec3Int.down;
			if (buildingsManagerMain.WallTypeBuildingExists(position + b))
			{
				return false;
			}
		}
		else if (Map.BuildingsManagerMain.WallTypeBuildingExists(position))
		{
			return false;
		}
		if (Map.BuildingsManagerMain.BuildingExists(position, BuildingType.Floor))
		{
			return false;
		}
		Vec3Int[] array = new Vec3Int[5];
		b = Vec3Int.right;
		array[0] = position + b;
		Vec3Int b2 = Vec3Int.left;
		array[1] = position + b2;
		Vec3Int b3 = new Vec3Int(0, 0, 1);
		array[2] = position + b3;
		Vec3Int b4 = new Vec3Int(0, 0, -1);
		array[3] = position + b4;
		array[4] = position;
		Vec3Int[] array2 = array;
		BuildingType checkFor = BuildingType.Wall | BuildingType.Floor | BuildingType.Window | BuildingType.Door | BuildingType.Merlon;
		for (int i = 0; i < array2.Length; i++)
		{
			if (Map.BuildingsManagerMain.BuildingExists(checkFor, array2[i]))
			{
				return false;
			}
			if (MonoSingleton<GroundManager>.Instance.GroundExists(array2[i]))
			{
				return false;
			}
		}
		b = Vec3Int.down;
		Vec3Int vec3Int = position + b;
		if (Map.BuildingsManagerMain.BuildingExists(BuildingType.Beam, vec3Int))
		{
			return false;
		}
		if (VillageManager.ActiveVillage.Map.IsEmpty(vec3Int))
		{
			return false;
		}
		if (Map.BuildingsManagerMain.BuildingExists(BuildingType.Floor | BuildingType.ProductionBuilding, vec3Int))
		{
			return true;
		}
		if (!MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int))
		{
			return VillageManager.ActiveVillage.Map.IsEmpty(vec3Int);
		}
		return false;
	}

	private Vec3Int GetAdjustedWorldPosition(RaycastHit hit, bool adjustY = true)
	{
		tempSide = CalculateSide(this.hit);
		if (tempSide != ObjectSide.Top)
		{
			Vec3Int gridPosition = GridUtils.GetGridPosition(hit.transform.position);
			if (Map.BuildingsManagerMain.TryGetBasicBuilding(gridPosition, (BaseBuildingInstance x) => baseBuildingBlueprint.ReplacementFlag.HasFlag(x.BuildingType)) != null)
			{
				List<BaseBuildingInstance> buildings = Map.BuildingsManagerMain.GetBuildings(gridPosition);
				bool flag = true;
				foreach (BaseBuildingInstance item in buildings)
				{
					if (item.BuildingType.Equals(baseBuildingBlueprint.BuildingType))
					{
						flag = false;
						break;
					}
				}
				if (flag)
				{
					return new Vec3Int(gridPosition.x, gridPosition.y * World.MapBlockHeight, gridPosition.z);
				}
			}
		}
		Vec3Int a = ((!adjustY) ? new Vec3Int((int)(hit.point.x + 0.5f), (int)hit.point.y, (int)(hit.point.z + 0.5f)) : new Vec3Int((int)(hit.point.x + 0.5f), (int)(hit.point.y + 0.5f), (int)(hit.point.z + 0.5f)));
		if (tempSide == ObjectSide.Left)
		{
			Vec3Int b = new Vec3Int(-1, 0, 0);
			a += b;
		}
		_ = tempSide;
		_ = 8;
		_ = tempSide;
		_ = 16;
		if (tempSide == ObjectSide.Back)
		{
			Vec3Int b = new Vec3Int(0, 0, -1);
			a += b;
		}
		_ = tempSide;
		_ = 2;
		if (adjustedWorldPosition != a)
		{
			hitSide = tempSide;
			adjustedWorldPosition = a;
		}
		return a;
	}

	private ObjectSide CalculateSide(RaycastHit hit)
	{
		Vector3 vector = hit.normal - Vector3.up;
		if (vector == new Vector3(0f, -1f, -1f))
		{
			return ObjectSide.Back;
		}
		if (vector == new Vector3(0f, -1f, 1f))
		{
			return ObjectSide.Front;
		}
		if (vector == new Vector3(0f, 0f, 0f))
		{
			return ObjectSide.Top;
		}
		if (vector == new Vector3(1f, 1f, 1f))
		{
			return ObjectSide.Bottom;
		}
		if (vector == new Vector3(-1f, -1f, 0f))
		{
			return ObjectSide.Left;
		}
		if (vector == new Vector3(1f, -1f, 0f))
		{
			return ObjectSide.Right;
		}
		return ObjectSide.None;
	}

	public void InitializeBuilding(string id, RelocateBuilding moveBuilding = RelocateBuilding.None)
	{
		skipAutomaticRotation = false;
		userForceMerlonRotation = false;
		this.moveBuilding = moveBuilding;
		ResetPreviewGrids();
		baseBuildingBlueprint = Repository<BaseBuildingRepository, BaseBuildingBlueprint>.Instance.GetByID(id);
		if (baseBuildingBlueprint == null)
		{
			Debug.LogError("Blueprint " + id + " not found in BaseBuildingRepository.json!");
			return;
		}
		DoorComponentBlueprint byID = Repository<DoorComponentRepository, DoorComponentBlueprint>.Instance.GetByID(baseBuildingBlueprint.DoorComponentID);
		if (byID != null)
		{
			DoorType doorType = byID.DoorType;
			skipAutomaticRotation = doorType == DoorType.Drawbridge || doorType == DoorType.Portcullis;
		}
		if (baseBuildingBlueprint.BuildingType == BuildingType.Roof)
		{
			roofComponentBlueprint = Repository<RoofComponentRepository, RoofComponentBlueprint>.Instance.GetByID(baseBuildingBlueprint.RoofComponentID);
			if (roofComponentBlueprint == null)
			{
				bool isEnabled;
				FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(62, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingPlacementManager.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Roof component blueprint ");
					messageBuilder.AppendFormatted(id);
					messageBuilder.AppendLiteral(" not found in RoofComponentRepository");
				}
				Log.Error(messageBuilder);
				return;
			}
		}
		preview = SpawnPreview(baseBuildingBlueprint);
		preview.SetActive(value: true);
		SetupForbiddenAreaPreview(baseBuildingBlueprint);
		previewAngle = preview.transform.GetEulerAngleY();
		if (baseBuildingBlueprint.BuildingType == BuildingType.Beam)
		{
			beamPreview = preview.GetComponent<BeamPreviewView>();
		}
		Map.BeamComponentManager.AllBeamCollidersEnabled(baseBuildingBlueprint.CanPlaceOnBeam);
		if (baseBuildingBlueprint.PlacementType.Equals(PlacementType.SinglePlacement))
		{
			RefreshHoverRulers();
		}
		else if (baseBuildingBlueprint.PlacementType == PlacementType.WallSocket)
		{
			uicontroller.ShowActionInfo(new List<string>
			{
				ActionInfoUtils.Socketable,
				ActionInfoUtils.Dismiss
			});
		}
		else
		{
			uicontroller.ShowActionInfo(new List<string>
			{
				ActionInfoUtils.PlaceRow,
				ActionInfoUtils.PlaceRotate,
				ActionInfoUtils.Dismiss
			});
		}
		CacheCursorInfoData();
		MonoSingleton<ConstructionController>.Instance.ChangeBuildingTypeToPlace();
	}

	private void SetupForbiddenAreaPreview(BaseBuildingBlueprint blueprint)
	{
		if (!(blueprint == null) && blueprint.ForbiddenAreaInfo.HasForbiddenArea && !(preview == null))
		{
			forbiddenAreaIndicatorFront.transform.parent = preview.transform;
			forbiddenAreaIndicatorBack.transform.parent = preview.transform;
			forbiddenAreaIndicatorLeft.transform.parent = preview.transform;
			forbiddenAreaIndicatorRight.transform.parent = preview.transform;
			forbiddenAreaIndicatorFront.transform.localRotation = Quaternion.identity;
			forbiddenAreaIndicatorBack.transform.localRotation = Quaternion.identity;
			forbiddenAreaIndicatorLeft.transform.localRotation = Quaternion.identity;
			forbiddenAreaIndicatorRight.transform.localRotation = Quaternion.identity;
			ForbiddenAreaInfo forbiddenAreaInfo = blueprint.ForbiddenAreaInfo;
			Vec3Int size = blueprint.Size;
			float x = ((blueprint.Size.x == 1) ? 0f : ((blueprint.Size.x % 2 != 0) ? ((float)(blueprint.Size.x / 2)) : ((float)(blueprint.Size.x / 2) - 0.5f)));
			float z = ((blueprint.Size.z == 1) ? 0f : ((blueprint.Size.z % 2 != 0) ? ((float)blueprint.Size.z / 2f) : ((float)(blueprint.Size.z / 2) - 0.5f)));
			if (forbiddenAreaInfo.HasFrontOffset)
			{
				forbiddenAreaIndicatorFront.transform.localScale = new Vector3(size.x, 1f, forbiddenAreaInfo.ForbiddenAreaFrontOffset);
				forbiddenAreaIndicatorFront.transform.localPosition = new Vector3(x, 0.1f, (float)size.z + GetOffset(forbiddenAreaInfo.ForbiddenAreaFrontOffset));
			}
			if (forbiddenAreaInfo.HasBackOffset)
			{
				forbiddenAreaIndicatorBack.transform.localScale = new Vector3(size.x, 1f, forbiddenAreaInfo.ForbiddenAreaBackOffset);
				forbiddenAreaIndicatorBack.transform.localPosition = new Vector3(x, 0.1f, -1f - GetOffset(forbiddenAreaInfo.ForbiddenAreaBackOffset));
			}
			if (forbiddenAreaInfo.HasLeftOffset)
			{
				forbiddenAreaIndicatorLeft.transform.localScale = new Vector3(forbiddenAreaInfo.ForbiddenAreaLeftOffset, 1f, size.z);
				forbiddenAreaIndicatorLeft.transform.localPosition = new Vector3(-1f - GetOffset(forbiddenAreaInfo.ForbiddenAreaLeftOffset), 0.1f, z);
			}
			if (forbiddenAreaInfo.HasRightOffset)
			{
				forbiddenAreaIndicatorRight.transform.localScale = new Vector3(forbiddenAreaInfo.ForbiddenAreaRightOffset, 1f, size.z);
				forbiddenAreaIndicatorRight.transform.localPosition = new Vector3((float)size.x + GetOffset(forbiddenAreaInfo.ForbiddenAreaRightOffset), 0.1f, z);
			}
		}
		static float GetOffset(int length)
		{
			if (length == 1)
			{
				return 0f;
			}
			if (length % 2 == 0)
			{
				return (float)(length / 2) - 0.5f;
			}
			return length / 2;
		}
	}

	private void RefreshHoverRulers()
	{
		uicontroller.ShowActionInfo(new List<string>
		{
			ActionInfoUtils.PlaceSingle,
			ActionInfoUtils.PlaceRotate,
			ActionInfoUtils.Dismiss
		});
		singlePlacementPreviewGridRuler.transform.parent = preview.transform;
		singlePlacementPreviewGridRuler.Scale(baseBuildingBlueprint.Size);
		BaseBuildingInstance movedBaseBuildingInstance = MonoSingleton<MoveBuildingsManager>.Instance.GetMovedBaseBuildingInstance();
		if (movedBaseBuildingInstance != null)
		{
			Quaternion rotation = Quaternion.Euler(new Vector3(0f, movedBaseBuildingInstance.Angle, 0f));
			singlePlacementPreviewGridRuler.transform.rotation = rotation;
			return;
		}
		BaseBuildingInstance buildingToCopy = Map.BuildingsManagerMain.BuildingToCopy;
		if (buildingToCopy != null)
		{
			Quaternion rotation2 = Quaternion.Euler(new Vector3(0f, buildingToCopy.Angle, 0f));
			singlePlacementPreviewGridRuler.transform.rotation = rotation2;
		}
	}

	private void ResetPreviewGrids()
	{
		dragRulers.transform.position = gridResetPos;
		dragRulers.ResetRulers();
		dragGrid.SetActive(value: false);
		socketablePreviewGridRuler.transform.position = gridResetPos;
		socketablePreviewGridRuler.HideGrid();
		Transform obj = singlePlacementPreviewGridRuler.transform;
		obj.parent = base.transform;
		obj.position = gridResetPos;
		foreach (GameObject forbiddenAreaIndicator in forbiddenAreaIndicators)
		{
			forbiddenAreaIndicator.transform.parent = base.transform;
			forbiddenAreaIndicator.transform.localScale = Vector3.one;
			forbiddenAreaIndicator.transform.eulerAngles = Vector3.zero;
			forbiddenAreaIndicator.transform.position = gridResetPos;
		}
		singlePlacementPreviewGridRuler.ResetRulers();
	}

	private GameObject SpawnPreview(BaseBuildingBlueprint blueprint)
	{
		if (preview != null)
		{
			UnityEngine.Object.Destroy(preview);
			preview = null;
		}
		hasSelectedItem = true;
		GameObject gameObject = UnityEngine.Object.Instantiate(MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByAddress(blueprint.PreviewPrefabID), Input.mousePosition, Quaternion.identity);
		wellPreviewView = gameObject.GetComponent<WellPreviewView>();
		originalPreviewLayer = gameObject.layer;
		BaseBuildingPreview component = gameObject.GetComponent<BaseBuildingPreview>();
		if ((object)component != null)
		{
			component.SetMarkerTransform(blueprint);
			component.SetWorkPositionsMarkersTransforms(blueprint);
		}
		BaseBuildablePreview component2 = gameObject.GetComponent<BaseBuildablePreview>();
		if ((object)component2 == null)
		{
			return gameObject;
		}
		baseBuildablePreview = component2;
		baseBuildablePreview.Initialize(blueprint);
		component2.SetMesh(blueprint);
		IReadOnlyList<string> movedObjectMeshVariations = MonoSingleton<MoveBuildingsManager>.Instance.GetMovedObjectMeshVariations();
		if (movedObjectMeshVariations != null && movedObjectMeshVariations.Count > 0)
		{
			component2.UpdateMeshVariations(movedObjectMeshVariations, baseBuildingBlueprint);
		}
		else
		{
			component2.UpdateMeshVariation("default", baseBuildingBlueprint);
		}
		BaseBuildingInstance movedBaseBuildingInstance = MonoSingleton<MoveBuildingsManager>.Instance.GetMovedBaseBuildingInstance();
		if (movedBaseBuildingInstance != null)
		{
			Quaternion rotation = Quaternion.Euler(new Vector3(0f, movedBaseBuildingInstance.Angle, 0f));
			component2.transform.rotation = rotation;
			component2.UpdateMeshRotation(movedBaseBuildingInstance);
			return gameObject;
		}
		BaseBuildingInstance buildingToCopy = Map.BuildingsManagerMain.BuildingToCopy;
		if (buildingToCopy != null)
		{
			Quaternion rotation2 = Quaternion.Euler(new Vector3(0f, buildingToCopy.Angle, 0f));
			component2.transform.rotation = rotation2;
			component2.UpdateMeshRotation(buildingToCopy);
		}
		return gameObject;
	}

	private void OnTick(float deltaTime)
	{
		using (ProfilerSampleJanitor.Begin("BuildingPlacementManager.Tick"))
		{
			if (!setupCompleted || preview == null)
			{
				return;
			}
			if (showCantDigTopLayer && !mouseDown)
			{
				showCantDigTopLayer = false;
				MonoSingleton<GroundManager>.Instance.ShowBlackBarTextCantBuildTopLayer();
			}
			if (baseBuildingBlueprint == null)
			{
				return;
			}
			if (!mouseDown)
			{
				if (raycastGridPrevious != raycastGridCurrent)
				{
					raycastGridPrevious = raycastGridCurrent;
					userForceMerlonRotation = false;
				}
				raycastGridCurrent = GetGridPositionFromRaycast(baseBuildingBlueprint.PlacementType != PlacementType.WallSocket);
				preview.transform.position = GridUtils.GetWorldPosition(raycastGridCurrent);
				CacheCursorInfoData();
				UpdateMerlonPreview();
			}
			TryUpdateReplacementColorWhileHovering();
			SocketablePlacementTick();
			UpdatePreviewVisuals();
			BeamPlacementTick(GetHitSide(), GetBeamGridPositionFromRaycast());
			UpdateDragGridRulers();
			UpdateWellVisuals();
			baseBuildablePreview?.Refresh();
			UpdateMerlonPreviewAngle();
			if (!mouseDown && raycastGridCurrent != raycastGridPrevious)
			{
				AutoRotate();
			}
		}
	}

	private void UpdateMerlonPreview()
	{
		if (baseBuildingBlueprint.BuildingType == BuildingType.Merlon && !userForceMerlonRotation)
		{
			float previewAngleForFloorAttachment = map.MerlonRotationManager.GetPreviewAngleForFloorAttachment(preview.transform.position.ToGridVec3Int());
			if (previewAngleForFloorAttachment >= 0f)
			{
				SetMerlonPreviewAngle(previewAngleForFloorAttachment);
			}
		}
	}

	private bool IsSocketablePlacement()
	{
		if (baseBuildingBlueprint == null)
		{
			return false;
		}
		return baseBuildingBlueprint.PlacementType == PlacementType.WallSocket;
	}

	private void SocketablePlacementTick()
	{
		if (IsSocketablePlacement())
		{
			raycastGridPrevious = raycastGridCurrent;
			raycastGridCurrent = GetGridPositionFromRaycast(adjustY: false);
			if (hitSide.Equals(ObjectSide.Top) || hitSide.Equals(ObjectSide.Bottom) || hitSide.Equals(ObjectSide.None))
			{
				preview.transform.position = GetWorldPositionFromRaycastClampY();
				socketablePreviewGridRuler.HideGrid();
			}
			else
			{
				preview.transform.position = GridUtils.GetWorldPosition(raycastGridCurrent);
				socketablePreviewGridRuler.ShowGrid();
				socketablePreviewGridRuler.SetAngle(hitSide);
			}
			socketablePreviewGridRuler.transform.position = preview.transform.position;
			AdjustSocketablePreview(GetHitSide(), GetSocketableGridPositionFromRaycast());
		}
	}

	private void TrySpawnBeam()
	{
		if (!validSockets.HasFlag(hitSide))
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("beam_error_default"));
			return;
		}
		if (beamTooLong)
		{
			string text = MonoSingleton<LocalizationController>.Instance.GetText("error_beam_min_distance");
			text = text.Replace("{0}", (maxLength - 2).ToString());
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(text);
			return;
		}
		if (beamBlockedBySlope)
		{
			Map.BuildingsManagerMain.ShowMessageBlockedBySlope();
			return;
		}
		if (beamBlockerBuilding != null)
		{
			Map.BuildingsManagerMain.ShowMessageBlockedByBuilding(beamBlockerBuilding);
			return;
		}
		if (beamBlockedByBuildingForbiddenArea)
		{
			string text2 = MonoSingleton<LocalizationController>.Instance.GetText("Can't place in area forbidden by other buildings!");
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(text2);
			return;
		}
		if (beamIntersection)
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("error_beam_intersect"));
			return;
		}
		if (startSocketOccupied || endSocketOccupied)
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("default_error_space_taken"));
			return;
		}
		if (!placeable)
		{
			if (canShowBeamPlacementError && objectsInTheWay && !beamPreview.Scale.Equals(Vector3.one))
			{
				canShowBeamPlacementError = false;
				StartCoroutine(ShowMessageBeamHasObstacle(beamPreview.Transform.position));
			}
			return;
		}
		object obj = ((startWall != null) ? startWall : ((object)startVoxelPos));
		object obj2 = ((endWall != null) ? endWall : ((object)endVoxelPos));
		switch (hitSide)
		{
		case ObjectSide.Left:
			SpawnBeamAxisX(baseBuildingBlueprint, obj2, obj);
			break;
		case ObjectSide.Right:
			SpawnBeamAxisX(baseBuildingBlueprint, obj, obj2);
			break;
		case ObjectSide.Front:
			SpawnBeamAxisZ(baseBuildingBlueprint, obj, obj2);
			break;
		case ObjectSide.Back:
			SpawnBeamAxisZ(baseBuildingBlueprint, obj2, obj);
			break;
		}
		startVoxelPos = Vec3Int.down;
		endVoxelPos = Vec3Int.down;
	}

	private void AdjustSocketablePreview(ObjectSide hitSide, Vec3Int gridPosition)
	{
		socketPlaceable = false;
		objectHolderPos = gridPosition;
		if (PlaceableOnVoxel(gridPosition, hitSide))
		{
			socketPlaceable = true;
		}
		buildingInstance = Map.BuildingsManagerMain.TryGetWallForBeam(gridPosition);
		if (buildingInstance != null)
		{
			socketPlaceable = true;
		}
		if (!socketPlaceable)
		{
			return;
		}
		switch (hitSide)
		{
		case ObjectSide.Left:
			SetHitData(gridPosition, new Vec3Int(gridPosition.x - 1, gridPosition.y, gridPosition.z), 180, hitSide);
			break;
		case ObjectSide.Right:
			SetHitData(gridPosition, new Vec3Int(gridPosition.x + 1, gridPosition.y, gridPosition.z), 0, hitSide);
			break;
		case ObjectSide.Front:
			SetHitData(gridPosition, new Vec3Int(gridPosition.x, gridPosition.y, gridPosition.z + 1), 270, hitSide);
			break;
		case ObjectSide.Back:
			SetHitData(gridPosition, new Vec3Int(gridPosition.x, gridPosition.y, gridPosition.z - 1), 90, hitSide);
			break;
		}
		if (adjacent.Equals(Vec3Int.down))
		{
			ref Vec3Int reference = ref objectHolderPos;
			Vec3Int rhs = Vec3Int.down;
			if (!(reference != rhs))
			{
				return;
			}
		}
		if (!map.BuildingsManagerMain.SocketableBlockerExists(adjacent))
		{
			preview.transform.eulerAngles = new Vector3(0f, previewAngle, 0f);
		}
	}

	private void SetHitData(Vec3Int objectHolderPos, Vec3Int adjacent, int angle, ObjectSide socket)
	{
		this.objectHolderPos = objectHolderPos;
		this.adjacent = adjacent;
		previewAngle = angle;
		this.socket = socket;
	}

	private bool PlaceableOnVoxel(Vec3Int position, ObjectSide targetSide)
	{
		if (!MonoSingleton<GroundManager>.Instance.GroundExists(position))
		{
			return false;
		}
		return !MonoSingleton<GroundManager>.Instance.VoxelSocketOccupied(position, targetSide);
	}

	private Vec3Int GetSocketableGridPositionFromRaycast()
	{
		ray = mainCamera.ScreenPointToRay(Input.mousePosition);
		int num = Physics.RaycastNonAlloc(ray, raycastHits, 1000f, raycastMask);
		if (num > 0)
		{
			hit = GetClosestRaycastHit(raycastHits, num);
			return TryGetSocketWallPosition(hit);
		}
		Vec3Int result = new Vec3Int((int)ray.GetPoint(1000f).x, (int)ray.GetPoint(1000f).y, (int)ray.GetPoint(1000f).z);
		Vec3Int min = new Vec3Int(0, 0, 0);
		Vec3Int max = new Vec3Int(world.SizeX, world.SizeY, world.SizeZ);
		result.Clamp(in min, in max);
		return result;
	}

	private Vec3Int TryGetSocketWallPosition(RaycastHit hit)
	{
		if (CalculateSide(this.hit) != ObjectSide.Top)
		{
			Vec3Int socketableAdjustedPosition = GetSocketableAdjustedPosition(hit);
			socketableAdjustedPosition.y /= World.MapBlockHeight;
			return socketableAdjustedPosition;
		}
		return Vec3Int.down;
	}

	private Vec3Int GetSocketableAdjustedPosition(RaycastHit hit)
	{
		hitSide = CalculateSide(this.hit);
		Vec3Int a = new Vec3Int((int)(hit.point.x + 0.5f), (int)hit.point.y, (int)(hit.point.z + 0.5f));
		if (hitSide == ObjectSide.Left)
		{
			return a;
		}
		if (hitSide == ObjectSide.Right)
		{
			Vec3Int b = new Vec3Int(-1, 0, 0);
			return a + b;
		}
		if (hitSide == ObjectSide.Front)
		{
			Vec3Int b = new Vec3Int(0, 0, -1);
			return a + b;
		}
		if (hitSide == ObjectSide.Back)
		{
			return a;
		}
		_ = hitSide;
		_ = 2;
		return a;
	}

	private ObjectSide GetHitSide()
	{
		ray = mainCamera.ScreenPointToRay(Input.mousePosition);
		int num = Physics.RaycastNonAlloc(ray, raycastHits, 1000f, raycastMask);
		if (num > 0)
		{
			hit = GetClosestRaycastHit(raycastHits, num);
			return CalculateSide(hit);
		}
		return ObjectSide.None;
	}

	private Vector3 GetWorldPositionFromRaycastClampY()
	{
		ray = mainCamera.ScreenPointToRay(Input.mousePosition);
		int num = Physics.RaycastNonAlloc(ray, raycastHits, 1000f, raycastMask);
		if (num > 0)
		{
			hit = GetClosestRaycastHit(raycastHits, num);
			return new Vector3(hit.point.x, (int)(hit.point.y + 0.5f), hit.point.z);
		}
		return ray.GetPoint(1000f);
	}

	private void OnSelectionAssignOrder(OrderType orderType, AreaType areaType)
	{
		CancelSelection();
	}

	public void CancelSelection(bool resetCancelPlacement = false)
	{
		if (hasSelectedItem)
		{
			mouseDown = false;
			ClearPileToInstall();
			ResetPreviewGrids();
			uicontroller.ToggleInfoCursor(active: false);
			uicontroller.HideActionInfo();
			raycastHeplerPlane.transform.position = gridResetPos;
			preview.SetActive(value: true);
			UnityEngine.Object.Destroy(preview);
			preview = null;
			hasSelectedItem = false;
			raycastMask = defaultRaycastMask;
			cancelPlacement = true;
			ClearAllObjects();
			dragDirection = DragDirection.None;
			buildingsDictionary.Clear();
			roofPositionView.Clear();
			Map.BeamComponentManager.AllBeamCollidersEnabled(value: true);
			baseBuildingBlueprint = null;
			roofComponentBlueprint = null;
			draggingRoof = false;
			twoAxisDrag = false;
			singleAxisDrag = false;
			moveBuilding = RelocateBuilding.None;
			skipAutomaticRotation = false;
			userForceMerlonRotation = false;
			raycastGridCurrent = Vec3Int.down;
			this.SelectionCanceledEvent?.Invoke();
			if (resetCancelPlacement)
			{
				cancelPlacement = false;
			}
			baseBuildablePreview = null;
		}
	}

	public void OnLeftMouseDrag()
	{
		if (!hasSelectedItem || baseBuildingBlueprint.PlacementType.Equals(PlacementType.WallSocket) || cancelPlacement)
		{
			return;
		}
		if (baseBuildingBlueprint.PlacementType != 0 && preview != null && preview.activeSelf)
		{
			preview.SetActive(value: false);
		}
		if (raycastGridStart.y >= MonoSingleton<World>.Instance.SizeY)
		{
			return;
		}
		Vec3Int rhs = GetGridPositionFromRaycast();
		if (raycastGridCurrent != rhs)
		{
			raycastGridPrevious = raycastGridCurrent;
			raycastGridCurrent = rhs;
			if (baseBuildingBlueprint.PlacementType != 0)
			{
				InitializeDragGrid();
			}
		}
		switch (baseBuildingBlueprint.PlacementType)
		{
		case PlacementType.SingleAxisDrag:
			if (Input.GetKey(KeyCode.LeftAlt))
			{
				TwoAxisDragPlacementTick(fillCenter: false);
			}
			else
			{
				SingleAxisDragPlacementTick();
			}
			break;
		case PlacementType.TwoAxisDrag:
			TwoAxisDragPlacementTick();
			break;
		case PlacementType.Roof:
			RoofPlacementTick();
			break;
		case PlacementType.SinglePlacement:
		case PlacementType.WallSocket:
		case PlacementType.None:
			break;
		}
	}

	private void InitializeDragGrid()
	{
		if (!dragGrid.activeSelf)
		{
			PlacementType placementType = baseBuildingBlueprint.PlacementType;
			if (placementType == PlacementType.Roof || placementType == PlacementType.SingleAxisDrag || placementType == PlacementType.TwoAxisDrag)
			{
				dragGrid.SetActive(value: true);
			}
		}
	}

	private Vector3 GetWorldPositionFromRaycast()
	{
		ray = mainCamera.ScreenPointToRay(Input.mousePosition);
		int num = Physics.RaycastNonAlloc(ray, raycastHits, 1000f, raycastMask);
		if (num > 0)
		{
			hit = GetClosestRaycastHit(raycastHits, num);
			return hit.point;
		}
		return ray.GetPoint(1000f);
	}

	private RaycastHit GetClosestRaycastHit(RaycastHit[] raycastHits, int numberOfHits)
	{
		int num = -1;
		for (int i = 0; i < numberOfHits; i++)
		{
			if (!raycastHits[i].transform.gameObject.name.Equals("selection_collider_room") && (num == -1 || raycastHits[i].distance < raycastHits[num].distance))
			{
				num = i;
			}
		}
		return raycastHits[num];
	}

	private void UpdatePreviewVisuals()
	{
		if (baseBuildingBlueprint.PlacementType == PlacementType.SinglePlacement)
		{
			singlePlacementPreviewGridRuler.gameObject.transform.position = preview.transform.position;
		}
	}

	private void SingleAxisDragPlacementTick(bool forceExecute = false)
	{
		singleAxisDrag = true;
		difference = raycastGridStart - raycastGridCurrent;
		diffX = difference.x;
		diffZ = difference.z;
		wasForbiddenEdge = false;
		if (Mathf.Abs(diffX) >= Mathf.Abs(diffZ))
		{
			raycastGridCurrent = new Vec3Int(raycastGridCurrent.x, raycastGridStart.y, raycastGridStart.z);
			if (raycastGridPrevious == raycastGridCurrent && !forceExecute)
			{
				return;
			}
			if (diffX <= 0f)
			{
				dragDirection = DragDirection.PositiveX;
				if (previousDragDirection != dragDirection)
				{
					DirectionChangeObjectPreviewUpdate();
				}
				Vec3Int[] array = buildingsDictionary.Keys.ToArray();
				for (int i = 0; i < array.Length; i++)
				{
					Vec3Int position = array[i];
					if (position.x >= raycastGridCurrent.x)
					{
						RemoveObject(position);
					}
				}
				int num = ((Mathf.Abs(raycastGridStart.x - raycastGridCurrent.x) < maximumGridSize) ? raycastGridCurrent.x : (raycastGridStart.x + maximumGridSize));
				UpdateDragRulers(raycastGridStart.x, num, raycastGridStart.z, raycastGridStart.z);
				for (int j = raycastGridStart.x; j <= num; j++)
				{
					if (GridDataIndexTools.IsForbiddenEdge(j, raycastGridStart.z))
					{
						wasForbiddenEdge = true;
					}
					SpawnFromPool(baseBuildingBlueprint, j, raycastGridStart.y, raycastGridStart.z);
				}
				UpdateInfoCursorSingleAxisDrag(raycastGridCurrent.x - raycastGridStart.x + 1, buildingsDictionary.Count);
				Map.StabilityManager.UpdateStabilitiesWhileDragging(buildingsDictionary.Keys.ToList());
			}
			else
			{
				dragDirection = DragDirection.NegativeX;
				if (previousDragDirection != dragDirection)
				{
					DirectionChangeObjectPreviewUpdate();
				}
				Vec3Int[] array = buildingsDictionary.Keys.ToArray();
				for (int i = 0; i < array.Length; i++)
				{
					Vec3Int position2 = array[i];
					if (position2.x <= raycastGridCurrent.x)
					{
						RemoveObject(position2);
					}
				}
				int num2 = ((Mathf.Abs(raycastGridStart.x - raycastGridCurrent.x) < maximumGridSize) ? raycastGridCurrent.x : (raycastGridStart.x - maximumGridSize));
				UpdateDragRulers(num2, raycastGridStart.x, raycastGridStart.z, raycastGridStart.z);
				for (int num3 = raycastGridStart.x; num3 >= num2; num3--)
				{
					if (GridDataIndexTools.IsForbiddenEdge(num3, raycastGridStart.z))
					{
						wasForbiddenEdge = true;
					}
					SpawnFromPool(baseBuildingBlueprint, num3, raycastGridStart.y, raycastGridStart.z);
				}
				UpdateInfoCursorSingleAxisDrag(raycastGridStart.x - raycastGridCurrent.x + 1, buildingsDictionary.Count);
				Map.StabilityManager.UpdateStabilitiesWhileDragging(buildingsDictionary.Keys.ToList());
			}
		}
		else
		{
			raycastGridCurrent = new Vec3Int(raycastGridStart.x, raycastGridStart.y, raycastGridCurrent.z);
			if (diffZ <= 0f)
			{
				dragDirection = DragDirection.PositiveZ;
				if (previousDragDirection != dragDirection)
				{
					DirectionChangeObjectPreviewUpdate();
				}
				Vec3Int[] array = buildingsDictionary.Keys.ToArray();
				for (int i = 0; i < array.Length; i++)
				{
					Vec3Int position3 = array[i];
					if (position3.z >= raycastGridCurrent.z)
					{
						RemoveObject(position3);
					}
				}
				int num4 = ((Mathf.Abs(raycastGridStart.z - raycastGridCurrent.z) < maximumGridSize) ? raycastGridCurrent.z : (raycastGridStart.z + maximumGridSize));
				UpdateDragRulers(raycastGridStart.x, raycastGridStart.x, raycastGridStart.z, num4);
				for (int k = raycastGridStart.z; k <= num4; k++)
				{
					if (GridDataIndexTools.IsForbiddenEdge(raycastGridStart.x, k))
					{
						wasForbiddenEdge = true;
					}
					SpawnFromPool(baseBuildingBlueprint, raycastGridStart.x, raycastGridStart.y, k);
				}
				UpdateInfoCursorSingleAxisDrag(raycastGridCurrent.z - raycastGridStart.z + 1, buildingsDictionary.Count);
				Map.StabilityManager.UpdateStabilitiesWhileDragging(buildingsDictionary.Keys.ToList());
			}
			else
			{
				dragDirection = DragDirection.NegativeZ;
				if (previousDragDirection != dragDirection)
				{
					DirectionChangeObjectPreviewUpdate();
				}
				Vec3Int[] array = buildingsDictionary.Keys.ToArray();
				for (int i = 0; i < array.Length; i++)
				{
					Vec3Int position4 = array[i];
					if (position4.z <= raycastGridCurrent.z)
					{
						RemoveObject(position4);
					}
				}
				int num5 = ((Mathf.Abs(raycastGridStart.z - raycastGridCurrent.z) < maximumGridSize) ? raycastGridCurrent.z : (raycastGridStart.z + maximumGridSize));
				UpdateDragRulers(raycastGridStart.x, raycastGridStart.x, num5, raycastGridStart.z);
				for (int num6 = raycastGridStart.z; num6 >= num5; num6--)
				{
					if (GridDataIndexTools.IsForbiddenEdge(raycastGridStart.x, num6))
					{
						wasForbiddenEdge = true;
					}
					SpawnFromPool(baseBuildingBlueprint, raycastGridStart.x, raycastGridStart.y, num6);
				}
				UpdateInfoCursorSingleAxisDrag(raycastGridStart.z - raycastGridCurrent.z + 1, buildingsDictionary.Count);
				Map.StabilityManager.UpdateStabilitiesWhileDragging(buildingsDictionary.Keys.ToList());
			}
		}
		UpdateFenceAngle();
		UpdateMerlonAngle();
		if (!baseBuildingBlueprint.ReplacementFlag.Equals(BuildingType.Default))
		{
			TryUpdateReplacementColorDrag();
		}
	}

	private void TryUpdateReplacementColorDrag()
	{
		if (baseBuildingBlueprint.ReplacementFlag.Equals(BuildingType.Default))
		{
			return;
		}
		foreach (KeyValuePair<Vec3Int, BaseBuildingViewComponent> item in buildingsDictionary)
		{
			if (Map.BuildingsManagerMain.GetBuilding(item.Key, (BaseBuildingInstance baseBuildingInstance) => baseBuildingInstance.ConstructionPhase == ConstructionPhase.Blueprint && baseBuildingBlueprint.ReplacementFlag.HasFlag(baseBuildingInstance.BuildingType) && baseBuildingInstance.BuildingType != BuildingType.Floor) != null)
			{
				item.Value.ColorBlueprint(3f);
				item.Value.gameObject.layer = buildingReplacementLayer;
			}
			else
			{
				item.Value.ColorBlueprint(2f);
				item.Value.gameObject.layer = pooledObjectLayer;
			}
		}
	}

	private void TryUpdateReplacementColorWhileHovering()
	{
		if (!baseBuildingBlueprint.ReplacementFlag.Equals(BuildingType.Default) && !(baseBuildablePreview == null))
		{
			if (Map.BuildingsManagerMain.GetBuilding(raycastGridCurrent, (BaseBuildingInstance baseBuildingInstance) => baseBuildingBlueprint.ReplacementFlag.HasFlag(baseBuildingInstance.BuildingType) && baseBuildingInstance.BuildingType != BuildingType.Floor) != null)
			{
				baseBuildablePreview.SetReplacementView(3f, buildingReplacementLayer);
			}
			else
			{
				baseBuildablePreview.SetReplacementView(2f, originalPreviewLayer);
			}
		}
	}

	private bool TryUpdateReplaceColor(Vec3Int gridPosition)
	{
		return Map.BuildingsManagerMain.GetBuilding(gridPosition, (BaseBuildingInstance baseBuildingInstance) => baseBuildingInstance.ConstructionPhase == ConstructionPhase.Blueprint && baseBuildingBlueprint.ReplacementFlag.HasFlag(baseBuildingInstance.BuildingType)) != null;
	}

	private void TwoAxisDragPlacementTick(bool fillCenter = true)
	{
		twoAxisDrag = true;
		Vec3Int b = new Vec3Int(raycastGridCurrent.x, raycastGridStart.y, raycastGridCurrent.z);
		int value = raycastGridCurrent.x - raycastGridStart.x;
		int value2 = raycastGridCurrent.y - raycastGridStart.y;
		int value3 = raycastGridCurrent.z - raycastGridStart.z;
		bounds.center = (Vector3)(raycastGridStart - b);
		bounds.size = new Vector3(Mathf.Abs(value), Mathf.Abs(value2), Mathf.Abs(value3));
		UpdateGridDragDirectionChange();
		wasForbiddenEdge = false;
		if (raycastGridStart.x <= b.x)
		{
			int num = ((Mathf.Abs(raycastGridStart.x - b.x) < maximumGridSize) ? b.x : (raycastGridStart.x + maximumGridSize));
			if (raycastGridStart.z <= b.z)
			{
				int x = raycastGridStart.x;
				int num2 = ((Mathf.Abs(raycastGridStart.z - b.z) < maximumGridSize) ? b.z : (raycastGridStart.z + maximumGridSize));
				int z = raycastGridStart.z;
				UpdateDragRulers(raycastGridStart.x, num, raycastGridStart.z, num2);
				if (fillCenter)
				{
					for (int i = raycastGridStart.x; i <= num; i++)
					{
						for (int j = raycastGridStart.z; j <= num2; j++)
						{
							if (GridDataIndexTools.IsForbiddenEdge(i, j))
							{
								wasForbiddenEdge = true;
							}
							SpawnFromPool(baseBuildingBlueprint, i, raycastGridStart.y, j);
						}
					}
				}
				else
				{
					int bottomRowAngle2;
					int topRowAngle2;
					if (baseBuildingBlueprint.BuildingType != BuildingType.Merlon)
					{
						bottomRowAngle2 = 90;
						topRowAngle2 = 90;
					}
					else
					{
						bottomRowAngle2 = 270;
						topRowAngle2 = 90;
					}
					for (int k = raycastGridStart.x; k <= num; k++)
					{
						if (GridDataIndexTools.IsForbiddenEdge(k, z) || GridDataIndexTools.IsForbiddenEdge(k, num2))
						{
							wasForbiddenEdge = true;
						}
						SpawnFromPool(baseBuildingBlueprint, k, raycastGridStart.y, z);
						SpawnFromPool(baseBuildingBlueprint, k, raycastGridStart.y, num2);
					}
					int leftColumnAngle2;
					int rightColumnAngle2;
					if (baseBuildingBlueprint.BuildingType != BuildingType.Merlon)
					{
						leftColumnAngle2 = 0;
						rightColumnAngle2 = 0;
					}
					else
					{
						leftColumnAngle2 = 0;
						rightColumnAngle2 = 180;
					}
					for (int l = z; l <= num2; l++)
					{
						if (GridDataIndexTools.IsForbiddenEdge(x, l) || GridDataIndexTools.IsForbiddenEdge(num, l))
						{
							wasForbiddenEdge = true;
						}
						SpawnFromPool(baseBuildingBlueprint, x, raycastGridStart.y, l);
						SpawnFromPool(baseBuildingBlueprint, num, raycastGridStart.y, l);
					}
					RotateIntoPosition(x, num, z, num2, bottomRowAngle2, topRowAngle2, leftColumnAngle2, rightColumnAngle2);
				}
				UpdateInfoCursor2DGrid(Mathf.Abs(num - raycastGridStart.x) + 1, Mathf.Abs(num2 - raycastGridStart.z) + 1, buildingsDictionary.Count);
				Map.StabilityManager.UpdateStabilitiesWhileDragging(buildingsDictionary.Keys.ToList());
				return;
			}
			if (raycastGridStart.z > b.z)
			{
				int x2 = raycastGridStart.x;
				int num3 = ((Mathf.Abs(raycastGridStart.z - b.z) < maximumGridSize) ? b.z : (raycastGridStart.z - maximumGridSize));
				int z2 = raycastGridStart.z;
				UpdateDragRulers(raycastGridStart.x, num, num3, raycastGridStart.z);
				if (fillCenter)
				{
					for (int m = x2; m <= num; m++)
					{
						for (int num4 = z2; num4 >= num3; num4--)
						{
							if (GridDataIndexTools.IsForbiddenEdge(m, num4))
							{
								wasForbiddenEdge = true;
							}
							SpawnFromPool(baseBuildingBlueprint, m, raycastGridStart.y, num4);
						}
					}
				}
				else
				{
					int bottomRowAngle3;
					int topRowAngle3;
					if (baseBuildingBlueprint.BuildingType != BuildingType.Merlon)
					{
						bottomRowAngle3 = 90;
						topRowAngle3 = 90;
					}
					else
					{
						bottomRowAngle3 = 270;
						topRowAngle3 = 90;
					}
					for (int n = raycastGridStart.x; n <= num; n++)
					{
						if (GridDataIndexTools.IsForbiddenEdge(n, z2) || GridDataIndexTools.IsForbiddenEdge(n, num3))
						{
							wasForbiddenEdge = true;
						}
						SpawnFromPool(baseBuildingBlueprint, n, raycastGridStart.y, num3);
						SpawnFromPool(baseBuildingBlueprint, n, raycastGridStart.y, z2);
					}
					int leftColumnAngle3;
					int rightColumnAngle3;
					if (baseBuildingBlueprint.BuildingType != BuildingType.Merlon)
					{
						leftColumnAngle3 = 0;
						rightColumnAngle3 = 0;
					}
					else
					{
						leftColumnAngle3 = 0;
						rightColumnAngle3 = 180;
					}
					for (int num5 = z2; num5 >= num3; num5--)
					{
						if (GridDataIndexTools.IsForbiddenEdge(x2, num5) || GridDataIndexTools.IsForbiddenEdge(num, num5))
						{
							wasForbiddenEdge = true;
						}
						SpawnFromPool(baseBuildingBlueprint, x2, raycastGridStart.y, num5);
						SpawnFromPool(baseBuildingBlueprint, num, raycastGridStart.y, num5);
					}
					RotateIntoPosition(x2, num, num3, z2, bottomRowAngle3, topRowAngle3, leftColumnAngle3, rightColumnAngle3);
				}
				UpdateInfoCursor2DGrid(Mathf.Abs(num - raycastGridStart.x) + 1, Mathf.Abs(num3 - raycastGridStart.z) + 1, buildingsDictionary.Count);
				Map.StabilityManager.UpdateStabilitiesWhileDragging(buildingsDictionary.Keys.ToList());
				return;
			}
		}
		if (raycastGridStart.x <= b.x)
		{
			return;
		}
		int num6 = ((Mathf.Abs(raycastGridStart.x - b.x) < maximumGridSize) ? b.x : (raycastGridStart.x - maximumGridSize));
		if (raycastGridStart.z <= b.z)
		{
			int x3 = raycastGridStart.x;
			int z3 = raycastGridStart.z;
			int num7 = ((Mathf.Abs(raycastGridStart.z - b.z) < maximumGridSize) ? b.z : (raycastGridStart.z + maximumGridSize));
			UpdateDragRulers(num6, raycastGridStart.x, raycastGridStart.z, num7);
			if (fillCenter)
			{
				for (int num8 = x3; num8 >= num6; num8--)
				{
					for (int num9 = z3; num9 <= num7; num9++)
					{
						if (GridDataIndexTools.IsForbiddenEdge(num8, num9))
						{
							wasForbiddenEdge = true;
						}
						SpawnFromPool(baseBuildingBlueprint, num8, raycastGridStart.y, num9);
					}
				}
			}
			else
			{
				int bottomRowAngle4;
				int topRowAngle4;
				if (baseBuildingBlueprint.BuildingType != BuildingType.Merlon)
				{
					bottomRowAngle4 = 90;
					topRowAngle4 = 90;
				}
				else
				{
					bottomRowAngle4 = 270;
					topRowAngle4 = 90;
				}
				for (int num10 = x3; num10 >= num6; num10--)
				{
					if (GridDataIndexTools.IsForbiddenEdge(num10, num7) || GridDataIndexTools.IsForbiddenEdge(num10, z3))
					{
						wasForbiddenEdge = true;
					}
					SpawnFromPool(baseBuildingBlueprint, num10, raycastGridStart.y, z3);
					SpawnFromPool(baseBuildingBlueprint, num10, raycastGridStart.y, num7);
				}
				int leftColumnAngle4;
				int rightColumnAngle4;
				if (baseBuildingBlueprint.BuildingType != BuildingType.Merlon)
				{
					leftColumnAngle4 = 0;
					rightColumnAngle4 = 0;
				}
				else
				{
					leftColumnAngle4 = 0;
					rightColumnAngle4 = 180;
				}
				for (int num11 = z3; num11 <= num7; num11++)
				{
					if (GridDataIndexTools.IsForbiddenEdge(num6, num11) || GridDataIndexTools.IsForbiddenEdge(x3, num11))
					{
						wasForbiddenEdge = true;
					}
					SpawnFromPool(baseBuildingBlueprint, num6, raycastGridStart.y, num11);
					SpawnFromPool(baseBuildingBlueprint, x3, raycastGridStart.y, num11);
				}
				RotateIntoPosition(num6, x3, z3, num7, bottomRowAngle4, topRowAngle4, leftColumnAngle4, rightColumnAngle4);
			}
			UpdateInfoCursor2DGrid(Mathf.Abs(num6 - raycastGridStart.x) + 1, Mathf.Abs(num7 - raycastGridStart.z) + 1, buildingsDictionary.Count);
			Map.StabilityManager.UpdateStabilitiesWhileDragging(buildingsDictionary.Keys.ToList());
		}
		else
		{
			if (raycastGridStart.z <= b.z)
			{
				return;
			}
			int x4 = raycastGridStart.x;
			int num12 = ((Mathf.Abs(raycastGridStart.z - b.z) < maximumGridSize) ? b.z : (raycastGridStart.z - maximumGridSize));
			int z4 = raycastGridStart.z;
			UpdateDragRulers(num6, raycastGridStart.x, num12, raycastGridStart.z);
			if (fillCenter)
			{
				for (int num13 = x4; num13 >= num6; num13--)
				{
					for (int num14 = z4; num14 >= num12; num14--)
					{
						if (GridDataIndexTools.IsForbiddenEdge(num13, num14))
						{
							wasForbiddenEdge = true;
						}
						SpawnFromPool(baseBuildingBlueprint, num13, raycastGridStart.y, num14);
					}
				}
			}
			else
			{
				int bottomRowAngle5;
				int topRowAngle5;
				if (baseBuildingBlueprint.BuildingType != BuildingType.Merlon)
				{
					bottomRowAngle5 = 90;
					topRowAngle5 = 90;
				}
				else
				{
					bottomRowAngle5 = 270;
					topRowAngle5 = 90;
				}
				for (int num15 = x4; num15 >= num6; num15--)
				{
					if (GridDataIndexTools.IsForbiddenEdge(num15, z4) || GridDataIndexTools.IsForbiddenEdge(num15, num12))
					{
						wasForbiddenEdge = true;
					}
					SpawnFromPool(baseBuildingBlueprint, num15, raycastGridStart.y, num12);
					SpawnFromPool(baseBuildingBlueprint, num15, raycastGridStart.y, z4);
				}
				int leftColumnAngle5;
				int rightColumnAngle5;
				if (baseBuildingBlueprint.BuildingType != BuildingType.Merlon)
				{
					leftColumnAngle5 = 0;
					rightColumnAngle5 = 0;
				}
				else
				{
					leftColumnAngle5 = 0;
					rightColumnAngle5 = 180;
				}
				for (int num16 = z4; num16 >= num12; num16--)
				{
					if (GridDataIndexTools.IsForbiddenEdge(num6, num16) || GridDataIndexTools.IsForbiddenEdge(x4, num16))
					{
						wasForbiddenEdge = true;
					}
					SpawnFromPool(baseBuildingBlueprint, num6, raycastGridStart.y, num16);
					SpawnFromPool(baseBuildingBlueprint, x4, raycastGridStart.y, num16);
				}
				RotateIntoPosition(num6, x4, num12, z4, bottomRowAngle5, topRowAngle5, leftColumnAngle5, rightColumnAngle5);
			}
			UpdateInfoCursor2DGrid(Mathf.Abs(num6 - raycastGridStart.x) + 1, Mathf.Abs(num12 - raycastGridStart.z) + 1, buildingsDictionary.Count);
			Map.StabilityManager.UpdateStabilitiesWhileDragging(buildingsDictionary.Keys.ToList());
		}
		void RotateIntoPosition(int minX, int maxX, int minZ, int maxZ, int bottomRowAngle, int topRowAngle, int leftColumnAngle, int rightColumnAngle)
		{
			foreach (KeyValuePair<Vec3Int, BaseBuildingViewComponent> item in buildingsDictionary)
			{
				if (item.Key.z == minZ)
				{
					item.Value.transform.eulerAngles = new Vector3(0f, bottomRowAngle, 0f);
				}
				else if (item.Key.z == maxZ)
				{
					item.Value.transform.eulerAngles = new Vector3(0f, topRowAngle, 0f);
				}
				else if (item.Key.x == minX)
				{
					item.Value.transform.eulerAngles = new Vector3(0f, leftColumnAngle, 0f);
				}
				else if (item.Key.x == maxX)
				{
					item.Value.transform.eulerAngles = new Vector3(0f, rightColumnAngle, 0f);
				}
			}
		}
	}

	private void UpdateGridDragDirectionChange()
	{
		if (buildingsDictionary.Count == 0)
		{
			return;
		}
		Vec3Int[] array = buildingsDictionary.Keys.ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			if (!bounds.Contains((Vector3)array[i]) && raycastGridStart != array[i])
			{
				RemoveObject(array[i]);
			}
		}
	}

	private void RoofPlacementTick()
	{
		draggingRoof = true;
		raycastGridCurrent.y = raycastGridStart.y;
		int i;
		for (i = preview.transform.GetEulerAngleY(); i < 0; i += 360)
		{
		}
		while (i > 360)
		{
			i -= 360;
		}
		Vec3Int[] array = buildingsDictionary.Keys.ToArray();
		foreach (Vec3Int position in array)
		{
			RemoveRoof(position);
		}
		if (raycastGridStart.x <= raycastGridCurrent.x)
		{
			if (raycastGridStart.z < raycastGridCurrent.z)
			{
				RoofDragPositiveXPositiveZ(i);
				UpdateDragRulers(raycastGridStart.x, raycastGridCurrent.x, raycastGridStart.z, raycastGridCurrent.z);
			}
			if (raycastGridStart.z >= raycastGridCurrent.z)
			{
				RoofDragPositiveXNegativeZ(i);
				UpdateDragRulers(raycastGridStart.x, raycastGridCurrent.x, raycastGridStart.z + 1, raycastGridCurrent.z - 1);
			}
		}
		if (raycastGridStart.x > raycastGridCurrent.x)
		{
			if (raycastGridStart.z <= raycastGridCurrent.z)
			{
				RoofDragNegativeXPositiveZ(i);
				UpdateDragRulers(raycastGridStart.x + 1, raycastGridCurrent.x - 1, raycastGridStart.z, raycastGridCurrent.z);
			}
			if (raycastGridStart.z > raycastGridCurrent.z)
			{
				RoofDragNegativeXNegativeZ(i);
				UpdateDragRulers(raycastGridStart.x + 1, raycastGridCurrent.x - 1, raycastGridStart.z + 1, raycastGridCurrent.z - 1);
			}
		}
		array = roofPositionView.Keys.ToArray();
		foreach (Vec3Int key in array)
		{
			roofPositionView[key].UpdateRoofPlacementPreviewVisuals(Map.BuildingsManagerMain.CanPlaceRoof(roofPositionView[key], showMessage: false));
		}
	}

	private IEnumerator ShowRoofPlacementError(string messageKey)
	{
		if (canShowPlacementError)
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("messageKey"));
			canShowPlacementError = false;
		}
		yield return new WaitForSecondsRealtime(2f);
		canShowPlacementError = true;
	}

	private void UpdateDragRulers(float minX, float maxX, float minZ, float maxZ)
	{
		PlacementType placementType = baseBuildingBlueprint.PlacementType;
		if (placementType == PlacementType.SingleAxisDrag || placementType == PlacementType.TwoAxisDrag || placementType == PlacementType.Roof)
		{
			dragRulers.DragAdjust(minX, maxX, minZ, maxZ);
		}
	}

	private void DirectionChangeObjectPreviewUpdate()
	{
		userForceMerlonRotation = false;
		previousDragDirection = dragDirection;
		ClearAllObjects();
	}

	private void ClearAllObjects()
	{
		Vec3Int[] array = buildingsDictionary.Keys.ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			RemoveObject(array[i]);
		}
	}

	private void RemoveObject(Vec3Int position)
	{
		if (buildingsDictionary.TryGetValue(position, out var value))
		{
			MonoSingleton<BuildingsPool>.Instance.Return(value, baseBuildingBlueprint);
			buildingsDictionary.Remove(position);
			roofPositionView.Remove(position);
		}
	}

	private void RemoveRoof(Vec3Int position)
	{
		if (roofPositionView.TryGetValue(position, out var value))
		{
			BaseBuildingViewComponent component = value.GetComponent<BaseBuildingViewComponent>();
			if (component != null)
			{
				MonoSingleton<BuildingsPool>.Instance.Return(component, baseBuildingBlueprint);
			}
		}
		roofPositionView.Remove(position);
		buildingsDictionary.Remove(position);
	}

	private BaseBuildingViewComponent SpawnFromPool(BaseBuildingBlueprint blueprint, int x, int y, int z)
	{
		Vec3Int gridPosition = new Vec3Int(x, y, z);
		BaseBuildingViewComponent baseBuildingViewComponent = SpawnFromPool(blueprint, gridPosition, previewAngle);
		if (baseBuildingViewComponent != null)
		{
			baseBuildingViewComponent.UpdateMeshVariation(blueprint.GetMeshVariation("default"));
		}
		return baseBuildingViewComponent;
	}

	private BaseBuildingViewComponent SpawnFromPool(BaseBuildingBlueprint blueprint, Vec3Int gridPosition, int angleY, FactionOwnership factionOwnership = FactionOwnership.Player)
	{
		if (buildingsDictionary.ContainsKey(gridPosition))
		{
			return null;
		}
		if (GridDataIndexTools.IsTopLayer(gridPosition.y))
		{
			showCantDigTopLayer = true;
			return null;
		}
		if (blueprint.BuildingType != BuildingType.Roof)
		{
			if (factionOwnership == FactionOwnership.Player)
			{
				if (!Map.BuildingsManagerMain.CanPlace(blueprint, gridPosition, angleY))
				{
					return null;
				}
			}
			else if (!Map.BuildingsManagerMain.CanPlaceEnemyBuilding(blueprint, gridPosition, angleY))
			{
				return null;
			}
		}
		BaseBuildingViewComponent baseBuildingViewComponent = SpawnBaseBuildingViewComponent(blueprint, gridPosition, angleY);
		buildingsDictionary.TryAdd(gridPosition, baseBuildingViewComponent);
		return baseBuildingViewComponent;
	}

	public BaseBuildingViewComponent SpawnBaseBuildingViewComponent(BaseBuildingBlueprint blueprint, Vec3Int gridPosition, int angleY)
	{
		BaseBuildingViewComponent baseBuildingViewComponent = MonoSingleton<BuildingsPool>.Instance.Take(blueprint.GetID());
		TryBackupShadowCastingMode(blueprint);
		pooledObjectLayer = baseBuildingViewComponent.gameObject.layer;
		baseBuildingViewComponent.transform.eulerAngles = new Vector3(0f, angleY, 0f);
		Vector3 worldPosition = GridUtils.GetWorldPosition(gridPosition);
		baseBuildingViewComponent.transform.position = worldPosition;
		BaseComponent[] components = baseBuildingViewComponent.GetComponents<BaseComponent>();
		for (int i = 0; i < components.Length; i++)
		{
			components[i].PreSpawnInitialization();
		}
		ComponentBaseView[] components2 = baseBuildingViewComponent.GetComponents<ComponentBaseView>();
		for (int i = 0; i < components2.Length; i++)
		{
			components2[i].PreSpawnInitialization();
		}
		return baseBuildingViewComponent;
	}

	private void TryBackupShadowCastingMode(BaseBuildingBlueprint blueprint)
	{
		string prefabID = blueprint.PrefabID;
		if (shadowCastingModes.TryGetValue(prefabID, out var value))
		{
			return;
		}
		BaseBuildingViewComponent component = MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByAddress(prefabID).GetComponent<BaseBuildingViewComponent>();
		value = new Dictionary<string, ShadowCastingMode>();
		foreach (MeshFusionSource meshFusionSource in component.MeshFusionSources)
		{
			Renderer component2 = meshFusionSource.GetComponent<Renderer>();
			if (component2 != null)
			{
				ShadowCastingMode shadowCastingMode = component.GetShadowCastingMode(component2, blueprint);
				value.Add(meshFusionSource.name, shadowCastingMode);
			}
		}
		shadowCastingModes.Add(prefabID, value);
	}

	private void OnRotateLeft()
	{
		RotateKeyPress(-90);
	}

	private void OnRotateRight()
	{
		RotateKeyPress(90);
	}

	private void RotateKeyPress(int angle)
	{
		if (preview == null)
		{
			return;
		}
		if (baseBuildingBlueprint.BuildingType == BuildingType.Merlon)
		{
			if (dragDirection != 0)
			{
				if (buildingsDictionary.Count > 1)
				{
					userForceMerlonRotation = true;
				}
			}
			else
			{
				userForceMerlonRotation = true;
			}
		}
		BuildingType buildingType = baseBuildingBlueprint.BuildingType;
		if (buildingType == BuildingType.Door || buildingType == BuildingType.BarnDoor || buildingType == BuildingType.FenceGate || buildingType == BuildingType.Window)
		{
			Vec3Int vec3Int = raycastGridCurrent.Left();
			Vec3Int vec3Int2 = raycastGridCurrent.Right();
			Vec3Int vec3Int3 = raycastGridCurrent.Front();
			Vec3Int vec3Int4 = raycastGridCurrent.Back();
			bool flag = Map.BuildingsManagerMain.GetAlignmentBuilding(vec3Int) != null;
			bool flag2 = MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int);
			bool flag3 = Map.BuildingsManagerMain.GetAlignmentBuilding(vec3Int2) != null;
			bool flag4 = MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int2);
			bool num = Map.BuildingsManagerMain.GetAlignmentBuilding(vec3Int3) != null;
			bool flag5 = MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int3);
			bool flag6 = Map.BuildingsManagerMain.GetAlignmentBuilding(vec3Int4) != null;
			bool flag7 = MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int4);
			if ((flag || flag2) && (flag3 || flag4))
			{
				angle *= 2;
			}
			if ((num || flag5) && (flag6 || flag7))
			{
				angle *= 2;
			}
		}
		Vector3 eulerAngles = preview.transform.eulerAngles;
		float y = eulerAngles.y + (float)angle;
		Vector3 angle2 = new Vector3(eulerAngles.x, y, eulerAngles.z);
		SetAngle(angle2);
	}

	private void SetMerlonPreviewAngle(float newYAngle)
	{
		if (!(preview == null))
		{
			Vector3 eulerAngles = preview.transform.eulerAngles;
			Vector3 eulerAngles2 = new Vector3(eulerAngles.x, newYAngle, eulerAngles.z);
			preview.transform.eulerAngles = eulerAngles2;
			previewAngle = preview.transform.GetEulerAngleY();
		}
	}

	private void SetAngle(Vector3 newAngle)
	{
		preview.transform.eulerAngles = newAngle;
		previewAngle = preview.transform.GetEulerAngleY();
		UpdateWellVisuals(isRotating: true);
		if (buildingsDictionary.Count == 0)
		{
			return;
		}
		if (baseBuildingBlueprint.BuildingType == BuildingType.Roof)
		{
			ClearAllObjects();
			RoofPlacementTick();
		}
		else if (baseBuildingBlueprint.PlacementType == PlacementType.SingleAxisDrag)
		{
			ClearAllObjects();
			if (Input.GetKey(KeyCode.LeftAlt))
			{
				TwoAxisDragPlacementTick(fillCenter: false);
			}
			else
			{
				SingleAxisDragPlacementTick(forceExecute: true);
			}
		}
		else if (baseBuildingBlueprint.PlacementType == PlacementType.TwoAxisDrag)
		{
			ClearAllObjects();
			if (Input.GetKey(KeyCode.LeftAlt))
			{
				TwoAxisDragPlacementTick(fillCenter: false);
			}
			else
			{
				TwoAxisDragPlacementTick();
			}
		}
	}

	private void UpdateFenceAngle()
	{
		if (baseBuildingBlueprint == null || preview == null || baseBuildingBlueprint.BuildingType != BuildingType.Fence)
		{
			return;
		}
		switch (dragDirection)
		{
		case DragDirection.PositiveX:
		case DragDirection.NegativeX:
			if (!Mathf.Approximately(preview.transform.eulerAngles.y, 90f))
			{
				SetAngle(new Vector3(0f, 90f, 0f));
			}
			break;
		case DragDirection.PositiveZ:
		case DragDirection.NegativeZ:
			if (!Mathf.Approximately(preview.transform.eulerAngles.y, 0f))
			{
				SetAngle(new Vector3(0f, 0f, 0f));
			}
			break;
		}
	}

	private void UpdateMerlonPreviewAngle()
	{
		if (baseBuildingBlueprint == null || baseBuildingBlueprint.BuildingType != BuildingType.Merlon || dragDirection != 0 || userForceMerlonRotation)
		{
			return;
		}
		ref Vec3Int reference = ref raycastGridCurrent;
		Vec3Int b = Vec3Int.right;
		if (TryAlignMerlons(reference + b, horizontalDrag: true))
		{
			return;
		}
		ref Vec3Int reference2 = ref raycastGridCurrent;
		Vec3Int b2 = Vec3Int.left;
		if (!TryAlignMerlons(reference2 + b2, horizontalDrag: true))
		{
			ref Vec3Int reference3 = ref raycastGridCurrent;
			b = Vec3Int.forward;
			if (!TryAlignMerlons(reference3 + b, horizontalDrag: false))
			{
				ref Vec3Int reference4 = ref raycastGridCurrent;
				b2 = Vec3Int.back;
				TryAlignMerlons(reference4 + b2, horizontalDrag: false);
			}
		}
	}

	private void UpdateMerlonAngle()
	{
		if (baseBuildingBlueprint == null || preview == null || baseBuildingBlueprint.BuildingType != BuildingType.Merlon || userForceMerlonRotation)
		{
			return;
		}
		switch (dragDirection)
		{
		case DragDirection.PositiveX:
		case DragDirection.NegativeX:
		{
			int num4 = 0;
			int num5 = 0;
			GroundManager groundManager2 = MonoSingleton<GroundManager>.Instance;
			foreach (Vec3Int key in buildingsDictionary.Keys)
			{
				Vec3Int a3 = key;
				Vec3Int b = Vec3Int.forward;
				Vec3Int a2 = a3 + b;
				Vec3Int b2 = Vec3Int.down;
				Vec3Int vec3Int3 = a2 + b2;
				if (!map.BuildingsManagerMain.WallTypeBuildingExists(vec3Int3) && !groundManager2.GroundExists(vec3Int3))
				{
					num4++;
				}
				b = Vec3Int.back;
				a2 = a3 + b;
				b2 = Vec3Int.down;
				Vec3Int vec3Int4 = a2 + b2;
				if (!map.BuildingsManagerMain.WallTypeBuildingExists(vec3Int4) && !groundManager2.GroundExists(vec3Int4))
				{
					num5++;
				}
			}
			if (num4 > num5)
			{
				if (!Mathf.Approximately(preview.transform.eulerAngles.y, 90f))
				{
					SetAngle(new Vector3(0f, 90f, 0f));
				}
				break;
			}
			if (num4 < num5)
			{
				if (!Mathf.Approximately(preview.transform.eulerAngles.y, 270f))
				{
					SetAngle(new Vector3(0f, 270f, 0f));
				}
				break;
			}
			bool flag2 = false;
			if (dragDirection == DragDirection.PositiveX)
			{
				for (int j = raycastGridStart.x - 1; j <= raycastGridCurrent.x + 1; j++)
				{
					Vec3Int pos3 = new Vec3Int(j, raycastGridStart.y, raycastGridStart.z);
					if (TryAlignMerlons(pos3, horizontalDrag: true))
					{
						flag2 = true;
						break;
					}
				}
			}
			else
			{
				for (int num6 = raycastGridStart.x + 1; num6 >= raycastGridCurrent.x - 1; num6--)
				{
					Vec3Int pos4 = new Vec3Int(num6, raycastGridStart.y, raycastGridStart.z);
					if (TryAlignMerlons(pos4, horizontalDrag: true))
					{
						flag2 = true;
						break;
					}
				}
			}
			if (!flag2 && (Mathf.Approximately(preview.transform.eulerAngles.y, 0f) || Mathf.Approximately(preview.transform.eulerAngles.y, 180f)))
			{
				SetAngle(new Vector3(0f, 90f, 0f));
			}
			break;
		}
		case DragDirection.PositiveZ:
		case DragDirection.NegativeZ:
		{
			int num = 0;
			int num2 = 0;
			GroundManager groundManager = MonoSingleton<GroundManager>.Instance;
			foreach (Vec3Int key2 in buildingsDictionary.Keys)
			{
				Vec3Int a = key2;
				Vec3Int b = Vec3Int.left;
				Vec3Int a2 = a + b;
				Vec3Int b2 = Vec3Int.down;
				Vec3Int vec3Int = a2 + b2;
				if (!map.BuildingsManagerMain.WallTypeBuildingExists(vec3Int) && !groundManager.GroundExists(vec3Int))
				{
					num++;
				}
				b = Vec3Int.right;
				a2 = a + b;
				b2 = Vec3Int.down;
				Vec3Int vec3Int2 = a2 + b2;
				if (!map.BuildingsManagerMain.WallTypeBuildingExists(vec3Int2) && !groundManager.GroundExists(vec3Int2))
				{
					num2++;
				}
			}
			if (num > num2)
			{
				if (!Mathf.Approximately(preview.transform.eulerAngles.y, 0f))
				{
					SetAngle(new Vector3(0f, 0f, 0f));
				}
				break;
			}
			if (num < num2)
			{
				if (!Mathf.Approximately(preview.transform.eulerAngles.y, 180f))
				{
					SetAngle(new Vector3(0f, 180f, 0f));
				}
				break;
			}
			bool flag = false;
			if (dragDirection == DragDirection.PositiveZ)
			{
				for (int i = raycastGridStart.z - 1; i <= raycastGridCurrent.z + 1; i++)
				{
					Vec3Int pos = new Vec3Int(raycastGridStart.x, raycastGridStart.y, i);
					if (TryAlignMerlons(pos, horizontalDrag: false))
					{
						flag = true;
						break;
					}
				}
			}
			else
			{
				for (int num3 = raycastGridStart.z + 1; num3 >= raycastGridCurrent.z - 1; num3--)
				{
					Vec3Int pos2 = new Vec3Int(raycastGridStart.x, raycastGridStart.y, num3);
					if (TryAlignMerlons(pos2, horizontalDrag: false))
					{
						flag = true;
						break;
					}
				}
			}
			if (!flag && (Mathf.Approximately(preview.transform.eulerAngles.y, 90f) || Mathf.Approximately(preview.transform.eulerAngles.y, 270f)))
			{
				SetAngle(new Vector3(0f, 0f, 0f));
			}
			break;
		}
		}
	}

	private bool TryAlignMerlons(Vec3Int pos, bool horizontalDrag)
	{
		BaseBuildingInstance building = map.BuildingsManagerMain.GetBuilding(pos, (BaseBuildingInstance item) => item.Blueprint.BuildingType == BuildingType.Merlon);
		if (building == null)
		{
			return false;
		}
		int alignmentAngle = map.MerlonRotationManager.GetAlignmentAngle(building, horizontalDrag);
		if (alignmentAngle != -1)
		{
			if (previewAngle != alignmentAngle)
			{
				SetAngle(new Vector3(0f, alignmentAngle, 0f));
			}
			return true;
		}
		return false;
	}

	private void DragSpawnRoof(Vec3Int roofPosition)
	{
		if (roofPositionView.ContainsKey(roofPosition))
		{
			return;
		}
		BaseBuildingViewComponent baseBuildingViewComponent = SpawnFromPool(baseBuildingBlueprint, roofPosition, previewAngle);
		if (baseBuildingViewComponent != null)
		{
			RoofViewComponent component = baseBuildingViewComponent.GetComponent<RoofViewComponent>();
			if (component == null)
			{
				Log.Error("Can't find RoofViewComponent", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingPlacementManager.cs");
				return;
			}
			roofPositionView.TryAdd(roofPosition, component);
			buildingsDictionary.TryAdd(roofPosition, baseBuildingViewComponent);
		}
	}

	private void RoofDragPositiveXPositiveZ(int angle)
	{
		switch (angle)
		{
		case 0:
		case 180:
		{
			int num3 = ((Mathf.Abs(raycastGridStart.z - raycastGridCurrent.z) < maximumGridSize) ? raycastGridCurrent.z : (raycastGridStart.z + maximumGridSize));
			int num4 = Mathf.Clamp(Mathf.Abs(raycastGridStart.x - raycastGridCurrent.x) + 1, 0, roofComponentBlueprint.MaxLength);
			int height2 = roofComponentBlueprint.GetHeight(num4);
			Vec3Int scale2 = new Vec3Int(num4, height2, 1);
			for (int k = raycastGridStart.z; k <= num3; k++)
			{
				Vec3Int roofPosition = new Vec3Int(raycastGridStart.x, raycastGridStart.y, k);
				DragSpawnRoof(roofPosition);
			}
			int max = Mathf.Abs(raycastGridStart.x + roofComponentBlueprint.MaxLength - 1);
			foreach (Vec3Int key in roofPositionView.Keys)
			{
				roofPositionView[key].Scale(scale2);
				roofPositionView[key].ClearPositions();
				for (int l = raycastGridStart.x; l < raycastGridStart.x + num4; l++)
				{
					roofPositionView[key].AddPosition(new Vec3Int(l, raycastGridStart.y, (int)roofPositionView[key].transform.position.z));
				}
				if (angle == 180)
				{
					roofPositionView[key].transform.eulerAngles = new Vector3(0f, angle, 0f);
					roofPositionView[key].transform.position = new Vector3(Mathf.Clamp(raycastGridCurrent.x, raycastGridStart.x, max), roofPositionView[key].transform.position.y, roofPositionView[key].transform.position.z);
				}
			}
			UpdateInfoCursor2DGrid(num4, roofPositionView.Count, roofPositionView.Count);
			break;
		}
		case 90:
		case 270:
		{
			int num = ((Mathf.Abs(raycastGridStart.x - raycastGridCurrent.x) < maximumGridSize) ? raycastGridCurrent.x : (raycastGridStart.x + maximumGridSize));
			int num2 = Mathf.Clamp(Mathf.Abs(raycastGridStart.z - raycastGridCurrent.z) + 1, 0, roofComponentBlueprint.MaxLength);
			int height = roofComponentBlueprint.GetHeight(num2);
			Vec3Int scale = new Vec3Int(num2, height, 1);
			for (int i = raycastGridStart.x; i <= num; i++)
			{
				Vec3Int vec3Int = new Vec3Int(i, raycastGridStart.y, raycastGridStart.z);
				if (!roofPositionView.ContainsKey(vec3Int))
				{
					DragSpawnRoof(vec3Int);
				}
			}
			foreach (Vec3Int key2 in roofPositionView.Keys)
			{
				roofPositionView[key2].Scale(scale);
				roofPositionView[key2].transform.eulerAngles = new Vector3(0f, angle, 0f);
				if (angle == 90)
				{
					roofPositionView[key2].transform.position = new Vector3(roofPositionView[key2].transform.position.x, roofPositionView[key2].transform.position.y, Mathf.Clamp(raycastGridCurrent.z, raycastGridStart.z, Mathf.Abs(raycastGridStart.z + roofComponentBlueprint.MaxLength) - 1));
				}
				roofPositionView[key2].ClearPositions();
				for (int j = raycastGridStart.z; j < raycastGridStart.z + num2; j++)
				{
					roofPositionView[key2].AddPosition(new Vec3Int((int)roofPositionView[key2].transform.position.x, raycastGridStart.y, j));
				}
			}
			UpdateInfoCursor2DGrid(num2, roofPositionView.Count, roofPositionView.Count);
			break;
		}
		}
	}

	private void RoofDragPositiveXNegativeZ(int angle)
	{
		switch (angle)
		{
		case 0:
		case 180:
		{
			int num4 = ((Mathf.Abs(raycastGridStart.z - raycastGridCurrent.z) < maximumGridSize) ? raycastGridCurrent.z : (raycastGridStart.z - maximumGridSize));
			int num5 = Mathf.Clamp(Mathf.Abs(raycastGridStart.x - raycastGridCurrent.x) + 1, 0, roofComponentBlueprint.MaxLength);
			int height2 = roofComponentBlueprint.GetHeight(num5);
			Vec3Int scale2 = new Vec3Int(num5, height2, 1);
			for (int num6 = raycastGridStart.z; num6 >= num4; num6--)
			{
				Vec3Int vec3Int2 = new Vec3Int(raycastGridStart.x, raycastGridStart.y, num6);
				if (!roofPositionView.ContainsKey(vec3Int2))
				{
					DragSpawnRoof(vec3Int2);
				}
			}
			int max = Mathf.Abs(raycastGridStart.x + roofComponentBlueprint.MaxLength - 1);
			foreach (Vec3Int key in roofPositionView.Keys)
			{
				roofPositionView[key].Scale(scale2);
				if (angle == 180)
				{
					roofPositionView[key].transform.eulerAngles = new Vector3(0f, angle, 0f);
					roofPositionView[key].transform.position = new Vector3(Mathf.Clamp(raycastGridCurrent.x, raycastGridStart.x, max), roofPositionView[key].transform.position.y, roofPositionView[key].transform.position.z);
				}
				roofPositionView[key].ClearPositions();
				for (int j = raycastGridStart.x; j < raycastGridStart.x + num5; j++)
				{
					roofPositionView[key].AddPosition(new Vec3Int(j, raycastGridStart.y, (int)roofPositionView[key].transform.position.z));
				}
			}
			UpdateInfoCursor2DGrid(num5, roofPositionView.Count, roofPositionView.Count);
			break;
		}
		case 90:
		case 270:
		{
			int num = ((Mathf.Abs(raycastGridStart.x - raycastGridCurrent.x) < maximumGridSize) ? raycastGridCurrent.x : (raycastGridStart.x + maximumGridSize));
			int num2 = Mathf.Clamp(Mathf.Abs(raycastGridStart.z - raycastGridCurrent.z) + 1, 0, roofComponentBlueprint.MaxLength);
			int height = roofComponentBlueprint.GetHeight(num2);
			Vec3Int scale = new Vec3Int(num2, height, 1);
			for (int i = raycastGridStart.x; i <= num; i++)
			{
				Vec3Int vec3Int = new Vec3Int(i, raycastGridStart.y, raycastGridStart.z);
				if (!roofPositionView.ContainsKey(vec3Int))
				{
					DragSpawnRoof(vec3Int);
				}
			}
			foreach (Vec3Int key2 in roofPositionView.Keys)
			{
				roofPositionView[key2].Scale(scale);
				roofPositionView[key2].transform.eulerAngles = new Vector3(0f, angle, 0f);
				if (angle == 270)
				{
					roofPositionView[key2].transform.position = new Vector3(roofPositionView[key2].transform.position.x, roofPositionView[key2].transform.position.y, Mathf.Clamp(raycastGridStart.z - num2, 0, raycastGridStart.z) + 1);
				}
				roofPositionView[key2].ClearPositions();
				for (int num3 = raycastGridStart.z; num3 > raycastGridStart.z - num2; num3--)
				{
					roofPositionView[key2].AddPosition(new Vec3Int((int)roofPositionView[key2].transform.position.x, raycastGridStart.y, num3));
				}
			}
			UpdateInfoCursor2DGrid(num2, roofPositionView.Count, roofPositionView.Count);
			break;
		}
		}
	}

	private void RoofDragNegativeXPositiveZ(int angle)
	{
		switch (angle)
		{
		case 0:
		case 180:
		{
			int num4 = ((Mathf.Abs(raycastGridStart.z - raycastGridCurrent.z) < maximumGridSize) ? raycastGridCurrent.z : (raycastGridStart.z + maximumGridSize));
			int num5 = Mathf.Clamp(Mathf.Abs(raycastGridStart.x - raycastGridCurrent.x) + 1, 0, roofComponentBlueprint.MaxLength);
			int height2 = roofComponentBlueprint.GetHeight(num5);
			Vec3Int scale2 = new Vec3Int(num5, height2, 1);
			for (int j = raycastGridStart.z; j <= num4; j++)
			{
				Vec3Int vec3Int2 = new Vec3Int(raycastGridStart.x, raycastGridStart.y, j);
				if (!roofPositionView.ContainsKey(vec3Int2))
				{
					DragSpawnRoof(vec3Int2);
				}
			}
			int min = raycastGridStart.x - roofComponentBlueprint.MaxLength + 1;
			foreach (Vec3Int key in roofPositionView.Keys)
			{
				roofPositionView[key].Scale(scale2);
				roofPositionView[key].transform.eulerAngles = new Vector3(0f, angle, 0f);
				if (angle == 0)
				{
					roofPositionView[key].transform.position = new Vector3(Mathf.Clamp(raycastGridCurrent.x, min, raycastGridStart.x), roofPositionView[key].transform.position.y, roofPositionView[key].transform.position.z);
				}
				roofPositionView[key].ClearPositions();
				for (int num6 = raycastGridStart.x; num6 > raycastGridStart.x - num5; num6--)
				{
					roofPositionView[key].AddPosition(new Vec3Int(num6, raycastGridStart.y, (int)roofPositionView[key].transform.position.z));
				}
			}
			UpdateInfoCursor2DGrid(num5, roofPositionView.Count, roofPositionView.Count);
			break;
		}
		case 90:
		case 270:
		{
			int num = ((Mathf.Abs(raycastGridStart.x - raycastGridCurrent.x) < maximumGridSize) ? raycastGridCurrent.x : (raycastGridStart.x - maximumGridSize));
			int num2 = Mathf.Clamp(Mathf.Abs(raycastGridStart.z - raycastGridCurrent.z) + 1, 0, roofComponentBlueprint.MaxLength);
			int height = roofComponentBlueprint.GetHeight(num2);
			Vec3Int scale = new Vec3Int(num2, height, 1);
			for (int num3 = raycastGridStart.x; num3 >= num; num3--)
			{
				Vec3Int vec3Int = new Vec3Int(num3, raycastGridStart.y, raycastGridStart.z);
				if (!roofPositionView.ContainsKey(vec3Int))
				{
					DragSpawnRoof(vec3Int);
				}
			}
			foreach (Vec3Int key2 in roofPositionView.Keys)
			{
				roofPositionView[key2].Scale(scale);
				roofPositionView[key2].transform.eulerAngles = new Vector3(0f, angle, 0f);
				if (angle == 90)
				{
					roofPositionView[key2].transform.position = new Vector3(roofPositionView[key2].transform.position.x, roofPositionView[key2].transform.position.y, Mathf.Clamp(raycastGridCurrent.z, raycastGridStart.z, Mathf.Abs(raycastGridStart.z + roofComponentBlueprint.MaxLength) - 1));
				}
				roofPositionView[key2].ClearPositions();
				for (int i = raycastGridStart.z; i < raycastGridStart.z + num2; i++)
				{
					roofPositionView[key2].AddPosition(new Vec3Int((int)roofPositionView[key2].transform.position.x, raycastGridStart.y, i));
				}
			}
			UpdateInfoCursor2DGrid(num2, roofPositionView.Count, roofPositionView.Count);
			break;
		}
		}
	}

	private void RoofDragNegativeXNegativeZ(int angle)
	{
		switch (angle)
		{
		case 0:
		case 180:
		{
			int num5 = ((Mathf.Abs(raycastGridStart.z - raycastGridCurrent.z) < maximumGridSize) ? raycastGridCurrent.z : (raycastGridStart.z - maximumGridSize));
			int num6 = Mathf.Clamp(Mathf.Abs(raycastGridStart.x - raycastGridCurrent.x) + 1, 0, roofComponentBlueprint.MaxLength);
			int height2 = roofComponentBlueprint.GetHeight(num6);
			Vec3Int scale2 = new Vec3Int(num6, height2, 1);
			for (int num7 = raycastGridStart.z; num7 >= num5; num7--)
			{
				Vec3Int vec3Int2 = new Vec3Int(raycastGridStart.x, raycastGridStart.y, num7);
				if (!roofPositionView.ContainsKey(vec3Int2))
				{
					DragSpawnRoof(vec3Int2);
				}
			}
			int min = Mathf.Abs(raycastGridStart.x - roofComponentBlueprint.MaxLength) + 1;
			foreach (Vec3Int key in roofPositionView.Keys)
			{
				roofPositionView[key].Scale(scale2);
				roofPositionView[key].transform.eulerAngles = new Vector3(0f, angle, 0f);
				if (angle == 0)
				{
					roofPositionView[key].transform.position = new Vector3(Mathf.Clamp(raycastGridCurrent.x, min, raycastGridStart.x), roofPositionView[key].transform.position.y, roofPositionView[key].transform.position.z);
				}
				roofPositionView[key].ClearPositions();
				for (int num8 = raycastGridStart.x; num8 > raycastGridStart.x - num6; num8--)
				{
					roofPositionView[key].AddPosition(new Vec3Int(num8, raycastGridStart.y, (int)roofPositionView[key].transform.position.z));
				}
			}
			UpdateInfoCursor2DGrid(num6, roofPositionView.Count, roofPositionView.Count);
			break;
		}
		case 90:
		case 270:
		{
			int num = ((Mathf.Abs(raycastGridStart.x - raycastGridCurrent.x) < maximumGridSize) ? raycastGridCurrent.x : (raycastGridStart.x - maximumGridSize));
			int num2 = Mathf.Clamp(Mathf.Abs(raycastGridStart.z - raycastGridCurrent.z) + 1, 0, roofComponentBlueprint.MaxLength);
			int height = roofComponentBlueprint.GetHeight(num2);
			Vec3Int scale = new Vec3Int(num2, height, 1);
			for (int num3 = raycastGridStart.x; num3 >= num; num3--)
			{
				Vec3Int vec3Int = new Vec3Int(num3, raycastGridStart.y, raycastGridStart.z);
				if (!roofPositionView.ContainsKey(vec3Int))
				{
					DragSpawnRoof(vec3Int);
				}
			}
			foreach (Vec3Int key2 in roofPositionView.Keys)
			{
				roofPositionView[key2].Scale(scale);
				roofPositionView[key2].transform.eulerAngles = new Vector3(0f, angle, 0f);
				if (angle == 270)
				{
					roofPositionView[key2].transform.position = new Vector3(roofPositionView[key2].transform.position.x, roofPositionView[key2].transform.position.y, Mathf.Clamp(raycastGridCurrent.z, raycastGridStart.z - num2 + 1, raycastGridStart.z));
				}
				roofPositionView[key2].ClearPositions();
				for (int num4 = raycastGridStart.z; num4 > raycastGridStart.z - num2; num4--)
				{
					roofPositionView[key2].AddPosition(new Vec3Int((int)roofPositionView[key2].transform.position.x, raycastGridStart.y, num4));
				}
			}
			UpdateInfoCursor2DGrid(num2, roofPositionView.Count, roofPositionView.Count);
			break;
		}
		}
	}

	private void UpdateWellVisuals(bool isRotating = false)
	{
		if (!(baseBuildingBlueprint == null) && baseBuildingBlueprint.BuildingType == BuildingType.Well && (!(raycastGridCurrent == raycastGridPrevious) || isRotating) && !(wellPreviewView == null))
		{
			List<Vec3Int> positions = Singleton<GridTools>.Instance.GetPositions(raycastGridCurrent, baseBuildingBlueprint.Size, previewAngle, usePool: true);
			bool valueOrDefault = (map?.WellComponentManager?.CheckBlueprintWaterSource(positions[1])).GetValueOrDefault();
			wellPreviewView.UpdateWaterIndicator(valueOrDefault);
			ListPool<Vec3Int>.Return(positions);
		}
	}

	private void UpdateDragGridRulers()
	{
		PlacementType placementType = baseBuildingBlueprint.PlacementType;
		if (placementType == PlacementType.SinglePlacement || placementType == PlacementType.WallSocket)
		{
			return;
		}
		if (singleAxisDrag || twoAxisDrag || draggingRoof)
		{
			dragRulers.EnableDragGrid();
			return;
		}
		dragRulers.transform.position = preview.transform.position;
		if (hitSide.Equals(ObjectSide.Top))
		{
			dragRulers.EnableTopGrid();
		}
		else if (!hitSide.Equals(ObjectSide.Top) && !hitSide.Equals(ObjectSide.Bottom))
		{
			dragRulers.EnableSideGrid(hitSide);
		}
	}

	private void TryPlaceSocketable(Vec3Int socketableItemPos, Vec3Int socketOwnerPos, ObjectSide socket, int angle)
	{
		if (IsInForbiddenZone(baseBuildingBlueprint, socketableItemPos, angle))
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("cannot_build_on_edge"));
			return;
		}
		bool flag = false;
		bool flag2 = false;
		if (!validSockets.HasFlag(socket))
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("error_socketable"));
			return;
		}
		SocketComponentInstance freeSocket = GetFreeSocket(socketOwnerPos);
		if (freeSocket != null)
		{
			flag = true;
		}
		if (MonoSingleton<GroundManager>.Instance.CanPlaceOnSocket(socket, socketOwnerPos))
		{
			flag2 = true;
		}
		if (!flag && !flag2)
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("error_socketable"));
			return;
		}
		StairsComponentInstance componentInstance = Map.StairsComponentManager.GetComponentInstance(socketableItemPos);
		if (componentInstance != null)
		{
			string localizedName = BuildingUtils.GetLocalizedName(componentInstance.BaseBuildingBlueprint);
			string text = MonoSingleton<LocalizationController>.Instance.GetText("error_socketable_blocking");
			text = text.Replace("{0}", localizedName);
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(text);
			return;
		}
		BaseBuildingInstance building = Map.BuildingsManagerMain.GetBuilding(socketableItemPos, (BaseBuildingInstance x) => x.Blueprint.PlacementType == PlacementType.WallSocket && x.Blueprint.BuildingType != BuildingType.Beam);
		if (building != null)
		{
			string localizedName2 = BuildingUtils.GetLocalizedName(building.BlueprintId);
			string text2 = MonoSingleton<LocalizationController>.Instance.GetText("error_socketable_blocking");
			text2 = text2.Replace("{0}", localizedName2);
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(text2);
			return;
		}
		BaseBuildingViewComponent baseBuildingViewComponent = SpawnFromPool(baseBuildingBlueprint, socketableItemPos, previewAngle);
		if (baseBuildingViewComponent == null)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(29, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingPlacementManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Couldn't spawn ");
				messageBuilder.AppendFormatted(baseBuildingBlueprint.GetID());
				messageBuilder.AppendLiteral(" at position ");
				messageBuilder.AppendFormatted(socketableItemPos);
				messageBuilder.AppendLiteral("!");
			}
			Log.Warning(messageBuilder);
		}
		else
		{
			BaseBuildingInstance item = Map.BuildingsManagerMain.CreateAndReturnBuildingInstance(baseBuildingBlueprint, baseBuildingViewComponent, GridUtils.GetWorldPosition(socketableItemPos), angle);
			ObjectPlacedOnMap(baseBuildingViewComponent);
			if (flag)
			{
				freeSocket?.AttachToSocket(item, socket);
			}
			if (flag2)
			{
				MonoSingleton<GroundManager>.Instance.AttachToVoxelSocket(item, socket, socketOwnerPos);
			}
		}
	}

	private SocketComponentInstance GetFreeSocket(Vec3Int pos)
	{
		SocketComponentInstance socketComponentInstance = Map.SocketComponentManager.GetSocketComponentInstance(pos);
		if (socketComponentInstance == null)
		{
			return null;
		}
		buildingInstance = socketComponentInstance.OwnerBuilding;
		if (!socketComponentInstance.SocketOccupied(socket))
		{
			return socketComponentInstance;
		}
		return null;
	}

	private bool IsInForbiddenZone(BaseBuildingBlueprint model, Vec3Int position, int angle)
	{
		if (model.Size.Equals(Vec3Int.one))
		{
			return GridDataIndexTools.IsForbiddenEdge(position.x, position.z);
		}
		using PooledList<Vec3Int> pooledList = Singleton<GridTools>.Instance.GetPositionsJanitor(position, model.Size, angle);
		return IsInForbiddenZone(pooledList);
	}

	private bool IsInForbiddenZone(IEnumerable<Vec3Int> positions)
	{
		foreach (Vec3Int position in positions)
		{
			if (GridDataIndexTools.IsForbiddenEdge(position.x, position.z))
			{
				return true;
			}
		}
		return false;
	}

	private bool CheckIfStartPositionOccupied(Vec3Int startPosition, ObjectSide objectSide)
	{
		return Map.SocketComponentManager.GetSocketComponentInstance(startWall)?.SocketOccupied(objectSide) ?? MonoSingleton<GroundManager>.Instance.VoxelSocketOccupied(startPosition, objectSide);
	}

	private void OnSaveStart()
	{
		OnRightMouseUp();
	}

	public bool BeamExists(Vec3Int gridPosition, bool onlyFinished = false)
	{
		return Map.BuildingsManagerMain.BuildingExists(gridPosition, BuildingType.Beam, onlyFinished);
	}

	private bool CanBeamExist(Vec3Int gridPosition)
	{
		if (Map.BuildingsManagerMain.GetFirstBuilding(BuildingType.Beam, gridPosition) != null)
		{
			beamIntersection = true;
			return false;
		}
		if (Map.BuildingsManagerMain.IsBlueprintInAreaForbiddenByBuildings(baseBuildingBlueprint, gridPosition))
		{
			BaseBuildingInstance forbiddenAreaOwner = Map.BuildingsManagerMain.GetForbiddenAreaOwner(gridPosition);
			BuildingsManagerMain buildingsManagerMain = Map.BuildingsManagerMain;
			Vec3Int b = Vec3Int.up;
			if (forbiddenAreaOwner == buildingsManagerMain.GetForbiddenAreaOwner(gridPosition + b))
			{
				beamBlockedByBuildingForbiddenArea = true;
				return false;
			}
		}
		if (MonoSingleton<SlopeManager>.Instance.SlopeExists(gridPosition))
		{
			beamBlockedBySlope = true;
			return false;
		}
		BuildingType checkFor = BuildingType.Roof | BuildingType.Stairs | BuildingType.Window | BuildingType.Door | BuildingType.BarnDoor | BuildingType.Ladder;
		BaseBuildingInstance firstBuilding = Map.BuildingsManagerMain.GetFirstBuilding(checkFor, gridPosition);
		if (firstBuilding != null)
		{
			beamBlockerBuilding = firstBuilding;
			return false;
		}
		BaseBuildingInstance building = map.BuildingsManagerMain.GetBuilding(gridPosition, (BaseBuildingInstance x) => x.BuildingType == BuildingType.FenceGate);
		if (building != null && !building.HasDisposed)
		{
			BuildingsManagerMain buildingsManagerMain2 = Map.BuildingsManagerMain;
			Vec3Int b = Vec3Int.up;
			if (building == buildingsManagerMain2.GetBuilding(gridPosition + b, (BaseBuildingInstance x) => x.BuildingType == BuildingType.FenceGate))
			{
				beamBlockerBuilding = building;
				return false;
			}
		}
		BaseBuildingInstance building2 = map.BuildingsManagerMain.GetBuilding(gridPosition, (BaseBuildingInstance x) => x.BuildingType == BuildingType.SiegeWeapon);
		if (building2 != null && !building2.HasDisposed)
		{
			SiegeWeaponComponentInstance componentInstance = map.SiegeWeaponComponentManager.GetComponentInstance(building2);
			if (componentInstance != null && componentInstance.Blueprint.SiegeWeaponType == SiegeWeaponType.Trebuchet)
			{
				beamBlockerBuilding = building2;
				return false;
			}
		}
		return true;
	}

	private bool AdjustPreviewEndPositionIsBuilding(ObjectSide endSide, Vec3Int position)
	{
		endWall = Map.BuildingsManagerMain.TryGetWallForBeam(position);
		if (endWall != null)
		{
			SocketComponentInstance socketComponentInstance = Map.SocketComponentManager.GetSocketComponentInstance(endWall);
			if (socketComponentInstance == null)
			{
				return false;
			}
			endSocketOccupied = socketComponentInstance.SocketOccupied(endSide);
			if (endSocketOccupied || objectsInTheWay)
			{
				placeable = false;
				beamPreview.InvalidPosition();
			}
			else
			{
				placeable = true;
			}
			return true;
		}
		return false;
	}

	private bool AdjustPreviewEndPositionIsVoxel(Vec3Int position, ObjectSide targetSide)
	{
		if (MonoSingleton<GroundManager>.Instance.GroundExists(position))
		{
			endVoxelPos = position;
			if (MonoSingleton<GroundManager>.Instance.VoxelSocketOccupied(position, targetSide) || objectsInTheWay)
			{
				placeable = false;
				beamPreview.InvalidPosition();
				endSocketOccupied = true;
			}
			else
			{
				placeable = true;
			}
			return true;
		}
		return false;
	}

	private void BeamPlacementTick(ObjectSide hitSide, Vec3Int gridPosition)
	{
		if (baseBuildingBlueprint == null || baseBuildingBlueprint.BuildingType != BuildingType.Beam || beamPreview == null)
		{
			return;
		}
		placeable = false;
		objectsInTheWay = false;
		startSocketOccupied = false;
		endSocketOccupied = false;
		beamIntersection = false;
		beamTooLong = false;
		beamBlockedBySlope = false;
		beamBlockedByBuildingForbiddenArea = false;
		beamBlockerBuilding = null;
		this.hitSide = hitSide;
		beamPreview.ResetBeamPreview();
		bool num = Map.BuildingsManagerMain.BuildingExists(gridPosition, BuildingType.Wall) || Map.BuildingsManagerMain.BuildingExists(gridPosition, BuildingType.Voxel);
		bool flag = !hitSide.Equals(ObjectSide.Top) && !hitSide.Equals(ObjectSide.Bottom) && MonoSingleton<GroundManager>.Instance.GroundExists(gridPosition);
		bool flag2 = false;
		if (!num && !flag)
		{
			return;
		}
		startWall = Map.BuildingsManagerMain.TryGetWallForBeam(gridPosition);
		bool flag3 = MonoSingleton<GroundManager>.Instance.GroundExists(gridPosition);
		if (startWall == null && !flag3)
		{
			return;
		}
		startVoxelPos = gridPosition;
		switch (hitSide)
		{
		case ObjectSide.Left:
		{
			objectsInTheWay = CheckIfStartPositionOccupied(gridPosition, ObjectSide.Left);
			startSocketOccupied = objectsInTheWay;
			for (int num3 = gridPosition.x - minLength; num3 > gridPosition.x - maxLength; num3--)
			{
				Vec3Int vec3Int2 = new Vec3Int(num3, gridPosition.y, gridPosition.z);
				if (!CanBeamExist(vec3Int2))
				{
					beamPreview.InvalidPosition();
					objectsInTheWay = true;
					ScaleBeamAxisX(startVoxelPos, vec3Int2, beamPreview);
				}
				if (AdjustPreviewEndPositionIsBuilding(ObjectSide.Right, vec3Int2))
				{
					ScaleBeamAxisX(endWall.GridDataPosition, gridPosition, beamPreview);
					flag2 = true;
					break;
				}
				if (AdjustPreviewEndPositionIsVoxel(vec3Int2, ObjectSide.Right))
				{
					ScaleBeamAxisX(vec3Int2, gridPosition, beamPreview);
					flag2 = true;
					break;
				}
			}
			if (!objectsInTheWay && !placeable && !flag2)
			{
				Vec3Int vec3Int3 = startVoxelPos;
				ref Vec3Int reference3 = ref startVoxelPos;
				Vec3Int b = new Vec3Int(-maxLength - 1, 0, 0);
				ScaleBeamAxisX(vec3Int3, reference3 + b, beamPreview);
				BeamPreviewView beamPreviewView2 = beamPreview;
				Vec3Int startPosition2 = startVoxelPos;
				ref Vec3Int reference4 = ref startVoxelPos;
				b = new Vec3Int(-maxLength - 1, 0, 0);
				beamPreviewView2.BeamTooLong(startPosition2, reference4 + b);
				beamTooLong = true;
			}
			break;
		}
		case ObjectSide.Right:
		{
			objectsInTheWay = CheckIfStartPositionOccupied(gridPosition, ObjectSide.Right);
			startSocketOccupied = objectsInTheWay;
			for (int i = gridPosition.x + minLength; i < gridPosition.x + maxLength; i++)
			{
				Vec3Int vec3Int4 = new Vec3Int(i, gridPosition.y, gridPosition.z);
				if (!CanBeamExist(vec3Int4))
				{
					beamPreview.InvalidPosition();
					objectsInTheWay = true;
					ScaleBeamAxisX(startVoxelPos, vec3Int4, beamPreview);
				}
				if (AdjustPreviewEndPositionIsBuilding(ObjectSide.Left, vec3Int4))
				{
					ScaleBeamAxisX(gridPosition, endWall.GridDataPosition, beamPreview);
					flag2 = true;
					break;
				}
				if (AdjustPreviewEndPositionIsVoxel(vec3Int4, ObjectSide.Left))
				{
					ScaleBeamAxisX(gridPosition, vec3Int4, beamPreview);
					flag2 = true;
					break;
				}
			}
			if (!objectsInTheWay && !placeable && !flag2)
			{
				Vec3Int vec3Int5 = startVoxelPos;
				ref Vec3Int reference5 = ref startVoxelPos;
				Vec3Int b = new Vec3Int(maxLength + 1, 0, 0);
				ScaleBeamAxisX(vec3Int5, reference5 + b, beamPreview);
				BeamPreviewView beamPreviewView3 = beamPreview;
				Vec3Int startPosition3 = startVoxelPos;
				ref Vec3Int reference6 = ref startVoxelPos;
				b = new Vec3Int(maxLength + 1, 0, 0);
				beamPreviewView3.BeamTooLong(startPosition3, reference6 + b);
				beamTooLong = true;
			}
			break;
		}
		case ObjectSide.Front:
		{
			objectsInTheWay = CheckIfStartPositionOccupied(gridPosition, ObjectSide.Front);
			startSocketOccupied = objectsInTheWay;
			for (int j = gridPosition.z + minLength; j < gridPosition.z + maxLength; j++)
			{
				Vec3Int vec3Int6 = new Vec3Int(gridPosition.x, gridPosition.y, j);
				if (!CanBeamExist(vec3Int6))
				{
					beamPreview.InvalidPosition();
					objectsInTheWay = true;
					ScaleBeamAxisZ(startVoxelPos, vec3Int6, beamPreview);
				}
				if (AdjustPreviewEndPositionIsBuilding(ObjectSide.Back, vec3Int6))
				{
					ScaleBeamAxisZ(gridPosition, endWall.GridDataPosition, beamPreview);
					flag2 = true;
					break;
				}
				if (AdjustPreviewEndPositionIsVoxel(vec3Int6, ObjectSide.Back))
				{
					ScaleBeamAxisZ(gridPosition, vec3Int6, beamPreview);
					flag2 = true;
					break;
				}
			}
			if (!objectsInTheWay && !placeable && !flag2)
			{
				Vec3Int start2 = startVoxelPos;
				ref Vec3Int reference7 = ref startVoxelPos;
				Vec3Int b = new Vec3Int(0, 0, maxLength + 1);
				ScaleBeamAxisZ(start2, reference7 + b, beamPreview);
				BeamPreviewView beamPreviewView4 = beamPreview;
				Vec3Int startPosition4 = startVoxelPos;
				ref Vec3Int reference8 = ref startVoxelPos;
				b = new Vec3Int(0, 0, maxLength + 1);
				beamPreviewView4.BeamTooLong(startPosition4, reference8 + b);
				beamTooLong = true;
			}
			break;
		}
		case ObjectSide.Back:
		{
			objectsInTheWay = CheckIfStartPositionOccupied(gridPosition, ObjectSide.Back);
			startSocketOccupied = objectsInTheWay;
			for (int num2 = gridPosition.z - minLength; num2 > gridPosition.z - maxLength; num2--)
			{
				Vec3Int vec3Int = new Vec3Int(gridPosition.x, gridPosition.y, num2);
				if (!CanBeamExist(vec3Int))
				{
					beamPreview.InvalidPosition();
					objectsInTheWay = true;
					ScaleBeamAxisZ(startVoxelPos, vec3Int, beamPreview);
				}
				if (AdjustPreviewEndPositionIsBuilding(ObjectSide.Front, vec3Int))
				{
					ScaleBeamAxisZ(endWall.GridDataPosition, gridPosition, beamPreview);
					flag2 = true;
					break;
				}
				if (AdjustPreviewEndPositionIsVoxel(vec3Int, ObjectSide.Front))
				{
					ScaleBeamAxisZ(vec3Int, gridPosition, beamPreview);
					flag2 = true;
					break;
				}
			}
			if (!objectsInTheWay && !placeable && !flag2)
			{
				Vec3Int start = startVoxelPos;
				ref Vec3Int reference = ref startVoxelPos;
				Vec3Int b = new Vec3Int(0, 0, -maxLength - 1);
				ScaleBeamAxisZ(start, reference + b, beamPreview);
				BeamPreviewView beamPreviewView = beamPreview;
				Vec3Int startPosition = startVoxelPos;
				ref Vec3Int reference2 = ref startVoxelPos;
				b = new Vec3Int(0, 0, -maxLength - 1);
				beamPreviewView.BeamTooLong(startPosition, reference2 + b);
				beamTooLong = true;
			}
			break;
		}
		default:
			placeable = false;
			beamPreview.ResetBeamPreview();
			break;
		}
	}

	private Vec3Int GetBeamGridPositionFromRaycast()
	{
		ray = mainCamera.ScreenPointToRay(Input.mousePosition);
		int num = Physics.RaycastNonAlloc(ray, raycastHits, 1000f, raycastMask);
		if (num > 0)
		{
			hit = GetClosestRaycastHit(raycastHits, num);
			return TryGetBeamWallPosition(hit);
		}
		Vec3Int result = new Vec3Int((int)ray.GetPoint(1000f).x, (int)ray.GetPoint(1000f).y, (int)ray.GetPoint(1000f).z);
		Vec3Int min = new Vec3Int(0, 0, 0);
		Vec3Int max = new Vec3Int(world.SizeX, world.SizeY, world.SizeZ);
		result.Clamp(in min, in max);
		return result;
	}

	private Vec3Int TryGetBeamWallPosition(RaycastHit hit)
	{
		if (CalculateSide(this.hit) != ObjectSide.Top)
		{
			Vec3Int socketableAdjustedPosition = GetSocketableAdjustedPosition(hit);
			socketableAdjustedPosition.y /= World.MapBlockHeight;
			return socketableAdjustedPosition;
		}
		return Vec3Int.down;
	}

	private void ScaleBeamAxisX(Vec3Int startWall, Vec3Int endWall, IBeamView objectToScale)
	{
		int num = CalculateBeamScaleX(startWall, endWall, objectToScale);
		objectToScale.SetupPositionAndScale(new Vector3((float)num / 2f, 0f, 0f), new Vector3((float)(-num) / 2f, 0f, 0f), new Vector3(num, 1f, 1f));
	}

	public static int CalculateBeamScaleX(Vec3Int startWall, Vec3Int endWall, IBeamView objectToScale)
	{
		Vector3 position = new Vector3((float)(startWall.x + endWall.x) / 2f, startWall.y * World.MapBlockHeight, startWall.z);
		int result = Mathf.Abs(startWall.x - endWall.x) - 1;
		objectToScale.Transform.position = position;
		if (Math.Abs(objectToScale.Transform.eulerAngles.y - 90f) < 0.1f || Math.Abs(objectToScale.Transform.eulerAngles.y - 270f) < 0.1f)
		{
			objectToScale.Transform.eulerAngles = Vector3.zero;
		}
		return result;
	}

	private void ScaleBeamAxisZ(Vec3Int start, Vec3Int end, IBeamView objectToScale)
	{
		int num = CalculateBeamScaleZ(start, end, objectToScale);
		objectToScale.SetupPositionAndScale(new Vector3((float)num / 2f, 0f, 0f), new Vector3((float)(-num) / 2f, 0f, 0f), new Vector3(num, 1f, 1f));
	}

	public static int CalculateBeamScaleZ(Vec3Int startWall, Vec3Int endWall, IBeamView objectToScale)
	{
		Vector3 position = new Vector3(startWall.x, startWall.y * World.MapBlockHeight, (float)(startWall.z + endWall.z) / 2f);
		int result = Mathf.Abs(startWall.z - endWall.z) - 1;
		objectToScale.Transform.position = position;
		if (objectToScale.Transform.eulerAngles.y == 0f || Math.Abs(objectToScale.Transform.eulerAngles.y - 180f) < 0.1f)
		{
			objectToScale.Transform.eulerAngles = new Vector3(0f, 90f, 0f);
		}
		return result;
	}

	public BeamComponentInstance SpawnBeamAxisX(BaseBuildingBlueprint beamToSpawn, object start, object end)
	{
		bool flag = start is BaseBuildingInstance;
		bool flag2 = end is BaseBuildingInstance;
		BaseBuildingInstance baseBuildingInstance = null;
		BaseBuildingInstance baseBuildingInstance2 = null;
		Vec3Int vec3Int;
		if (flag)
		{
			baseBuildingInstance = (BaseBuildingInstance)start;
			vec3Int = baseBuildingInstance.GridDataPosition;
		}
		else
		{
			vec3Int = (Vec3Int)start;
		}
		Vec3Int vec3Int2;
		if (flag2)
		{
			baseBuildingInstance2 = (BaseBuildingInstance)end;
			vec3Int2 = baseBuildingInstance2.GridDataPosition;
		}
		else
		{
			vec3Int2 = (Vec3Int)end;
		}
		Vector3 vector = new Vector3((float)(vec3Int.x + vec3Int2.x) / 2f, vec3Int.y * World.MapBlockHeight, vec3Int.z);
		int num = Mathf.Abs(vec3Int.x - vec3Int2.x) - 1;
		if (num.Even())
		{
			vector += new Vector3(0.5f, 0f, 0f);
		}
		List<Vec3Int> list = new List<Vec3Int>();
		for (int i = vec3Int.x + 1; i < vec3Int2.x; i++)
		{
			list.Add(new Vec3Int(i, vec3Int.y, vec3Int.z));
		}
		list.Sort((Vec3Int a, Vec3Int b) => a.x.CompareTo(b.x));
		if (list.Any((Vec3Int pos) => GridDataIndexTools.IsForbiddenEdge(pos.x, pos.z)))
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("cannot_build_on_edge"));
			return null;
		}
		BaseBuildingViewComponent baseBuildingViewComponent = SpawnBaseBuildingViewComponent(beamToSpawn, raycastGridStart, 0);
		BeamViewComponent component = baseBuildingViewComponent.GetComponent<BeamViewComponent>();
		BaseBuildingBlueprint byID = Repository<BaseBuildingRepository, BaseBuildingBlueprint>.Instance.GetByID(beamToSpawn.GetID());
		Vector3 worldPos = vector;
		BaseBuildingInstance owner = Map.BuildingsManagerMain.CreateAndReturnBuildingInstance(byID, baseBuildingViewComponent, worldPos, 0);
		BeamComponent component2 = baseBuildingViewComponent.GetComponent<BeamComponent>();
		if (component2 == null)
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(28, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingPlacementManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Beam component was null for ");
				messageBuilder.AppendFormatted(beamToSpawn.GetID());
			}
			Log.Error(messageBuilder);
			return null;
		}
		BeamComponentBlueprint byID2 = Repository<BeamComponentRepository, BeamComponentBlueprint>.Instance.GetByID(byID.BeamComponentID);
		BeamComponentInstance beamComponentInstance = Map.BeamComponentManager.CreateBeamComponentInstance(byID2, owner, component, component2);
		beamComponentInstance.Positions.AddRangeUnique(list);
		Map.BeamComponentManager.AddToCache(component2, beamComponentInstance);
		if (flag)
		{
			if (flag2)
			{
				beamComponentInstance.Setup(baseBuildingInstance, baseBuildingInstance2, ObjectSide.Right, ObjectSide.Left);
			}
			else
			{
				beamComponentInstance.Setup(baseBuildingInstance, vec3Int2, ObjectSide.Right, ObjectSide.Left);
			}
		}
		else if (flag2)
		{
			beamComponentInstance.Setup(vec3Int, baseBuildingInstance2, ObjectSide.Right, ObjectSide.Left);
		}
		else
		{
			beamComponentInstance.Setup(vec3Int, vec3Int2, ObjectSide.Right, ObjectSide.Left);
		}
		beamComponentInstance.SetupOffsetAndScale(new Vector3((float)(-num) / 2f, 0f, 0f), new Vector3((float)num / 2f, 0f, 0f), new Vector3(num, 1f, 1f));
		ObjectPlacedOnMap(baseBuildingViewComponent);
		return beamComponentInstance;
	}

	public BeamComponentInstance SpawnBeamAxisZ(BaseBuildingBlueprint beamToSpawn, object start, object end)
	{
		bool flag = start is BaseBuildingInstance;
		bool flag2 = end is BaseBuildingInstance;
		BaseBuildingInstance baseBuildingInstance = null;
		BaseBuildingInstance baseBuildingInstance2 = null;
		Vec3Int vec3Int;
		if (flag)
		{
			baseBuildingInstance = (BaseBuildingInstance)start;
			vec3Int = baseBuildingInstance.GridDataPosition;
		}
		else
		{
			vec3Int = (Vec3Int)start;
		}
		Vec3Int vec3Int2;
		if (flag2)
		{
			baseBuildingInstance2 = (BaseBuildingInstance)end;
			vec3Int2 = baseBuildingInstance2.GridDataPosition;
		}
		else
		{
			vec3Int2 = (Vec3Int)end;
		}
		Vector3 vector = new Vector3(vec3Int.x, vec3Int.y * World.MapBlockHeight, (float)(vec3Int.z + vec3Int2.z) / 2f);
		int num = Mathf.Abs(vec3Int.z - vec3Int2.z) - 1;
		if (num.Even())
		{
			vector += new Vector3(0f, 0f, 0.5f);
		}
		List<Vec3Int> list = new List<Vec3Int>();
		for (int i = vec3Int.z + 1; i < vec3Int2.z; i++)
		{
			list.Add(new Vec3Int(vec3Int.x, vec3Int.y, i));
		}
		list.Sort((Vec3Int a, Vec3Int b) => a.z.CompareTo(b.z));
		if (list.Any((Vec3Int pos) => GridDataIndexTools.IsForbiddenEdge(pos.x, pos.z)))
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("cannot_build_on_edge"));
			return null;
		}
		BaseBuildingViewComponent baseBuildingViewComponent = SpawnBaseBuildingViewComponent(beamToSpawn, raycastGridStart, 90);
		BeamViewComponent component = baseBuildingViewComponent.GetComponent<BeamViewComponent>();
		BaseBuildingBlueprint byID = Repository<BaseBuildingRepository, BaseBuildingBlueprint>.Instance.GetByID(beamToSpawn.GetID());
		Vector3 worldPos = vector;
		BaseBuildingInstance owner = Map.BuildingsManagerMain.CreateAndReturnBuildingInstance(byID, baseBuildingViewComponent, worldPos, 90);
		BeamComponent component2 = baseBuildingViewComponent.GetComponent<BeamComponent>();
		if (component2 == null)
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(28, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingPlacementManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Beam component was null for ");
				messageBuilder.AppendFormatted(beamToSpawn.GetID());
			}
			Log.Error(messageBuilder);
			return null;
		}
		BeamComponentBlueprint byID2 = Repository<BeamComponentRepository, BeamComponentBlueprint>.Instance.GetByID(byID.BeamComponentID);
		BeamComponentInstance beamComponentInstance = Map.BeamComponentManager.CreateBeamComponentInstance(byID2, owner, component, component2);
		beamComponentInstance.Positions.AddRangeUnique(list);
		Map.BeamComponentManager.AddToCache(component2, beamComponentInstance);
		if (flag)
		{
			if (flag2)
			{
				beamComponentInstance.Setup(baseBuildingInstance, baseBuildingInstance2, ObjectSide.Front, ObjectSide.Back);
			}
			else
			{
				beamComponentInstance.Setup(baseBuildingInstance, vec3Int2, ObjectSide.Front, ObjectSide.Back);
			}
		}
		else if (flag2)
		{
			beamComponentInstance.Setup(vec3Int, baseBuildingInstance2, ObjectSide.Front, ObjectSide.Back);
		}
		else
		{
			beamComponentInstance.Setup(vec3Int, vec3Int2, ObjectSide.Front, ObjectSide.Back);
		}
		beamComponentInstance.SetupOffsetAndScale(new Vector3((float)(-num) / 2f, 0f, 0f), new Vector3((float)num / 2f, 0f, 0f), new Vector3(num, 1f, 1f));
		ObjectPlacedOnMap(baseBuildingViewComponent);
		return beamComponentInstance;
	}

	private IEnumerator ShowMessageBeamHasObstacle(Vector3 worldPos)
	{
		DamagePopup.Create(worldPos, MonoSingleton<LocalizationController>.Instance.GetText("placement_error_beam"));
		yield return new WaitForSeconds(2f);
		canShowBeamPlacementError = true;
	}

	private void AutoRotate()
	{
		if (skipAutomaticRotation)
		{
			return;
		}
		BuildingType buildingType = baseBuildingBlueprint.BuildingType;
		if (buildingType != BuildingType.Door && buildingType != BuildingType.BarnDoor && buildingType != BuildingType.FenceGate && buildingType != BuildingType.Window)
		{
			return;
		}
		Vec3Int vec3Int = raycastGridCurrent.Left();
		Vec3Int vec3Int2 = raycastGridCurrent.Right();
		Vec3Int vec3Int3 = raycastGridCurrent.Front();
		Vec3Int vec3Int4 = raycastGridCurrent.Back();
		bool flag = Map.BuildingsManagerMain.GetAlignmentBuilding(raycastGridCurrent) != null;
		BaseBuildingInstance alignmentBuilding = Map.BuildingsManagerMain.GetAlignmentBuilding(vec3Int);
		bool flag2 = alignmentBuilding != null;
		bool flag3 = MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int);
		BaseBuildingInstance alignmentBuilding2 = Map.BuildingsManagerMain.GetAlignmentBuilding(vec3Int2);
		bool flag4 = alignmentBuilding2 != null;
		bool flag5 = MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int2);
		BaseBuildingInstance alignmentBuilding3 = Map.BuildingsManagerMain.GetAlignmentBuilding(vec3Int3);
		bool flag6 = alignmentBuilding3 != null;
		bool flag7 = MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int3);
		BaseBuildingInstance alignmentBuilding4 = Map.BuildingsManagerMain.GetAlignmentBuilding(vec3Int4);
		bool flag8 = alignmentBuilding4 != null;
		bool flag9 = MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int4);
		if (baseBuildingBlueprint.BuildingType.Equals(BuildingType.Door) || baseBuildingBlueprint.BuildingType.Equals(BuildingType.BarnDoor) || baseBuildingBlueprint.BuildingType.Equals(BuildingType.FenceGate))
		{
			if (flag2 && alignmentBuilding.BuildingType.Equals(buildingType))
			{
				AdjustPreviewAngle(alignmentBuilding.Angle + 180f);
			}
			else if (flag4 && alignmentBuilding2.BuildingType.Equals(buildingType))
			{
				AdjustPreviewAngle(alignmentBuilding2.Angle + 180f);
			}
			else if (flag6 && alignmentBuilding3.BuildingType.Equals(buildingType))
			{
				AdjustPreviewAngle(alignmentBuilding3.Angle + 180f);
			}
			else if (flag8 && alignmentBuilding4.BuildingType.Equals(buildingType))
			{
				AdjustPreviewAngle(alignmentBuilding4.Angle + 180f);
			}
			else if ((flag2 || flag3) && (flag4 || flag5))
			{
				AdjustPreviewAngle(0f);
			}
			else if ((flag6 || flag7) && (flag8 || flag9))
			{
				AdjustPreviewAngle(90f);
			}
			else if (flag)
			{
				if (flag2)
				{
					AdjustPreviewAngle(0f);
				}
				else if (flag4)
				{
					AdjustPreviewAngle(0f);
				}
				else if (flag6)
				{
					AdjustPreviewAngle(90f);
				}
				else if (flag8)
				{
					AdjustPreviewAngle(90f);
				}
			}
		}
		else
		{
			if (!buildingType.Equals(BuildingType.Window))
			{
				return;
			}
			if ((flag2 || flag3) && (flag4 || flag5))
			{
				AdjustPreviewAngle(0f);
			}
			else if ((flag6 || flag7) && (flag8 || flag9))
			{
				AdjustPreviewAngle(90f);
			}
			else if (flag)
			{
				if (flag2 || flag3)
				{
					AdjustPreviewAngle(0f);
				}
				else if (flag4 || flag5)
				{
					AdjustPreviewAngle(0f);
				}
				else if (flag6 || flag7)
				{
					AdjustPreviewAngle(90f);
				}
				else if (flag8 || flag9)
				{
					AdjustPreviewAngle(90f);
				}
			}
		}
		void AdjustPreviewAngle(float neighbourAngle)
		{
			float num;
			for (num = neighbourAngle; num > 360f; num -= 360f)
			{
			}
			for (; num < 0f; num += 360f)
			{
			}
			if (!(preview == null))
			{
				Vector3 eulerAngles = preview.transform.eulerAngles;
				preview.transform.eulerAngles = new Vector3(eulerAngles.x, num, eulerAngles.z);
				previewAngle = preview.transform.GetEulerAngleY();
			}
		}
	}

	private string GetBeamStabilityForInfoCursor()
	{
		if (baseBuildingBlueprint == null || baseBuildingBlueprint.BuildingType != BuildingType.Beam)
		{
			return string.Empty;
		}
		if (!placeable)
		{
			return string.Empty;
		}
		int maxNeighbourStability = map.StabilityManager.GetMaxNeighbourStability(startVoxelPos);
		int num = 0;
		num = ((endWall == null) ? map.StabilityManager.GetMaxNeighbourStability(endVoxelPos) : map.StabilityManager.GetMaxNeighbourStability(endWall.GridDataPosition));
		int num2 = Mathf.Min(maxNeighbourStability, num);
		string text = num2 switch
		{
			0 => "<style=DefaultRed>" + num2 + "</style>", 
			1 => "<style=DefaultOrange>" + num2 + "</style>", 
			2 => "<style=DefaultYellow>" + num2 + "</style>", 
			3 => "<style=DefaultGreenYellow>" + num2 + "</style>", 
			_ => "<style=DefaultGreen>" + num2 + "</style>", 
		};
		return MonoSingleton<LocalizationController>.Instance.GetText("info_stability") + ": " + text;
	}

	private string GetStabilityForInfoCursor()
	{
		if (baseBuildingBlueprint == null)
		{
			return string.Empty;
		}
		BuildingType buildingType = BuildingType.Wall | BuildingType.Floor | BuildingType.Voxel | BuildingType.Door | BuildingType.BarnDoor;
		if (!buildingType.HasFlag(baseBuildingBlueprint.BuildingType))
		{
			return string.Empty;
		}
		int maxNeighbourStability = map.StabilityManager.GetMaxNeighbourStability(raycastGridCurrent);
		string text = maxNeighbourStability switch
		{
			0 => "<style=DefaultRed>" + maxNeighbourStability + "</style>", 
			1 => "<style=DefaultOrange>" + maxNeighbourStability + "</style>", 
			2 => "<style=DefaultYellow>" + maxNeighbourStability + "</style>", 
			3 => "<style=DefaultGreenYellow>" + maxNeighbourStability + "</style>", 
			_ => "<style=DefaultGreen>" + maxNeighbourStability + "</style>", 
		};
		return MonoSingleton<LocalizationController>.Instance.GetText("info_stability") + ": " + text;
	}

	private string GetBeeSkepInfoCursor()
	{
		if (baseBuildingBlueprint == null || string.IsNullOrEmpty(baseBuildingBlueprint.ProductionComponentID))
		{
			return string.Empty;
		}
		if (Repository<ProductionComponentsRepository, ProductionComponentBlueprint>.Instance.GetByID(baseBuildingBlueprint.ProductionComponentID).ProductionSpeedMultiplierSkep.Radius.IsCloseToZero())
		{
			return string.Empty;
		}
		return baseBuildablePreview.GetComponent<BeeSkepPreview>().GetBeeSkepCursorInfo();
	}

	private void CacheCursorInfoData()
	{
		if (baseBuildingBlueprint == null)
		{
			return;
		}
		localizedBuildingName = BuildingUtils.GetLocalizedName(baseBuildingBlueprint.GetID());
		tooltipResourcesInfos.Clear();
		if (moveBuilding == RelocateBuilding.None)
		{
			foreach (string key in baseBuildingBlueprint.Materials.Dictionary.Keys)
			{
				Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(key);
				int amount = baseBuildingBlueprint.Materials.Dictionary[key];
				_ = ResourceUtils.GetTextIcon(byID) + amount + ResourceUtils.GetLocalizedResourceName(byID);
				TooltipResourcesInfo item = new TooltipResourcesInfo(ResourceUtils.GetTextIcon(byID) ?? "", amount, ResourceUtils.GetLocalizedResourceName(byID), byID);
				tooltipResourcesInfos.Add(item);
			}
		}
		localizedInfoCursorData.Clear();
		localizedInfoCursorData.Add(localizedBuildingName);
		if (baseBuildingBlueprint.BuildingType == BuildingType.Well)
		{
			localizedInfoCursorData.Add(MonoSingleton<LocalizationController>.Instance.GetText("structure_info_well"));
		}
		if (moveBuilding == RelocateBuilding.None)
		{
			localizedInfoCursorData.Add("1x");
			foreach (TooltipResourcesInfo tooltipResourcesInfo in tooltipResourcesInfos)
			{
				int amount2 = tooltipResourcesInfo.Amount;
				if (MonoSingleton<ResourcePileTracker>.Instance.GetCount(tooltipResourcesInfo.Blueprint).AllowedCount < amount2)
				{
					localizedInfoCursorData.Add(tooltipResourcesInfo.SpriteFormatted + " " + ColorUtils.ColorText(amount2.ToString(), "red") + "  " + tooltipResourcesInfo.ResourceNameLocalized);
				}
				else
				{
					localizedInfoCursorData.Add($"{tooltipResourcesInfo.SpriteFormatted} {amount2} {tooltipResourcesInfo.ResourceNameLocalized}");
				}
			}
			if (!Map.BuildingsManagerMain.HasEnoughAllowedResources(baseBuildingBlueprint))
			{
				localizedInfoCursorData.Add("<style=DefaultRed>" + MonoSingleton<LocalizationController>.Instance.GetText("building_error_no_resources") + "</style>");
			}
		}
		localizedInfoCursorData.AddIfNotNullOrEmpty(GetStabilityForInfoCursor());
		localizedInfoCursorData.AddIfNotNullOrEmpty(GetBeamStabilityForInfoCursor());
		localizedInfoCursorData.AddIfNotNullOrEmpty(GetBeeSkepInfoCursor());
		uicontroller.UpdateInfoCursorContent(localizedInfoCursorData);
	}

	private void UpdateInfoCursor2DGrid(int width, int height, int count = 1)
	{
		if (baseBuildingBlueprint == null)
		{
			return;
		}
		localizedInfoCursorData.Clear();
		localizedInfoCursorData.Add(localizedBuildingName);
		localizedInfoCursorData.Add($"{width} x {height}");
		foreach (TooltipResourcesInfo tooltipResourcesInfo in tooltipResourcesInfos)
		{
			int num = tooltipResourcesInfo.Amount * count;
			if (MonoSingleton<ResourcePileTracker>.Instance.GetCount(tooltipResourcesInfo.Blueprint).AllowedCount < num)
			{
				localizedInfoCursorData.Add(tooltipResourcesInfo.SpriteFormatted + " " + ColorUtils.ColorText(num.ToString(), "red") + "  " + tooltipResourcesInfo.ResourceNameLocalized);
			}
			else
			{
				localizedInfoCursorData.Add($"{tooltipResourcesInfo.SpriteFormatted} {num} {tooltipResourcesInfo.ResourceNameLocalized}");
			}
		}
		if (!Map.BuildingsManagerMain.HasEnoughAllowedResources(baseBuildingBlueprint, count))
		{
			localizedInfoCursorData.Add("<style=DefaultRed>" + MonoSingleton<LocalizationController>.Instance.GetText("building_error_no_resources") + "</style>");
		}
		uicontroller.UpdateInfoCursorContent(localizedInfoCursorData);
	}

	private void UpdateInfoCursorSingleAxisDrag(int numberOfGridSpaces, int numberOfBuildings)
	{
		if (baseBuildingBlueprint == null)
		{
			return;
		}
		localizedInfoCursorData.Clear();
		localizedInfoCursorData.Add(localizedBuildingName);
		localizedInfoCursorData.Add($"{numberOfGridSpaces}x");
		foreach (TooltipResourcesInfo tooltipResourcesInfo in tooltipResourcesInfos)
		{
			int num = tooltipResourcesInfo.Amount * numberOfBuildings;
			if (MonoSingleton<ResourcePileTracker>.Instance.GetCount(tooltipResourcesInfo.Blueprint).AllowedCount < num)
			{
				localizedInfoCursorData.Add(tooltipResourcesInfo.SpriteFormatted + " " + ColorUtils.ColorText(num.ToString(), "red") + "  " + tooltipResourcesInfo.ResourceNameLocalized);
			}
			else
			{
				localizedInfoCursorData.Add($"{tooltipResourcesInfo.SpriteFormatted} {num} {tooltipResourcesInfo.ResourceNameLocalized}");
			}
		}
		if (!Map.BuildingsManagerMain.HasEnoughAllowedResources(baseBuildingBlueprint, numberOfBuildings))
		{
			localizedInfoCursorData.Add("<style=DefaultRed>" + MonoSingleton<LocalizationController>.Instance.GetText("building_error_no_resources") + "</style>");
		}
		uicontroller.UpdateInfoCursorContent(localizedInfoCursorData);
	}

	private void UpdateInfoCursor(int count = 1)
	{
		if (baseBuildingBlueprint == null)
		{
			return;
		}
		localizedInfoCursorData.Clear();
		localizedInfoCursorData.Add(localizedBuildingName);
		localizedInfoCursorData.Add("1x");
		foreach (TooltipResourcesInfo tooltipResourcesInfo in tooltipResourcesInfos)
		{
			int num = tooltipResourcesInfo.Amount * count;
			if (MonoSingleton<ResourcePileTracker>.Instance.GetCount(tooltipResourcesInfo.Blueprint).AllowedCount < num)
			{
				localizedInfoCursorData.Add(tooltipResourcesInfo.SpriteFormatted + " " + ColorUtils.ColorText(num.ToString(), "red") + "  " + tooltipResourcesInfo.ResourceNameLocalized);
			}
			else
			{
				localizedInfoCursorData.Add($"{tooltipResourcesInfo.SpriteFormatted} {num} {tooltipResourcesInfo.ResourceNameLocalized}");
			}
		}
		if (!Map.BuildingsManagerMain.HasEnoughAllowedResources(baseBuildingBlueprint))
		{
			localizedInfoCursorData.Add("<style=DefaultRed>" + MonoSingleton<LocalizationController>.Instance.GetText("building_error_no_resources") + "</style>");
		}
		uicontroller.UpdateInfoCursorContent(localizedInfoCursorData);
	}

	public void SpawnBlueprint(BaseBuildingBlueprint blueprint, Vec3Int gridPos, int angle = 0, RelocateBuilding moveBuilding = RelocateBuilding.None)
	{
		ObjectSide objectSide = ObjectSide.None;
		Vec3Int zero = Vec3Int.zero;
		switch (angle)
		{
		case 180:
			objectSide = ObjectSide.Left;
			zero = Vec3Int.left;
			break;
		case 90:
			objectSide = ObjectSide.Back;
			zero = Vec3Int.back;
			break;
		case 270:
			objectSide = ObjectSide.Front;
			zero = Vec3Int.forward;
			break;
		default:
			objectSide = ObjectSide.Right;
			zero = Vec3Int.right;
			break;
		}
		if (blueprint.ConstructableBaseCategory == ConstructableBaseCategory.Beam)
		{
			InitializeBuilding(blueprint.GetID());
			BeamPlacementTick(objectSide, gridPos);
			TrySpawnBeam();
		}
		else if (blueprint.ConstructableBaseCategory == ConstructableBaseCategory.Roof)
		{
			List<Vec3Int> list = new List<Vec3Int>();
			switch (angle)
			{
			case 0:
			{
				for (int j = gridPos.x; j < gridPos.x + 1; j++)
				{
					list.Add(new Vec3Int(j, gridPos.y, gridPos.z));
				}
				break;
			}
			case 180:
			{
				for (int num = gridPos.x; num > gridPos.x - 1; num--)
				{
					list.Add(new Vec3Int(num, gridPos.y, gridPos.z));
				}
				break;
			}
			case 90:
			{
				for (int num2 = gridPos.z; num2 > gridPos.z - 1; num2--)
				{
					list.Add(new Vec3Int(gridPos.x, gridPos.y, num2));
				}
				break;
			}
			default:
			{
				for (int i = gridPos.z; i < gridPos.z + 1; i++)
				{
					list.Add(new Vec3Int(gridPos.x, gridPos.y, i));
				}
				break;
			}
			}
			SpawnRoofAutoTesting(blueprint, gridPos, angle, Vec3Int.one, list);
		}
		else if (blueprint.ConstructableBaseCategory == ConstructableBaseCategory.Socket)
		{
			Vec3Int socketableItemPos = gridPos + zero;
			InitializeBuilding(blueprint.GetID(), moveBuilding);
			AdjustSocketablePreview(objectSide, gridPos);
			TryPlaceSocketable(socketableItemPos, gridPos, objectSide, angle);
		}
		else
		{
			InitializeBuilding(blueprint.GetID(), moveBuilding);
			raycastGridStart = gridPos;
			MouseUpSpawnInitializeBuildings(angle);
		}
		buildingsDictionary.Clear();
	}

	public void SpawnRoofAutoTesting(BaseBuildingBlueprint blueprint, Vec3Int gridPos, int angle, Vec3Int scale, List<Vec3Int> positions)
	{
		baseBuildingBlueprint = blueprint;
		roofComponentBlueprint = Repository<RoofComponentRepository, RoofComponentBlueprint>.Instance.GetByID(baseBuildingBlueprint.RoofComponentID);
		raycastGridStart = gridPos;
		previewAngle = angle;
		CreateRoofs(angle, scale, positions);
	}

	public BaseBuildingInstance SpawnEnemyBuilding(string blueprintId, Vec3Int gridPos, int yAngle)
	{
		BaseBuildingBlueprint byID = Repository<BaseBuildingRepository, BaseBuildingBlueprint>.Instance.GetByID(blueprintId);
		BaseBuildingViewComponent baseBuildingViewComponent = SpawnFromPool(byID, gridPos, yAngle, FactionOwnership.Enemy);
		if (baseBuildingViewComponent == null)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(66, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Managers\\BuildingPlacementManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Couldn't spawn ");
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral(" at position ");
				messageBuilder.AppendFormatted(gridPos);
				messageBuilder.AppendLiteral(" and angle ");
				messageBuilder.AppendFormatted(yAngle);
				messageBuilder.AppendLiteral(". Something is blocking it.");
			}
			Log.Warning(messageBuilder);
			return null;
		}
		bool num = byID.TransfersStability();
		BaseBuildingInstance baseBuildingInstance = Map.BuildingsManagerMain.CreateAndReturnBuildingInstance(byID, baseBuildingViewComponent, GridUtils.GetWorldPosition(gridPos), yAngle, FactionOwnership.Enemy);
		ObjectPlacedOnMap(baseBuildingViewComponent);
		buildingsDictionary.Clear();
		if (num)
		{
			Map.StabilityManager.PlaceBlueprint(baseBuildingViewComponent);
			if (Map.StabilityManager.GetBlueprintStability(gridPos) == 0)
			{
				Map.StabilityManager.ClearBlueprint(gridPos, byID);
				baseBuildingInstance.Map.BuildingsManagerMain.DestroyBuilding(baseBuildingInstance);
				return null;
			}
		}
		return baseBuildingInstance;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
