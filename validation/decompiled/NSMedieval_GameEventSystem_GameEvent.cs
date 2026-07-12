using System;
using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix;
using NSEipix.Base;
using NSEipix.Model;
using NSEipix.Repository;
using NSMedieval.Dictionary;
using NSMedieval.EventBase;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Serialization;
using NSMedieval.StatsSystem;
using NSMedieval.Tools;
using NSMedieval.Types;
using NSMedieval.UI.Utils;
using UnityEngine;
using UnityEngine.Serialization;

namespace NSMedieval.GameEventSystem;

[Serializable]
public class GameEvent : EventBaseModel
{
	[Serializable]
	public struct DialogContent
	{
		[SerializeField]
		private string id;

		[SerializeField]
		private LocKeys[] locKeys;

		[SerializeField]
		private string typeTextKey;

		[SerializeField]
		private string nameTextKey;

		[SerializeField]
		private string descriptionTextKey;

		[SerializeField]
		private string imagePath;

		[SerializeField]
		private List<EventOption> options;

		[SerializeField]
		private List<EventEffectsList> optionEffects;

		[SerializeField]
		private NewsSettings newsMessage;

		[SerializeField]
		private bool showCloseButton;

		public string Id => id;

		public string TypeTextKey => LocKeyUtils.GetType(locKeys);

		public string NameTextKey => LocKeyUtils.GetName(LocKeys);

		public string DescriptionTextKey => LocKeyUtils.GetDescription(locKeys);

		public string ImagePath => imagePath;

		public NewsSettings NewsMessage => newsMessage;

		public bool ShowCloseButton => showCloseButton;

		public List<string> Options
		{
			get
			{
				List<string> list = new List<string>();
				foreach (EventOption option in options)
				{
					list.Add(LocKeyUtils.GetName(option.LocKeys));
				}
				return list;
			}
		}

		public List<EventEffectsList> OptionEffects => optionEffects;

		public LocKeys[] LocKeys => locKeys;
	}

	[Serializable]
	public struct EventOption
	{
		[SerializeField]
		private LocKeys[] locKeys;

		public LocKeys[] LocKeys => locKeys;
	}

	[Serializable]
	[FVSerializableKey("GameEventStatSetting", "StatSetting")]
	public struct StatSetting : IFVSerializable
	{
		[SerializeField]
		private string stat;

		[SerializeField]
		private float value;

		[SerializeField]
		private FloatRange valueRange;

		public float Value => ValueRange?.Random() ?? value;

		public StatType Stat
		{
			get
			{
				if (Enum.TryParse<StatType>(stat, out var result))
				{
					return result;
				}
				return StatType.None;
			}
		}

		public FloatRange ValueRange => valueRange;

		public StatSetting(string stat, FloatRange valueRange)
		{
			this = default(StatSetting);
			this.stat = stat;
			this.valueRange = valueRange;
		}

		public void Serialize(FVSerializer serializer)
		{
			serializer.Write("stat", stat);
			serializer.Write("value", value);
			serializer.Write("valueRange", valueRange);
		}

		public StatSetting(FVDeserializer deserializer)
		{
			stat = deserializer.ReadString("stat");
			value = deserializer.ReadFloat("value");
			valueRange = deserializer.ReadObject<FloatRange>("valueRange");
		}
	}

	[Serializable]
	public class WoundsSetting
	{
		[SerializeField]
		private List<string> list = new List<string>();

		[SerializeField]
		private int minCount = -1;

		[SerializeField]
		private int maxCount = -1;

		public List<string> GetWounds()
		{
			if (minCount == -1 && maxCount == -1)
			{
				return new List<string>(this.list.Shuffle());
			}
			int num = UnityEngine.Random.Range(minCount, maxCount + 1);
			if (num <= 0)
			{
				return new List<string>();
			}
			if (num > this.list.Count)
			{
				num = this.list.Count;
			}
			List<string> list = new List<string>(this.list.Shuffle());
			if (this.list.Count - num > 0)
			{
				list.RemoveRange(0, this.list.Count - num);
			}
			return list;
		}
	}

	[Serializable]
	public class NewsSettings
	{
		[SerializeField]
		private string messageKey;

		[SerializeField]
		private string tooltipKey;

		[SerializeField]
		private string iconPath;

		[SerializeField]
		private int defaultDurationHours;

		public string MessageKey => messageKey;

		public string TooltipKey => tooltipKey;

		public string IconPath => iconPath;

		public int DefaultDurationHours => defaultDurationHours;
	}

	[Serializable]
	public class WarningMessageSettings
	{
		[SerializeField]
		private string textKey = string.Empty;

		[SerializeField]
		private string tooltipKey = string.Empty;

		[SerializeField]
		private string iconPath = string.Empty;

		public string TextKey => textKey;

		public string TooltipKey => tooltipKey;

		public string IconPath => iconPath;

		public bool IsEmpty
		{
			get
			{
				if (textKey == string.Empty && tooltipKey == string.Empty)
				{
					return IconPath == string.Empty;
				}
				return false;
			}
		}
	}

	[Serializable]
	public class RaidOutcomeGlobalStatModifier
	{
		[SerializeField]
		private string raidStatus;

		[SerializeField]
		private string globalStat;

		[SerializeField]
		private float addValue;

		[NonSerialized]
		private bool raidStatusCacheInitialized;

		[NonSerialized]
		private RaidStatus raidStatusCache;

		public string GlobalStat => globalStat;

		public float AddValue => addValue;

		public RaidStatus RaidStatus
		{
			get
			{
				if (!raidStatusCacheInitialized)
				{
					raidStatusCache = Enum.Parse<RaidStatus>(raidStatus);
					raidStatusCacheInitialized = true;
				}
				return raidStatusCache;
			}
		}
	}

	private static GameEvent defaultInstance;

	[SerializeField]
	private bool hideInScenario;

	[SerializeField]
	private string category = string.Empty;

	[SerializeField]
	private List<StatSetting> stats = new List<StatSetting>();

	[SerializeField]
	private WoundsSetting wounds;

	[SerializeField]
	private List<string> equipment = new List<string>();

	[SerializeField]
	private string prefab = string.Empty;

	[SerializeField]
	private List<SerializablePair<string, float>> lightningStrikeChance = new List<SerializablePair<string, float>>();

	[SerializeField]
	private FloatRange religionRange = new FloatRange(0f, 0f);

	[SerializeField]
	private IntRange npcsCountRange;

	[SerializeField]
	private FloatRange durationHours = new FloatRange(3f, 6f);

	[SerializeField]
	private FloatRange damageRange = new FloatRange(3f, 6f);

	[SerializeField]
	private FloatRange damageHitPoints = new FloatRange(50f, 200f);

	[SerializeField]
	private FloatRange creatureDamageHitPoints = new FloatRange(10f, 50f);

	[SerializeField]
	private FloatRange groundDamageHitPoints = new FloatRange(50f, 200f);

	[SerializeField]
	private string animationLayer = string.Empty;

	[SerializeField]
	private string endGameObjective = string.Empty;

	[SerializeField]
	private string endGameObjectiveTaskToComplete = string.Empty;

	[SerializeField]
	private FloatRange percentage = new FloatRange(40f, 75f);

	[SerializeField]
	private FloatRange temperatureRange = new FloatRange(10f, 15f);

	[SerializeField]
	private FloatRange temperatureRangeNight = new FloatRange(10f, 15f);

	[SerializeField]
	private bool temperatureAdditive;

	[SerializeField]
	private float sunStrengthMultiplier = 1f;

	[SerializeField]
	private string enableKeywordOnStart = string.Empty;

	[SerializeField]
	private string[] hitEffectorGroupIDs;

	[SerializeField]
	private List<string> effectors;

	[SerializeField]
	private string weatherTextKey;

	[SerializeField]
	private List<string> skipIfWeatherEventsRunning;

	[SerializeField]
	private List<string> friendliness = new List<string>();

	[SerializeField]
	private float genderDistribution = 0.5f;

	[SerializeField]
	private string messageStart;

	[SerializeField]
	private string messageEnd;

	[SerializeField]
	private string messageEndAlternative;

	[SerializeField]
	private IntRange count = new IntRange(0, 25);

	[SerializeField]
	private bool useRaidPoints;

	[SerializeField]
	private string animalType;

	[SerializeField]
	private bool nonBlocking;

	[SerializeField]
	private WarningMessageSettings warningMessage;

	[SerializeField]
	private float startFireChance;

	[SerializeField]
	private List<string> excludeFactions;

	[SerializeField]
	private bool resetFactionFriendliness;

	[SerializeField]
	private List<InterpolatedValueList> interpolatedValues;

	[SerializeField]
	private IntRange stashMarkerDurationRangeMinutes;

	[SerializeField]
	private List<string> stashMarkerMapIds;

	[SerializeField]
	private IntRange stashMarkerSpawnDistanceRange;

	[SerializeField]
	private float beggarStashMarkerSpawnChance;

	[FormerlySerializedAs("traderStashMarkerSpawnChance")]
	[SerializeField]
	private float traderGiftStashMarkerSpawnChance;

	[SerializeField]
	private SerializableIntStringDictionary lootConfigByWealth;

	[SerializeField]
	private string[] needUniqueResources;

	[SerializeField]
	private NeedResource[] needResources;

	[SerializeField]
	private bool countNpcs;

	[SerializeField]
	private string npcId;

	[SerializeField]
	private string customNpcWarningMessage;

	[SerializeField]
	private string chatGraphId;

	[SerializeField]
	private RaidOutcomeGlobalStatModifier[] raidOutcomeGlobalStatModifiers;

	[SerializeField]
	private List<SortingGroupsWithWealth> tradeDealDemands;

	[SerializeField]
	private string unlockAchievementOnCompleted;

	[SerializeField]
	private bool isBlockingObjectiveButton = true;

	[SerializeField]
	private string[] roomTypes;

	[SerializeField]
	private string idleAnimTrigger;

	[SerializeField]
	private bool npcsStandInPlace;

	[NonSerialized]
	private HitEffector[] onHitEffectors;

	[NonSerialized]
	private HashSet<string> excludeFactionsSet;

	private Dictionary<string, InterpolatedValueList> indexedInterpolatedValues;

	private HashSet<FactionFriendliness> friendlinessCache;

	public List<string> SkipIfWeatherEventsRunning => skipIfWeatherEventsRunning;

	private Dictionary<string, InterpolatedValueList> IndexedInterpolatedValues
	{
		get
		{
			if (interpolatedValues == null)
			{
				return null;
			}
			if (indexedInterpolatedValues == null)
			{
				indexedInterpolatedValues = new Dictionary<string, InterpolatedValueList>();
				foreach (InterpolatedValueList interpolatedValue in interpolatedValues)
				{
					if (!indexedInterpolatedValues.TryAdd(interpolatedValue.Name, interpolatedValue))
					{
						throw new Exception("There's a duplicate interpolated value list name for Game Event id='" + id + "'");
					}
				}
			}
			return indexedInterpolatedValues;
		}
	}

	public static GameEvent DefaultInstance
	{
		get
		{
			if (defaultInstance == null)
			{
				defaultInstance = new GameEvent();
			}
			return defaultInstance;
		}
	}

	public string Category => category;

	public List<StatSetting> Stats => stats;

	public WoundsSetting Wounds => wounds;

	public List<string> Equipment => equipment;

	public string Prefab => prefab;

	public List<SerializablePair<string, float>> LightningStrikeChance => lightningStrikeChance;

	public FloatRange ReligionRange => religionRange;

	public IntRange NpcsCountRange => npcsCountRange;

	public FloatRange DurationHours => durationHours;

	public FloatRange DamageRange => damageRange;

	public FloatRange DamageHitPoints => damageHitPoints;

	public FloatRange CreatureDamageHitPoints => creatureDamageHitPoints;

	public FloatRange GroundDamageHitPoints => groundDamageHitPoints;

	public string AnimationLayer => animationLayer;

	public string EndGameObjective => endGameObjective;

	public string EndGameObjectiveTaskToComplete => endGameObjectiveTaskToComplete;

	public FloatRange Percentage => percentage;

	public FloatRange TemperatureRange => temperatureRange;

	public FloatRange TemperatureRangeNight => temperatureRangeNight;

	public WarningMessageSettings WarningMessage => warningMessage;

	public float StartFireChance => startFireChance;

	public IntRange StashMarkerDurationRangeMinutes => stashMarkerDurationRangeMinutes;

	public IReadOnlyList<string> StashMarkerMapIds => stashMarkerMapIds;

	public IntRange StashMarkerSpawnDistanceRange => stashMarkerSpawnDistanceRange;

	public float BeggarStashMarkerSpawnChance => beggarStashMarkerSpawnChance;

	public float TraderGiftStashMarkerSpawnChance => traderGiftStashMarkerSpawnChance;

	public bool TemperatureAdditive => temperatureAdditive;

	public string EnableKeywordOnStart => enableKeywordOnStart;

	public string ChatGraphId => chatGraphId;

	public HitEffector[] OnHitEffectors
	{
		get
		{
			if (onHitEffectors == null)
			{
				List<HitEffector> list = new List<HitEffector>();
				string[] array = hitEffectorGroupIDs;
				foreach (string text in array)
				{
					list.AddRange(Repository<HitEffectorGroupRepository, HitEffectorGroup>.Instance.GetByID(text).HitEffectors);
				}
				onHitEffectors = list.ToArray();
			}
			return onHitEffectors;
		}
	}

	public List<string> Effectors => effectors;

	public string WeatherTextKey => weatherTextKey;

	public HashSet<FactionFriendliness> Friendliness
	{
		get
		{
			if (friendlinessCache == null)
			{
				if (friendliness == null)
				{
					return null;
				}
				friendlinessCache = new HashSet<FactionFriendliness>();
				foreach (string item in friendliness)
				{
					if (Enum.TryParse<FactionFriendliness>(item, out var result))
					{
						friendlinessCache.Add(result);
					}
				}
			}
			return friendlinessCache;
		}
	}

	public float GenderDistribution => genderDistribution;

	public string MessageStart => messageStart;

	public string MessageEnd => messageEnd;

	public string MessageEndAlternative => messageEndAlternative;

	public IntRange Count => count;

	public bool UseRaidPoints => useRaidPoints;

	public AnimalType AnimalType => (AnimalType)Enum.Parse(typeof(AnimalType), animalType);

	public bool HasAnimalType => !string.IsNullOrEmpty(animalType);

	public float SunStrengthMultiplier => sunStrengthMultiplier;

	public bool NonBlocking => nonBlocking;

	public RaidOutcomeGlobalStatModifier[] RaidOutcomeGlobalStatModifiers => raidOutcomeGlobalStatModifiers;

	public HashSet<string> ExcludeFactions
	{
		get
		{
			if (excludeFactionsSet == null)
			{
				excludeFactionsSet = new HashSet<string>();
				if (excludeFactions != null)
				{
					excludeFactionsSet.UnionWith(excludeFactions);
				}
			}
			return excludeFactionsSet;
		}
	}

	public bool ResetFactionFriendliness => resetFactionFriendliness;

	public SerializableIntStringDictionary LootConfigByWealth => lootConfigByWealth;

	public bool HideInScenario => hideInScenario;

	public string[] NeedUniqueResources => needUniqueResources;

	public NeedResource[] NeedResources => needResources;

	public bool CountNpcs => countNpcs;

	public string NpcId => npcId;

	public string CustomNpcWarningMessage => customNpcWarningMessage;

	public List<SortingGroupsWithWealth> TradeDealDemands => tradeDealDemands;

	public string UnlockAchievementOnCompleted => unlockAchievementOnCompleted;

	public bool IsBlockingObjectiveButton => isBlockingObjectiveButton;

	public string[] RoomTypes => roomTypes;

	public string IdleAnimTrigger => idleAnimTrigger;

	public bool NpcsStandInPlace => npcsStandInPlace;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void OnDomainReload()
	{
		defaultInstance = null;
	}

	public float GetInterpolatedValue(string name, int x)
	{
		if (!indexedInterpolatedValues.TryGetValue(name, out var value))
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(79, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GameEventSystem\\Core\\GameEvent.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("InterpolatedValueList of name '");
				messageBuilder.AppendFormatted(name);
				messageBuilder.AppendLiteral("'  not defined for Game Event '");
				messageBuilder.AppendFormatted(id);
				messageBuilder.AppendLiteral("', returning 0.0f");
			}
			Log.Warning(messageBuilder);
			return 0f;
		}
		return value.GetMultiplierInterpolated(x);
	}

	public float GetAnimalEventChance()
	{
		if (!HasAnimalType)
		{
			return 1f;
		}
		int animalsCountFromCategory = GetAnimalsCountFromCategory();
		return GetInterpolatedValue("animalCountMultipliers", animalsCountFromCategory);
	}

	public int GetAnimalsCountFromCategory()
	{
		IEnumerable<Animal> enumerable = from animal in Repository<AnimalBaseRepository, Animal>.Instance.GetAllItems()
			where animal.Category.Equals(Category)
			select animal;
		int num = 0;
		foreach (Animal item in enumerable)
		{
			num += MonoSingleton<AnimalManager>.Instance.GetCount(item);
		}
		return num;
	}

	public override string ToString()
	{
		return base.ClassName + "." + id;
	}

	public int GetRandomDurationMinutes()
	{
		int minutesInHour = GlobalSaveController.CurrentVillageData.DateAndTime.MinutesInHour;
		System.Random random = new System.Random();
		return (int)(DurationHours.Random(random) * (float)minutesInHour);
	}

	public int GetRandomDurationHours()
	{
		return (int)DurationHours.Random();
	}

	public int GetDialogById(string dialogId)
	{
		return base.Dialogs.FindIndex((DialogContent item) => item.Id != null && item.Id.Equals(dialogId));
	}

	public void Validate()
	{
		_ = IndexedInterpolatedValues;
	}

	public bool HasInterpolatedValue(string key)
	{
		if (IndexedInterpolatedValues != null)
		{
			return IndexedInterpolatedValues.ContainsKey(key);
		}
		return false;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
