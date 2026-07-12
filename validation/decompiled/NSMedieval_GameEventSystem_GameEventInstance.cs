using System;
using System.Collections.Generic;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.Controllers;
using NSMedieval.Dialogs.Data;
using NSMedieval.Extensions;
using NSMedieval.GameEventSystem.Events;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Serialization;
using NSMedieval.State;
using NSMedieval.Tools;
using NSMedieval.UI;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using NSMedieval.Weather;
using NSMedieval.WorldMap;
using Objectives;
using UnityEngine;

namespace NSMedieval.GameEventSystem;

[FVSerializableKey("GameEvents.GameEventInstance", "")]
public abstract class GameEventInstance : IDisposable, IFVSerializable
{
	public static readonly FVLogger Logger = FVLogger.New("GameEvent");

	private GameEvent blueprint;

	private string blueprintId;

	private string replaceWeatherText;

	protected GameEventStateMachine stateMachine;

	private WarningMessageData warningMessage;

	private List<string> warningTooltipPrefixLines;

	private bool weatherEventNameInitDone;

	private const string fvs_blueprintId = "blueprintId";

	private const string fvs_stateMachine = "stateMachine";

	public bool HasEnded => stateMachine.HasEnded;

	public List<string> WarningTooltipPrefixLines => warningTooltipPrefixLines ?? (warningTooltipPrefixLines = new List<string>());

	protected static WorldDate DateTime
	{
		get
		{
			if (!MonoSingleton<GlobalSaveController>.IsInstantiated() || GlobalSaveController.CurrentVillageData == null)
			{
				return null;
			}
			return GlobalSaveController.CurrentVillageData.DateAndTime;
		}
	}

	public GameEvent Blueprint
	{
		get
		{
			if (blueprintId == null)
			{
				return null;
			}
			if (blueprint == null || blueprint.GetID() != blueprintId)
			{
				blueprint = Repository<GameEventSettingsRepository, GameEvent>.Instance.GetByID(blueprintId);
			}
			return blueprint;
		}
	}

	public string ReplaceWeatherText
	{
		get
		{
			if (!weatherEventNameInitDone)
			{
				weatherEventNameInitDone = true;
				if (Blueprint.WeatherTextKey != null && !Blueprint.WeatherTextKey.Equals(string.Empty))
				{
					replaceWeatherText = MonoSingleton<LocalizationController>.Instance.GetText(Blueprint.WeatherTextKey);
				}
			}
			return replaceWeatherText;
		}
	}

	protected GameEventInstance()
	{
		stateMachine = new GameEventStateMachine();
	}

	public virtual void Dispose()
	{
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(10, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Disposing ");
			messageBuilder.AppendFormatted(GetType().Name);
		}
		Log.Info(messageBuilder);
		Unsubscribe();
		stateMachine.Dispose();
		blueprint = null;
		stateMachine = null;
		warningMessage = null;
		warningTooltipPrefixLines?.Clear();
		warningTooltipPrefixLines = null;
	}

	protected abstract GameEventPhaseBase GetStartingPhase();

	public virtual bool CanStart()
	{
		if (Blueprint.NeedUniqueResources != null)
		{
			VillageMap map = VillageManager.ActiveVillage.Map;
			string[] needUniqueResources = Blueprint.NeedUniqueResources;
			foreach (string text in needUniqueResources)
			{
				if (map.UniqueResourceTracker.GetUniqueResourceCount(text) <= 0)
				{
					bool isEnabled;
					FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(61, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventInstance.cs");
					if (isEnabled)
					{
						messageBuilder.AppendLiteral("Cannot start event ");
						messageBuilder.AppendFormatted(Blueprint.GetID());
						messageBuilder.AppendLiteral(" - unique required resource ");
						messageBuilder.AppendFormatted(text);
						messageBuilder.AppendLiteral(" was not found");
					}
					Log.Info(messageBuilder);
					return false;
				}
			}
		}
		if (blueprint.NeedResources != null && !NeedResource.CheckNeededResources(blueprint.NeedResources))
		{
			return false;
		}
		if (MonoSingleton<ObjectiveManager>.IsInstantiated() && MonoSingleton<ObjectiveManager>.Instance.ActiveObjective != null && MonoSingleton<ObjectiveManager>.Instance.ActiveObjective.IsBlockingEvent(Blueprint))
		{
			return false;
		}
		return true;
	}

	public bool Start()
	{
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(22, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Starting game event '");
			messageBuilder.AppendFormatted(Blueprint);
			messageBuilder.AppendLiteral("'");
		}
		Logger.Info(in messageBuilder);
		if (stateMachine.HasEnded)
		{
			throw new Exception("Can't start game event that has already ended");
		}
		if (stateMachine.HasStarted)
		{
			throw new Exception("Can't start game event that has already started");
		}
		GameEventPhaseBase startingPhase = GetStartingPhase();
		if (startingPhase == null)
		{
			Logger.Info("Failed to start event, starting phase is null.");
			return false;
		}
		RunEffectors(run: true);
		stateMachine.SetEventInstance(this);
		stateMachine.Start(startingPhase);
		MonoSingleton<GameEventSystem>.Instance.AddToRunningEvents(this);
		MonoSingleton<GameEventSystemController>.Instance.EventStarted(this);
		EnableShaderKeywords();
		ShowStartText();
		CreateWarningMessage();
		Subscribe();
		return true;
	}

	public virtual void OnLoaded(bool fromSave)
	{
		Subscribe();
		CreateWarningMessage();
		stateMachine.SetEventInstance(this);
		stateMachine.OnLoaded(fromSave);
	}

	public void ForceEnd()
	{
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(26, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Force-ending game event '");
			messageBuilder.AppendFormatted(Blueprint);
			messageBuilder.AppendLiteral("'");
		}
		Logger.Info(in messageBuilder);
		try
		{
			stateMachine.ForceEnd();
			OnEnd();
		}
		catch (Exception t)
		{
			FVLogErrorInterpolationHandler messageBuilder2 = new FVLogErrorInterpolationHandler(41, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventInstance.cs");
			if (isEnabled)
			{
				messageBuilder2.AppendLiteral("Failed to force end event '");
				messageBuilder2.AppendFormatted(GetType().Name);
				messageBuilder2.AppendLiteral("', exception: ");
				messageBuilder2.AppendFormatted(t);
			}
			Logger.Error(in messageBuilder2);
		}
	}

	private void Tick(float deltaTime)
	{
		if (LoadingController.IsSceneTransition)
		{
			return;
		}
		using (ProfilerSampleJanitor.Begin("GameEventInstance.Tick"))
		{
			try
			{
				stateMachine.Tick();
			}
			catch (Exception)
			{
				Logger.Error("Failed to tick state machine due to an exception, removing the event!");
				OnEnd();
				throw;
			}
			if (stateMachine.HasEnded)
			{
				OnEnd();
			}
		}
	}

	private void EnableShaderKeywords()
	{
		if (Blueprint.EnableKeywordOnStart != null && !Blueprint.EnableKeywordOnStart.Equals(string.Empty))
		{
			bool isEnabled;
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(27, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendFormatted(Blueprint.GetID());
				messageBuilder.AppendLiteral(": Enabling shader keyword: ");
				messageBuilder.AppendFormatted(Blueprint.EnableKeywordOnStart);
			}
			Logger.Info(in messageBuilder);
			Shader.EnableKeyword(Blueprint.EnableKeywordOnStart);
			Shader.DisableKeyword(WeatherEventInstance.ClearWeatherShaderKeyword);
		}
	}

	private void DisableShaderKeywords()
	{
		if (Blueprint.EnableKeywordOnStart != null && !Blueprint.EnableKeywordOnStart.Equals(string.Empty))
		{
			bool isEnabled;
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(28, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendFormatted(Blueprint.GetID());
				messageBuilder.AppendLiteral(": Disabling shader keyword: ");
				messageBuilder.AppendFormatted(Blueprint.EnableKeywordOnStart);
			}
			Logger.Info(in messageBuilder);
			Shader.DisableKeyword(Blueprint.EnableKeywordOnStart);
			Shader.EnableKeyword(WeatherEventInstance.ClearWeatherShaderKeyword);
		}
	}

	private void Subscribe()
	{
		MonoSingleton<SceneController>.Instance.Tick += Tick;
	}

	private void Unsubscribe()
	{
		if (MonoSingleton<SceneController>.IsInstantiated())
		{
			MonoSingleton<SceneController>.Instance.Tick -= Tick;
		}
	}

	private void RunEffectors(bool run)
	{
		if (Blueprint?.Effectors == null)
		{
			return;
		}
		VillageMap map = VillageManager.ActiveVillage.Map;
		foreach (string effector in blueprint.Effectors)
		{
			map.GlobalEffectorsManager.RunEffectorOnDomain(effector, run, GlobalEffectorDomain.All);
		}
	}

	public virtual void SetBlueprint(GameEvent blueprintEvent)
	{
		blueprint = blueprintEvent;
		blueprintId = blueprint.GetID();
	}

	public virtual void OnEnd()
	{
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(23, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Game event '");
			messageBuilder.AppendFormatted(Blueprint);
			messageBuilder.AppendLiteral("' has ended");
		}
		Logger.Info(in messageBuilder);
		Unsubscribe();
		RunEffectors(run: false);
		MonoSingleton<GameEventSystem>.Instance.RemoveFromRunningEvents(this);
		MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.Data.IncreaseEndedEventCount(blueprintId);
		MonoSingleton<GameEventSystemController>.Instance.EventEnded(this);
		ShowEndText();
		DisableShaderKeywords();
		RemoveWarningMessage();
		if (ShouldUnlockAchievement())
		{
			MonoSingleton<AchievementManager>.Instance.UnlockAchievement(Blueprint.UnlockAchievementOnCompleted);
		}
	}

	public void UpdateWarningMessageTooltip()
	{
		if (Blueprint.WarningMessage != null && !Blueprint.WarningMessage.IsEmpty)
		{
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(warningMessage, visible: true);
		}
	}

	private void CreateWarningMessage()
	{
		if (Blueprint.WarningMessage != null && !Blueprint.WarningMessage.IsEmpty)
		{
			GameEvent.WarningMessageSettings warningMessageSettings = Blueprint.WarningMessage;
			if (warningMessageSettings.TextKey == null || warningMessageSettings.TooltipKey == null || warningMessageSettings.IconPath == null)
			{
				throw new Exception($"Tried to show warning message for game event '{this}', but data from blueprint is incomplete");
			}
			warningMessage = new WarningMessageData(WarningMessageCategory.Warning, warningMessageSettings.TextKey, warningMessageSettings.TooltipKey, warningMessageSettings.IconPath, null, delegate(List<string> lines, WarningMessageData data)
			{
				lines.InsertRange(1, WarningTooltipPrefixLines);
			});
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(warningMessage, visible: true);
		}
	}

	private void RemoveWarningMessage()
	{
		if (warningMessage != null)
		{
			MonoSingleton<WarningMessageController>.Instance.SetMessageVisible(warningMessage, visible: false);
		}
	}

	public virtual SortedDictionary<string, int> GetPossibleEnemiesList()
	{
		Logger.Info("Getting possible enemies list");
		SortedDictionary<string, int> sortedDictionary = new SortedDictionary<string, int>();
		string text = string.Empty;
		if (this is RaidEvent raidEvent)
		{
			text = raidEvent.RaiderFactionInstance?.BlueprintId;
		}
		else if (this is RunawayEvent runawayEvent)
		{
			text = runawayEvent.RaiderFactionInstance.BlueprintId;
		}
		if (string.IsNullOrEmpty(text))
		{
			return sortedDictionary;
		}
		bool shouldForceSiegeWeaponRaid = GlobalSaveController.CurrentVillageData.LastRaidInfo.ShouldForceSiegeWeaponRaid;
		if (!MonoSingleton<RaidEnemySelector>.Instance.PurchaseEnemies((int)MonoSingleton<BaseWealth>.Instance.GetRaidPoints(), out var enemiesToSpawn, out var siegeWeaponsInventory, text, shouldForceSiegeWeaponRaid))
		{
			return sortedDictionary;
		}
		foreach (IEnemyPurchaseUnit item in enemiesToSpawn)
		{
			if (item != null)
			{
				string text2 = MonoSingleton<LocalizationController>.Instance.GetText(item.GetID(), BodyType.None);
				if (!sortedDictionary.TryAdd(text2, 1))
				{
					sortedDictionary[text2]++;
				}
			}
		}
		foreach (SiegeWeaponComponentBlueprint item2 in siegeWeaponsInventory)
		{
			if (!(item2 == null))
			{
				string text3 = MonoSingleton<LocalizationController>.Instance.GetText(item2.GetID(), BodyType.None);
				if (!sortedDictionary.TryAdd(text3, 1))
				{
					sortedDictionary[text3]++;
				}
			}
		}
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(51, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Enemies that will spawn: humanoids ");
			messageBuilder.AppendFormatted(sortedDictionary.ToPrettyString());
			messageBuilder.AppendLiteral(", siege weapons ");
			messageBuilder.AppendFormatted(siegeWeaponsInventory.ToPrettyString());
		}
		Logger.Info(in messageBuilder);
		return sortedDictionary;
	}

	protected virtual bool ShouldUnlockAchievement()
	{
		return false;
	}

	protected virtual void ShowStartText()
	{
		if (!string.IsNullOrEmpty(Blueprint.MessageStart))
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText(Blueprint.MessageStart));
		}
	}

	protected virtual void ShowEndText()
	{
		if (!string.IsNullOrEmpty(Blueprint.MessageEnd))
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText(Blueprint.MessageEnd));
		}
	}

	public GameEvent.DialogContent GetDialogContent(int dialogIndex)
	{
		if (dialogIndex < 0 || dialogIndex >= blueprint.Dialogs.Count)
		{
			throw new ArgumentException($"Invalid dialogIndex '{dialogIndex}' for blueprint dialog array of {blueprint.Dialogs.Count} elements");
		}
		return blueprint.Dialogs[dialogIndex];
	}

	public GameEvent.DialogContent GetDialogContent(string dialogId)
	{
		int dialogById = blueprint.GetDialogById(dialogId);
		return GetDialogContent(dialogById);
	}

	public virtual string GetEventTitle(GameEvent.DialogContent dialogContent)
	{
		return TextFormatting.FormatText(MonoSingleton<LocalizationController>.Instance.GetText(dialogContent.TypeTextKey));
	}

	public virtual string GetEventName(GameEvent.DialogContent dialogContent, BodyType bodyType)
	{
		return TextFormatting.FormatText(MonoSingleton<LocalizationController>.Instance.GetText(dialogContent.NameTextKey, bodyType));
	}

	public virtual string GetEventInfo(GameEvent.DialogContent dialogContent)
	{
		return TextFormatting.FormatText(MonoSingleton<LocalizationController>.Instance.GetText(dialogContent.DescriptionTextKey));
	}

	public virtual string GetEventImagePath(GameEvent.DialogContent dialogContent)
	{
		return dialogContent.ImagePath;
	}

	public virtual string ProcessLocalizedButtonText(string buttonText)
	{
		return TextFormatting.FormatText(buttonText, (HumanoidInstance)null);
	}

	public virtual void Serialize(FVSerializer serializer)
	{
		serializer.Write("blueprintId", blueprintId);
		serializer.Write("stateMachine", stateMachine);
	}

	public GameEventInstance(FVDeserializer deserializer)
	{
		blueprintId = deserializer.ReadString("blueprintId");
		stateMachine = deserializer.ReadObject<GameEventStateMachine>("stateMachine");
	}

	public virtual void FillAgentsLeavingTooltip(TooltipData tooltipData)
	{
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
