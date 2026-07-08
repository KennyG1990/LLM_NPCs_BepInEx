using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FIMSpace.FSpine;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.CombatAi;
using NSMedieval.Construction;
using NSMedieval.Controllers;
using NSMedieval.Goap;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.State;
using NSMedieval.State.WorkerJobs;
using NSMedieval.StatsSystem;
using NSMedieval.Tutorial;
using NSMedieval.Types;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Utils.TimeHelpers;
using NSMedieval.View.Animals;
using NSMedieval.Views.Resources;
using NSMedieval.Village.Map;
using NSMedieval.Village.Map.Pathfinding;
using NSMedieval.WorldMap;
using UnityEngine;

namespace NSMedieval.Manager;

public class AnimalManager : MonoSingleton<AnimalManager>, IObserver
{
	public const int MaxVisibleSpineAnimatorAnimals = 20;

	public int FrustumCullingVisibleCounter;

	[NonSerialized]
	private readonly ConcurrentDictionary<AnimalInstance, AnimalView> allAnimals = new ConcurrentDictionary<AnimalInstance, AnimalView>();

	[NonSerialized]
	private readonly ConcurrentDictionary<AnimalType, HashSet<AnimalInstance>> animalsByType = new ConcurrentDictionary<AnimalType, HashSet<AnimalInstance>>();

	[NonSerialized]
	private readonly Dictionary<AnimalOrderType, int> animalOrders = new Dictionary<AnimalOrderType, int>();

	[NonSerialized]
	private readonly List<AnimalInstance> animalsEndOfLife = new List<AnimalInstance>();

	[NonSerialized]
	private readonly Dictionary<Animal, int> animalsCount = new Dictionary<Animal, int>();

	[NonSerialized]
	private readonly HashSet<AnimalInstance> canBeRopedToPen = new HashSet<AnimalInstance>();

	private int uniqueIdTracker;

	private Cooldown debugEventCooldown;

	public ConcurrentDictionary<AnimalInstance, AnimalView> Animals => allAnimals;

	public HashSet<AnimalInstance> CanBeRopedToPen => canBeRopedToPen;

	public IEnumerable<AnimalInstance> GetAnimals(AnimalType type)
	{
		return animalsByType.GetOrAdd(type);
	}

	public void SpawnAnimalsFromSavegame()
	{
		uniqueIdTracker = 0;
		RepositionStuckAnimals();
		bool isEnabled;
		foreach (CaravanInstance caravan in MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.Data.Caravans)
		{
			foreach (CreatureBase creature in caravan.Creatures)
			{
				AnimalInstance animalInstance = creature as AnimalInstance;
				if (animalInstance == null)
				{
					continue;
				}
				if (!animalInstance.IsInIncognitoMode())
				{
					FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(69, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\AnimalManager.cs");
					if (isEnabled)
					{
						messageBuilder.AppendLiteral("IncognitoDispose animal ");
						messageBuilder.AppendFormatted(animalInstance.GetFullName());
						messageBuilder.AppendLiteral(". It was not in incognito but was travelling.");
					}
					Log.Info(messageBuilder);
					animalInstance.IncognitoDispose();
				}
				AnimalInstance animalInstance2 = GlobalSaveController.CurrentVillageData.Animals.FirstOrDefault((AnimalInstance a) => a.UniqueId == animalInstance.UniqueId);
				if (animalInstance2 != null)
				{
					FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(70, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\AnimalManager.cs");
					if (isEnabled)
					{
						messageBuilder.AppendLiteral("Detected double animal on map & region map. Removing ");
						messageBuilder.AppendFormatted(animalInstance.GetFullName());
						messageBuilder.AppendLiteral(" (");
						messageBuilder.AppendFormatted(animalInstance.UniqueId);
						messageBuilder.AppendLiteral(") from the map.");
					}
					Log.Info(messageBuilder);
					GlobalSaveController.CurrentVillageData.RemoveAnimal(animalInstance2);
				}
			}
		}
		AnimalInstance[] array = GlobalSaveController.CurrentVillageData.Animals.ToArray();
		foreach (AnimalInstance animalInstance3 in array)
		{
			StatInstance statInstance = animalInstance3?.Stats?.GetStat(StatType.Health);
			if (statInstance == null)
			{
				Log.Error("Animal health stat was null when trying to spawn the animal. Removing that animal.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\AnimalManager.cs");
				GlobalSaveController.CurrentVillageData.RemoveAnimal(animalInstance3);
				MonoSingleton<CaravanFormingManager>.Instance.CancelCaravansWithAnimal(animalInstance3);
				continue;
			}
			if (statInstance.Current <= 0f)
			{
				GlobalSaveController.CurrentVillageData.RemoveAnimal(animalInstance3);
				MonoSingleton<CaravanFormingManager>.Instance.CancelCaravansWithAnimal(animalInstance3);
				continue;
			}
			InstantiateAnimal(animalInstance3, afterLoading: true);
			if (uniqueIdTracker < animalInstance3.UniqueId)
			{
				uniqueIdTracker = animalInstance3.UniqueId;
			}
		}
		using PooledHashSet<int> pooledHashSet = HashSetPool<int>.GetJanitor();
		foreach (HumanoidInstance nPC in GlobalSaveController.CurrentVillageData.NPCs)
		{
			if (nPC.PetsIDs != null)
			{
				pooledHashSet.UnionWith(nPC.PetsIDs);
			}
		}
		foreach (HumanoidInstance worker in GlobalSaveController.CurrentVillageData.Workers)
		{
			if (worker.PetsIDs != null)
			{
				pooledHashSet.UnionWith(worker.PetsIDs);
			}
		}
		array = GlobalSaveController.CurrentVillageData.Animals.ToArray();
		foreach (AnimalInstance animalInstance4 in array)
		{
			if (animalInstance4.AnimalType == AnimalType.DomesticNpc && !pooledHashSet.Contains(animalInstance4.UniqueId))
			{
				FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(67, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\AnimalManager.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("DomesticNpc animal ");
					messageBuilder.AppendFormatted(animalInstance4);
					messageBuilder.AppendLiteral(" is without an owner. Setting to plain Domestic.");
				}
				Log.Info(messageBuilder);
				animalInstance4.SetAnimalType(AnimalType.Domestic);
			}
		}
		Dictionary<AnimalInstance, CreatureBase> petsAndOwners = new Dictionary<AnimalInstance, CreatureBase>();
		using PooledList<CreatureBase> pooledList = ListPool<CreatureBase>.GetJanitor();
		pooledList.AddRange(GlobalSaveController.CurrentVillageData.NPCs);
		pooledList.AddRange(GlobalSaveController.CurrentVillageData.Workers);
		foreach (CreatureBase item in pooledList)
		{
			if (!(item is HumanoidInstance { WorkerBehaviour: not null }))
			{
				continue;
			}
			if (item.Pets.Count > 0)
			{
				AnimalInstance animalInstance5 = item.Pets.FirstOrDefault((AnimalInstance p) => !petsAndOwners.ContainsKey(p));
				if (animalInstance5 != null)
				{
					petsAndOwners.Add(animalInstance5, item);
				}
			}
			item.Pets.Clear();
			item.PetsIDs?.Clear();
		}
		foreach (KeyValuePair<AnimalInstance, CreatureBase> item2 in petsAndOwners)
		{
			item2.Value.AssignPet(item2.Key);
		}
	}

	public void SpawnAnimalsFromTravel(List<MapNode> availablePositions)
	{
		uniqueIdTracker = 0;
		AnimalInstance[] array = GlobalSaveController.CurrentVillageData.Animals.ToArray();
		foreach (AnimalInstance animalInstance in array)
		{
			MonoSingleton<StartPositionManager>.Instance.GetNextPosition();
			Vector3 worldPosition = availablePositions.PickRandom().WorldPosition;
			animalInstance.SetPosition(worldPosition);
			if (animalInstance.Stats.GetStat(StatType.Health).Current <= 0f)
			{
				GlobalSaveController.CurrentVillageData.RemoveAnimal(animalInstance);
				MonoSingleton<CaravanFormingManager>.Instance.CancelCaravansWithAnimal(animalInstance);
				continue;
			}
			InstantiateAnimal(animalInstance, afterLoading: true);
			if (uniqueIdTracker < animalInstance.UniqueId)
			{
				uniqueIdTracker = animalInstance.UniqueId;
			}
		}
		HashSet<int> hashSet = HashSetPool<int>.Get();
		foreach (HumanoidInstance nPC in GlobalSaveController.CurrentVillageData.NPCs)
		{
			if (nPC.PetsIDs != null)
			{
				hashSet.UnionWith(nPC.PetsIDs);
			}
		}
		foreach (HumanoidInstance worker in GlobalSaveController.CurrentVillageData.Workers)
		{
			if (worker.PetsIDs != null)
			{
				hashSet.UnionWith(worker.PetsIDs);
			}
		}
		array = GlobalSaveController.CurrentVillageData.Animals.ToArray();
		foreach (AnimalInstance animalInstance2 in array)
		{
			if (animalInstance2.AnimalType == AnimalType.DomesticNpc && !hashSet.Contains(animalInstance2.UniqueId))
			{
				bool isEnabled;
				FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(67, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\AnimalManager.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("DomesticNpc animal ");
					messageBuilder.AppendFormatted(animalInstance2);
					messageBuilder.AppendLiteral(" is without an owner. Setting to plain Domestic.");
				}
				Log.Info(messageBuilder);
				animalInstance2.SetAnimalType(AnimalType.Domestic);
			}
		}
		HashSetPool<int>.Return(hashSet);
		hashSet = null;
		Dictionary<AnimalInstance, CreatureBase> petsAndOwners = new Dictionary<AnimalInstance, CreatureBase>();
		List<CreatureBase> list = ListPool<CreatureBase>.Get();
		list.AddRange(GlobalSaveController.CurrentVillageData.NPCs);
		list.AddRange(GlobalSaveController.CurrentVillageData.Workers);
		foreach (CreatureBase item in list)
		{
			if (!(item is HumanoidInstance { WorkerBehaviour: not null }))
			{
				continue;
			}
			if (item.Pets.Count > 0)
			{
				AnimalInstance animalInstance3 = item.Pets.FirstOrDefault((AnimalInstance p) => !petsAndOwners.ContainsKey(p));
				if (animalInstance3 != null)
				{
					petsAndOwners.Add(animalInstance3, item);
				}
			}
			item.Pets.Clear();
			item.PetsIDs?.Clear();
		}
		ListPool<CreatureBase>.Return(list);
		list = null;
		foreach (KeyValuePair<AnimalInstance, CreatureBase> item2 in petsAndOwners)
		{
			item2.Value.AssignPet(item2.Key);
		}
	}

	public void RepositionStuckAnimals()
	{
		List<AnimalInstance> list = ListPool<AnimalInstance>.Get();
		foreach (AnimalInstance animal in GlobalSaveController.CurrentVillageData.Animals)
		{
			Vec3Int lhs = animal.GetGridPosition();
			Vec3Int rhs = Vec3Int.zero;
			if (lhs == rhs)
			{
				list.Add(animal);
				continue;
			}
			Vector3 position = animal.GetPosition();
			if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z))
			{
				list.Add(animal);
			}
		}
		if (list.Count == 0)
		{
			ListPool<AnimalInstance>.Return(list);
			return;
		}
		NPCStartPositionManager.SetStartPositionsForAgents(Repository<WalkableModelRepository, WalkableModel>.Instance.GetTestAgentWalkableDoors(), list);
		ListPool<AnimalInstance>.Return(list);
	}

	public int GetCount(Animal blueprint)
	{
		if (blueprint == null)
		{
			return 0;
		}
		return animalsCount.GetValueOrDefault(blueprint, 0);
	}

	public AnimalInstance GetByCreationID(int uniqueId)
	{
		return allAnimals.Keys.FirstOrDefault((AnimalInstance item) => item.UniqueId == uniqueId);
	}

	public AnimalInstance GetByUniqueId(int uniqueId)
	{
		return allAnimals.Keys.FirstOrDefault((AnimalInstance item) => item.UniqueId == uniqueId);
	}

	public AnimalView GetView(AnimalInstance animalInstance)
	{
		return allAnimals.GetValueOrDefault(animalInstance);
	}

	public bool HasAnimalWithOrder(AnimalOrderType order)
	{
		if (animalOrders.ContainsKey(order))
		{
			return animalOrders[order] > 0;
		}
		return false;
	}

	public AnimalInstance SpawnAnimal(string animalID, Vector3 position, BodyType bodyType, int lifePhaseIndex = -1, float lifePhasePercent = 0f, bool isInIncognitoMode = false, AnimalType animalType = AnimalType.Wild, CreatureBase ropeTo = null, CreatureBase petOwner = null)
	{
		AnimalInstance animalInstance = new AnimalInstance(animalID, position, bodyType, lifePhaseIndex, lifePhasePercent, isInIncognitoMode, animalType);
		if (petOwner != null)
		{
			animalInstance.AssignPetOwner(petOwner);
		}
		if (ropeTo != null)
		{
			animalInstance.RopeTo(ropeTo);
		}
		GlobalSaveController.CurrentVillageData.AddAnimal(animalInstance);
		InstantiateAnimal(animalInstance);
		return animalInstance;
	}

	public AnimalInstance CreateNewAnimalInstance(string animalID, Vector3 position, BodyType bodyType, int lifePhaseIndex = -1, float lifePhasePercent = 0f, bool isInIncognitoMode = false, AnimalType animalType = AnimalType.Wild)
	{
		return new AnimalInstance(animalID, position, bodyType, lifePhaseIndex, lifePhasePercent, isInIncognitoMode, animalType);
	}

	public void RemoveAnimal(AnimalInstance animal, bool dropResources)
	{
		if (!allAnimals.ContainsKey(animal))
		{
			return;
		}
		canBeRopedToPen.Remove(animal);
		if (animal.OrderType != 0 && !animalOrders.TryAdd(animal.OrderType, 0))
		{
			animalOrders[animal.OrderType]--;
		}
		if (dropResources)
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(animal.LifePhase.CarcassResourceId);
			bool isEnabled;
			if (byID == null)
			{
				FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(72, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\AnimalManager.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Resource in animal's 'CarcassResourceId' Value:'");
					messageBuilder.AppendFormatted(animal.LifePhase.CarcassResourceId);
					messageBuilder.AppendLiteral("' not found. AnimalID: ");
					messageBuilder.AppendFormatted(animal.Id);
					messageBuilder.AppendLiteral(" ");
				}
				Log.Error(messageBuilder);
			}
			else
			{
				ResourceInstance resourceInstance = new ResourceInstance(byID, 1, animal);
				resourceInstance.SetLocalizedInheritedName(animal.GetFullName());
				bool flag = (animal.AnimalType == AnimalType.Wild || animal.AnimalType == AnimalType.WildAggressive) && animal.OrderType != AnimalOrderType.Hunt;
				resourceInstance.ForbidOnInit = (IsTooCloseToAggressiveAnimal(animal.GetPosition()) || flag) && !animal.KilledByTrap;
				ResourcePileView resourcePileView = MonoSingleton<ResourcePileManager>.Instance.SpawnPile(resourceInstance, animal.GetGridPosition().ToVector3World(), resourceInstance.ForbidOnInit);
				animal.SetCorpsePile((resourcePileView != null) ? resourcePileView.ResourcePileInstance : null);
				if (resourcePileView?.ResourcePileInstance != null)
				{
					MonoSingleton<AnimalController>.Instance.AnimalKilledByHunter(animal, resourcePileView.ResourcePileInstance);
				}
				if (resourcePileView == null)
				{
					FVLogWarningInterpolationHandler messageBuilder2 = new FVLogWarningInterpolationHandler(47, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\AnimalManager.cs");
					if (isEnabled)
					{
						messageBuilder2.AppendLiteral("Could not spawn animal corpse at ");
						messageBuilder2.AppendFormatted(animal.GetPosition());
						messageBuilder2.AppendLiteral(" for animalId:");
						messageBuilder2.AppendFormatted(animal.Id);
					}
					Log.Warning(messageBuilder2);
				}
			}
		}
		Animal blueprint = animal.Blueprint;
		GlobalSaveController.CurrentVillageData.RemoveAnimal(animal);
		animalsByType.GetOrAdd(animal.AnimalType).Remove(animal);
		AnimalView animalView = allAnimals[animal];
		allAnimals.Remove(animal);
		animalView.Dispose(disposeInstance: true);
		AddToAnimalsCount(blueprint, -1);
		MonoSingleton<AnimalController>.Instance.OnAnimalRemoved(animal);
	}

	public void DestroyView(AnimalInstance animalInstance)
	{
		AnimalView view = GetView(animalInstance);
		if ((object)view != null)
		{
			canBeRopedToPen.Remove(animalInstance);
			if (animalInstance.OrderType != 0 && !animalOrders.TryAdd(animalInstance.OrderType, 0))
			{
				animalOrders[animalInstance.OrderType]--;
			}
			AddToAnimalsCount(animalInstance.Blueprint, -1);
			animalsByType.GetOrAdd(animalInstance.AnimalType).Remove(animalInstance);
			allAnimals.Remove(animalInstance);
			if (view != null)
			{
				view.Dispose(disposeInstance: false);
			}
		}
	}

	public void RefreshAllMarkForRoping()
	{
		canBeRopedToPen.Clear();
		foreach (KeyValuePair<AnimalInstance, AnimalView> allAnimal in allAnimals)
		{
			RefreshMarkForRoping(allAnimal.Key);
		}
	}

	public void RefreshMarkForRoping(AnimalInstance instance)
	{
		if (instance.AnimalType != 0 || CombatUtils.IsNullOrDisposed(instance))
		{
			canBeRopedToPen.Remove(instance);
		}
		else if (instance.GetNode() == null)
		{
			canBeRopedToPen.Remove(instance);
		}
		else if (MonoSingleton<PenDetection>.Instance.IsAnimalInOwnPen(instance))
		{
			canBeRopedToPen.Remove(instance);
		}
		else if (!MonoSingleton<PenDetection>.Instance.CanBeAddedToSomePen(instance))
		{
			canBeRopedToPen.Remove(instance);
		}
		else if (instance.IsFormingCaravan() || instance.IsLeavingMap)
		{
			canBeRopedToPen.Remove(instance);
		}
		else
		{
			canBeRopedToPen.Add(instance);
		}
	}

	public bool IsTooCloseToAggressiveAnimal(Vector3 positionToCheck, float animalPerceptionMultiplier = 1.4f)
	{
		foreach (AnimalInstance key in allAnimals.Keys)
		{
			if (key != null && !(key.Blueprint == null) && !key.HasDisposed && !key.HasDied && key.CombatAi.IsStateSet(CombatAiState.IsAggressive) && key.AnimalType != 0 && key.AnimalType != AnimalType.Pet && Vector3.Distance(key.GetPosition(), positionToCheck) < key.GetAttributeValue(AttributeType.CombatPerception) * animalPerceptionMultiplier)
			{
				return true;
			}
		}
		return false;
	}

	public void ScareOffAnimals(Vec3Int gridPosition, float range, IDamageCommonAgent agentDealtDamage)
	{
		Vector3 worldPosition = GridUtils.GetWorldPosition(gridPosition);
		try
		{
			AnimalInstance animalInstance = agentDealtDamage as AnimalInstance;
			foreach (AnimalInstance key in allAnimals.Keys)
			{
				if (key != null && !key.HasDisposed && !key.HasDied && agentDealtDamage != key && (animalInstance == null || !(key.Blueprint == animalInstance.Blueprint)) && !key.CombatAi.GetState<bool>(CombatAiState.IsAggressive) && !key.CombatAi.IsStateSet(CombatAiState.PreferedTarget) && Vector3.Distance(key.GetPosition(), worldPosition) <= range && PathfinderUtil.IsPathPossible(key, gridPosition))
				{
					key.ScareOff();
				}
			}
		}
		catch (Exception)
		{
		}
	}

	public void ScareOffAnimals(Vec3Int gridPosition, float range)
	{
		Vector3 worldPosition = GridUtils.GetWorldPosition(gridPosition);
		try
		{
			foreach (AnimalInstance key in allAnimals.Keys)
			{
				if (key != null && !key.HasDisposed && !key.HasDied && !key.CombatAi.GetState<bool>(CombatAiState.IsAggressive) && !key.CombatAi.IsStateSet(CombatAiState.PreferedTarget) && Vector3.Distance(key.GetPosition(), worldPosition) <= range)
				{
					key.ScareOffForced();
				}
			}
		}
		catch (Exception)
		{
		}
	}

	public AnimalInstance GetReferenceAnimalForTeleport(AnimalInstance animalToTeleport, Func<AnimalInstance, bool> func)
	{
		if (animalsByType.TryGetValue(animalToTeleport.AnimalType, out var value))
		{
			foreach (AnimalInstance item in value)
			{
				if (item != null && !item.HasDisposed && func(item))
				{
					return item;
				}
			}
		}
		return null;
	}

	public void TryUnstuckAnimal(AnimalInstance animal)
	{
		if (animal == null || animal.HasDisposed)
		{
			return;
		}
		VillageMap map = animal.Map;
		Vec3Int gridPosition = animal.GetGridPosition();
		MapNode node = map.GetNode(in gridPosition);
		if (!(animal.PathTraversalProvider is TagTraversalProvider tagTraversalProvider) || tagTraversalProvider.CanStandOnRegion(node.Region))
		{
			return;
		}
		foreach (MapNode item in FloodFillUtil.IterateFloodFillConnections(node, 20f))
		{
			if (tagTraversalProvider.CanStandOnRegion(item.Region))
			{
				animal.PathDriver.Teleport(item.Position);
				break;
			}
		}
	}

	protected override void Awake()
	{
		base.Awake();
		debugEventCooldown = new Cooldown(TutorialManager.IsTutorialActive);
	}

	private void OnFaceObject(AnimalInstance animalInstance, Vector3 objectPosition)
	{
		if (allAnimals.TryGetValue(animalInstance, out var value))
		{
			value.FaceObject(objectPosition);
		}
	}

	private void OnTriggersReset(IGoapAgentOwner agentOwner)
	{
		if (agentOwner is AnimalInstance key && allAnimals.TryGetValue(key, out var value))
		{
			value.ResetTriggers();
		}
	}

	private void OnTriggerAnimation(IGoapAgentOwner agentOwner, string trigger)
	{
		if (agentOwner is AnimalInstance key && allAnimals.TryGetValue(key, out var value))
		{
			value.OnTriggerAnimation(trigger);
		}
	}

	private void OnAnimalDeath(AnimalInstance animal)
	{
		RemoveAnimal(animal, dropResources: true);
	}

	private void OnMarkAnimalForOrder(AnimalOrderType newOrder, AnimalInstance animal)
	{
		if (animal == null || animal.HasDisposed || animal.OrderType == newOrder)
		{
			return;
		}
		if (newOrder == AnimalOrderType.None && animal.OrderType != 0)
		{
			if (!animalOrders.TryAdd(animal.OrderType, 0))
			{
				animalOrders[animal.OrderType]--;
			}
		}
		else if (newOrder != 0)
		{
			animalOrders.TryAdd(newOrder, 0);
			animalOrders[newOrder]++;
			if (animal.OrderType != 0 && !animalOrders.TryAdd(animal.OrderType, 0))
			{
				animalOrders[animal.OrderType]--;
			}
		}
		if (newOrder == AnimalOrderType.Tame && !WorkerManager.WorkerExistsCheckJobAndSkill(SkillType.AnimalHandling, JobType.Animal, animal.Blueprint.MinTameSkill))
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("error_no_skilled_animal_worker"));
		}
		animal.SetOrder(newOrder);
		if (allAnimals.TryGetValue(animal, out var value))
		{
			value.OnMarkForOrder(newOrder);
		}
	}

	public void InstantiateAnimal(AnimalInstance animalInstance, bool afterLoading = false)
	{
		bool isEnabled;
		if (animalInstance.Blueprint == null)
		{
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(33, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\AnimalManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Blueprint not found for animal (");
				messageBuilder.AppendFormatted(animalInstance.Id);
				messageBuilder.AppendLiteral(")");
			}
			Log.Error(messageBuilder);
			GlobalSaveController.CurrentVillageData.RemoveAnimal(animalInstance);
			return;
		}
		GameObject byAddress = MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByAddress(animalInstance.Prefab);
		if (byAddress == null)
		{
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(33, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\AnimalManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Prefab (");
				messageBuilder.AppendFormatted(animalInstance.Prefab);
				messageBuilder.AppendLiteral(") not found for animal \"");
				messageBuilder.AppendFormatted(animalInstance.Id);
				messageBuilder.AppendLiteral("\"");
			}
			Log.Error(messageBuilder);
			GlobalSaveController.CurrentVillageData.RemoveAnimal(animalInstance);
			return;
		}
		animalInstance.Stats.SetOwner(animalInstance);
		animalInstance.Stats.SetOwnerOnStats();
		AnimalView component = UnityEngine.Object.Instantiate(byAddress, animalInstance.GetPosition(), Quaternion.Euler(new Vector3(0f, UnityEngine.Random.Range(0, 360), 0f)), base.transform).GetComponent<AnimalView>();
		if (component == null)
		{
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(57, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\AnimalManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("AnimalView component not found on prefab (");
				messageBuilder.AppendFormatted(animalInstance.Prefab);
				messageBuilder.AppendLiteral(") for animal \"");
				messageBuilder.AppendFormatted(animalInstance.Id);
				messageBuilder.AppendLiteral("\"");
			}
			Log.Error(messageBuilder);
			GlobalSaveController.CurrentVillageData.RemoveAnimal(animalInstance);
			return;
		}
		if (animalInstance.OrderType != 0)
		{
			animalOrders.TryAdd(animalInstance.OrderType, 0);
			animalOrders[animalInstance.OrderType]++;
		}
		allAnimals.TryAdd(animalInstance, component);
		animalsByType.GetOrAdd(animalInstance.AnimalType).Add(animalInstance);
		animalInstance.InitGoap();
		animalInstance.Spawn();
		if (animalInstance.ShouldResetOrder())
		{
			animalInstance.SetOrder(AnimalOrderType.None);
		}
		component.Setup(animalInstance);
		AddToAnimalsCount(animalInstance.Blueprint, 1);
		if (afterLoading)
		{
			animalInstance.SetupAfterLoading();
		}
		MonoSingleton<AnimalController>.Instance.OnSpawnAnimal(animalInstance);
	}

	private void AddToAnimalsCount(Animal animal, int toAdd)
	{
		if (!animalsCount.TryAdd(animal, toAdd))
		{
			animalsCount[animal] += toAdd;
		}
	}

	private void OnPensRefreshed()
	{
		MonoSingleton<PenDetection>.Instance.OnPensRefreshed -= OnPensRefreshed;
		foreach (KeyValuePair<AnimalInstance, AnimalView> allAnimal in allAnimals)
		{
			RefreshMarkForRoping(allAnimal.Key);
		}
	}

	private void AnimalsLifePhaseUpdate()
	{
		foreach (KeyValuePair<AnimalInstance, AnimalView> allAnimal in allAnimals)
		{
			if (allAnimal.Key.ShouldGoToNextPhase())
			{
				AnimalLifePhase nextLifePhase = allAnimal.Key.GetNextLifePhase();
				if (nextLifePhase == null)
				{
					animalsEndOfLife.Add(allAnimal.Key);
				}
				else
				{
					allAnimal.Key.SetLifePhase(nextLifePhase);
				}
			}
			else
			{
				allAnimal.Value.SetScale();
			}
		}
		if (animalsEndOfLife.Count <= 0)
		{
			return;
		}
		foreach (AnimalInstance item in animalsEndOfLife)
		{
			item.Stats.GetStat(StatType.Health).SetCurrent(0f);
		}
		animalsEndOfLife.Clear();
	}

	private void OnPenRemoved(AnimalPenInstance pen)
	{
		foreach (AnimalInstance key in allAnimals.Keys)
		{
			if (key?.GetNode()?.Region != null && key.AnimalType == AnimalType.Domestic && pen.ContainsRegion(key.GetNode().Region))
			{
				RefreshMarkForRoping(key);
			}
		}
	}

	private void OnPenRegionRefreshed(AnimalPenInstance pen)
	{
		PenDetection penDetection = MonoSingleton<PenDetection>.Instance;
		foreach (AnimalInstance key in allAnimals.Keys)
		{
			if (key.GetNode()?.Region != null && key.AnimalType == AnimalType.Domestic && penDetection.IsAnimalInOwnPen(key))
			{
				RefreshMarkForRoping(key);
			}
		}
	}

	private void OnCaravanReturnedOrCanceled(CaravanInstance caravanInstance)
	{
		if (caravanInstance?.Creatures == null)
		{
			return;
		}
		foreach (CreatureBase creature in caravanInstance.Creatures)
		{
			if (creature is AnimalInstance animalInstance)
			{
				RefreshMarkForRoping(animalInstance);
			}
		}
	}

	private void OnLadderConstructed(BaseBuildingInstance ladder)
	{
		if (ladder == null || ladder.HasDisposed)
		{
			return;
		}
		using PooledList<AnimalInstance> pooledList = ListPool<AnimalInstance>.GetJanitor(allAnimals.Keys);
		foreach (AnimalInstance item in pooledList)
		{
			Vec3Int lhs = item.GetGridPosition();
			Vec3Int rhs = ladder.GridDataPosition;
			if (lhs == rhs)
			{
				TryUnstuckAnimal(item);
			}
		}
	}

	private void Update()
	{
		FSpineAnimator.IsGloballyDisabled = FrustumCullingVisibleCounter > 20;
		FrustumCullingVisibleCounter = 0;
	}

	private void Start()
	{
		MonoSingleton<AnimalController>.Instance.FaceObjectEvent += OnFaceObject;
		MonoSingleton<AnimalController>.Instance.HealthDepletedEvent += OnAnimalDeath;
		MonoSingleton<AnimalController>.Instance.DieFormStarvationEvent += OnAnimalDeath;
		MonoSingleton<AnimalController>.Instance.MarkForOrderEvent += OnMarkAnimalForOrder;
		MonoSingleton<AnimationController>.Instance.TriggerAnimationEvent += OnTriggerAnimation;
		MonoSingleton<AnimationController>.Instance.ResetTriggersAnimationEvent += OnTriggersReset;
		MonoSingleton<AnimationController>.Instance.ForeQuitAnimationEvent += OnTriggerAnimation;
		MonoSingleton<CombatController>.Instance.DamageTakenEvent += OnDamageTaken;
		MonoSingleton<CombatController>.Instance.OnAgentKilledEvent += OnAgentKilled;
		MonoSingleton<CombatController>.Instance.DealGateDamageEvent += OnDealGateDamage;
		MonoSingleton<CombatController>.Instance.DealDrawbridgeDamageEvent += OnDealDrawbridgeDamage;
		MonoSingleton<WorldTimeManager>.Instance.QuarterHourUpdateEvent += AnimalsLifePhaseUpdate;
		MonoSingleton<PenDetection>.Instance.OnPensRefreshed += OnPensRefreshed;
		MonoSingleton<PenController>.Instance.OnPenRemovedEvent += OnPenRemoved;
		MonoSingleton<PenController>.Instance.OnPenRegionRefreshedEvent += OnPenRegionRefreshed;
		MonoSingleton<CaravanController>.Instance.CaravanFormingCanceledEvent += OnCaravanReturnedOrCanceled;
		MonoSingleton<CaravanController>.Instance.CaravanReturnedHomeEvent += OnCaravanReturnedOrCanceled;
		MonoSingleton<ConstructionController>.Instance.LadderConstructedEvent += OnLadderConstructed;
		AnimalType[] animalTypes = EnumValues.AnimalTypes;
		foreach (AnimalType key in animalTypes)
		{
			animalsByType[key] = new HashSet<AnimalInstance>();
		}
	}

	protected override void OnDestroy()
	{
		if (MonoSingleton<AnimalController>.IsInstantiated())
		{
			MonoSingleton<AnimalController>.Instance.FaceObjectEvent -= OnFaceObject;
			MonoSingleton<AnimalController>.Instance.HealthDepletedEvent -= OnAnimalDeath;
			MonoSingleton<AnimalController>.Instance.DieFormStarvationEvent -= OnAnimalDeath;
			MonoSingleton<AnimalController>.Instance.MarkForOrderEvent -= OnMarkAnimalForOrder;
		}
		if (MonoSingleton<AnimationController>.IsInstantiated())
		{
			MonoSingleton<AnimationController>.Instance.TriggerAnimationEvent -= OnTriggerAnimation;
			MonoSingleton<AnimationController>.Instance.ResetTriggersAnimationEvent -= OnTriggersReset;
			MonoSingleton<AnimationController>.Instance.ForeQuitAnimationEvent -= OnTriggerAnimation;
		}
		if (MonoSingleton<CombatController>.IsInstantiated())
		{
			MonoSingleton<CombatController>.Instance.DamageTakenEvent -= OnDamageTaken;
			MonoSingleton<CombatController>.Instance.OnAgentKilledEvent -= OnAgentKilled;
			MonoSingleton<CombatController>.Instance.DealGateDamageEvent -= OnDealGateDamage;
			MonoSingleton<CombatController>.Instance.DealDrawbridgeDamageEvent -= OnDealDrawbridgeDamage;
		}
		if (MonoSingleton<WorldTimeManager>.IsInstantiated())
		{
			MonoSingleton<WorldTimeManager>.Instance.QuarterHourUpdateEvent -= AnimalsLifePhaseUpdate;
		}
		if (MonoSingleton<PenDetection>.IsInstantiated())
		{
			MonoSingleton<PenDetection>.Instance.OnPensRefreshed -= OnPensRefreshed;
		}
		if (MonoSingleton<PenController>.IsInstantiated())
		{
			MonoSingleton<PenController>.Instance.OnPenRemovedEvent -= OnPenRemoved;
			MonoSingleton<PenController>.Instance.OnPenRegionRefreshedEvent -= OnPenRegionRefreshed;
		}
		if (MonoSingleton<CaravanController>.IsInstantiated())
		{
			MonoSingleton<CaravanController>.Instance.CaravanFormingCanceledEvent -= OnCaravanReturnedOrCanceled;
			MonoSingleton<CaravanController>.Instance.CaravanReturnedHomeEvent -= OnCaravanReturnedOrCanceled;
		}
		if (MonoSingleton<ConstructionController>.IsInstantiated())
		{
			MonoSingleton<ConstructionController>.Instance.LadderConstructedEvent -= OnLadderConstructed;
		}
		allAnimals.Clear();
		animalsByType.Clear();
		animalOrders.Clear();
		animalsCount.Clear();
		animalsEndOfLife.Clear();
		canBeRopedToPen.Clear();
		base.OnDestroy();
	}

	private void OnAgentKilled(IDamageDealAgent deal, IDamageTakingAgent take)
	{
		if (take is AnimalInstance { PetOwner: not null } animalInstance && deal is HumanoidInstance { WorkerBehaviour: not null } && animalInstance.PetOwner is HumanoidInstance humanoidInstance2 && humanoidInstance2.IsTrader() && humanoidInstance2.IsFriendlyFaction())
		{
			humanoidInstance2.Faction?.HitFromFriendly(-150f);
		}
	}

	private void OnDamageTaken(IDamageDealAgent deal, IDamageTakingAgent take, CombatHitInfo hitinfo)
	{
		if (take is AnimalInstance { PetOwner: not null } animalInstance && deal is HumanoidInstance { WorkerBehaviour: not null } && animalInstance.PetOwner is HumanoidInstance humanoidInstance2 && humanoidInstance2.IsTrader() && humanoidInstance2.IsFriendlyFaction())
		{
			humanoidInstance2.Faction?.HitFromFriendly(-20f);
		}
	}

	public void OnAnimalTypeChanged(AnimalType oldAnimalType, AnimalInstance animalInstance)
	{
		animalsByType.GetOrAdd(oldAnimalType).Remove(animalInstance);
		animalsByType.GetOrAdd(animalInstance.AnimalType).Add(animalInstance);
	}

	public bool HasHostileAnimals()
	{
		return animalsByType[AnimalType.WildAggressive].Count > 0;
	}

	private void OnDealGateDamage(DoorComponentInstance doorComponentInstance)
	{
		if (doorComponentInstance == null || doorComponentInstance.HasDisposed || doorComponentInstance.OwnerBuilding == null || doorComponentInstance.OwnerBuilding.HasDisposed)
		{
			return;
		}
		int y = doorComponentInstance.OwnerBuilding.GridDataPosition.y;
		using PooledHashSet<Vec3Int> pooledHashSet = HashSetPool<Vec3Int>.GetJanitor();
		foreach (Vec3Int position in doorComponentInstance.OwnerBuilding.Positions)
		{
			if (position.y == y)
			{
				pooledHashSet.Add(position);
			}
		}
		DoorComponentBlueprint blueprint = doorComponentInstance.Blueprint;
		using PooledList<AnimalInstance> pooledList = ListPool<AnimalInstance>.GetJanitor();
		foreach (AnimalInstance key in allAnimals.Keys)
		{
			Vec3Int gridPosition = key.GetGridPosition();
			if (pooledHashSet.Contains(gridPosition) && !(UnityEngine.Random.value > blueprint.ChanceToHurt) && key.Stats?.GetStat(StatType.Health) != null)
			{
				pooledList.Add(key);
			}
		}
		foreach (AnimalInstance item in pooledList)
		{
			StatInstance stat = item.Stats.GetStat(StatType.Health);
			float current3 = stat.Current - doorComponentInstance.Blueprint.AnimalDamage;
			stat.SetCurrent(current3);
		}
	}

	private void OnDealDrawbridgeDamage(DrawbridgeComponent drawbridgeComponent)
	{
		if (drawbridgeComponent == null)
		{
			return;
		}
		DoorComponentInstance componentInstance = drawbridgeComponent.DoorComponent.ComponentInstance;
		if (componentInstance == null || componentInstance.HasDisposed || componentInstance.OwnerBuilding == null || componentInstance.OwnerBuilding.HasDisposed)
		{
			return;
		}
		float damagePercent = componentInstance.DamagePercent;
		foreach (AnimalInstance key in allAnimals.Keys)
		{
			Vec3Int gridPosition = key.GetGridPosition();
			if (drawbridgeComponent.DrawbridgePositions.Contains(gridPosition))
			{
				StatInstance statInstance = key.Stats?.GetStat(StatType.Health);
				if (statInstance != null)
				{
					float current2 = statInstance.Current - componentInstance.Blueprint.AnimalDamage * damagePercent;
					statInstance.SetCurrent(current2);
				}
			}
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
