using System;
using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using Models;
using NSEipix;
using NSEipix.Base;
using NSEipix.Model;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.CombatAi;
using NSMedieval.Components;
using NSMedieval.Construction;
using NSMedieval.Controllers;
using NSMedieval.FloatingOverlaySystem;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.PlayerTriggeredEventSystem;
using NSMedieval.Repository;
using NSMedieval.Serialization;
using NSMedieval.State.Timers;
using NSMedieval.StatsSystem;
using NSMedieval.Tools;
using NSMedieval.Types;
using NSMedieval.UI;
using NSMedieval.UI.Utils;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.View;
using NSMedieval.View.Animals;
using NSMedieval.Village.Map;
using NSMedieval.Village.Map.Pathfinding;
using NSMedieval.Water;
using NSMedieval.WorldMap;
using Social;
using UnityEngine;

namespace NSMedieval.State;

[Serializable]
[FVSerializableKey("AnimalInstance", "")]
public class AnimalInstance : CreatureBase, IReservable, IGameDisposable, IDisposable, ISleepAgent, IPathfindingAgent, IGoapAgentOwner, IDamageDealAgent, IDamageCommonAgent, IGoapTargetable, IFormCaravanAgent, IEventParticipant, IRopableAgent
{
	private const int MaxDailyTamingAttempts = 1;

	private const int MaxDailyTrainingAttempts = 1;

	[SerializeField]
	private string animalName;

	[SerializeField]
	private AnimalType animalType = AnimalType.Wild;

	[SerializeField]
	private string lifePhaseId;

	[SerializeField]
	private AnimalOrderType orderType;

	[SerializeField]
	private AnimalOrderType previousOrder;

	[SerializeField]
	private bool isLeavingMap;

	[SerializeField]
	private long phaseStartMinutes;

	[SerializeField]
	private long phaseEndMinutes;

	[SerializeField]
	private BodyType gender;

	[SerializeField]
	private List<AnimalProductionInstance> productionInstances;

	[SerializeField]
	private int hoursSinceLastTamingAttempt;

	[SerializeField]
	private int tamingAttemptsCount;

	[SerializeField]
	private string lastTamerName;

	[SerializeField]
	private bool lastTamingAttemptSuccessful;

	[SerializeField]
	private int hoursSinceLastTrainingAttempt;

	[SerializeField]
	private int trainingAttemptsCount;

	[SerializeField]
	private string lastTrainerName;

	[SerializeField]
	private bool lastTrainingAttemptSuccessful;

	[SerializeField]
	private int ageInDays;

	[SerializeField]
	private Timer pregnancyTimer;

	[SerializeField]
	private bool pregnant;

	[SerializeField]
	private bool isInIncognitoMode;

	[SerializeField]
	private string attackGroupId = string.Empty;

	[SerializeField]
	private string pestGroupId = string.Empty;

	[SerializeField]
	private bool petBattleEnabled;

	[SerializeField]
	private bool petHaulEnabled;

	[SerializeField]
	private bool petPestControlEnabled;

	[SerializeField]
	private long predatorCannotTargetUntil;

	[SerializeField]
	private TraderStockItem traderStockItem;

	[SerializeField]
	private CreatureInfoBase info;

	[NonSerialized]
	private bool breedingInitialized;

	[NonSerialized]
	private Animal blueprint;

	[NonSerialized]
	private List<bool> modified;

	[NonSerialized]
	private AnimalLifePhase lifePhase;

	[NonSerialized]
	private IGoapTargetable ropedTo;

	[NonSerialized]
	private CreatureBase petOwner;

	[NonSerialized]
	private System.Random random;

	[NonSerialized]
	private ResourcePileInstance corpsePile;

	[NonSerialized]
	private AnimalAttackGroup attackGroupCache;

	[NonSerialized]
	private bool attackGroupCacheInit;

	[NonSerialized]
	private AnimalAttackGroup pestGroupCache;

	[NonSerialized]
	private bool pestGroupCacheInit;

	[NonSerialized]
	private bool isPestGoalDisabled;

	[field: SerializeField]
	public bool IsUnnamed { get; private set; }

	public bool KilledByTrap { get; set; }

	public AnimalAttackGroup PestGroup
	{
		get
		{
			if (!pestGroupCacheInit)
			{
				pestGroupCache = (string.IsNullOrEmpty(pestGroupId) ? null : Repository<AnimalAttackGroupRepository, AnimalAttackGroup>.Instance.GetByID(pestGroupId));
				pestGroupCacheInit = true;
			}
			return pestGroupCache;
		}
		set
		{
			pestGroupCacheInit = true;
			pestGroupCache = value;
			pestGroupId = ((value == null) ? string.Empty : pestGroupCache.GetID());
		}
	}

	public AnimalAttackGroup AttackGroup
	{
		get
		{
			if (!attackGroupCacheInit)
			{
				attackGroupCache = (string.IsNullOrEmpty(attackGroupId) ? null : Repository<AnimalAttackGroupRepository, AnimalAttackGroup>.Instance.GetByID(attackGroupId));
				attackGroupCacheInit = true;
			}
			return attackGroupCache;
		}
		set
		{
			attackGroupCacheInit = true;
			attackGroupCache = value;
			attackGroupId = ((value == null) ? string.Empty : attackGroupCache.GetID());
		}
	}

	public override float WealthPoints
	{
		get
		{
			if (base.HasDisposed || base.HasDied)
			{
				return 0f;
			}
			return Blueprint.WealthPoints * Stats.GetStat(StatType.Health).GetNormalizedPercentage();
		}
	}

	public override int CaravanStorageCapacity => LifePhase.CaravanStorageCapacity;

	public override string IconPath => Blueprint.IconPath;

	public override string TradeName => AnimalUtils.GetTradeName(this);

	protected override float FlameSpawnInterval
	{
		get
		{
			if (base.HasDisposed || Blueprint == null)
			{
				return 0f;
			}
			return Blueprint.FlameSpawnInterval;
		}
	}

	public AnimalType AnimalType => animalType;

	public bool PetBattleEnabled
	{
		get
		{
			return petBattleEnabled;
		}
		set
		{
			petBattleEnabled = value;
		}
	}

	public bool PetHaulEnabled
	{
		get
		{
			return petHaulEnabled;
		}
		set
		{
			petHaulEnabled = value;
		}
	}

	public bool PetPestControlEnabled
	{
		get
		{
			return petPestControlEnabled;
		}
		set
		{
			petPestControlEnabled = value;
		}
	}

	public Animal Blueprint
	{
		get
		{
			Animal obj = blueprint ?? Repository<AnimalBaseRepository, Animal>.Instance.GetByID(base.Id);
			Animal result = obj;
			blueprint = obj;
			return result;
		}
	}

	public bool IsLeavingMap => isLeavingMap;

	public int AgeInDays => ageInDays;

	public bool CanBreed => lifePhase?.CanBreed ?? false;

	public AnimalLifePhase LifePhase
	{
		get
		{
			if (lifePhase == null)
			{
				if (string.IsNullOrEmpty(lifePhaseId))
				{
					SetLifePhase(Blueprint.LifePhases[0]);
				}
				else
				{
					foreach (AnimalLifePhase lifePhase in Blueprint.LifePhases)
					{
						if (lifePhase.PhaseName.Equals(lifePhaseId))
						{
							this.lifePhase = lifePhase;
							break;
						}
					}
				}
			}
			return this.lifePhase;
		}
	}

	public AnimalOrderType OrderType => orderType;

	public AnimalOrderType PreviousOrder => previousOrder;

	public new bool IsSleeping { get; set; }

	public bool CanSleep { get; set; }

	public override DamageTakingAgentType DamageAgentType => DamageTakingAgentType.Animal;

	public string Prefab
	{
		get
		{
			if (gender != BodyType.Male)
			{
				return Blueprint.PrefabFemale;
			}
			return Blueprint.PrefabMale;
		}
	}

	public ScheduleConfig ScheduleConfig => Blueprint.GetScheduleConfig(animalType);

	public ScheduleModel ScheduleModel => Blueprint?.GetScheduleModel(animalType);

	public bool CanTryTaming
	{
		get
		{
			if (tamingAttemptsCount < 1)
			{
				return Blueprint.CanBeTamed;
			}
			return false;
		}
	}

	public bool CanTryTraining
	{
		get
		{
			if (trainingAttemptsCount < 1)
			{
				return Blueprint.CanBeTrained;
			}
			return false;
		}
	}

	public int CurrentTamingAttemptsLeft => Mathf.Clamp(1 - tamingAttemptsCount, 0, 1);

	public int CurrentTrainingAttemptsLeft => Mathf.Clamp(1 - trainingAttemptsCount, 0, 1);

	public string LastTamerName => lastTamerName;

	public string LastTrainerName => lastTrainerName;

	public bool LastTamingAttemptSuccessful => lastTamingAttemptSuccessful;

	public bool LastTrainingAttemptSuccessful => lastTrainingAttemptSuccessful;

	public BodyType Gender => gender;

	public bool Pregnant => pregnant;

	public List<AnimalProductionInstance> AnimalProductionInstances => productionInstances;

	public CreatureBase PetOwner => petOwner;

	public override string HaulEndEffectorName => Blueprint.HaulEndEffectorName;

	public override bool ShouldFireHaulEndEffector
	{
		get
		{
			if (Blueprint == null || Blueprint.FireHaulEndEffectorChance <= 0f)
			{
				return false;
			}
			return Random.NextDouble() <= (double)Blueprint.FireHaulEndEffectorChance;
		}
	}

	public override float HaulEndEffectorDuration
	{
		get
		{
			if (Blueprint == null)
			{
				return 1f;
			}
			return Blueprint.HaulEndEffectorDuration.Random(Random);
		}
	}

	private System.Random Random
	{
		get
		{
			if (random == null)
			{
				random = new System.Random();
			}
			return random;
		}
	}

	public long PredatorCannotTargetUntil => predatorCannotTargetUntil;

	public override DietModel CurrentDietModel => Blueprint?.DietModel;

	public override ThermalModel ThermalModel => Blueprint?.ThermalModel;

	public TraderStockItem TraderStockItem
	{
		get
		{
			return traderStockItem;
		}
		set
		{
			traderStockItem = value;
		}
	}

	public bool CanPestControlGoalStart
	{
		get
		{
			if (PestGroup == null || isPestGoalDisabled)
			{
				return false;
			}
			if (AnimalType == AnimalType.Pet)
			{
				if (Blueprint == null || !Blueprint.CanPestControlAsPet)
				{
					return false;
				}
				return PetPestControlEnabled;
			}
			if (AnimalType == AnimalType.Wild || AnimalType == AnimalType.WildAggressive)
			{
				return true;
			}
			return false;
		}
	}

	public override bool CanMakeDirtPath
	{
		get
		{
			if (Blueprint == null)
			{
				return false;
			}
			return blueprint.CanMakeDirtPath;
		}
	}

	public override bool IsProtectiveAgainstPredators
	{
		get
		{
			if (AnimalType == AnimalType.Pet && !IsSleeping && !base.HasFainted && !isInIncognitoMode)
			{
				return blueprint.IsPetProtectiveAgainstPredators;
			}
			return false;
		}
	}

	protected override bool NewProximityDetectionEnabled => AnimalType == AnimalType.Pet;

	public event Action<AnimalInstance, AnimalLifePhase> LifePhaseChangedEvent;

	public event Action<AnimalInstance, AnimalType> AnimalTypeChangedEvent;

	public event Action<AnimalInstance, CreatureBase> PetOwnerChangedEvent;

	public event Action AnimalNameChangeEvent;

	public AnimalInstance(string id, Vector3 position, BodyType bodyType, int lifePhaseIndex = -1, float lifePhasePercent = -1f, bool isInIncognitoMode = false, AnimalType animalType = AnimalType.Wild)
		: base(id, position)
	{
		this.animalType = animalType;
		this.isInIncognitoMode = isInIncognitoMode;
		gender = bodyType;
		blueprint = Repository<AnimalBaseRepository, Animal>.Instance.GetByID(base.Id);
		if (blueprint == null)
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(18, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\AnimalInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Can't find animal ");
				messageBuilder.AppendFormatted(base.Id);
			}
			Log.Error(messageBuilder);
			return;
		}
		if (Blueprint.LifePhases != null && Blueprint.LifePhases.Any())
		{
			if (lifePhaseIndex == -1)
			{
				lifePhaseIndex = UnityEngine.Random.Range(0, Blueprint.LifePhases.Count);
			}
			for (int i = 0; i < lifePhaseIndex; i++)
			{
				ageInDays += (int)blueprint.LifePhases[i].DurationDays.Random();
			}
			if (lifePhaseIndex >= 0 && lifePhaseIndex < blueprint.LifePhases.Count)
			{
				if (lifePhasePercent == 0f)
				{
					ageInDays = 0;
				}
				else
				{
					float num = blueprint.LifePhases[lifePhaseIndex].DurationDays.Random();
					if (lifePhasePercent > 0f)
					{
						num *= lifePhasePercent;
					}
					ageInDays += (int)Mathf.Clamp(num, 1f, blueprint.LifePhases[lifePhaseIndex].DurationDays.Max);
				}
			}
			SetLifePhase(Blueprint.LifePhases[lifePhaseIndex % Blueprint.LifePhases.Count], lifePhasePercent);
		}
		TryInitializeBreeding();
		SetName(string.Empty);
		InitStats();
		StartAnimalTypeEffector(this.animalType);
	}

	public override IGoapTargetable RopedTo()
	{
		return ropedTo;
	}

	public override CreatureInfoBase GetInfo()
	{
		return info ?? (info = new CreatureInfoBase(Gender, AgeInDays));
	}

	public void SetName(string animalName)
	{
		if (animalName.Equals(string.Empty))
		{
			if (animalType == AnimalType.Domestic || animalType == AnimalType.Pet)
			{
				this.animalName = Repository<AnimalNameRepository, AnimalName>.Instance.GetName(gender);
				IsUnnamed = false;
			}
			else
			{
				this.animalName = MonoSingleton<LocalizationController>.Instance.GetText("unnamed");
				IsUnnamed = true;
			}
		}
		else
		{
			this.animalName = animalName;
			IsUnnamed = false;
		}
		this.AnimalNameChangeEvent?.Invoke();
	}

	public override string GetFullName()
	{
		return animalName;
	}

	public WaterDepthLevel GetWaterDepthLevel()
	{
		return base.Map.WaterManager.GetWaterLevelAsDepth(GetGridPosition());
	}

	public void AssignPetOwner(CreatureBase owner)
	{
		if ((animalType == AnimalType.Domestic || animalType == AnimalType.Pet || animalType == AnimalType.DomesticNpc) && petOwner != owner)
		{
			if (owner == null || owner is HumanoidInstance { WorkerBehaviour: not null })
			{
				SetAnimalType(AnimalType.Pet);
			}
			else
			{
				SetAnimalType(AnimalType.DomesticNpc);
			}
			petOwner?.RemovePet(this);
			petOwner = owner;
			this.PetOwnerChangedEvent?.Invoke(this, petOwner);
			if (MonoSingleton<AnimalController>.IsInstantiated())
			{
				MonoSingleton<AnimalController>.Instance.PetOwnerChanged(this, petOwner);
			}
			if (petOwner != null)
			{
				petOwner.AssignPet(this);
			}
		}
	}

	public void ResetPetOwner()
	{
		petOwner = null;
		this.PetOwnerChangedEvent?.Invoke(this, petOwner);
		if (MonoSingleton<AnimalController>.IsInstantiated())
		{
			MonoSingleton<AnimalController>.Instance.PetOwnerChanged(this, petOwner);
		}
	}

	public void SetAnimalType(AnimalType animalType)
	{
		if (this.animalType != animalType)
		{
			AnimalType oldAnimalType = this.animalType;
			this.animalType = animalType;
			ResetWalkableModel();
			RefreshCombatAi();
			this.AnimalTypeChangedEvent?.Invoke(this, animalType);
			StartAnimalTypeEffector(animalType);
			if (MonoSingleton<AnimalController>.IsInstantiated())
			{
				MonoSingleton<AnimalController>.Instance.OnAnimalTypeChanged(this);
			}
			if (MonoSingleton<AnimalManager>.IsInstantiated())
			{
				MonoSingleton<AnimalManager>.Instance.RefreshMarkForRoping(this);
				MonoSingleton<AnimalManager>.Instance.OnAnimalTypeChanged(oldAnimalType, this);
			}
			TryInitializeBreeding();
		}
	}

	public override Transform GetTransform()
	{
		AnimalView agentView = GetAgentView<AnimalView>();
		if (!(agentView != null))
		{
			return null;
		}
		return agentView.transform;
	}

	public override DamageTakingAgentType CanAttackTypes()
	{
		if (base.HasFainted || Blueprint == null || base.HasDisposed)
		{
			return DamageTakingAgentType.None;
		}
		if (base.CombatAi.IsStateSet(CombatAiState.PreferedTarget))
		{
			CreatureBase state = base.CombatAi.GetState<CreatureBase>(CombatAiState.PreferedTarget);
			if (state != null)
			{
				return state.DamageAgentType;
			}
		}
		if (animalType == AnimalType.Domestic || animalType == AnimalType.DomesticNpc)
		{
			return DamageTakingAgentType.None;
		}
		if (animalType == AnimalType.Pet && Blueprint.CanPestControlAsPet && PestGroup != null && petPestControlEnabled)
		{
			if (Blueprint.CanAttackAsPet)
			{
				return DamageTakingAgentType.Animal | DamageTakingAgentType.NPC;
			}
			return DamageTakingAgentType.Animal;
		}
		if (animalType == AnimalType.Pet && !Blueprint.CanAttackAsPet)
		{
			return DamageTakingAgentType.None;
		}
		return DamageTakingAgentType.Animal | DamageTakingAgentType.Worker | DamageTakingAgentType.NPC;
	}

	internal void SetOrder(AnimalOrderType order)
	{
		if (orderType != order)
		{
			previousOrder = orderType;
			if (order != AnimalOrderType.Hunt)
			{
				MonoSingleton<CombatTargetManager>.Instance.ClearAttackers(this);
			}
			orderType = order;
			if (ShouldResetOrder())
			{
				orderType = AnimalOrderType.None;
			}
		}
	}

	internal bool ShouldResetOrder()
	{
		switch (animalType)
		{
		case AnimalType.Domestic:
			if (orderType == AnimalOrderType.Tame)
			{
				return true;
			}
			break;
		case AnimalType.Pet:
			if (orderType == AnimalOrderType.Train)
			{
				return true;
			}
			if (orderType == AnimalOrderType.Tame)
			{
				return true;
			}
			break;
		case AnimalType.Wild:
			if (orderType == AnimalOrderType.Release)
			{
				return true;
			}
			if (orderType == AnimalOrderType.Harvest)
			{
				return true;
			}
			if (orderType == AnimalOrderType.Train)
			{
				return true;
			}
			if (orderType == AnimalOrderType.Slaughter)
			{
				return true;
			}
			break;
		}
		return false;
	}

	internal void SetCorpsePile(ResourcePileInstance corpse)
	{
		corpsePile = corpse;
	}

	private void RefreshCombatAi()
	{
		string defaultCombatAgentId = GetDefaultCombatAgentId();
		if (base.CombatAi == null || !defaultCombatAgentId.Equals(base.CombatAi.BlueprintId))
		{
			SetCombatAiAgent(defaultCombatAgentId);
		}
	}

	public override void Spawn(bool startGoapTicker = true)
	{
		CanSleep = true;
		isInIncognitoMode = false;
		base.Spawn(startGoapTicker);
		AnimalView view = GetAgentView<AnimalView>();
		base.Storage.ResourceAddedEvent += view.OnStorageChange;
		base.Storage.ResourceTakenEvent += view.OnStorageChange;
		base.Storage.ResourceDeletedEvent += delegate(ResourceInstance instance)
		{
			view.OnStorageChange(new SimpleResourceCount(instance.Blueprint, instance.Amount));
		};
		if (!isInIncognitoMode)
		{
			MonoSingleton<WorldTimeManager>.Instance.DateUpdateEvent += OnDateUpdate;
		}
		MonoSingleton<CombatController>.Instance.DamageTakenEvent += OnDamageTaken;
		ResetWalkableModel();
		if (petOwner is HumanoidInstance humanoidInstance && humanoidInstance.IsTrader())
		{
			SetWalkableModel(petOwner.WalkableModel);
			((TagTraversalProvider)base.PathTraversalProvider).NotWalkableTags |= MapNodeTags.Ladder;
		}
		if (isLeavingMap)
		{
			LeaveMap();
		}
		OnGridSpaceChanged(null, GetNode(), firstTick: true);
	}

	public override int GetHeat()
	{
		if (base.HasDied || base.HasDisposed || Blueprint?.ThermalModel == null)
		{
			return 0;
		}
		return Blueprint.ThermalModel.Emission;
	}

	public override CombatAiAgent CreateNewCombatAiAgent(string id)
	{
		return new AnimalCombatAiAgent(this, id);
	}

	public override string GetDefaultCombatAgentId()
	{
		return blueprint.GetCombatAiBlueprintId(animalType);
	}

	public override void InitGoap()
	{
		if (Blueprint.GoapAgent != null && !string.Empty.Equals(Blueprint.GoapAgent))
		{
			string text = "NSMedieval.Goap." + Blueprint.GoapAgent;
			Type type = Type.GetType(text);
			bool isEnabled;
			if (type == null)
			{
				FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(16, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\AnimalInstance.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Type not found: ");
					messageBuilder.AppendFormatted(text);
				}
				Log.Error(messageBuilder);
				base.GoapAgent = new AnimalGoapAgent(this);
				return;
			}
			object obj = Activator.CreateInstance(type, this);
			if (obj == null)
			{
				FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(29, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\AnimalInstance.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Cannot create an instance of ");
					messageBuilder.AppendFormatted(type.Name);
				}
				Log.Error(messageBuilder);
				base.GoapAgent = new AnimalGoapAgent(this);
			}
			else
			{
				base.GoapAgent = (Agent)obj;
			}
		}
		else
		{
			base.GoapAgent = new AnimalGoapAgent(this);
		}
	}

	public override string GetGoapAgentID()
	{
		return "animal";
	}

	public void ResetTimestamps(long oldTime, long newTime)
	{
		long num = oldTime - spawnTime;
		spawnTime = newTime - num;
		long num2 = oldTime - phaseStartMinutes;
		phaseStartMinutes = newTime - num2;
		long num3 = oldTime - phaseEndMinutes;
		phaseEndMinutes = newTime - num3;
	}

	public void SetLifePhase(AnimalLifePhase lifePhase, float lifePhasePercent = -1f)
	{
		if (this.lifePhase != lifePhase)
		{
			AnimalLifePhase animalLifePhase = this.lifePhase;
			lifePhaseId = lifePhase.PhaseName;
			this.lifePhase = lifePhase;
			int minutesInDay = GlobalSaveController.CurrentVillageData.DateAndTime.MinutesInDay;
			phaseStartMinutes = GlobalSaveController.CurrentVillageData.DateAndTime.MinutesTotal;
			phaseEndMinutes = phaseStartMinutes + (long)(this.lifePhase.DurationDays.Random() * (float)minutesInDay);
			if (lifePhasePercent > 0f)
			{
				long num = (long)((double)(phaseEndMinutes - phaseStartMinutes) * (double)lifePhasePercent);
				phaseStartMinutes -= num;
				phaseEndMinutes -= num;
				ageInDays += (int)(num / minutesInDay);
			}
			base.Storage = new Storage(this.lifePhase.StorageBase);
			if (animalLifePhase != null)
			{
				RefreshAttributesOverride();
				RemoveCurrentProductions();
			}
			InitializeProductions();
			this.LifePhaseChangedEvent?.Invoke(this, animalLifePhase);
			MonoSingleton<AnimalController>.Instance.LifePhaseChanged(this, animalLifePhase);
		}
	}

	private void RefreshAttributesOverride()
	{
		foreach (NSMedieval.StatsSystem.Attribute attribute in lifePhase.AttributesList.Attributes)
		{
			if (!(attribute == null) && Stats.Attributes.ContainsKey(attribute.Type))
			{
				Stats.Attributes[attribute.Type]?.RefreshOverride(Stats);
			}
		}
	}

	public override float GetWeight()
	{
		float num = Mathf.Lerp(LifePhase.ScaleStart, LifePhase.ScaleEnd, GetLifePhasePercent());
		return Blueprint.MaxWeight * num;
	}

	public float GetLifePhasePercent()
	{
		return Mathf.Clamp01((float)(GlobalSaveController.CurrentVillageData.DateAndTime.MinutesTotal - phaseStartMinutes) / (float)(phaseEndMinutes - phaseStartMinutes));
	}

	public void SetLifePhase(string lifePhaseId)
	{
		if (this.lifePhaseId.Equals(lifePhaseId) && lifePhaseId.Equals(lifePhase?.PhaseName))
		{
			return;
		}
		foreach (AnimalLifePhase lifePhase in Blueprint.LifePhases)
		{
			if (lifePhase.PhaseName.Equals(lifePhaseId))
			{
				SetLifePhase(lifePhase);
				return;
			}
		}
		bool isEnabled;
		FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(35, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\AnimalInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Cannot set life phase ");
			messageBuilder.AppendFormatted(lifePhaseId);
			messageBuilder.AppendLiteral(" for animal ");
			messageBuilder.AppendFormatted(base.Id);
			messageBuilder.AppendLiteral(".");
		}
		Log.Error(messageBuilder);
	}

	public bool ShouldGoToNextPhase()
	{
		if (!MonoSingleton<WorldTimeManager>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
		{
			return false;
		}
		return GlobalSaveController.CurrentVillageData.DateAndTime.MinutesTotal >= phaseEndMinutes;
	}

	public AnimalLifePhase GetNextLifePhase()
	{
		int num = Blueprint.LifePhases.IndexOf(lifePhase);
		if (num == -1 || num >= Blueprint.LifePhases.Count - 1)
		{
			return null;
		}
		return blueprint.LifePhases[num + 1];
	}

	public bool HasLifePhases()
	{
		if (Blueprint == null || Blueprint.LifePhases == null)
		{
			return false;
		}
		return blueprint.LifePhases.Count > 0;
	}

	public override void FaceObject(Vector3 objectPosition)
	{
		if (MonoSingleton<AnimalController>.IsInstantiated())
		{
			MonoSingleton<AnimalController>.Instance.FaceObject(this, objectPosition);
		}
	}

	public override ProgressBarFloatingElement GetProgressBar(OverlayProgressBarType type = OverlayProgressBarType.None)
	{
		AnimalView agentView = GetAgentView<AnimalView>();
		if (!(agentView == null))
		{
			return agentView.GetProgressBar(type);
		}
		return null;
	}

	public override void DestroyProgressBar(OverlayProgressBarType type)
	{
		GetAgentView<AnimalView>()?.DestroyProgressBar(type);
	}

	public void SnapToBed(BedComponentInstance bed)
	{
	}

	public override NSMedieval.StatsSystem.Attribute GetAttributeOverride(AttributeType type)
	{
		if (LifePhase == null)
		{
			return null;
		}
		return lifePhase.AttributesList?.GetOverride(type);
	}

	public override bool RopeTo(IGoapTargetable target, bool matchSpeed = false)
	{
		if (base.GoapAgent == null || base.HasDisposed)
		{
			return false;
		}
		if (IsAtEvent())
		{
			return false;
		}
		ropedTo = target;
		return true;
	}

	public override void FinalizeDispose()
	{
		if (base.CombatAi?.GetState<IDamageDealAgent>(CombatAiState.LastDamageTakenFrom) is HumanoidInstance { WorkerBehaviour: not null })
		{
			string text = Blueprint?.UnlockAchievementOnDeath;
			if (!string.IsNullOrEmpty(text))
			{
				MonoSingleton<AchievementManager>.Instance.UnlockAchievement(text);
			}
			text = Blueprint?.IncreaseAchievementCountOnDeath;
			if (!string.IsNullOrEmpty(text))
			{
				MonoSingleton<AchievementManager>.Instance.IncreaseStat(text);
			}
		}
		base.FinalizeDispose();
		RemoveCurrentProductions();
		AbortPregnancy();
		if (MonoSingleton<WorldTimeManager>.IsInstantiated())
		{
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(OnHourUpdateTryMating);
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(OnHourChangeTraining);
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(OnHourChangeTaming);
			MonoSingleton<WorldTimeManager>.Instance.DateUpdateEvent -= OnDateUpdate;
		}
		if (MonoSingleton<CombatController>.IsInstantiated())
		{
			MonoSingleton<CombatController>.Instance.DamageTakenEvent -= OnDamageTaken;
		}
		productionInstances?.Clear();
		productionInstances = null;
		pregnancyTimer?.Dispose();
		pregnancyTimer = null;
		traderStockItem = null;
		info = null;
		ropedTo = null;
		petOwner = null;
		random = null;
		corpsePile = null;
		attackGroupCache = null;
		pestGroupCache = null;
		this.LifePhaseChangedEvent = null;
		this.AnimalTypeChangedEvent = null;
		this.PetOwnerChangedEvent = null;
		this.AnimalNameChangeEvent = null;
	}

	protected override void InitStats()
	{
		if (base.Stats == null)
		{
			base.Stats = new StatsInstance(this, Blueprint.StatsModel);
			base.Stats.GenerateFromStatsModel();
			base.Stats.Initialize();
			base.RegisterStatsListeners();
		}
		else
		{
			if (base.Stats.Owner == null)
			{
				base.Stats.SetOwner(this);
			}
			Stats.AddMissingAttributes(Repository<AttributeRepository, NSMedieval.StatsSystem.Attribute>.Instance.GetAllItems());
			base.RegisterStatsListeners();
			base.Stats.Initialize();
		}
		OnStatsInitialized();
	}

	protected override void OnStatsInitialized()
	{
		if (!base.AfterStatsInitialisedCallbackExecuted)
		{
			base.OnStatsInitialized();
			Stats.Update();
			Stats.Controller.RegisterListener(StatEventType.ValueUpdated, StatType.Health, StatsListener);
			Stats.Controller.RegisterListener(StatEventType.ValueUpdated, StatType.Hunger, HungerListener);
			Stats.Controller.RegisterListener(StatEventType.ValueUpdated, StatType.Sleep, StatsListener);
			BreathSpeedModifierInstance modifier = new BreathSpeedModifierInstance(AttributeType.BreathLossSpeed, 1f, "breath");
			Stats.AddAttributeModifier(modifier);
		}
	}

	private void StatsListener(object stat)
	{
	}

	private void HungerListener(object stats)
	{
		if (NotifyHungerChange())
		{
			MonoSingleton<AnimalController>.Instance.OnHungerChange(this);
		}
	}

	public bool NotifyHungerChange()
	{
		if (animalType != 0)
		{
			return animalType == AnimalType.Pet;
		}
		return true;
	}

	protected override IAgentView GetAgentView()
	{
		return MonoSingleton<AnimalManager>.Instance.GetView(this);
	}

	protected override List<TimedWounds> GetFireWounds()
	{
		if (base.HasDisposed || Blueprint == null)
		{
			return null;
		}
		return blueprint.FireWounds;
	}

	protected override void OnHealthDepleted(bool wasNaturalDeath = false)
	{
		base.OnHealthDepleted(wasNaturalDeath);
		string text = string.Empty;
		if (IsKilled(out var killer))
		{
			LogLifeEvent(LifeEventUtils.GetHealthKilledEventLog(killer));
			text = Repository<HealthLogDataRepository, PersonalLogData>.Instance.GetRandomVariantLocalized("killed_by").Replace("<other_name>", CreatureBaseUtils.GetCreatureName(killer));
		}
		LogLifeEvent(LifeEventUtils.GetHealthDeathEventLog(this));
		AnimalType animalType = this.animalType;
		if (animalType == AnimalType.Pet || animalType == AnimalType.Domestic)
		{
			string text2 = ParseMessageText("message_animal_died");
			if (!string.IsNullOrEmpty(text))
			{
				text2 = text2 + ". " + text;
			}
			MonoSingleton<BlackBarMessageController>.Instance.ShowClickableBlackBarMessage(text2, GetPosition());
		}
		if (MonoSingleton<AnimalController>.IsInstantiated())
		{
			MonoSingleton<AnimalController>.Instance.DepletedHealth(this);
		}
	}

	private string ParseMessageText(string messageKey)
	{
		return MonoSingleton<LocalizationController>.Instance.GetText(messageKey).Replace("<name>", GetFullName()).Replace("<animal>", MonoSingleton<LocalizationController>.Instance.GetText(LocKeyUtils.GetName(Blueprint.LocKeys)));
	}

	public void LeaveMap()
	{
		if (!LoadingController.IsSceneTransition)
		{
			isLeavingMap = true;
			base.CombatAi?.Disable();
			base.GoapAgent?.Abort();
			base.GoapAgent?.ForceNextGoal("LeaveMapGoal");
		}
	}

	public void CancelLeaveMap()
	{
		if (isLeavingMap)
		{
			isLeavingMap = false;
			base.GoapAgent?.Abort();
		}
	}

	public bool IsSameCategory(AnimalInstance other)
	{
		if (other.Blueprint == null || Blueprint == null)
		{
			return false;
		}
		if (other.Blueprint == Blueprint)
		{
			return true;
		}
		return Blueprint.Category == other.Blueprint.Category;
	}

	public void ResetWalkableModel()
	{
		SetWalkableModel(Blueprint.GetWalkableModel(animalType));
	}

	private void OnDateUpdate()
	{
		ageInDays++;
	}

	public void SetupAfterLoading()
	{
		if (productionInstances == null)
		{
			return;
		}
		InitStats();
		foreach (AnimalProductionInstance productionInstance in productionInstances)
		{
			productionInstance.ReInstantiate(this);
		}
		if (tamingAttemptsCount >= 1)
		{
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent += new Action(OnHourChangeTaming);
		}
		if (trainingAttemptsCount >= 1)
		{
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent += new Action(OnHourChangeTraining);
		}
		SetupPregnancyAfterLoading();
		if (string.IsNullOrEmpty(animalName))
		{
			SetName(string.Empty);
		}
		StartAnimalTypeEffector(animalType);
		foreach (ResourceInstance resource in base.Storage.Resources)
		{
			resource.InitAfterLoadPile();
		}
		base.Storage.SetResourcesOwner();
		MonoSingleton<AnimalManager>.Instance.TryUnstuckAnimal(this);
	}

	public void ScareOff()
	{
		if (Blueprint == null || base.CombatAi == null)
		{
			return;
		}
		Agent agent = base.GoapAgent;
		if (agent == null || !CombatUtils.IsAlive(this))
		{
			return;
		}
		if (!string.IsNullOrEmpty(Blueprint.ScaredEffector))
		{
			Stats.StartEffector(Blueprint.ScaredEffector, Blueprint.ScaredEffectorDuration.Random(Random));
		}
		if (!base.CombatAi.GetState<bool>(CombatAiState.IsAggressive))
		{
			base.CombatAi.SetState(CombatAiState.IsFleeing, true);
			if (!agent.CurrentGoalName.Equals("FleeGoal"))
			{
				agent.ForceNextGoal("FleeGoal");
			}
		}
	}

	public void ScareOffForced()
	{
		if (Blueprint == null || base.CombatAi == null)
		{
			return;
		}
		Agent agent = base.GoapAgent;
		if (agent != null && CombatUtils.IsAlive(this))
		{
			if (!string.IsNullOrEmpty(Blueprint.ScaredEffector))
			{
				Stats.StartEffector(Blueprint.ScaredEffector, Blueprint.ScaredEffectorDuration.Random(Random));
			}
			base.CombatAi.SetState(CombatAiState.IsFleeing, true);
			if (!agent.CurrentGoalName.Equals("ForcedFleeGoal"))
			{
				agent.ForceNextGoal("ForcedFleeGoal");
			}
		}
	}

	protected override void OnStatEffectorEvent(object data)
	{
		base.OnStatEffectorEvent(data);
		if (base.HasDisposed || data == null)
		{
			return;
		}
		GoapEventEffect.GoapStatEventData goapStatEventData = (GoapEventEffect.GoapStatEventData)data;
		if (!string.IsNullOrEmpty(goapStatEventData.Name))
		{
			switch (goapStatEventData.Name)
			{
			case "StopPregnancy":
				AbortPregnancy();
				break;
			case "SetHuntAnimalsGroup":
			{
				AnimalAttackGroup attackGroup = ((!goapStatEventData.IsStart) ? null : Repository<AnimalAttackGroupRepository, AnimalAttackGroup>.Instance.GetByID(goapStatEventData.Value));
				AttackGroup = attackGroup;
				break;
			}
			case "SetPestAnimalsGroup":
			{
				AnimalAttackGroup pestGroup = ((!goapStatEventData.IsStart) ? null : Repository<AnimalAttackGroupRepository, AnimalAttackGroup>.Instance.GetByID(goapStatEventData.Value));
				PestGroup = pestGroup;
				break;
			}
			case "DisablePestControl":
				isPestGoalDisabled = goapStatEventData.IsStart;
				break;
			}
		}
	}

	public override string ToString()
	{
		return $"(AnimalInstance) {animalName} - {animalType} {Blueprint.GetID()};";
	}

	public void ReturnedHomeFromCaravan()
	{
		if (productionInstances == null)
		{
			Log.Info("productionInstances is null, creating it.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\AnimalInstance.cs");
			InitializeProductions();
		}
		if (Stats == null)
		{
			Log.Info("this.Stats is null, re-initializing it.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\AnimalInstance.cs");
			InitStats();
		}
		if (base.Storage == null)
		{
			Log.Info("this.Storage is null, re-initializing it.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\AnimalInstance.cs");
			base.Storage = new Storage(lifePhase.StorageBase);
		}
		foreach (AnimalProductionInstance productionInstance in productionInstances)
		{
			productionInstance.ReInstantiate(this);
		}
		if (Stats.Owner != this)
		{
			Stats.SetOwner(this);
			Stats.SetOwnerOnStats();
		}
		Stats.StartEffector("CaravanReturnedHomeAnimal");
	}

	private void OnDamageTaken(IDamageDealAgent deal, IDamageTakingAgent take, CombatHitInfo hitInfo)
	{
		if (this == take && deal is HumanoidInstance { WorkerBehaviour: not null })
		{
			ResetTaming();
			ResetTraining();
		}
	}

	private void StartAnimalTypeEffector(AnimalType animalType)
	{
		if (Blueprint == null)
		{
			return;
		}
		AnimalType[] animalTypes = EnumValues.AnimalTypes;
		foreach (AnimalType animalType2 in animalTypes)
		{
			if (animalType2 != animalType)
			{
				string effectorForAnimalType = Blueprint.GetEffectorForAnimalType(animalType2);
				if (!string.IsNullOrEmpty(effectorForAnimalType))
				{
					Stats.EndEffector(effectorForAnimalType);
				}
			}
		}
		string effectorForAnimalType2 = Blueprint.GetEffectorForAnimalType(animalType);
		if (!string.IsNullOrEmpty(effectorForAnimalType2))
		{
			Stats.StartEffector(effectorForAnimalType2);
		}
	}

	public override float GetBuildablePassThroughDestroyChance()
	{
		return Blueprint.BuildablePassThroughDestroyChance;
	}

	public void CompleteAnimalProduction()
	{
		if (AnimalProductionInstances == null)
		{
			return;
		}
		foreach (AnimalProductionInstance animalProductionInstance in AnimalProductionInstances)
		{
			animalProductionInstance.ForceCompleteProduction();
		}
	}

	public bool HasHarvestableProduction()
	{
		if (base.HasDisposed)
		{
			return false;
		}
		return productionInstances?.Any((AnimalProductionInstance x) => x.ReadyForHarvest) ?? false;
	}

	public AnimalProductionInstance GetFirstHarvestableProduction()
	{
		foreach (AnimalProductionInstance productionInstance in productionInstances)
		{
			if (productionInstance.ReadyForHarvest)
			{
				return productionInstance;
			}
		}
		return null;
	}

	public void HarvestCompleted()
	{
		if (!HasHarvestableProduction())
		{
			MonoSingleton<AnimalController>.Instance.MarkForOrder(previousOrder, this);
		}
	}

	public void GetHarvestPosition(out Vector3 gridPos, out Vector3 eulerAngles, IPathfindingAgent agent = null)
	{
		AnimalView agentView = GetAgentView<AnimalView>();
		eulerAngles = Vector3.zero;
		if (agent == null)
		{
			Transform frontHarvestPosition = agentView.FrontHarvestPosition;
			gridPos = frontHarvestPosition.position;
			if (!IsPosValid(GridUtils.GetGridPosition(gridPos)))
			{
				gridPos = GetPosition();
			}
			else
			{
				eulerAngles = frontHarvestPosition.localEulerAngles;
			}
		}
		else
		{
			Transform closestInteractTransform = agentView.GetClosestInteractTransform(agent, IsPosValid);
			if (closestInteractTransform == null)
			{
				gridPos = GetPosition();
				return;
			}
			gridPos = closestInteractTransform.position;
			eulerAngles = closestInteractTransform.localEulerAngles;
		}
		bool IsPosValid(Vec3Int gridPos)
		{
			MapNode node = base.Map.GetNode(in gridPos);
			if (node == null || (node.Tag & MapNodeTags.Ladder) != 0 || (node.DataType & GridDataType.SlopeOrStairs) != 0)
			{
				return false;
			}
			MapNode nodeBelow = node.GetNodeBelow();
			if (nodeBelow == null || (nodeBelow.Tag & MapNodeTags.Ladder) != 0 || (nodeBelow.DataType & GridDataType.SlopeOrStairs) != 0)
			{
				return false;
			}
			return true;
		}
	}

	private void InitializeProductions()
	{
		if (productionInstances == null)
		{
			productionInstances = new List<AnimalProductionInstance>();
		}
		foreach (string production in LifePhase.GetProductions(gender))
		{
			AnimalProductionInstance item = new AnimalProductionInstance(production, this);
			productionInstances.Add(item);
		}
	}

	private void RemoveCurrentProductions()
	{
		if (productionInstances == null)
		{
			return;
		}
		foreach (AnimalProductionInstance productionInstance in productionInstances)
		{
			productionInstance.CancelProduction();
		}
		productionInstances.Clear();
	}

	public int GetTamedPercentage()
	{
		if (Stats == null)
		{
			return 0;
		}
		float current = Stats.GetStat(StatType.AnimalWild).Current;
		float max = Stats.GetStat(StatType.AnimalWild).Max;
		float num = current / max * 100f;
		return (int)(100f - num);
	}

	public void SetLastTamingAttemptInfo(HumanoidInstance humanoidInstance, bool greatSuccess)
	{
		lastTamerName = humanoidInstance.Info.GetFullName();
		lastTamingAttemptSuccessful = greatSuccess;
	}

	public void TamingAttemptCompleted()
	{
		tamingAttemptsCount++;
		if (tamingAttemptsCount >= 1)
		{
			hoursSinceLastTamingAttempt = 0;
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent += new Action(OnHourChangeTaming);
		}
	}

	public void ResetTamingCounters()
	{
		hoursSinceLastTamingAttempt = 0;
		tamingAttemptsCount = 0;
		MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(OnHourChangeTaming);
	}

	private void ResetTaming()
	{
		Stats.StartEffector(Blueprint.AnimalHitTameEffectorName);
	}

	private void OnHourChangeTaming()
	{
		hoursSinceLastTamingAttempt++;
		if (hoursSinceLastTamingAttempt >= GlobalSaveController.CurrentVillageData.DateAndTime.HoursInDay)
		{
			hoursSinceLastTamingAttempt = 0;
			tamingAttemptsCount = 0;
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(OnHourChangeTaming);
		}
	}

	public int GetTrainedPercentage()
	{
		StatInstance statInstance = Stats?.GetStat(StatType.AnimalUntrained);
		if (statInstance == null)
		{
			return 0;
		}
		float num = statInstance.Current / statInstance.Max * 100f;
		return (int)(100f - num);
	}

	public void SetLastTrainingAttemptInfo(HumanoidInstance humanoidInstance, bool greatSuccess)
	{
		lastTrainerName = humanoidInstance.Info.GetFullName();
		lastTrainingAttemptSuccessful = greatSuccess;
	}

	public void TrainingAttemptCompleted()
	{
		trainingAttemptsCount++;
		if (trainingAttemptsCount >= 1)
		{
			hoursSinceLastTrainingAttempt = 0;
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent += new Action(OnHourChangeTraining);
		}
	}

	public void ResetTrainingCounters()
	{
		hoursSinceLastTrainingAttempt = 0;
		trainingAttemptsCount = 0;
		MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(OnHourChangeTraining);
	}

	private void ResetTraining()
	{
		Stats.StartEffector(Blueprint.AnimalAnimalHitTrainEffectorName);
	}

	private void OnHourChangeTraining()
	{
		hoursSinceLastTrainingAttempt++;
		if (hoursSinceLastTrainingAttempt >= GlobalSaveController.CurrentVillageData.DateAndTime.HoursInDay)
		{
			hoursSinceLastTrainingAttempt = 0;
			trainingAttemptsCount = 0;
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(OnHourChangeTraining);
		}
	}

	public void SetAnimalAsPregnant()
	{
		if (!pregnant)
		{
			StartPregnancy();
		}
	}

	public void GiveBirth()
	{
		if (pregnant)
		{
			pregnancyTimer?.ForceComplete();
		}
	}

	public float GetChanceForPregnancy()
	{
		if (animalType == AnimalType.DomesticNpc)
		{
			return 0f;
		}
		int num = 0;
		AnimalInstance[] array = MonoSingleton<AnimalManager>.Instance.Animals.Keys.ToArray();
		Vec3Int a = (Vec3Int)GetPosition();
		AnimalInstance[] array2 = array;
		foreach (AnimalInstance animalInstance in array2)
		{
			if (!CombatUtils.IsNullOrDisposed(animalInstance) && this != animalInstance && !(blueprint != animalInstance.Blueprint) && animalInstance.CanBreed && animalInstance.gender != BodyType.Female)
			{
				Vec3Int b = (Vec3Int)animalInstance.GetPosition();
				if (!(Vec3Int.Distance(in a, in b) > 10f) && PathfinderUtil.IsPathPossible(this, animalInstance.GetNode()))
				{
					num++;
					break;
				}
			}
		}
		if (num == 0)
		{
			return 0f;
		}
		float num2 = blueprint.ChanceForPregnancy * (float)num;
		int num3 = Mathf.Clamp(Blueprint.MaxCount, 1, Blueprint.MaxCount);
		float num4 = Mathf.Lerp(1f, 0f, (float)MonoSingleton<AnimalManager>.Instance.GetCount(Blueprint) / (float)num3);
		num2 *= num4;
		AnimalPenInstance pen = GetPen();
		if (pen != null)
		{
			int penSize = pen.GetPenSize();
			using PooledList<AnimalInstance> pooledList = ListPool<AnimalInstance>.GetJanitor();
			pen.GetAnimalsInPen(pooledList, array);
			int num5 = Mathf.Clamp(pooledList.Count, 1, pooledList.Count);
			if (penSize / num5 < blueprint.MinGridSpace)
			{
				num2 *= 0.1f;
			}
		}
		return Mathf.Clamp(num2, 0f, 1f);
	}

	private void StartPregnancy()
	{
		if (pregnancyTimer == null)
		{
			float interval = blueprint.PregnancyDuration * (float)GlobalSaveController.CurrentVillageData.DateAndTime.MinutesInDay;
			pregnancyTimer = new Timer(interval);
		}
		pregnancyTimer.RestartTimer();
		pregnancyTimer.AddCallback(OnPregnancyCompleted);
		pregnant = true;
	}

	private void AbortPregnancy()
	{
		pregnancyTimer?.Dispose();
		pregnancyTimer = null;
		pregnant = false;
	}

	private void TryInitializeBreeding()
	{
		if (!breedingInitialized && gender == BodyType.Female)
		{
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent += new Action(OnHourUpdateTryMating);
			breedingInitialized = true;
		}
	}

	private void SetupPregnancyAfterLoading()
	{
		if (gender == BodyType.Female)
		{
			if (pregnant)
			{
				pregnancyTimer.AddCallback(OnPregnancyCompleted);
				pregnancyTimer.ResumeAddToTimerController();
			}
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent += new Action(OnHourUpdateTryMating);
			breedingInitialized = true;
		}
	}

	private void OnHourUpdateTryMating()
	{
		if (!base.HasDisposed && !base.HasDied && !isInIncognitoMode && Stats != null && !(blueprint == null))
		{
			string stopPregnancyEffector = blueprint.StopPregnancyEffector;
			if (!Stats.IsEffectorActive(stopPregnancyEffector) && !pregnant && CanBreed)
			{
				TryMating();
			}
		}
	}

	public AnimalPenInstance GetPen()
	{
		if (base.HasDied || base.HasDisposed)
		{
			return null;
		}
		Region region = this?.GetNode()?.Region;
		if (region == null)
		{
			return null;
		}
		foreach (AnimalPenInstance penInstance in MonoSingleton<PenViewManager>.Instance.PenInstances)
		{
			if (penInstance.Regions.Contains(region))
			{
				return penInstance;
			}
		}
		return null;
	}

	private void TryMating()
	{
		float num = UnityEngine.Random.Range(0f, 1f);
		if (GetChanceForPregnancy() >= num)
		{
			StartPregnancy();
		}
	}

	private void OnPregnancyCompleted()
	{
		AbortPregnancy();
		if (animalType == AnimalType.DomesticNpc)
		{
			return;
		}
		List<string> list = new List<string>();
		CaravanInstance caravan = CaravanManager.GetCaravan(this);
		IntRange offspringRange = blueprint.OffspringRange;
		int num = UnityEngine.Random.Range(offspringRange.Min, offspringRange.Max + 1);
		for (int i = 0; i < num; i++)
		{
			AnimalInstance babyAnimal = MonoSingleton<AnimalManager>.Instance.SpawnAnimal(blueprint.GetID(), GetPosition(), (!(UnityEngine.Random.Range(0f, 1f) > 0.5f)) ? BodyType.Male : BodyType.Female, 0);
			babyAnimal.SetAnimalType(animalType);
			babyAnimal.SetName(string.Empty);
			switch (animalType)
			{
			case AnimalType.Domestic:
			{
				StatInstance stat2 = babyAnimal.Stats.GetStat(StatType.AnimalUntrained);
				stat2?.SetCurrent(stat2.Max);
				break;
			}
			case AnimalType.Pet:
			{
				StatInstance stat = babyAnimal.Stats.GetStat(StatType.AnimalWild);
				stat?.SetCurrent(stat.Max);
				break;
			}
			}
			list.Add(babyAnimal.GetFullName());
			if (caravan != null)
			{
				babyAnimal.IncognitoDispose();
				caravan.Creatures.Add(babyAnimal);
			}
			else
			{
				MonoSingleton<TaskController>.Instance.WaitFor(0.5f).Then(delegate
				{
					babyAnimal.GetGoapAgent()?.StartTicker();
				});
			}
		}
		string text = ParseMessageText("message_animal_birth");
		string text2 = TextFormatting.Join(list);
		LogLifeEvent(LifeEventUtils.GetMiscLog(text + ": " + text2));
		if (animalType == AnimalType.Pet || animalType == AnimalType.Domestic)
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowClickableBlackBarMessage(text + ": " + text2, GetGoapAgent().GetView(), follow: true);
		}
	}

	public int GetPregnancyCompletionPercentage()
	{
		return pregnancyTimer?.GetCompletionPercentage() ?? 0;
	}

	public bool IsFormingCaravan()
	{
		return ((IFormCaravanGoapAgent)base.GoapAgent)?.PreparingForCaravan != null;
	}

	public CaravanInstance GetFormingCaravanInstance()
	{
		return ((IFormCaravanGoapAgent)base.GoapAgent).PreparingForCaravan;
	}

	public void IncognitoSpawn(Vector3 worldPosition)
	{
		if (base.GoapAgent == null)
		{
			InitGoap();
		}
		if (isInIncognitoMode)
		{
			VillageSaveData currentVillageData = GlobalSaveController.CurrentVillageData;
			SetWalkableModel(Blueprint.GetWalkableModel(AnimalType));
			UpdatePosition(worldPosition);
			currentVillageData.AddAnimal(this);
			MonoSingleton<AnimalManager>.Instance.InstantiateAnimal(this);
			if (!base.StatsListenersAttached)
			{
				RegisterStatsListeners();
			}
			isInIncognitoMode = false;
			MonoSingleton<TaskController>.Instance.WaitFor(0.1f).Then(delegate
			{
				GetGoapAgent()?.StartTicker();
			});
		}
	}

	public void ResetIncognito()
	{
		isInIncognitoMode = false;
	}

	public void SetPredatorsCannotTargetForHours(float hours, bool maxIfAlreadySet = true)
	{
		WorldDate dateAndTime = GlobalSaveController.CurrentVillageData.DateAndTime;
		if (maxIfAlreadySet)
		{
			predatorCannotTargetUntil = Math.Max(predatorCannotTargetUntil, dateAndTime.MinutesTotal + (long)(hours * (float)dateAndTime.MinutesInHour));
		}
		else
		{
			predatorCannotTargetUntil = dateAndTime.MinutesTotal + (long)(hours * (float)dateAndTime.MinutesInHour);
		}
	}

	public override void IncognitoDispose()
	{
		if (isInIncognitoMode)
		{
			return;
		}
		if (base.Storage != null)
		{
			if (!base.Storage.IsEmpty())
			{
				Vec3Int lhs = ((GetNode() != null) ? GetNode().Position : GetGridPosition());
				Vec3Int rhs = Vec3Int.zero;
				if (lhs == rhs)
				{
					bool isEnabled;
					FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(24, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\AnimalInstance.cs");
					if (isEnabled)
					{
						messageBuilder.AppendLiteral("Cannot drop storage to ");
						messageBuilder.AppendFormatted(lhs);
						messageBuilder.AppendLiteral(".");
					}
					Log.Error(messageBuilder);
				}
				else
				{
					base.Storage.DropAll(lhs);
				}
			}
			base.Storage.ClearAll();
		}
		isInIncognitoMode = true;
		RemoveStatsListeners();
		MonoSingleton<AnimalManager>.Instance.DestroyView(this);
		bool num = GlobalSaveController.CurrentVillageData.RemoveAnimal(this);
		MonoSingleton<CombatAgentManager>.Instance.RemoveCommonCombatAgent(this);
		GetGoapAgent()?.StopTicker();
		if (num)
		{
			MonoSingleton<AnimalController>.Instance.OnAnimalRemoved(this);
		}
	}

	public override bool IsInIncognitoMode()
	{
		return isInIncognitoMode;
	}

	public void ClearCaravanFormingData()
	{
		((IFormCaravanGoapAgent)base.GoapAgent)?.ClearCaravanFormingData();
	}

	public void StartCaravanFormation(CaravanInstance caravan)
	{
		((IFormCaravanGoapAgent)base.GoapAgent)?.StartCaravanFormation(caravan);
		MonoSingleton<AnimalManager>.Instance.RefreshMarkForRoping(this);
	}

	public void GoapAttendPlayerTriggeredEvent(string goalId)
	{
		((AnimalGoapAgent)base.GoapAgent).AttendPlayerTriggeredEvent(goalId);
		MonoSingleton<AnimalManager>.Instance.RefreshMarkForRoping(this);
	}

	public void GoapLeavePlayerTriggeredEvent(string goalId)
	{
		if (base.GoapAgent != null)
		{
			((AnimalGoapAgent)base.GoapAgent).LeavePlayerTriggeredEvent();
			MonoSingleton<AnimalManager>.Instance.RefreshMarkForRoping(this);
		}
	}

	public Sprite GetSprite()
	{
		return AssetUtils.GetSprite(Blueprint.IconPath);
	}

	public bool IsAtEvent()
	{
		if (!MonoSingleton<PlayerTriggeredEventManager>.IsInstantiated())
		{
			return false;
		}
		return MonoSingleton<PlayerTriggeredEventManager>.Instance.IsAnimalAtEvent(this);
	}

	public override void Serialize(FVSerializer serializer)
	{
		base.Serialize(serializer);
		serializer.Write("animalName", animalName);
		serializer.WriteEnum("animalType", animalType);
		serializer.Write("lifePhaseId", lifePhaseId);
		serializer.WriteEnum("orderType", orderType);
		serializer.WriteEnum("previousOrder", previousOrder);
		serializer.Write("isLeavingMap", isLeavingMap);
		serializer.Write("phaseStartMinutes", phaseStartMinutes);
		serializer.Write("phaseEndMinutes", phaseEndMinutes);
		serializer.WriteEnum("gender", gender);
		serializer.Write("productionInstances", productionInstances);
		serializer.Write("hoursSinceLastTamingAttempt", hoursSinceLastTamingAttempt);
		serializer.Write("tamingAttemptsCount", tamingAttemptsCount);
		serializer.Write("lastTamerName", lastTamerName);
		serializer.Write("lastTamingAttemptSuccessful", lastTamingAttemptSuccessful);
		serializer.Write("hoursSinceLastTrainingAttempt", hoursSinceLastTrainingAttempt);
		serializer.Write("trainingAttemptsCount", trainingAttemptsCount);
		serializer.Write("lastTrainerName", lastTrainerName);
		serializer.Write("lastTrainingAttemptSuccessful", lastTrainingAttemptSuccessful);
		serializer.Write("ageInDays", ageInDays);
		serializer.Write("pregnancyTimer", pregnancyTimer);
		serializer.Write("pregnant", pregnant);
		serializer.Write("isInIncognitoMode", isInIncognitoMode);
		serializer.Write("attackGroupId", attackGroupId);
		serializer.Write("pestGroupId", pestGroupId);
		serializer.Write("petBattleEnabled", petBattleEnabled);
		serializer.Write("petHaulEnabled", petHaulEnabled);
		serializer.Write("petPestControlEnabled", petPestControlEnabled);
		serializer.Write("predatorCannotTargetUntil", predatorCannotTargetUntil);
		serializer.Write("traderStockItem", traderStockItem);
		serializer.Write("info", info);
		serializer.Write("isUnnamed", IsUnnamed);
	}

	public AnimalInstance(FVDeserializer deserializer)
		: base(deserializer)
	{
		animalName = deserializer.ReadString("animalName");
		animalType = deserializer.ReadEnum("animalType", AnimalType.Domestic);
		lifePhaseId = deserializer.ReadString("lifePhaseId");
		orderType = deserializer.ReadEnum("orderType", AnimalOrderType.None);
		previousOrder = deserializer.ReadEnum("previousOrder", AnimalOrderType.None);
		isLeavingMap = deserializer.ReadBool("isLeavingMap");
		phaseStartMinutes = deserializer.ReadLong("phaseStartMinutes", 0L);
		phaseEndMinutes = deserializer.ReadLong("phaseEndMinutes", 0L);
		gender = deserializer.ReadEnum("gender", BodyType.None);
		productionInstances = deserializer.ReadObjectList<AnimalProductionInstance>("productionInstances");
		hoursSinceLastTamingAttempt = deserializer.ReadInt("hoursSinceLastTamingAttempt");
		tamingAttemptsCount = deserializer.ReadInt("tamingAttemptsCount");
		lastTamerName = deserializer.ReadString("lastTamerName");
		lastTamingAttemptSuccessful = deserializer.ReadBool("lastTamingAttemptSuccessful");
		hoursSinceLastTrainingAttempt = deserializer.ReadInt("hoursSinceLastTrainingAttempt");
		trainingAttemptsCount = deserializer.ReadInt("trainingAttemptsCount");
		lastTrainerName = deserializer.ReadString("lastTrainerName");
		lastTrainingAttemptSuccessful = deserializer.ReadBool("lastTrainingAttemptSuccessful");
		ageInDays = deserializer.ReadInt("ageInDays");
		pregnancyTimer = deserializer.ReadObject<Timer>("pregnancyTimer");
		pregnant = deserializer.ReadBool("pregnant");
		isInIncognitoMode = deserializer.ReadBool("isInIncognitoMode");
		attackGroupId = deserializer.ReadString("attackGroupId");
		pestGroupId = deserializer.ReadString("pestGroupId");
		petBattleEnabled = deserializer.ReadBool("petBattleEnabled");
		petHaulEnabled = deserializer.ReadBool("petHaulEnabled");
		petPestControlEnabled = deserializer.ReadBool("petPestControlEnabled");
		predatorCannotTargetUntil = deserializer.ReadLong("predatorCannotTargetUntil", 0L);
		traderStockItem = deserializer.ReadObject<TraderStockItem>("traderStockItem");
		info = deserializer.ReadObject<CreatureInfoBase>("info");
		IsUnnamed = deserializer.ReadBool("isUnnamed");
	}

	public bool IsProtectorInProximity()
	{
		if (GetNode() == null)
		{
			return true;
		}
		if (!base.Map.ProtectorCreatureManager.IsProtected(GetNode().Index))
		{
			return base.Map.ProtectorBuildingManager.IsProtected(GetNode().Index);
		}
		return true;
	}

	protected override void OnGridSpaceChanged(MapNode oldNode, MapNode newNode, bool firstTick)
	{
		base.OnGridSpaceChanged(oldNode, newNode, firstTick);
		if ((newNode != null && base.Map.ProtectorBuildingManager.IsProtected(newNode.Index)) || (oldNode != null && base.Map.ProtectorBuildingManager.IsProtected(oldNode.Index)))
		{
			SetPredatorsCannotTargetForHours(2.5f);
		}
		if ((newNode != null && base.Map.ProtectorCreatureManager.IsProtected(newNode.Index)) || (oldNode != null && base.Map.ProtectorCreatureManager.IsProtected(oldNode.Index)))
		{
			SetPredatorsCannotTargetForHours(2.5f);
		}
	}

	public override void OnPredatorProtectionChanged()
	{
		MapNode node = GetNode();
		if (node != null)
		{
			if (base.Map.ProtectorBuildingManager.IsProtected(node.Index))
			{
				SetPredatorsCannotTargetForHours(2.5f);
			}
			if (base.Map.ProtectorCreatureManager.IsProtected(node.Index))
			{
				SetPredatorsCannotTargetForHours(2.5f);
			}
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
