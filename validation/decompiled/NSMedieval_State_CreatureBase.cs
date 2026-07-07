using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using Models;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.CombatAi;
using NSMedieval.Components;
using NSMedieval.Components.Base;
using NSMedieval.Construction;
using NSMedieval.Controllers;
using NSMedieval.Dictionary;
using NSMedieval.Enums;
using NSMedieval.Fire;
using NSMedieval.FloatingOverlaySystem;
using NSMedieval.Goap;
using NSMedieval.Goap.Actions;
using NSMedieval.Manager;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.RoomDetection;
using NSMedieval.Serialization;
using NSMedieval.StatsSystem;
using NSMedieval.Tools;
using NSMedieval.Tutorial;
using NSMedieval.Types;
using NSMedieval.UI.Utils;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Utils.TimeHelpers;
using NSMedieval.View;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using NSMedieval.Village.Map.Pathfinding;
using NSMedieval.Water;
using UnityEngine;
using UnityEngine.Serialization;

namespace NSMedieval.State;

[FVSerializableKey("CreatureBase", "")]
public abstract class CreatureBase : IGoapAgentOwner, IGameDisposable, IDisposable, IHungerAgent, IStorageAgent, IStatsOwner, IPathfindingAgent, IDamageTakingAgent, IDamageCommonAgent, IGoapTargetable, IDamageDealAgent, IProgressBarOwner, IReservable, IHaulAgent, ILifeLogOwner, IGridPositionProvider, ILightReceiver, IFVSerializable, IRopableAgent
{
	public static HaulTargetingMode GlobalHaulTargetingMode;

	private static bool proximitySphereInitialized;

	private static int[] proximitySphereSurfaceX;

	private static int[] proximitySphereSurfaceY;

	private static int[] proximitySphereSurfaceZ;

	private static int[] proximitySphereVolumeX;

	private static int[] proximitySphereVolumeY;

	private static int[] proximitySphereVolumeZ;

	private const float MinFlameValue = 0.2f;

	private object memProfileInstance;

	private const int DefaultObjectProximityPerception = 6;

	[SerializeField]
	private readonly string id;

	[SerializeField]
	protected long spawnTime;

	[SerializeField]
	private Vector3 position;

	[SerializeField]
	private StatsInstance stats;

	[SerializeField]
	private Storage storage;

	[SerializeField]
	private Storage foodStorage;

	[SerializeField]
	private Storage medicineStorage;

	[SerializeField]
	private InventoryInstance inventory;

	[SerializeField]
	private bool isFirstSpawn;

	[SerializeField]
	private Vector3 spawnPosition;

	[SerializeField]
	private Vec3Int secondMapSpawnPosition;

	[SerializeField]
	private bool hasDied;

	[SerializeField]
	private string combatAiAgentId = string.Empty;

	[SerializeField]
	private HashSet<int> petsIDs;

	[SerializeField]
	private IntFloatDictionary serializableAffectionDictionary;

	[SerializeField]
	private List<RoleEffectorData> onStayRoomEffectors;

	[SerializeField]
	private List<RoleEffectorData> onEnterRoomEffectors;

	[SerializeField]
	private float fireIntensity;

	[SerializeField]
	private float lastAppliedWoundsAfter;

	[SerializeField]
	private float lastFireSpawnTime;

	[SerializeField]
	private int flameType = -1;

	[SerializeField]
	private bool flammableProjectilesAllowed;

	private bool isFallingDown;

	[NonSerialized]
	private bool isImmuneToFire;

	private Dictionary<int, float> affectionDictionary;

	private Transform viewTransform;

	[SerializeField]
	private LinkedList<LifeEventLogStruct> lifeEventLogs;

	[NonSerialized]
	private HashSet<AnimalInstance> pets;

	[NonSerialized]
	private Agent goapAgent;

	[NonSerialized]
	private volatile bool isWounded;

	[NonSerialized]
	private volatile bool isReceivingWoundTreatman;

	[NonSerialized]
	private bool hasFainted;

	[NonSerialized]
	protected bool isSleeping;

	[NonSerialized]
	private int faintEventCounter;

	[NonSerialized]
	private bool canReceiveWoundTreatment;

	[NonSerialized]
	private volatile bool isBeingCarried;

	[NonSerialized]
	private CreatureBase followCreature;

	[NonSerialized]
	private List<WorldObject> proximityObjects;

	[NonSerialized]
	private List<CreatureBase> proximityCreatures;

	[NonSerialized]
	private HashSet<WorldObject> proximityObjectsHelperCache;

	[NonSerialized]
	private HashSet<CreatureBase> proximityCreaturesHelperCache;

	[NonSerialized]
	private bool firstRoomChange = true;

	[NonSerialized]
	private Room currentRoom;

	[NonSerialized]
	private RoomType currentRoomType;

	[NonSerialized]
	private CombatAiAgent combatAiAgent;

	[NonSerialized]
	private IDamageTakingAgent target;

	[NonSerialized]
	private MapNode standingOnNode;

	[NonSerialized]
	private Dictionary<string, CombatAiAgent> combatAiAgents;

	[NonSerialized]
	private PathfinderAgentDriver pathDriver;

	[NonSerialized]
	private WalkableModel walkableModel;

	[NonSerialized]
	private List<string> effectorLogs;

	[NonSerialized]
	private bool weaponVisible;

	[NonSerialized]
	private bool isClimbingWeaponCheck;

	[NonSerialized]
	private PathTraversalProvider pathTraversalProvider;

	[NonSerialized]
	private PathTraversalProvider pathTraversalProviderFireWalkable;

	[NonSerialized]
	private PathTraversalProvider currentPathTraversalProvider;

	[FormerlySerializedAs("creationID")]
	[SerializeField]
	private int uniqueId;

	private Cooldown cooldownPathTraversal = new Cooldown(TutorialManager.IsTutorialActive);

	public bool IsMidStrike { get; set; }

	public bool HasActivePath
	{
		get
		{
			if (PathDriver != null)
			{
				return PathDriver.CurrentPath != null;
			}
			return false;
		}
	}

	public bool FlammableProjectilesAllowed
	{
		get
		{
			return flammableProjectilesAllowed;
		}
		set
		{
			if (flammableProjectilesAllowed != value)
			{
				if (!value)
				{
					CombatUtils.GetWeapon(this)?.ConsumeFlammableProjectile();
				}
				flammableProjectilesAllowed = value;
			}
		}
	}

	[field: NonSerialized]
	public bool StatsListenersAttached { get; private set; }

	public List<string> EffectorLogs
	{
		get
		{
			if (effectorLogs == null)
			{
				effectorLogs = new List<string>();
			}
			return effectorLogs;
		}
	}

	public bool HasDisposed { get; protected set; }

	public virtual ResourcePileInstance ForceEatPile { get; set; }

	public virtual ResourceInstance ForceEatResource { get; set; }

	public string Id => id;

	public bool AvailableForInteraction { get; set; }

	public bool IsWounded => isWounded;

	public bool HasDied => hasDied;

	public bool HasDiedOrFainted
	{
		get
		{
			if (!HasFainted)
			{
				return HasDied;
			}
			return true;
		}
	}

	public bool HasView => (object)GetTransform() != null;

	public PathfinderAgentDriver PathDriver => pathDriver;

	public virtual WalkableModel WalkableModel => walkableModel;

	public virtual ThermalModel ThermalModel => null;

	public bool IsFallingDown => isFallingDown;

	public int FlameType => flameType;

	public HashSet<AnimalInstance> Pets
	{
		get
		{
			if (pets != null)
			{
				return pets;
			}
			pets = new HashSet<AnimalInstance>();
			if (petsIDs == null)
			{
				return pets;
			}
			if (MonoSingleton<AnimalManager>.IsInstantiated())
			{
				foreach (int petsID in petsIDs)
				{
					AnimalInstance byUniqueId = MonoSingleton<AnimalManager>.Instance.GetByUniqueId(petsID);
					if (byUniqueId != null)
					{
						pets.Add(byUniqueId);
					}
				}
			}
			return pets;
		}
	}

	public HashSet<int> PetsIDs => petsIDs;

	public VillageMap Map => VillageManager.ActiveVillage.Map;

	public Room Room => currentRoom;

	public bool IsReceivingWoundTreatman
	{
		get
		{
			return isReceivingWoundTreatman;
		}
		set
		{
			isReceivingWoundTreatman = value;
		}
	}

	public bool CanReceiveWoundTreatment
	{
		get
		{
			if (HasUntendendWounds())
			{
				if (!canReceiveWoundTreatment)
				{
					return IsLayingDown();
				}
				return true;
			}
			return false;
		}
		set
		{
			canReceiveWoundTreatment = value;
		}
	}

	public bool IsBeingCarried
	{
		get
		{
			return isBeingCarried;
		}
		set
		{
			isBeingCarried = value;
		}
	}

	public bool IsSleeping
	{
		get
		{
			return isSleeping;
		}
		set
		{
			if (value != isSleeping)
			{
				isSleeping = value;
				GetAgentView<AnimatedAgentView>()?.TrySetParameter("Sleep", value);
			}
		}
	}

	public bool HasFainted
	{
		get
		{
			return hasFainted;
		}
		private set
		{
			hasFainted = value;
			MonoSingleton<AnimationController>.Instance.SetAnimatorParameter(this, "IsFainted", hasFainted);
		}
	}

	public bool IsBleeding => !Stats.GetAttributeInstance(AttributeType.BloodLoss).IsDisabled;

	public CreatureBase FollowCreature => followCreature;

	public virtual DietModel CurrentDietModel => null;

	public virtual DietModel CurrentDrinkDietModel => null;

	public abstract DamageTakingAgentType DamageAgentType { get; }

	public virtual StatsInstance Stats
	{
		get
		{
			return stats;
		}
		protected set
		{
			stats = value;
		}
	}

	public InventoryInstance Inventory
	{
		get
		{
			return inventory;
		}
		protected set
		{
			inventory = value;
		}
	}

	public virtual bool ForbidWeapon { get; set; }

	public virtual bool CanMakeDirtPath => false;

	public int CurrentAttackStream { get; set; }

	public CombatAiAgent CombatAi => combatAiAgent;

	public List<WorldObject> ProximityObjects => proximityObjects;

	protected bool IsFirstSpawn => isFirstSpawn;

	public Agent GoapAgent
	{
		get
		{
			return goapAgent;
		}
		protected set
		{
			goapAgent = value;
		}
	}

	public HaulTargetingMode HaulTargetMode => GlobalHaulTargetingMode;

	protected bool AfterStatsInitialisedCallbackExecuted { get; set; }

	protected virtual bool CurrentProximityDetection => false;

	protected virtual bool ProximityDetectionCreatures => true;

	protected virtual bool ProximityDetectionObjects => true;

	protected virtual bool NewProximityDetectionEnabled => true;

	public virtual bool IsProtectiveAgainstPredators => false;

	protected virtual float FlameSpawnInterval { get; }

	public Storage Storage
	{
		get
		{
			return storage;
		}
		protected set
		{
			storage = value;
		}
	}

	public Storage FoodStorage
	{
		get
		{
			if (foodStorage == null)
			{
				InitFoodStorage();
			}
			return foodStorage;
		}
		protected set
		{
			foodStorage = value;
		}
	}

	public Storage MedicineStorage
	{
		get
		{
			if (medicineStorage == null)
			{
				InitMedicineStorage();
			}
			return medicineStorage;
		}
		protected set
		{
			medicineStorage = value;
		}
	}

	public Vector3 SpawnPosition
	{
		get
		{
			return spawnPosition;
		}
		set
		{
			spawnPosition = value;
		}
	}

	public Vec3Int SecondMapSpawnPosition => secondMapSpawnPosition;

	public long SpawnTime => spawnTime;

	[field: NonSerialized]
	public bool IsFoodAllowed { get; private set; }

	[field: NonSerialized]
	public bool IsDrinkAllowed { get; private set; }

	public virtual bool ShouldFireHaulEndEffector => false;

	public virtual string HaulEndEffectorName => string.Empty;

	public virtual float HaulEndEffectorDuration => 1f;

	public PathTraversalProvider PathTraversalProvider => currentPathTraversalProvider;

	public int UniqueId
	{
		get
		{
			if (uniqueId == 0)
			{
				uniqueId = MonoSingleton<UniqueIdManager>.Instance.GetUniqueId(UniqueIdType.Creature);
			}
			return uniqueId;
		}
	}

	public Dictionary<int, float> AffectionDictionary => affectionDictionary ?? (affectionDictionary = new Dictionary<int, float>());

	public LinkedList<LifeEventLogStruct> LifeEventLogs
	{
		get
		{
			LinkedList<LifeEventLogStruct> obj = lifeEventLogs ?? new LinkedList<LifeEventLogStruct>();
			LinkedList<LifeEventLogStruct> result = obj;
			lifeEventLogs = obj;
			return result;
		}
	}

	public List<CreatureBase> ProximityCreatures => proximityCreatures;

	public virtual float OptimalNodeTemperatureRangeMin => Map.TemperatureManager.Settings.OptimalNodeTemperatureRangeMin;

	public virtual float OptimalNodeTemperatureRangeMax => Map.TemperatureManager.Settings.OptimalNodeTemperatureRangeMax;

	public virtual float RopedFollowRange => 2f;

	public List<RoleEffectorData> OnStayRoomEffectors
	{
		get
		{
			return onStayRoomEffectors ?? (onStayRoomEffectors = new List<RoleEffectorData>());
		}
		set
		{
			onStayRoomEffectors = value;
		}
	}

	public List<RoleEffectorData> OnEnterRoomEffectors
	{
		get
		{
			return onEnterRoomEffectors ?? (onEnterRoomEffectors = new List<RoleEffectorData>());
		}
		set
		{
			onEnterRoomEffectors = value;
		}
	}

	public IEnumerable<WorkerBehaviour> ProximityWorkers
	{
		get
		{
			if (ProximityCreatures == null)
			{
				yield break;
			}
			foreach (CreatureBase proximityCreature in ProximityCreatures)
			{
				if (proximityCreature is HumanoidInstance { ActiveBehaviour: WorkerBehaviour activeBehaviour })
				{
					yield return activeBehaviour;
				}
			}
		}
	}

	public virtual float WealthPoints => 0f;

	public virtual int CaravanStorageCapacity => 0;

	public virtual string IconPath => "CreatureBase";

	public virtual string TradeName => GetCharacterInfo().GetFullName();

	public bool IsOnFire => fireIntensity > 1f;

	public float TimeOnFire { get; private set; }

	public event Action<StatsInstance> StatsInitializedEvent;

	public event Action<CreatureBase, MapNode, MapNode> OnGridSpaceChangedEvent;

	public event Action<IGameDisposable> OnDisposedEvent;

	public event Action<IReservable, IGoapAgentOwner> OnReservedEvent;

	public event Action<IReservable, IGoapAgentOwner> OnReleasedEvent;

	public event Action<CreatureBase, CreatureBase> ProximityInteractionEvent;

	public event Action FireStartedEvent;

	public event Action FireEndedEvent;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void OnDomainReload()
	{
		GlobalHaulTargetingMode = HaulTargetingMode.TreatAllEqually;
		proximitySphereInitialized = false;
		proximitySphereSurfaceX = null;
		proximitySphereSurfaceY = null;
		proximitySphereSurfaceZ = null;
		proximitySphereVolumeX = null;
		proximitySphereVolumeY = null;
		proximitySphereVolumeZ = null;
	}

	private static void TryInitProximitySpheres()
	{
		if (!proximitySphereInitialized)
		{
			proximitySphereInitialized = true;
			CreatureBaseUtils.GenerateProximitySphere(6, checkSurfaceOnly: true, out proximitySphereSurfaceX, out proximitySphereSurfaceY, out proximitySphereSurfaceZ);
			CreatureBaseUtils.GenerateProximitySphere(6, checkSurfaceOnly: false, out proximitySphereVolumeX, out proximitySphereVolumeY, out proximitySphereVolumeZ);
		}
	}

	public void SetImmuneToFire(bool isImmuneToFire)
	{
		this.isImmuneToFire = isImmuneToFire;
	}

	public void SyncAffectionToSave()
	{
		if ((this is HumanoidInstance humanoidInstance && humanoidInstance.IsEnemy()) || affectionDictionary == null)
		{
			return;
		}
		if (serializableAffectionDictionary == null)
		{
			serializableAffectionDictionary = new IntFloatDictionary();
		}
		serializableAffectionDictionary.Dictionary.Clear();
		foreach (KeyValuePair<int, float> item in affectionDictionary)
		{
			serializableAffectionDictionary.Dictionary[item.Key] = item.Value;
		}
	}

	public void SyncAffectionFromSave()
	{
		if (!(this is HumanoidInstance humanoidInstance) || humanoidInstance.IsEnemy() || serializableAffectionDictionary == null)
		{
			return;
		}
		AffectionDictionary.Clear();
		foreach (KeyValuePair<int, float> item in serializableAffectionDictionary.Dictionary)
		{
			AffectionDictionary[item.Key] = item.Value;
		}
	}

	protected CreatureBase(string id, Vector3 position)
	{
		spawnPosition = position;
		this.id = id;
		this.position = position;
		isFirstSpawn = true;
	}

	public virtual MapNode GetNode()
	{
		return standingOnNode;
	}

	public bool IsLayingDown()
	{
		return Stats.HasAttributeModifier(ModifierType.Sleeping);
	}

	public void SetWalkableModel(string id)
	{
		SetWalkableModel(Repository<WalkableModelRepository, WalkableModel>.Instance.GetByID(id));
	}

	public void SetWalkableModel(WalkableModel walkableModel)
	{
		this.walkableModel = walkableModel;
		pathTraversalProvider = this.walkableModel.GenerateTraversalProvider();
		pathTraversalProviderFireWalkable = this.walkableModel.GenerateTraversalProviderFireWalkable();
		currentPathTraversalProvider = pathTraversalProvider;
	}

	public int GetMaxReservers()
	{
		return 1;
	}

	public virtual CreatureInfoBase GetInfo()
	{
		return null;
	}

	public virtual CharacterInfoBase GetCharacterInfo()
	{
		return null;
	}

	public virtual string GetFullName()
	{
		return string.Empty;
	}

	public void SetSecondMapSpawnPosition(Vec3Int gridPosition)
	{
		secondMapSpawnPosition = gridPosition;
	}

	public void LogLifeEvent(LifeEventLogStruct lifeEvent)
	{
		LifeEventLogs.AddFirst(lifeEvent);
		int lifeLogLimit = MonoSingleton<GlobalSaveController>.Instance.GlobalSettings.LifeLogLimit;
		for (int num = LifeEventLogs.Count - lifeLogLimit; num > 0; num--)
		{
			LifeEventLogs.RemoveLast();
		}
	}

	public void OnProximityEvent(CreatureBase targetInstance)
	{
		this.ProximityInteractionEvent?.Invoke(this, targetInstance);
	}

	public void AssignPet(AnimalInstance pet)
	{
		if (!Pets.Contains(pet))
		{
			Pets.Add(pet);
			if (petsIDs == null)
			{
				petsIDs = new HashSet<int>();
			}
			if (petsIDs.Add(pet.UniqueId))
			{
				pet.AssignPetOwner(this);
			}
		}
	}

	public void RemovePet(AnimalInstance pet)
	{
		Pets.Remove(pet);
		petsIDs?.Remove(pet.UniqueId);
	}

	public void RefreshTagTraversalNonWalkableTags()
	{
		if (PathTraversalProvider is TagTraversalProvider tagTraversalProvider)
		{
			tagTraversalProvider.NotWalkableTags = WalkableModel.GetNonWalkableTags();
		}
	}

	public virtual void Spawn(bool startGoapTicker = true)
	{
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(8, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\CreatureBase.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Spawn '");
			messageBuilder.AppendFormatted(this);
			messageBuilder.AppendLiteral("'");
		}
		Log.Info(messageBuilder);
		TryInitProximitySpheres();
		if (Inventory != null)
		{
			Inventory.OnDestroyEvent += OnEquipmentDestroyed;
		}
		if (spawnTime <= 0)
		{
			spawnTime = GlobalSaveController.CurrentVillageData.DateAndTime.MinutesTotal;
		}
		Vec3Int size = Map.Size;
		if (Mathf.Round(position.x + 0.5f) >= (float)size.x)
		{
			position.x = size.x - 1;
		}
		if (Mathf.Round(position.z + 0.5f) >= (float)size.z)
		{
			position.z = size.z - 1;
		}
		proximityObjects = new List<WorldObject>();
		proximityObjectsHelperCache = new HashSet<WorldObject>();
		proximityCreatures = new List<CreatureBase>();
		proximityCreaturesHelperCache = new HashSet<CreatureBase>();
		isFirstSpawn = false;
		VillageMap map = Map;
		Vec3Int gridPosition = GetGridPosition();
		standingOnNode = map.GetNode(in gridPosition);
		if (pathDriver == null)
		{
			pathDriver = new PathfinderAgentDriver(this);
			pathDriver.Initialize();
		}
		FoodStorage.FreshnessDepletedEvent += OnFoodStorageFreshnessDepleted;
		FoodStorage.HealthDepletedEvent += OnFoodStorageHealthDepleted;
		FoodStorage.SubscribeAll();
		MedicineStorage.FreshnessDepletedEvent += OnMedicineStorageFreshnessDepleted;
		MedicineStorage.HealthDepletedEvent += OnMedicineStorageHealthDepleted;
		MedicineStorage.SubscribeAll();
		Map.OnVillageDisposeEvent -= Dispose;
		Map.OnVillageDisposeEvent += Dispose;
		MonoSingleton<WorkerController>.Instance.RemoveWorkerEvent += OnCreatureBaseRemoved;
		MonoSingleton<AnimalController>.Instance.RemovedAnimalEvent += OnCreatureBaseRemoved;
		storage?.InitResourcesAfterLoad();
		foodStorage?.InitResourcesAfterLoad();
		medicineStorage?.InitResourcesAfterLoad();
		SyncAffectionFromSave();
		foreach (NSMedieval.StatsSystem.Attribute allItem in Repository<AttributeRepository, NSMedieval.StatsSystem.Attribute>.Instance.GetAllItems())
		{
			if (!stats.Attributes.ContainsKey(allItem.Type))
			{
				stats.Attributes.Add(allItem.Type, new AttributeInstance(allItem.Type, stats));
			}
		}
		InitCombatAi();
		MonoSingleton<CombatAgentManager>.Instance.RegisterCommonCombatAgent(this);
		MonoSingleton<CombatController>.Instance.PreferedTargetUpdateEvent += PreferredTargetUpdated;
	}

	private void OnCreatureBaseRemoved(CreatureBase creature)
	{
		if (!HasDisposed && proximityCreatures.Remove(creature))
		{
			OnCreatureExitProximity(creature);
		}
	}

	public abstract void InitGoap();

	public abstract Transform GetTransform();

	public virtual bool IsInIncognitoMode()
	{
		return false;
	}

	private void SetInternalFireAttributes()
	{
		float value = GetAttribute(AttributeType.AgentFlammability).Value;
		float equipmentFlammability = GetEquipmentFlammability();
		GetAttribute(AttributeType.AgentFlammabilityInternal).SetMultiplier(value * equipmentFlammability);
		float value2 = GetAttribute(AttributeType.AgentFireDamageMultiplier).Value;
		float equipmentFireDamageMultiplier = GetEquipmentFireDamageMultiplier();
		GetAttribute(AttributeType.AgentFireDamageMultiplierInternal).SetMultiplier(value2 * equipmentFireDamageMultiplier);
	}

	public float GetEquipmentFlammability()
	{
		List<EquipmentInstance> equipment = GetEquipment();
		if (equipment == null)
		{
			return 1f;
		}
		float num = 1f;
		foreach (EquipmentInstance item in equipment)
		{
			if (!item.HasDisposed && !(item.Blueprint == null))
			{
				float agentFlammability = item.Blueprint.AgentFlammability;
				if (!(agentFlammability <= 0f))
				{
					num *= agentFlammability;
				}
			}
		}
		return num;
	}

	public float GetEquipmentFireDamageMultiplier()
	{
		List<EquipmentInstance> equipment = GetEquipment();
		if (equipment == null)
		{
			return 1f;
		}
		float num = 1f;
		foreach (EquipmentInstance item in equipment)
		{
			if (!item.HasDisposed && !(item.Blueprint == null))
			{
				float agentFireDamageMultiplier = item.Blueprint.AgentFireDamageMultiplier;
				if (!(agentFireDamageMultiplier <= 0f))
				{
					num *= agentFireDamageMultiplier;
				}
			}
		}
		return num;
	}

	private void CheckSpawnFire(MapNode node)
	{
		if (!(FlameSpawnInterval <= 0f) && !(fireIntensity <= 1f) && !node.HasWaterTag)
		{
			float flameSpawnInterval = FlameSpawnInterval;
			if (TimeOnFire - lastFireSpawnTime >= flameSpawnInterval)
			{
				lastFireSpawnTime = TimeOnFire;
				float fireData = Map.FireSimLogic.GetFireData(node.Index);
				Map.FireSimLogic.SetFireData(node.Index, Math.Max(fireData, 0.3f));
			}
		}
	}

	public void Tick(float deltaTime)
	{
		if (!HasDisposed && !hasDied && !IsInIncognitoMode() && standingOnNode != null && (goapAgent == null || goapAgent.IsTickActive))
		{
			TickFire(deltaTime);
			if (!HasDisposed)
			{
				GoapAgent?.Tick(deltaTime);
				CombatAi?.Tick(deltaTime);
			}
		}
	}

	public void TickFire(float deltaTime)
	{
		float flameValue = GetFlameValue();
		FireSimLogic fireSimLogic = Map.FireSimLogic;
		float fireData = fireSimLogic.GetFireData(standingOnNode.Index);
		if (flameValue <= 0f && fireData <= 0f && fireIntensity <= 0f)
		{
			return;
		}
		SetInternalFireAttributes();
		float value = GetAttribute(AttributeType.AgentFlammabilityInternal).Value;
		bool flag = standingOnNode.WaterDepthLevel != WaterDepthLevel.None;
		bool num = flag || (fireIntensity > 0f && fireData <= 0.2f);
		bool flag2 = !isImmuneToFire && fireData > 0.2f;
		if (num)
		{
			float num2 = 0f;
			num2 = ((!flag || flameType == -1) ? (deltaTime * Math.Clamp((1f - value) * 0.1f, 0.01f, 1f)) : (num2 + deltaTime * FireLogicJob.WaterFlameDecreaseOnAgents[flameType]));
			fireIntensity = Math.Clamp(fireIntensity - num2, 0f, 2f);
		}
		if (flag2)
		{
			float num3 = fireData * deltaTime * value;
			fireIntensity = Math.Clamp(fireIntensity + num3, 0f, 2f);
		}
		if (fireIntensity <= 0f)
		{
			flameType = -1;
			lastAppliedWoundsAfter = 0f;
			lastFireSpawnTime = 0f;
		}
		else if (flameType == -1)
		{
			flameType = fireSimLogic.GetFlameType(standingOnNode.Index);
		}
		TimeOnFire = ((fireIntensity <= 1f) ? 0f : (TimeOnFire + deltaTime));
		float flameValue2 = GetFlameValue();
		if (flameValue != flameValue2)
		{
			if (flameValue2 > 0f && flameValue <= 0f)
			{
				this.FireStartedEvent?.Invoke();
			}
			else if (flameValue2 <= 0f && flameValue > 0f)
			{
				this.FireEndedEvent?.Invoke();
			}
		}
		if (Stats != null)
		{
			StatInstance stat = Stats.GetStat(StatType.Health);
			if (stat != null)
			{
				float value2 = GetAttribute(AttributeType.AgentFireDamageMultiplierInternal).Value;
				float num4 = ((fireIntensity > 1f) ? (fireIntensity - 1f) : (fireData * 0.1f));
				float current = stat.Current - num4 * value2 * deltaTime;
				stat.SetCurrent(current);
			}
		}
		if (fireIntensity > 1f && TimeOnFire > 0f)
		{
			List<TimedWounds> fireWounds = GetFireWounds();
			if (fireWounds != null)
			{
				foreach (TimedWounds item in fireWounds)
				{
					if (item.Time <= TimeOnFire && lastAppliedWoundsAfter < item.Time)
					{
						lastAppliedWoundsAfter = item.Time;
						string effectorId = item.Wounds.PickRandom();
						stats.StartEffector(effectorId);
					}
				}
			}
		}
		if (stats != null)
		{
			if (GetFlameValue() > 0.33f)
			{
				if (!stats.IsEffectorActive("AgentOnFire"))
				{
					stats.StartEffector("AgentOnFire");
					MonoSingleton<AnimationController>.Instance.SetAnimatorParameter(this, "IsOnFire", value: true);
				}
			}
			else if (stats.IsEffectorActive("AgentOnFire"))
			{
				stats.EndEffector("AgentOnFire");
				MonoSingleton<AnimationController>.Instance.SetAnimatorParameter(this, "IsOnFire", value: false);
			}
		}
		RefreshPathTraversalProvider();
	}

	private void RefreshPathTraversalProvider()
	{
		if (!hasDied && !HasDisposed)
		{
			if (IsOnFire || standingOnNode.IsFire)
			{
				cooldownPathTraversal = Cooldown.FromNowMinutes(10, TutorialManager.IsTutorialActive);
			}
			bool hasEnded = cooldownPathTraversal.HasEnded;
			if (hasEnded && currentPathTraversalProvider != pathTraversalProvider)
			{
				currentPathTraversalProvider = pathTraversalProvider;
			}
			else if (hasEnded && currentPathTraversalProvider != pathTraversalProviderFireWalkable)
			{
				currentPathTraversalProvider = pathTraversalProviderFireWalkable;
			}
		}
	}

	public float GetFlameValue()
	{
		return Math.Clamp(fireIntensity - 1f, 0f, 1f);
	}

	public abstract void FaceObject(Vector3 objectPosition);

	public abstract ProgressBarFloatingElement GetProgressBar(OverlayProgressBarType type = OverlayProgressBarType.None);

	public abstract void DestroyProgressBar(OverlayProgressBarType type);

	public abstract CombatAiAgent CreateNewCombatAiAgent(string id);

	public abstract string GetDefaultCombatAgentId();

	public virtual NSMedieval.StatsSystem.Attribute GetAttributeOverride(AttributeType type)
	{
		return null;
	}

	public abstract float GetWeight();

	public virtual void DropStorage(Vec3Int position = default(Vec3Int), float tempDisableHaulingSeconds = 0f)
	{
		if (position.Equals(default(Vec3Int)))
		{
			position = GetGridPosition();
		}
		Storage?.DropAll(position, forbid: false, tempDisableHaulingSeconds);
	}

	public void DropFoodStorage(Vec3Int position = default(Vec3Int))
	{
		if (position.Equals(default(Vec3Int)))
		{
			position = GetGridPosition();
		}
		FoodStorage?.DropAll(position);
	}

	public void DropMedicineStorage(Vec3Int position = default(Vec3Int))
	{
		if (position.Equals(default(Vec3Int)))
		{
			position = GetGridPosition();
		}
		MedicineStorage?.DropAll(position);
	}

	public virtual void ConsumeStorage()
	{
		if (Storage.IsEmpty())
		{
			return;
		}
		foreach (ResourceInstance item in new List<ResourceInstance>(Storage.Resources))
		{
			Storage.Take(item);
		}
		Storage.ClearAll();
	}

	public virtual Storage GetStorage()
	{
		return storage;
	}

	public virtual bool CanConsume(DietModel dietModel, ResourcePileInstance resourcePile)
	{
		return dietModel.CanConsume(resourcePile);
	}

	public virtual bool CanConsume(DietModel dietModel, PlantMapResourceInstance plantMapResource)
	{
		return dietModel.CanConsume(plantMapResource);
	}

	public virtual bool CanConsume(DietModel dietModel, ResourceInstance resourceInstance)
	{
		return dietModel.CanConsume(resourceInstance);
	}

	public virtual List<EquipmentInstance> GetEquipment()
	{
		return Inventory?.GetEquipments();
	}

	public Agent GetGoapAgent()
	{
		return goapAgent;
	}

	public virtual string GetGoapAgentID()
	{
		return id;
	}

	public AttributeInstance GetAttribute(AttributeType attribute)
	{
		return Stats?.GetAttributeInstance(attribute);
	}

	public float GetAttributeValue(AttributeType attribute)
	{
		return GetAttribute(attribute)?.Value ?? 0f;
	}

	public virtual float GetMovementSpeed()
	{
		float num = GetAttributeValue(AttributeType.MovementSpeed);
		if (storage != null && !storage.IsEmpty() && storage.IsOverweight())
		{
			num *= GetAttributeValue(AttributeType.OverweightMinMoveSpeed);
		}
		if (num < 0.22f)
		{
			return 0.22f;
		}
		return num;
	}

	public virtual Vector3 GetPosition()
	{
		return position;
	}

	public virtual Vec3Int GetGridPosition()
	{
		return GridUtils.GetGridPosition(position, 0.01f);
	}

	public void SetPosition(Vector3 position)
	{
		this.position = position;
	}

	public void SetGridPosition(Vec3Int position)
	{
		this.position = GridUtils.GetWorldPosition(position);
	}

	public virtual void UpdatePosition(Vector3 position)
	{
		Vec3Int size = Map.Size;
		if (Mathf.Round(position.x + 0.5f) >= (float)size.x)
		{
			position.x = size.x - 1;
		}
		if (Mathf.Round(position.z + 0.5f) >= (float)size.z)
		{
			position.z = size.z - 1;
		}
		this.position = position;
		if (viewTransform == null)
		{
			viewTransform = ((MonoBehaviour)GetAgentView())?.transform;
		}
		if (viewTransform != null)
		{
			viewTransform.position = position;
		}
		if (Map == null)
		{
			return;
		}
		UpdateLadderWeaponVisibility();
		Vec3Int gridPosition = GridUtils.GetGridPosition(this.position, 0.01f);
		MapNode node = Map.GetNode(in gridPosition);
		if (standingOnNode == node)
		{
			return;
		}
		if (node == null)
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(89, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\CreatureBase.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Creature new node is NULL while updating position! Position: ");
				messageBuilder.AppendFormatted(this.position);
				messageBuilder.AppendLiteral(", GridPosition: ");
				messageBuilder.AppendFormatted(gridPosition);
				messageBuilder.AppendLiteral(", Creature: ");
				messageBuilder.AppendFormatted(this);
			}
			Log.Error(messageBuilder);
		}
		if (standingOnNode != null)
		{
			OnGridSpaceChanged(standingOnNode, node, firstTick: false);
		}
		standingOnNode = node;
	}

	public virtual int GetHeat()
	{
		return 0;
	}

	public float GetReceivingLightAmount()
	{
		return Map.TemperatureManager.GetLightIntensity(GetGridPosition());
	}

	public float GetSunlightLossMultiplier()
	{
		float receivingLightAmount = GetReceivingLightAmount();
		return Mathf.Lerp(30f, -10f, receivingLightAmount);
	}

	private void UpdateLadderWeaponVisibility()
	{
		if (!(GetAgentView() is HumanoidView humanoidView))
		{
			return;
		}
		if (!weaponVisible)
		{
			if (isClimbingWeaponCheck)
			{
				humanoidView.BodyPreview.SetShieldOnBack(putOnBack: false);
			}
		}
		else if (!isClimbingWeaponCheck && PathDriver.IsClimbing)
		{
			isClimbingWeaponCheck = true;
			humanoidView.BodyPreview.SetShieldOnBack(putOnBack: true);
		}
		else if (isClimbingWeaponCheck && !PathDriver.IsClimbing)
		{
			isClimbingWeaponCheck = false;
			humanoidView.BodyPreview.SetShieldOnBack(putOnBack: false);
		}
	}

	public void UpdateRotation(Quaternion rotation)
	{
		IAgentView agentView = GetAgentView();
		if (agentView == null)
		{
			Log.Error("Creature view is null. This should never happen. HasDisposed: " + HasDisposed, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\CreatureBase.cs");
			return;
		}
		if (PathDriver.IsClimbing)
		{
			rotation = pathDriver.LadderRotation;
		}
		if (WalkableModel.LockXZRotation <= 0f && agentView.TargetRotation != rotation)
		{
			agentView.TargetRotation = rotation;
			return;
		}
		Quaternion b = Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
		Quaternion quaternion = Quaternion.Slerp(rotation, b, WalkableModel.LockXZRotation);
		if (!(agentView.TargetRotation == quaternion))
		{
			agentView.TargetRotation = quaternion;
		}
	}

	public Quaternion GetRotation()
	{
		Transform transform = ((MonoBehaviour)GetAgentView())?.transform;
		if ((object)transform == null)
		{
			Log.Error("Creature transform is null. This should never happen. HasDisposed: " + HasDisposed, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\CreatureBase.cs");
			return default(Quaternion);
		}
		return transform.rotation;
	}

	public T GetAgentView<T>() where T : class, IAgentView
	{
		return GetAgentView() as T;
	}

	public virtual void DestroyStorage()
	{
		if (Storage?.Resources == null)
		{
			return;
		}
		foreach (ResourceInstance item in new List<ResourceInstance>(Storage.Resources))
		{
			Storage.Take(item);
		}
		storage.ClearAll();
	}

	public virtual void DestroyEquipment()
	{
		Inventory?.DestroyEquipmentSilent();
	}

	public virtual void TendWounds(float tendingQuality)
	{
		foreach (ActiveEffectorInfo untendedWound in GetUntendedWounds())
		{
			untendedWound.WoundInfo.TendWound(tendingQuality);
		}
		HandleBloodLoss();
	}

	public void UpdateBloodLoss()
	{
		HandleBloodLoss();
	}

	public virtual void Faint()
	{
		if (!HasFainted)
		{
			HasFainted = true;
			goapAgent?.Abort();
			Action action = delegate
			{
				MonoSingleton<LifeController>.Instance.OnFaint(stats);
				MonoSingleton<CombatTargetManager>.Instance.ClearAttackers(this);
				LogLifeEvent(LifeEventUtils.GetHealthFaintEventLog(this));
			};
			if (!LadderFallDown(action))
			{
				action();
			}
		}
	}

	public virtual void UnFaint()
	{
		HasFainted = false;
	}

	public bool LadderFallDown(Action callback = null)
	{
		if (standingOnNode == null || (standingOnNode.Tag & MapNodeTags.Ladder) == 0)
		{
			return false;
		}
		if (Mathf.Abs(standingOnNode.WorldPosition.y - position.y) < 0.1f)
		{
			MapNode nodeBelow = standingOnNode.GetNodeBelow();
			if (nodeBelow == null || (nodeBelow.Tag & MapNodeTags.Ladder) == 0)
			{
				return false;
			}
		}
		Vec3Int vec3Int = standingOnNode.Position;
		MapNode targetNode = standingOnNode;
		for (int i = 1; i <= vec3Int.y; i++)
		{
			VillageMap map = Map;
			Vec3Int gridPosition = new Vec3Int(vec3Int.x, i, vec3Int.z);
			MapNode node = map.GetNode(in gridPosition);
			if (node.IsWalkable)
			{
				targetNode = node;
			}
		}
		MonoBehaviour monoBehaviour = (MonoBehaviour)GetAgentView();
		if (monoBehaviour != null)
		{
			monoBehaviour.StartCoroutine(DoFallDown(targetNode, callback));
			return true;
		}
		return false;
	}

	private IEnumerator DoFallDown(MapNode targetNode, Action doneCallback)
	{
		if (HasDisposed || isFallingDown)
		{
			yield break;
		}
		isFallingDown = true;
		pathDriver.Abort();
		goapAgent?.Abort();
		bool wasTicking = goapAgent?.IsTickActive ?? false;
		goapAgent?.StopTicker();
		Transform transform = ((MonoBehaviour)GetAgentView()).transform;
		float targetPosY = targetNode.WorldPosition.y;
		float totalAcceleration = 0.01f;
		bool hasFallen = false;
		while (!hasFallen)
		{
			if (Time.deltaTime <= 1E-05f)
			{
				yield return new WaitForEndOfFrame();
			}
			totalAcceleration += VillageConstants.AgentLadderFallDownAcceleration * Time.deltaTime;
			Vector3 currentPos = transform.position;
			currentPos.y -= totalAcceleration;
			if (currentPos.y < targetPosY)
			{
				currentPos.y = targetPosY;
				hasFallen = true;
				yield return new WaitForEndOfFrame();
			}
			transform.position = currentPos;
			yield return new WaitForEndOfFrame();
		}
		MonoSingleton<AnimationController>.Instance.TriggerAgentAnimation(this, "Laydown");
		pathDriver.Teleport(targetNode.Position);
		yield return new WaitForSeconds(VillageConstants.AgentFallDownDelay);
		if (!HasDisposed)
		{
			doneCallback?.Invoke();
			isFallingDown = false;
			if (wasTicking)
			{
				goapAgent.StartTicker();
			}
		}
	}

	public bool HasUntendendWounds()
	{
		if (isWounded)
		{
			return Stats.GetActiveEffectors().Any(delegate(ActiveEffectorInfo item)
			{
				WoundEffectorInfo woundInfo = item.WoundInfo;
				return woundInfo != null && woundInfo.NeedTend && !WoundUtils.IsTended(item.WoundInfo);
			});
		}
		return false;
	}

	public List<ActiveEffectorInfo> GetUntendedWounds()
	{
		return (from item in Stats.GetActiveEffectors()
			where item.WoundInfo != null && !WoundUtils.IsTended(item.WoundInfo)
			select item).ToList();
	}

	public List<ActiveEffectorInfo> GetTendedWounds()
	{
		return (from item in Stats.GetActiveEffectors()
			where item.WoundInfo != null && WoundUtils.IsTended(item.WoundInfo)
			select item).ToList();
	}

	public virtual float GetBaseDamageOverride(DamageTakingAgentType targetType)
	{
		return 0f;
	}

	public virtual DamageTakingAgentType CanAttackTypes()
	{
		if (!HasFainted)
		{
			return DamageTakingAgentType.All;
		}
		return DamageTakingAgentType.None;
	}

	public IDamageTakingAgent GetTarget()
	{
		return target;
	}

	public void FaceTarget()
	{
		if (!CombatUtils.IsNullOrDisposed(this, target) && !pathDriver.IsClimbing)
		{
			FaceObject(target.GetPosition());
		}
	}

	public virtual void SetTarget(IDamageTakingAgent target)
	{
		if (target != null || this.target != null)
		{
			IDamageTakingAgent oldTarget = this.target;
			this.target = target;
			if (MonoSingleton<CombatController>.IsInstantiated())
			{
				MonoSingleton<CombatController>.Instance.OnTargetChanged(this, oldTarget);
			}
		}
	}

	public virtual void SetWeaponVisibility(bool isVisible)
	{
		weaponVisible = isVisible;
	}

	public virtual Transform GetWeaponTransform(int hand)
	{
		return null;
	}

	public virtual bool IsNextRoundFlammable()
	{
		return false;
	}

	public virtual void SetNextRoundFlammable(bool isNextFlammable, bool ignoreAllowed = false)
	{
	}

	public virtual bool ConsumeFlammableRound()
	{
		return false;
	}

	public void SetFollowCreature(CreatureBase creature)
	{
		if (followCreature != creature)
		{
			if (followCreature != null)
			{
				followCreature.OnDisposedEvent -= FollowCreateDisposalHandler;
			}
			followCreature = creature;
			if (followCreature != null)
			{
				followCreature.OnDisposedEvent += FollowCreateDisposalHandler;
			}
		}
	}

	public virtual float GetBeauty()
	{
		if (HasDied || HasDisposed)
		{
			return 0f;
		}
		float num = 0f;
		using PooledList<ResourceInstance> pooledList = Storage.GetResourcesPooled();
		if (pooledList.Count > 0)
		{
			foreach (ResourceInstance item in pooledList)
			{
				if (!(item.Blueprint == null) && !item.HasDisposed)
				{
					num = ((!(item.Blueprint.EquipmentBlueprint != null)) ? (num + item.Blueprint.BeautyInput) : (num + item.Blueprint.EquipmentBlueprint.BeautyInputEquipped));
				}
			}
		}
		if (Inventory?.GetEquipments() != null)
		{
			foreach (EquipmentInstance equipment in Inventory.GetEquipments())
			{
				if (equipment != null && !equipment.HasDisposed && !(equipment.Blueprint == null))
				{
					num += equipment.GetBeautyInput();
				}
			}
		}
		return num + GetAttributeValue(AttributeType.AgentBeautyInput);
	}

	public void SetCombatAiAgent(string agentId)
	{
		if (string.IsNullOrEmpty(agentId) || (agentId.Equals(combatAiAgentId) && combatAiAgent != null))
		{
			return;
		}
		if (combatAiAgents == null)
		{
			combatAiAgents = new Dictionary<string, CombatAiAgent>();
		}
		goapAgent?.Abort();
		combatAiAgentId = agentId;
		if (combatAiAgents.ContainsKey(agentId))
		{
			if (CombatAi != combatAiAgents[agentId])
			{
				combatAiAgent?.Disable();
				combatAiAgent = combatAiAgents[agentId];
				if (!IsInIncognitoMode())
				{
					combatAiAgent.Enable();
				}
			}
		}
		else
		{
			CombatAiAgent value = CreateNewCombatAiAgent(agentId);
			combatAiAgents.Add(agentId, value);
			combatAiAgent?.Disable();
			combatAiAgent = combatAiAgents[agentId];
			if (!IsInIncognitoMode())
			{
				combatAiAgent.Enable();
			}
		}
	}

	public void OnReservationChanged(bool isReserved, IGoapAgentOwner agent)
	{
		if (isReserved)
		{
			this.OnReservedEvent?.Invoke(this, agent);
		}
		else
		{
			this.OnReleasedEvent?.Invoke(this, agent);
		}
	}

	public virtual void Dispose()
	{
		if (!HasDisposed)
		{
			HasDisposed = true;
			if (LoadingController.IsLeavingMainScene)
			{
				FinalizeDispose();
			}
			else if (MonoSingleton<CreatureManager>.IsInstantiated())
			{
				MonoSingleton<CreatureManager>.Instance.ScheduleDispose(this);
			}
		}
	}

	public virtual void FinalizeDispose()
	{
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(18, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\CreatureBase.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("FinalizeDispose '");
			messageBuilder.AppendFormatted(this);
			messageBuilder.AppendLiteral("'");
		}
		Log.Info(messageBuilder);
		if (MonoSingleton<CombatAgentManager>.IsInstantiated())
		{
			MonoSingleton<CombatAgentManager>.Instance.RemoveCommonCombatAgent(this);
		}
		if (MonoSingleton<CombatController>.IsInstantiated())
		{
			MonoSingleton<CombatController>.Instance.PreferedTargetUpdateEvent -= PreferredTargetUpdated;
		}
		if (MonoSingleton<WorkerController>.IsInstantiated())
		{
			MonoSingleton<WorkerController>.Instance.RemoveWorkerEvent -= OnCreatureBaseRemoved;
		}
		if (MonoSingleton<AnimalController>.IsInstantiated())
		{
			MonoSingleton<AnimalController>.Instance.RemovedAnimalEvent -= OnCreatureBaseRemoved;
		}
		if (LoadingController.IsSceneTransition || MonoSingleton<GlobalSaveController>.IsApplicationQuitting())
		{
			storage?.ClearAll(isSilent: true);
			foodStorage?.ClearAll(isSilent: true);
			medicineStorage?.ClearAll(isSilent: true);
		}
		else
		{
			DropStorage();
			DropFoodStorage();
			DropMedicineStorage();
			inventory?.ClearInventoryOnDeath();
			inventory?.Dispose();
		}
		inventory = null;
		goapAgent?.Dispose();
		goapAgent = null;
		pathDriver?.Dispose();
		pathDriver = null;
		this.StatsInitializedEvent = null;
		if (!LoadingController.IsLeavingMainScene)
		{
			this.OnDisposedEvent?.Invoke(this);
			if (MonoSingleton<ReservationManager>.IsInstantiated())
			{
				MonoSingleton<ReservationManager>.Instance.ReleaseAll((IGoapAgentOwner)this);
			}
		}
		if (LoadingController.IsLeavingMainScene || LoadingController.IsSceneTransition)
		{
			DisposeAllCombatAiAgents();
		}
		else
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(DisposeAllCombatAiAgents);
		}
		if (MonoSingleton<CreatureManager>.IsInstantiated())
		{
			MonoSingleton<CreatureManager>.Instance.WoundedCreatures.Remove(this);
		}
		if (MonoSingleton<World>.IsInstantiated())
		{
			VillageMap map = Map;
			Vec3Int gridPosition = GetGridPosition();
			MapNode selfNode = map.GetNode(in gridPosition);
			if (MonoSingleton<TaskController>.IsInstantiated() && !LoadingController.IsLeavingMainScene)
			{
				MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
				{
					if (!LoadingController.IsLeavingMainScene && selfNode != null)
					{
						selfNode.ForceRefresh();
					}
				});
			}
		}
		MonoSingleton<UniqueIdManager>.Instance.ReleaseUniqueId(UniqueIdType.Creature, uniqueId);
		proximityCreatures?.Clear();
		proximityCreatures = null;
		affectionDictionary?.Clear();
		affectionDictionary = null;
		proximityObjectsHelperCache?.Clear();
		proximityObjectsHelperCache = null;
		proximityCreaturesHelperCache?.Clear();
		proximityCreaturesHelperCache = null;
		pets?.Clear();
		pets = null;
		this.StatsInitializedEvent = null;
		this.OnGridSpaceChangedEvent = null;
		this.OnDisposedEvent = null;
		this.OnReservedEvent = null;
		this.OnReleasedEvent = null;
		this.ProximityInteractionEvent = null;
		if (HasDisposed)
		{
			stats?.Dispose();
		}
		stats = null;
		FoodStorage?.Dispose();
		MedicineStorage?.Dispose();
		Storage?.Dispose();
		foodStorage = null;
		medicineStorage = null;
		storage = null;
		affectionDictionary?.Clear();
		affectionDictionary = null;
		this.FireEndedEvent = null;
		this.FireStartedEvent = null;
		currentRoom = null;
		followCreature = null;
		onEnterRoomEffectors?.Clear();
		onEnterRoomEffectors = null;
		onStayRoomEffectors?.Clear();
		onStayRoomEffectors = null;
		pathTraversalProvider = null;
		pathTraversalProviderFireWalkable = null;
		standingOnNode = null;
		target = null;
		walkableModel = null;
	}

	private void DisposeAllCombatAiAgents()
	{
		CombatAiAgent combatAi = CombatAi;
		if (combatAi != null && !combatAi.HasDisposed)
		{
			combatAiAgent.Dispose();
		}
		combatAiAgent = null;
		if (combatAiAgents == null)
		{
			return;
		}
		foreach (KeyValuePair<string, CombatAiAgent> combatAiAgent in combatAiAgents)
		{
			combatAiAgent.Value.Dispose();
		}
		combatAiAgents.Clear();
		combatAiAgents = null;
	}

	public void ForceCheckRoomChange()
	{
		CheckRoomChange();
	}

	public CreatureBase GetRandomFromSameRoom<T>() where T : CreatureBase
	{
		if (Room == null)
		{
			return null;
		}
		List<CreatureBase> list = new List<CreatureBase>();
		foreach (CreatureBase proximityCreature in ProximityCreatures)
		{
			if (proximityCreature is T && Room == proximityCreature.Room)
			{
				list.Add(proximityCreature);
			}
		}
		return list.PickRandom();
	}

	public CreatureBase GetClosestWithSameGoal<T>() where T : CreatureBase
	{
		CreatureBase result = null;
		float num = float.MaxValue;
		foreach (CreatureBase proximityCreature in ProximityCreatures)
		{
			if (proximityCreature != null && !proximityCreature.HasDisposed && proximityCreature is T && !(GetGoapAgent().CurrentGoalName != proximityCreature.GetGoapAgent().CurrentGoalName))
			{
				float num2 = Vector3.Distance(GetPosition(), proximityCreature.GetPosition());
				if (num2 < num)
				{
					result = proximityCreature;
					num = num2;
				}
			}
		}
		return result;
	}

	public StatInstance GetStat(StatType statType)
	{
		return Stats?.GetStat(statType);
	}

	public float Distance(IDamageCommonAgent other)
	{
		Vector3 a = GetPosition();
		Vector3 b = other.GetPosition();
		return a.Distance(in b);
	}

	public float DistanceSquared(IDamageCommonAgent other)
	{
		Vector3 a = GetPosition();
		Vector3 b = other.GetPosition();
		return VectorExtension.DistanceSquared(a, in b);
	}

	public float Distance(IGoapTargetable other)
	{
		Vector3 a = GetPosition();
		Vector3 b = other.GetPosition();
		return a.Distance(in b);
	}

	public float DistanceSquared(IGoapTargetable other)
	{
		Vector3 a = GetPosition();
		Vector3 b = other.GetPosition();
		return VectorExtension.DistanceSquared(a, in b);
	}

	public bool IsKilled(out CreatureBase killer)
	{
		killer = null;
		if (!hasDied || CombatAi == null)
		{
			return false;
		}
		if (CombatAi.GetState<long>(CombatAiState.LastDamageTakenTime) - GlobalSaveController.CurrentVillageData.DateAndTime.CurrentTimeTutorialAware >= 2)
		{
			return false;
		}
		if (!(CombatAi.GetState<IDamageDealAgent>(CombatAiState.LastDamageTakenFrom) is CreatureBase creatureBase))
		{
			return false;
		}
		killer = creatureBase;
		return true;
	}

	public EquipmentInstance GetBestCombatCoverEquipment(DamageType damageType)
	{
		List<EquipmentInstance> equipment = GetEquipment();
		if (equipment == null)
		{
			return null;
		}
		EquipmentInstance equipmentInstance = null;
		foreach (EquipmentInstance item in equipment)
		{
			if (!item.HasDisposed && item.Blueprint.CanBlockAttacks(damageType) && (item.Blueprint.EquipmentSlots & (EquipmentSlotType.RightHand | EquipmentSlotType.LeftHand)) != 0)
			{
				if (item.Blueprint.ItemType == ItemType.Armor)
				{
					equipmentInstance = item;
					break;
				}
				if (equipmentInstance == null || item.Blueprint.GetCoverChance(damageType) > equipmentInstance.Blueprint.GetCoverChance(damageType))
				{
					equipmentInstance = item;
				}
			}
		}
		return equipmentInstance;
	}

	protected virtual void OnEquipmentDestroyed(EquipmentInstance instance)
	{
	}

	protected virtual void InitCombatAi()
	{
		if (!string.IsNullOrEmpty(combatAiAgentId))
		{
			SetCombatAiAgent(combatAiAgentId);
		}
		else if (!string.IsNullOrEmpty(GetDefaultCombatAgentId()))
		{
			SetCombatAiAgent(GetDefaultCombatAgentId());
		}
	}

	protected abstract void InitStats();

	public void EnsureStats()
	{
		if (stats == null)
		{
			InitStats();
		}
	}

	protected abstract IAgentView GetAgentView();

	protected virtual void OnHealthDepleted(bool wasNaturalDeath = false)
	{
		hasDied = true;
	}

	protected virtual void OnBloodDepleted()
	{
		StatInstance stat = stats?.GetStat(StatType.Health);
		if (stat != null)
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				stat.SetCurrent(-999f);
			});
		}
	}

	protected virtual void OnStatsInitialized()
	{
		if (AfterStatsInitialisedCallbackExecuted)
		{
			return;
		}
		Stats.AddAttributeModifier(new CustomAttributeAdderModifierInstance(AttributeType.BloodLoss, -1f, "bloodloss_substraction_fix"));
		this.StatsInitializedEvent?.Invoke(Stats);
		AfterStatsInitialisedCallbackExecuted = true;
		if (hasDied)
		{
			Stats.Dispose();
		}
		MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
		{
			GoapAgent?.GoalScheduler?.UpdatePrioritiesDelayed();
		});
		MonoSingleton<TaskController>.Instance.WaitFor(1f).Then(delegate
		{
			if (!HasDisposed)
			{
				HandleBloodLoss();
			}
		});
	}

	protected virtual void RegisterStatsListeners()
	{
		if (!StatsListenersAttached)
		{
			StatsListenersAttached = true;
			Stats.OnEffectorStartEvent += OnEffectorStartWoundsCheck;
			Stats.OnEffectorStackEvent += OnEffectorStartWoundsCheck;
			Stats.OnEffectorEndEvent += OnEffectorEndWoundsCheck;
			Stats.Controller.RegisterListener(StatEventType.GoapEvent, OnStatEffectorEvent);
			Stats.Controller.RegisterListener(StatEventType.AttributeModiferAdded, OnAttributeModifierChanged);
			Stats.Controller.RegisterListener(StatEventType.AttributeModiferRemoved, OnAttributeModifierChanged);
			Stats.Controller.RegisterListener(StatEventType.AttributeModiferStackChanged, OnAttributeModifierChanged);
			Stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Health, OnHealthDepleted);
			Stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Blood, OnBloodDepleted);
		}
	}

	protected virtual void RemoveStatsListeners()
	{
		StatsListenersAttached = false;
		if (Stats != null)
		{
			Stats.OnEffectorStartEvent -= OnEffectorStartWoundsCheck;
			Stats.OnEffectorStackEvent -= OnEffectorStartWoundsCheck;
			Stats.OnEffectorEndEvent -= OnEffectorEndWoundsCheck;
			Stats.Controller.RemoveListener(OnStatEffectorEvent);
			Stats.Controller.RemoveListener(OnAttributeModifierChanged);
			Stats.Controller.RemoveListener(OnHealthDepleted);
			Stats.Controller.RemoveListener(OnBloodDepleted);
		}
	}

	protected virtual void OnStatEffectorEvent(object data)
	{
		if (HasDisposed || data == null)
		{
			return;
		}
		GoapEventEffect.GoapStatEventData goapStatEventData = (GoapEventEffect.GoapStatEventData)data;
		if (string.IsNullOrEmpty(goapStatEventData.Name))
		{
			return;
		}
		switch (goapStatEventData.Name)
		{
		case "AllowFood":
			IsFoodAllowed = goapStatEventData.IsStart && goapStatEventData.Value.Equals("true");
			break;
		case "AllowDrink":
			IsDrinkAllowed = goapStatEventData.IsStart && goapStatEventData.Value.Equals("true");
			break;
		case "Faint":
			if (goapStatEventData.IsStart)
			{
				faintEventCounter++;
				if (faintEventCounter <= 1)
				{
					Faint();
				}
			}
			else
			{
				faintEventCounter--;
				if (faintEventCounter < 1)
				{
					UnFaint();
				}
			}
			break;
		case "TriggerParticle":
		{
			string value = goapStatEventData.Value;
			if (string.IsNullOrEmpty(value))
			{
				break;
			}
			AnimatedAgentView agentView = GetAgentView<AnimatedAgentView>();
			if (!(agentView == null))
			{
				if (goapStatEventData.IsStart)
				{
					agentView.StartParticle(value);
				}
				else
				{
					agentView.StopParticle(value);
				}
			}
			break;
		}
		}
	}

	protected void OnAttributeModifierChanged(object data)
	{
	}

	protected void OnHealthDepleted(object data)
	{
		if (!hasDied && MonoSingleton<World>.Instance.IsLoaded)
		{
			OnHealthDepleted();
		}
	}

	protected void OnBloodDepleted(object data)
	{
		OnBloodDepleted();
	}

	protected virtual void PreferredTargetUpdated(IDamageDealAgent deal, IDamageTakingAgent target, IDamageTakingAgent oldTarget)
	{
		if (deal == this && target != null && goapAgent.CurrentGoalName.Equals("IdleGoal"))
		{
			goapAgent.Abort();
		}
	}

	protected void InitFoodStorage()
	{
		FoodStorage = new Storage(new StorageBase(100));
	}

	protected void InitMedicineStorage()
	{
		MedicineStorage = new Storage(new StorageBase(100));
	}

	private void OnFoodStorageFreshnessDepleted(ResourceInstance resourceDepleted)
	{
		if (resourceDepleted == null || resourceDepleted.HasDisposed)
		{
			return;
		}
		string rottenId = resourceDepleted.Blueprint.RottenId;
		if (!string.IsNullOrEmpty(rottenId))
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(rottenId);
			if (byID != null)
			{
				float statValue = resourceDepleted.GetStatValue(StatType.Health);
				if ((byID.Category & ResourceCategory.CtgMeal) != 0)
				{
					ResourceInstance resourceInstance = new ResourceInstance(byID, 1);
					resourceInstance.GetStat(StatType.Health).SetCurrent(statValue);
					FoodStorage.Add(resourceInstance, resourceInstance.Amount);
				}
				else
				{
					ResourceInstance resourceInstance2 = new ResourceInstance(byID, resourceDepleted.Amount);
					resourceInstance2.GetStat(StatType.Health).SetCurrent(statValue);
					MonoSingleton<ResourcePileManager>.Instance.SpawnPile(resourceInstance2, GridUtils.GetWorldPosition(GetGridPosition()));
				}
			}
		}
		FoodStorage.DeleteResource(resourceDepleted);
	}

	private void OnMedicineStorageFreshnessDepleted(ResourceInstance resourceDepleted)
	{
		if (resourceDepleted == null)
		{
			return;
		}
		string rottenId = resourceDepleted.Blueprint.RottenId;
		if (!string.IsNullOrEmpty(rottenId))
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(rottenId);
			if (byID != null)
			{
				float statValue = resourceDepleted.GetStatValue(StatType.Health);
				if ((byID.Category & ResourceCategory.CtgMeal) != 0)
				{
					ResourceInstance resourceInstance = new ResourceInstance(byID, 1);
					resourceInstance.GetStat(StatType.Health).SetCurrent(statValue);
					MedicineStorage.Add(resourceInstance, resourceInstance.Amount);
				}
				else
				{
					ResourceInstance resourceInstance2 = new ResourceInstance(byID, resourceDepleted.Amount);
					resourceInstance2.GetStat(StatType.Health).SetCurrent(statValue);
					MonoSingleton<ResourcePileManager>.Instance.SpawnPile(resourceInstance2, GridUtils.GetWorldPosition(GetGridPosition()));
				}
			}
		}
		MedicineStorage.DeleteResource(resourceDepleted);
	}

	private void OnFoodStorageHealthDepleted(ResourceInstance resourceDepleted)
	{
		if (resourceDepleted != null)
		{
			FoodStorage.DeleteResource(resourceDepleted);
		}
	}

	private void OnMedicineStorageHealthDepleted(ResourceInstance resourceDepleted)
	{
		if (resourceDepleted != null)
		{
			MedicineStorage.DeleteResource(resourceDepleted);
		}
	}

	private void CheckRoomChange()
	{
		if (Map?.RoomDetection == null)
		{
			return;
		}
		Room room = Map.RoomDetection.GetRoom(GridUtils.GetGridPosition(position, 0.01f));
		if (currentRoom != room)
		{
			if (!firstRoomChange)
			{
				OnRoomChanged(currentRoom, room);
			}
			currentRoom = room;
		}
		if (currentRoomType != currentRoom?.RoomType)
		{
			RoomType oldRoomType = currentRoomType;
			currentRoomType = room?.RoomType;
			OnRoomTypeChanged(currentRoomType, oldRoomType);
		}
		if (firstRoomChange)
		{
			firstRoomChange = false;
		}
	}

	protected virtual void OnGridSpaceChanged(MapNode oldNode, MapNode newNode, bool firstTick)
	{
		this.OnGridSpaceChangedEvent?.Invoke(this, oldNode, newNode);
		if (NewProximityDetectionEnabled)
		{
			Map.ProtectorCreatureManager.CreatureStateChanged(this, oldNode, newNode);
		}
		if (CurrentProximityDetection)
		{
			try
			{
				UpdateProximityObjectsAndCreatures(oldNode, newNode);
			}
			catch (Exception ex)
			{
				Log.Error("Update proximity exception: " + ex, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\CreatureBase.cs");
			}
		}
		if (newNode != null && (newNode.Tag & MapNodeTags.Ladder) != 0 && !(GetTarget() is BaseBuildingInstance))
		{
			CreatureBase creatureBase = null;
			if (newNode.CreaturesCount > 1)
			{
				foreach (CreatureBase item in Map.CreaturesOnNodes[newNode.Index])
				{
					if (!item.HasDisposed && HostileProximitySensor.IsHostile(item, this))
					{
						creatureBase = item;
						break;
					}
				}
			}
			if (creatureBase == null && newNode.GetNodeAbove()?.CreaturesCount > 0)
			{
				creatureBase = Map.CreaturesOnNodes[newNode.GetNodeAbove().Index].FirstOrDefault((CreatureBase item) => HostileProximitySensor.IsHostile(item, this));
			}
			if (creatureBase == null && newNode.GetNodeBelow()?.CreaturesCount > 0)
			{
				creatureBase = Map.CreaturesOnNodes[newNode.GetNodeBelow().Index].FirstOrDefault((CreatureBase item) => HostileProximitySensor.IsHostile(item, this));
			}
			if (creatureBase != null && GetTarget() != creatureBase)
			{
				HumanoidInstance obj = creatureBase as HumanoidInstance;
				if (obj == null || !obj.IsEnemy())
				{
					HumanoidInstance obj2 = this as HumanoidInstance;
					if (obj2 == null || !obj2.IsEnemy())
					{
						goto IL_0293;
					}
				}
				MonoSingleton<CombatTargetManager>.Instance.SetPreferredTarget(this, creatureBase);
				CombatActions.GetLadderAttackOffset(creatureBase);
				GoapAgent.Abort();
				Log.Info("NEW TARGET: " + creatureBase?.ToString() + " " + this, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\CreatureBase.cs");
			}
		}
		goto IL_0293;
		IL_0293:
		if (!firstTick && newNode != null)
		{
			TrapComponentInstance trapComponentInstance = newNode.Map?.TrapComponentsManager.GetComponentInstance(newNode.GetWorldObject(GridDataType.Trap));
			if (trapComponentInstance != null && !trapComponentInstance.HasDisposed)
			{
				OnTrapTriggered(trapComponentInstance);
			}
			BaseBuildingInstance buildingInstance = Map.BuildingsManagerMain.GetBuildingInstance(newNode.Position, BuildingType.PassThroughDamageable);
			if (buildingInstance != null && !buildingInstance.HasDisposed)
			{
				bool isEnabled;
				FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(5, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\CreatureBase.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("bbi: ");
					messageBuilder.AppendFormatted(buildingInstance);
				}
				Log.Debug(messageBuilder);
				buildingInstance.OnCreaturePassedThrough(this);
			}
		}
		CheckRoomChange();
		CheckSpawnFire(newNode);
		oldNode?.ForceRefreshTemperatureInput();
		oldNode?.ForceRefreshBeautyInput();
		newNode?.ForceRefreshTemperatureInput();
		newNode?.ForceRefreshBeautyInput();
		if (this is HumanoidInstance)
		{
			oldNode?.RefreshTags();
			newNode?.RefreshTags();
		}
	}

	protected virtual void OnWorldObjectEnterProximity(WorldObject worldObject)
	{
	}

	protected virtual void OnWorldObjectExitProximity(WorldObject worldObject)
	{
	}

	protected virtual void OnCreatureEnterProximity(CreatureBase creature)
	{
	}

	protected virtual void OnCreatureExitProximity(CreatureBase creature)
	{
	}

	protected virtual void OnTrapTriggered(TrapComponentInstance trapInstance)
	{
		if (trapInstance.Operational && Stats.GetStat(StatType.Health).Blueprint.Min != Stats.GetStat(StatType.Health).Blueprint.Max)
		{
			CombatHitManager.DealTrapDamage(this, trapInstance);
		}
	}

	protected virtual void OnRoomChanged(Room oldRoom, Room newRoom)
	{
	}

	protected virtual void OnRoomTypeChanged(RoomType roomType, RoomType oldRoomType)
	{
	}

	private void UpdateProximityObjectsAndCreatures(MapNode oldNode, MapNode newNode)
	{
		if (HasDisposed || !proximitySphereInitialized || proximityObjectsHelperCache == null || proximityCreaturesHelperCache == null)
		{
			return;
		}
		proximityObjectsHelperCache.Clear();
		proximityCreaturesHelperCache.Clear();
		Vec3Int b = GetGridPosition();
		int x = b.x;
		int y = b.y;
		int z = b.z;
		bool proximityDetectionObjects = ProximityDetectionObjects;
		bool proximityDetectionCreatures = ProximityDetectionCreatures;
		int[] array;
		int[] array2;
		int[] array3;
		if (oldNode != null)
		{
			Vec3Int a = oldNode.Position;
			Vec3Int b2 = newNode.Position;
			if (Vec3Int.DistanceSquared(in a, in b2) < 4)
			{
				array = proximitySphereSurfaceX;
				array2 = proximitySphereSurfaceY;
				array3 = proximitySphereSurfaceZ;
				goto IL_00ae;
			}
		}
		array = proximitySphereVolumeX;
		array2 = proximitySphereVolumeY;
		array3 = proximitySphereVolumeZ;
		goto IL_00ae;
		IL_00ae:
		IPathfindingAgent agent = pathDriver.Agent;
		MapNode[] gridSpaceData = Map.GridSpaceData;
		using (ProfilerSampleJanitor.Begin("For loop"))
		{
			int i = 0;
			for (int num = array.Length; i < num; i++)
			{
				int x2 = x + array[i];
				if (!GridDataIndexTools.InRangeX(x2))
				{
					continue;
				}
				int y2 = y + array2[i];
				if (!GridDataIndexTools.InRangeY(y2))
				{
					continue;
				}
				int z2 = z + array3[i];
				if (!GridDataIndexTools.InRangeZ(z2))
				{
					continue;
				}
				int num2 = GridDataIndexTools.FastTo1DIndexNoCheck(x2, y2, z2);
				MapNode mapNode = gridSpaceData[num2];
				if (!mapNode.IsWalkable)
				{
					continue;
				}
				bool flag = false;
				bool flag2 = false;
				if (proximityDetectionCreatures && mapNode.CreaturesCount > 0 && Map.CreaturesOnNodes.TryGetValue(mapNode.Index, out var value))
				{
					using (ProfilerSampleJanitor.Begin("Detect creatures"))
					{
						flag = true;
						flag2 = PathfinderUtil.IsPathPossible(agent, mapNode);
						if (!flag2)
						{
							continue;
						}
						proximityCreaturesHelperCache.AddRange(value);
						goto IL_01af;
					}
				}
				goto IL_01af;
				IL_01af:
				if (!proximityDetectionObjects)
				{
					continue;
				}
				using (ProfilerSampleJanitor.Begin("Detect objects"))
				{
					if (mapNode.DataType == GridDataType.None)
					{
						continue;
					}
					foreach (WorldObject worldObject2 in mapNode.WorldObjects)
					{
						using (ProfilerSampleJanitor.Begin("Flag check"))
						{
							if ((worldObject2.GridDataType & (GridDataType.BuildingBlueprint | GridDataType.OthersBlueprint | GridDataType.BeamBlueprint | GridDataType.SocketableBlueprint | GridDataType.RugBlueprint)) != 0)
							{
								continue;
							}
						}
						using (ProfilerSampleJanitor.Begin("Building cast + flag check"))
						{
							if (!(worldObject2 is BaseBuildingInstance baseBuildingInstance))
							{
								proximityObjectsHelperCache.Add(worldObject2);
								continue;
							}
							if ((baseBuildingInstance.BuildingType & (BuildingType.Floor | BuildingType.Beam | BuildingType.Window | BuildingType.Door)) != 0)
							{
								continue;
							}
						}
						if (!flag)
						{
							flag = true;
							using (ProfilerSampleJanitor.Begin("IsPathPossible"))
							{
								flag2 = PathfinderUtil.IsPathPossible(agent, mapNode);
							}
						}
						if (!flag2)
						{
							break;
						}
						proximityObjectsHelperCache.Add(worldObject2);
					}
				}
			}
		}
		using (ProfilerSampleJanitor.Begin("WorldObjects exit proximity"))
		{
			for (int num3 = proximityObjects.Count - 1; num3 >= 0; num3--)
			{
				if (num3 < proximityObjects.Count)
				{
					WorldObject worldObject = proximityObjects[num3];
					bool flag3 = worldObject.HasDisposed;
					if (!flag3)
					{
						Vec3Int a = worldObject.GridDataPosition;
						flag3 = Vec3Int.DistanceSquaredY3(in a, in b) > 36;
					}
					if (flag3)
					{
						proximityObjects.RemoveAt(num3);
						OnWorldObjectExitProximity(worldObject);
					}
				}
			}
		}
		using (ProfilerSampleJanitor.Begin("WorldObjects enter proximity"))
		{
			int count = proximityObjectsHelperCache.Count;
			foreach (WorldObject item in proximityObjectsHelperCache)
			{
				if (!proximityObjects.Contains(item))
				{
					OnWorldObjectEnterProximity(item);
					if (count != proximityObjectsHelperCache.Count)
					{
						Log.Error("OnWorldObjectEnterProximity modified proximityObjectsHelperCache! WorldObject" + item?.ToString() + " Report this if you see it.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\CreatureBase.cs");
					}
					proximityObjects.Add(item);
					if (!(GetAgentView() is AnimatedAgentView animatedAgentView))
					{
						return;
					}
					animatedAgentView.OnCarcassProximityEnter(item);
				}
			}
		}
		using (ProfilerSampleJanitor.Begin("Creatures exit proximity"))
		{
			for (int num4 = proximityCreatures.Count - 1; num4 >= 0; num4--)
			{
				if (num4 < proximityCreatures.Count)
				{
					CreatureBase creatureBase = proximityCreatures[num4];
					bool flag4 = creatureBase.HasDisposed;
					if (!flag4)
					{
						Vec3Int a = creatureBase.GetGridPosition();
						flag4 = Vec3Int.DistanceSquared(in a, in b) > 36;
					}
					if (flag4)
					{
						proximityCreatures.RemoveAt(num4);
						OnCreatureExitProximity(creatureBase);
					}
				}
			}
		}
		using (ProfilerSampleJanitor.Begin("Creatures enter proximity"))
		{
			foreach (CreatureBase item2 in proximityCreaturesHelperCache)
			{
				OnCreatureEnterProximity(item2);
				ProximityCreatures.Add(item2);
			}
		}
	}

	private void OnEffectorStartWoundsCheck(StatEffector effector)
	{
		if (effector is StatEffectorWound)
		{
			isWounded = true;
			MonoSingleton<CreatureManager>.Instance.WoundedCreatures.Add(this);
			HandleBloodLoss();
		}
	}

	private void OnEffectorEndWoundsCheck(StatEffector effector)
	{
		if (!HasDisposed && effector is StatEffectorWound)
		{
			isWounded = Stats.GetActiveEffectors().Any((ActiveEffectorInfo info) => info.WoundInfo != null);
			if (!isWounded)
			{
				MonoSingleton<CreatureManager>.Instance.WoundedCreatures.Remove(this);
			}
			HandleBloodLoss();
		}
	}

	private void HandleBloodLoss()
	{
		if (!IsWounded)
		{
			StopBleeding();
		}
		else if (HasUntendendWounds())
		{
			StartBleeding();
		}
		else
		{
			StopBleeding();
		}
	}

	private void StopBleeding()
	{
		Stats.GetAttributeInstance(AttributeType.BloodRecovery).IsDisabled = false;
		Stats.GetAttributeInstance(AttributeType.BloodLoss).IsDisabled = true;
		MonoSingleton<LifeController>.Instance.OnStopBleeding(this);
	}

	private void StartBleeding()
	{
		Stats.GetAttributeInstance(AttributeType.BloodRecovery).IsDisabled = true;
		Stats.GetAttributeInstance(AttributeType.BloodLoss).IsDisabled = false;
		if (!isFirstSpawn)
		{
			MonoSingleton<LifeController>.Instance.OnStartBleeding(this);
		}
	}

	private void FollowCreateDisposalHandler(IDisposable disposable)
	{
		followCreature = null;
	}

	public virtual void OnPredatorProtectionChanged()
	{
	}

	public abstract bool RopeTo(IGoapTargetable target, bool matchSpeed = false);

	public abstract IGoapTargetable RopedTo();

	public abstract void IncognitoDispose();

	public virtual void ToggleWeaponMode(EquipmentInstance weapon = null)
	{
	}

	public void ResetUniqueId()
	{
		MonoSingleton<UniqueIdManager>.Instance.ReleaseUniqueId(UniqueIdType.Creature, uniqueId);
		uniqueId = 0;
	}

	public void ReassignUniqueId()
	{
		ResetUniqueId();
		_ = UniqueId;
		bool isEnabled;
		FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(24, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\CreatureBase.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Reassigned unique ID of ");
			messageBuilder.AppendFormatted(this);
		}
		Log.Debug(messageBuilder);
	}

	protected virtual List<TimedWounds> GetFireWounds()
	{
		return null;
	}

	public int GetUniqueId()
	{
		return uniqueId;
	}

	public virtual void Serialize(FVSerializer serializer)
	{
		serializer.Write("id", id);
		serializer.Write("spawnTime", spawnTime);
		serializer.Write("position", position);
		serializer.Write("stats", stats);
		serializer.Write("storage", storage);
		serializer.Write("foodStorage", foodStorage);
		serializer.Write("medicineStorage", medicineStorage);
		serializer.Write("inventory", inventory);
		serializer.Write("isFirstSpawn", isFirstSpawn);
		serializer.Write("spawnPosition", spawnPosition);
		serializer.Write("hasDied", hasDied);
		serializer.Write("combatAiAgentId", combatAiAgentId);
		serializer.Write("petsIDs", petsIDs);
		serializer.Write("serializableAffectionDictionary", serializableAffectionDictionary);
		serializer.Write("lifeEventLogs", lifeEventLogs);
		serializer.Write("uniqueId", uniqueId);
		serializer.Write("fireIntensity", fireIntensity);
		serializer.Write("flameType", flameType);
		serializer.Write("timeOnFire", TimeOnFire);
		serializer.Write("lastAppliedWoundsAfter", lastAppliedWoundsAfter);
		serializer.Write("lastFireSpawnTime", lastFireSpawnTime);
		serializer.Write("isImmuneToFire", isImmuneToFire);
	}

	public CreatureBase(CreatureBase original)
	{
		id = original.id;
		spawnTime = original.spawnTime;
		position = original.position;
		stats = original.stats;
		storage = original.storage;
		foodStorage = original.foodStorage;
		medicineStorage = original.medicineStorage;
		inventory = original.inventory;
		isFirstSpawn = original.isFirstSpawn;
		spawnPosition = original.spawnPosition;
		secondMapSpawnPosition = original.secondMapSpawnPosition;
		hasDied = original.hasDied;
		combatAiAgentId = original.combatAiAgentId;
		petsIDs = original.petsIDs;
		serializableAffectionDictionary = original.serializableAffectionDictionary;
		lifeEventLogs = original.lifeEventLogs;
		uniqueId = original.uniqueId;
		fireIntensity = original.fireIntensity;
		flameType = original.flameType;
		TimeOnFire = original.TimeOnFire;
		lastAppliedWoundsAfter = original.lastAppliedWoundsAfter;
		lastFireSpawnTime = original.lastFireSpawnTime;
		isImmuneToFire = original.isImmuneToFire;
	}

	public CreatureBase(FVDeserializer deserializer)
	{
		id = deserializer.ReadString("id");
		spawnTime = deserializer.ReadLong("spawnTime", 0L);
		position = deserializer.ReadVector3("position");
		stats = deserializer.ReadObject<StatsInstance>("stats");
		storage = deserializer.ReadObject<Storage>("storage");
		foodStorage = deserializer.ReadObject<Storage>("foodStorage");
		medicineStorage = deserializer.ReadObject<Storage>("medicineStorage");
		inventory = deserializer.ReadObject<InventoryInstance>("inventory");
		isFirstSpawn = deserializer.ReadBool("isFirstSpawn");
		spawnPosition = deserializer.ReadVector3("spawnPosition");
		secondMapSpawnPosition = deserializer.ReadVec3Int("secondMapSpawnPosition");
		hasDied = deserializer.ReadBool("hasDied");
		combatAiAgentId = deserializer.ReadString("combatAiAgentId");
		petsIDs = deserializer.ReadIntHashSet("petsIDs");
		serializableAffectionDictionary = deserializer.ReadObject<IntFloatDictionary>("serializableAffectionDictionary");
		lifeEventLogs = deserializer.ReadObjectLinkedList<LifeEventLogStruct>("lifeEventLogs");
		uniqueId = deserializer.ReadInt("uniqueId");
		fireIntensity = deserializer.ReadFloat("fireIntensity");
		flameType = deserializer.ReadInt("flameType");
		TimeOnFire = deserializer.ReadFloat("timeOnFire");
		lastAppliedWoundsAfter = deserializer.ReadFloat("lastAppliedWoundsAfter");
		lastFireSpawnTime = deserializer.ReadFloat("lastFireSpawnTime");
		isImmuneToFire = deserializer.ReadBool("isImmuneToFire");
	}

	public void SetFireIntensity(float fireIntensity)
	{
		this.fireIntensity = fireIntensity;
	}

	public abstract float GetBuildablePassThroughDestroyChance();
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
