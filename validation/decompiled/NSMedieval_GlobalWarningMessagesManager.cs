using System;
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
using NSMedieval.Construction;
using NSMedieval.Controllers;
using NSMedieval.Enums;
using NSMedieval.Extensions;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.PlayerTriggeredEventSystem;
using NSMedieval.Production;
using NSMedieval.Repository;
using NSMedieval.Research;
using NSMedieval.Resources;
using NSMedieval.Roles;
using NSMedieval.RoomDetection;
using NSMedieval.State;
using NSMedieval.State.WorkerJobs;
using NSMedieval.StatsSystem;
using NSMedieval.Stockpiles;
using NSMedieval.Tools;
using NSMedieval.Types;
using NSMedieval.UI;
using NSMedieval.UI.Utils;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.View;
using NSMedieval.View.Animals;
using NSMedieval.Views.Resources;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using NSMedieval.WorldMap;
using NSMedieval.WorldMap.Caravan;
using Repository;
using Unity.Collections;
using UnityEngine;

namespace NSMedieval;

public class GlobalWarningMessagesManager : MonoSingleton<GlobalWarningMessagesManager>, IObserver
{
	private const int StockpileMessageDelay = 1;

	private const int RaidCountdownDelay = 1;

	private const int BedMessageDelay = 3;

	private const int FoodMessageDelay = 5;

	private const int MealProductionMessageDelay = 24;

	private const int ResearchBenchMessageDelay = 30;

	private const int ResearchAvailableMessageDelay = 30;

	private const int RecreationMessageDelay = 48;

	private const int DefensiveStructureMessageDelay = 77;

	private const int EventPossibleMessageDelay = 2;

	private const int RolePossibleMessageDelay = 3;

	private readonly HashSet<string> allCustomWarningMessages = new HashSet<string>();

	private readonly Dictionary<string, WarningMessageData> effectorWarningMessages = new Dictionary<string, WarningMessageData>();

	private readonly Dictionary<string, WarningMessageData> generalWarningMessages = new Dictionary<string, WarningMessageData>();

	private readonly Dictionary<string, WarningMessageData> objectiveWarningMessages = new Dictionary<string, WarningMessageData>();

	private readonly Dictionary<int, Action> scheduledMessages = new Dictionary<int, Action>();

	private readonly Dictionary<string, List<StatsInstance>> statsByEffectorWarning = new Dictionary<string, List<StatsInstance>>();

	private readonly List<Vec3Int> passThoughDestroyedPositions = new List<Vec3Int>();

	private readonly Dictionary<string, List<Vec3Int>> roomCreatedPositions = new Dictionary<string, List<Vec3Int>>();

	private int bedsCount;

	private int defensesCount;

	private int foodProductionCount;

	private int recreationBuildingCount;

	private int researchBenchCount;

	private int stockpileCount;

	private int totalWinterClothesCount;

	private int unlockableResearchCount;

	private int warningTmpClickCounter;

	private const string AggressiveAnimalsWarning = "AggressiveAnimalsWarning";

	private const string IdleWarning = "IdleWarning";

	private const string EventPossibleWarning = "EventPossibleWarning";

	private const string RaidWarning = "RaidWarning";

	private const string HunterMissingWeaponWarning = "HunterMissingWeaponWarning";

	private const string NeedTendingWarning = "NeedTendingWarning";

	private const string WarningStockpile = "WarningStockpile";

	private const string WarningDefensiveStructure = "WarningDefensiveStructure";

	private const string WarningBeds = "WarningBeds";

	private const string WarningLowFood = "WarningLowFood";

	private const string WarningMealProduction = "WarningMealProduction";

	private const string WarningRecreation = "WarningRecreation";

	private const string WarningResearchBench = "WarningResearchBench";

	private const string WarningAvailableResearch = "WarningAvailableResearch";

	private const string WarningWinterClothes = "WarningWinterClothes";

	private const string WarningUnarmedVillagers = "WarningUnarmedVillagers";

	private const string TraderWarning = "TraderWarning";

	private const string VisitorWarning = "RoleVisitorWarning";

	private const string CaravanArrivedWarning = "CaravanArrivedWarning";

	private const string TraderCantLeave = "TraderCantLeave";

	private const string CropBlightEventWarning = "CropBlightEventWarning";

	private const string AnimalsHungryWarning = "AnimalsHungryWarning";

	private const string RolePossibleWarning = "RolePossibleWarning";

	private const string RoleWorkHourWarning = "RoleWorkHourWarning";

	private const string WardenMissingRecruitWarning = "WardenMissingRecruitWarning";

	private const string PrisonerEscapingWarning = "PrisonerEscapingWarning";

	private const string GaolerMissingWarning = "GaolerMissingWarning";

	private const string PrisonCellMissingWarning = "PrisonMissingWarning";

	private const string RoleHourMissingWarning = "RoleHourMissingWarning";

	private const string FireWarning = "FireWarning";

	private const string EnemySiegeWeaponsWarning = "EnemySiegeWeaponsWarning";

	private const string BeggarWarning = "BeggarWarning";

	private const string MapMarkersWarning = "MapMarkersWarning";

	private const string CaravanAmbushWarning = "CaravanAmbushWarning";

	private const string SecondMapTimeRunningOut = "SecondMapTimeRunningOut";

	private const string PassThroughDestroyedWarning = "PassThroughDestroyedWarning";

	private const string RoomCreatedWarning = "RoomCreatedWarning";

	private VillageSaveData CurrentVillage
	{
		get
		{
			if (!MonoSingleton<GlobalSaveController>.IsInstantiated())
			{
				return null;
			}
			return GlobalSaveController.CurrentVillageData;
		}
	}

	private void InitMessages()
	{
		CreateWarnings();
		CreateObjectiveMessages();
	}

	private void CreateObjectiveMessages()
	{
		objectiveWarningMessages.Add("WarningStockpile", new WarningMessageData(WarningMessageCategory.Objective, "warning_message_short_MissingStockpile", "warning_message_info_MissingStockpile", "MissingStockpile", OnStockpileWarningClick));
		objectiveWarningMessages.Add("WarningDefensiveStructure", new WarningMessageData(WarningMessageCategory.Objective, "warning_message_short_DefensiveStructure", "warning_message_info_DefensiveStructure", "NoDefensiveStructure", OnDefensiveStructureWarningClick));
		objectiveWarningMessages.Add("WarningBeds", new WarningMessageData(WarningMessageCategory.Objective, "warning_message_short_MissingBed", "warning_message_info_MissingBed", "MissingBed", OnBedsWarningClick));
		objectiveWarningMessages.Add("WarningLowFood", new WarningMessageData(WarningMessageCategory.Objective, "warning_message_short_LowStockpileFood", "warning_message_info_LowStockpileFood", "LowStockpileFood", OnLowFoodWarningClick));
		objectiveWarningMessages.Add("WarningMealProduction", new WarningMessageData(WarningMessageCategory.Objective, "warning_message_short_MealProduction", "warning_message_info_MealProduction", "NoMealProduction", OnMealProductionWarningClick));
		objectiveWarningMessages.Add("WarningRecreation", new WarningMessageData(WarningMessageCategory.Objective, "warning_message_short_Recreation", "warning_message_info_Recreation", "NoRecreation", OnRecreationWarningClick));
		objectiveWarningMessages.Add("WarningResearchBench", new WarningMessageData(WarningMessageCategory.Objective, "warning_message_short_ResearchBench", "warning_message_info_ResearchBench", "NoResearchBench", OnResearchBenchWarningClick));
		objectiveWarningMessages.Add("WarningAvailableResearch", new WarningMessageData(WarningMessageCategory.Objective, "warning_message_short_AvailableResearch", "warning_message_info_AvailableResearch", "ResearchAvailable", OnAvailableResearchWarningClick, OnAvailableResearchTooltip));
		objectiveWarningMessages.Add("WarningWinterClothes", new WarningMessageData(WarningMessageCategory.Objective, "warning_message_short_WinterClothes", "warning_message_info_WinterClothes", "NoWinterClothes", OnWinterClothesWarningClick, OnWinterClothesTooltip));
		objectiveWarningMessages.Add("WarningUnarmedVillagers", new WarningMessageData(WarningMessageCategory.Objective, "warning_message_short_UnarmedVillagers", "warning_message_info_UnarmedVillagers", "UnarmedVillagers", OnUnarmedVillagersWarningClick, OnUnarmedVillagersTooltip));
	}

	private void CreateWarnings()
	{
		generalWarningMessages.Add("RaidWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_Raid", "warning_message_info_Raid", "Raid", OnRaidClick, OnRaidProcessTooltip));
		generalWarningMessages.Add("AggressiveAnimalsWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_AnimalRaid", "warning_message_info_AnimalRaid", "Raid", OnAggressiveAnimalsClick, OnAggressiveAnimalsProcessTooltip));
		generalWarningMessages.Add("IdleWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_Idle", "warning_message_info_Idle", "Idle", OnVillagersIdleClick, OnVillagersIdleProcessTooltip));
		generalWarningMessages.Add("HunterMissingWeaponWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_HunterMissingWeapon", "warning_message_info_HunterMissingWeapon", "HunterMissingWeapon", OnHunterMissingWeaponClick, OnHunterMissingWeaponTooltip));
		generalWarningMessages.Add("NeedTendingWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_NeedTending", "warning_message_info_NeedTending", "NeedTending", OnNeedTendingClick, OnNeedTendingTooltip, null, showInPlayerVillageOnly: false));
		generalWarningMessages.Add("TraderWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_trader", "warning_message_info_trader", "Idle", OnTraderClick, OnTraderProcessTooltip));
		generalWarningMessages.Add("BeggarWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_beggar", "warning_message_info_beggar", "Idle", OnBeggarClick, OnBeggarProcessTooltip));
		generalWarningMessages.Add("MapMarkersWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_map_markers_available", "warning_message_info_map_markers_available", "Idle", OnMapMarkerClick, OnMapMarkerProcessTooltips));
		generalWarningMessages.Add("CaravanAmbushWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_ambush", "warning_message_info_ambush", "Idle", OnAmbushClick, OnAmbushProcessTooltips));
		generalWarningMessages.Add("RoleVisitorWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_visitor", "warning_message_info_visitor", "Idle", OnVisitorClick, OnVisitorProcessTooltip));
		generalWarningMessages.Add("CaravanArrivedWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_caravan", "warning_message_short_caravan", "Idle", OnCaravanClick, OnCaravanProcessTooltip));
		generalWarningMessages.Add("TraderCantLeave", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_trader_cant_leave", "warning_message_info_trader_cant_leave", "Raid", OnTraderCantLeaveClick, OnTraderCantLeaveProcessTooltip));
		generalWarningMessages.Add("CropBlightEventWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_crop_blight", "warning_message_info_crop_blight", "Raid", OnCropBlightClick));
		generalWarningMessages.Add("AnimalsHungryWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_animals_hungry", "warning_message_info_animals_hungry", "Hunger", OnAnimalsHungryClick, OnAnimalsHungryProcessTooltip));
		generalWarningMessages.Add("FireWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_fire", "warning_message_info_fire", "Raid", OnFireClick, OnFireProcessTooltip));
		generalWarningMessages.Add("EnemySiegeWeaponsWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_enemy_siege", "warning_message_info_enemy_siege", "Raid", OnEnemySiegeWeaponClick, OnEnemySiegeWeaponProcessTooltip));
		generalWarningMessages.Add("EventPossibleWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_event_possible", "warning_message_info_event_possible", "Idle", OnEventPossibleClick));
		generalWarningMessages.Add("RolePossibleWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_role_possible", "warning_message_info_role_possible", "Idle", OnRolePossibleClick, OnRolePossibleProcessTooltip));
		generalWarningMessages.Add("RoleWorkHourWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_role_hour", "warning_message_info_role_hour", "Idle", OnRoleWorkHourClick, OnRoleWorkHourProcessTooltip));
		generalWarningMessages.Add("WardenMissingRecruitWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_warden_recruit", "warning_message_info_warden_recruit", "Idle"));
		generalWarningMessages.Add("PrisonerEscapingWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_prisoner_run", "warning_message_info_prisoner_run", "WarningMessageIconRed", OnPrisonerEscapingClick, OnPrisonerEscapingProcessTooltip));
		generalWarningMessages.Add("GaolerMissingWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_gaoler_missing", "warning_message_info_gaoler_missing", "Idle", OnGaolerMissingClick));
		generalWarningMessages.Add("PrisonMissingWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_prison_cell", "warning_message_info_prison_cell", "WarningMessageIconRed", OnPrisonCellMissingClick));
		generalWarningMessages.Add("RoleHourMissingWarning", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_no_role_hour", "warning_message_info_no_role_hour", "Idle", OnRoleWorkHourMissingClick, OnRoleWorkHourMissingProcessTooltip));
		generalWarningMessages.Add("PassThroughDestroyedWarning", new WarningMessageData(WarningMessageCategory.WarningClosable, "warning_message_pass_through_destroyed", "warning_message_info_pass_through_destroyed", "Idle", OnPassThroughDestroyedClick, null, OnPassThroughDestroyCloseClick));
		generalWarningMessages.Add("SecondMapTimeRunningOut", new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_caravan_leave", "warning_message_info_caravan_leave", "Raid", null, null, null, showInPlayerVillageOnly: false));
		foreach (RoomType allItem in Repository<RoomTypeRepository, RoomType>.Instance.GetAllItems())
		{
			string iD = allItem.GetID();
			if (!iD.Equals("default") && !iD.Equals("bedroom_shared"))
			{
				WarningMessageData value = new WarningMessageData(WarningMessageCategory.WarningClosable, "warning_message_room_created", LocKeyUtils.GetInfo(allItem.LocKeys), "WarningMessageIconGray", OnRoomCreatedClick, closeClickAction: OnRoomCreatedCloseClick, processTooltipDataAction: OnRoomCreatedProcessTooltip, showInPlayerVillageOnly: true, id: iD);
				generalWarningMessages.Add(iD, value);
				roomCreatedPositions[iD] = new List<Vec3Int>();
			}
		}
		foreach (NPCCustomWarningMessage allItem2 in Repository<NPCCustomWarningMessageRepository, NPCCustomWarningMessage>.Instance.GetAllItems())
		{
			string shortInfo = allItem2.ShortInfo;
			string info2 = allItem2.Info;
			string icon2 = allItem2.Icon;
			WarningMessageData value2 = new WarningMessageData(allItem2.Category, shortInfo, info2, icon2, OnCustomNpcWarningClick, OnCustomNpcWarningProcessTooltip);
			generalWarningMessages.Add(allItem2.GetID(), value2);
		}
	}

	public void SetTraderCantLeaveMapMessageVisible(bool traderCannotLeave)
	{
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["TraderCantLeave"], traderCannotLeave);
	}

	public void SetEnemySiegeWeaponsMessageVisible(bool visible)
	{
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["EnemySiegeWeaponsWarning"], visible);
	}

	public void SetWarningMessageSecondMapTimeout(int minutesTimer)
	{
		WarningMessageData warningMessageData = generalWarningMessages["SecondMapTimeRunningOut"];
		warningMessageData.SetTimer(minutesTimer);
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(warningMessageData, visible: true);
	}

	public void SetEffectorMessageVisible(string messageName, bool visible, StatsInstance statsInstance)
	{
		HumanoidInstance ownerHumanoidInstance = statsInstance.OwnerHumanoidInstance;
		if (ownerHumanoidInstance == null || !ownerHumanoidInstance.IsCaptive() || ownerHumanoidInstance.CaptiveNpcBehaviour.IsPlayerVillagePrisoner || (!(messageName == "Hunger") && !(messageName == "Unconscious")))
		{
			if (!statsByEffectorWarning.ContainsKey(messageName))
			{
				statsByEffectorWarning.Add(messageName, new List<StatsInstance>());
			}
			if (visible && !statsByEffectorWarning[messageName].Contains(statsInstance))
			{
				statsByEffectorWarning[messageName].Add(statsInstance);
			}
			else if (!visible && statsByEffectorWarning[messageName].Contains(statsInstance))
			{
				statsByEffectorWarning[messageName].Remove(statsInstance);
			}
			RefreshEffectorMessageVisible(messageName);
		}
	}

	public void JobConfigUpdated(HumanoidInstance humanoidInstance)
	{
		RefreshHunterMessage();
		RefreshGaolerMissingMessage();
	}

	public void OnEquipmentChanged()
	{
		RefreshHunterMessage();
		RefreshUnarmedVillagersMessage();
	}

	public void OnWorkerScheduleChanged()
	{
		RefreshRoleHourMessage();
		RefreshRoleHourMissingMessage();
	}

	public void RefreshPrisonerEscapingMessage()
	{
		bool visible = MonoSingleton<NPCManager>.Instance.AnyNpc((HumanoidInstance npc) => npc.ActiveBehaviour is PrisonerBehaviour prisonerBehaviour && prisonerBehaviour.IsEscaping);
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["PrisonerEscapingWarning"], visible);
	}

	public void RefreshGaolerMissingMessage()
	{
		if (!MonoSingleton<NPCManager>.Instance.AnyNpc((HumanoidInstance npc) => !npc.HasDisposed && !npc.IsInIncognitoMode() && npc.ActiveBehaviour is PrisonerBehaviour prisonerBehaviour && prisonerBehaviour.IsPlayerVillagePrisoner))
		{
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["GaolerMissingWarning"], visible: false);
			return;
		}
		bool visible = true;
		foreach (HumanoidInstance key in MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys)
		{
			if (!key.IsInIncognitoMode() && (key.WorkerBehaviour.ActiveJobCombination & JobType.Gaoler) != 0)
			{
				visible = false;
				break;
			}
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["GaolerMissingWarning"], visible);
	}

	private void RefreshPrisonCellMissingMessage()
	{
		if (!MonoSingleton<NPCManager>.Instance.AnyNpc((HumanoidInstance npc) => !npc.HasDisposed && npc.ActiveBehaviour is PrisonerBehaviour prisonerBehaviour && prisonerBehaviour.IsPlayerVillagePrisoner))
		{
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["PrisonMissingWarning"], visible: false);
			return;
		}
		bool visible = !VillageManager.ActiveVillage.Map.RoomDetection.AnyRoomSafe((Room room) => room.RoomType != null && room.RoomType.Prison);
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["PrisonMissingWarning"], visible);
	}

	private void OnPrisonCellMissingClick(WarningMessageData message)
	{
		MonoSingleton<UIController>.Instance.ToggleConstructionCategory(BuildingCategoryUI.Decoration, BuildingSubCategoryUI.SubCtgPrisonMarker.ToString(), BuildingSubCategoryUI.SubCtgPrisonStash.ToString());
	}

	private void OnPrisonerEscapingProcessTooltip(List<string> textlines, WarningMessageData warningmessagedata)
	{
	}

	private void OnPrisonerEscapingClick(WarningMessageData message)
	{
		using PooledList<PrisonerBehaviour> pooledList = MonoSingleton<NPCManager>.Instance.GetNPCsPooled((PrisonerBehaviour prisoner) => prisoner.IsEscaping);
		if (pooledList.Count > 0)
		{
			HumanoidInstance humanoid = pooledList[warningTmpClickCounter++ % pooledList.Count].Humanoid;
			NPCView agentView = humanoid.GetAgentView<NPCView>();
			if (!(agentView == null))
			{
				MonoSingleton<SelectableObjectManager>.Instance.DeselectAll();
				agentView.Select();
				MonoSingleton<RtsCamera>.Instance.JumpTo(humanoid.GetPosition());
			}
		}
	}

	private void OnGaolerMissingClick(WarningMessageData message)
	{
		using PooledList<PrisonerBehaviour> pooledList = MonoSingleton<NPCManager>.Instance.GetNPCsPooled<PrisonerBehaviour>();
		if (pooledList.Count > 0)
		{
			HumanoidInstance humanoid = pooledList[warningTmpClickCounter++ % pooledList.Count].Humanoid;
			NPCView agentView = humanoid.GetAgentView<NPCView>();
			if (!(agentView == null))
			{
				MonoSingleton<SelectableObjectManager>.Instance.DeselectAll();
				agentView.Select();
				MonoSingleton<RtsCamera>.Instance.JumpTo(humanoid.GetPosition());
			}
		}
	}

	private WarningMessageData GetEffectorMessage(string messageName)
	{
		if (effectorWarningMessages == null || !effectorWarningMessages.ContainsKey(messageName))
		{
			return null;
		}
		return effectorWarningMessages[messageName];
	}

	private void Start()
	{
		InitMessages();
		MonoSingleton<LoadingController>.Instance.MainSceneLoadedEvent += OnMainSceneLoaded;
	}

	private void OnMainSceneLoaded()
	{
		MonoSingleton<LoadingController>.Instance.MainSceneLoadedEvent -= OnMainSceneLoaded;
		MonoSingleton<UIController>.Instance.GameStartedEvent += OnGameplayStart;
		MonoSingleton<GoapController>.Instance.OnGoalStartedEvent += OnGoalStarted;
		MonoSingleton<GoapController>.Instance.OnGoalEndedEvent += OnGoapUpdated;
		MonoSingleton<NPCController>.Instance.OnNPCChanged += OnNpcChanged;
		MonoSingleton<NPCController>.Instance.OnOwnerSetEvent += OnPrisonerOwnerChanged;
		MonoSingleton<NPCController>.Instance.OnMarkedForRecruitmentEvent += OnNpcMarkedForRecruitment;
		MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent += new Action<BaseBuildingInstance>(OnBuildingChanges);
		MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent += new Action<BaseBuildingInstance>(OnObjectBuilt);
		MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent += new Action<BaseBuildingInstance>(OnBuildingChanges);
		MonoSingleton<ConstructionController>.Instance.ObjectDestroyedOnPassThroughEvent += OnObjectPassThroughDestroyed;
		MonoSingleton<WorkerController>.Instance.CreateWorkerEvent += OnWorkersChanged;
		MonoSingleton<WorkerController>.Instance.RemoveWorkerEvent += OnRemoveWorker;
		MonoSingleton<WorkerController>.Instance.SpawnWorkerEvent += OnWorkersChanged;
		MonoSingleton<AnimalController>.Instance.MarkForOrderEvent += OnAnimalMarkForOrder;
		MonoSingleton<AnimalController>.Instance.HealthDepletedEvent += OnAnimalDied;
		MonoSingleton<AnimalController>.Instance.DieFormStarvationEvent += OnAnimalDied;
		MonoSingleton<AnimalController>.Instance.SpawnAnimalEvent += OnAnimalAdded;
		MonoSingleton<AnimalController>.Instance.RemovedAnimalEvent += OnAnimalDied;
		MonoSingleton<AnimalController>.Instance.OnAnimalTypeChangedEvent += OnAnimalTypeChanged;
		MonoSingleton<AnimalController>.Instance.HungerEvent += OnHungerChanged;
		MonoSingleton<AnimalController>.Instance.RemovedAnimalEvent += OnHungerChanged;
		MonoSingleton<ResourcePileController>.Instance.ResourceCountChangeEvent += OnAvailableResourcesChanged;
		MonoSingleton<World>.Instance.MapLoadedEvent += OnMapLoaded;
		Log.Debug("MapLoadedEvent subscribed to", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
		MonoSingleton<WorldTimeManager>.Instance.QuarterHourUpdateEvent += OnQuarterHourUpdate;
		MonoSingleton<WorldTimeManager>.Instance.DateUpdateEvent += OnDateUpdate;
		MonoSingleton<ResearchController>.Instance.UnlockResearchEvent += OnResearchChange;
		MonoSingleton<ResearchController>.Instance.LockResearchEvent += OnResearchChange;
		MonoSingleton<ResearchController>.Instance.ActivateResearchEvent += OnResearchActivate;
		MonoSingleton<RaidController>.Instance.RaidSpawnedEvent += OnRaidStart;
		MonoSingleton<RaidController>.Instance.RaidEndedEvent += OnRaidEnd;
		MonoSingleton<StockpileController>.Instance.StockpilePlacedEvent += OnStockpilePlaced;
		MonoSingleton<StockpileController>.Instance.StockpileDestroyedEvent += OnStockpileDestroyed;
		MonoSingleton<FactionsController>.Instance.FriendlinessChangedEvent += OnFactionFriendlinessChanged;
		MonoSingleton<CropBlightController>.Instance.BlightStartedEvent += OnCropBlightStarted;
		MonoSingleton<CropBlightController>.Instance.BlightEndedEvent += OnCropBlightEnded;
		MonoSingleton<CaravanController>.Instance.CaravanStateChangedEvent += OnCaravanStateChanged;
		MonoSingleton<CaravanController>.Instance.CaravanCreatedEvent += OnCaravanStarted;
		MonoSingleton<CaravanController>.Instance.CaravanReturnedHomeEvent += OnCaravanEnded;
		MonoSingleton<PlayerTriggeredEventManager>.Instance.EventViewShownEvent += OnEventViewShown;
		MonoSingleton<RoleManager>.Instance.RoleViewShownEvent += OnRoleViewShown;
		MonoSingleton<RoomDetectionController>.Instance.RoomAddedEvent += OnRoomChanged;
		MonoSingleton<RoomDetectionController>.Instance.RoomRemovedEvent += OnRoomRemoved;
		MonoSingleton<RoomDetectionController>.Instance.RoomTypeChangedEvent += OnRoomChanged;
		MonoSingleton<RoleManager>.Instance.RoleChangedEvent += OnRoleChanged;
		MonoSingleton<FireController>.Instance.FirstFireLitEvent += OnFirstFireLit;
		MonoSingleton<FireController>.Instance.LastFirePutOutEvent += OnLastFirePutOut;
		MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.MarkerManager.MarkerCreatedEvent += OnWorldMapMarkersChanged;
		MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.MarkerManager.MarkerDestroyedEvent += OnWorldMapMarkersChanged;
		InitEffectorMessages();
	}

	private void OnDisable()
	{
		if (MonoSingleton<GoapController>.IsInstantiated())
		{
			MonoSingleton<GoapController>.Instance.OnGoalStartedEvent -= OnGoalStarted;
			MonoSingleton<GoapController>.Instance.OnGoalEndedEvent -= OnGoapUpdated;
		}
		if (MonoSingleton<NPCController>.IsInstantiated())
		{
			MonoSingleton<NPCController>.Instance.OnNPCChanged -= OnNpcChanged;
			MonoSingleton<NPCController>.Instance.OnOwnerSetEvent -= OnPrisonerOwnerChanged;
			MonoSingleton<NPCController>.Instance.OnMarkedForRecruitmentEvent -= OnNpcMarkedForRecruitment;
		}
		if (MonoSingleton<ConstructionController>.IsInstantiated())
		{
			MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent -= new Action<BaseBuildingInstance>(OnBuildingChanges);
			MonoSingleton<ConstructionController>.Instance.AfterConstructionCompletedEvent -= new Action<BaseBuildingInstance>(OnObjectBuilt);
			MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent -= new Action<BaseBuildingInstance>(OnBuildingChanges);
			MonoSingleton<ConstructionController>.Instance.ObjectDestroyedOnPassThroughEvent -= OnObjectPassThroughDestroyed;
		}
		if (MonoSingleton<WorkerController>.IsInstantiated())
		{
			MonoSingleton<WorkerController>.Instance.CreateWorkerEvent -= OnWorkersChanged;
			MonoSingleton<WorkerController>.Instance.RemoveWorkerEvent -= OnRemoveWorker;
			MonoSingleton<WorkerController>.Instance.SpawnWorkerEvent -= OnWorkersChanged;
		}
		if (MonoSingleton<AnimalController>.IsInstantiated())
		{
			MonoSingleton<AnimalController>.Instance.MarkForOrderEvent -= OnAnimalMarkForOrder;
			MonoSingleton<AnimalController>.Instance.HealthDepletedEvent -= OnAnimalDied;
			MonoSingleton<AnimalController>.Instance.DieFormStarvationEvent -= OnAnimalDied;
			MonoSingleton<AnimalController>.Instance.SpawnAnimalEvent -= OnAnimalAdded;
			MonoSingleton<AnimalController>.Instance.RemovedAnimalEvent -= OnAnimalDied;
			MonoSingleton<AnimalController>.Instance.OnAnimalTypeChangedEvent -= OnAnimalTypeChanged;
			MonoSingleton<AnimalController>.Instance.HungerEvent -= OnHungerChanged;
			MonoSingleton<AnimalController>.Instance.RemovedAnimalEvent -= OnHungerChanged;
		}
		if (MonoSingleton<ResourcePileController>.IsInstantiated())
		{
			MonoSingleton<ResourcePileController>.Instance.ResourceCountChangeEvent -= OnAvailableResourcesChanged;
		}
		if (MonoSingleton<World>.IsInstantiated())
		{
			MonoSingleton<World>.Instance.MapLoadedEvent -= OnMapLoaded;
		}
		if (MonoSingleton<WorldTimeManager>.IsInstantiated())
		{
			MonoSingleton<WorldTimeManager>.Instance.QuarterHourUpdateEvent -= OnQuarterHourUpdate;
			MonoSingleton<WorldTimeManager>.Instance.DateUpdateEvent -= OnDateUpdate;
		}
		if (MonoSingleton<ResearchController>.IsInstantiated())
		{
			MonoSingleton<ResearchController>.Instance.UnlockResearchEvent -= OnResearchChange;
			MonoSingleton<ResearchController>.Instance.LockResearchEvent -= OnResearchChange;
			MonoSingleton<ResearchController>.Instance.ActivateResearchEvent -= OnResearchActivate;
		}
		if (MonoSingleton<RaidController>.IsInstantiated())
		{
			MonoSingleton<RaidController>.Instance.RaidSpawnedEvent -= OnRaidStart;
			MonoSingleton<RaidController>.Instance.RaidEndedEvent -= OnRaidEnd;
		}
		if (MonoSingleton<StockpileController>.IsInstantiated())
		{
			MonoSingleton<StockpileController>.Instance.StockpilePlacedEvent -= OnStockpilePlaced;
			MonoSingleton<StockpileController>.Instance.StockpileDestroyedEvent -= OnStockpileDestroyed;
		}
		if (MonoSingleton<FactionsController>.IsInstantiated())
		{
			MonoSingleton<FactionsController>.Instance.FriendlinessChangedEvent -= OnFactionFriendlinessChanged;
		}
		if (MonoSingleton<CropBlightController>.IsInstantiated())
		{
			MonoSingleton<CropBlightController>.Instance.BlightStartedEvent -= OnCropBlightStarted;
			MonoSingleton<CropBlightController>.Instance.BlightEndedEvent -= OnCropBlightEnded;
		}
		if (MonoSingleton<CaravanController>.IsInstantiated())
		{
			MonoSingleton<CaravanController>.Instance.CaravanStateChangedEvent -= OnCaravanStateChanged;
			MonoSingleton<CaravanController>.Instance.CaravanCreatedEvent -= OnCaravanStarted;
			MonoSingleton<CaravanController>.Instance.CaravanReturnedHomeEvent -= OnCaravanEnded;
		}
		if (MonoSingleton<FireController>.IsInstantiated())
		{
			MonoSingleton<FireController>.Instance.FirstFireLitEvent -= OnFirstFireLit;
			MonoSingleton<FireController>.Instance.LastFirePutOutEvent -= OnLastFirePutOut;
		}
		if (MonoSingleton<UIController>.IsInstantiated())
		{
			MonoSingleton<UIController>.Instance.GameStartedEvent -= OnGameplayStart;
		}
		if (MonoSingleton<PlayerTriggeredEventManager>.IsInstantiated())
		{
			MonoSingleton<PlayerTriggeredEventManager>.Instance.EventViewShownEvent -= OnEventViewShown;
		}
		if (MonoSingleton<RoleManager>.IsInstantiated())
		{
			MonoSingleton<RoleManager>.Instance.RoleViewShownEvent -= OnRoleViewShown;
		}
		if (MonoSingleton<RoomDetectionController>.IsInstantiated())
		{
			MonoSingleton<RoomDetectionController>.Instance.RoomAddedEvent -= OnRoomChanged;
			MonoSingleton<RoomDetectionController>.Instance.RoomRemovedEvent -= OnRoomRemoved;
			MonoSingleton<RoomDetectionController>.Instance.RoomTypeChangedEvent -= OnRoomChanged;
		}
		if (MonoSingleton<RoleManager>.IsInstantiated())
		{
			MonoSingleton<RoleManager>.Instance.RoleChangedEvent -= OnRoleChanged;
		}
		if (MonoSingleton<NSMedieval.WorldMap.WorldMap>.IsInstantiated())
		{
			MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.MarkerManager.MarkerCreatedEvent -= OnWorldMapMarkersChanged;
			MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.MarkerManager.MarkerDestroyedEvent -= OnWorldMapMarkersChanged;
		}
	}

	private void OnRoleChanged(Role role, HumanoidInstance humanoidInstance)
	{
		RefreshRoleHourMessage();
		RefreshRoleHourMissingMessage();
		RefreshMissingWardenRecruitMessage();
	}

	private void OnNpcMarkedForRecruitment(CaptiveNpcBehaviour captiveNpcBehaviour, bool markedForRecruitment)
	{
		RefreshMissingWardenRecruitMessage();
	}

	private void OnDateUpdate()
	{
		RefreshWinterClothesMessage();
	}

	private void OnQuarterHourUpdate()
	{
		int[] array = scheduledMessages.Keys.ToArray();
		foreach (int num in array)
		{
			if (TimeToShow(num))
			{
				scheduledMessages[num]();
				scheduledMessages.Remove(num);
			}
		}
		RefreshRolePossibleMessage();
		RefreshGaolerMissingMessage();
		RefreshWorldMapMessages();
		RefreshLeaveSecondMapTimerMessage();
	}

	private bool TimeToShow(long timeToShow)
	{
		return CurrentVillage.DateAndTime.HoursTotalZero >= timeToShow;
	}

	private void OnGoapUpdated(Agent agent, string goalId)
	{
		if (!(agent.GetType() != typeof(WorkerGoapAgent)))
		{
			if (goalId == "EquipGoal" || goalId == "AutoEquipGoal")
			{
				RefreshUnarmedVillagersMessage();
			}
			else
			{
				RefreshWorkerWoundsMessage();
			}
		}
	}

	private void OnGoapUpdated(Agent agent, string goalId, GoalCondition state)
	{
		OnGoapUpdated(agent, goalId);
		if (((agent.AgentOwner as HumanoidInstance)?.WorkerBehaviour?.IsDrafting).GetValueOrDefault() && goalId != null && goalId.Equals("IdleGoal"))
		{
			bool visible = GlobalSaveController.CurrentVillageData.Workers.Any((HumanoidInstance worker) => worker.WorkerBehaviour.IsIdle && !worker.WorkerBehaviour.IsCrazy && !worker.WorkerBehaviour.IsDrafting);
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["IdleWarning"], visible);
		}
	}

	private void OnGameplayStart(bool start)
	{
		if (!start)
		{
			return;
		}
		OnQuarterHourUpdate();
		RefreshEventPossibleMessage();
		List<CaravanInstance> caravans = MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.Data.Caravans;
		if (caravans == null || caravans.Count <= 0)
		{
			return;
		}
		foreach (CaravanInstance item in caravans)
		{
			HashSet<HumanoidInstance> workers = item.Workers;
			if (workers == null || workers.Count <= 0)
			{
				continue;
			}
			foreach (HumanoidInstance item2 in workers)
			{
				OnRemoveWorker(item2);
			}
		}
	}

	private void OnGoalStarted(Agent agent, Goal goal)
	{
		OnGoapUpdated(agent, goal.Id);
		if (agent is WorkerGoapAgent)
		{
			bool visible = GlobalSaveController.CurrentVillageData.Workers.Any((HumanoidInstance worker) => worker.WorkerBehaviour.IsIdle && !worker.WorkerBehaviour.IsCrazy && !worker.WorkerBehaviour.IsDrafting);
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["IdleWarning"], visible);
		}
	}

	private void OnNpcChanged(HumanoidInstance humanoid)
	{
		RefreshRaidWarningMessage();
		RefreshPrisonCellMissingMessage();
		RefreshAllHostilesKilledBBT(humanoid);
	}

	private void OnWorldMapMarkersChanged(WorldMapMarkerPlace obj)
	{
		RefreshWorldMapMessages();
	}

	private void OnPrisonerOwnerChanged(CaptiveNpcBehaviour prisoner)
	{
		RefreshPrisonCellMissingMessage();
	}

	private void RefreshWorldMapMessages()
	{
		bool hasVisibleMarkers = MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.MarkerManager.HasVisibleMarkers;
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["MapMarkersWarning"], hasVisibleMarkers);
		WarningMessageData warningMessageData = generalWarningMessages["CaravanAmbushWarning"];
		if (MonoSingleton<CaravanManager>.Instance.IsAnyCaravanAmbushed())
		{
			long num = long.MaxValue;
			foreach (CaravanInstance caravan in MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.Data.Caravans)
			{
				if (caravan.EventContext is AmbushContext ambushContext)
				{
					long num2 = ambushContext.MinutesToAutoSurrenderAmbush();
					if (num2 < num)
					{
						num = num2;
					}
				}
			}
			warningMessageData.SetTimer((int)num);
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(warningMessageData, visible: true);
			MonoSingleton<WarningMessageController>.Instance.RefreshMessage(warningMessageData, visible: true);
		}
		else
		{
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(warningMessageData, visible: false);
		}
	}

	private void RefreshLeaveSecondMapTimerMessage()
	{
		if (GlobalSaveController.CurrentVillageData.IsSecondMap && VillageManager.ActiveVillage.Map.SecondMapLeaveManager.IsTimeRunningOutMessageVisible)
		{
			WarningMessageData warningMessageData = generalWarningMessages["SecondMapTimeRunningOut"];
			int num = GlobalSaveController.CurrentVillageData.DateAndTime.MinutesInHour / 4;
			int timer = warningMessageData.Timer - num;
			warningMessageData.SetTimer(timer);
			MonoSingleton<WarningMessageController>.Instance.RefreshMessage(warningMessageData, visible: true);
		}
	}

	private void RefreshRaidWarningMessage()
	{
		using PooledHashSet<string> pooledHashSet = HashSetPool<string>.GetJanitor();
		List<HumanoidInstance> nPCs = GlobalSaveController.CurrentVillageData.NPCs;
		foreach (HumanoidInstance item in nPCs)
		{
			if (!string.IsNullOrEmpty(item.CustomWarningMessage))
			{
				pooledHashSet.Add(item.CustomWarningMessage);
			}
		}
		allCustomWarningMessages.UnionWith(pooledHashSet);
		foreach (string allCustomWarningMessage in allCustomWarningMessages)
		{
			bool visible = pooledHashSet.Contains(allCustomWarningMessage);
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages[allCustomWarningMessage], visible);
		}
		bool visible2 = nPCs.Count > 0 && nPCs.Any((HumanoidInstance npcInstance) => npcInstance.CustomWarningMessage == null && npcInstance.IsEnemy());
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["RaidWarning"], visible2);
		bool visible3 = nPCs.Count > 0 && nPCs.Any((HumanoidInstance npcInstance) => npcInstance.CustomWarningMessage == null && npcInstance.IsTrader() && !npcInstance.IsEnemy());
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["TraderWarning"], visible3);
		bool visible4 = nPCs.Count > 0 && nPCs.Any((HumanoidInstance npcInstance) => npcInstance.CustomWarningMessage == null && npcInstance.ActiveBehaviour.HumanoidRoleOwner.AssignedRole && !npcInstance.IsEnemy());
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["RoleVisitorWarning"], visible4);
		bool visible5 = nPCs.Count > 0 && nPCs.Any((HumanoidInstance npcInstance) => npcInstance.CustomWarningMessage == null && npcInstance.IsBeggar() && string.IsNullOrEmpty(npcInstance.CustomWarningMessage));
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["BeggarWarning"], visible5);
	}

	private void OnStockpilePlaced(StockpileInstance stockpileInstance)
	{
		RefreshStockpileMessage();
	}

	private void OnStockpileDestroyed(StockpileInstance stockpileInstance)
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "OnStockpileDestroyed", RefreshStockpileMessage);
	}

	private void OnObjectBuilt(BaseBuildingInstance baseBuildingInstance)
	{
		foreach (Vec3Int position in baseBuildingInstance.Positions)
		{
			if (passThoughDestroyedPositions.Contains(position))
			{
				passThoughDestroyedPositions.Remove(position);
				RefreshDestroyOnPassThroughMessage();
				break;
			}
		}
	}

	private void OnObjectPassThroughDestroyed(BaseBuildingInstance baseBuildingInstance)
	{
		Vec3Int gridPosition = baseBuildingInstance.GetGridPosition();
		if (!passThoughDestroyedPositions.Contains(gridPosition))
		{
			passThoughDestroyedPositions.Add(gridPosition);
			RefreshDestroyOnPassThroughMessage();
		}
	}

	private void OnBuildingChanges(BaseBuildingInstance building)
	{
		if (building.BuildingType.HasFlag(BuildingType.Bed))
		{
			RefreshBedsMessage();
		}
		if (building.BuildingType.HasFlag(BuildingType.ProductionBuilding))
		{
			RefreshMealProductionMessage();
			RefreshResearchBenchMessage();
		}
		if (building.BuildingType.HasFlag(BuildingType.Shrine))
		{
			RefreshRecreationMessage();
		}
		if (building.BuildingType.HasFlag(BuildingType.Merlon) || building.BuildingType.HasFlag(BuildingType.Trap))
		{
			RefreshDefensiveStructureMessage();
		}
		BaseBuildingBlueprint blueprint = building.Blueprint;
		if ((object)blueprint != null && blueprint.PlayerTriggeredEvents?.Count > 0)
		{
			RefreshEventPossibleMessage();
		}
	}

	private void OnAvailableResourcesChanged(Resource resource, ResourcePileCount pileCount)
	{
		if (resource == null || (resource.Category & ResourceCategory.CtgEdible) == ResourceCategory.CtgEdible)
		{
			MonoSingleton<TaskController>.Instance.OptimizedCall(this, "ResChange", RefreshLowFoodMessage);
		}
		if (resource == null || (resource.Category & ResourceCategory.CtgItem) == ResourceCategory.CtgItem)
		{
			MonoSingleton<TaskController>.Instance.OptimizedCall(this, "WinterClothesChange", RefreshWinterClothesMessage);
		}
		if (resource == null || (resource.Category & ResourceCategory.CtgResearch) == ResourceCategory.CtgResearch)
		{
			MonoSingleton<TaskController>.Instance.OptimizedCall(this, "ResearchChange", RefreshAvailableResearchMessage);
		}
	}

	private void OnResearchChange(ResearchNodeInstance node)
	{
		MonoSingleton<TaskController>.Instance.WaitFor(0.3f).Then(RefreshAvailableResearchMessage);
	}

	private void OnResearchActivate(ResearchNodeInstance node, bool afterLoading, bool forceUnlock)
	{
		MonoSingleton<TaskController>.Instance.WaitFor(0.3f).Then(RefreshAvailableResearchMessage);
	}

	private void OnAnimalDied(AnimalInstance animalInstance)
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "HuntWMsg", RefreshHunterMessage);
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "AggressiveAnimalMsg", RefreshAggressiveAnimalsMessage);
		RefreshAllHostilesKilledBBT(animalInstance);
	}

	private void OnAnimalMarkForOrder(AnimalOrderType orderType, AnimalInstance animalInstance)
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "HuntWMsg", RefreshHunterMessage);
	}

	private void OnAnimalTypeChanged(AnimalInstance animal)
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "AggressiveAnimalMsg", RefreshAggressiveAnimalsMessage);
	}

	private void OnAnimalAdded(AnimalInstance animal)
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "AggressiveAnimalMsg", RefreshAggressiveAnimalsMessage);
	}

	private void OnHungerChanged(AnimalInstance animal)
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "AnimalsHungryMsg", RefreshAnimalsHungryMessage);
	}

	private void RefreshAggressiveAnimalsMessage()
	{
		bool visible = MonoSingleton<AnimalManager>.Instance.Animals.Keys.Any((AnimalInstance animal) => animal != null && !animal.HasDied && !animal.HasDisposed && animal.AnimalType == AnimalType.WildAggressive);
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["AggressiveAnimalsWarning"], visible);
	}

	private void RefreshAllHostilesKilledBBT(CreatureBase killedOrChangedCreature)
	{
		VillageSaveData currentVillageData = GlobalSaveController.CurrentVillageData;
		if (!currentVillageData.IsSecondMap || currentVillageData.WorldMapPlace.SecondMapType != SecondMapType.LootStash || !killedOrChangedCreature.HasDied || !killedOrChangedCreature.HasDisposed || killedOrChangedCreature is AnimalInstance { AnimalType: not AnimalType.WildAggressive } || (killedOrChangedCreature is HumanoidInstance humanoidInstance && !humanoidInstance.ContainsBehaviourType(BehaviourType.Enemy)))
		{
			return;
		}
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "AllEnemiesKilledMsg", delegate
		{
			if (!MonoSingleton<AnimalManager>.Instance.HasHostileAnimals() && !MonoSingleton<NPCManager>.Instance.HasHostileNPCs())
			{
				MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage("all_enemies_killed".ToLocalized());
			}
		});
	}

	private void RefreshAnimalsHungryMessage()
	{
		int num = 0;
		foreach (AnimalInstance key in MonoSingleton<AnimalManager>.Instance.Animals.Keys)
		{
			if (key != null && !key.HasDied && !key.HasDisposed && key.NotifyHungerChange() && key.Stats.GetStat(StatType.Hunger).Current < 0f)
			{
				num++;
			}
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["AnimalsHungryWarning"], num > 0);
	}

	private void OnRemoveWorker(HumanoidInstance humanoidInstance)
	{
		foreach (string key in statsByEffectorWarning.Keys)
		{
			if (statsByEffectorWarning[key].RemoveAll((StatsInstance stat) => stat.Owner == null || stat.Owner == humanoidInstance || stat.Owner.HasDisposed) > 0)
			{
				RefreshEffectorMessageVisible(key);
			}
		}
		RefreshWorkerWoundsMessage();
		RefreshBedsMessage();
		RefreshUnarmedVillagersMessage();
	}

	private void OnWorkersChanged(HumanoidInstance humanoidInstance)
	{
		if (MonoSingleton<UIController>.Instance.GameStarted)
		{
			RefreshBedsMessage();
			RefreshUnarmedVillagersMessage();
		}
	}

	private void OnRaidStart(ActiveRaidInfo info, List<HumanoidInstance> NPCs)
	{
		RefreshUnarmedVillagersMessage();
	}

	private void OnRaidEnd(ActiveRaidInfo info)
	{
		MonoSingleton<TaskController>.Instance.WaitFor(1f).Then(RefreshUnarmedVillagersMessage);
	}

	private void OnFactionFriendlinessChanged(FactionFriendliness friendliness, FactionInstance factionInstance)
	{
		if (friendliness == FactionFriendliness.Hostile || friendliness == FactionFriendliness.PermanentlyHostile)
		{
			RefreshRaidWarningMessage();
		}
	}

	private void OnCaravanStateChanged(CaravanInstance caravanInstance, CaravanState caravanState)
	{
		RefreshCaravanArrivedMessage();
	}

	private void RefreshCaravanArrivedMessage()
	{
		bool visible = GlobalSaveController.CurrentVillageData.WorldMapData.Caravans.Any((CaravanInstance caravan) => caravan.CaravanState == CaravanState.Arrived);
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["CaravanArrivedWarning"], visible);
	}

	private void RefreshCropBlightMessage()
	{
		bool visible = CropBlightManager.IsBlightActive();
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["CropBlightEventWarning"], visible);
	}

	private void OnMapLoaded(bool fromSave)
	{
		RefreshHunterMessage();
		OnAvailableResourcesChanged(null, null);
		RefreshStockpileMessage();
		RefreshMealProductionMessage();
		RefreshResearchBenchMessage();
		RefreshRecreationMessage();
		RefreshDefensiveStructureMessage();
		RefreshCaravanArrivedMessage();
		RefreshCropBlightMessage();
	}

	private void OnRoleViewShown()
	{
		RefreshRolePossibleMessage();
		if (MonoSingleton<RoleManager>.IsInstantiated())
		{
			MonoSingleton<RoleManager>.Instance.RoleViewShownEvent -= OnRoleViewShown;
		}
	}

	private void OnEventViewShown()
	{
		RefreshEventPossibleMessage();
	}

	private void OnRoomRemoved(Room obj)
	{
		RefreshEventPossibleMessage();
		RefreshPrisonCellMissingMessage();
	}

	private void OnRoomChanged(Room room, RoomType roomType)
	{
		RefreshEventPossibleMessage();
		RefreshPrisonCellMissingMessage();
		RefreshRoomCreatedMessage(room);
	}

	private void OnCropBlightEnded()
	{
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["CropBlightEventWarning"], visible: false);
	}

	private void OnCropBlightStarted()
	{
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["CropBlightEventWarning"], visible: true);
	}

	private void OnCaravanEnded(CaravanInstance caravaninstance)
	{
		RefreshHunterMessage();
	}

	private void OnCaravanStarted(CaravanInstance caravaninstance)
	{
		RefreshHunterMessage();
	}

	private void OnLastFirePutOut()
	{
		RefreshFireMessage(hasFire: false);
	}

	private void OnFirstFireLit()
	{
		RefreshFireMessage(hasFire: true);
	}

	private static bool IsExitingOrLoading()
	{
		if (MonoSingleton<WorkerManager>.IsInstantiated() && MonoSingleton<ResourcePileManager>.IsInstantiated() && MonoSingleton<WarningMessageController>.IsInstantiated() && !MonoSingleton<WorkerManager>.IsApplicationQuitting() && MonoSingleton<ResearchManager>.IsInstantiated() && MonoSingleton<GlobalSaveController>.IsInstantiated())
		{
			return GlobalSaveController.CurrentVillageData == null;
		}
		return true;
	}

	private void RefreshLowFoodMessage()
	{
		if (IsExitingOrLoading())
		{
			return;
		}
		int storedCategoryAmount = MonoSingleton<ResourcePileManager>.Instance.GetStoredCategoryAmount(ResourceCategory.CtgEdible);
		bool flag = CurrentVillage.Workers.Count > 0 && storedCategoryAmount / CurrentVillage.Workers.Count < 300;
		if (flag)
		{
			flag = TimeToShow(5L);
			if (!flag)
			{
				scheduledMessages[5] = RefreshLowFoodMessage;
			}
		}
		bool isEnabled;
		FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(24, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("RefreshLowFoodMessage: ");
			messageBuilder.AppendFormatted(storedCategoryAmount);
			messageBuilder.AppendLiteral(" ");
			messageBuilder.AppendFormatted(flag);
		}
		Log.Debug(messageBuilder);
		MonoSingleton<AchievementManager>.Instance.SetStat("NUTR_CNT", storedCategoryAmount);
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["WarningLowFood"], flag);
	}

	private void RefreshStockpileMessage()
	{
		if (IsExitingOrLoading() || !MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
		{
			return;
		}
		stockpileCount = VillageManager.ActiveVillage.Map.GetObjectCount(GridDataType.Stockpile);
		bool flag = stockpileCount < 1;
		if (flag)
		{
			flag = TimeToShow(1L);
			if (!flag)
			{
				scheduledMessages[1] = RefreshStockpileMessage;
			}
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["WarningStockpile"], flag);
	}

	private void RefreshDefensiveStructureMessage(bool recalculateCount = true)
	{
		if (IsExitingOrLoading())
		{
			return;
		}
		if (recalculateCount)
		{
			if (!MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
			{
				return;
			}
			int num = 0;
			if (VillageManager.ActiveVillage.Map.GetObjectCount(GridDataType.BuildingFinished) > 0)
			{
				num = VillageManager.ActiveVillage.Map.GetWorldObjects(GridDataType.BuildingFinished).Distinct().Count((WorldObject worldObject) => worldObject is BaseBuildingInstance baseBuildingInstance2 && baseBuildingInstance2.BuildingType.HasFlag(BuildingType.Merlon));
			}
			int num2 = 0;
			if (VillageManager.ActiveVillage.Map.GetObjectCount(GridDataType.Trap) > 0)
			{
				num2 = VillageManager.ActiveVillage.Map.GetWorldObjects(GridDataType.Trap).Distinct().Count((WorldObject worldObject) => worldObject is BaseBuildingInstance baseBuildingInstance && baseBuildingInstance.BuildingType.HasFlag(BuildingType.Trap));
			}
			defensesCount = num2 + num;
		}
		bool flag = defensesCount < 1;
		if (flag)
		{
			flag = TimeToShow(77L);
			if (!flag)
			{
				scheduledMessages[77] = delegate
				{
					RefreshDefensiveStructureMessage();
				};
			}
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["WarningDefensiveStructure"], flag);
	}

	private void RefreshUnarmedVillagersMessage()
	{
		if (IsExitingOrLoading() || !MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
		{
			return;
		}
		bool flag = GlobalSaveController.CurrentVillageData.Raids.Count != 0 && CurrentVillage.Workers.Any((HumanoidInstance worker) => !worker.HasDied && !worker.HasDisposed && !worker.HasWeapon());
		if (flag)
		{
			flag = GlobalSaveController.CurrentVillageData.Raids.Any((ActiveRaidInfo raidInfo) => !raidInfo.HasEnded);
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["WarningUnarmedVillagers"], flag);
	}

	private void RefreshRolePossibleMessage()
	{
		if (IsExitingOrLoading() || !MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
		{
			return;
		}
		if (CurrentVillage.RolesSaveData.ViewShown || CurrentVillage.RolesSaveData.AnyRoleAssigned)
		{
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["RolePossibleWarning"], visible: false);
			return;
		}
		bool flag = CurrentVillage.Workers.Where((HumanoidInstance workerInstance) => Repository<RoleRepository, Role>.Instance.GetAllItems().Any(workerInstance.WorkerBehaviour.HumanoidRoleOwner.CanRoleBeLeveledUp)).Any();
		if (flag)
		{
			flag = TimeToShow(3L);
			if (!flag)
			{
				scheduledMessages[3] = delegate
				{
					RefreshDefensiveStructureMessage();
				};
			}
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["RolePossibleWarning"], flag);
	}

	private void RefreshMissingWardenRecruitMessage()
	{
		if (IsExitingOrLoading() || !MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
		{
			return;
		}
		bool flag = false;
		foreach (HumanoidInstance item in MonoSingleton<NPCManager>.Instance.IterateNPCs())
		{
			CaptiveNpcBehaviour captiveNpcBehaviour = item.CaptiveNpcBehaviour;
			if (captiveNpcBehaviour != null && captiveNpcBehaviour.MarkedForRecruiting)
			{
				flag = true;
			}
		}
		HumanoidInstance humanoidInstance;
		bool visible = flag && !MonoSingleton<RoleManager>.Instance.HasWardenRole(out humanoidInstance);
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["WardenMissingRecruitWarning"], visible);
	}

	private void RefreshRoleHourMessage()
	{
		if (!IsExitingOrLoading() && MonoSingleton<GlobalSaveController>.IsInstantiated() && GlobalSaveController.CurrentVillageData != null)
		{
			bool visible = CurrentVillage.Workers.Any((HumanoidInstance worker) => worker.WorkerBehaviour.HasRoleSchedule && !worker.ActiveBehaviour.HumanoidRoleOwner.AssignedRole);
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["RoleWorkHourWarning"], visible);
		}
	}

	private void RefreshRoleHourMissingMessage()
	{
		if (!IsExitingOrLoading() && MonoSingleton<GlobalSaveController>.IsInstantiated() && GlobalSaveController.CurrentVillageData != null)
		{
			bool visible = CurrentVillage.Workers.Any((HumanoidInstance worker) => worker.ActiveBehaviour.HumanoidRoleOwner.AssignedRole && !worker.WorkerBehaviour.HasRoleSchedule && !worker.WorkerBehaviour.HumanoidRoleOwner.RoleInstance.Blueprint.RoleHoursWaningSkip);
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["RoleHourMissingWarning"], visible);
		}
	}

	private void RefreshTraderWarningMessage()
	{
		if (!IsExitingOrLoading() && CurrentVillage != null)
		{
			bool visible = !CurrentVillage.NPCs.Any((HumanoidInstance npc) => npc.IsTrader());
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["TraderWarning"], visible);
		}
	}

	private void RefreshVisitorWarningMessage()
	{
		if (!IsExitingOrLoading() && CurrentVillage != null)
		{
			bool visible = !CurrentVillage.NPCs.Any((HumanoidInstance npc) => npc.ActiveBehaviour.HumanoidRoleOwner.AssignedRole);
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["RoleVisitorWarning"], visible);
		}
	}

	private void RefreshWorkerWoundsMessage()
	{
		if (!IsExitingOrLoading())
		{
			bool visible = GlobalSaveController.CurrentVillageData.Workers.Any((HumanoidInstance worker) => worker.HasUntendendWounds());
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["NeedTendingWarning"], visible);
		}
	}

	private void RefreshWinterClothesMessage()
	{
		if (IsExitingOrLoading())
		{
			return;
		}
		Season season = GlobalSaveController.CurrentVillageData.DateAndTime.Season;
		if (season.Name != "autumn" || season.Name != "winter")
		{
			return;
		}
		totalWinterClothesCount = MonoSingleton<ResourcePileManager>.Instance.CountPiles((ResourcePileInstance pile) => pile.IsStoredOnStockpile() && pile.Blueprint.Category.HasFlag(ResourceCategory.CtgItem) && pile.Blueprint.GroupIdentifier == "winter_clothes");
		int num = 0;
		foreach (HumanoidInstance worker in CurrentVillage.Workers)
		{
			EquipmentInstance equipmentInstance = worker.GetEquipment().Find((EquipmentInstance item) => item.Blueprint.ItemType == ItemType.Garment && (item.Blueprint.EquipmentSlots & EquipmentSlotType.Body) != 0);
			if (equipmentInstance != null && equipmentInstance.Blueprint.WarmthModifier.Min < 0f)
			{
				num++;
			}
		}
		bool visible = totalWinterClothesCount + num < CurrentVillage.Workers.Count;
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["WarningWinterClothes"], visible);
	}

	private void RefreshBedsMessage(bool recalculateCount = true)
	{
		if (IsExitingOrLoading())
		{
			return;
		}
		if (recalculateCount)
		{
			if (!MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
			{
				return;
			}
			bedsCount = VillageManager.ActiveVillage.Map.BedComponentManager.GetBeds(BedType.Basic).Count;
		}
		bool flag = bedsCount < CurrentVillage.Workers.Count;
		if (flag)
		{
			flag = TimeToShow(3L);
			if (!flag)
			{
				scheduledMessages[3] = delegate
				{
					RefreshBedsMessage();
				};
			}
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["WarningBeds"], flag);
	}

	private void RefreshDestroyOnPassThroughMessage()
	{
		if (!IsExitingOrLoading())
		{
			bool isEnabled;
			FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(46, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Refreshing Passed Through message. Positions: ");
				messageBuilder.AppendFormatted(passThoughDestroyedPositions.Count);
			}
			Log.Debug(messageBuilder);
			bool visible = passThoughDestroyedPositions.Count > 0;
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["PassThroughDestroyedWarning"], visible);
		}
	}

	private void RefreshRoomCreatedMessage(Room room)
	{
		FVLogTraceInterpolationHandler messageBuilder = new FVLogTraceInterpolationHandler(43, 1, out var isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Refreshing Room created message. RoomType: ");
			messageBuilder.AppendFormatted(room.RoomType.GetID());
		}
		Log.Trace(messageBuilder);
		if (IsExitingOrLoading() || !MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
		{
			return;
		}
		if (GlobalSaveController.CurrentVillageData.ShownRoomBuildWarnings.Contains(room.RoomType.GetID()))
		{
			messageBuilder = new FVLogTraceInterpolationHandler(33, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("ShownRoomBuildWarnings.Contains: ");
				messageBuilder.AppendFormatted(room.RoomType.GetID());
			}
			Log.Trace(messageBuilder);
			return;
		}
		if (!roomCreatedPositions.ContainsKey(room.RoomType.GetID()))
		{
			messageBuilder = new FVLogTraceInterpolationHandler(40, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("roomCreatedPositions ");
				messageBuilder.AppendFormatted(roomCreatedPositions.Keys.Count);
				messageBuilder.AppendLiteral(" does not contain: ");
				messageBuilder.AppendFormatted(room.RoomType.GetID());
			}
			Log.Trace(messageBuilder);
			return;
		}
		roomCreatedPositions[room.RoomType.GetID()].Add(room.Center);
		FVLogDebugInterpolationHandler messageBuilder2 = new FVLogDebugInterpolationHandler(44, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
		if (isEnabled)
		{
			messageBuilder2.AppendLiteral("Refreshing Room created message. Positions: ");
			messageBuilder2.AppendFormatted(roomCreatedPositions[room.RoomType.GetID()].Count);
		}
		Log.Debug(messageBuilder2);
		bool visible = roomCreatedPositions.Count > 0;
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages[room.RoomType.GetID()], visible);
	}

	private void RefreshMealProductionMessage(bool recalculateCount = true)
	{
		if (IsExitingOrLoading())
		{
			return;
		}
		if (recalculateCount)
		{
			if (!MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
			{
				return;
			}
			if (VillageManager.ActiveVillage.Map.GetObjectCount(GridDataType.ProductionBuilding) > 0)
			{
				foodProductionCount = VillageManager.ActiveVillage.Map.ProductionComponentBuildingManager.ComponentInstances.Distinct().Count((ProductionComponentInstance productionComponentInstance) => productionComponentInstance.BaseBuildingBlueprint.GetID() == "camp_fire" || productionComponentInstance.BaseBuildingBlueprint.GetID() == "limestone_stove" || productionComponentInstance.BaseBuildingBlueprint.GetID() == "limestone_block_stove" || productionComponentInstance.BaseBuildingBlueprint.GetID() == "clay_brick_stove");
			}
		}
		bool flag = foodProductionCount < 1;
		if (flag)
		{
			flag = TimeToShow(24L);
			if (!flag)
			{
				scheduledMessages[24] = delegate
				{
					RefreshMealProductionMessage();
				};
			}
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["WarningMealProduction"], flag);
	}

	private void RefreshHunterMessage()
	{
		if (IsExitingOrLoading())
		{
			return;
		}
		if (!MonoSingleton<AnimalManager>.Instance.HasAnimalWithOrder(AnimalOrderType.Hunt))
		{
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["HunterMissingWeaponWarning"], visible: false);
			return;
		}
		bool flag = true;
		bool flag2 = false;
		foreach (HumanoidInstance key in MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys)
		{
			if (!key.IsInIncognitoMode() && (key.WorkerBehaviour.ActiveJobCombination & JobType.Hunting) != 0)
			{
				flag2 = true;
				if (CombatUtils.HasRangedWeapon(key))
				{
					flag = false;
					break;
				}
			}
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["HunterMissingWeaponWarning"], flag2 && flag);
	}

	private void RefreshRecreationMessage(bool recalculateCount = true)
	{
		if (IsExitingOrLoading())
		{
			return;
		}
		if (recalculateCount)
		{
			if (!MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
			{
				return;
			}
			if (VillageManager.ActiveVillage.Map.GetObjectCount(GridDataType.Furniture) > 0)
			{
				int componentCount = VillageManager.ActiveVillage.Map.ShrineComponentManager.ComponentCount;
				int componentCount2 = VillageManager.ActiveVillage.Map.EntertainmentComponentManager.ComponentCount;
				recreationBuildingCount = componentCount + componentCount2;
			}
		}
		bool flag = recreationBuildingCount < 1;
		if (flag)
		{
			flag = TimeToShow(48L);
			if (!flag)
			{
				scheduledMessages[48] = delegate
				{
					RefreshRecreationMessage();
				};
			}
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["WarningRecreation"], flag);
	}

	private void RefreshResearchBenchMessage(bool recalculateCount = true)
	{
		if (IsExitingOrLoading())
		{
			return;
		}
		if (recalculateCount)
		{
			if (!MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
			{
				return;
			}
			if (VillageManager.ActiveVillage.Map.GetObjectCount(GridDataType.ProductionBuilding) > 0)
			{
				researchBenchCount = VillageManager.ActiveVillage.Map.GetObjectCount(GridDataType.ProductionBuilding, (WorldObject item) => BuildingUtils.GetResearchBuildings.Contains(item.BlueprintId));
				if (researchBenchCount > 0)
				{
					GlobalSaveController.CurrentVillageData.ResearchTableBuilt = true;
				}
			}
		}
		bool flag = researchBenchCount < 1;
		if (flag)
		{
			flag = TimeToShow(30L);
			if (!flag)
			{
				scheduledMessages[30] = delegate
				{
					RefreshResearchBenchMessage();
				};
			}
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["WarningResearchBench"], flag);
	}

	private void RefreshEventPossibleMessage()
	{
		if (IsExitingOrLoading())
		{
			return;
		}
		VillageMap map = VillageManager.ActiveVillage.Map;
		bool isEnabled;
		if (!MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null || map.GetObjectCount(GridDataType.Furniture) == 0)
		{
			FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(18, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Furniture on map: ");
				messageBuilder.AppendFormatted(map.GetObjectCount(GridDataType.Furniture));
			}
			Log.Debug(messageBuilder);
			return;
		}
		using PooledList<BaseBuildingInstance> pooledList = BuildingUtils.GetPossibleEventHolders();
		bool flag = pooledList.Count > 0;
		bool flag2 = TimeToShow(2L);
		bool flag3 = flag && flag2;
		if (!flag2)
		{
			scheduledMessages[2] = RefreshEventPossibleMessage;
		}
		FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(47, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Show warning: ");
			messageBuilder.AppendFormatted(flag3);
			messageBuilder.AppendLiteral(".  Furniture: ");
			messageBuilder.AppendFormatted(flag);
			messageBuilder.AppendLiteral(", Is time to show: ");
			messageBuilder.AppendFormatted(flag2);
		}
		Log.Debug(messageBuilder);
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["EventPossibleWarning"], flag3);
	}

	private void RefreshFireMessage(bool hasFire)
	{
		if (!IsExitingOrLoading())
		{
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages["FireWarning"], hasFire);
		}
	}

	private void RefreshAvailableResearchMessage()
	{
		if (IsExitingOrLoading() || !GlobalSaveController.CurrentVillageData.ResearchTableBuilt || !MonoSingleton<ResearchManager>.IsInstantiated() || !MonoSingleton<WarningMessageController>.IsInstantiated())
		{
			return;
		}
		bool flag = unlockableResearchCount != MonoSingleton<ResearchManager>.Instance.GetUnlockableNodesCount();
		unlockableResearchCount = MonoSingleton<ResearchManager>.Instance.GetUnlockableNodesCount();
		bool flag2 = unlockableResearchCount > 0;
		if (flag2)
		{
			flag2 = TimeToShow(30L);
			if (!flag2)
			{
				scheduledMessages[30] = RefreshAvailableResearchMessage;
				flag = false;
			}
		}
		if (flag && flag2)
		{
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["WarningAvailableResearch"], visible: false);
		}
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(objectiveWarningMessages["WarningAvailableResearch"], flag2);
	}

	private void InitEffectorMessages()
	{
		effectorWarningMessages.Clear();
		string[] array = new string[11]
		{
			"Bleeding", "Cold", "CriticalCondition", "Hunger", "MoodLow", "Sleep", "Warmth", "MoodLowRevolt", "Unconscious", "MoodAnnoyed",
			"Suffocating"
		};
		foreach (string text in array)
		{
			WarningMessageData value = new WarningMessageData(WarningMessageCategory.Warning, "warning_message_short_" + text, "warning_message_info_" + text, text, OnEffectorWarningClick, OnEffectorWarningProcessTooltip, null, showInPlayerVillageOnly: false);
			effectorWarningMessages.Add(text, value);
		}
	}

	private void RefreshEffectorMessageVisible(string messageName)
	{
		bool visible = statsByEffectorWarning[messageName].Count > 0;
		WarningMessageData effectorMessage = GetEffectorMessage(messageName);
		if (effectorMessage != null)
		{
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(effectorMessage, visible);
		}
	}

	private void OnAvailableResearchWarningClick(WarningMessageData obj)
	{
		MonoSingleton<UIController>.Instance.LeftPanelView.SceneUIManager.TogglePanel("ResearchPanelManager");
	}

	private void OnStockpileWarningClick(WarningMessageData data)
	{
		MonoSingleton<UIController>.Instance.ToggleConstructionCategory(BuildingCategoryUI.Zone, BuildingSubCategoryUI.SubCtgStockpiles.ToString());
	}

	private void OnDefensiveStructureWarningClick(WarningMessageData data)
	{
		MonoSingleton<UIController>.Instance.ToggleConstructionCategory(BuildingCategoryUI.Defense, BuildingSubCategoryUI.SubCtgMerlon.ToString(), BuildingSubCategoryUI.SubCtgTrapWeak.ToString());
	}

	private void OnBedsWarningClick(WarningMessageData obj)
	{
		MonoSingleton<UIController>.Instance.ToggleConstructionCategory(BuildingCategoryUI.Furniture, BuildingSubCategoryUI.SubCtgBed.ToString(), BuildingSubCategoryUI.SubCtgSleepingSpot.ToString(), BuildingSubCategoryUI.SubCtgBed.ToString());
	}

	private void OnPassThroughDestroyedClick(WarningMessageData obj)
	{
		Vector3 worldPosition = GridUtils.GetWorldPosition(passThoughDestroyedPositions[warningTmpClickCounter++ % passThoughDestroyedPositions.Count]);
		MonoSingleton<RtsCamera>.Instance.JumpTo(worldPosition);
	}

	private void OnPassThroughDestroyCloseClick(WarningMessageData obj)
	{
		passThoughDestroyedPositions.Clear();
		RefreshDestroyOnPassThroughMessage();
	}

	private void OnRoomCreatedClick(WarningMessageData obj)
	{
		bool isEnabled;
		FVLogTraceInterpolationHandler messageBuilder = new FVLogTraceInterpolationHandler(0, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
		if (isEnabled)
		{
			messageBuilder.AppendFormatted(obj.ID);
		}
		Log.Trace(messageBuilder);
		List<Vec3Int> list = roomCreatedPositions[obj.ID];
		if (list == null || list.Count <= 0)
		{
			FVLogErrorInterpolationHandler messageBuilder2 = new FVLogErrorInterpolationHandler(42, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
			if (isEnabled)
			{
				messageBuilder2.AppendLiteral("No positions for ");
				messageBuilder2.AppendFormatted(obj.ID);
				messageBuilder2.AppendLiteral(" in roomCreatedPositions.");
			}
			Log.Error(messageBuilder2);
		}
		else
		{
			Vector3 worldPosition = GridUtils.GetWorldPosition(list[warningTmpClickCounter++ % list.Count]);
			MonoSingleton<RtsCamera>.Instance.JumpTo(worldPosition);
		}
	}

	private void OnRoomCreatedCloseClick(WarningMessageData obj)
	{
		roomCreatedPositions.Remove(obj.ID);
		MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(generalWarningMessages[obj.ID], visible: false);
		if (MonoSingleton<GlobalSaveController>.IsInstantiated() && GlobalSaveController.CurrentVillageData != null)
		{
			GlobalSaveController.CurrentVillageData.AddToShownRoomBuildWarnings(obj.ID);
		}
	}

	private void OnRecreationWarningClick(WarningMessageData obj)
	{
		MonoSingleton<UIController>.Instance.ToggleConstructionCategory(BuildingCategoryUI.Leisure, BuildingSubCategoryUI.SubCtgChristianShrine.ToString(), BuildingSubCategoryUI.SubCtgPaganShrine.ToString(), BuildingSubCategoryUI.SubCtgBackgammon.ToString());
	}

	private void OnMealProductionWarningClick(WarningMessageData obj)
	{
		MonoSingleton<UIController>.Instance.ToggleConstructionCategory(BuildingCategoryUI.Production, "camp_fire", "SubCtgStove");
	}

	private void OnResearchBenchWarningClick(WarningMessageData obj)
	{
		MonoSingleton<UIController>.Instance.ToggleConstructionCategory(BuildingCategoryUI.Production, BuildingUtils.GetResearchBuildings);
	}

	private void OnEventPossibleClick(WarningMessageData obj)
	{
		using PooledList<BaseBuildingInstance> pooledList = BuildingUtils.GetPossibleEventHolders();
		if (!pooledList.Any())
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(41, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Selected buildables count is zero. For: ");
				messageBuilder.AppendFormatted(obj.Text);
				messageBuilder.AppendLiteral(".");
			}
			Log.Error(messageBuilder);
			RefreshEventPossibleMessage();
		}
		else
		{
			BaseBuildingInstance selectable = pooledList[warningTmpClickCounter++ % pooledList.Count];
			SelectAndCenterBuilding(selectable);
		}
	}

	private void SelectAndCenterBuilding(BaseBuildingInstance selectable)
	{
		if (selectable != null)
		{
			MonoSingleton<RtsCamera>.Instance.JumpTo(selectable.GetPosition());
			MonoSingleton<SelectableObjectManager>.Instance.DeselectAll();
			selectable.SelectBuilding();
		}
	}

	private void OnLowFoodWarningClick(WarningMessageData obj)
	{
		List<WorldObject> worldObjectsList = VillageManager.ActiveVillage.Map.GetWorldObjectsList<WorldObject>(GridDataType.Stockpile, distinct: true);
		if (worldObjectsList.Count >= 1)
		{
			Vector3 worldPosition = worldObjectsList[warningTmpClickCounter++ % worldObjectsList.Count].WorldPosition;
			CenterCamera(worldPosition);
		}
	}

	private void OnWinterClothesWarningClick(WarningMessageData obj)
	{
		using PooledList<HumanoidInstance> pooledList = ListPool<HumanoidInstance>.GetJanitor();
		foreach (HumanoidInstance worker in CurrentVillage.Workers)
		{
			EquipmentInstance equipmentInstance = worker.GetEquipment().Find((EquipmentInstance item) => item.Blueprint.ItemType == ItemType.Garment && (item.Blueprint.EquipmentSlots & EquipmentSlotType.Body) != 0);
			if (equipmentInstance == null || equipmentInstance.Blueprint.WarmthModifier.Min > 0f)
			{
				pooledList.Add(worker);
			}
		}
		if (pooledList.Count >= 1)
		{
			HumanoidInstance humanoid = pooledList[warningTmpClickCounter++ % pooledList.Count];
			SelectAndCenterWorker(humanoid, 1);
		}
	}

	private void OnUnarmedVillagersWarningClick(WarningMessageData obj)
	{
		using PooledList<HumanoidInstance> pooledList = ListPool<HumanoidInstance>.GetJanitor();
		foreach (HumanoidInstance worker in CurrentVillage.Workers)
		{
			if (!worker.HasWeapon())
			{
				pooledList.Add(worker);
			}
		}
		if (pooledList.Count >= 1)
		{
			HumanoidInstance humanoid = pooledList[warningTmpClickCounter++ % pooledList.Count];
			SelectAndCenterWorker(humanoid, 1);
		}
	}

	private void OnRolePossibleClick(WarningMessageData obj)
	{
		List<HumanoidInstance> list = CurrentVillage.Workers.Where((HumanoidInstance workerInstance) => Repository<RoleRepository, Role>.Instance.GetAllItems().Any(workerInstance.WorkerBehaviour.HumanoidRoleOwner.CanRoleBeLeveledUp)).ToList();
		if (list.Count >= 1)
		{
			HumanoidInstance humanoid = list[warningTmpClickCounter++ % list.Count];
			SelectAndCenterWorker(humanoid, 0);
		}
	}

	private void OnRoleWorkHourClick(WarningMessageData obj)
	{
		List<HumanoidInstance> list = CurrentVillage.Workers.Where((HumanoidInstance worker) => worker.WorkerBehaviour.HasRoleSchedule && !worker.ActiveBehaviour.HumanoidRoleOwner.AssignedRole).ToList();
		if (list.Count >= 1)
		{
			HumanoidInstance humanoid = list[warningTmpClickCounter++ % list.Count];
			SelectAndCenterWorker(humanoid, 0);
			MonoSingleton<UIController>.Instance.LeftPanelView.SceneUIManager.SetPanelOpen("SchedulePanelManager");
		}
	}

	private void OnRoleWorkHourMissingClick(WarningMessageData obj)
	{
		List<HumanoidInstance> list = CurrentVillage.Workers.Where((HumanoidInstance worker) => !worker.WorkerBehaviour.HasRoleSchedule && worker.ActiveBehaviour.HumanoidRoleOwner.AssignedRole && !worker.ActiveBehaviour.HumanoidRoleOwner.RoleInstance.Blueprint.RoleHoursWaningSkip).ToList();
		if (list.Count >= 1)
		{
			HumanoidInstance humanoid = list[warningTmpClickCounter++ % list.Count];
			SelectAndCenterWorker(humanoid, 0);
			MonoSingleton<UIController>.Instance.LeftPanelView.SceneUIManager.SetPanelOpen("SchedulePanelManager");
		}
	}

	private void OnRoomCreatedProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		if (string.IsNullOrEmpty(data.ID))
		{
			return;
		}
		RoomType byID = Repository<RoomTypeRepository, RoomType>.Instance.GetByID(data.ID);
		if ((object)byID != null)
		{
			string value = tooltipLines[0].Replace("<room_type>", RoomUtils.GetLocalizedName(data.ID));
			tooltipLines[0] = value;
			tooltipLines.Add("\n");
			tooltipLines.Add(("room_effect_" + byID.GetID()).ToLocalized().ToStyled(TooltipStyles.TooltipAttribute));
			if (RoomUtils.GetPlayerTriggeredEventsInfo(byID, out var line))
			{
				tooltipLines.Add(line);
			}
			if (RoomUtils.GetRoleInfo(byID, out var line2))
			{
				tooltipLines.Add(line2);
			}
		}
	}

	private void OnVisitorProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		foreach (HumanoidInstance nPC in CurrentVillage.NPCs)
		{
			if (nPC.ActiveBehaviour is RoleVisitorBehaviour roleVisitorBehaviour && nPC.ActiveBehaviour.HumanoidRoleOwner.AssignedRole)
			{
				HumanoidInstance humanoid = roleVisitorBehaviour.Humanoid;
				string line = MonoSingleton<LocalizationController>.Instance.GetText(LocKeyUtils.GetName(humanoid.ActiveBehaviour.HumanoidRoleOwner.RoleInstance.Blueprint.LocKeys), humanoid.Info.BodyType) + ": " + nPC.Info.GetFullName();
				tooltipLines.Add(TooltipStyles.ApplyStyle(line, TooltipStyles.TooltipDescriptionLine));
			}
		}
		tooltipLines.Add(ClickInfoText());
	}

	private void OnTraderProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		foreach (HumanoidInstance nPC in CurrentVillage.NPCs)
		{
			if (nPC.ActiveBehaviour is TraderBehaviour traderBehaviour)
			{
				string line = MonoSingleton<LocalizationController>.Instance.GetText(LocKeyUtils.GetName(traderBehaviour.TraderType.LocKeys)) + ": " + nPC.Info.GetFullName();
				tooltipLines.Add(TooltipStyles.ApplyStyle(line, TooltipStyles.TooltipDescriptionLine));
			}
		}
		tooltipLines.Add(ClickInfoText());
	}

	private void OnBeggarProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		foreach (HumanoidInstance nPC in CurrentVillage.NPCs)
		{
			if (nPC.IsBeggar())
			{
				tooltipLines.Add(TooltipStyles.ApplyStyle(MonoSingleton<LocalizationController>.Instance.GetText(nPC.GetGoapAgentID()) + " " + nPC.Info.GetFullName(), TooltipStyles.TooltipDescriptionLine));
			}
		}
		tooltipLines.Add(ClickInfoText());
	}

	private void OnVisitorClick(WarningMessageData data)
	{
		List<HumanoidInstance> list = CurrentVillage.NPCs.Where((HumanoidInstance npcInstance) => npcInstance.IsVisitor()).ToList();
		if (list.Count > 0)
		{
			OnNPCsCLick(list);
		}
	}

	private void OnTraderClick(WarningMessageData data)
	{
		List<HumanoidInstance> list = CurrentVillage.NPCs.Where((HumanoidInstance npcInstance) => npcInstance.IsTrader()).ToList();
		if (list.Count > 0)
		{
			OnNPCsCLick(list);
		}
	}

	private void OnBeggarClick(WarningMessageData data)
	{
		using PooledList<HumanoidInstance> pooledList = CurrentVillage.NPCs.WherePooled((HumanoidInstance npcInstance) => npcInstance.IsBeggar() && string.IsNullOrEmpty(npcInstance.CustomWarningMessage));
		if (pooledList.Count > 0)
		{
			OnNPCsCLick(pooledList);
		}
	}

	private void OnCustomNpcWarningProcessTooltip(List<string> tooltipLines, WarningMessageData warningMessageData)
	{
		string text = generalWarningMessages.Keys.FirstOrDefault((string key) => generalWarningMessages[key] == warningMessageData);
		foreach (HumanoidInstance nPC in CurrentVillage.NPCs)
		{
			if (nPC.CustomWarningMessage == text)
			{
				tooltipLines.Add(TooltipStyles.ApplyStyle(nPC.Info.GetFullName() ?? "", TooltipStyles.TooltipDescriptionLine));
			}
		}
		tooltipLines.Add(ClickInfoText());
	}

	private void OnCustomNpcWarningClick(WarningMessageData warningMessageData)
	{
		string text = generalWarningMessages.Keys.FirstOrDefault((string key) => generalWarningMessages[key] == warningMessageData);
		using PooledList<HumanoidInstance> pooledList = ListPool<HumanoidInstance>.GetJanitor();
		foreach (HumanoidInstance nPC in CurrentVillage.NPCs)
		{
			if (nPC.CustomWarningMessage == text)
			{
				pooledList.Add(nPC);
			}
		}
		if (pooledList.Count > 0)
		{
			OnNPCsCLick(pooledList);
		}
	}

	private void OnMapMarkerClick(WarningMessageData data)
	{
		if (!MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.MarkerManager.HasVisibleMarkers)
		{
			return;
		}
		using PooledList<WorldMapMarkerPlace> pooledList = ListPool<WorldMapMarkerPlace>.GetJanitor(MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.MarkerManager.VisibleMarkers);
		MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.JumpToPlace(pooledList[warningTmpClickCounter++ % pooledList.Count]);
	}

	private void OnMapMarkerProcessTooltips(List<string> tooltipLines, WarningMessageData data)
	{
		tooltipLines.Add(MonoSingleton<LocalizationController>.Instance.GetText("warning_message_tooltip_map_markers_available"));
		foreach (WorldMapMarkerPlace visibleMarker in MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.MarkerManager.VisibleMarkers)
		{
			tooltipLines.Add(visibleMarker.Name);
		}
	}

	private void OnAmbushClick(WarningMessageData data)
	{
		using PooledList<CaravanInstance> pooledList = MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.Data.Caravans.WherePooled((CaravanInstance caravan) => caravan.EventContext is AmbushContext);
		if (pooledList.Count > 0)
		{
			MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.JumpToCaravan(pooledList[warningTmpClickCounter++ % pooledList.Count]);
		}
	}

	private void OnAmbushProcessTooltips(List<string> tooltipLines, WarningMessageData data)
	{
		foreach (CaravanInstance caravan in MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.Data.Caravans)
		{
			if (!(caravan.EventContext is AmbushContext))
			{
				tooltipLines.Add(caravan.Name);
			}
		}
	}

	private void OnNPCsCLick(IList<HumanoidInstance> npcs)
	{
		HumanoidInstance humanoidInstance = npcs[warningTmpClickCounter++ % npcs.Count];
		MonoSingleton<RtsCamera>.Instance.JumpTo(humanoidInstance.GetPosition());
		NPCView view = MonoSingleton<NPCManager>.Instance.GetView(humanoidInstance);
		if (view != null)
		{
			MonoSingleton<SelectableObjectManager>.Instance.DeselectAll();
			view.Select();
		}
	}

	private void OnCaravanProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		foreach (CaravanInstance caravan in GlobalSaveController.CurrentVillageData.WorldMapData.Caravans)
		{
			if (caravan != null && caravan.CaravanState == CaravanState.Arrived && caravan.DestinationPlace != null)
			{
				tooltipLines.Add(TooltipStyles.ApplyStyle(caravan.DestinationPlace.Name, TooltipStyles.TooltipDescriptionLine));
			}
		}
		tooltipLines.Add(ClickInfoText());
	}

	private void OnCaravanClick(WarningMessageData obj)
	{
		CaravanInstance[] array = GlobalSaveController.CurrentVillageData.WorldMapData.Caravans.Where((CaravanInstance caravan) => caravan.CaravanState == CaravanState.Arrived).ToArray();
		if (array.Length != 0)
		{
			CaravanInstance caravanInstance = array[warningTmpClickCounter++ % array.Length];
			MonoSingleton<CaravanController>.Instance.SelectedCaravan(caravanInstance);
		}
	}

	private static bool IsNPCStuck(HumanoidInstance humanoidInstance)
	{
		if (humanoidInstance?.Faction == null || humanoidInstance.IsRaider() || humanoidInstance.HasDisposed || humanoidInstance.HasDied)
		{
			return false;
		}
		if ((humanoidInstance.CombatAi.IsStateSet(CombatAiState.EnemyIsRetreating) || humanoidInstance.IsLeaving) && !humanoidInstance.IsCaptive())
		{
			return humanoidInstance.CombatAi.IsStateSet(CombatAiState.StuckWhileRetreating);
		}
		return false;
	}

	private static bool IsAnimalStuck(AnimalInstance animal)
	{
		if (animal.PetOwner is HumanoidInstance humanoidInstance && humanoidInstance.IsTrader() && !humanoidInstance.HasDied && !humanoidInstance.HasDisposed && animal.CombatAi != null && animal.CombatAi.IsStateSet(CombatAiState.AnimalRetreatingNoPathToTrader))
		{
			return true;
		}
		return false;
	}

	private void OnTraderCantLeaveClick(WarningMessageData obj)
	{
		using PooledList<CreatureBase> pooledList = ListPool<CreatureBase>.GetJanitor();
		IEnumerable<HumanoidInstance> collection = GlobalSaveController.CurrentVillageData.NPCs.Where(IsNPCStuck);
		pooledList.AddRange(collection);
		foreach (HumanoidInstance nPC in GlobalSaveController.CurrentVillageData.NPCs)
		{
			if (nPC.Pets == null)
			{
				continue;
			}
			foreach (AnimalInstance pet in nPC.Pets)
			{
				if (!pet.HasDied && !pet.HasDisposed && pet.CombatAi != null && pet.CombatAi.IsStateSet(CombatAiState.AnimalRetreatingNoPathToTrader))
				{
					pooledList.Add(pet);
				}
			}
		}
		if (pooledList.Count != 0)
		{
			CreatureBase creatureBase = pooledList[warningTmpClickCounter++ % pooledList.Count];
			MonoSingleton<RtsCamera>.Instance.JumpTo(creatureBase.GetPosition());
			SelectableObject selectableObject = null;
			if (creatureBase is HumanoidInstance humanoidInstance && humanoidInstance.IsNpc())
			{
				selectableObject = MonoSingleton<NPCManager>.Instance.GetView(humanoidInstance);
			}
			if (creatureBase is AnimalInstance animalInstance)
			{
				selectableObject = MonoSingleton<AnimalManager>.Instance.GetView(animalInstance);
			}
			if (selectableObject != null)
			{
				MonoSingleton<SelectableObjectManager>.Instance.DeselectAll();
				selectableObject.Select();
			}
		}
	}

	private void OnCropBlightClick(WarningMessageData obj)
	{
		JumpToBlightedCrop();
	}

	public void JumpToBlightedCrop()
	{
		if (!CropBlightManager.IsBlightActive())
		{
			return;
		}
		CropBlightManager cropBlightManager = MonoSingleton<CropBlightManager>.Instance;
		List<WorldObject> list = (from kvp in VillageManager.ActiveVillage.Map.GetWorldObjects(GridDataType.PlantMapResource)
			where cropBlightManager.IsBlightAt(kvp.GridDataPosition)
			select kvp).ToList();
		if (list.Any() && list[warningTmpClickCounter++ % list.Count] is PlantMapResourceInstance plantMapResourceInstance)
		{
			PlantMapResourceView view = MonoSingleton<PlantResourceManager>.Instance.GetView(plantMapResourceInstance);
			if (view != null)
			{
				MonoSingleton<SelectableObjectManager>.Instance.DeselectAll();
				view.Select();
				MonoSingleton<RtsCamera>.Instance.JumpTo(plantMapResourceInstance.GetPosition());
			}
		}
	}

	private void OnVillagersIdleClick(WarningMessageData data)
	{
		List<HumanoidInstance> list = GlobalSaveController.CurrentVillageData.Workers.Where((HumanoidInstance worker) => worker.WorkerBehaviour.IsIdle && !worker.WorkerBehaviour.IsCrazy).ToList();
		if (list.Count > 0)
		{
			HumanoidInstance humanoid = list[warningTmpClickCounter++ % list.Count];
			SelectAndCenterWorker(humanoid);
		}
	}

	private void OnHunterMissingWeaponClick(WarningMessageData data)
	{
		if (MonoSingleton<AnimalManager>.Instance.HasAnimalWithOrder(AnimalOrderType.Hunt))
		{
			List<HumanoidInstance> list = MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys.Where(delegate(HumanoidInstance worker)
			{
				AttackType attackType = CombatUtils.GetAttackType(worker);
				bool num = (worker.WorkerBehaviour.ActiveJobCombination & JobType.Hunting) != 0;
				bool flag = attackType == AttackType.RangeChargeAfter || attackType == AttackType.RangeChargeBefore;
				return num && !flag;
			}).ToList();
			if (list.Count > 0)
			{
				HumanoidInstance humanoid = list[warningTmpClickCounter++ % list.Count];
				SelectAndCenterWorker(humanoid, 1);
			}
		}
	}

	private void OnNeedTendingClick(WarningMessageData obj)
	{
		List<HumanoidInstance> list = GlobalSaveController.CurrentVillageData.Workers.Where((HumanoidInstance worker) => worker.HasUntendendWounds()).ToList();
		if (list.Count > 0)
		{
			HumanoidInstance humanoid = list[warningTmpClickCounter++ % list.Count];
			SelectAndCenterWorker(humanoid, 6);
		}
	}

	private void OnRaidClick(WarningMessageData data)
	{
		using PooledList<HumanoidInstance> pooledList = ListPool<HumanoidInstance>.GetJanitor(CurrentVillage.NPCs.Where((HumanoidInstance item) => item.IsEnemy()));
		if (pooledList.Count > 0)
		{
			HumanoidInstance humanoidInstance = pooledList[warningTmpClickCounter++ % pooledList.Count];
			NPCView view = MonoSingleton<NPCManager>.Instance.GetView(humanoidInstance);
			if (!(view == null))
			{
				MonoSingleton<SelectableObjectManager>.Instance.DeselectAll();
				view.Select();
				MonoSingleton<RtsCamera>.Instance.JumpTo(humanoidInstance.GetPosition());
			}
		}
	}

	private void OnAggressiveAnimalsProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		tooltipLines.Add(ClickInfoText());
	}

	private void OnAnimalsHungryProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		tooltipLines.Add(string.Empty);
		if (!MonoSingleton<AnimalManager>.IsInstantiated())
		{
			return;
		}
		foreach (AnimalInstance item in MonoSingleton<AnimalManager>.Instance.Animals.Keys.Where((AnimalInstance animal) => animal != null && !animal.HasDied && !animal.HasDisposed && animal.NotifyHungerChange() && animal.Stats.GetStat(StatType.Hunger).Current < 0f).ToList())
		{
			tooltipLines.Add(item.GetFullName());
		}
		tooltipLines.Add(ClickInfoText());
	}

	private void OnAggressiveAnimalsClick(WarningMessageData data)
	{
		JumpToAggressiveAnimal();
	}

	public void JumpToAggressiveAnimal()
	{
		if (!MonoSingleton<AnimalManager>.IsInstantiated())
		{
			return;
		}
		List<AnimalInstance> list = MonoSingleton<AnimalManager>.Instance.Animals.Keys.Where((AnimalInstance animal) => animal != null && !animal.HasDied && !animal.HasDisposed && animal.AnimalType == AnimalType.WildAggressive).ToList();
		if (list.Count == 0)
		{
			RefreshAggressiveAnimalsMessage();
			return;
		}
		AnimalInstance animalInstance = list[warningTmpClickCounter++ % list.Count];
		if (animalInstance != null)
		{
			AnimalView animalView = MonoSingleton<AnimalManager>.Instance.Animals[animalInstance];
			if (animalView != null)
			{
				MonoSingleton<SelectableObjectManager>.Instance.DeselectAll();
				animalView.Select();
			}
			MonoSingleton<RtsCamera>.Instance.JumpTo(animalInstance.GetPosition());
		}
	}

	private void OnAnimalsHungryClick(WarningMessageData data)
	{
		if (!MonoSingleton<AnimalManager>.IsInstantiated())
		{
			return;
		}
		List<AnimalInstance> list = MonoSingleton<AnimalManager>.Instance.Animals.Keys.Where((AnimalInstance animal) => animal != null && !animal.HasDied && !animal.HasDisposed && animal.NotifyHungerChange() && animal.Stats.GetStat(StatType.Hunger).Current < 0f).ToList();
		if (list.Count == 0)
		{
			RefreshAggressiveAnimalsMessage();
			return;
		}
		AnimalInstance animalInstance = list[warningTmpClickCounter++ % list.Count];
		if (animalInstance != null)
		{
			AnimalView animalView = MonoSingleton<AnimalManager>.Instance.Animals[animalInstance];
			if (animalView != null)
			{
				MonoSingleton<SelectableObjectManager>.Instance.DeselectAll();
				animalView.Select();
			}
			MonoSingleton<RtsCamera>.Instance.JumpTo(animalInstance.GetPosition());
		}
	}

	private void OnEffectorWarningClick(WarningMessageData data)
	{
		string key = effectorWarningMessages.First((KeyValuePair<string, WarningMessageData> pair) => pair.Value == data).Key;
		int count = statsByEffectorWarning[key].Count;
		CreatureBase creatureBase = (CreatureBase)statsByEffectorWarning[key][warningTmpClickCounter++ % count].Owner;
		if (creatureBase != null)
		{
			SelectAndCenterWorker(creatureBase as HumanoidInstance, key switch
			{
				"Bleeding" => 6, 
				"CriticalCondition" => 6, 
				"Unconscious" => 6, 
				"MoodLow" => 5, 
				"MoodAnnoyed" => 5, 
				"MoodLowRevolt" => 5, 
				"Hunger" => 4, 
				"Sleep" => 4, 
				"Warmth" => 4, 
				_ => -1, 
			});
		}
	}

	private void OnRaidProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		tooltipLines.Add(string.Empty);
		PooledDictionary<string, int> janitor = DictionaryPool<string, int>.GetJanitor();
		try
		{
			foreach (HumanoidInstance nPC in CurrentVillage.NPCs)
			{
				if (!nPC.IsEnemy())
				{
					continue;
				}
				string text = LocKeyUtils.GetName(nPC.ActiveBehaviour.NpcBlueprint.LocKeys).ToLocalized(BodyType.None);
				if (!string.IsNullOrEmpty(text))
				{
					if (!janitor.ContainsKey(text))
					{
						janitor[text] = 0;
					}
					janitor[text]++;
				}
			}
			foreach (KeyValuePair<string, int> item in janitor)
			{
				tooltipLines.Add(TooltipStyles.ApplyStyle(TextFormatting.GetFormatedItemCount(item.Value, item.Key), TooltipStyles.TooltipDescriptionLine));
			}
			if (CurrentVillage.SiegeWeapons.Count > 0)
			{
				using PooledHashSet<string> pooledHashSet = HashSetPool<string>.GetJanitor();
				foreach (SiegeWeaponComponentInstance siegeWeapon in CurrentVillage.SiegeWeapons)
				{
					string text2 = MonoSingleton<LocalizationController>.Instance.GetText(LocKeyUtils.GetName(siegeWeapon.OwnerBuilding.Blueprint.LocKeys));
					pooledHashSet.Add(text2);
				}
				string line = string.Format("{0}: {1}", string.Join(",", pooledHashSet), CurrentVillage.SiegeWeapons.Count);
				tooltipLines.Add(TooltipStyles.ApplyStyle(line, TooltipStyles.TooltipDescriptionLine));
			}
			tooltipLines.Add(ClickInfoText());
		}
		finally
		{
			((IDisposable)janitor).Dispose();
		}
	}

	private void OnFireClick(WarningMessageData obj)
	{
		VillageManager.ActiveVillage.Map.FireSimLogic.NodesOnFireSafeOperation(JumpToRandomFire);
	}

	private static void JumpToRandomFire(NativeArray<int> nodeIndicesOnFire, int nodeIndIcesOnFireCount, NativeArray<float> flameData, NativeArray<byte> flameType)
	{
		Vector3 worldPosition = GridUtils.GetWorldPosition(GridDataIndexTools.FastTo3DIndex(nodeIndicesOnFire[UnityEngine.Random.Range(0, nodeIndIcesOnFireCount)]));
		MonoSingleton<RtsCamera>.Instance.JumpTo(worldPosition);
	}

	private void OnFireProcessTooltip(List<string> textlines, WarningMessageData warningmessagedata)
	{
	}

	private void OnEnemySiegeWeaponProcessTooltip(List<string> textlines, WarningMessageData warningmessagedata)
	{
	}

	private void OnEnemySiegeWeaponClick(WarningMessageData obj)
	{
		BaseBuildingInstance baseBuildingInstance = VillageManager.ActiveVillage.Map.EnemyBuildingsManager.SelectRandomSiegeWeapon();
		if (baseBuildingInstance != null)
		{
			MonoSingleton<RtsCamera>.Instance.JumpTo(baseBuildingInstance.WorldPosition);
		}
	}

	private static string ClickInfoText()
	{
		return TooltipStyles.ApplyStyle("\n" + MonoSingleton<LocalizationController>.Instance.GetText("menu_click_to_jump"), TooltipStyles.TooltipDescriptionLine);
	}

	private void SelectAndCenterWorker(HumanoidInstance humanoid, int selectionTab = -1)
	{
		if (humanoid != null)
		{
			MonoSingleton<RtsCamera>.Instance.JumpTo(humanoid.GetPosition());
			MonoSingleton<SelectableObjectManager>.Instance.DeselectAll();
			if (selectionTab >= 0)
			{
				MonoSingleton<UIController>.Instance.ShowExtraSelectionPanelTab(selectionTab);
			}
			WorkerView view = MonoSingleton<WorkerManager>.Instance.GetView(humanoid);
			if (view != null)
			{
				view.Select();
			}
		}
	}

	private void OnNeedTendingTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		List<HumanoidInstance> list = GlobalSaveController.CurrentVillageData.Workers.Where((HumanoidInstance worker) => worker.HasUntendendWounds()).ToList();
		if (list.Any())
		{
			ProcessTooltipVillagersList(tooltipLines, list, clickToJumpInfo: true);
		}
	}

	private void OnWinterClothesTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		using PooledList<HumanoidInstance> pooledList = ListPool<HumanoidInstance>.GetJanitor();
		foreach (HumanoidInstance worker in CurrentVillage.Workers)
		{
			EquipmentInstance equipmentInstance = worker.GetEquipment().Find((EquipmentInstance item) => item.Blueprint.ItemType == ItemType.Garment && (item.Blueprint.EquipmentSlots & EquipmentSlotType.Body) != 0);
			if (equipmentInstance == null || equipmentInstance.Blueprint.WarmthModifier.Min > 0f)
			{
				pooledList.Add(worker);
			}
		}
		ProcessTooltipVillagersList(tooltipLines, pooledList, clickToJumpInfo: true);
	}

	private void OnRolePossibleProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		List<HumanoidInstance> villagers = CurrentVillage.Workers.Where((HumanoidInstance workerInstance) => Repository<RoleRepository, Role>.Instance.GetAllItems().Any(workerInstance.WorkerBehaviour.HumanoidRoleOwner.CanRoleBeLeveledUp)).ToList();
		ProcessTooltipVillagersList(tooltipLines, villagers, clickToJumpInfo: true);
	}

	private void OnRoleWorkHourProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		List<HumanoidInstance> villagers = CurrentVillage.Workers.Where((HumanoidInstance worker) => worker.WorkerBehaviour.HasRoleSchedule && !worker.ActiveBehaviour.HumanoidRoleOwner.AssignedRole).ToList();
		ProcessTooltipVillagersList(tooltipLines, villagers, clickToJumpInfo: true);
	}

	private void OnRoleWorkHourMissingProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		List<HumanoidInstance> villagers = CurrentVillage.Workers.Where((HumanoidInstance worker) => !worker.WorkerBehaviour.HasRoleSchedule && worker.ActiveBehaviour.HumanoidRoleOwner.AssignedRole).ToList();
		ProcessTooltipVillagersList(tooltipLines, villagers, clickToJumpInfo: true);
	}

	private void OnUnarmedVillagersTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		using PooledList<HumanoidInstance> pooledList = ListPool<HumanoidInstance>.GetJanitor();
		foreach (HumanoidInstance worker in CurrentVillage.Workers)
		{
			if (!worker.HasWeapon())
			{
				pooledList.Add(worker);
			}
		}
		ProcessTooltipVillagersList(tooltipLines, pooledList, clickToJumpInfo: true);
	}

	private void OnVillagersIdleProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		List<HumanoidInstance> list = GlobalSaveController.CurrentVillageData.Workers.Where((HumanoidInstance worker) => worker.WorkerBehaviour.IsIdle && !worker.WorkerBehaviour.IsCrazy).ToList();
		if (list.Any())
		{
			ProcessTooltipVillagersList(tooltipLines, list, clickToJumpInfo: true);
		}
	}

	private void CenterCamera(Vector3 position)
	{
		MonoSingleton<RtsCamera>.Instance.JumpTo(position);
	}

	private void OnHunterMissingWeaponTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		List<HumanoidInstance> list = MonoSingleton<WorkerManager>.Instance.AllWorkers.Keys.Where(delegate(HumanoidInstance worker)
		{
			AttackType attackType = CombatUtils.GetAttackType(worker);
			bool num = (worker.WorkerBehaviour.ActiveJobCombination & JobType.Hunting) != 0;
			bool flag = attackType == AttackType.RangeChargeAfter || attackType == AttackType.RangeChargeBefore;
			return num && !flag;
		}).ToList();
		if (list.Any())
		{
			ProcessTooltipVillagersList(tooltipLines, list, clickToJumpInfo: true);
		}
	}

	private void ProcessTooltipVillagersList(List<string> tooltipLines, IEnumerable<HumanoidInstance> villagers, bool clickToJumpInfo)
	{
		if (clickToJumpInfo)
		{
			tooltipLines.Add(ClickInfoText());
		}
		foreach (HumanoidInstance villager in villagers)
		{
			tooltipLines.Insert(1, TooltipStyles.ApplyStyle(villager.Info.GetFullName(), TooltipStyles.TooltipDescriptionLine));
		}
	}

	private void OnEffectorWarningProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		string key = effectorWarningMessages.First((KeyValuePair<string, WarningMessageData> pair) => pair.Value == data).Key;
		tooltipLines.Add(ClickInfoText());
		if (!statsByEffectorWarning.ContainsKey(key))
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(99, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GlobalWarningMessagesManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("KeyNotFound: Dictionary<string, List<StatsInstance>> statsByEffectorWarning does not contain key: ");
				messageBuilder.AppendFormatted(key);
				messageBuilder.AppendLiteral("!");
			}
			Log.Error(messageBuilder);
			return;
		}
		foreach (StatsInstance item in statsByEffectorWarning[key])
		{
			if (item.Owner is HumanoidInstance humanoidInstance)
			{
				tooltipLines.Insert(1, TooltipStyles.ApplyStyle(humanoidInstance.Info.GetFullName(), TooltipStyles.TooltipDescriptionLine));
			}
		}
	}

	private void OnAvailableResearchTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		foreach (ResearchNodeInstance unlockableResearchNode in MonoSingleton<ResearchManager>.Instance.GetUnlockableResearchNodes())
		{
			tooltipLines.Add(LocKeyUtils.GetName(unlockableResearchNode.Blueprint.LocKeys).ToLocalized().ToStyled(TooltipStyles.BulletPoint));
		}
	}

	private void OnTraderCantLeaveProcessTooltip(List<string> tooltipLines, WarningMessageData data)
	{
		HumanoidInstance[] array = GlobalSaveController.CurrentVillageData.NPCs.Where(IsNPCStuck).ToArray();
		if (array.Length == 0)
		{
			return;
		}
		HashSet<string> hashSet = new HashSet<string>();
		HumanoidInstance[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			FactionInstance faction = array2[i].Faction;
			if (faction != null && !hashSet.Contains(faction.NameLocalized))
			{
				hashSet.Add(faction.NameLocalized);
			}
		}
		string newValue = hashSet.ToPrettyStringNoBrackets().ToStyled(TooltipStyles.DefaultOrange);
		using PooledList<string> pooledList = ListPool<string>.GetJanitor();
		foreach (string tooltipLine in tooltipLines)
		{
			pooledList.Add(tooltipLine.Replace("<faction_name>", newValue));
		}
		tooltipLines.Clear();
		tooltipLines.AddRange(pooledList.GetRawList());
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
