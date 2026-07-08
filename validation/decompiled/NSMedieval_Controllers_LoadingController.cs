using System;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix;
using NSEipix.Base;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NSMedieval.Controllers;

public class LoadingController : MonoSingleton<LoadingController>
{
	private string loadingPhaseName;

	private static bool isSceneTransition;

	public static bool IsLoadingComplete { get; set; }

	public static bool IsLeavingMainScene { get; private set; }

	public static bool IsSceneTransition
	{
		get
		{
			return isSceneTransition;
		}
		private set
		{
			bool isEnabled;
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(20, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Controller\\LoadingController.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("IsSceneTransition = ");
				messageBuilder.AppendFormatted(value);
			}
			Log.Info(messageBuilder);
			isSceneTransition = value;
		}
	}

	public static bool IsNotLoaded
	{
		get
		{
			if (!IsSceneTransition && !IsLeavingMainScene && IsLoadingComplete)
			{
				return MonoSingleton<LoadingController>.IsApplicationQuitting();
			}
			return true;
		}
	}

	public event Action<string, float, int> LoadingPhaseChangedEvent;

	public event Action HomeSceneLoadedEvent;

	public event Action LoadingSceneLoadedEvent;

	public event Action MainSceneLoadedEvent;

	public event Action HomeSceneLeavingEvent;

	public event Action MainSceneLeavingEvent;

	public event Action ApplicationQuitEvent;

	public event Action<Scene> ActiveSceneChangedEvent;

	public event Action<Scene> SceneUnloadedEvent;

	public event Action<string> ShowLoadingErrorEvent;

	public event Action LoadingCompleteEvent;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	public new static void OnDomainReload()
	{
		isSceneTransition = false;
		IsLeavingMainScene = false;
		IsLoadingComplete = false;
		MonoSingleton<LoadingController>.OnDomainReload();
	}

	public void DebugMeasureLoadingTime(string message)
	{
		DateTime loadingStart = DateTime.Now;
		LoadingCompleteEvent += OnLoadingComplete;
		void OnLoadingComplete()
		{
			if (MonoSingleton<SceneController>.IsInstantiated())
			{
				LoadingCompleteEvent -= OnLoadingComplete;
			}
			TimeSpan timeSpan = DateTime.Now - loadingStart;
			bool isEnabled;
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(13, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Controller\\LoadingController.cs");
			if (isEnabled)
			{
				messageBuilder.AppendFormatted(message);
				messageBuilder.AppendLiteral(" Duration: ");
				messageBuilder.AppendFormatted(timeSpan.TotalMilliseconds, "F3");
				messageBuilder.AppendLiteral("ms");
			}
			Log.Info(messageBuilder);
		}
	}

	protected override void Awake()
	{
		base.Awake();
		DebugTimer.StartTimer("home_load_on_app_start");
	}

	protected override void OnApplicationQuit()
	{
		base.OnApplicationQuit();
		this.ApplicationQuitEvent?.Invoke();
		this.ApplicationQuitEvent = null;
	}

	public void InvokeLoadingCompleteEvent()
	{
		IsLoadingComplete = true;
		this.LoadingCompleteEvent?.Invoke();
		this.LoadingCompleteEvent = null;
	}

	public void InvokeHomeSceneLeaving()
	{
		IsSceneTransition = true;
		this.HomeSceneLeavingEvent?.Invoke();
	}

	public void InvokeMainSceneLeaving()
	{
		DebugTimer.StartTimer("exit_to_main");
		IsSceneTransition = true;
		IsLeavingMainScene = true;
		IsLoadingComplete = false;
		this.MainSceneLeavingEvent?.Invoke();
	}

	public void InvokeLoadingPhaseChanged(string newLoadingPhase, float percent = -1f)
	{
		if (loadingPhaseName != newLoadingPhase)
		{
			if (!string.IsNullOrEmpty(loadingPhaseName))
			{
				DebugTimer.EndTimer(loadingPhaseName);
			}
			loadingPhaseName = newLoadingPhase;
			DebugTimer.StartTimer(loadingPhaseName);
			bool isEnabled;
			FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(23, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Controller\\LoadingController.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Loading phase changed: ");
				messageBuilder.AppendFormatted(newLoadingPhase);
			}
			Log.Debug(messageBuilder);
		}
		this.LoadingPhaseChangedEvent?.Invoke(newLoadingPhase, percent, -1);
	}

	public void InvokeLoadingPhaseChanged(string newLoadingPhase, int number)
	{
		bool isEnabled;
		FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(23, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Controller\\LoadingController.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Loading phase changed: ");
			messageBuilder.AppendFormatted(newLoadingPhase);
		}
		Log.Debug(messageBuilder);
		this.LoadingPhaseChangedEvent?.Invoke(newLoadingPhase, -1f, number);
	}

	public void InvokeShowLoadingError(string message)
	{
		this.ShowLoadingErrorEvent?.Invoke(message);
	}

	private void SceneLoaded(Scene scene, LoadSceneMode mode)
	{
		bool isEnabled;
		FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(14, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Controller\\LoadingController.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("SceneLoaded = ");
			messageBuilder.AppendFormatted(scene);
		}
		Log.Debug(messageBuilder);
		MonoSingleton<TaskController>.Instance.WaitForNextFrameUnscaled().Then(delegate
		{
			switch (scene.name)
			{
			case "HomeScene":
				IsSceneTransition = false;
				IsLeavingMainScene = false;
				this.HomeSceneLoadedEvent?.Invoke();
				DebugTimer.EndTimer("exit_to_main");
				DebugTimer.EndTimer("home_load_on_app_start");
				break;
			case "LoadingScene":
				this.LoadingSceneLoadedEvent?.Invoke();
				break;
			case "MainScene":
				IsSceneTransition = false;
				this.MainSceneLoadedEvent?.Invoke();
				DebugTimer.EndTimer("exit_to_main");
				break;
			}
		});
	}

	private void SceneUnloaded(Scene scene)
	{
		bool isEnabled;
		FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(16, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Controller\\LoadingController.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("SceneUnloaded = ");
			messageBuilder.AppendFormatted(scene);
		}
		Log.Debug(messageBuilder);
		loadingPhaseName = null;
		this.SceneUnloadedEvent?.Invoke(scene);
	}

	private void ActiveSceneChanged(Scene newScene, Scene oldScene)
	{
		if (!string.IsNullOrEmpty(newScene.name))
		{
			this.ActiveSceneChangedEvent?.Invoke(newScene);
		}
	}

	private void OnEnable()
	{
		SceneManager.sceneLoaded += SceneLoaded;
		SceneManager.sceneUnloaded += SceneUnloaded;
		SceneManager.activeSceneChanged += ActiveSceneChanged;
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		SceneManager.sceneLoaded -= SceneLoaded;
		SceneManager.sceneUnloaded -= SceneUnloaded;
		SceneManager.activeSceneChanged -= ActiveSceneChanged;
		this.LoadingPhaseChangedEvent = null;
		this.HomeSceneLoadedEvent = null;
		this.LoadingSceneLoadedEvent = null;
		this.MainSceneLoadedEvent = null;
		this.HomeSceneLeavingEvent = null;
		this.MainSceneLeavingEvent = null;
		this.ApplicationQuitEvent = null;
		this.ActiveSceneChangedEvent = null;
		this.SceneUnloadedEvent = null;
		this.ShowLoadingErrorEvent = null;
		this.LoadingCompleteEvent = null;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
