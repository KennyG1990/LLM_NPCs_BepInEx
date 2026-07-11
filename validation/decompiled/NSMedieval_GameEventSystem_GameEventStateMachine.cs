using System;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSMedieval.Serialization;
using UnityEngine;

namespace NSMedieval.GameEventSystem;

[Serializable]
[FVSerializableKey("GameEventStateMachine", "")]
public class GameEventStateMachine : IFVSerializable
{
	[SerializeField]
	private bool hasStarted;

	[SerializeField]
	private bool hasEnded;

	[SerializeField]
	private GameEventPhaseBase currentPhase;

	private GameEventInstance parentEventInstance;

	private const string fvs_hasStarted = "hasStarted";

	private const string fvs_hasEnded = "hasEnded";

	private const string fvs_currentPhase = "currentPhase";

	private static FVLogger Logger => GameEventInstance.Logger;

	public bool HasStarted => hasStarted;

	public bool HasEnded => hasEnded;

	public GameEventStateMachine()
	{
		hasStarted = false;
		hasEnded = false;
		currentPhase = null;
	}

	public void Start(GameEventPhaseBase firstPhase)
	{
		if (hasEnded)
		{
			throw new Exception("Can't start state machine that has already ended");
		}
		if (hasStarted)
		{
			throw new Exception("Can't start state machine that has already started");
		}
		hasStarted = true;
		SwitchPhase(firstPhase);
	}

	public void OnLoaded(bool fromSave)
	{
		currentPhase?.InitEventInstance(parentEventInstance);
		currentPhase?.OnLoaded(fromSave);
	}

	private void SwitchPhase(GameEventPhaseBase phase)
	{
		if (phase == null)
		{
			throw new Exception("Phase cannot be null");
		}
		FVLogger logger = Logger;
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(17, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventStateMachine.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Starting phase '");
			messageBuilder.AppendFormatted(phase.GetType().Name);
			messageBuilder.AppendLiteral("'");
		}
		logger.Info(in messageBuilder);
		currentPhase = phase;
		currentPhase.InitEventInstance(parentEventInstance);
		if (!currentPhase.OnStart())
		{
			Logger.Error("Phase failed to start, ending event");
			hasEnded = true;
		}
	}

	public void Tick()
	{
		if (currentPhase == null)
		{
			return;
		}
		if (hasEnded)
		{
			Logger.Warning("Trying to tick game event state machine that has ended");
			return;
		}
		GameEventPhaseBase gameEventPhaseBase = currentPhase.Tick();
		if (gameEventPhaseBase == currentPhase)
		{
			return;
		}
		if (currentPhase != null)
		{
			FVLogger logger = Logger;
			bool isEnabled;
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(15, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventStateMachine.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Ending phase '");
				messageBuilder.AppendFormatted(currentPhase.GetType().Name);
				messageBuilder.AppendLiteral("'");
			}
			logger.Info(in messageBuilder);
			currentPhase.OnEnd();
		}
		if (gameEventPhaseBase == null)
		{
			hasEnded = true;
		}
		else
		{
			SwitchPhase(gameEventPhaseBase);
		}
	}

	public void ForceEnd()
	{
		if (!HasEnded)
		{
			FVLogger logger = Logger;
			bool isEnabled;
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(21, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\GameEventStateMachine.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Force-ending phase '");
				messageBuilder.AppendFormatted(currentPhase.GetType().Name);
				messageBuilder.AppendLiteral("'");
			}
			logger.Info(in messageBuilder);
			currentPhase?.OnEnd();
			hasEnded = true;
		}
	}

	public void SetEventInstance(GameEventInstance eventInstance)
	{
		parentEventInstance = eventInstance;
	}

	public void Dispose()
	{
		currentPhase?.Dispose();
	}

	public void Serialize(FVSerializer serializer)
	{
		serializer.Write("hasStarted", hasStarted);
		serializer.Write("hasEnded", hasEnded);
		serializer.Write("currentPhase", currentPhase);
	}

	public GameEventStateMachine(FVDeserializer deserializer)
	{
		hasStarted = deserializer.ReadBool("hasStarted");
		hasEnded = deserializer.ReadBool("hasEnded");
		currentPhase = deserializer.ReadObject<GameEventPhaseBase>("currentPhase");
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
