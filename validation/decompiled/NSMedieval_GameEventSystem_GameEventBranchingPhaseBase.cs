using System.Collections.Generic;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSMedieval.Serialization;

namespace NSMedieval.GameEventSystem;

[FVSerializableKey("GameEventBranchingPhaseBase", "")]
public abstract class GameEventBranchingPhaseBase : GameEventPhaseBase
{
	protected const int STAY_IN_THIS_PHASE = -1;

	protected List<GameEventPhaseBase> nextPhases = new List<GameEventPhaseBase>();

	private const string fvs_nextPhases = "nextPhases";

	protected GameEventBranchingPhaseBase()
	{
	}

	public override void Dispose()
	{
		base.Dispose();
		nextPhases = null;
	}

	protected abstract int TickNextPhaseIndex();

	public override GameEventPhaseBase Tick()
	{
		int num = TickNextPhaseIndex();
		if (num == -1)
		{
			return this;
		}
		if (num < -1 || num >= nextPhases.Count)
		{
			FVLogger logger = GameEventPhaseBase.Logger;
			bool isEnabled;
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(36, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\BranchingPhases\\Base\\GameEventBranchingPhaseBase.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("No phase at index ");
				messageBuilder.AppendFormatted(num);
				messageBuilder.AppendLiteral(", ending the event");
			}
			logger.Info(in messageBuilder);
			return null;
		}
		return nextPhases[num];
	}

	protected void SetNextPhase(GameEventPhaseBase nextPhase, int index)
	{
		while (nextPhases.Count < index + 1)
		{
			nextPhases.Add(null);
		}
		nextPhases[index] = nextPhase;
	}

	public override void Serialize(FVSerializer serializer)
	{
		base.Serialize(serializer);
		serializer.Write("nextPhases", nextPhases);
	}

	public GameEventBranchingPhaseBase(FVDeserializer deserializer)
		: base(deserializer)
	{
		nextPhases = deserializer.ReadObjectList<GameEventPhaseBase>("nextPhases");
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
