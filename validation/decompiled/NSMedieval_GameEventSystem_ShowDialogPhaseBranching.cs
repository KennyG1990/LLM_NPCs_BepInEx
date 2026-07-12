using System;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix.Base;
using NSMedieval.Dialogs;
using NSMedieval.GameEventSystem.Events;
using NSMedieval.Serialization;
using UnityEngine;

namespace NSMedieval.GameEventSystem;

[Serializable]
[FVSerializableKey("ShowDialogPhaseBranching", "")]
public class ShowDialogPhaseBranching : ShowDialogPhase
{
	private static readonly int NUMBER_OF_CHOICES = 4;

	[SerializeField]
	private GameEventPhaseBase[] choiceDestinationPhases = new GameEventPhaseBase[NUMBER_OF_CHOICES];

	[SerializeField]
	private int switchPhaseIndexNextTick = -1;

	private const string fvs_choiceDestinationPhases = "choiceDestinationPhases";

	private const string fvs_switchPhaseIndexNextTick = "switchPhaseIndexNextTick";

	private bool DialogWasClosed
	{
		get
		{
			if (!dialogWasClosed)
			{
				return switchPhaseIndexNextTick > -1;
			}
			return true;
		}
	}

	public ShowDialogPhaseBranching(int dialogIndex, string overrideDialogImage = null)
		: base(dialogIndex, overrideDialogImage)
	{
	}

	public ShowDialogPhaseBranching(string dialogId)
		: base(dialogId)
	{
	}

	public override void Dispose()
	{
		base.Dispose();
		choiceDestinationPhases = null;
		if (MonoSingleton<DialogViewManager>.IsInstantiated())
		{
			MonoSingleton<DialogViewManager>.Instance.OnClose -= OnClose;
		}
	}

	public override void OnLoaded(bool fromSave)
	{
		if (!DialogWasClosed)
		{
			MonoSingleton<DialogViewManager>.Instance.OnClose += OnClose;
		}
	}

	protected override void OnClose(int selectedOptionIndex)
	{
		MonoSingleton<DialogViewManager>.Instance.OnClose -= OnClose;
		base.EventInstance.GetDialogContent(dialogIndex);
		MonoSingleton<GameEventSystemController>.Instance.EventOptionChosen(base.EventInstance, dialogIndex);
		switchPhaseIndexNextTick = selectedOptionIndex;
	}

	public override GameEventPhaseBase Tick()
	{
		if (!DialogWasClosed)
		{
			return this;
		}
		GameEventPhaseBase gameEventPhaseBase = choiceDestinationPhases[switchPhaseIndexNextTick];
		FVLogger logger = GameEventPhaseBase.Logger;
		bool isEnabled;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(48, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\Events\\Common\\BranchingPhases\\ShowDialogPhaseBranching.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Dialog option index ");
			messageBuilder.AppendFormatted(switchPhaseIndexNextTick);
			messageBuilder.AppendLiteral(" chosen, switching to phase ");
			messageBuilder.AppendFormatted(gameEventPhaseBase);
		}
		logger.Info(in messageBuilder);
		return gameEventPhaseBase;
	}

	public ShowDialogPhaseBranching NextPhaseOnChoice(int choiceIndex, GameEventPhaseBase nextPhase)
	{
		choiceDestinationPhases[choiceIndex] = nextPhase;
		return this;
	}

	public ShowDialogPhaseBranching NextPhaseOnAccept(GameEventPhaseBase nextPhase)
	{
		return NextPhaseOnChoice(0, nextPhase);
	}

	public ShowDialogPhaseBranching NextPhaseOnReject(GameEventPhaseBase nextPhase)
	{
		return NextPhaseOnChoice(1, nextPhase);
	}

	public override void Serialize(FVSerializer serializer)
	{
		base.Serialize(serializer);
		serializer.Write("choiceDestinationPhases", choiceDestinationPhases);
		serializer.Write("switchPhaseIndexNextTick", switchPhaseIndexNextTick);
	}

	public ShowDialogPhaseBranching(FVDeserializer deserializer)
		: base(deserializer)
	{
		choiceDestinationPhases = deserializer.ReadObjectArray<GameEventPhaseBase>("choiceDestinationPhases");
		switchPhaseIndexNextTick = deserializer.ReadInt("switchPhaseIndexNextTick");
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
