using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix;
using NSEipix.Base;
using NSEipix.Model;
using NSEipix.Repository;
using NSEipix.TaskManager;
using NSMedieval.Components;
using NSMedieval.Components.Base;
using NSMedieval.Construction;
using NSMedieval.Controllers;
using NSMedieval.DebugEvents;
using NSMedieval.Dictionary;
using NSMedieval.Enums;
using NSMedieval.Fire;
using NSMedieval.FloatingOverlaySystem;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Managers.Selection;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.MovableBuildings;
using NSMedieval.Repository;
using NSMedieval.Serialization;
using NSMedieval.State;
using NSMedieval.StatsSystem;
using NSMedieval.Terrain;
using NSMedieval.Tools;
using NSMedieval.Tutorial;
using NSMedieval.Types;
using NSMedieval.UI.Utils;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.View;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using NSMedieval.Village.Map.Pathfinding;
using NSMedieval.Water;
using UnityEngine;

namespace NSMedieval.BuildingComponents;

[Serializable]
[FVSerializableKey("BaseBuildingInstance", "")]
public class BaseBuildingInstance : WorldObject, IConstructable, IGameDisposable, IDisposable, IReservable, IForbidable, IStatsOwner, IDamageTakingAgent, IDamageCommonAgent, IGoapTargetable, IProgressBarOwner
{
	[NonSerialized]
	public int? DeliveryJobPriority;

	[SerializeField]
	private Storage storage;

	[SerializeField]
	private LockState lockState;

	[SerializeField]
	private EventStorage eventStorage;

	[SerializeField]
	private ConstructionPhase constructionPhase;

	[SerializeField]
	private float remainingTime;

	[SerializeField]
	private float totalWorkerBuildTime;

	[SerializeField]
	private int stability;

	[SerializeField]
	private int tempStability;

	[SerializeField]
	private int producerUniqueId;

	[SerializeField]
	private bool reachable;

	[SerializeField]
	private BuildingType buildingType;

	[NonSerialized]
	private float buildTime;

	[NonSerialized]
	private BaseBuildingBlueprint blueprint;

	[SerializeField]
	private List<Vec3Int> positions = new List<Vec3Int>();

	[SerializeField]
	private float rotateMeshVariation;

	[SerializeField]
	private bool flipXMeshVariation;

	[SerializeField]
	private bool flipZMeshVariation;

	[SerializeField]
	private bool markedForDestruction;

	[SerializeField]
	private bool isMoveBlueprint;

	[SerializeField]
	private bool markedForUninstall;

	[SerializeField]
	private bool markedForMoving;

	[SerializeField]
	protected OrderType orders;

	[NonSerialized]
	private StatsInstance stats;

	[SerializeField]
	private bool showFoundation;

	[SerializeField]
	private bool isForbidden;

	[SerializeField]
	private bool resourcesAvailable;

	[SerializeField]
	private BuildingOwnershipInfo buildingOwnershipInfo;

	[SerializeField]
	private bool automaticMeshVariationLoading;

	[NonSerialized]
	private ThermalModel currentThermalModel;

	[SerializeField]
	private bool placedOnBeam;

	[NonSerialized]
	private ushort pathfindingPenalty;

	[NonSerialized]
	private float walkSpeedMultiplier;

	[NonSerialized]
	private float combatCover;

	[SerializeField]
	private BuildingType componentFlags;

	private float attackTraversePenalty;

	private ProgressBarFloatingElement[] progressBar;

	[NonSerialized]
	private MovableBuildingPileInstance movableBuildingPileInstance;

	[NonSerialized]
	private List<Vec3Int> forbiddenArea = new List<Vec3Int>();

	[SerializeField]
	private Vec3Int voxelHolderPosition;

	[SerializeField]
	private ObjectSide objectSide = ObjectSide.None;

	private bool attachedToSocketComponent;

	private bool hasStabilityToBuild;

	private bool materialVariationsAppliedRefresh;

	private string materialVariationsApplied;

	private Dictionary<string, int> constructionCost = new Dictionary<string, int>();

	private bool underWater;

	private Task onGroundDestroyWaitForNavmeshUpdateTask;

	private bool protectingAgainstPredators;

	private bool setMaxHealth;

	[SerializeField]
	private List<string> variationsApplied = new List<string>();

	public int AdjustedAngle => (int)(base.Angle + rotateMeshVariation) % 360;

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
				isForbidden = value;
				this.ForbidChangeEvent?.Invoke(this);
				UpdateJobManager();
			}
		}
	}

	public override float Flammability
	{
		get
		{
			if (constructionPhase == ConstructionPhase.Blueprint)
			{
				return 0f;
			}
			return Blueprint.Flammability;
		}
	}

	public override bool BlueprintExists
	{
		get
		{
			if (Blueprint == null)
			{
				return false;
			}
			if (!string.IsNullOrEmpty(Blueprint.ShelfComponentID) && Repository<ShelfComponentRepository, ShelfComponentBlueprint>.Instance.GetByID(Blueprint.ShelfComponentID) == null)
			{
				return false;
			}
			return true;
		}
	}

	public bool IsForbiddenPrisonCell { get; set; }

	public bool HealthDepleted { get; private set; }

	internal override ThermalModel ThermalModel => currentThermalModel;

	public bool PlacedOnBeam => placedOnBeam;

	public List<Vec3Int> ForbiddenArea => forbiddenArea;

	public EventStorage EventStorage => eventStorage;

	public MovableBuildingPileInstance MovableBuildingPileInstance => movableBuildingPileInstance;

	public int Stability => stability;

	public int TempStability
	{
		get
		{
			return tempStability;
		}
		set
		{
			tempStability = value;
		}
	}

	public BaseBuildingBlueprint Blueprint => blueprint;

	public ConstructableBaseCategory ConstructableBaseCategory => blueprint.ConstructableBaseCategory;

	public BuildingType ReplacementFlag => blueprint.ReplacementFlag;

	public override List<Vec3Int> Positions => positions;

	public float RemainingTime => remainingTime;

	public ConstructionPhase ConstructionPhase => constructionPhase;

	public bool IsUnderConstruction
	{
		get
		{
			if (ConstructionPhase != ConstructionPhase.Finished)
			{
				return ConstructionPhase != ConstructionPhase.Uninstalled;
			}
			return false;
		}
	}

	public bool IsMoveBlueprint => isMoveBlueprint;

	public bool MarkedForUninstall => markedForUninstall;

	public bool MarkedForMoving => markedForMoving;

	public bool Loaded { get; set; }

	public int ProducerUniqueId => producerUniqueId;

	public BuildingType ComponentFlags => componentFlags;

	public OrderType Orders => orders;

	public DamageTakingAgentType DamageAgentType => DamageTakingAgentType.Building;

	public bool HasDied => base.HasDisposed;

	public BuildingType BuildingType
	{
		get
		{
			if (buildingType != BuildingType.Default)
			{
				return buildingType;
			}
			if (blueprint == null)
			{
				blueprint = Repository<BaseBuildingRepository, BaseBuildingBlueprint>.Instance.GetByID(blueprintId);
			}
			if (blueprint != null)
			{
				buildingType = blueprint.BuildingType;
				return buildingType;
			}
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(48, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Unable to get blueprint for ");
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral(". Returning default.");
			}
			Log.Error(messageBuilder);
			return BuildingType.Default;
		}
	}

	public IReadOnlyList<string> VariationsApplied => variationsApplied;

	public string MaterialVariationsApplied
	{
		get
		{
			if (materialVariationsAppliedRefresh)
			{
				StringBuilder stringBuilder = new StringBuilder();
				stringBuilder.Append(blueprintId);
				foreach (string item in variationsApplied)
				{
					MeshVariation meshVariation = blueprint.GetMeshVariation(item);
					if (meshVariation != null && meshVariation.HasTextureSlots)
					{
						stringBuilder.Append('.');
						stringBuilder.Append(meshVariation.Name);
					}
				}
				materialVariationsApplied = stringBuilder.ToString();
				materialVariationsAppliedRefresh = false;
			}
			return materialVariationsApplied;
		}
	}

	public bool FlipXMeshVariation => flipXMeshVariation;

	public bool FlipZMeshVariation => flipZMeshVariation;

	public Vec3Int VoxelHolderPosition => voxelHolderPosition;

	public ObjectSide ObjectSide => objectSide;

	public bool AttachedToSocketComponent => attachedToSocketComponent;

	public StatsInstance Stats
	{
		get
		{
			if (constructionPhase == ConstructionPhase.Blueprint)
			{
				return null;
			}
			if (stats?.Owner == null)
			{
				InitStats();
			}
			return stats;
		}
	}

	public BuildingOwnershipInfo BuildingOwnershipInfo => buildingOwnershipInfo;

	public Dictionary<string, int> ConstructionCost => constructionCost;

	public Storage Storage => storage;

	public bool MarkedForDestruction => markedForDestruction;

	public bool Reachable
	{
		get
		{
			return reachable;
		}
		set
		{
			reachable = value;
		}
	}

	public bool HasStabilityToBuild
	{
		get
		{
			return hasStabilityToBuild;
		}
		set
		{
			hasStabilityToBuild = value;
			base.Map.SocketComponentManager.GetSocketComponentInstance(this)?.RefreshSocketHasStabilityToBuild();
		}
	}

	public bool ResourcesAvailable
	{
		get
		{
			return resourcesAvailable;
		}
		set
		{
			resourcesAvailable = value;
		}
	}

	public bool SkilledConstructionWorkerExists { get; set; }

	public float RotateMeshVariation => rotateMeshVariation;

	public string CurrentMeshVariation { get; private set; }

	public bool AutomaticMeshVariationLoading => automaticMeshVariationLoading;

	public bool HasActivePath => false;

	public LockState LockState => lockState;

	public override ushort PathfindingPenalty
	{
		get
		{
			if (Blueprint == null)
			{
				return 1000;
			}
			return GetPathfindingPenalty();
		}
	}

	public override float WalkSpeedMultiplier
	{
		get
		{
			if (Blueprint == null)
			{
				return 0.85f;
			}
			return GetSpeedMultiplier();
		}
	}

	[field: NonSerialized]
	public event Action<WaterDepthLevel> WaterLevelChangedEvent;

	[field: NonSerialized]
	public event Action<IForbidable> ForbidChangeEvent;

	[field: NonSerialized]
	public event Action<IForbidable> ForbidStateWillChangeEvent;

	[field: NonSerialized]
	public event Action StabilityUpdatedRefreshVisualsEvent;

	[field: NonSerialized]
	public event Action ObjectPlacedOnMapEvent;

	[field: NonSerialized]
	public event Action ReturnToBlueprintEvent;

	[field: NonSerialized]
	public event Action<bool> BaseBuildingEnterFoundationStateEvent;

	[field: NonSerialized]
	public event Action<bool> BaseBuildingEnterFinishedStateEvent;

	[field: NonSerialized]
	public event Action ConstructionCompletedEvent;

	[field: NonSerialized]
	public event Action ConstructionPausedEvent;

	[field: NonSerialized]
	public event Action ConstructionStartedEvent;

	[field: NonSerialized]
	public event Action<StatInstance> BuildingHealthUpdatedEvent;

	[field: NonSerialized]
	public event Action RefreshWalkableColliderEvent;

	[field: NonSerialized]
	public event Action BuildingMeshVariationSetEvent;

	[field: NonSerialized]
	public event Action BuildingMeshVariationRotatedEvent;

	[field: NonSerialized]
	public event Action BuildingMeshVariationFlippedEvent;

	[field: NonSerialized]
	public event Action RequestHasStabilityToBuildEvent;

	[field: NonSerialized]
	public event Action ReachabilityChangedEvent;

	[field: NonSerialized]
	public event Action MainBuildingStabilityChangedEvent;

	[field: NonSerialized]
	public event Action RefreshRoomChangedEvent;

	[field: NonSerialized]
	public event Action<StatInstance> BuildingRepairingTickEvent;

	[field: NonSerialized]
	public event Action DisposeComponentsEvent;

	[field: NonSerialized]
	public event Action<bool> PileForbidChangedColorBlueprintEvent;

	[field: NonSerialized]
	internal event Action SelectBuildingEvent;

	public event Action<float> TimeRemainingEvent;

	public event Action TriggerBuildParticlesEvent;

	public BaseBuildingInstance(BaseBuildingBlueprint blueprint, Vector3 worldPosition, int angle, FactionOwnership factionOwnership = FactionOwnership.Player)
		: base(WorldObjectType.Building, worldPosition, blueprint.Size, Mathf.Abs(angle), GridDataType.None, factionOwnership)
	{
		this.blueprint = blueprint;
		blueprintId = blueprint.GetID();
		buildingType = blueprint.BuildingType;
		buildTime = blueprint.BuildTime;
		remainingTime = buildTime;
		protectingAgainstPredators = blueprint.PassivePredatorProtection;
		storage = new Storage(new StorageBase(999, ignoreWeigth: true));
		storage.ResourceAddedEvent += OnResourceAdded;
		SetPositions();
		LoadBlueprintDefaultData(this.blueprint);
		eventStorage = new EventStorage(this.blueprint.StorageBase.Capacity);
		InitializeMeshVariations();
		progressBar = new ProgressBarFloatingElement[5];
		using PooledList<Vec3Int> buildingPositions = ListPool<Vec3Int>.GetJanitor(positions);
		using PooledList<Vec3Int> pooledList = Singleton<GridTools>.Instance.GetForbiddenPositions(this.blueprint, buildingPositions, base.GridDataPosition, base.Angle);
		forbiddenArea.AddRange(pooledList);
		CurrentMeshVariation = this.blueprint.VariationLists[0].Variations[0].Name;
		automaticMeshVariationLoading = true;
	}

	public void ForceSetHealthDepleted()
	{
		HealthDepleted = true;
	}

	public T GetComponentInstance<T>() where T : BaseComponentInstance
	{
		VillageMap map = base.Map;
		if (map == null)
		{
			return null;
		}
		BuildingsManagerMain buildingsManagerMain = map.BuildingsManagerMain;
		if (buildingsManagerMain == null)
		{
			return null;
		}
		return buildingsManagerMain.GetComponentInstance<T>(this);
	}

	public bool HasComponentInstance<T>() where T : BaseComponentInstance
	{
		return base.Map.BuildingsManagerMain.GetComponentInstance<T>(this) != null;
	}

	public bool HasConstructionOrder()
	{
		if (!markedForDestruction && !markedForUninstall)
		{
			return markedForMoving;
		}
		return true;
	}

	public ProgressBarFloatingElement GetProgressBar(OverlayProgressBarType type = OverlayProgressBarType.None)
	{
		if (progressBar[(int)type] != null)
		{
			return progressBar[(int)type];
		}
		progressBar[(int)type] = FloatingElementFactory.ProduceProgressBarElement(type, FloatingElementHolderType.Default, GetTransform());
		progressBar[(int)type].OnDisposedEvent += delegate
		{
			progressBar[(int)type] = null;
		};
		return progressBar[(int)type];
	}

	public NSMedieval.StatsSystem.Attribute GetAttributeOverride(AttributeType type)
	{
		return null;
	}

	public List<EquipmentInstance> GetEquipment()
	{
		return null;
	}

	public Transform GetTransform()
	{
		SelectableObject view = GetView();
		if (!(view == null))
		{
			return view.transform;
		}
		return null;
	}

	public override SelectableObject GetView()
	{
		return base.Map?.BuildingsManagerMain?.GetView(this);
	}

	public bool IsBuildingWalkable()
	{
		if ((BuildingType & BuildingType.Stairs) != 0)
		{
			return false;
		}
		return Blueprint.PathfindingPenalty < ushort.MaxValue;
	}

	public bool OverlapsWithSleepingCreature()
	{
		foreach (Vec3Int position in Positions)
		{
			Vec3Int gridPosition = position;
			MapNode node = base.Map.GetNode(in gridPosition);
			if (node != null && node.ContainsSleepingOrFaintedCreature())
			{
				return true;
			}
		}
		return false;
	}

	public bool DeconstructionBlockedBySleepingOrFaintedCreature()
	{
		if (buildingType == BuildingType.Floor)
		{
			return OverlapsWithSleepingCreature();
		}
		if (blueprint.VerticalStabilityCanStandOn())
		{
			foreach (Vec3Int position in Positions)
			{
				Vec3Int gridPosition = position;
				MapNode mapNode = base.Map.GetNode(in gridPosition)?.GetNodeAbove();
				if (mapNode != null && mapNode.ContainsSleepingOrFaintedCreature())
				{
					return true;
				}
			}
		}
		return false;
	}

	public bool IsInBounds(Vec3Int input)
	{
		return Positions.Contains(input);
	}

	public void PileForbidChangedColorBlueprint(bool sourcePileForbidden)
	{
		this.PileForbidChangedColorBlueprintEvent?.Invoke(sourcePileForbidden);
	}

	public void RefreshRoomChanged()
	{
		this.RefreshRoomChangedEvent?.Invoke();
	}

	public void SetProtectingAgainstPredators(bool protectingAgainstPredators)
	{
		this.protectingAgainstPredators = protectingAgainstPredators;
	}

	public bool IsProtectingAgainstPredators()
	{
		return protectingAgainstPredators;
	}

	public void AddNewComponent(BuildingType componentType)
	{
		componentFlags |= componentType;
	}

	public void OverridePathfindingPenalty(ushort pathfindingPenalty)
	{
		this.pathfindingPenalty = pathfindingPenalty;
	}

	public void LoadDefaultPathfindingPenalty()
	{
		pathfindingPenalty = blueprint.PathfindingPenalty;
	}

	public void OverrideWalkSpeedMultiplier(float walkSpeedMultiplier)
	{
		this.walkSpeedMultiplier = walkSpeedMultiplier;
	}

	public void LoadDefaultWalkSpeedMultiplier()
	{
		walkSpeedMultiplier = blueprint.WalkSpeedMultiplier;
	}

	public void OverrideCombatCover(float newCombatCover)
	{
		combatCover = newCombatCover;
	}

	public void LoadDefaultCombatCover()
	{
		combatCover = blueprint.Cover;
	}

	public void BuildingDeconstructed(Vector3 pileSpawnPos)
	{
		BuildingRemovedSpawnResources(pileSpawnPos, BuildingResourceSpawnType.Deconstructed);
		base.Map.BuildingsManagerMain.BuildingDeconstructed(this);
	}

	public void BuildingCanceled(Vector3 pileSpawnPos)
	{
		BuildingRemovedSpawnResources(pileSpawnPos, BuildingResourceSpawnType.Deconstructed);
		base.Map.BuildingsManagerMain.BuildingDeconstructed(this);
	}

	public void DestroyBuildingStabilityZero(bool replaced = false, bool skipStabilityCheck = false)
	{
		if (ConstructionPhase == ConstructionPhase.Finished)
		{
			BuildingRemovedSpawnResources(base.WorldPosition, BuildingResourceSpawnType.StabilityLoss);
		}
		base.Map.BuildingsManagerMain.DestroyBuilding(this, replaced, skipStabilityCheck);
	}

	public void DropConstructionResources()
	{
		if (constructionPhase == ConstructionPhase.Finished || LoadingController.IsLeavingMainScene)
		{
			return;
		}
		Vector3 input;
		if (BuildingType == BuildingType.Roof)
		{
			input = base.WorldPosition - new Vector3(0f, World.MapBlockHeight, 0f);
			Vec3Int v = input.ToGridVec3Int();
			if (MonoSingleton<GroundManager>.Instance.GroundExists(v))
			{
				input = base.WorldPosition;
			}
		}
		else
		{
			input = base.WorldPosition;
		}
		if (storage == null)
		{
			return;
		}
		foreach (ResourceInstance resource in storage.Resources)
		{
			MonoSingleton<ResourcePileManager>.Instance.SpawnPile(storage.Take(resource), input);
		}
	}

	public bool BlocksSocketablePlacement()
	{
		if (base.HasDisposed)
		{
			return false;
		}
		if (BuildingType.SocketableBlocker.HasFlag(buildingType))
		{
			return true;
		}
		if (!string.IsNullOrEmpty(blueprint.DoorComponentID))
		{
			return true;
		}
		return false;
	}

	public void SetupAfterLoading()
	{
		buildTime = blueprint.BuildTime;
		constructionCost = blueprint.Materials.Dictionary;
		attackTraversePenalty = blueprint.AttackTraversePenalty;
		LoadConstructionCost(blueprint);
		pathfindingPenalty = blueprint.PathfindingPenalty;
		if (storage != null)
		{
			storage.ResourceAddedEvent += OnResourceAdded;
			if (isMoveBlueprint)
			{
				ResourceInstance singleResource = storage.GetSingleResource();
				if (singleResource != null && singleResource.Blueprint.IsBuildingStructure)
				{
					OverrideDefaultConstructionCost(singleResource.Blueprint, 1);
				}
			}
		}
		buildingOwnershipInfo?.SetupAfterLoading(this);
		CheckHealth();
		if (markedForDestruction)
		{
			base.Map.BuildingsManagerMain.ConstructionJobManager.CreateDeconstructJob(this);
		}
		UpdateJobManager();
		if (markedForUninstall)
		{
			MonoSingleton<ConstructablesGoapUninstallManager>.Instance.AddToUninstallList(this);
		}
		Loaded = true;
	}

	public void BuildingRemovedSpawnResources(Vector3 pileSpawnPos, BuildingResourceSpawnType spawnType)
	{
		if (MonoSingleton<BuildingPlacementManager>.Instance.Autoconstruct)
		{
			return;
		}
		if (ConstructionPhase == ConstructionPhase.Blueprint)
		{
			DropConstructionResources();
			return;
		}
		if (ConstructionPhase == ConstructionPhase.Finished)
		{
			if (spawnType == BuildingResourceSpawnType.StabilityLoss && Blueprint.SpawnStructurePileOnStabilityLoss)
			{
				base.Map.BuildingsManagerMain.SpawnBuildingPile(this);
				return;
			}
			string[] array = Blueprint.Materials.Dictionary.Keys.ToArray();
			foreach (string text in array)
			{
				Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(text);
				if (!(byID == null))
				{
					float num = 1f;
					switch (spawnType)
					{
					case BuildingResourceSpawnType.Deconstructed:
						num = blueprint.ReturnOnDeconstruct.Dictionary.GetValueOrDefault(text, 0.6f);
						break;
					case BuildingResourceSpawnType.Destroyed:
						num = blueprint.ReturnOnDestroy.Dictionary.GetValueOrDefault(text, 0f);
						break;
					case BuildingResourceSpawnType.StabilityLoss:
						num = blueprint.ReturnOnDeconstruct.Dictionary.GetValueOrDefault(text, 0.6f);
						break;
					}
					int num2 = (int)((float)Blueprint.Materials.Dictionary[text] * num);
					if (num2 < 1)
					{
						return;
					}
					if (spawnType == BuildingResourceSpawnType.Destroyed)
					{
						MonoSingleton<ResourcePileManager>.Instance.TeleportPile(new ResourceInstance(byID, num2), pileSpawnPos);
					}
					else
					{
						MonoSingleton<ResourcePileManager>.Instance.SpawnPile(new ResourceInstance(byID, num2), pileSpawnPos);
					}
				}
			}
		}
		DropConstructionResources();
	}

	private void LoadBlueprintDefaultData(BaseBuildingBlueprint blueprint)
	{
		if (this.blueprint == null)
		{
			Log.Error("Blueprint was null when trying to cache data.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
			return;
		}
		currentThermalModel = blueprint.DefaultThermalModel;
		pathfindingPenalty = blueprint.PathfindingPenalty;
		walkSpeedMultiplier = blueprint.WalkSpeedMultiplier;
		combatCover = blueprint.Cover;
	}

	private void CheckHealth()
	{
		StatInstance statInstance = Stats?.GetStat(StatType.Health);
		if (statInstance != null)
		{
			if (setMaxHealth)
			{
				statInstance.SetCurrent(statInstance.Max);
				setMaxHealth = false;
			}
			base.Map.BuildingsManagerMain.OnBuildingHealthStatUpdated(this, statInstance.Current, statInstance.Max);
		}
	}

	internal void SelectBuilding()
	{
		this.SelectBuildingEvent?.Invoke();
	}

	public bool EligibleForEvent()
	{
		if (constructionPhase == ConstructionPhase.Finished && !MarkedForDestruction)
		{
			return Blueprint.PlayerTriggeredEvents.Count > 0;
		}
		return false;
	}

	public void SetMovableBuildingResourceInstance(MovableBuildingPileInstance movableBuildingPileInstance)
	{
		this.movableBuildingPileInstance = movableBuildingPileInstance;
	}

	public void OverrideDefaultConstructionCost(Resource blueprint, int amount)
	{
		if (blueprint == null)
		{
			Log.Error("Resource blueprint for overriding default building construction cost is NULL.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
			return;
		}
		constructionCost = new Dictionary<string, int>();
		constructionCost.Add(blueprint.GetID(), amount);
	}

	public void AutoConstructSequence()
	{
		MonoSingleton<TaskController>.Instance.WaitForNextFrameUnscaled().Then(delegate
		{
			EnterFoundationState();
			EnterFinishedState();
		});
	}

	public HashSet<Vec3Int> GetSurroundingPositions()
	{
		return Singleton<GridTools>.Instance.GetSurroundingPositions(base.GridDataPosition, base.Size, base.Angle);
	}

	private void InitializeMeshVariations()
	{
		if (MonoSingleton<MoveBuildingsManager>.IsInstantiated())
		{
			IReadOnlyList<string> movedObjectMeshVariations = MonoSingleton<MoveBuildingsManager>.Instance.GetMovedObjectMeshVariations();
			if (movedObjectMeshVariations != null && movedObjectMeshVariations.Count > 0)
			{
				ApplyMeshVariations(movedObjectMeshVariations);
				return;
			}
		}
		List<string> list = new List<string>();
		if (blueprint.DefaultVariations != null && blueprint.DefaultVariations.Count > 0)
		{
			list.AddRangeUnique(blueprint.DefaultVariations);
		}
		else
		{
			foreach (MeshVariationList variationList in blueprint.VariationLists)
			{
				if (variationList?.Variations != null && variationList.Variations.Count > 0 && variationList.Variations[0] != null)
				{
					MeshVariation meshVariation = variationList.Variations[0];
					if (variationList.IsRandom)
					{
						meshVariation = variationList.Variations.PickRandom();
					}
					list.Add(meshVariation.Name);
				}
			}
		}
		ApplyMeshVariations(list);
	}

	public void ComponentReservationChanged(IGoapAgentOwner agent, BaseComponentInstance reservedComponent, bool reserved)
	{
		if (reserved)
		{
			MonoSingleton<ReservationManager>.Instance.TryReserveObject(this, agent);
		}
		else
		{
			MonoSingleton<ReservationManager>.Instance.ReleaseObject(this, agent);
		}
	}

	public void SetIsMoved(bool isMoved)
	{
		isMoveBlueprint = isMoved;
	}

	public bool UnderWater()
	{
		return underWater;
	}

	public void SetMarkedForDestruction(bool value)
	{
		if (OwnedByPlayer())
		{
			DebugEventLog.Write(new OrderIssued(this, OrderType.Deconstructing));
			markedForDestruction = value;
			if (value)
			{
				CancelUninstall();
				base.Map.BuildingsManagerMain.ConstructionJobManager.CreateDeconstructJob(this);
			}
			else
			{
				base.Map.BuildingsManagerMain.ConstructionJobManager.RemoveDeconstructJobs(this);
			}
		}
	}

	public void CancelUninstall()
	{
		if (MarkedForMoving)
		{
			MonoSingleton<MoveBuildingsManager>.Instance.MoveCanceledFromSource(this);
		}
		SetIsMarkedForUninstall(markedForUninstall: false);
		SetIsMarkedForMoving(markedForMoving: false);
		MonoSingleton<ConstructablesGoapUninstallManager>.Instance.RemoveFromUninstallList(this);
	}

	public float GetAngle()
	{
		return base.Angle;
	}

	public void AddToMeshRotation(float angle, bool updateView = true)
	{
		rotateMeshVariation += angle;
		if (rotateMeshVariation < 0f)
		{
			rotateMeshVariation += 360f;
		}
		if (rotateMeshVariation >= 360f)
		{
			rotateMeshVariation %= 360f;
		}
		if (updateView)
		{
			this.BuildingMeshVariationRotatedEvent?.Invoke();
		}
	}

	public void ForceMeshVariationRotatedUpdateView()
	{
		this.BuildingMeshVariationRotatedEvent?.Invoke();
	}

	public void SetMeshRotation(float angle)
	{
		rotateMeshVariation = angle;
		if (rotateMeshVariation < 0f)
		{
			rotateMeshVariation += 360f;
		}
		if (rotateMeshVariation >= 360f)
		{
			rotateMeshVariation %= 360f;
		}
		this.BuildingMeshVariationRotatedEvent?.Invoke();
	}

	public void MeshVariationFlipX()
	{
		flipXMeshVariation = !flipXMeshVariation;
		this.BuildingMeshVariationFlippedEvent?.Invoke();
	}

	public void SetMeshVariationFlipZ(bool value)
	{
		flipZMeshVariation = value;
		this.BuildingMeshVariationFlippedEvent?.Invoke();
	}

	public void MeshVariationFlipZ()
	{
		flipZMeshVariation = !flipZMeshVariation;
		this.BuildingMeshVariationFlippedEvent?.Invoke();
	}

	public void ToggleAutomaticMeshVariationLoading(bool toggleValue)
	{
		if (!(blueprint == null) && !blueprint.HideAutomaticMeshCheckbox)
		{
			automaticMeshVariationLoading = toggleValue;
		}
	}

	public void ApplyMeshVariations(IReadOnlyList<string> variations)
	{
		variationsApplied.Clear();
		foreach (string variation in variations)
		{
			AddToVariationsAppliedSorted(variation);
		}
		materialVariationsAppliedRefresh = true;
		this.BuildingMeshVariationSetEvent?.Invoke();
	}

	private void AddToVariationsAppliedSorted(string variation)
	{
		int num = variationsApplied.BinarySearch(variation);
		if (num < 0)
		{
			num = ~num;
		}
		variationsApplied.Insert(num, variation);
	}

	public void ApplyMeshVariation(MeshVariation variation)
	{
		if (variation == null)
		{
			Log.Info("MeshVariation is null while calling ApplyMeshVariation.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
		}
		else if (base.HasDisposed)
		{
			Log.Info("BaseBuildingInstance is disposed while calling ApplyMeshVariation.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
		}
		else if (variationsApplied == null)
		{
			Log.Info("BaseBuildingInstance.variationsApplied is null while calling ApplyMeshVariation.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
		}
		else
		{
			if (variationsApplied.Contains(variation.Name))
			{
				return;
			}
			AddToVariationsAppliedSorted(variation.Name);
			if (variation.HasTextureSlots)
			{
				materialVariationsAppliedRefresh = true;
			}
			if (blueprint.VariationLists[0].ContainsVariation(variation.Name))
			{
				if (CurrentMeshVariation != variation.Name)
				{
					variationsApplied.Remove(CurrentMeshVariation);
				}
				CurrentMeshVariation = variation.Name;
			}
			this.BuildingMeshVariationSetEvent?.Invoke();
		}
	}

	public bool IsMeshVariationApplied(MeshVariation meshVariation)
	{
		return variationsApplied.Contains(meshVariation.Name);
	}

	public bool IsMeshVariationApplied(string meshVariationId)
	{
		return variationsApplied.Contains(meshVariationId);
	}

	public void RemoveMeshVariation(MeshVariationList fromList)
	{
		if (variationsApplied.RemoveAll(fromList.ContainsVariation) > 0)
		{
			materialVariationsAppliedRefresh = true;
		}
	}

	public override float GetBeautyInput()
	{
		if (Blueprint != null)
		{
			if (Positions.Count <= 1)
			{
				return Blueprint.BeautyInput;
			}
			return Blueprint.BeautyInput / (float)Positions.Count;
		}
		return 0f;
	}

	public override bool BeautyBlocker()
	{
		if (Blueprint != null)
		{
			if ((BuildingType & (BuildingType.Window | BuildingType.Door)) != 0)
			{
				return lockState != LockState.AlwaysOpen;
			}
			return Blueprint.BeautyBlocker;
		}
		return false;
	}

	public void SetPlacedOnBeam()
	{
		placedOnBeam = true;
	}

	public void SetupVoxelHolderData(Vec3Int voxelHolderPosition, ObjectSide objectSide)
	{
		if (this.objectSide == ObjectSide.None)
		{
			this.voxelHolderPosition = voxelHolderPosition;
			this.objectSide = objectSide;
		}
	}

	public void SetAttachedToSocketComponent(bool attachedToSocketComponent)
	{
		this.attachedToSocketComponent = attachedToSocketComponent;
	}

	private void OnBeamDestroyed(BeamComponentInstance beamInstance)
	{
		if (placedOnBeam)
		{
			List<Vec3Int> list = beamInstance.Positions;
			Vec3Int a = base.GridDataPosition;
			Vec3Int b = Vec3Int.down;
			if (list.Contains(a + b))
			{
				placedOnBeam = false;
			}
		}
	}

	private void OnBeamPlaced(BeamComponentInstance beamInstance)
	{
		List<Vec3Int> list = beamInstance.Positions;
		Vec3Int a = base.GridDataPosition;
		Vec3Int b = Vec3Int.down;
		if (list.Contains(a + b))
		{
			placedOnBeam = true;
		}
	}

	private void OnBeamConstructed(BeamComponentInstance beamInstance)
	{
		List<Vec3Int> list = beamInstance.Positions;
		Vec3Int a = base.GridDataPosition;
		Vec3Int b = Vec3Int.down;
		if (list.Contains(a + b))
		{
			placedOnBeam = true;
		}
	}

	public void ObjectPlacedOnMap(bool afterLoading = false)
	{
		bool isEnabled;
		if (!afterLoading)
		{
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(17, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Building placed: ");
				messageBuilder.AppendFormatted(this);
			}
			Log.Info(messageBuilder);
		}
		LoadConstructionCost(blueprint);
		SetConstructionPhase((!afterLoading) ? ConstructionPhase.Blueprint : constructionPhase);
		HasStabilityToBuild = base.Map.BuildingsManagerMain.HasStabilityToBuild(this);
		this.RequestHasStabilityToBuildEvent?.Invoke();
		if (blueprint.CanPlaceOnBeam)
		{
			base.Map.BeamComponentManager.BeamDestroyedEvent += new Action<BeamComponentInstance>(OnBeamDestroyed);
			base.Map.BeamComponentManager.BeamConstructedEvent += new Action<BeamComponentInstance>(OnBeamConstructed);
			base.Map.BeamComponentManager.BeamPlacedEvent += new Action<BeamComponentInstance>(OnBeamPlaced);
			BeamComponentManager beamComponentManager = base.Map.BeamComponentManager;
			Vec3Int a = base.GridDataPosition;
			Vec3Int b = Vec3Int.down;
			if (beamComponentManager.BeamExists(a + b, onlyFinished: false))
			{
				placedOnBeam = true;
			}
		}
		UpdateBuildingReachability();
		this.ObjectPlacedOnMapEvent?.Invoke();
		if (!blueprint.Socketable && !TutorialManager.IsTutorialActive)
		{
			foreach (Vec3Int position in positions)
			{
				if (!VillageManager.ActiveVillage.Map.ResourceExists(position))
				{
					continue;
				}
				if (base.FactionOwnership == FactionOwnership.Player)
				{
					FVLogTraceInterpolationHandler messageBuilder2 = new FVLogTraceInterpolationHandler(12, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
					if (isEnabled)
					{
						messageBuilder2.AppendLiteral("Force Order ");
						messageBuilder2.AppendFormatted(position);
					}
					Log.Trace(messageBuilder2);
					MonoSingleton<SelectionManager>.Instance.ForceOrderOnResource(position);
				}
				else
				{
					base.Map.EnemyBuildingsManager.CachePlantToChop(MonoSingleton<PlantResourceManager>.Instance.GetPlant(position));
				}
			}
		}
		CalculateReachabilityOptimizedCall();
		base.Map.BuildingsManagerMain.TryReplaceBuilding(this);
		if (!afterLoading && base.FactionOwnership == FactionOwnership.Enemy && !isMoveBlueprint)
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrameUnscaled().Then(delegate
			{
				EnterFoundationState();
			});
		}
	}

	public void AfterViewObjectPlacedOnMap(bool afterLoading = false)
	{
		StringIntDictionary materials = blueprint.Materials;
		if (materials != null && materials.Dictionary?.Count == 0)
		{
			MonoSingleton<ConstructionController>.Instance.BlueprintPlaced(this, MonoSingleton<BuildingPlacementManager>.Instance.MoveBuilding, afterLoading);
			EnterFoundationState(afterLoading);
			base.Map.AddToTheWorld(this);
		}
	}

	public void UpdateBuildingReachability()
	{
		IntRange intRange = null;
		WorldDirection worldDirection = WorldDirection.AllHorizontal;
		switch (constructionPhase)
		{
		case ConstructionPhase.Preview:
		case ConstructionPhase.Blueprint:
		case ConstructionPhase.Foundation:
		{
			IntRange intRange2;
			switch (blueprint.ConstructableBaseCategory)
			{
			case ConstructableBaseCategory.Building:
			case ConstructableBaseCategory.Stairs:
			case ConstructableBaseCategory.Roof:
				intRange2 = new IntRange(-1, 1);
				break;
			case ConstructableBaseCategory.Beam:
				intRange2 = new IntRange(0, 1);
				break;
			default:
				intRange2 = new IntRange(0, 0);
				break;
			}
			intRange = intRange2;
			break;
		}
		case ConstructionPhase.Finished:
		{
			int max = 0;
			if ((BuildingType & (BuildingType.Wall | BuildingType.Stairs | BuildingType.Ladder)) != 0)
			{
				max = 1;
			}
			ConstructableBaseCategory constructableBaseCategory = blueprint.ConstructableBaseCategory;
			intRange = ((constructableBaseCategory != ConstructableBaseCategory.Building && constructableBaseCategory != ConstructableBaseCategory.Stairs && constructableBaseCategory != ConstructableBaseCategory.Roof) ? ((blueprint.ConstructableBaseCategory != ConstructableBaseCategory.Beam) ? new IntRange(0, 0) : new IntRange(0, 1)) : new IntRange(-1, max));
			break;
		}
		}
		if (intRange == null)
		{
			Debug.LogWarning("IntRange reachLevel is null. This should never happen!");
			intRange = new IntRange(0, 0);
		}
		if (base.ReachabilityInfo?.YRange == intRange)
		{
			ReachabilityInfo obj = base.ReachabilityInfo;
			if (obj != null && obj.GetReachability() == worldDirection)
			{
				return;
			}
		}
		SetReachability(new ReachabilityInfo(intRange, worldDirection));
	}

	public void LoadConstructionCost(BaseBuildingBlueprint blueprint)
	{
		constructionCost = blueprint.Materials.Dictionary;
	}

	public void ReturnToBlueprint()
	{
		storage.ClearAll();
		showFoundation = false;
		remainingTime = buildTime;
		foreach (KeyValuePair<string, int> item in constructionCost)
		{
			storage.Add(new ResourceInstance(Repository<ResourceRepository, Resource>.Instance.GetByID(item.Key), 0));
		}
		SetConstructionPhase(ConstructionPhase.Blueprint);
		MonoSingleton<ConstructionController>.Instance.BlueprintPlaced(this);
		this.ReturnToBlueprintEvent?.Invoke();
		DebugEventLog.Write(new BuildingPhaseChanged(this));
	}

	public void EnterFoundationState(bool afterLoading = false)
	{
		if (afterLoading || constructionPhase != ConstructionPhase.Finished)
		{
			if (afterLoading)
			{
				this.BaseBuildingEnterFoundationStateEvent?.Invoke(obj: true);
				this.BaseBuildingEnterFoundationStateEvent = null;
				return;
			}
			UpdateBuildingReachability();
			SetConstructionPhase(ConstructionPhase.Foundation);
			this.BaseBuildingEnterFoundationStateEvent?.Invoke(obj: false);
			this.BaseBuildingEnterFoundationStateEvent = null;
			MonoSingleton<ConstructionController>.Instance.ConstructionMaterialsDelivered(this);
			DebugEventLog.Write(new BuildingPhaseChanged(this));
		}
	}

	public void EnterFinishedState(bool afterLoading = false)
	{
		bool isEnabled;
		FVLogTraceInterpolationHandler messageBuilder = new FVLogTraceInterpolationHandler(19, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("EnterFinishedState ");
			messageBuilder.AppendFormatted(this);
		}
		Log.Trace(messageBuilder);
		if (afterLoading)
		{
			InitStats();
			base.Map.BuildingsManagerMain.BuildingConstructionCompleted(this);
			if (blueprint.CanHaveOwner && buildingOwnershipInfo == null)
			{
				buildingOwnershipInfo = new BuildingOwnershipInfo(base.Map);
			}
			protectingAgainstPredators = blueprint.PassivePredatorProtection;
			this.BaseBuildingEnterFinishedStateEvent?.Invoke(obj: true);
			this.BaseBuildingEnterFinishedStateEvent = null;
			base.Map?.ProtectorBuildingManager?.BuildingLoaded(this);
			return;
		}
		FVLogInfoInterpolationHandler messageBuilder2 = new FVLogInfoInterpolationHandler(15, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
		if (isEnabled)
		{
			messageBuilder2.AppendLiteral("EnterFinished: ");
			messageBuilder2.AppendFormatted(this);
		}
		Log.Info(messageBuilder2);
		SetConstructionPhase(ConstructionPhase.Finished);
		RefreshCoverage();
		UpdateBuildingReachability();
		using PooledList<WorldObject> pooledList = base.Map.BuildingsManagerMain.GetNeighboursForReachabilityUpdate(this);
		for (int i = 0; i < pooledList.Count; i++)
		{
			pooledList[i].IsReachabilityUpdateInProgress = true;
		}
		MonoSingleton<TaskController>.Instance.WaitForNextFrameUnscaled().Then(delegate
		{
			base.Map.BuildingsManagerMain.RecalculateReachabilityForNeighbors(this);
		});
		if (blueprint.CanHaveOwner && buildingOwnershipInfo == null)
		{
			buildingOwnershipInfo = new BuildingOwnershipInfo(base.Map);
		}
		this.BaseBuildingEnterFinishedStateEvent?.Invoke(obj: false);
		this.BaseBuildingEnterFinishedStateEvent = null;
		MonoSingleton<ConstructionController>.Instance.ConstructionCompleted(this);
		base.Map.BuildingsManagerMain.TryReplaceBuilding(this);
		base.Map.StabilityManager.BuildingConstructed(this);
		if (storage != null)
		{
			storage.DisposeAllResources();
			storage.Dispose();
		}
		storage = null;
		WaterLevelChanged();
		DebugEventLog.Write(new BuildingPhaseChanged(this));
	}

	public void ConstructionPaused()
	{
		this.ConstructionPausedEvent?.Invoke();
	}

	public void ConstructionStarted()
	{
		this.ConstructionStartedEvent?.Invoke();
	}

	public void ConstructionFailed()
	{
		if (!base.HasDisposed)
		{
			StringIntDictionary materials = blueprint.Materials;
			if (materials != null && materials.Dictionary?.Count == 0)
			{
				EnterFoundationState();
			}
			else
			{
				ReturnToBlueprint();
			}
		}
	}

	public void ConstructionCompleted()
	{
		orders = OrderType.None;
		EnterFinishedState();
		this.ConstructionCompletedEvent?.Invoke();
	}

	public void RemoveOrder(OrderType orderToRemove)
	{
		orders &= ~orderToRemove;
	}

	public void SetConstructionPhase(ConstructionPhase constructionPhase, bool autoConstruct = false)
	{
		bool num = this.constructionPhase != constructionPhase;
		this.constructionPhase = constructionPhase;
		if (constructionPhase == ConstructionPhase.Blueprint || autoConstruct)
		{
			base.Map.AddToTheWorld(this);
		}
		if (num && constructionPhase == ConstructionPhase.Foundation)
		{
			InitStats();
		}
		if (num && constructionPhase == ConstructionPhase.Finished)
		{
			stats?.Dispose();
			stats = null;
			InitStats();
			base.Map.BuildingsManagerMain.BuildingConstructionCompleted(this);
		}
		CalculateReachabilityOptimizedCall();
		base.GridDataType = WorldObjectTemporaryDataTypeSwitcher.GetWorldObjectDataType(this);
		if (!autoConstruct)
		{
			UpdateJobManager();
		}
	}

	private void UpdateJobManager()
	{
		ConstructionJobManager constructionJobManager = base.Map.BuildingsManagerMain.ConstructionJobManager;
		switch (constructionPhase)
		{
		case ConstructionPhase.Blueprint:
			constructionJobManager.RemoveConstructBuildingJob(this);
			if (isForbidden)
			{
				constructionJobManager.RemoveDeliverResourceJob(this);
			}
			else
			{
				constructionJobManager.CreateDeliverResourceJob(this);
			}
			break;
		case ConstructionPhase.Foundation:
			constructionJobManager.RemoveDeliverResourceJob(this);
			if (isForbidden)
			{
				constructionJobManager.RemoveConstructBuildingJob(this);
			}
			else
			{
				constructionJobManager.CreateConstructBuildingJob(this);
			}
			break;
		case ConstructionPhase.Finished:
			constructionJobManager.RemoveConstructBuildingJob(this);
			constructionJobManager.RemoveDeliverResourceJob(this);
			break;
		}
	}

	public bool HasConstructionMaterials()
	{
		if (MonoSingleton<BuildingPlacementManager>.Instance.ConstructWithoutResource)
		{
			return true;
		}
		foreach (KeyValuePair<string, int> item in constructionCost)
		{
			if (item.Value > (storage.GetById(item.Key)?.Amount ?? 0))
			{
				return false;
			}
		}
		return true;
	}

	public int GetRequiredAmount(Resource blueprint)
	{
		if (!constructionCost.TryGetValue(blueprint.GetID(), out var value))
		{
			return 0;
		}
		int num = storage?.GetTotalStoredCount(blueprint) ?? 0;
		if (num >= value)
		{
			return 0;
		}
		return value - num;
	}

	public int GetResourceOrderCount(Resource resource)
	{
		return constructionCost.GetValueOrDefault(resource.GetID());
	}

	public IEnumerable<SimpleResourceCount> GetResourceOrder(IPathfindingAgent agent)
	{
		return from item in constructionCost.Select(delegate(KeyValuePair<string, int> entry)
			{
				Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(entry.Key);
				if (agent != null)
				{
					if (!PathfinderUtil.IsPathPossible(agent, ReachablePositions))
					{
						return default(SimpleResourceCount);
					}
					HashSet<ResourcePileInstance> allPiles = MonoSingleton<ResourcePileManager>.Instance.GetAllPiles(byID);
					bool flag = false;
					foreach (ResourcePileInstance item in allPiles)
					{
						if (!item.IsForbidden && PathfinderUtil.IsPathPossible(agent, item) && !item.PlacedOnAnimalFeeder)
						{
							flag = true;
							break;
						}
					}
					if (!flag)
					{
						return default(SimpleResourceCount);
					}
				}
				int num = entry.Value - (storage.GetById(entry.Key)?.Amount ?? 0);
				return (num > 0) ? new SimpleResourceCount(byID, num) : default(SimpleResourceCount);
			})
			where !item.Equals(default(SimpleResourceCount))
			select item;
	}

	private void OnResourceAdded(SimpleResourceCount count)
	{
		if (!showFoundation)
		{
			showFoundation = true;
			MonoSingleton<ConstructionController>.Instance.ShowFoundation(this);
		}
		if (HasConstructionMaterials())
		{
			EnterFoundationState();
		}
		else
		{
			base.Map.BuildingsManagerMain.ResourceDeliveredRefreshBlueprint(this);
		}
		foreach (ResourceInstance resource in storage.Resources)
		{
			if (resource is MoveBuildingResourceInstance moveBuildingResourceInstance)
			{
				moveBuildingResourceInstance.SetTargetBuilding(null);
			}
		}
	}

	public void RefreshBuilding()
	{
		CalculateReachabilityOptimizedCall();
	}

	public void RefreshWalkableCollider()
	{
		this.RefreshWalkableColliderEvent?.Invoke();
	}

	public bool CanPlaceNavmeshAbove()
	{
		if (Blueprint.BuildingType == BuildingType.Floor)
		{
			return true;
		}
		Vec3Int a = base.GridDataPosition;
		Vec3Int b = Vec3Int.up;
		Vec3Int vec3Int = a + b;
		if (!MonoSingleton<GroundManager>.Instance.GroundExists(vec3Int))
		{
			return !base.Map.BuildingsManagerMain.BuildingVerticalStabilityCarrierExits(vec3Int);
		}
		return false;
	}

	private void InitStats()
	{
		if (stats == null)
		{
			stats = BuildingStatsProducer.ProduceBuildingStats(this, null);
		}
		else
		{
			BuildingStatsProducer.ProduceBuildingStats(this, stats);
			stats.SetOwner(this);
			stats.SetOwnerOnStats();
		}
		stats.Initialize();
		stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Health, OnHealthDepleted);
		stats.Controller.RegisterListener(StatEventType.ValueUpdated, StatType.Health, OnHealthUpdated);
		MonoSingleton<CombatAgentManager>.Instance.RegisterCommonCombatAgent(this);
	}

	private void OnHealthDepleted(object data)
	{
		HealthDepleted = true;
		BuildingRemovedSpawnResources(base.WorldPosition, BuildingResourceSpawnType.Destroyed);
		base.Map.BuildingsManagerMain.DestroyBuilding(this);
	}

	private void OnHealthUpdated(object data)
	{
		StatInstance statInstance = (StatInstance)data;
		if (!(statInstance.Current <= 0.1f))
		{
			base.Map.BuildingsManagerMain.OnBuildingHealthStatUpdated(this, statInstance.Current, statInstance.Max);
			this.BuildingHealthUpdatedEvent?.Invoke(statInstance);
		}
	}

	public void Repair(StatInstance healthStat, float statIncrement)
	{
		healthStat.SetCurrent(healthStat.Current + statIncrement);
		this.BuildingRepairingTickEvent?.Invoke(healthStat);
	}

	public void SetProducerUniqueId(int producerUniqueId)
	{
		if (this.producerUniqueId == 0)
		{
			this.producerUniqueId = producerUniqueId;
		}
	}

	public void SetPositions()
	{
		if (Blueprint.BuildingType.Equals(BuildingType.Beam) || Blueprint.BuildingType.Equals(BuildingType.Roof))
		{
			return;
		}
		if (base.Size.Equals(Vec3Int.one) && base.WorldPosition != Vector3.zero && !positions.Contains(base.GridDataPosition))
		{
			positions.Add(base.GridDataPosition);
			return;
		}
		using PooledList<Vec3Int> pooledList = Singleton<GridTools>.Instance.GetPositionsJanitor(base.GridDataPosition, base.Size, base.Angle);
		positions.Clear();
		positions.AddRange(pooledList);
	}

	public string GetProducerName()
	{
		return HumanoidUtils.GetProducerName(producerUniqueId);
	}

	public string GetBuildingName()
	{
		return BuildingUtils.GetLocalizedName(base.BlueprintId);
	}

	public string GetBuildingPhase()
	{
		string result = string.Empty;
		if (constructionPhase < ConstructionPhase.Finished)
		{
			result = MonoSingleton<LocalizationController>.Instance.GetText($"building_phase_{(int)constructionPhase}");
		}
		return result;
	}

	public void SetIsMarkedForUninstall(bool markedForUninstall)
	{
		this.markedForUninstall = markedForUninstall;
		if (this.markedForUninstall)
		{
			SetMarkedForDestruction(value: false);
		}
	}

	public void SetIsMarkedForMoving(bool markedForMoving)
	{
		this.markedForMoving = markedForMoving;
		if (this.markedForMoving)
		{
			SetMarkedForDestruction(value: false);
		}
	}

	public bool IsBlueprintOnClearNode()
	{
		if (Blueprint.PlacementType == PlacementType.WallSocket)
		{
			return true;
		}
		foreach (Vec3Int position in positions)
		{
			Vec3Int gridPosition = position;
			MapNode node = base.Map.GetNode(in gridPosition);
			if (node != null && node.DataType.HasFlag(GridDataType.PlantMapResource))
			{
				return false;
			}
		}
		return true;
	}

	public void ForEveryGridSpace(Func<Vec3Int, bool> callback)
	{
		foreach (Vec3Int position in positions)
		{
			if (!callback(position))
			{
				break;
			}
		}
	}

	public float GetRemainingBuildTimeRelativeToStartBuildTime()
	{
		if (totalWorkerBuildTime <= 0f)
		{
			return buildTime;
		}
		float num = (totalWorkerBuildTime - remainingTime) / totalWorkerBuildTime * 100f;
		float num2 = buildTime / 100f * num;
		return buildTime - num2;
	}

	public void SetTotalWorkerBuildTime(float totalWorkerBuildTime)
	{
		this.totalWorkerBuildTime = totalWorkerBuildTime;
	}

	public void PlayBuildParticles()
	{
		this.TriggerBuildParticlesEvent?.Invoke();
	}

	public void SetRemainingTime(float remainingTime)
	{
		this.remainingTime = remainingTime;
		this.TimeRemainingEvent?.Invoke(this.remainingTime);
	}

	public void RefreshCoverage()
	{
		if (LoadingController.IsLeavingMainScene)
		{
			return;
		}
		foreach (Vec3Int position in Positions)
		{
			MonoSingleton<Heightmap>.Instance.EnqueueModifiedPosition(position.x, position.z);
		}
	}

	public override void Dispose()
	{
		if (base.HasDisposed)
		{
			return;
		}
		if (blueprint.BuildingType == BuildingType.Floor)
		{
			FireSimLogic fireSimLogic = base.Map.FireSimLogic;
			foreach (Vec3Int position in positions)
			{
				Vec3Int gridPosition = position;
				int index = base.Map.GetNode(in gridPosition).Index;
				fireSimLogic?.SetOilBlobHealth(index, 0f, 0);
				fireSimLogic?.SetOilBlobHealth(index, 0f, 1);
			}
		}
		this.ConstructionStartedEvent = null;
		this.ConstructionPausedEvent = null;
		this.ReturnToBlueprintEvent = null;
		this.BuildingHealthUpdatedEvent = null;
		this.BaseBuildingEnterFinishedStateEvent = null;
		this.BuildingMeshVariationRotatedEvent = null;
		this.StabilityUpdatedRefreshVisualsEvent = null;
		this.SelectBuildingEvent = null;
		this.BuildingRepairingTickEvent = null;
		this.RefreshWalkableColliderEvent = null;
		this.RequestHasStabilityToBuildEvent = null;
		this.ReachabilityChangedEvent = null;
		this.PileForbidChangedColorBlueprintEvent = null;
		base.Map.BuildingsManagerMain.ConstructionJobManager.RemoveAllJobs(this);
		eventStorage?.ClearEventStorage(base.GridDataPosition);
		eventStorage?.Dispose();
		eventStorage = null;
		MonoSingleton<MoveBuildingsManager>.Instance.BlueprintCanceled(this);
		if (storage != null)
		{
			foreach (ResourceInstance resource in storage.Resources)
			{
				if (resource is MoveBuildingResourceInstance moveBuildingResourceInstance)
				{
					moveBuildingResourceInstance.SetTargetBuilding(null);
				}
			}
		}
		this.DisposeComponentsEvent?.Invoke();
		this.DisposeComponentsEvent = null;
		base.Map.BeamComponentManager.BeamDestroyedEvent -= new Action<BeamComponentInstance>(OnBeamDestroyed);
		base.Map.BeamComponentManager.BeamConstructedEvent -= new Action<BeamComponentInstance>(OnBeamConstructed);
		base.Map.BeamComponentManager.BeamPlacedEvent -= new Action<BeamComponentInstance>(OnBeamPlaced);
		MonoSingleton<CombatAgentManager>.Instance.RemoveCommonCombatAgent(this);
		RemoveEvents();
		DestroyProgressBar(OverlayProgressBarType.Last);
		RefreshCoverage();
		base.Dispose();
		if (objectSide != ObjectSide.None && MonoSingleton<GroundManager>.IsInstantiated())
		{
			MonoSingleton<GroundManager>.Instance.RemoveFromVoxelSocket(voxelHolderPosition, objectSide);
		}
		stats?.Dispose();
		stats = null;
		buildingOwnershipInfo?.Dispose();
		buildingOwnershipInfo = null;
		this.BuildingMeshVariationFlippedEvent = null;
		this.WaterLevelChangedEvent = null;
		this.ForbidChangeEvent = null;
		this.ForbidStateWillChangeEvent = null;
		this.ObjectPlacedOnMapEvent = null;
		this.BaseBuildingEnterFoundationStateEvent = null;
		this.ConstructionCompletedEvent = null;
		this.BuildingMeshVariationSetEvent = null;
		this.MainBuildingStabilityChangedEvent = null;
		this.RefreshRoomChangedEvent = null;
		this.DisposeComponentsEvent = null;
		this.TimeRemainingEvent = null;
		this.TriggerBuildParticlesEvent = null;
	}

	public void RemoveEvents()
	{
		if (onGroundDestroyWaitForNavmeshUpdateTask != null)
		{
			onGroundDestroyWaitForNavmeshUpdateTask.Stop();
			onGroundDestroyWaitForNavmeshUpdateTask = null;
		}
	}

	public void DestroyProgressBar(OverlayProgressBarType type)
	{
		if (progressBar == null)
		{
			return;
		}
		if (type == OverlayProgressBarType.Last)
		{
			for (int i = 0; i < progressBar.Length; i++)
			{
				if (!(progressBar[i] == null))
				{
					progressBar[i].Dispose();
					progressBar[i] = null;
				}
			}
		}
		else if (progressBar[(int)type] != null)
		{
			progressBar[(int)type].Dispose();
			progressBar[(int)type] = null;
		}
	}

	public string GetLocalizedBlockerPile()
	{
		VillageMap map = base.Village.Map;
		Vec3Int gridPosition = base.GridDataPosition;
		foreach (WorldObject worldObject in map.GetNode(in gridPosition).WorldObjects)
		{
			if (worldObject is ResourcePileInstance resourcePileInstance)
			{
				return MonoSingleton<LocalizationController>.Instance.GetText(LocKeyUtils.GetName(resourcePileInstance.Blueprint.LocKeys));
			}
		}
		return string.Empty;
	}

	public void CalculateReachabilityTemp(Func<MapNode, bool> additionalCheck = null)
	{
		CalculateReachabilityOptimizedCall(additionalCheck);
	}

	protected override void CalculateReachability(Func<MapNode, bool> additionalCheck = null)
	{
		base.CalculateReachability(BuildingCheck);
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "ReachabilityChangedEvent", delegate
		{
			this.ReachabilityChangedEvent?.Invoke();
		});
		if (constructionPhase == ConstructionPhase.Blueprint && MonoSingleton<World>.Instance.IsLoaded)
		{
			base.Map.BuildingsManagerMain.WorldStateChangedRefreshBuilding(this);
		}
		bool BuildingCheck(MapNode node)
		{
			if (node == null)
			{
				return false;
			}
			if ((BuildingType & (BuildingType.Floor | BuildingType.Rug)) != 0 && node.Position.y > base.GridDataPosition.y)
			{
				return false;
			}
			if ((node.DataType & GridDataType.BuildingFinished) != 0 && node.GetWorldObject(GridDataType.BuildingFinished) is BaseBuildingInstance baseBuildingInstance)
			{
				return (baseBuildingInstance.BuildingType & (BuildingType.Floor | BuildingType.Door | BuildingType.Merlon | BuildingType.Ladder)) != 0;
			}
			MapNode nodeBelow = node.GetNodeBelow();
			if (nodeBelow != null)
			{
				Vec3Int rhs = nodeBelow.Position;
				if ((nodeBelow.DataType & GridDataType.BuildingFinished) != 0)
				{
					if (nodeBelow.GetWorldObject(GridDataType.BuildingFinished) is BaseBuildingInstance baseBuildingInstance2)
					{
						return (baseBuildingInstance2.BuildingType & (BuildingType.Wall | BuildingType.Door | BuildingType.BarnDoor | BuildingType.Ladder)) != 0;
					}
					return false;
				}
				if ((nodeBelow.DataType & GridDataType.Stairs) != 0)
				{
					StairsComponentInstance componentInstance = nodeBelow.Map.StairsComponentManager.GetComponentInstance(rhs);
					if (componentInstance != null && !componentInstance.HasDisposed)
					{
						Vec3Int lhs = componentInstance.GridDataPosition;
						return lhs == rhs;
					}
				}
				if ((nodeBelow.DataType & GridDataType.Slope) != 0)
				{
					SlopeInstance slopeAtPosition = MonoSingleton<SlopeManager>.Instance.GetSlopeAtPosition(rhs);
					if (slopeAtPosition != null && !slopeAtPosition.HasDisposed)
					{
						Vec3Int lhs = slopeAtPosition.GridDataPosition;
						return lhs == rhs;
					}
				}
				if (nodeBelow.IsWater && nodeBelow.IsWalkable)
				{
					return true;
				}
				if (nodeBelow.VoxelType == null)
				{
					return false;
				}
			}
			if (additionalCheck != null)
			{
				return additionalCheck(node);
			}
			return true;
		}
	}

	public void SetHasStabilityToBuild(bool value)
	{
		HasStabilityToBuild = value;
	}

	public void SetStability(int stability)
	{
		this.stability = stability;
		if (blueprint.BuildingType == BuildingType.Beam)
		{
			this.MainBuildingStabilityChangedEvent?.Invoke();
		}
		else
		{
			base.Map.SocketComponentManager.GetSocketComponentInstance(this)?.UpdateBeamStability(stability);
		}
	}

	public void RefreshHasStabilityToBuildAndReachability()
	{
		if (ConstructionPhase != ConstructionPhase.Finished)
		{
			HasStabilityToBuild = base.Map.BuildingsManagerMain.HasStabilityToBuild(this);
			this.RequestHasStabilityToBuildEvent?.Invoke();
		}
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "UpdateBuildingReachability", UpdateBuildingReachability, 2f);
		this.StabilityUpdatedRefreshVisualsEvent?.Invoke();
	}

	public void OverrideThermalModel(ThermalModel newThermalModel)
	{
		currentThermalModel = newThermalModel;
	}

	public void LoadDefaultThermalModel()
	{
		currentThermalModel = Blueprint.DefaultThermalModel;
	}

	public void ForceRefreshTemperatureInput()
	{
		if (base.Map == null || Blueprint == null || Blueprint.DefaultThermalModel == null || Positions == null)
		{
			return;
		}
		foreach (Vec3Int position in Positions)
		{
			Vec3Int gridPosition = position;
			base.Map.GetNode(in gridPosition)?.ForceRefreshTemperatureInput();
		}
	}

	public void OverrideLockState(LockState lockState)
	{
		this.lockState = lockState;
	}

	public ushort GetPathfindingPenalty()
	{
		if (constructionPhase == ConstructionPhase.Blueprint || constructionPhase == ConstructionPhase.Preview)
		{
			return 0;
		}
		if (constructionPhase == ConstructionPhase.Foundation)
		{
			return Blueprint.PathfindingPenaltyConstruction;
		}
		return pathfindingPenalty;
	}

	public void OnCreaturePassedThrough(CreatureBase creatureBase)
	{
		if (ConstructionPhase != ConstructionPhase.Finished)
		{
			return;
		}
		FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(24, 3, out var isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendFormatted(creatureBase);
			messageBuilder.AppendLiteral(" Passed Through ");
			messageBuilder.AppendFormatted(blueprint.GetID());
			messageBuilder.AppendLiteral(" phase: ");
			messageBuilder.AppendFormatted(ConstructionPhase);
		}
		Log.Debug(messageBuilder);
		if (!Blueprint.PassThroughDestroyable)
		{
			Log.Debug("Building is not PassThroughDestroyable", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
			return;
		}
		float buildablePassThroughDestroyChance = creatureBase.GetBuildablePassThroughDestroyChance();
		float num = UnityEngine.Random.Range(0f, 1f);
		messageBuilder = new FVLogDebugInterpolationHandler(22, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Destroy Chance ");
			messageBuilder.AppendFormatted(num);
			messageBuilder.AppendLiteral(" >= ");
			messageBuilder.AppendFormatted(buildablePassThroughDestroyChance);
			messageBuilder.AppendLiteral(" - ");
			messageBuilder.AppendFormatted(num >= buildablePassThroughDestroyChance);
		}
		Log.Debug(messageBuilder);
		if (!(num >= buildablePassThroughDestroyChance))
		{
			MonoSingleton<AnimalManager>.Instance.ScareOffAnimals(creatureBase.GetGridPosition(), 10f);
			MonoSingleton<ConstructionController>.Instance.PassThroughDestroyed(this);
			DestroyBuildingStabilityZero();
		}
	}

	public float GetSpeedMultiplier()
	{
		if (constructionPhase == ConstructionPhase.Blueprint || constructionPhase == ConstructionPhase.Preview)
		{
			return 0.85f;
		}
		if (constructionPhase == ConstructionPhase.Foundation)
		{
			return Blueprint.WalkSpeedMultiplierConstruction;
		}
		return walkSpeedMultiplier;
	}

	public float GetCover()
	{
		return combatCover;
	}

	public void GroundDestroyedRefreshReachability()
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "CalculateReachability", delegate
		{
			if (onGroundDestroyWaitForNavmeshUpdateTask != null)
			{
				onGroundDestroyWaitForNavmeshUpdateTask.Stop();
				onGroundDestroyWaitForNavmeshUpdateTask = null;
			}
			onGroundDestroyWaitForNavmeshUpdateTask = MonoSingleton<TaskController>.Instance.WaitFor(1f).Then(delegate
			{
				onGroundDestroyWaitForNavmeshUpdateTask = null;
				if (!base.HasDisposed)
				{
					CalculateReachability();
				}
			});
		});
	}

	public float GetWealth()
	{
		if (!(blueprint != null))
		{
			return 0f;
		}
		return blueprint.WealthPoints;
	}

	public void WaterLevelChanged()
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
		this.WaterLevelChangedEvent?.Invoke(waterDepthLevel);
	}

	public void SetUnderWater(bool underWater)
	{
		this.underWater = underWater;
	}

	public void SetMaxHealth()
	{
		setMaxHealth = true;
	}

	public void SetFire(Vec3Int agentPosition, float flameHealth)
	{
		Vec3Int pos = Positions.MinItem((Vec3Int position) => position.DistanceSquared(in agentPosition));
		base.Map.FireSimLogic.SetFireData(GridDataIndexTools.FastTo1DIndexNoCheck(pos), flameHealth);
	}

	public override string ToString()
	{
		return $"BuildingInstance '{blueprintId}' at {base.GridDataPosition}, uniqueId {base.UniqueId}";
	}

	public float GetAverageTemperature()
	{
		if (base.Map?.TemperatureManager == null)
		{
			return 0f;
		}
		if (positions == null || positions.Count == 0)
		{
			return base.Map.TemperatureManager.GetTemperature(base.GridDataPosition);
		}
		float num = 0f;
		foreach (Vec3Int position in positions)
		{
			num += base.Map.TemperatureManager.GetTemperature(position);
		}
		return num / (float)positions.Count;
	}

	public override void Serialize(FVSerializer serializer)
	{
		base.Serialize(serializer);
		if (constructionPhase != ConstructionPhase.Finished)
		{
			serializer.Write("storage", storage);
		}
		serializer.WriteEnum("buildingType", buildingType);
		serializer.WriteEnum("constructionPhase", constructionPhase);
		serializer.Write("remainingTime", remainingTime);
		serializer.Write("totalWorkerBuildTime", totalWorkerBuildTime);
		serializer.Write("stability", stability);
		serializer.Write("tempStability", tempStability);
		serializer.Write("showFoundation", showFoundation);
		serializer.Write("positions", positions);
		serializer.Write("stats", stats);
		serializer.Write("markedForDestruction", markedForDestruction);
		serializer.Write("isMoveBlueprint", isMoveBlueprint);
		serializer.Write("markedForUninstall", markedForUninstall);
		serializer.Write("markedForMoving", markedForMoving);
		serializer.WriteEnum("orders", orders);
		serializer.WriteEnum("lockState", lockState);
		serializer.Write("rotateMeshVariation", rotateMeshVariation);
		serializer.Write("flipXMeshVariation", flipXMeshVariation);
		serializer.Write("flipZMeshVariation", flipZMeshVariation);
		serializer.Write("reachable", reachable);
		serializer.Write("resourcesAvailable", resourcesAvailable);
		serializer.Write("producerUniqueId", producerUniqueId);
		serializer.Write("variationsApplied", variationsApplied);
		serializer.Write("buildingOwnershipInfo", buildingOwnershipInfo);
		serializer.Write("isForbidden", isForbidden);
		serializer.Write("placedOnBeam", placedOnBeam);
		serializer.WriteEnum("componentFlags", componentFlags);
		serializer.Write("playerEventStorage", eventStorage);
		serializer.Write("voxelHolderPosition", voxelHolderPosition);
		serializer.WriteEnum("objectSide", objectSide);
		serializer.Write("automaticMeshVariationLoading", automaticMeshVariationLoading);
		serializer.Write("CurrentMeshVariation", CurrentMeshVariation);
		serializer.Write("underWater", underWater);
	}

	public BaseBuildingInstance(FVDeserializer deserializer)
		: base(deserializer)
	{
		componentFlags = deserializer.ReadEnum("componentFlags", (BuildingType)0);
		buildingType = deserializer.ReadEnum("buildingType", (BuildingType)0);
		if ((buildingType & BuildingType.Stairs) != 0 && base.ReachabilityInfo != null && base.ReachabilityInfo.YRange.Max != 1)
		{
			SetReachabilityInfo(new ReachabilityInfo(new IntRange(base.ReachabilityInfo.YRange.Min, 1)));
		}
		if (!BlueprintLoaded())
		{
			return;
		}
		constructionPhase = deserializer.ReadEnum("constructionPhase", ConstructionPhase.Preview);
		remainingTime = deserializer.ReadFloat("remainingTime");
		totalWorkerBuildTime = deserializer.ReadFloat("totalWorkerBuildTime");
		stability = deserializer.ReadInt("stability");
		tempStability = deserializer.ReadInt("tempStability");
		showFoundation = deserializer.ReadBool("showFoundation");
		positions = deserializer.ReadObjectList<Vec3Int>("positions");
		stats = deserializer.ReadObject<StatsInstance>("stats");
		markedForDestruction = deserializer.ReadBool("markedForDestruction");
		isMoveBlueprint = deserializer.ReadBool("isMoveBlueprint");
		markedForUninstall = deserializer.ReadBool("markedForUninstall");
		markedForMoving = deserializer.ReadBool("markedForMoving");
		orders = (OrderType)deserializer.ReadInt("orders");
		lockState = (LockState)deserializer.ReadInt("lockState");
		rotateMeshVariation = deserializer.ReadFloat("rotateMeshVariation");
		flipXMeshVariation = deserializer.ReadBool("flipXMeshVariation");
		flipZMeshVariation = deserializer.ReadBool("flipZMeshVariation");
		reachable = deserializer.ReadBool("reachable");
		resourcesAvailable = deserializer.ReadBool("resourcesAvailable");
		producerUniqueId = deserializer.ReadInt("producerUniqueId");
		variationsApplied = deserializer.ReadStringList("variationsApplied");
		buildingOwnershipInfo = deserializer.ReadObject<BuildingOwnershipInfo>("buildingOwnershipInfo");
		isForbidden = deserializer.ReadBool("isForbidden");
		placedOnBeam = deserializer.ReadBool("placedOnBeam");
		objectSide = deserializer.ReadEnum("objectSide", ObjectSide.None);
		voxelHolderPosition = deserializer.ReadVec3Int("voxelHolderPosition", Vec3Int.zero);
		automaticMeshVariationLoading = deserializer.ReadBool("automaticMeshVariationLoading");
		CurrentMeshVariation = deserializer.ReadString("CurrentMeshVariation");
		underWater = deserializer.ReadBool("underWater");
		if (variationsApplied == null)
		{
			variationsApplied = new List<string>();
		}
		variationsApplied.Sort();
		if (constructionPhase != ConstructionPhase.Finished)
		{
			this.storage = deserializer.ReadObject<Storage>("storage");
			if (this.storage == null)
			{
				bool isEnabled;
				FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(75, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("this.storage was null for ");
					messageBuilder.AppendFormatted(blueprintId);
					messageBuilder.AppendLiteral(" during deserialization at position ");
					messageBuilder.AppendFormatted(base.GridDataPosition);
					messageBuilder.AppendLiteral("; faction is ");
					messageBuilder.AppendFormatted(base.FactionOwnership);
				}
				Log.Info(messageBuilder);
				this.storage = new Storage(new StorageBase(999, ignoreWeigth: true));
			}
		}
		LoadBlueprintDefaultData(blueprint);
		using PooledList<Vec3Int> buildingPositions = ListPool<Vec3Int>.GetJanitor(positions);
		using PooledList<Vec3Int> pooledList = Singleton<GridTools>.Instance.GetForbiddenPositions(blueprint, buildingPositions, base.GridDataPosition, base.Angle);
		forbiddenArea.AddRange(pooledList);
		Storage storage = deserializer.ReadObject<Storage>("eventStorage");
		if (storage != null)
		{
			eventStorage = new EventStorage(storage);
		}
		else
		{
			eventStorage = deserializer.ReadObject<EventStorage>("playerEventStorage");
		}
		if (buildingType == (BuildingType)0)
		{
			buildingType = blueprint.BuildingType;
		}
		if (eventStorage == null)
		{
			eventStorage = new EventStorage(blueprint.StorageBase.Capacity);
		}
		DefaultVariationsCheck();
		ApplyHiddenVariations();
		FixWrongMeshVariations();
		CacheCurrentVariation();
	}

	private void ApplyHiddenVariations()
	{
		foreach (MeshVariationList variationList in blueprint.VariationLists)
		{
			if (variationList.HideInUI && variationList.ApplyAllByDefault && variationList.Variations.Count > 0)
			{
				MeshVariation meshVariation = variationList.Variations[0];
				if (!variationsApplied.Contains(meshVariation.Name))
				{
					variationsApplied.Add(meshVariation.Name);
				}
			}
		}
	}

	private bool BlueprintLoaded()
	{
		blueprint = Repository<BaseBuildingRepository, BaseBuildingBlueprint>.Instance.GetByID(blueprintId);
		if (blueprint != null)
		{
			return true;
		}
		MonoSingleton<GlobalSaveController>.Instance.CorruptedBlueprintIds.Add(blueprintId);
		bool isEnabled;
		if (Repository<BaseBuildingRepository, BaseBuildingBlueprint>.Instance.TryGetDefaultByBuildingType(buildingType, out blueprint))
		{
			MonoSingleton<GlobalSaveController>.Instance.ReplacedBlueprintIds.Add(blueprintId);
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(46, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral(" Replaced by Type with default ");
				messageBuilder.AppendFormatted(buildingType);
				messageBuilder.AppendLiteral(" building type.");
			}
			Log.Info(messageBuilder);
			blueprintId = blueprint.GetID();
			return true;
		}
		if (Repository<BaseBuildingRepository, BaseBuildingBlueprint>.Instance.TryGetDefaultById(blueprintId, out blueprint))
		{
			MonoSingleton<GlobalSaveController>.Instance.ReplacedBlueprintIds.Add(blueprintId);
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(44, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral(" Replaced by ID with default ");
				messageBuilder.AppendFormatted(blueprint.GetID());
				messageBuilder.AppendLiteral(" building type.");
			}
			Log.Info(messageBuilder);
			blueprintId = blueprint.GetID();
			return true;
		}
		FVLogErrorInterpolationHandler messageBuilder2 = new FVLogErrorInterpolationHandler(60, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
		if (isEnabled)
		{
			messageBuilder2.AppendLiteral("Blueprint could not be found in BaseBuildingRepository. ID: ");
			messageBuilder2.AppendFormatted(blueprintId);
		}
		Log.Error(messageBuilder2);
		return false;
	}

	private void FixWrongMeshVariations()
	{
		bool flag = false;
		bool flag2 = false;
		foreach (string item in variationsApplied.IterateInReverseDynamic())
		{
			MeshVariation meshVariation = blueprint.GetMeshVariation(item);
			if (meshVariation == null)
			{
				continue;
			}
			if (meshVariation.HasMeshSlots)
			{
				if (flag)
				{
					variationsApplied.Remove(item);
					continue;
				}
				flag = true;
			}
			MeshVariationList variationsList = blueprint.GetVariationsList(meshVariation);
			if (variationsList != null && !variationsList.HideInUI && meshVariation.HasTextureSlots)
			{
				flag2 = true;
			}
		}
		if (flag2 || !blueprint.HasVariationWithTexture)
		{
			return;
		}
		foreach (MeshVariationList variationList in blueprint.VariationLists)
		{
			bool flag3 = false;
			foreach (MeshVariation variation in variationList.Variations)
			{
				if (variation.HasTextureSlots)
				{
					flag3 = true;
					if (!variationsApplied.Contains(variation.Name))
					{
						variationsApplied.Add(variation.Name);
					}
					break;
				}
			}
			if (flag3)
			{
				break;
			}
		}
	}

	private void DefaultVariationsCheck()
	{
		materialVariationsAppliedRefresh = true;
		foreach (string item in variationsApplied.IterateInReverseDynamic())
		{
			if (!Blueprint.ContainsMeshVariation(item))
			{
				bool isEnabled;
				FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(51, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Basic buildings\\BaseBuildingInstance.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Removing obsolete applied variation ");
					messageBuilder.AppendFormatted(item);
					messageBuilder.AppendLiteral(" from ");
					messageBuilder.AppendFormatted(Blueprint.GetID());
					messageBuilder.AppendLiteral(" instance");
				}
				Log.Debug(messageBuilder);
				variationsApplied.Remove(item);
			}
		}
		int num = variationsApplied.Count;
		if (num == blueprint.VariationLists.Count)
		{
			return;
		}
		if (num > blueprint.VariationLists.Count)
		{
			while (num > blueprint.VariationLists.Count)
			{
				variationsApplied.RemoveAt(num - 1);
				num--;
			}
			return;
		}
		for (int i = 0; i < blueprint.VariationLists.Count; i++)
		{
			MeshVariation meshVariation = blueprint.VariationLists[i].Variations[0];
			if (!IsMeshVariationApplied(meshVariation))
			{
				AddToVariationsAppliedSorted(meshVariation.Name);
			}
		}
	}

	private void CacheCurrentVariation()
	{
		if (!string.IsNullOrEmpty(CurrentMeshVariation))
		{
			return;
		}
		foreach (MeshVariation variation in blueprint.VariationLists[0].Variations)
		{
			foreach (string item in variationsApplied)
			{
				if (variation.Name == item)
				{
					CurrentMeshVariation = variation.Name;
					return;
				}
			}
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
