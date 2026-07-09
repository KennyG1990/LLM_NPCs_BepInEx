using System;
using Controller;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Controllers;
using NSMedieval.Enums;
using NSMedieval.Map;
using NSMedieval.UI;
using UnityEngine;

namespace NSMedieval.Manager;

public class GameSpeedManager : MonoSingleton<GameSpeedManager>, IPauseGame
{
	private const int SpeedSlowIndexOffset = 4;

	private const int MinSpeed = 0;

	private const int MaxSpeed = 20;

	private GameSpeedIndex preSleepSpeedIndex;

	private GameSpeedIndex previousSpeedIndex;

	private float[] speeds;

	private bool processingLock;

	public bool IsFasterSpeedDisabled => GlobalSaveController.CurrentVillageData.Raids.Count > 0;

	public GameSpeedIndex CurrentSpeedIndex { get; private set; }

	public event Action<float, int> UpdateTimeScaleUIEvent;

	protected override void Awake()
	{
		base.Awake();
		GameSettings data = Repository<GameSettingsData, GameSettings>.Instance.GetData<GameSettings>();
		speeds = new float[EnumValues.GameSpeedIndex.Length];
		speeds[0] = 0f;
		speeds[1] = Mathf.Clamp(data.GameSpeedNormal, 0f, 20f);
		speeds[2] = Mathf.Clamp(data.GameSpeedFast, 0f, 20f);
		speeds[3] = Mathf.Clamp(data.GameSpeedFaster, 0f, 20f);
		speeds[4] = Mathf.Clamp(data.GameSpeedWhenAllSleeping, 0f, 20f);
		speeds[5] = Mathf.Clamp(data.GameSpeedSlow, 0f, 20f);
		speeds[6] = Mathf.Clamp(data.GameSpeedSlower, 0f, 20f);
		speeds[7] = Mathf.Clamp(data.GameSpeedSuperSlow, 0f, 20f);
		speeds[8] = Mathf.Clamp(data.GameSpeedDev, 0f, 20f);
	}

	private void Start()
	{
		MonoSingleton<KeybindingManager>.Instance.SubscribeToEvent(KeyInputEvent.GameSpeedNormal, SetSpeedNormal, activeOnWorldMap: true);
		MonoSingleton<KeybindingManager>.Instance.SubscribeToEvent(KeyInputEvent.GameSpeedFast, SetSpeedFast, activeOnWorldMap: true);
		MonoSingleton<KeybindingManager>.Instance.SubscribeToEvent(KeyInputEvent.GameSpeedFaster, SetSpeedFaster, activeOnWorldMap: true);
		MonoSingleton<KeybindingManager>.Instance.SubscribeToEvent(KeyInputEvent.GamePause, OnPauseKeyPress, activeOnWorldMap: true);
		MonoSingleton<GameplayPauseManager>.Instance.PauseEvent += OnGamePauseManager_Pause;
		MonoSingleton<GameplayPauseManager>.Instance.UnpauseEvent += OnGamePauseManager_Unpause;
		MonoSingleton<UIController>.Instance.GameStartedEvent += OnGameplayStart;
		MonoSingleton<World>.Instance.UIInitCompleteEvent += OnUIInitComplete;
	}

	protected override void OnDestroy()
	{
		if (MonoSingleton<KeybindingManager>.IsInstantiated())
		{
			MonoSingleton<KeybindingManager>.Instance.UnsubscribeFromEvent(KeyInputEvent.GameSpeedNormal, SetSpeedNormal);
			MonoSingleton<KeybindingManager>.Instance.UnsubscribeFromEvent(KeyInputEvent.GameSpeedFast, SetSpeedFast);
			MonoSingleton<KeybindingManager>.Instance.UnsubscribeFromEvent(KeyInputEvent.GameSpeedFaster, SetSpeedFaster);
			MonoSingleton<KeybindingManager>.Instance.UnsubscribeFromEvent(KeyInputEvent.GameSpeedDev, OnGameSpeedSetDev);
			MonoSingleton<KeybindingManager>.Instance.UnsubscribeFromEvent(KeyInputEvent.GamePause, OnPauseKeyPress);
		}
		if (MonoSingleton<GameplayPauseManager>.IsInstantiated())
		{
			MonoSingleton<GameplayPauseManager>.Instance.PauseEvent -= OnGamePauseManager_Pause;
			MonoSingleton<GameplayPauseManager>.Instance.UnpauseEvent -= OnGamePauseManager_Unpause;
		}
		if (MonoSingleton<UIController>.IsInstantiated())
		{
			MonoSingleton<UIController>.Instance.GameStartedEvent -= OnGameplayStart;
		}
		if (MonoSingleton<SceneController>.IsInstantiated())
		{
			MonoSingleton<SceneController>.Instance.Tick -= OnTick;
		}
		if (MonoSingleton<World>.IsInstantiated())
		{
			MonoSingleton<World>.Instance.UIInitCompleteEvent -= OnUIInitComplete;
		}
		base.OnDestroy();
	}

	public void SetSpeedPause()
	{
		ProcessSpeedChange(GameSpeedIndex.Pause);
	}

	public void SetSpeedNormal()
	{
		ProcessSpeedChange(GameSpeedIndex.Normal);
	}

	public void SetSpeedFast()
	{
		ProcessSpeedChange(GameSpeedIndex.Fast);
	}

	public void SetSpeedFaster()
	{
		ProcessSpeedChange(GameSpeedIndex.Faster);
	}

	public void EnableSleepSpeed()
	{
		if (CurrentSpeedIndex != GameSpeedIndex.Sleeping)
		{
			preSleepSpeedIndex = CurrentSpeedIndex;
			ProcessSpeedChange(GameSpeedIndex.Sleeping);
		}
	}

	public void DisableSpeedSleep()
	{
		if (CurrentSpeedIndex == GameSpeedIndex.Sleeping)
		{
			if (preSleepSpeedIndex != 0)
			{
				ProcessSpeedChange(preSleepSpeedIndex);
			}
			else
			{
				ProcessSpeedChange(GameSpeedIndex.Normal);
			}
		}
	}

	public void OnUIButtonClicked(int index)
	{
		ProcessSpeedChange((GameSpeedIndex)index);
	}

	private void OnGameSpeedSetDev()
	{
	}

	private void ProcessSpeedChange(GameSpeedIndex newSpeedIndex)
	{
		bool isEnabled;
		FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(19, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GameSpeedManager.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("ProcessSpeedChange ");
			messageBuilder.AppendFormatted(newSpeedIndex);
		}
		Log.Debug(messageBuilder);
		processingLock = true;
		if (newSpeedIndex == GameSpeedIndex.Pause)
		{
			Log.Trace("Pausing the game with UI button", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GameSpeedManager.cs");
			if (CurrentSpeedIndex == GameSpeedIndex.Pause)
			{
				Log.Trace("Unpausing the game with key press", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GameSpeedManager.cs");
				MonoSingleton<GameplayPauseManager>.Instance.Unregister(this);
			}
			else
			{
				Log.Trace("Pausing the game with key press", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GameSpeedManager.cs");
				MonoSingleton<GameplayPauseManager>.Instance.Register(this);
			}
			processingLock = false;
			return;
		}
		if (CurrentSpeedIndex == GameSpeedIndex.Pause)
		{
			Log.Trace("Unpausing the game with UI button", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GameSpeedManager.cs");
			MonoSingleton<GameplayPauseManager>.Instance.ReleaseSilently(this);
		}
		FVLogTraceInterpolationHandler messageBuilder2 = new FVLogTraceInterpolationHandler(43, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GameSpeedManager.cs");
		if (isEnabled)
		{
			messageBuilder2.AppendLiteral("Setting game speed to index ");
			messageBuilder2.AppendFormatted(newSpeedIndex);
			messageBuilder2.AppendLiteral(" with UI button");
		}
		Log.Trace(messageBuilder2);
		if (newSpeedIndex == GameSpeedIndex.Faster)
		{
			if (IsFasterSpeedDisabled)
			{
				MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("cannot_change_during_raid"));
				SetSpeedFast();
				return;
			}
			if (MonoSingleton<WorkerManager>.Instance.IsEveryoneSleeping())
			{
				EnableSleepSpeed();
				return;
			}
		}
		CurrentSpeedIndex = newSpeedIndex;
		processingLock = false;
	}

	private void OnPauseKeyPress()
	{
		ProcessSpeedChange(GameSpeedIndex.Pause);
	}

	private void OnGamePauseManager_Pause()
	{
		CurrentSpeedIndex = GameSpeedIndex.Pause;
	}

	private void OnGamePauseManager_Unpause()
	{
		GameSpeedIndex currentSpeedIndex = ((previousSpeedIndex == GameSpeedIndex.Pause) ? GameSpeedIndex.Normal : previousSpeedIndex);
		CurrentSpeedIndex = currentSpeedIndex;
	}

	private int GetCurrentTimescaleIndex()
	{
		if (speeds == null)
		{
			return 0;
		}
		for (int i = 0; i < speeds.Length; i++)
		{
			if (Mathf.Approximately(speeds[i], Time.timeScale))
			{
				return i;
			}
		}
		return 0;
	}

	private void OnTick(float obj)
	{
		using (ProfilerSampleJanitor.Begin("GameSpeedManager.Tick"))
		{
			if (!processingLock)
			{
				ChangeTimeScaleByIndex((int)CurrentSpeedIndex);
			}
		}
	}

	private void ChangeTimeScaleByIndex(int timeScaleIndex)
	{
		if (timeScaleIndex == GetCurrentTimescaleIndex())
		{
			return;
		}
		previousSpeedIndex = ((CurrentSpeedIndex == GameSpeedIndex.Pause) ? previousSpeedIndex : CurrentSpeedIndex);
		bool isEnabled;
		if (timeScaleIndex >= speeds.Length || timeScaleIndex < 0)
		{
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(84, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GameSpeedManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Tried to set timescale to index ");
				messageBuilder.AppendFormatted(timeScaleIndex);
				messageBuilder.AppendLiteral(" but there are only ");
				messageBuilder.AppendFormatted(speeds.Length);
				messageBuilder.AppendLiteral(" speeds available. SETTING TO 0.");
			}
			Log.Error(messageBuilder);
			timeScaleIndex = 0;
		}
		Time.timeScale = speeds[timeScaleIndex];
		FVLogTraceInterpolationHandler messageBuilder2 = new FVLogTraceInterpolationHandler(0, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\GameSpeedManager.cs");
		if (isEnabled)
		{
			messageBuilder2.AppendFormatted(Time.timeScale);
		}
		Log.Trace(messageBuilder2);
		this.UpdateTimeScaleUIEvent?.Invoke(speeds[timeScaleIndex], timeScaleIndex);
	}

	private void OnUIInitComplete()
	{
		Time.timeScale = speeds[0];
		MonoSingleton<World>.Instance.UIInitCompleteEvent -= OnUIInitComplete;
	}

	private void OnGameplayStart(bool start)
	{
		if (start)
		{
			MonoSingleton<UIController>.Instance.GameStartedEvent -= OnGameplayStart;
			MonoSingleton<SceneController>.Instance.Tick += OnTick;
			SetSpeedNormal();
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
