using System;
using System.Collections.Generic;
using NSEipix.Base;
using NSMedieval.EventBase;
using NSMedieval.Manager;

namespace NSMedieval.GameEventSystem;

[Serializable]
public class GameEventSystemController : MonoSingleton<GameEventSystemController>
{
	public delegate void RaidStartedDelegate(bool isSiege, List<IEnemyPurchaseUnit> enemies, string settingsCategory, int raidId);

	public delegate void OptionChosenDelegate(GameEventInstance eventInstance, int dialogShowingIndex);

	public CustomAction<GameEventInstance> GameEventStarted = new CustomAction<GameEventInstance>();

	public CustomAction<GameEventInstance> GameEventEnded = new CustomAction<GameEventInstance>();

	public event OptionChosenDelegate GameEventOptionChosen;

	public event Action<ActiveRaidInfo> RaidEventEnded;

	public event RaidStartedDelegate RaidEventStarted;

	public event Action<EventBaseModel> GameEventUnlockedEvent;

	public event Action<string> NpcArrivedToEventEvent;

	public void RaidStarted(bool isSiege, List<IEnemyPurchaseUnit> enemies, string settingsCategory, int raidId)
	{
		this.RaidEventStarted?.Invoke(isSiege, enemies, settingsCategory, raidId);
	}

	public void RaidEnded(ActiveRaidInfo info)
	{
		this.RaidEventEnded?.Invoke(info);
	}

	public void EventStarted(GameEventInstance eventInstance)
	{
		GameEventStarted?.Invoke(eventInstance);
	}

	public void EventEnded(GameEventInstance eventInstance)
	{
		GameEventEnded?.Invoke(eventInstance);
	}

	public void EventOptionChosen(GameEventInstance eventInstance, int dialogShowingIndex)
	{
		this.GameEventOptionChosen?.Invoke(eventInstance, dialogShowingIndex);
	}

	public void GameEventUnlocked(EventBaseModel gameEventUnlocked)
	{
		this.GameEventUnlockedEvent?.Invoke(gameEventUnlocked);
	}

	public void NpcArrivedToEvent(string fromEvent)
	{
		this.NpcArrivedToEventEvent?.Invoke(fromEvent);
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		GameEventStarted?.Clear();
		GameEventStarted = null;
		GameEventEnded?.Clear();
		GameEventEnded = null;
		this.RaidEventEnded = null;
		this.GameEventUnlockedEvent = null;
		this.GameEventOptionChosen = null;
		this.RaidEventStarted = null;
		this.NpcArrivedToEventEvent = null;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
