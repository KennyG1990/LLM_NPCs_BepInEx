using System;
using System.Collections.Generic;
using System.Linq;
using Managers;
using NSEipix.Base;
using NSMedieval.BuildingComponents;
using NSMedieval.Controllers;
using NSMedieval.Dialogs.Data;
using NSMedieval.GameEventSystem.Events;
using NSMedieval.State;
using NSMedieval.Tools;
using NSMedieval.Utils.TimeHelpers;

namespace NSMedieval.GameEventSystem;

public static class GameEventUtil
{
	public static bool TryPurchaseEnemies(string factionBlueprintId, int wealthPoints, out List<IEnemyPurchaseUnit> enemies, out List<SiegeWeaponComponentBlueprint> siegeWeapons, out bool isSiege, bool isAmbush = false)
	{
		isSiege = GlobalSaveController.CurrentVillageData.LastRaidInfo.ShouldForceSiegeWeaponRaid;
		return MonoSingleton<RaidEnemySelector>.Instance.PurchaseEnemies(wealthPoints, out enemies, out siegeWeapons, factionBlueprintId, isSiege, isAmbush);
	}

	public static bool TryPurchaseEnemiesForceSiege(string factionBlueprintId, int wealthPoints, out List<IEnemyPurchaseUnit> enemies, out List<SiegeWeaponComponentBlueprint> siegeWeapons)
	{
		LastRaidInfo.RaidVariation overrideRaidVariation = MonoSingleton<RaidEnemySelector>.Instance.OverrideRaidVariation;
		MonoSingleton<RaidEnemySelector>.Instance.OverrideRaidVariation = LastRaidInfo.RaidVariation.Siege;
		bool result = MonoSingleton<RaidEnemySelector>.Instance.PurchaseEnemies(wealthPoints, out enemies, out siegeWeapons, factionBlueprintId, forceSiegeWeaponRaid: true);
		MonoSingleton<RaidEnemySelector>.Instance.OverrideRaidVariation = overrideRaidVariation;
		return result;
	}

	public static DialogContent BuildDialogContent(GameEventInstance eventInstance, int dialogIndex)
	{
		GameEvent.DialogContent dialogContent = eventInstance.GetDialogContent(dialogIndex);
		return BuildDialogContent(eventInstance, dialogContent);
	}

	public static DialogContent BuildDialogContent(GameEventInstance eventInstance, GameEvent.DialogContent eventDialogContent)
	{
		DialogContent dialogContent = new DialogContent();
		HumanoidInstance humanoidInstance = (eventInstance as NewWorkerEvent)?.HumanoidToAdd;
		dialogContent.Options = new List<DialogOption>();
		int num = 0;
		foreach (string option in eventDialogContent.Options)
		{
			DialogOption dialogOption = new DialogOption();
			int num2 = num;
			string text = MonoSingleton<LocalizationController>.Instance.GetText(option);
			text = TextFormatting.FormatText(text, humanoidInstance);
			dialogOption.Text = text;
			List<EventEffectsList> optionEffects = eventDialogContent.OptionEffects;
			if (optionEffects != null && optionEffects.Count > num2)
			{
				dialogOption.Tooltips = BuildTooltips(eventInstance, eventDialogContent.OptionEffects[num2], humanoidInstance);
			}
			dialogContent.Options.Add(dialogOption);
			num++;
		}
		dialogContent.WindowTitle = eventInstance.GetEventTitle(eventDialogContent);
		dialogContent.ContentBodyImagePath = eventInstance.GetEventImagePath(eventDialogContent);
		BodyType bodyType = humanoidInstance?.Info.BodyType ?? BodyType.None;
		dialogContent.ContentTitle = eventInstance.GetEventName(eventDialogContent, bodyType);
		dialogContent.ContentBodyText = eventInstance.GetEventInfo(eventDialogContent);
		dialogContent.ShowCloseButton = eventDialogContent.ShowCloseButton;
		return dialogContent;
	}

	private static List<TooltipData> BuildTooltips(GameEventInstance eventInstance, EventEffectsList effects, HumanoidInstance humanoid)
	{
		List<TooltipData> list = new List<TooltipData>();
		foreach (GameEventOptionEffect item in effects.Items)
		{
			if (item == GameEventOptionEffect.None)
			{
				continue;
			}
			TooltipData tooltipData = new TooltipData();
			humanoid = ((item == GameEventOptionEffect.NewWorker) ? humanoid : null);
			tooltipData.Key = item.ToString();
			tooltipData.Humanoid = humanoid;
			switch (item)
			{
			case GameEventOptionEffect.AgentsLeaving:
				eventInstance.FillAgentsLeavingTooltip(tooltipData);
				break;
			case GameEventOptionEffect.PossibleRaid:
			case GameEventOptionEffect.RaidImminent:
				tooltipData.Args = (from pair in eventInstance.GetPossibleEnemiesList()
					select $"~{pair.Value} {pair.Key}").ToList();
				break;
			default:
				tooltipData.Args = new List<string>();
				break;
			}
			list.Add(tooltipData);
		}
		return list;
	}

	public static uint PublishNews(GameEventInstance eventInstance, int dialogIndex)
	{
		NewsData newsData = BuildNewsData(eventInstance, dialogIndex);
		MonoSingleton<NewsManager>.Instance.Publish(newsData);
		return newsData.Id;
	}

	public static uint PublishNews(GameEventInstance eventInstance, string dialogId)
	{
		NewsData newsData = BuildNewsData(eventInstance, dialogId);
		MonoSingleton<NewsManager>.Instance.Publish(newsData);
		return newsData.Id;
	}

	public static NewsData BuildNewsData(GameEventInstance eventInstance, string dialogId)
	{
		GameEvent.DialogContent dialogContent = eventInstance.GetDialogContent(dialogId);
		return BuildNewsData(eventInstance, dialogContent);
	}

	public static NewsData BuildNewsData(GameEventInstance eventInstance, int dialogIndex)
	{
		GameEvent.DialogContent dialogContent = eventInstance.GetDialogContent(dialogIndex);
		return BuildNewsData(eventInstance, dialogContent);
	}

	private static NewsData BuildNewsData(GameEventInstance eventInstance, GameEvent.DialogContent eventDialogContent)
	{
		if (eventDialogContent.NewsMessage == null || eventDialogContent.NewsMessage.MessageKey == null || eventDialogContent.NewsMessage.TooltipKey == null || eventDialogContent.NewsMessage.IconPath == null)
		{
			throw new Exception($"News message data not found or incomplete for event '{eventInstance}', dialogId {eventDialogContent.Id}");
		}
		string localizedText = GetLocalizedText(eventDialogContent.NewsMessage.MessageKey);
		string localizedText2 = GetLocalizedText(eventDialogContent.NewsMessage.TooltipKey);
		string iconPath = eventDialogContent.NewsMessage.IconPath;
		int defaultDurationHours = eventDialogContent.NewsMessage.DefaultDurationHours;
		TimeInterval? activeTimeInterval = ((defaultDurationHours == 0) ? null : new TimeInterval?(TimeInterval.FromNowHours(defaultDurationHours)));
		DialogContent dialogContent = BuildDialogContent(eventInstance, eventDialogContent);
		return new NewsData(localizedText, iconPath, localizedText2, dialogContent, activeTimeInterval);
	}

	private static string GetLocalizedText(string key)
	{
		if (key == null)
		{
			throw new Exception("Localization key is null");
		}
		return TextFormatting.FormatText(MonoSingleton<LocalizationController>.Instance.GetText(key));
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
