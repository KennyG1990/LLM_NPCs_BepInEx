using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
using NSMedieval.Components.Base;
using NSMedieval.Construction;
using NSMedieval.Controllers;
using NSMedieval.Dictionary;
using NSMedieval.FloatingOverlaySystem;
using NSMedieval.GameEventSystem;
using NSMedieval.Goap;
using NSMedieval.Heraldry;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.PlayerTriggeredEventSystem;
using NSMedieval.Repository;
using NSMedieval.RoomDetection;
using NSMedieval.Serialization;
using NSMedieval.StatsSystem;
using NSMedieval.Tools;
using NSMedieval.Tools.Math;
using NSMedieval.Types;
using NSMedieval.UI;
using NSMedieval.UI.Utils;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.View;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using NSMedieval.WorldMap;
using UnityEngine;

namespace NSMedieval.State;

[Serializable]
[FVSerializableKey("HumanoidInstance", "WorkerInstance, EnemyInstance, NPCInstance")]
public class HumanoidInstance : CreatureBase, IDamageDealAgent, IDamageCommonAgent, IGoapTargetable, IGameDisposable, IDisposable, IEquipableAgent, IPathfindingAgent, IGoapAgentOwner, IEventParticipant, ISerializationCallbackReceiver, ISleepAgent, IProductionAgent, IStorageAgent, IToolAgent, IHarvestAgent, IFormCaravanAgent, IRopableAgent
{
	[SerializeField]
	private List<ActionTagType> blockedActionTags = new List<ActionTagType>();

	[SerializeField]
	private HumanoidInfo info;

	[SerializeField]
	private List<string> perkIds = new List<string>();

	[SerializeField]
	private WorkerSkills skills;

	[SerializeField]
	private VillagePlaceReference originVillage;

	[SerializeField]
	private string factionId;

	[SerializeField]
	private PlayerTriggeredEventInstance activeEventInstance;

	[SerializeField]
	private bool isLeaving;

	[SerializeField]
	private List<HumanoidBehaviour> behaviours;

	[SerializeField]
	private int activeBehaviourIndex;

	[SerializeField]
	private bool isInIncognitoMode;

	[SerializeField]
	private string customWarningMessage;

	[NonSerialized]
	private BehaviourType activeBehaviourType;

	[NonSerialized]
	private HumanoidBehaviour activeBehaviour;

	[NonSerialized]
	private WorkerBehaviour workerBehaviour;

	[NonSerialized]
	private PrisonerBehaviour prisonerBehaviour;

	[NonSerialized]
	private EnemyBehaviour enemyBehaviour;

	[NonSerialized]
	private TraderBehaviour traderBehaviour;

	[NonSerialized]
	private CaptiveLabourerBehaviour captiveLabourerBehaviour;

	[NonSerialized]
	private NegotiatorBehaviour negotiatorBehaviour;

	[NonSerialized]
	private List<WorkerSkill> skillsCreationHash;

	[NonSerialized]
	private HumanoidInstanceGoalPreferences goalPreferences;

	[NonSerialized]
	private HumanoidInstanceBelief humanoidBelief;

	[NonSerialized]
	private List<string> ageEffectors;

	[NonSerialized]
	private List<Perk> perks = new List<Perk>();

	[NonSerialized]
	private List<WorkerSkill> skillsOrdered;

	[NonSerialized]
	private bool producedFromGenerator;

	[NonSerialized]
	private FactionInstance factionCache;

	[NonSerialized]
	private bool factionCacheInit;

	[NonSerialized]
	private bool isUnfaintPlanned;

	[NonSerialized]
	private IGoapTargetable ropedTo;

	public bool IsBulidProgressAlowed { get; set; }

	public BehaviourType ActiveBehaviourType => activeBehaviourType;

	public float XpOverCapMultiplier => 0.1f;

	public HumanoidInfo Info => info;

	public WorkerSkills Skills => skills;

	public List<ActionTagType> BlockedActionTags => blockedActionTags;

	public WarmthInstance WarmthInstance { get; private set; }

	public override float RopedFollowRange
	{
		get
		{
			if (activeBehaviourType == BehaviourType.None)
			{
				return 2f;
			}
			return activeBehaviour.RopedFollowRange;
		}
	}

	public bool OperatingSiegeWeapon { get; set; }

	public bool RopedAllowedToIdle => activeBehaviour?.RopedAllowedToIdle ?? false;

	public bool RopedShouldAlwaysWalk => activeBehaviour?.RopedShouldAlwaysWalk ?? false;

	public override DamageTakingAgentType DamageAgentType => activeBehaviour.DamageAgentType;

	public override bool CanMakeDirtPath => true;

	public override string IconPath => "human_generic";

	public override float WealthPoints
	{
		get
		{
			if (base.HasDisposed)
			{
				return 0f;
			}
			float num = 0f;
			foreach (WorkerSkill skill in skills.Skills)
			{
				num += (float)skill.Level;
			}
			num *= 1f;
			float num2 = 0f;
			foreach (EquipmentInstance item in GetEquipment())
			{
				num2 += item.GetWealth();
			}
			num2 *= 0.2f;
			float normalizedPercentage = Stats.GetStat(StatType.Health).GetNormalizedPercentage();
			return 20f + num * normalizedPercentage + num2;
		}
	}

	public override int CaravanStorageCapacity => base.Storage.StorageBase.Capacity;

	public bool DontSpawnCarcassOnDispose { get; set; }

	public bool SkipHistoryOnDeath { get; set; }

	public override ThermalModel ThermalModel => CurrentHumanType?.ThermalModel;

	public string CustomWarningMessage
	{
		get
		{
			return customWarningMessage;
		}
		set
		{
			customWarningMessage = value;
		}
	}

	private Worker WorkerBlueprint => activeBehaviour.Blueprint as Worker;

	public HumanType CurrentHumanType => activeBehaviour.HumanType;

	public HumanoidBehaviour ActiveBehaviour => activeBehaviour;

	public WorkerBehaviour WorkerBehaviour => workerBehaviour;

	public PrisonerBehaviour PrisonerBehaviour => prisonerBehaviour;

	public EnemyBehaviour EnemyBehaviour => enemyBehaviour;

	public TraderBehaviour TraderBehaviour => traderBehaviour;

	public CaptiveNpcBehaviour CaptiveNpcBehaviour
	{
		get
		{
			if (!IsCaptive())
			{
				return null;
			}
			return activeBehaviour as CaptiveNpcBehaviour;
		}
	}

	public CaptiveLabourerBehaviour CaptiveLabourerBehaviour => captiveLabourerBehaviour;

	public NegotiatorBehaviour NegotiatorBehaviour => negotiatorBehaviour;

	protected override bool CurrentProximityDetection => activeBehaviour?.ProximityDetection ?? false;

	public override DietModel CurrentDietModel => activeBehaviour?.GetDietModel();

	public override DietModel CurrentDrinkDietModel => activeBehaviour?.GetDrinkDietModel();

	public override float OptimalNodeTemperatureRangeMin => WarmthInstance.OptimalRangeCacheMin;

	public override float OptimalNodeTemperatureRangeMax => WarmthInstance.OptimalRangeCacheMax;

	public bool IsStatsInitialized
	{
		get
		{
			if (base.Stats != null)
			{
				return base.Stats.Owner != null;
			}
			return false;
		}
	}

	public override bool IsProtectiveAgainstPredators
	{
		get
		{
			if (!base.HasFainted && !base.HasDied)
			{
				return !base.HasDisposed;
			}
			return false;
		}
	}

	public FactionInstance Faction
	{
		get
		{
			if (!factionCacheInit)
			{
				if (!string.IsNullOrEmpty(factionId))
				{
					factionCacheInit = true;
					factionCache = MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.Data.FactionInstances.FirstOrDefault((FactionInstance fi) => factionId.Equals(fi.BlueprintId));
				}
				else if (originVillage != null && originVillage.FactionInstance != null)
				{
					factionCache = originVillage.FactionInstance;
					factionId = factionCache.BlueprintId;
					factionCacheInit = true;
				}
			}
			return factionCache;
		}
	}

	public List<WorkerSkill> SkillsCreationHash
	{
		get
		{
			return skillsCreationHash;
		}
		set
		{
			skillsCreationHash = value;
		}
	}

	public List<WorkerSkill> SkillsOrdered
	{
		get
		{
			if (skillsOrdered == null)
			{
				skillsOrdered = new List<WorkerSkill>();
			}
			if (skillsOrdered.Count == 0 && Skills?.Skills != null)
			{
				LocalizationController loc = MonoSingleton<LocalizationController>.Instance;
				IOrderedEnumerable<WorkerSkill> collection = Skills.Skills.OrderBy((WorkerSkill skill) => (skill != null) ? loc.GetText(skill.GetSkillTextKey()) : string.Empty);
				skillsOrdered.AddRange(collection);
			}
			return skillsOrdered;
		}
	}

	public List<Perk> Perks
	{
		get
		{
			if ((perks != null && perks.Count != 0) || perkIds == null || perkIds.Count <= 0)
			{
				if (perks == null)
				{
					perks = new List<Perk>();
				}
				return perks;
			}
			perks = new List<Perk>();
			foreach (string perkId in perkIds)
			{
				Perk byID = Repository<PerkRepository, Perk>.Instance.GetByID(perkId);
				if (!(byID == null))
				{
					perks.Add(byID);
				}
			}
			return perks;
		}
	}

	public PlayerTriggeredEventInstance ActiveEventInstance
	{
		get
		{
			return activeEventInstance;
		}
		set
		{
			activeEventInstance = value;
		}
	}

	public HumanoidInstanceGoalPreferences GoalPreferences => goalPreferences ?? (goalPreferences = new HumanoidInstanceGoalPreferences(this));

	public HumanoidInstanceBelief HumanoidBelief => humanoidBelief ?? (humanoidBelief = new HumanoidInstanceBelief(this));

	public bool IsLeaving
	{
		get
		{
			return isLeaving;
		}
		set
		{
			bool isEnabled;
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(13, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("IsLeaving = ");
				messageBuilder.AppendFormatted(value);
				messageBuilder.AppendLiteral(" ");
				messageBuilder.AppendFormatted(this);
			}
			Log.Info(messageBuilder);
			if (value && ActiveBehaviour is EnemyBehaviour enemyBehaviour)
			{
				enemyBehaviour.CurrentOrder = null;
			}
			isLeaving = value;
			base.CombatAi?.SetState(CombatAiState.EnemyIsRetreating, value);
		}
	}

	public bool IsUnfaintPlanned => isUnfaintPlanned;

	public VillagePlaceReference OriginVillage => originVillage;

	public override bool ForbidWeapon
	{
		set
		{
			if (base.ForbidWeapon != value)
			{
				base.ForbidWeapon = value;
				if (MonoSingleton<ThreadingJobSystem>.IsInstantiated() && Thread.CurrentThread == MonoSingleton<ThreadingJobSystem>.Instance.MainThread)
				{
					GetAgentView<HumanoidView>()?.ForbidWeaponAnimation(ForbidWeapon);
				}
			}
		}
	}

	public override StatsInstance Stats
	{
		get
		{
			if (base.Stats == null || base.Stats.Owner == null)
			{
				InitStats();
			}
			return base.Stats;
		}
	}

	public bool IsProducing { get; set; }

	public HourType[] ScheduleHours => WorkerBehaviour?.ScheduleHours;

	public event Action<HumanoidInstance> BeforeDeathEvent;

	public event Action<HumanoidInstance> BeforeFaintEvent;

	public event Action<HumanoidInstance, EquipmentInstance> EquipEvent;

	public event Action<HumanoidInstance, EquipmentInstance> DropEvent;

	public HumanoidInstance(string id, Vector3 position, bool producedFromGenerator, VillagePlace originVillage = null, FactionInstance faction = null)
		: base(id, position)
	{
		behaviours = new List<HumanoidBehaviour>();
		skills = new WorkerSkills();
		if (originVillage != null)
		{
			this.originVillage = new VillagePlaceReference(originVillage);
			factionId = this.originVillage.FactionInstance.BlueprintId;
			factionCache = this.originVillage.FactionInstance;
			factionCacheInit = true;
		}
		if (faction != null)
		{
			factionId = faction.BlueprintId;
			factionCache = faction;
			factionCacheInit = true;
		}
		this.producedFromGenerator = producedFromGenerator;
		MonoSingleton<OptionsController>.Instance.LanguageChangedEvent += OnLanguageChange;
	}

	public float GetXpCapValue(SkillType skillType)
	{
		if (!IsStatsInitialized)
		{
			Log.Warning("Warning: don't use HumanoidInstance.XpCapValue in HomeScene (before game has been loaded).", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			return 0f;
		}
		float num = GetAttributeValue(AttributeType.XpCapMultiplier);
		if (Enum.TryParse<AttributeType>($"{skillType}XpCap", out var result))
		{
			bool isEnabled;
			FVLogTraceInterpolationHandler messageBuilder = new FVLogTraceInterpolationHandler(28, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("XpCapMultiplier: ");
				messageBuilder.AppendFormatted(num);
				messageBuilder.AppendLiteral(" *= XpCap: ");
				messageBuilder.AppendFormatted(GetAttributeValue(result));
			}
			Log.Trace(messageBuilder);
			num *= GetAttributeValue(result);
		}
		return 1600f * num;
	}

	public virtual GlobalEffectorDomain GetGlobalEffectorDomain()
	{
		if (WorkerBehaviour != null)
		{
			return GlobalEffectorDomain.Worker;
		}
		GlobalEffectorDomain globalEffectorDomain = GlobalEffectorDomain.None;
		if (IsEnemy())
		{
			globalEffectorDomain |= GlobalEffectorDomain.Enemy;
		}
		if ((activeBehaviourType & BehaviourType.Prisoner) != 0)
		{
			globalEffectorDomain = ((((PrisonerBehaviour)activeBehaviour).Owner == null) ? (globalEffectorDomain | GlobalEffectorDomain.PlayersPrisoner) : (globalEffectorDomain | GlobalEffectorDomain.EnemyPrisoner));
		}
		else if ((activeBehaviourType & BehaviourType.CaptiveLabourer) != 0)
		{
			globalEffectorDomain |= GlobalEffectorDomain.CaptiveLabourer;
		}
		if ((activeBehaviourType & BehaviourType.Trader) != 0)
		{
			globalEffectorDomain |= GlobalEffectorDomain.Trader;
		}
		if ((activeBehaviourType & BehaviourType.TraderBodyguard) != 0)
		{
			globalEffectorDomain |= GlobalEffectorDomain.TraderBodyGuard;
		}
		return globalEffectorDomain;
	}

	public int GetCaravanCarryWeight(bool allowFainted = false)
	{
		if (!CanFormCaravan(allowFainted))
		{
			return 0;
		}
		return base.Storage.StorageBase.Capacity;
	}

	public void InitStorage(StorageBase storageBase)
	{
		base.Storage = new Storage(storageBase);
	}

	public void LeaveMapSilent()
	{
		base.CombatAi?.Disable();
		base.GoapAgent?.Abort();
		base.GoapAgent?.ForceNextGoal("LeaveMapGoal");
	}

	public Sprite GetHeraldryCrest()
	{
		if (Faction != null)
		{
			return Faction.Blueprint.HeraldryCrestSprite;
		}
		return MonoSingleton<HeraldryManager>.Instance.Crest.sprite;
	}

	public Sprite GetHeraldryBackground()
	{
		if (Faction != null)
		{
			return Faction.Blueprint.HeraldryBackgroundSprite;
		}
		return MonoSingleton<HeraldryManager>.Instance.Pattern.sprite;
	}

	public bool IsCannibal()
	{
		return Perks.Any((Perk perk) => perk.GetID().Equals("Cannibal"));
	}

	protected override List<TimedWounds> GetFireWounds()
	{
		return CurrentHumanType?.FireWounds;
	}

	public override bool IsInIncognitoMode()
	{
		return isInIncognitoMode;
	}

	public override string GetFullName()
	{
		return info.GetFullName();
	}

	public override Transform GetTransform()
	{
		HumanoidView agentView = GetAgentView<HumanoidView>();
		if (!(agentView != null))
		{
			return null;
		}
		return agentView.transform;
	}

	public override float GetWeight()
	{
		return Info.GetWeight();
	}

	public void SetForbidWeaponSilent(bool value)
	{
		base.ForbidWeapon = value;
	}

	public override int GetHeat()
	{
		if (base.HasDied || base.HasDisposed || CurrentHumanType?.ThermalModel == null)
		{
			return 0;
		}
		return CurrentHumanType.ThermalModel.Emission;
	}

	public override void FinalizeDispose()
	{
		if (GlobalSaveController.CurrentVillageData?.WorldMapData?.Caravans != null)
		{
			foreach (CaravanInstance caravan in GlobalSaveController.CurrentVillageData.WorldMapData.Caravans)
			{
				if (caravan?.Workers != null && caravan.Workers.Remove(this))
				{
					bool isEnabled;
					FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(54, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
					if (isEnabled)
					{
						messageBuilder.AppendLiteral("FinalizeDispose: removed '");
						messageBuilder.AppendFormatted(this);
						messageBuilder.AppendLiteral("' from caravan ");
						messageBuilder.AppendFormatted(caravan.UniqueId);
						messageBuilder.AppendLiteral(" workers set.");
					}
					Log.Warning(messageBuilder);
				}
			}
		}
		if (behaviours != null)
		{
			foreach (HumanoidBehaviour behaviour in behaviours)
			{
				behaviour?.Dispose();
			}
		}
		if (MonoSingleton<OptionsController>.IsInstantiated())
		{
			MonoSingleton<OptionsController>.Instance.LanguageChangedEvent -= OnLanguageChange;
		}
		if (MonoSingleton<WorldTimeManager>.IsInstantiated())
		{
			MonoSingleton<WorldTimeManager>.Instance.DateUpdateEvent -= OnChangeDate;
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(OnHourChange);
		}
		this.BeforeDeathEvent = null;
		this.BeforeFaintEvent = null;
		base.FinalizeDispose();
		activeEventInstance = null;
		behaviours?.Clear();
		factionCache = null;
		factionCacheInit = false;
		goalPreferences?.SetOwner(null);
		goalPreferences = null;
		humanoidBelief?.SetHumanOwner(null);
		humanoidBelief = null;
		ropedTo = null;
		skillsOrdered?.Clear();
		skillsOrdered = null;
		skillsCreationHash?.Clear();
		skillsCreationHash = null;
		skills?.Dispose();
		skills = null;
	}

	public override void Spawn(bool startGoapTicker = true)
	{
		base.Inventory.Reinstance(GetPosition);
		base.Inventory.OnDroppedEvent += OnEquipmentDropped;
		base.Inventory.OnEquipedEvent += OnEquipmentEquipped;
		if (!isInIncognitoMode)
		{
			AttachToDateUpdateEvent();
		}
		WarmthInstance = new WarmthInstance(Stats, base.Inventory.GetEquipments, CurrentHumanType.Warmth);
		if (base.IsFirstSpawn)
		{
			foreach (EquipmentInstance equipment in base.Inventory.GetEquipments())
			{
				equipment.StartEquipEffects(Stats);
			}
			StatInstance stat = Stats.GetStat(StatType.ReligiousAlignment);
			SetReligiousAlignment(Mathf.Lerp(stat.Min, stat.Max, info.ReligiousAlignment));
			activeBehaviour.OnFirstSpawn();
		}
		activeBehaviour.OnSpawn();
		if (!isInIncognitoMode)
		{
			FirePerkEffectors();
		}
		if (!string.IsNullOrEmpty(info?.Background?.Effector))
		{
			Stats.StartEffector(info.Background.Effector);
		}
		if (!string.IsNullOrEmpty(info?.BackStory?.Effector))
		{
			Stats.StartEffector(info.BackStory.Effector);
		}
		MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
		{
			HandleWeightEffectors();
			HandleHeightEffectors();
			HandleAgeEffectors();
		});
		base.Spawn();
		InitBehaviour(startGoapTicker);
		activeBehaviour.HumanoidRoleOwner.RoleInstance?.SetupAfterLoad();
		OnGridSpaceChanged(null, GetNode(), firstTick: true);
		RefreshRopedAnimationParameter();
		activeBehaviour.OnAfterSpawn();
	}

	public void InitStorages()
	{
		if (base.IsFirstSpawn)
		{
			base.Storage = new Storage(CurrentHumanType.StorageBase);
			InitFoodStorage();
			InitMedicineStorage();
		}
		if (base.Inventory == null)
		{
			base.Inventory = new InventoryInstance(CurrentHumanType.AvailableEquipmentSlots, GetPosition);
		}
		base.Inventory.Init();
	}

	public void SetReligiousAlignment(float value = -1f)
	{
		StatInstance stat = Stats.GetStat(StatType.ReligiousAlignment);
		if (!Mathf.Approximately(value, -1f))
		{
			stat.SetCurrent(value);
			return;
		}
		IntRange intRange = originVillage?.FactionInstance?.Blueprint?.FactionType?.FaithRange;
		if (intRange != null)
		{
			stat.SetCurrent(NSMedieval.Tools.Math.Random.Range(intRange.Min, intRange.Max + 1));
		}
	}

	public void AttachToDateUpdateEvent()
	{
		MonoSingleton<WorldTimeManager>.Instance.DateUpdateEvent += OnChangeDate;
		MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent += new Action(OnHourChange);
	}

	public override void Faint()
	{
		this.BeforeFaintEvent?.Invoke(this);
		base.Faint();
		activeBehaviour.HandleOnFaint();
	}

	public override void UnFaint()
	{
		if (GlobalSaveController.CurrentVillageData.Raids.Any((ActiveRaidInfo raidInfo) => !raidInfo.HasEnded))
		{
			string restEffector = Repository<WorkerBaseRepository, Worker>.Instance.BaseWorker.DefaultHumanType.RestEffector;
			if (!Stats.IsEffectorActive(restEffector))
			{
				isUnfaintPlanned = true;
				return;
			}
		}
		isUnfaintPlanned = false;
		base.UnFaint();
	}

	protected override void OnEquipmentDestroyed(EquipmentInstance item)
	{
		base.OnEquipmentDestroyed(item);
		activeBehaviour.HandleOnEquipmentDestroyed(item);
	}

	private void OnEquipmentDropped(EquipmentInstance instance)
	{
		this.DropEvent?.Invoke(this, instance);
		activeBehaviour.OnEquipmentDropped(instance);
	}

	private void OnEquipmentEquipped(EquipmentInstance instance)
	{
		this.EquipEvent?.Invoke(this, instance);
		activeBehaviour.OnEquipmentEquipped(instance);
	}

	protected override void OnRoomChanged(Room oldRoom, Room newRoom)
	{
		base.OnRoomChanged(oldRoom, newRoom);
		activeBehaviour.OnRoomChanged(oldRoom, newRoom);
	}

	public void AddExperience(SkillType skill, float amount, bool isSilent = false)
	{
		bool isEnabled;
		FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(22, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Adding Experience for ");
			messageBuilder.AppendFormatted(skill);
		}
		Log.Debug(messageBuilder);
		float num = GetAttributeValue(AttributeType.GlobalXpGain);
		FVLogTraceInterpolationHandler messageBuilder2 = new FVLogTraceInterpolationHandler(32, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
		if (isEnabled)
		{
			messageBuilder2.AppendLiteral("Adding Experience for ");
			messageBuilder2.AppendFormatted(skill);
			messageBuilder2.AppendLiteral(", Global: ");
			messageBuilder2.AppendFormatted(num);
		}
		Log.Trace(messageBuilder2);
		if (Enum.TryParse<AttributeType>($"{skill}XpGainMult", out var result))
		{
			messageBuilder2 = new FVLogTraceInterpolationHandler(14, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			if (isEnabled)
			{
				messageBuilder2.AppendLiteral(" + + + Skill: ");
				messageBuilder2.AppendFormatted(GetAttributeValue(result));
			}
			Log.Trace(messageBuilder2);
			num *= GetAttributeValue(result);
		}
		else
		{
			FVLogErrorInterpolationHandler messageBuilder3 = new FVLogErrorInterpolationHandler(45, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			if (isEnabled)
			{
				messageBuilder3.AppendLiteral("Couldn't parse AttributeType for '");
				messageBuilder3.AppendFormatted(skill);
				messageBuilder3.AppendLiteral("XpGainMult'");
			}
			Log.Error(messageBuilder3);
		}
		amount *= num;
		WorkerSkill skill2 = Skills.GetSkill(skill);
		if (skill2 == null || skill2.IsMaxLevelReached())
		{
			return;
		}
		if (IsXpCapReached(skill))
		{
			amount *= XpOverCapMultiplier;
		}
		float experience = skill2.Experience;
		if (Skills.AddExperience(skill, amount))
		{
			string spriteAsset = AssetUtils.GetSpriteAsset(skill.ToString().ToLower() ?? "");
			string text = spriteAsset + " " + Info.GetFullName() + " " + MonoSingleton<LocalizationController>.Instance.GetText("skill_name_" + skill) + " " + string.Format("{0} {1} {2}", MonoSingleton<LocalizationController>.Instance.GetText("general_skill_level_up"), skill2.Level, spriteAsset);
			AnimatedAgentView agentView = GetAgentView<AnimatedAgentView>();
			MonoSingleton<BlackBarMessageController>.Instance.ShowClickableBlackBarMessage(text, agentView, follow: true);
			LogLifeEvent(LifeEventUtils.GetMiscLog(text));
			if (!isSilent && WorkerBehaviour != null)
			{
				GetAgentView<WorkerView>()?.PlayParticlesOnSkillUp();
			}
			MonoSingleton<AchievementManager>.Instance.IncreaseStat("WORKER_SKILL_LEVEL_UP_CNT");
		}
		int num2 = Mathf.RoundToInt(skill2.Experience - experience);
		if (!isSilent)
		{
			CombatAiAgent combatAi = base.CombatAi;
			if ((combatAi == null || !combatAi.IsStateSet(CombatAiState.IsInCombat)) && num2 > 0 && WorkerBehaviour != null)
			{
				GetAgentView<WorkerView>()?.ShowExperienceAddedPopup(skill, num2);
			}
		}
	}

	public float GetXpAddedToday(SkillType skillType)
	{
		if (Skills == null || !IsStatsInitialized)
		{
			return 0f;
		}
		return Skills.GetSkill(skillType)?.ExperienceAddedToday ?? 0f;
	}

	public bool IsXpCapReached(SkillType skillType)
	{
		if (!IsStatsInitialized)
		{
			return false;
		}
		return GetXpAddedToday(skillType) >= GetXpCapValue(skillType);
	}

	public override CreatureInfoBase GetInfo()
	{
		return info;
	}

	public override CharacterInfoBase GetCharacterInfo()
	{
		return info;
	}

	public HumanoidInfo GetWorkerInfo()
	{
		return info;
	}

	public override bool CanConsume(DietModel dietModel, ResourcePileInstance resourcePile)
	{
		if (resourcePile == null || resourcePile.Blueprint == null)
		{
			return false;
		}
		if (activeBehaviour.CanConsume(dietModel, resourcePile))
		{
			return base.CanConsume(dietModel, resourcePile);
		}
		return false;
	}

	public override bool CanConsume(DietModel dietModel, ResourceInstance resourceInstance)
	{
		if (resourceInstance == null || resourceInstance.Blueprint == null)
		{
			return false;
		}
		if (activeBehaviour.CanConsume(dietModel, resourceInstance))
		{
			return base.CanConsume(dietModel, resourceInstance);
		}
		return false;
	}

	public bool HasWeapon()
	{
		if (base.Inventory != null)
		{
			return (base.Inventory.OccupiedSlots & EquipmentSlotType.RightHand) != 0;
		}
		return false;
	}

	public EquipmentInstance GetWeapon()
	{
		return base.Inventory.GetItem(EquipmentSlotType.RightHand);
	}

	public override void SetTarget(IDamageTakingAgent target)
	{
		WorkerBehaviour workerBehaviour = WorkerBehaviour;
		if (workerBehaviour == null || !workerBehaviour.IsBanished)
		{
			base.SetTarget(target);
		}
	}

	public bool CanEquip(Equipment equipment)
	{
		List<SkillLevelPair> list = equipment?.RequiredSkills;
		if (list == null)
		{
			return false;
		}
		foreach (SkillLevelPair item in list)
		{
			if (SkillIsBlocked(item.Key) || GetSkillLevel(item.Key) < item.Value)
			{
				return false;
			}
		}
		return true;
	}

	public override void SetWeaponVisibility(bool isVisible)
	{
		WorkerBehaviour workerBehaviour = WorkerBehaviour;
		if (workerBehaviour != null && workerBehaviour.IsDrafting)
		{
			isVisible = true;
		}
		HumanoidView agentView = GetAgentView<HumanoidView>();
		if (!(agentView == null) && !(agentView.BodyPreview == null))
		{
			if (isVisible)
			{
				agentView.BodyPreview.ShowWeapons();
			}
			else
			{
				agentView.BodyPreview.HideWeapons();
			}
			MonoSingleton<AnimationController>.Instance.SetAnimatorParameter(this, "IsCombatAlert", isVisible && (base.Inventory?.GetEquipments()?.Any((EquipmentInstance item) => item.Blueprint.ItemType == ItemType.Weapon)).GetValueOrDefault());
			base.SetWeaponVisibility(isVisible);
		}
	}

	public override Transform GetWeaponTransform(int hand)
	{
		return GetAgentView<HumanoidView>().BodyPreview.GetWeaponTransform(hand);
	}

	public int GetSkillLevel(SkillType skill)
	{
		if (base.HasDisposed || base.HasDied)
		{
			Log.Warning("Worker has died or disposed and we are trying to access their skill", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			return 0;
		}
		WorkerSkill skill2 = Skills.GetSkill(skill);
		if (skill2 == null)
		{
			Log.Warning(skill.ToString() + " is null. Humanoid name is " + info.GetFullName(), "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			return 0;
		}
		return skill2.Level;
	}

	public bool SkillIsBlocked(SkillType skillType)
	{
		if (BlockedActionTags == null || BlockedActionTags.Count == 0)
		{
			return false;
		}
		List<ActionTagType> skillTags = Repository<SkillTagRepository, SkillTag>.Instance.GetSkillTags(skillType);
		if (skillTags.Count == 0)
		{
			return false;
		}
		return skillTags.Any((ActionTagType actionTagType) => BlockedActionTags.Contains(actionTagType));
	}

	public void SetSkills(List<WorkerSkill> skills)
	{
		if (skills == null || skills.Count == 0)
		{
			return;
		}
		if (this.skills == null)
		{
			this.skills = new WorkerSkills(skills);
			return;
		}
		foreach (WorkerSkill skill in skills)
		{
			this.skills.GetSkill(skill.Id)?.AddLevels(skill.Level);
		}
	}

	public void SetSkillLevel(SkillType skill, int level)
	{
		Skills.GetSkill(skill).SetLevel(level);
	}

	public override void FaceObject(Vector3 objectPosition)
	{
		activeBehaviour?.FaceObject(objectPosition);
	}

	public void FaceAway(Vector3 objectPosition)
	{
		activeBehaviour?.FaceAway(objectPosition);
	}

	public void FaceObject(Transform transform)
	{
		if (!(transform == null) && MonoSingleton<WorkerController>.IsInstantiated())
		{
			MonoSingleton<WorkerController>.Instance.FaceObject(this, transform);
		}
	}

	public void LookAt(Transform transform)
	{
		if (!(transform == null) && MonoSingleton<WorkerController>.IsInstantiated())
		{
			MonoSingleton<WorkerController>.Instance.LookAt(this, transform);
		}
	}

	public void SetEulerAngle(Vector3 eulerAngle)
	{
		if (MonoSingleton<WorkerController>.IsInstantiated())
		{
			MonoSingleton<WorkerController>.Instance.SetEulerAngle(this, eulerAngle);
		}
	}

	public override void SetNextRoundFlammable(bool isNextFlammable, bool ignoreAllowed = false)
	{
		if (!isNextFlammable || ignoreAllowed || base.FlammableProjectilesAllowed)
		{
			EquipmentInstance weapon = CombatUtils.GetWeapon(this);
			if (weapon != null && weapon.CanFireFlammableProjectiles)
			{
				weapon.SetNextRoundFlammable(isNextFlammable);
			}
		}
	}

	public override bool IsNextRoundFlammable()
	{
		EquipmentInstance weapon = CombatUtils.GetWeapon(this);
		if (weapon == null || !weapon.CanFireFlammableProjectiles)
		{
			return false;
		}
		return weapon.IsNextRoundFlammable;
	}

	public override bool ConsumeFlammableRound()
	{
		return CombatUtils.GetWeapon(this)?.ConsumeFlammableProjectile() ?? false;
	}

	public override void ToggleWeaponMode(EquipmentInstance weapon = null)
	{
		if (weapon == null)
		{
			weapon = CombatUtils.GetWeapon(this);
		}
		if (weapon != null)
		{
			weapon.ToggleWeaponMode();
			GetAgentView<HumanoidView>()?.OnToggleWeaponMode(weapon);
			bool isEnabled;
			FVLogTraceInterpolationHandler messageBuilder = new FVLogTraceInterpolationHandler(33, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Weapon mode switched to ");
				messageBuilder.AppendFormatted(weapon.AttackType);
				messageBuilder.AppendLiteral(", agent: ");
				messageBuilder.AppendFormatted(this);
			}
			Log.Trace(messageBuilder);
		}
	}

	public override DamageTakingAgentType CanAttackTypes()
	{
		DamageTakingAgentType canAttackTypes = activeBehaviour.CanAttackTypes;
		if (!base.HasFainted)
		{
			return canAttackTypes;
		}
		return DamageTakingAgentType.None;
	}

	public override ProgressBarFloatingElement GetProgressBar(OverlayProgressBarType type = OverlayProgressBarType.None)
	{
		HumanoidView agentView = GetAgentView<HumanoidView>();
		if (!(agentView == null))
		{
			return agentView.GetProgressBar(type);
		}
		return null;
	}

	public override void DestroyProgressBar(OverlayProgressBarType type)
	{
		GetAgentView<HumanoidView>()?.DestroyProgressBar(type);
	}

	public void SetInfo(HumanoidInfo info)
	{
		this.info = info;
	}

	public void SetBlockedActionTags(List<ActionTagType> blockedActions)
	{
		blockedActionTags = blockedActions;
	}

	public void SetPerks(List<Perk> perks)
	{
		perkIds = new List<string>();
		foreach (Perk perk in perks)
		{
			perkIds.Add(perk.GetID());
		}
		this.perks = perks;
		if (base.AfterStatsInitialisedCallbackExecuted)
		{
			StatsBanAndAllowEffectors();
			RecalcuateGoalPreferencesAndSkillModifiers();
		}
	}

	public bool RemovePerk(string perkId)
	{
		Perk byID = Repository<PerkRepository, Perk>.Instance.GetByID(perkId);
		if (byID == null || !Perks.Contains(byID))
		{
			return false;
		}
		Perks.RemoveAt(Perks.IndexOf(byID));
		if (perkIds.Contains(perkId))
		{
			perkIds.RemoveAt(perkIds.IndexOf(perkId));
		}
		return true;
	}

	public override void InitGoap()
	{
	}

	public void SetAge(int age)
	{
		info.SetAge(age);
	}

	public override string ToString()
	{
		return $"Humanoid '{Info?.FirstName} {Info?.LastName}' (uniqueId: {base.UniqueId}, active behaviour: '{activeBehaviour?.GetType().Name}', originVillage: '{originVillage?.Value?.Name}', incognito: {isInIncognitoMode})";
	}

	public bool IsRoofed()
	{
		int num = GridDataIndexTools.FastTo1DIndex(GetGridPosition());
		if (num == -1)
		{
			return false;
		}
		VillageMap map = base.Map;
		MapNode mapNode = ((map != null) ? map.GridSpaceData[num] : null);
		if (mapNode == null)
		{
			return false;
		}
		return mapNode.Coverage == CoverageType.Roofed;
	}

	public override void TendWounds(float tendingQuality)
	{
		base.TendWounds(tendingQuality);
		if (!HasUntendendWounds() && !string.IsNullOrEmpty(CurrentHumanType?.WhileUntendedEffector))
		{
			Stats?.EndEffector(CurrentHumanType?.WhileUntendedEffector);
		}
	}

	protected override void OnHealthDepleted(bool wasNaturalDeath)
	{
		this.BeforeDeathEvent?.Invoke(this);
		base.OnHealthDepleted(wasNaturalDeath);
		activeBehaviour.OnHealthDepleted(wasNaturalDeath);
	}

	public override void UpdatePosition(Vector3 position)
	{
		MapNode node = GetNode();
		base.UpdatePosition(position);
		MapNode node2 = GetNode();
		activeBehaviour.UpdatePosition(node, node2);
		if (node2 != null && node != null && !node2.IsWalkable && node.IsWalkable)
		{
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(33, 1, out var isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Underground settler stuck issue! ");
				messageBuilder.AppendFormatted(StackTraceUtility.ExtractStackTrace());
			}
			Log.Warning(messageBuilder);
			messageBuilder = new FVLogWarningInterpolationHandler(37, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Stuck is happening on going from ");
				messageBuilder.AppendFormatted(node.Position.ToString());
				messageBuilder.AppendLiteral(" to ");
				messageBuilder.AppendFormatted(node2.Position.ToString());
			}
			Log.Warning(messageBuilder);
			Vec3Int? vec3Int = base.PathDriver?.CurrentPath?.EndPositions.FirstOrDefault();
			Vec3Int? vec3Int2 = base.PathDriver?.CurrentPath?.Start;
			messageBuilder = new FVLogWarningInterpolationHandler(37, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Path end position: ");
				messageBuilder.AppendFormatted(vec3Int.HasValue ? vec3Int.Value.ToString() : "NULL");
				messageBuilder.AppendLiteral(", start position: ");
				messageBuilder.AppendFormatted(vec3Int2.HasValue ? vec3Int2.Value.ToString() : "NULL");
			}
			Log.Warning(messageBuilder);
		}
	}

	private void SwapStatsModel()
	{
		using PooledDictionary<StatType, float> pooledDictionary = DictionaryPool<StatType, float>.GetJanitor();
		foreach (KeyValuePair<StatType, StatInstance> stat in Stats.Stats)
		{
			pooledDictionary.Add(stat.Key, stat.Value.Current);
		}
		using PooledList<ActiveEffectorInfo> pooledList = ListPool<ActiveEffectorInfo>.GetJanitor();
		foreach (ActiveEffectorInfo activeEffector in Stats.GetActiveEffectors())
		{
			pooledList.Add(activeEffector);
		}
		RemoveStatsListeners();
		DetachFromStatsEvents();
		Stats.Dispose();
		Stats = null;
		base.AfterStatsInitialisedCallbackExecuted = false;
		InitStats();
		foreach (KeyValuePair<StatType, StatInstance> stat2 in Stats.Stats)
		{
			if (pooledDictionary.TryGetValue(stat2.Key, out var value))
			{
				stat2.Value.SetCurrent(value);
			}
		}
		Stats.SetActiveEffectors(pooledList);
	}

	protected override void InitStats()
	{
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(25, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Initializing stats for '");
			messageBuilder.AppendFormatted(this);
			messageBuilder.AppendLiteral("'");
		}
		Log.Info(messageBuilder);
		if (base.Stats == null)
		{
			if (activeBehaviour == null)
			{
				FVLogWarningInterpolationHandler messageBuilder2 = new FVLogWarningInterpolationHandler(137, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
				if (isEnabled)
				{
					messageBuilder2.AppendLiteral("InitStats: activeBehaviour is null for '");
					messageBuilder2.AppendFormatted(this);
					messageBuilder2.AppendLiteral("'. ");
					messageBuilder2.AppendLiteral("behaviours count=");
					messageBuilder2.AppendFormatted(behaviours?.Count ?? (-1));
					messageBuilder2.AppendLiteral(", ");
					messageBuilder2.AppendLiteral("activeBehaviourIndex=");
					messageBuilder2.AppendFormatted(activeBehaviourIndex);
					messageBuilder2.AppendLiteral(". ");
					messageBuilder2.AppendLiteral("Cannot create stats without a stats model. Skipping.");
				}
				Log.Warning(messageBuilder2);
				return;
			}
			Stats = new StatsInstance(this, activeBehaviour.GetStatsModel());
			Stats.GenerateFromStatsModel();
			Stats.Initialize();
			RegisterStatsListeners();
			StatsBanAndAllowEffectors();
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(ApplyCustomScenarioStatOverrides);
		}
		else
		{
			if (base.Stats.Owner == null)
			{
				base.Stats.SetOwner(this);
			}
			Stats.AddMissingAttributes(Repository<AttributeRepository, NSMedieval.StatsSystem.Attribute>.Instance.GetAllItems());
			Stats.AddMissingStats();
			RegisterStatsListeners();
			StatsBanAndAllowEffectors();
			Stats.Initialize();
		}
		OnStatsInitialized();
	}

	private void StatsBanAndAllowEffectors()
	{
		foreach (Perk perk in Perks)
		{
			if (perk.BannedEffector != null && perk.BannedEffector.Length != 0)
			{
				string[] bannedEffector = perk.BannedEffector;
				foreach (string name in bannedEffector)
				{
					Stats.BanEffector(name);
				}
			}
			if (perk.AllowedEffectors != null && perk.AllowedEffectors.Length != 0)
			{
				string[] bannedEffector = perk.AllowedEffectors;
				foreach (string name2 in bannedEffector)
				{
					Stats.AllowEffector(name2);
				}
			}
		}
		string[] bannedEffector2 = info.Background.BannedEffector;
		if (bannedEffector2 != null && bannedEffector2.Length != 0)
		{
			string[] bannedEffector = bannedEffector2;
			foreach (string name3 in bannedEffector)
			{
				Stats.BanEffector(name3);
			}
		}
		bannedEffector2 = info.BackStory.BannedEffector;
		if (bannedEffector2 != null && bannedEffector2.Length != 0)
		{
			string[] bannedEffector = bannedEffector2;
			foreach (string name4 in bannedEffector)
			{
				Stats.BanEffector(name4);
			}
		}
	}

	protected override void OnStatsInitialized()
	{
		if (!base.AfterStatsInitialisedCallbackExecuted && !base.HasDisposed)
		{
			base.OnStatsInitialized();
			if (!Stats.HasAttributeModifier(ModifierType.Skills))
			{
				Stats.AddAttributeModifier(new SkillsModifierInstance());
			}
			if (!Stats.HasAttributeModifier(ModifierType.Perk))
			{
				Stats.AddAttributeModifier(new PerkAttributeModifierInstance());
			}
			SunlightModifierInstance modifier = new SunlightModifierInstance(AttributeType.SunlightLoss, 1f, "sunlight");
			Stats.AddAttributeModifier(modifier);
			WetnessSpeedModifierInstance modifier2 = new WetnessSpeedModifierInstance(AttributeType.WetSpeed, 1f, "wetness");
			Stats.AddAttributeModifier(modifier2);
			BreathSpeedModifierInstance modifier3 = new BreathSpeedModifierInstance(AttributeType.BreathLossSpeed, 1f, "breath");
			Stats.AddAttributeModifier(modifier3);
			AttachToStatsEvents();
		}
	}

	protected override IAgentView GetAgentView()
	{
		if (WorkerBehaviour != null)
		{
			if (!MonoSingleton<WorkerManager>.IsInstantiated())
			{
				return null;
			}
			return MonoSingleton<WorkerManager>.Instance.GetView(this);
		}
		if (!MonoSingleton<NPCManager>.IsInstantiated())
		{
			return null;
		}
		return MonoSingleton<NPCManager>.Instance.GetView(this);
	}

	public TBehaviour SetActiveBehaviour<TBehaviour>(bool activate = true) where TBehaviour : HumanoidBehaviour, new()
	{
		if (activeBehaviour is TBehaviour result)
		{
			return result;
		}
		activeBehaviour?.Deactivate();
		bool flag = false;
		if (activate)
		{
			flag = (GetAgentView() is NPCView && typeof(TBehaviour) == typeof(WorkerBehaviour)) || (GetAgentView() is WorkerView && typeof(TBehaviour) != typeof(WorkerBehaviour));
		}
		if (flag)
		{
			IncognitoDispose();
		}
		int num = behaviours.FindIndex((HumanoidBehaviour behaviour) => behaviour is TBehaviour);
		if (num == -1)
		{
			HumanoidBehaviour humanoidBehaviour = new TBehaviour();
			humanoidBehaviour.Initialize(this);
			behaviours.Add(humanoidBehaviour);
			num = behaviours.Count - 1;
		}
		activeBehaviourIndex = num;
		ActiveBehaviourIndexChangedInternal();
		base.GoapAgent = activeBehaviour.GoapAgentUnchecked;
		if (activate)
		{
			SwapStatsModel();
		}
		if (flag)
		{
			RecalcuateGoalPreferencesAndSkillModifiers();
			IncognitoSpawn(GetPosition());
		}
		if (activate)
		{
			activeBehaviour.Activate();
		}
		if (IsNpc())
		{
			MonoSingleton<NPCController>.Instance.FireOnNpcChanged(this);
		}
		if (MonoSingleton<LoadingController>.IsInstantiated() && LoadingController.IsLoadingComplete)
		{
			GetAgentView<HumanoidView>()?.RegenerateIcon();
		}
		GetNode()?.RefreshTags();
		return activeBehaviour as TBehaviour;
	}

	private void ActiveBehaviourIndexChangedInternal()
	{
		if (behaviours == null || behaviours.Count == 0)
		{
			activeBehaviour = null;
			activeBehaviourType = BehaviourType.None;
		}
		else
		{
			activeBehaviour = behaviours[activeBehaviourIndex];
			activeBehaviourType = behaviours[activeBehaviourIndex].BehaviourType;
		}
		workerBehaviour = (((activeBehaviourType & BehaviourType.Worker) == 0) ? null : ((WorkerBehaviour)activeBehaviour));
		prisonerBehaviour = (((activeBehaviourType & BehaviourType.Prisoner) == 0) ? null : ((PrisonerBehaviour)activeBehaviour));
		enemyBehaviour = (((activeBehaviourType & BehaviourType.Enemy) == 0) ? null : ((EnemyBehaviour)activeBehaviour));
		traderBehaviour = (((activeBehaviourType & BehaviourType.Trader) == 0) ? null : ((TraderBehaviour)activeBehaviour));
		captiveLabourerBehaviour = (((activeBehaviourType & BehaviourType.CaptiveLabourer) == 0) ? null : ((CaptiveLabourerBehaviour)activeBehaviour));
		negotiatorBehaviour = (((activeBehaviourType & BehaviourType.Negotiator) == 0) ? null : ((NegotiatorBehaviour)activeBehaviour));
	}

	public void RecalcuateGoalPreferencesAndSkillModifiers()
	{
		GoalPreferences.RecalculateDefaultGoalPreferences();
		GoalPreferences.SetDefaultGoalPreferenceSkillModifiers();
	}

	public bool ContainsBehaviour<T>() where T : HumanoidBehaviour
	{
		return behaviours.Any((HumanoidBehaviour item) => item is T);
	}

	public bool ContainsBehaviourType(BehaviourType behaviourTypeMask)
	{
		foreach (HumanoidBehaviour behaviour in behaviours)
		{
			if ((behaviour.BehaviourType & behaviourTypeMask) != 0)
			{
				return true;
			}
		}
		return false;
	}

	public T GetBehaviour<T>() where T : HumanoidBehaviour
	{
		return (from behaviour in behaviours.OfType<T>()
			select (behaviour)).FirstOrDefault();
	}

	public bool IsEnemy()
	{
		return (activeBehaviourType & BehaviourType.Enemy) != 0;
	}

	public bool IsWorker()
	{
		return (activeBehaviourType & BehaviourType.Worker) != 0;
	}

	public bool IsRaiderOfRaid(int raidId)
	{
		if ((activeBehaviourType & BehaviourType.Enemy) != 0)
		{
			return ((EnemyBehaviour)activeBehaviour).RaidId == raidId;
		}
		return false;
	}

	public bool IsRaider()
	{
		if ((activeBehaviourType & BehaviourType.Enemy) != 0)
		{
			return ((EnemyBehaviour)activeBehaviour).RaidId != 0;
		}
		return false;
	}

	public bool IsTrader()
	{
		return (activeBehaviourType & BehaviourType.Trader) != 0;
	}

	public bool IsTraderBodyguard()
	{
		return (activeBehaviourType & BehaviourType.TraderBodyguard) != 0;
	}

	public bool IsVisitor()
	{
		return (activeBehaviourType & BehaviourType.RoleVisitor) != 0;
	}

	public bool IsNpc()
	{
		return activeBehaviour?.NpcBlueprint != null;
	}

	public bool IsCaptive()
	{
		return (activeBehaviourType & BehaviourType.CaptiveNpc) != 0;
	}

	public bool IsPrisoner()
	{
		return (activeBehaviourType & BehaviourType.Prisoner) != 0;
	}

	public bool IsNegotiator()
	{
		return (activeBehaviourType & BehaviourType.Negotiator) != 0;
	}

	public bool IsBeggar()
	{
		return (activeBehaviourType & BehaviourType.Beggar) != 0;
	}

	public bool IsPilgrimVisitor()
	{
		return (activeBehaviourType & BehaviourType.PilgrimVisitor) != 0;
	}

	public override string GetGoapAgentID()
	{
		return activeBehaviour.GetGoapAgentId();
	}

	private void InitBehaviour(bool startGoapTicker = true)
	{
		if (base.IsFirstSpawn && behaviours.Count <= 0)
		{
			SetActiveBehaviour<BlankBehaviour>();
			return;
		}
		base.GoapAgent = activeBehaviour.GoapAgentUnchecked;
		activeBehaviour.Activate(startGoapTicker);
	}

	public void InitBehaviourIncognito()
	{
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(29, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("InitBehaviourIncognito for '");
			messageBuilder.AppendFormatted(this);
			messageBuilder.AppendLiteral("'");
		}
		Log.Info(messageBuilder);
		if (behaviours.Count != 0)
		{
			base.GoapAgent = activeBehaviour.GoapAgentUnchecked;
			activeBehaviour.InitIncognitoAfterLoad();
			base.GoapAgent.StopTicker();
		}
	}

	private void BecomeAggressive()
	{
		if ((activeBehaviourType & (BehaviourType.CaptiveNpc | BehaviourType.Worker | BehaviourType.Enemy)) != 0 || SetActiveBehaviour<EnemyBehaviour>().RaidId != 0)
		{
			return;
		}
		List<HumanoidInstance> list = new List<HumanoidInstance>();
		foreach (HumanoidInstance nPC in GlobalSaveController.CurrentVillageData.NPCs)
		{
			if (nPC.Faction == Faction && !nPC.IsPrisoner())
			{
				nPC.SetActiveBehaviour<EnemyBehaviour>();
				list.Add(nPC);
			}
		}
		int raidId = GlobalSaveController.CurrentVillageData.Raids.Sum((ActiveRaidInfo item) => item.RaidId) + 1;
		List<HumanoidInstance> playerUnits = MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys.Where((HumanoidInstance item) => !item.HasDiedOrFainted && !item.HasDisposed).ToList();
		long currentTimeTutorialAware = GlobalSaveController.CurrentVillageData.DateAndTime.CurrentTimeTutorialAware;
		NSMedieval.Model.Raid blueprint = new NSMedieval.Model.Raid(raidId, isSiege: false, 0L, 130);
		uint commanderAgentId = base.Map.CommanderAIManager.CreateCommander(list);
		ActiveRaidInfo item2 = new ActiveRaidInfo(base.Map, blueprint, null, raidId, list, playerUnits, currentTimeTutorialAware, isSiege: false, commanderAgentId);
		foreach (HumanoidInstance item3 in list)
		{
			EnemyBehaviour enemyBehaviour = item3.EnemyBehaviour;
			if (enemyBehaviour.RaidId == 0)
			{
				enemyBehaviour.RaidId = raidId;
			}
		}
		GlobalSaveController.CurrentVillageData.Raids.Add(item2);
		MonoSingleton<NPCController>.Instance.NPCBecomeAggressive(this);
	}

	public bool IsFriendlyFaction()
	{
		if (Faction == null)
		{
			return true;
		}
		return !Faction.IsHostile();
	}

	public void OnFriendlinessStateChanged()
	{
		if (!IsFriendlyFaction())
		{
			if (Faction.IsPermanentlyHostile())
			{
				RetreatFromMap();
			}
			else
			{
				BecomeAggressive();
			}
		}
	}

	public void SnapToBed(BedComponentInstance bed)
	{
		GetAgentView<HumanoidView>().SnapToBed(bed);
	}

	public void SetTool(string toolId, Transform socket = null)
	{
		if (WorkerBehaviour != null)
		{
			MonoSingleton<WorkerController>.Instance.PullOutTool(this, toolId, socket);
			if (!WorkerBehaviour.IsDrafting)
			{
				SetWeaponVisibility(isVisible: false);
			}
		}
		if (IsNpc())
		{
			MonoSingleton<NPCController>.Instance.ShowTool(this, toolId, null);
			if ((activeBehaviourType & BehaviourType.Enemy) != 0)
			{
				SetWeaponVisibility(isVisible: false);
			}
		}
	}

	public void HideTool()
	{
		if (WorkerBehaviour != null)
		{
			MonoSingleton<WorkerController>.Instance.HideTool(this);
		}
		if (IsNpc())
		{
			MonoSingleton<NPCController>.Instance.HideTool(this);
			if ((activeBehaviourType & BehaviourType.Enemy) != 0)
			{
				SetWeaponVisibility(isVisible: true);
			}
		}
	}

	public override bool RopeTo(IGoapTargetable target, bool matchSpeed = false)
	{
		if (base.GoapAgent == null || base.HasDisposed)
		{
			return false;
		}
		base.PathDriver.GoapTempMatchSpeedDriver = null;
		ropedTo = target;
		if (matchSpeed)
		{
			base.PathDriver.GoapTempMatchSpeedDriver = (target as IPathfindingAgent)?.PathDriver;
		}
		RefreshRopedAnimationParameter();
		return true;
	}

	public override IGoapTargetable RopedTo()
	{
		return ropedTo;
	}

	public void IncognitoSpawn(Vector3 spawnPosition, bool randomizeAppearance = true)
	{
		if (isInIncognitoMode)
		{
			DontSpawnCarcassOnDispose = false;
			UpdatePosition(spawnPosition);
			if (WorkerBehaviour != null)
			{
				MonoSingleton<WorkerController>.Instance.CreateWorker(this);
			}
			if (IsNpc())
			{
				MonoSingleton<NPCManager>.Instance.CreateViewAndSetup(this, forceRegenerateEquipment: false, null, null, randomizeAppearance);
			}
			AttachToStatsEvents();
			isInIncognitoMode = false;
			MonoSingleton<TaskController>.Instance.WaitFor(0.1f).Then(EnableAiAgentsAfterIncognitoSpawn);
		}
	}

	public void ResetIncognito()
	{
		isInIncognitoMode = false;
	}

	private void EnableAiAgentsAfterIncognitoSpawn()
	{
		if (!base.HasDied && !base.HasDisposed)
		{
			MonoSingleton<DraftController>.Instance.OnEndDraft(this);
			GetGoapAgent()?.StartTicker();
			base.CombatAi?.Enable();
		}
	}

	private void DisableAiAgents()
	{
		GetGoapAgent()?.StopTicker();
		base.CombatAi?.Disable();
	}

	public override void IncognitoDispose()
	{
		if (!isInIncognitoMode)
		{
			activeBehaviour?.Deactivate();
			base.Storage.ClearAll();
			isInIncognitoMode = true;
			DontSpawnCarcassOnDispose = true;
			DetachFromStatsEvents();
			if (WorkerBehaviour != null)
			{
				MonoSingleton<WorkerManager>.Instance.DestroyView(this);
				GlobalSaveController.CurrentVillageData.Workers.Remove(this);
				MonoSingleton<WorkerController>.Instance.RemoveWorker(this);
			}
			if (IsNpc())
			{
				MonoSingleton<NPCManager>.Instance.DestroyView(this);
				GlobalSaveController.CurrentVillageData.NPCs.Remove(this);
				MonoSingleton<NPCController>.Instance.RemoveNPC(this);
			}
			isInIncognitoMode = true;
			MonoSingleton<CombatAgentManager>.Instance.RemoveCommonCombatAgent(this);
			DisableAiAgents();
		}
	}

	public bool IsFormingCaravan()
	{
		return ((IFormCaravanGoapAgent)base.GoapAgent).PreparingForCaravan != null;
	}

	public bool CanFormCaravan(bool allowFainted = false)
	{
		if ((!allowFainted && base.HasFainted) || base.IsBeingCarried || base.HasDied || base.HasDisposed)
		{
			return false;
		}
		if (WorkerBehaviour != null && (WorkerBehaviour.IsBanished || WorkerBehaviour.IsCrazy))
		{
			return false;
		}
		return true;
	}

	public CaravanInstance GetFormingCaravanInstance()
	{
		return ((IFormCaravanGoapAgent)base.GoapAgent).PreparingForCaravan;
	}

	public void ClearCaravanFormingData()
	{
		(base.GoapAgent as IFormCaravanGoapAgent)?.ClearCaravanFormingData();
	}

	public void StartCaravanFormation(CaravanInstance caravan)
	{
		((IFormCaravanGoapAgent)base.GoapAgent).StartCaravanFormation(caravan);
		base.GoapAgent.Abort();
	}

	public void OnKilled(IDamageDealAgent killer)
	{
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(23, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Humanoid ");
			messageBuilder.AppendFormatted(this);
			messageBuilder.AppendLiteral(" is killed by ");
			messageBuilder.AppendFormatted(killer);
		}
		Log.Info(messageBuilder);
		if (!(activeBehaviour is WorkerBehaviour) && !(killer is AnimalInstance))
		{
			activeBehaviour.OnKilled(killer);
		}
	}

	protected override void OnTrapTriggered(TrapComponentInstance trapInstance)
	{
		activeBehaviour.OnTrapTriggered(trapInstance);
	}

	public void RetreatFromMap()
	{
		if (base.CombatAi == null)
		{
			return;
		}
		base.CombatAi.SetState(CombatAiState.EnemyIsRetreating, true);
		base.CombatAi.Abort();
		IsLeaving = true;
		foreach (AnimalInstance pet in base.Pets)
		{
			if (pet != null && !pet.HasDied && !pet.HasDisposed)
			{
				pet.LeaveMap();
			}
		}
	}

	public void GoapAttendPlayerTriggeredEvent(string goalId)
	{
		activeBehaviour.AttendPlayerTriggeredEvent(goalId);
	}

	public void GoapLeavePlayerTriggeredEvent(string goalId)
	{
		if (!base.HasDied && !base.HasDisposed && base.GoapAgent != null && !base.GoapAgent.HasDisposed && base.CombatAi != null && !base.CombatAi.HasDisposed)
		{
			activeBehaviour.LeavePlayerTriggeredEvent(goalId);
		}
	}

	public Sprite GetSprite()
	{
		return MonoSingleton<HumanoidIconManager>.Instance.GetCachedIcon(this);
	}

	public void AddToEvent(PlayerTriggeredEventInstance eventInstance)
	{
		activeEventInstance = eventInstance;
	}

	public void RemoveFromEvent()
	{
		activeEventInstance = null;
	}

	public bool IsAtEvent(out PlayerTriggeredEventInstance eventInstance)
	{
		eventInstance = activeEventInstance;
		return eventInstance != null;
	}

	public bool IsAtEvent()
	{
		return activeEventInstance != null;
	}

	private void ApplyCustomScenarioStatOverrides()
	{
		if (WorkerBehaviour == null)
		{
			return;
		}
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(46, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Applying custom scenario stat overrides for '");
			messageBuilder.AppendFormatted(this);
			messageBuilder.AppendLiteral("'");
		}
		Log.Info(messageBuilder);
		if (Stats == null)
		{
			throw new Exception($"Tried to override stats, but this.Stats is null for {this}");
		}
		List<GameEvent.StatSetting> list = new List<GameEvent.StatSetting>();
		if (GlobalSaveController.CurrentVillageData != null)
		{
			Log.Info("Pulling stat overrides from GlobalSaveController.CurrentVillageData.Scenario.VillagerConstraints.OverrideStats", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			list = GlobalSaveController.CurrentVillageData.Scenario.VillagerConstraints.OverrideStats;
		}
		else if (MonoSingleton<GameStartController>.IsInstantiated() && MonoSingleton<GameStartController>.Instance.SelectedScenario != null)
		{
			Log.Info("Pulling stat overrides from GameStartController.Instance.SelectedScenario.VillagerConstraints.OverrideStats", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\State\\HumanoidInstance.cs");
			list = MonoSingleton<GameStartController>.Instance.SelectedScenario.VillagerConstraints.OverrideStats;
		}
		foreach (GameEvent.StatSetting item in list)
		{
			if (item.Stat != 0)
			{
				(Stats.GetStat(item.Stat) ?? throw new Exception($"Failed to find stat in humanoid for given stat override type '{item.Stat}'. {this}")).SetCurrent(item.Value);
			}
		}
	}

	private void FirePerkEffectors()
	{
		foreach (Perk perk in Perks)
		{
			if (!string.IsNullOrEmpty(perk.Effector))
			{
				Stats.StartEffector(perk.Effector);
			}
		}
	}

	private void WoundEffectorStartCheck(StatEffector effector)
	{
		if (effector is StatEffectorWound && base.IsWounded && !string.IsNullOrEmpty(CurrentHumanType.WhileUntendedEffector) && HasUntendendWounds())
		{
			Stats?.StartEffector(CurrentHumanType.WhileUntendedEffector);
		}
	}

	private void WoundEffectorEndCheck(StatEffector effector)
	{
		if (effector is StatEffectorWound && !HasUntendendWounds() && !string.IsNullOrEmpty(CurrentHumanType.WhileUntendedEffector))
		{
			Stats?.EndEffector(CurrentHumanType.WhileUntendedEffector);
		}
	}

	private void OnHourChange()
	{
		if (skills == null)
		{
			return;
		}
		XpDecaySettings data = Repository<XpDecaySettingsData, XpDecaySettings>.Instance.GetData<XpDecaySettings>();
		foreach (WorkerSkill skill in skills.Skills)
		{
			if (skill.Id != 0)
			{
				float xpDecay = data.GetXpDecay(skill.Level);
				xpDecay *= GetAttributeValue(AttributeType.XpDecayMultiplier);
				float xpRequirement = Repository<SkillLevelsRepository, SkillLevels>.Instance.GetXpRequirement(skill.Id, skill.Level);
				if (skill.Experience - xpDecay < xpRequirement)
				{
					skill.SetLevel(skill.Level);
				}
				else if (xpDecay > 0f)
				{
					skill.SetExperience(skill.Experience - xpDecay);
				}
			}
		}
	}

	private void OnChangeDate()
	{
		if (Skills != null)
		{
			foreach (WorkerSkill skill in Skills.Skills)
			{
				skill?.ResetExperienceAddedToday();
			}
		}
		DateTimeSettings data = Repository<DateTimeSettingsData, DateTimeSettings>.Instance.GetData<DateTimeSettings>();
		int num = data.Seasons.IndexOf(GlobalSaveController.CurrentVillageData.DateAndTime.Season);
		int num2 = (int)Math.Floor((float)info.Birthday / (float)(int)data.DaysInSeason);
		int num3 = info.Birthday % data.DaysInSeason;
		if (num3 == 0)
		{
			num3 = data.DaysInSeason;
			num2 = Mathf.Clamp(num2 - 1, 0, GlobalSaveController.CurrentVillageData.DateAndTime.SeasonsCount);
		}
		if (num == num2 && num3 == GlobalSaveController.CurrentVillageData.DateAndTime.Day)
		{
			info.SetAge(info.Age + 1);
			string text = MonoSingleton<LocalizationController>.Instance.GetText("general_birthday_today", this);
			AnimatedAgentView agentView = GetAgentView<AnimatedAgentView>();
			MonoSingleton<BlackBarMessageController>.Instance.ShowClickableBlackBarMessage(text, agentView, follow: true);
			LogLifeEvent(LifeEventUtils.GetMiscLog(text));
			HandleBirthdayPerk();
			HandleAgeEffectors();
		}
	}

	private void HandleHeightEffectors()
	{
		List<string> list = CurrentHumanType?.HeightEffectors.FirstOrDefault((RangeBasedEffector category) => category.Range.InRange(Mathf.RoundToInt(info.Height)))?.Effectors;
		if (list != null && list.Count > 0)
		{
			Stats.StartEffectors(list);
		}
	}

	private void HandleWeightEffectors()
	{
		List<string> list = CurrentHumanType?.WeightEffectors.FirstOrDefault((RangeBasedEffector category) => category.Range.InRange(Mathf.RoundToInt(info.GetWeight())))?.Effectors;
		if (list != null && list.Count > 0)
		{
			Stats.StartEffectors(list);
		}
	}

	private void HandleAgeEffectors()
	{
		List<string> list = CurrentHumanType.AgeEffectors.FirstOrDefault((RangeBasedEffector category) => category.Range.InRange(info.Age))?.Effectors;
		if (list == null || list.Count <= 0)
		{
			return;
		}
		ageEffectors = ageEffectors ?? new List<string>();
		if (list.SequenceEqual(ageEffectors))
		{
			return;
		}
		foreach (string ageEffector in ageEffectors)
		{
			Stats.EndEffector(ageEffector);
		}
		Stats.StartEffectors(list);
		ageEffectors = list;
	}

	private void HandleBirthdayPerk()
	{
		IntRange possiblePerksRange = HumanoidUtils.GetPossiblePerksRange(info.Age);
		if (Perks.Count >= possiblePerksRange.Max)
		{
			return;
		}
		FloatRange birthdayPerkChance = Repository<GenerationSettingsRepository, GenerationSettings>.Instance.Settings.BirthdayPerkChance;
		float num = ((Perks.Count < possiblePerksRange.Min) ? birthdayPerkChance.Min : birthdayPerkChance.Max);
		if (!(NSMedieval.Tools.Math.Random.Range(0f, 1f) > num))
		{
			TryAddNewOrRandomPerk(string.Empty, from perk in Repository<PerkRepository, Perk>.Instance.GetAllItems()
				where perk.ForbidOnBirthday
				select perk);
		}
	}

	public void TryAddNewPerk(string perkId)
	{
		List<Perk> forbiddenPerks = GetForbiddenPerks();
		if (Repository<PerkRepository, Perk>.Instance.GetPerk(perkId, forbiddenPerks, Info.IgnoredTypes, out var perk))
		{
			AddPerk(perk);
		}
	}

	public void TryAddNewOrRandomPerk(string perkId, IEnumerable<Perk> forbiddenPerksToAdd)
	{
		List<Perk> forbiddenPerks = GetForbiddenPerks();
		forbiddenPerks.AddRange(forbiddenPerksToAdd);
		if (string.IsNullOrEmpty(perkId) || !Repository<PerkRepository, Perk>.Instance.GetPerk(perkId, forbiddenPerks, Info.IgnoredTypes, out var perk))
		{
			perk = Repository<PerkRepository, Perk>.Instance.GetRandomPerk(forbiddenPerks, Info.IgnoredTypes);
		}
		AddPerk(perk);
	}

	private List<Perk> GetForbiddenPerks()
	{
		List<Perk> list = new List<Perk>();
		foreach (Perk perk in Perks)
		{
			list.Add(perk);
			list.AddRange(Repository<PerkRepository, Perk>.Instance.GetAllFromCategory(perk));
		}
		return list;
	}

	private void AddPerk(Perk newPerk)
	{
		if (!(newPerk == null) && !Perks.Contains(newPerk))
		{
			Perks.Add(newPerk);
			SetPerks(Perks);
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("general_perk_gained", this) + ": " + ColorUtils.ColorText(MonoSingleton<LocalizationController>.Instance.GetText(LocKeyUtils.GetName(newPerk.LocKeys), this), "highlight_orange"));
		}
	}

	public void AttachToStatsEvents()
	{
		Stats.OnEffectorStartEvent += WoundEffectorStartCheck;
		Stats.OnEffectorStackEvent += WoundEffectorStartCheck;
		Stats.OnEffectorEndEvent += WoundEffectorEndCheck;
		MonoSingleton<CombatController>.Instance.OnAgentKilledEvent += OnAgentKilled;
	}

	public void DetachFromStatsEvents()
	{
		Stats.OnEffectorStartEvent -= WoundEffectorStartCheck;
		Stats.OnEffectorStackEvent -= WoundEffectorStartCheck;
		Stats.OnEffectorEndEvent -= WoundEffectorEndCheck;
		MonoSingleton<CombatController>.Instance.OnAgentKilledEvent -= OnAgentKilled;
	}

	private void OnAgentKilled(IDamageDealAgent deal, IDamageTakingAgent take)
	{
		if (deal != this || !(take is HumanoidInstance))
		{
			return;
		}
		foreach (string killedHumanEffector in CurrentHumanType.KilledHumanEffectors)
		{
			Stats?.StartEffector(killedHumanEffector);
		}
	}

	private void OnLanguageChange()
	{
		skillsOrdered = null;
	}

	public void SetBeautyTarget()
	{
		if (GetNode() != null && Stats != null && base.Map?.BeautyManager != null)
		{
			StatInstance stat = Stats.GetStat(StatType.Beauty);
			if (stat != null)
			{
				float multiplier = Mathf.Clamp((stat.Min + stat.Max) / 2f + base.Map.BeautyManager.GetBeauty(GetNode().Position) * 10f, stat.Min, stat.Max);
				GetAttribute(AttributeType.BeautyTarget)?.SetMultiplier(multiplier);
			}
		}
	}

	protected override void OnWorldObjectEnterProximity(WorldObject worldObject)
	{
		base.OnWorldObjectEnterProximity(worldObject);
		activeBehaviour?.ProximityBehaviour?.HandleOnWorldObjectEnterProximity(worldObject);
	}

	protected override void OnWorldObjectExitProximity(WorldObject worldObject)
	{
		base.OnWorldObjectExitProximity(worldObject);
		activeBehaviour?.ProximityBehaviour?.HandleOnWorldObjectExitProximity(worldObject);
	}

	protected override void OnCreatureEnterProximity(CreatureBase creature)
	{
		base.OnCreatureEnterProximity(creature);
		activeBehaviour?.ProximityBehaviour?.HandleOnCreatureEnterProximity(creature);
	}

	protected override void RegisterStatsListeners()
	{
		base.RegisterStatsListeners();
		HumanoidBelief.RegisterStatsListeners();
	}

	protected override void RemoveStatsListeners()
	{
		base.RemoveStatsListeners();
		HumanoidBelief.RemoveStatsListeners();
	}

	public void OnBeforeSerialize()
	{
	}

	public override void Serialize(FVSerializer serializer)
	{
		base.Serialize(serializer);
		OnBeforeSerialize();
		serializer.Write("behaviors", behaviours);
		serializer.WriteEnum("blockedActionTags", (IList<ActionTagType>)blockedActionTags);
		serializer.Write("info", info);
		serializer.Write("perkIds", perkIds);
		serializer.Write("skills", skills);
		serializer.Write("isInIncognitoMode", isInIncognitoMode);
		serializer.Write("originVillage", originVillage);
		serializer.Write("factionId", factionId);
		serializer.Write("isLeaving", isLeaving);
		serializer.Write("activeBehaviorIndex", activeBehaviourIndex);
		SerializeGoalPrefDict("defaultGoalPrefDictionary", GoalPreferences.DefaultGoalPrefDictionary, serializer);
		SerializeGoalPrefDict("roleGoalPrefDictionary", GoalPreferences.SecondaryGoalPrefDictionary, serializer);
		serializer.Write("religiousEffectorsLog", HumanoidBelief.ReligiousEffectorsLog);
		serializer.Write("flammableProjectilesAllowed", base.FlammableProjectilesAllowed);
		serializer.Write("customWarningMessage", customWarningMessage);
	}

	public static void SerializeGoalPrefDict(string key, Dictionary<GoalPreference, GoalPreferenceLevelData> dictionary, FVSerializer serializer)
	{
		List<KeyValuePair<GoalPreference, GoalPreferenceLevelData>> source = dictionary.ToList();
		List<string> value = source.Select((KeyValuePair<GoalPreference, GoalPreferenceLevelData> pair) => pair.Key.GetID()).ToList();
		List<string> value2 = source.Select((KeyValuePair<GoalPreference, GoalPreferenceLevelData> pair) => pair.Value.GetID()).ToList();
		serializer.Write(key + "_keys", value);
		serializer.Write(key + "_values", value2);
	}

	public HumanoidInstance(HumanoidInstance original)
		: base(original)
	{
		behaviours = original.behaviours;
		foreach (HumanoidBehaviour behaviour in behaviours)
		{
			behaviour.OnLoaded(this);
		}
		activeBehaviourIndex = original.activeBehaviourIndex;
		ActiveBehaviourIndexChangedInternal();
		blockedActionTags = original.blockedActionTags;
		perkIds = original.perkIds;
		skills = original.skills;
		originVillage = original.originVillage;
		info = original.info;
		factionId = original.factionId;
		isLeaving = original.isLeaving;
		goalPreferences = original.GoalPreferences;
		HumanoidBelief.ReligiousEffectorsLog = original.HumanoidBelief.ReligiousEffectorsLog;
		if (factionId == null && originVillage != null)
		{
			factionId = originVillage.FactionId;
			factionCacheInit = false;
		}
		base.FlammableProjectilesAllowed = original.FlammableProjectilesAllowed;
	}

	public HumanoidInstance(FVDeserializer deserializer)
		: base(deserializer)
	{
		info = deserializer.ReadObject<HumanoidInfo>("info");
		NPCInfo nPCInfo = deserializer.ReadObject<NPCInfo>("npcInfo") ?? deserializer.ReadObject<NPCInfo>("enemyInfo");
		if (info == null && nPCInfo != null)
		{
			System.Random random = new System.Random();
			IntRange intRange = new IntRange(0, 100);
			float num = ((intRange == null) ? 0f : random.Range(intRange.Min, intRange.Max + 1));
			List<WorkerCharacteristicType> physicalIgnoreTypes = HumanoidUtils.GetPhysicalIgnoreTypes(new List<WorkerCharacteristicType>(), nPCInfo.BodyType, nPCInfo.Height, nPCInfo.WeightCoefficient);
			Background background = Repository<BackgroundRepository, Background>.Instance.GetBackground(physicalIgnoreTypes, Mathf.RoundToInt(num));
			BackStory background2 = Repository<BackStoryRepository, BackStory>.Instance.GetBackground(physicalIgnoreTypes, Mathf.RoundToInt(num));
			string originTown = ((originVillage == null) ? "" : originVillage.ToString());
			info = new HumanoidInfo(nPCInfo.BodyType, nPCInfo.Age, HumanoidUtils.GetBirthday(), nPCInfo.Height, nPCInfo.WeightCoefficient, originTown, background.GetID(), background2.GetID(), num);
			info.SetPhysicalLook(nPCInfo.PhysicalLook);
		}
		behaviours = deserializer.ReadObjectList("behaviors", new List<HumanoidBehaviour>());
		if (behaviours != null)
		{
			if (behaviours.Count == 0)
			{
				PreHumanoidCompatibility(deserializer);
			}
			foreach (HumanoidBehaviour behaviour in behaviours)
			{
				behaviour.OnLoaded(this);
			}
		}
		activeBehaviourIndex = deserializer.ReadInt("activeBehaviorIndex", -1);
		if (activeBehaviourIndex == -1 && behaviours != null)
		{
			activeBehaviourIndex = behaviours.Count - 1;
		}
		ActiveBehaviourIndexChangedInternal();
		blockedActionTags = deserializer.ReadEnumList<ActionTagType>("blockedActionTags");
		perkIds = deserializer.ReadStringList("perkIds");
		skills = deserializer.ReadObject<WorkerSkills>("skills");
		if (deserializer.ContainsKey("inInIncognitoMode"))
		{
			isInIncognitoMode = deserializer.ReadBool("inInIncognitoMode");
		}
		else
		{
			isInIncognitoMode = deserializer.ReadBool("isInIncognitoMode");
		}
		originVillage = deserializer.ReadObject<VillagePlaceReference>("originVillage");
		customWarningMessage = deserializer.ReadString("customWarningMessage");
		factionId = deserializer.ReadString("factionId");
		isLeaving = deserializer.ReadBool("isLeaving");
		GoalPreferences.SetDefault(DeserializeGoalPrefDict("defaultGoalPrefDictionary", deserializer));
		GoalPreferences.SetSecondary(DeserializeGoalPrefDict("roleGoalPrefDictionary", deserializer));
		HumanoidBelief.ReligiousEffectorsLog = deserializer.ReadObjectLinkedList<EffectorLogStruct>("religiousEffectorsLog");
		if (factionId == null && originVillage != null)
		{
			factionId = originVillage.FactionId;
			factionCacheInit = false;
		}
		base.FlammableProjectilesAllowed = deserializer.ReadBool("flammableProjectilesAllowed");
		OnAfterDeserialize();
	}

	public void OnAfterDeserialize()
	{
	}

	private void PreHumanoidCompatibility(FVDeserializer deserializer)
	{
		if (deserializer.ReadObject<JobTypeIntDictionary>("jobPriorities") != null)
		{
			WorkerBehaviour workerBehaviour = new WorkerBehaviour(deserializer);
			workerBehaviour.Initialize(this);
			behaviours.Add(workerBehaviour);
			activeBehaviourIndex = behaviours.Count - 1;
			ActiveBehaviourIndexChangedInternal();
			base.GoapAgent = activeBehaviour.GoapAgentUnchecked;
		}
	}

	public static Dictionary<GoalPreference, GoalPreferenceLevelData> DeserializeGoalPrefDict(string key, FVDeserializer deserializer)
	{
		List<string> list = deserializer.ReadStringList(key + "_keys", new List<string>());
		List<string> list2 = deserializer.ReadStringList(key + "_values", new List<string>());
		if (list.Count != list2.Count)
		{
			throw new Exception($"Corrupted save data, keys and values must be of same length (keys is {list.Count}, values is {list2.Count})");
		}
		Dictionary<GoalPreference, GoalPreferenceLevelData> dictionary = new Dictionary<GoalPreference, GoalPreferenceLevelData>();
		for (int i = 0; i < list.Count; i++)
		{
			GoalPreference byID = Repository<GoalPreferenceRepository, GoalPreference>.Instance.GetByID(list[i]);
			GoalPreferenceLevelData byID2 = Repository<GoalPreferenceLevelRepository, GoalPreferenceLevelData>.Instance.GetByID(list2[i]);
			if (!(byID == null) && !(byID2 == null))
			{
				dictionary[byID] = byID2;
			}
		}
		return dictionary;
	}

	public override CombatAiAgent CreateNewCombatAiAgent(string id)
	{
		return activeBehaviour.CreateNewCombatAiAgent(id);
	}

	public override string GetDefaultCombatAgentId()
	{
		return string.Empty;
	}

	public void SetFaction(FactionInstance faction)
	{
		factionId = faction.BlueprintId;
		factionCache = faction;
		factionCacheInit = true;
	}

	protected override void InitCombatAi()
	{
	}

	public void RefreshRopedAnimationParameter()
	{
		AnimatedAgentView agentView = GetAgentView<AnimatedAgentView>();
		if (!(agentView == null))
		{
			bool value = ropedTo != null || ((activeBehaviourType & BehaviourType.CaptiveNpc) != 0 && ((CaptiveNpcBehaviour)activeBehaviour).Shackled);
			agentView.TrySetParameter("IsRoped", value);
		}
	}

	public void ChangeSchedule(int hour, HourType newHourType)
	{
		if (ScheduleHours != null && hour < ScheduleHours.Length)
		{
			HourType num = ScheduleHours[hour];
			ScheduleHours[hour] = newHourType;
			if (num == HourType.RoleJob || newHourType == HourType.RoleJob)
			{
				MonoSingleton<GlobalWarningMessagesManager>.Instance.OnWorkerScheduleChanged();
			}
		}
	}

	public override float GetBuildablePassThroughDestroyChance()
	{
		return ActiveBehaviour.Blueprint.BuildablePassThroughDestroyChance;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
