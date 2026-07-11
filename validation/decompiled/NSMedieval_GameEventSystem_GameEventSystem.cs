using System;
using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Map;
using NSMedieval.Objectives;
using NSMedieval.Repository;
using Objectives;

namespace NSMedieval.GameEventSystem;

[Serializable]
public class GameEventSystem : MonoSingleton<GameEventSystem>
{
	private HashSet<string> runningEventsID;

	public Action<GameEventInstance> EventStart { get; set; }

	public List<GameEventInstance> RunningEvents
	{
		get
		{
			if (!MonoSingleton<GlobalSaveController>.IsInstantiated())
			{
				return null;
			}
			return GlobalSaveController.CurrentVillageData?.ActiveGameEvents;
		}
	}

	private HashSet<string> RunningEventsID
	{
		get
		{
			if (runningEventsID == null)
			{
				runningEventsID = new HashSet<string>();
				foreach (GameEventInstance runningEvent in RunningEvents)
				{
					runningEventsID.Add(runningEvent.Blueprint.GetID());
				}
			}
			return runningEventsID;
		}
	}

	public bool StartEvent(string eventId)
	{
		GameEvent byID = Repository<GameEventSettingsRepository, GameEvent>.Instance.GetByID(eventId);
		bool isEnabled;
		if (byID == null)
		{
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(90, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\GameEventSystem.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Failed to start game event: entry not found with id '");
				messageBuilder.AppendFormatted(eventId);
				messageBuilder.AppendLiteral("' in GameEventSettingsRepository.json");
			}
			Log.Error(messageBuilder);
			return false;
		}
		ObjectiveInstance activeObjective = MonoSingleton<ObjectiveManager>.Instance.ActiveObjective;
		if (activeObjective != null && activeObjective.IsBlockingEvent(byID))
		{
			return false;
		}
		string text = "NSMedieval.GameEventSystem.Events." + byID.ClassName;
		Type type = Type.GetType(text);
		if (type == null)
		{
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(44, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\GameEventSystem.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Failed to start game event: type not found: ");
				messageBuilder.AppendFormatted(text);
			}
			Log.Error(messageBuilder);
			return false;
		}
		object obj = Activator.CreateInstance(type);
		if (obj == null)
		{
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(57, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\GameEventSystem.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Failed to start game event: cannot create an instance of ");
				messageBuilder.AppendFormatted(type.Name);
			}
			Log.Error(messageBuilder);
			return false;
		}
		GameEventInstance gameEventInstance = (GameEventInstance)obj;
		gameEventInstance.SetBlueprint(byID);
		if (!gameEventInstance.CanStart())
		{
			FVLogInfoInterpolationHandler messageBuilder2 = new FVLogInfoInterpolationHandler(54, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\GameEventSystem.cs");
			if (isEnabled)
			{
				messageBuilder2.AppendLiteral("Failed to start game event: '");
				messageBuilder2.AppendFormatted(gameEventInstance.Blueprint.ClassName);
				messageBuilder2.AppendLiteral("' cannot start right now.");
			}
			Log.Info(messageBuilder2);
			return false;
		}
		if (!gameEventInstance.Start())
		{
			return false;
		}
		EventStart?.Invoke(gameEventInstance);
		return true;
	}

	private void Start()
	{
		MonoSingleton<World>.Instance.MapLoadedEvent += OnGameLoaded;
	}

	protected override void OnDestroy()
	{
		if (MonoSingleton<World>.IsInstantiated())
		{
			MonoSingleton<World>.Instance.MapLoadedEvent -= OnGameLoaded;
		}
		if (RunningEvents != null)
		{
			foreach (GameEventInstance runningEvent in RunningEvents)
			{
				runningEvent.Dispose();
			}
		}
		base.OnDestroy();
	}

	private void OnGameLoaded(bool fromSave)
	{
		foreach (GameEventInstance item in RunningEvents.ToList())
		{
			item.OnLoaded(fromSave);
		}
	}

	public bool IsBlockingEventRunning()
	{
		foreach (GameEventInstance runningEvent in RunningEvents)
		{
			if (runningEvent?.Blueprint != null && !runningEvent.Blueprint.NonBlocking)
			{
				return true;
			}
		}
		return false;
	}

	public void AddToRunningEvents(GameEventInstance eventInstanceToAdd)
	{
		bool isEnabled;
		if (RunningEvents.Contains(eventInstanceToAdd))
		{
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(39, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\GameEventSystem.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Event already added to running events: ");
				messageBuilder.AppendFormatted(eventInstanceToAdd);
			}
			Log.Info(messageBuilder);
			return;
		}
		FVLogDebugInterpolationHandler messageBuilder2 = new FVLogDebugInterpolationHandler(37, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\GameEventSystem.cs");
		if (isEnabled)
		{
			messageBuilder2.AppendLiteral("Adding event to running events: ID '");
			messageBuilder2.AppendFormatted(eventInstanceToAdd.Blueprint.GetID());
			messageBuilder2.AppendLiteral("'");
		}
		Log.Debug(messageBuilder2);
		RunningEvents.Add(eventInstanceToAdd);
		AddToRunningEventsID(eventInstanceToAdd.Blueprint.GetID());
	}

	public void RemoveFromRunningEvents(GameEventInstance eventInstanceToRemove)
	{
		if (RunningEvents.Contains(eventInstanceToRemove))
		{
			bool isEnabled;
			FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(41, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\GameEventSystem.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Removing event from running events: ID '");
				messageBuilder.AppendFormatted(eventInstanceToRemove.Blueprint.GetID());
				messageBuilder.AppendLiteral("'");
			}
			Log.Debug(messageBuilder);
			RunningEvents.Remove(eventInstanceToRemove);
			RemoveFromRunningEventsID(eventInstanceToRemove.Blueprint.GetID());
		}
	}

	private void AddToRunningEventsID(string id)
	{
		RunningEventsID.Add(id);
	}

	private void RemoveFromRunningEventsID(string id)
	{
		if (RunningEventsID.Contains(id))
		{
			RunningEventsID.Remove(id);
		}
	}

	public bool IsEventRunning(string eventID)
	{
		return RunningEventsID.Contains(eventID);
	}

	public bool IsBlockingObjectiveButton()
	{
		foreach (GameEventInstance runningEvent in RunningEvents)
		{
			if (runningEvent?.Blueprint != null && runningEvent.Blueprint.IsBlockingObjectiveButton)
			{
				return true;
			}
		}
		return false;
	}

	public string RunningEventsWeatherTextKey()
	{
		foreach (GameEventInstance runningEvent in RunningEvents)
		{
			if (runningEvent.ReplaceWeatherText != null)
			{
				return runningEvent.ReplaceWeatherText;
			}
		}
		return null;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
