using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Controller;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using JetBrains.Annotations;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.Components;
using NSMedieval.Construction;
using NSMedieval.Controllers;
using NSMedieval.Enums;
using NSMedieval.Goap;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.MovableBuildings;
using NSMedieval.Repository;
using NSMedieval.Resources;
using NSMedieval.State;
using NSMedieval.StatsSystem;
using NSMedieval.StorageUniversal;
using NSMedieval.Terrain;
using NSMedieval.Testing.Autoplay;
using NSMedieval.Types;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Views.Resources;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using NSMedieval.Village.Map.Pathfinding;
using NSMedieval.Water;
using UnityEngine;

namespace NSMedieval.Manager;

public class ResourcePileManager : MonoSingleton<ResourcePileManager>
{
	private const int MaxSpawnRange = 100;

	private const int MaxTeleportRange = 16;

	private Thread mainThread;

	private readonly Dictionary<ResourcePileInstance, ResourcePileView> spawnedPiles = new Dictionary<ResourcePileInstance, ResourcePileView>();

	private readonly List<ResourcePileInstance> spawnedPileInstances = new List<ResourcePileInstance>();

	private readonly Dictionary<Resource, HashSet<ResourcePileInstance>> blueprintInstanceDictionary = new Dictionary<Resource, HashSet<ResourcePileInstance>>();

	private readonly Dictionary<FactionOwnership, HashSet<ResourcePileInstance>> pilesByFaction = new Dictionary<FactionOwnership, HashSet<ResourcePileInstance>>();

	private readonly Dictionary<ResourceCategory, HashSet<ResourcePileInstance>> placedOnStorageByCategory = new Dictionary<ResourceCategory, HashSet<ResourcePileInstance>>();

	private readonly Dictionary<Resource, HashSet<ResourcePileInstance>> placedOnStorageByBlueprint = new Dictionary<Resource, HashSet<ResourcePileInstance>>();

	private readonly List<ResourcePileInstance> tempHaulingDisabledPiles = new List<ResourcePileInstance>();

	private readonly object blueprintInstanceDictionaryLock = new object();

	private readonly object placedOnStorageByCategoryLock = new object();

	private readonly object placedOnStorageByBlueprintLock = new object();

	private readonly object pilesByFactionLock = new object();

	private readonly object tempHaulingDisabledPilesLock = new object();

	private readonly Dictionary<Vec3Int, ResourcePileInstance> pilesByGridPosOnGround = new Dictionary<Vec3Int, ResourcePileInstance>();

	private readonly Dictionary<ResourceCategory, HashSet<ResourcePileInstance>> categoryInstanceDictionary = new Dictionary<ResourceCategory, HashSet<ResourcePileInstance>>();

	private readonly object categoryInstanceDictionaryLock = new object();

	private readonly Dictionary<Vec3Int, HashSet<ResourcePileInstance>> pilesByGridPos = new Dictionary<Vec3Int, HashSet<ResourcePileInstance>>();

	private readonly object pilesByGridPosLock = new object();

	[NonSerialized]
	private ResourcePileInstance lastSpawnedPile;

	private readonly List<ResourceInstance> startingResources = new List<ResourceInstance>();

	private readonly ConcurrentBag<ResourcePileIndicatorView> indicatorsToUpdate = new ConcurrentBag<ResourcePileIndicatorView>();

	private readonly List<HumanCarcassPileInstance> carcassesMarkedForStripping = new List<HumanCarcassPileInstance>();

	public IEnumerable<KeyValuePair<ResourcePileInstance, ResourcePileView>> AllPiles => spawnedPiles;

	public IEnumerable<ResourcePileInstance> AllPileInstances => spawnedPiles.Keys;

	public IReadOnlyList<ResourcePileInstance> SpawnedPileInstances => spawnedPileInstances;

	public ResourcePileInstance LastSpawnedPile => lastSpawnedPile;

	public Dictionary<Vec3Int, ResourcePileInstance> PilesByGridPosOnGround => pilesByGridPosOnGround;

	public List<ResourceInstance> StartingResources => startingResources;

	public List<HumanCarcassPileInstance> CarcassesMarkedForStripping => carcassesMarkedForStripping;

	public ConcurrentBag<ResourcePileIndicatorView> IndicatorsToUpdate => indicatorsToUpdate;

	public int GetPilesCount(Resource blueprint = null)
	{
		if (blueprint == null)
		{
			return spawnedPiles.Count;
		}
		lock (blueprintInstanceDictionaryLock)
		{
			if (!blueprintInstanceDictionary.ContainsKey(blueprint))
			{
				blueprintInstanceDictionary.Add(blueprint, new HashSet<ResourcePileInstance>());
			}
			return blueprintInstanceDictionary[blueprint].Count;
		}
	}

	public bool ResourcePileWithProtoIdExists(string protoId)
	{
		foreach (Resource item in Repository<ResourceRepository, Resource>.Instance.GetAllResourcesByProtoId(protoId))
		{
			if (GetPilesCount(item) > 0)
			{
				return true;
			}
		}
		return false;
	}

	public int GetStoredBlueprintAmount(Resource blueprint)
	{
		lock (placedOnStorageByBlueprint)
		{
			int num = 0;
			if (!placedOnStorageByBlueprint.TryGetValue(blueprint, out var value))
			{
				return num;
			}
			foreach (ResourcePileInstance item in value)
			{
				if (!item.HasDisposed)
				{
					num += (item.GetStorage()?.GetSingleResource()?.Amount).GetValueOrDefault();
				}
			}
			return num;
		}
	}

	public void UpdatePlacedOnStorageBlueprintDictionary(ResourcePileInstance pile, IStorage storage)
	{
		if (pile == null)
		{
			return;
		}
		Resource blueprint = pile.Blueprint;
		if (blueprint == null)
		{
			return;
		}
		if (storage == null)
		{
			lock (placedOnStorageByBlueprintLock)
			{
				if (placedOnStorageByBlueprint.TryGetValue(blueprint, out var value))
				{
					value.Remove(pile);
				}
				return;
			}
		}
		if (pile.HasDisposed)
		{
			return;
		}
		lock (placedOnStorageByBlueprintLock)
		{
			if (!placedOnStorageByBlueprint.ContainsKey(blueprint))
			{
				placedOnStorageByBlueprint.Add(blueprint, new HashSet<ResourcePileInstance>());
			}
			placedOnStorageByBlueprint[blueprint].Add(pile);
		}
	}

	public void UpdatePlacedOnStorageCategoryDictionary(ResourcePileInstance pile, IStorage storage)
	{
		if (pile == null)
		{
			return;
		}
		ResourceCategory category = pile.Blueprint.Category;
		if (storage == null)
		{
			ResourceCategory[] allResourceCategories = EnumValues.AllResourceCategories;
			foreach (ResourceCategory resourceCategory in allResourceCategories)
			{
				if ((category & resourceCategory) == 0 || resourceCategory == ResourceCategory.None)
				{
					continue;
				}
				lock (placedOnStorageByCategoryLock)
				{
					if (placedOnStorageByCategory.TryGetValue(resourceCategory, out var value))
					{
						value.Remove(pile);
					}
				}
			}
		}
		else
		{
			if (pile.HasDisposed)
			{
				return;
			}
			ResourceCategory[] allResourceCategories = EnumValues.AllResourceCategories;
			foreach (ResourceCategory resourceCategory2 in allResourceCategories)
			{
				if (!category.HasFlag(resourceCategory2) || resourceCategory2 == ResourceCategory.None)
				{
					continue;
				}
				lock (placedOnStorageByCategoryLock)
				{
					if (placedOnStorageByCategory.TryGetValue(resourceCategory2, out var value2))
					{
						value2.Add(pile);
						continue;
					}
					placedOnStorageByCategory.Add(resourceCategory2, new HashSet<ResourcePileInstance> { pile });
				}
			}
		}
	}

	public int GetStoredCategoryAmount(ResourceCategory category)
	{
		lock (placedOnStorageByCategoryLock)
		{
			int num = 0;
			if (!placedOnStorageByCategory.TryGetValue(category, out var value))
			{
				return num;
			}
			foreach (ResourcePileInstance item in value)
			{
				if (!item.HasDisposed)
				{
					int num2 = (int)(item.Blueprint?.Nutrition ?? 1f);
					int num3 = num;
					Storage storage = item.GetStorage();
					num = num3 + ((storage == null) ? null : (storage.GetSingleResource()?.Amount * num2)).GetValueOrDefault();
				}
			}
			return num;
		}
	}

	public int RemoveResources(Resource blueprint, int maxCount, bool skipNotLootable = false, IPathfindingAgent reachableByAgent = null, WalkableModel walkableModelOverride = null)
	{
		int num = 0;
		lock (blueprintInstanceDictionaryLock)
		{
			if (!blueprintInstanceDictionary.TryGetValue(blueprint, out var value))
			{
				return num;
			}
			using PooledList<(ResourcePileInstance, int)> pooledList = ListPool<(ResourcePileInstance, int)>.GetJanitor();
			foreach (ResourcePileInstance item in value)
			{
				ResourceInstance resourceInstance = item?.GetStoredResource();
				if (resourceInstance == null)
				{
					continue;
				}
				if (skipNotLootable)
				{
					UniversalStorage instanceStorage = item.InstanceStorage;
					if (instanceStorage != null)
					{
						UniversalStorageBlueprint blueprint2 = instanceStorage.Blueprint;
						if ((object)blueprint2 != null && !blueprint2.IsEnemyLootable)
						{
							continue;
						}
					}
				}
				if (reachableByAgent == null || PathfinderUtil.IsPathPossible(reachableByAgent, item, walkableModelOverride))
				{
					int num2 = 0;
					num += resourceInstance.Count.Amount;
					if (num > maxCount)
					{
						num2 = maxCount - num;
						num = maxCount;
					}
					else
					{
						num2 = resourceInstance.Count.Amount;
					}
					pooledList.Add((item, num2));
					if (num >= maxCount)
					{
						break;
					}
				}
			}
			foreach (var (resourcePileInstance, num3) in pooledList)
			{
				if (num3 > 0)
				{
					resourcePileInstance.GetStorage().Take(resourcePileInstance.Blueprint, num3, returnTakenResource: false);
				}
			}
			return num;
		}
	}

	public void CategoryInstanceDictionaryTryGetValue(ResourceCategory category, [MustDisposeResource] out PooledHashSet<ResourcePileInstance> resourcePileInstances)
	{
		lock (categoryInstanceDictionaryLock)
		{
			categoryInstanceDictionary.TryGetValue(category, out var value);
			if (value == null)
			{
				resourcePileInstances = default(PooledHashSet<ResourcePileInstance>);
			}
			else
			{
				resourcePileInstances = value.ToPooledHashSetJanitor();
			}
		}
	}

	public void BlueprintInstancesSafeOperation(Resource blueprint, Action<IEnumerable<ResourcePileInstance>> operation)
	{
		lock (blueprintInstanceDictionaryLock)
		{
			if (!(blueprint == null))
			{
				if (!blueprintInstanceDictionary.ContainsKey(blueprint))
				{
					blueprintInstanceDictionary.Add(blueprint, new HashSet<ResourcePileInstance>());
				}
				operation?.Invoke(blueprintInstanceDictionary[blueprint]);
			}
		}
	}

	public TResult BlueprintInstancesSafeOperation<TResult>(Resource blueprint, Func<IEnumerable<ResourcePileInstance>, TResult> operation)
	{
		lock (blueprintInstanceDictionaryLock)
		{
			if (blueprint == null || operation == null)
			{
				return default(TResult);
			}
			if (!blueprintInstanceDictionary.TryGetValue(blueprint, out var value))
			{
				value = new HashSet<ResourcePileInstance>();
				blueprintInstanceDictionary.Add(blueprint, value);
			}
			return operation(value);
		}
	}

	public TResult BlueprintInstancesSafeOperation<TState, TResult>(Resource blueprint, TState state, Func<IEnumerable<ResourcePileInstance>, TState, TResult> operation)
	{
		lock (blueprintInstanceDictionaryLock)
		{
			if (blueprint == null || operation == null)
			{
				return default(TResult);
			}
			if (!blueprintInstanceDictionary.TryGetValue(blueprint, out var value))
			{
				value = new HashSet<ResourcePileInstance>();
				blueprintInstanceDictionary.Add(blueprint, value);
			}
			return operation(value, state);
		}
	}

	[MustDisposeResource]
	public PooledHashSet<ResourcePileInstance> GetAllPiles(Resource blueprint)
	{
		lock (blueprintInstanceDictionaryLock)
		{
			if (blueprint == null)
			{
				return default(PooledHashSet<ResourcePileInstance>);
			}
			if (!blueprintInstanceDictionary.ContainsKey(blueprint))
			{
				blueprintInstanceDictionary.Add(blueprint, new HashSet<ResourcePileInstance>());
			}
			return blueprintInstanceDictionary[blueprint].ToPooledHashSetJanitor();
		}
	}

	public int GetTotalAmount(Resource blueprint)
	{
		int num = 0;
		lock (blueprintInstanceDictionaryLock)
		{
			if (blueprint == null)
			{
				return 0;
			}
			if (blueprintInstanceDictionary.ContainsKey(blueprint))
			{
				foreach (ResourcePileInstance item in blueprintInstanceDictionary[blueprint])
				{
					if (!item.HasDisposed)
					{
						num += (item.GetStorage()?.GetSingleResource()?.Amount).GetValueOrDefault();
					}
				}
			}
		}
		return num;
	}

	public void CategoryInstancesSafeOperation(ResourceCategory resourceCategory, Action<IEnumerable<ResourcePileInstance>> operation)
	{
		lock (categoryInstanceDictionaryLock)
		{
			categoryInstanceDictionary.TryGetValue(resourceCategory, out var value);
			operation?.Invoke(value);
		}
	}

	public TResult CategoryInstancesSafeOperation<TState, TResult>(ResourceCategory resourceCategory, TState state, Func<IEnumerable<ResourcePileInstance>, TState, TResult> operation)
	{
		lock (categoryInstanceDictionaryLock)
		{
			if (operation == null)
			{
				return default(TResult);
			}
			categoryInstanceDictionary.TryGetValue(resourceCategory, out var value);
			return operation(value, state);
		}
	}

	public IEnumerable<ResourcePileInstance> IterateCategoryPiles(ResourceCategory category)
	{
		lock (categoryInstanceDictionaryLock)
		{
			if (!categoryInstanceDictionary.TryGetValue(category, out var value))
			{
				yield break;
			}
			foreach (ResourcePileInstance item in value)
			{
				yield return item;
			}
		}
	}

	public void GetAllowedPilesAreaIDs(Resource blueprint, in ISet<uint> pilesAreaIDs)
	{
		pilesAreaIDs.Clear();
		if (blueprint == null)
		{
			return;
		}
		lock (blueprintInstanceDictionaryLock)
		{
			if (!blueprintInstanceDictionary.ContainsKey(blueprint))
			{
				blueprintInstanceDictionary.Add(blueprint, new HashSet<ResourcePileInstance>());
			}
			foreach (ResourcePileInstance item in blueprintInstanceDictionary[blueprint])
			{
				if (!item.IsForbidden && !item.HasDisposed && !item.PlacedOnAnimalFeeder)
				{
					uint num = item.GetNode()?.Area ?? 0;
					if (num != 0)
					{
						pilesAreaIDs.Add(num);
					}
				}
			}
		}
	}

	public HumanCarcassPileView SpawnPile(CreatureBase creature)
	{
		if (!(creature is HumanoidInstance humanoidInstance))
		{
			Log.Error("Called spawn pile with creature type which is not Humanoid or Humanoid type!", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
			return null;
		}
		Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(humanoidInstance.IsNpc() ? "enemy_carcass" : "human_carcass");
		CarcassResourceInstance resource = new CarcassResourceInstance(creature, byID, 1)
		{
			ForbidOnInit = MonoSingleton<AnimalManager>.Instance.IsTooCloseToAggressiveAnimal(creature.GetPosition())
		};
		HumanCarcassPileView humanCarcassPileView = (HumanCarcassPileView)SpawnPile(resource, creature.GetPosition());
		if (humanCarcassPileView != null)
		{
			humanCarcassPileView.HumanCarcassPileInstance?.SaveInventory(creature);
		}
		return humanCarcassPileView;
	}

	public BuildingPileView SpawnPile(Resource blueprint, Vector3 worldPosition, BaseBuildingInstance binderBuilding)
	{
		MoveBuildingResourceInstance resource = new MoveBuildingResourceInstance(blueprint, 1, binderBuilding);
		return (BuildingPileView)SpawnPile(resource, worldPosition);
	}

	public BuildingPileView SpawnPile(Resource blueprint, Vector3 worldPosition, string buildingID)
	{
		MoveBuildingResourceInstance resource = new MoveBuildingResourceInstance(blueprint, 1, buildingID);
		return (BuildingPileView)SpawnPile(resource, worldPosition);
	}

	public BuildingPileView SpawnEnemyPile(Resource blueprint, Vector3 worldPosition, string buildingID)
	{
		MoveBuildingResourceInstance resource = new MoveBuildingResourceInstance(blueprint, 1, buildingID);
		return (BuildingPileView)SpawnEnemyPile(resource, worldPosition);
	}

	public BuildingPileView SpawnPile(MoveBuildingResourceInstance resource, Vector3 worldPosition, string buildingID)
	{
		resource.SetBuildingId(buildingID);
		return (BuildingPileView)SpawnPile(resource, worldPosition);
	}

	public ResourcePileView SpawnPileAnimalHarvest(ResourceInstance resource, Vector3 worldPosition, AnimalInstance owner)
	{
		ResourcePileView view = null;
		SpawnResource(resource, worldPosition, delegate(ResourceInstance outResource, Vec3Int outGridPos, ResourcePileInstance existingPile)
		{
			if (existingPile != null)
			{
				view = GetView(existingPile);
			}
			else
			{
				ResourcePileInstance pile = ResourcePileFactory.ProducePile(outResource, GridUtils.GetWorldPosition(outGridPos));
				view = SpawnPile(pile);
				if (view == null)
				{
					bool isEnabled;
					FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(54, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
					if (isEnabled)
					{
						messageBuilder.AppendLiteral("Couldn't spawn view for ");
						messageBuilder.AppendFormatted(resource.BlueprintId);
						messageBuilder.AppendLiteral(" from harvesting ");
						messageBuilder.AppendFormatted(owner.GetFullName());
						messageBuilder.AppendLiteral(" at position ");
						messageBuilder.AppendFormatted(owner.GetPosition());
					}
					Log.Error(messageBuilder);
				}
			}
		});
		if (view != null)
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				if (!(view == null))
				{
					view.GetComponent<HideResource>().TryForceHide(MonoSingleton<World>.Instance.LayerLevel);
				}
			});
		}
		return view;
	}

	public ResourcePileView SpawnPile(ResourceInstance resource, Vector3 worldPosition, bool forbidOnInit = false, float tempDisableHaulingSeconds = 0f)
	{
		ResourcePileView view = null;
		SpawnResource(resource, worldPosition, delegate(ResourceInstance outResource, Vec3Int outGridPos, ResourcePileInstance existingPile)
		{
			if (existingPile != null)
			{
				view = GetView(existingPile);
			}
			else
			{
				ResourcePileInstance resourcePileInstance = ResourcePileFactory.ProducePile(outResource, GridUtils.GetWorldPosition(outGridPos));
				resourcePileInstance.IsForbidden = forbidOnInit;
				resourcePileInstance.HaulingDisabledSecondsRemaining = tempDisableHaulingSeconds;
				view = SpawnPile(resourcePileInstance);
			}
		});
		if (view != null)
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				if (!(view == null))
				{
					view.GetComponent<HideResource>().TryForceHide(MonoSingleton<World>.Instance.LayerLevel);
				}
			});
		}
		return view;
	}

	public ResourcePileView SpawnEnemyPile(ResourceInstance resource, Vector3 worldPosition, bool forbidOnInit = false)
	{
		ResourcePileView view = null;
		SpawnResource(resource, worldPosition, delegate(ResourceInstance outResource, Vec3Int outGridPos, ResourcePileInstance existingPile)
		{
			if (existingPile != null)
			{
				view = GetView(existingPile);
			}
			else
			{
				ResourcePileInstance resourcePileInstance = ResourcePileFactory.ProducePile(outResource, GridUtils.GetWorldPosition(outGridPos));
				resourcePileInstance.SetFaction(FactionOwnership.Enemy);
				resourcePileInstance.IsForbidden = forbidOnInit;
				view = SpawnPile(resourcePileInstance);
			}
		});
		if (view != null)
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				if (!(view == null))
				{
					view.GetComponent<HideResource>().TryForceHide(MonoSingleton<World>.Instance.LayerLevel);
				}
			});
		}
		return view;
	}

	public ResourcePileView TeleportPile(ResourceInstance resource, Vec3Int gridPos, bool forbidOnInit = false)
	{
		Vector3 position = gridPos.ToVector3World();
		ResourcePileView view = null;
		TeleportPile(resource, position, delegate(ResourceInstance outResource, Vec3Int outGridPos, ResourcePileInstance existingPile)
		{
			if (existingPile != null)
			{
				view = GetView(existingPile);
			}
			else
			{
				ResourcePileInstance resourcePileInstance = ResourcePileFactory.ProducePile(outResource, GridUtils.GetWorldPosition(outGridPos));
				resourcePileInstance.IsForbidden = forbidOnInit;
				view = SpawnPile(resourcePileInstance);
			}
		});
		if (view != null)
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				if (!(view == null))
				{
					view.GetComponent<HideResource>().TryForceHide(MonoSingleton<World>.Instance.LayerLevel);
				}
			});
		}
		return view;
	}

	public ResourcePileView TeleportPile(ResourceInstance resource, Vector3 worldPosition, bool forbidOnInit = false)
	{
		ResourcePileView view = null;
		TeleportPile(resource, worldPosition, delegate(ResourceInstance outResource, Vec3Int outGridPos, ResourcePileInstance existingPile)
		{
			if (existingPile != null)
			{
				view = GetView(existingPile);
			}
			else
			{
				ResourcePileInstance resourcePileInstance = ResourcePileFactory.ProducePile(outResource, GridUtils.GetWorldPosition(outGridPos));
				resourcePileInstance.IsForbidden = forbidOnInit;
				view = SpawnPile(resourcePileInstance);
			}
		});
		if (view != null)
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
			{
				if (!(view == null))
				{
					view.GetComponent<HideResource>().TryForceHide(MonoSingleton<World>.Instance.LayerLevel);
				}
			});
		}
		return view;
	}

	public ResourcePileView SpawnPile(ResourcePileInstance pile)
	{
		bool isEnabled;
		if (pile.HasDisposed)
		{
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(51, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Pile ");
				messageBuilder.AppendFormatted(pile.BlueprintId);
				messageBuilder.AppendLiteral(" at position ");
				messageBuilder.AppendFormatted(pile.GridDataPosition);
				messageBuilder.AppendLiteral(" is disposed while spawning view.");
			}
			Log.Warning(messageBuilder);
			OnPileDisposed(pile);
			return null;
		}
		if (pile.Blueprint == null)
		{
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(72, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Disposing pile while spawning view because blueprint is null. Position ");
				messageBuilder.AppendFormatted(pile.GridDataPosition);
				messageBuilder.AppendLiteral(" ");
				messageBuilder.AppendFormatted(pile.BlueprintId);
			}
			Log.Warning(messageBuilder);
			OnPileDisposed(pile);
			return null;
		}
		if (pile.GetStoredResource() == null)
		{
			return null;
		}
		if (spawnedPiles.TryGetValue(pile, out var value))
		{
			return value;
		}
		ResourcePileView resourcePileView = null;
		if (!pile.DoNotCreateView)
		{
			resourcePileView = ResourcePileFactory.ProducePileView(pile);
			if (resourcePileView == null)
			{
				return null;
			}
		}
		lastSpawnedPile = pile;
		pile.OnDisposedEvent += OnPileDisposed;
		spawnedPiles.Add(pile, resourcePileView);
		if (!spawnedPileInstances.Contains(pile))
		{
			spawnedPileInstances.Add(pile);
		}
		lock (blueprintInstanceDictionaryLock)
		{
			if (blueprintInstanceDictionary.TryGetValue(pile.Blueprint, out var value2))
			{
				value2.Add(pile);
			}
			else
			{
				blueprintInstanceDictionary.Add(pile.Blueprint, new HashSet<ResourcePileInstance> { pile });
			}
		}
		lock (pilesByFactionLock)
		{
			if (pilesByFaction.TryGetValue(pile.FactionOwnership, out var value3))
			{
				value3.Add(pile);
			}
			else
			{
				pilesByFaction.Add(pile.FactionOwnership, new HashSet<ResourcePileInstance> { pile });
			}
		}
		ResourceCategory category = pile.Blueprint.Category;
		ResourceCategory[] allResourceCategories = EnumValues.AllResourceCategories;
		foreach (ResourceCategory resourceCategory in allResourceCategories)
		{
			if (!category.HasFlag(resourceCategory))
			{
				continue;
			}
			lock (categoryInstanceDictionaryLock)
			{
				if (categoryInstanceDictionary.TryGetValue(resourceCategory, out var value4))
				{
					value4.Add(pile);
					continue;
				}
				categoryInstanceDictionary.Add(resourceCategory, new HashSet<ResourcePileInstance> { pile });
			}
		}
		if (!pile.IsPlacedOnStorageBuilding)
		{
			if (pilesByGridPosOnGround.ContainsKey(pile.GridDataPosition))
			{
				ResourceInstance resourceInstance = pile.GetStoredResource().Clone();
				pile.Dispose();
				OnPileDisposed(pile);
				Vector3 pileWorldPosition = pile.WorldPosition;
				MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(delegate
				{
					SpawnPile(resourceInstance, pileWorldPosition);
				});
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(46, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Multiple piles at same pos: ");
					messageBuilder.AppendFormatted(pile.GridDataPosition);
					messageBuilder.AppendLiteral(" Respawning pile: ");
					messageBuilder.AppendFormatted(resourceInstance.BlueprintId);
				}
				Log.Warning(messageBuilder);
				return null;
			}
			pilesByGridPosOnGround.Add(pile.GridDataPosition, pile);
			VillageManager.ActiveVillage.Map.AddToTheWorld(pile);
			if (pile.HasDisposed)
			{
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(53, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Created view for disposed pile. Pile ID: ");
					messageBuilder.AppendFormatted(pile.BlueprintId);
					messageBuilder.AppendLiteral(". Position: ");
					messageBuilder.AppendFormatted(pile.GridDataPosition);
				}
				Log.Warning(messageBuilder);
			}
		}
		lock (pilesByGridPosLock)
		{
			if (pilesByGridPos.ContainsKey(pile.GridDataPosition))
			{
				pilesByGridPos[pile.GridDataPosition].Add(pile);
			}
			else
			{
				pilesByGridPos.Add(pile.GridDataPosition, new HashSet<ResourcePileInstance> { pile });
			}
		}
		if (pile.HaulingDisabledSecondsRemaining > 0f)
		{
			FVLogDebugInterpolationHandler messageBuilder2 = new FVLogDebugInterpolationHandler(22, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
			if (isEnabled)
			{
				messageBuilder2.AppendLiteral("Hauling Disabled ");
				messageBuilder2.AppendFormatted(pile.HaulingDisabledSecondsRemaining);
				messageBuilder2.AppendLiteral(" for ");
				messageBuilder2.AppendFormatted(pile);
			}
			Log.Debug(messageBuilder2);
			lock (tempHaulingDisabledPilesLock)
			{
				tempHaulingDisabledPiles.Add(pile);
			}
		}
		MonoSingleton<ResourcePileController>.Instance.OnPilePreSpawnEvent(pile);
		MonoSingleton<ResourcePileController>.Instance.OnPileSpawned(pile);
		return resourcePileView;
	}

	public ResourcePileView GetView(ResourcePileInstance pile)
	{
		if (!spawnedPiles.TryGetValue(pile, out var value))
		{
			return null;
		}
		return value;
	}

	public IEnumerable<ResourcePileView> GetView(Func<bool, ResourcePileView> condition)
	{
		return from item in spawnedPiles
			where condition(item.Value)
			select item.Value;
	}

	public int CountPiles(Func<ResourcePileInstance, bool> condition)
	{
		return spawnedPiles.Keys.Count(condition.Invoke);
	}

	public ResourcePileInstance GetPileByGridPosition(Vec3Int position)
	{
		if (!pilesByGridPosOnGround.TryGetValue(position, out var value))
		{
			return null;
		}
		return value;
	}

	public void SpawnProfilePiles()
	{
		Log.Info("SpawnProfilePiles", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
		VillageMap map = VillageManager.ActiveVillage.Map;
		IEnumerable<ResourcePileInstance> enumerable = map.GetWorldObjectsList<ResourcePileInstance>(GridDataType.ResourcePile);
		bool flag = false;
		foreach (ResourcePileInstance item in enumerable.Where((ResourcePileInstance item) => item?.Blueprint == null || item.GetStoredResource() == null))
		{
			VillageManager.ActiveVillage.Map.RemoveFromWorld(item);
			flag = true;
		}
		if (flag)
		{
			enumerable = map.GetWorldObjects(GridDataType.ResourcePile).Cast<ResourcePileInstance>();
		}
		foreach (ResourcePileInstance item2 in new List<ResourcePileInstance>(enumerable))
		{
			item2.ReInstantiate();
			bool isEnabled;
			if (!item2.HasDisposed)
			{
				SpawnPile(item2);
			}
			else
			{
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(47, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Pile ");
					messageBuilder.AppendFormatted(item2.BlueprintId);
					messageBuilder.AppendLiteral(" at position ");
					messageBuilder.AppendFormatted(item2.GridDataPosition);
					messageBuilder.AppendLiteral(" was disposed during loading.");
				}
				Log.Warning(messageBuilder);
				OnPileDisposed(item2);
			}
			CarcassResourceInstance storedCarcass = item2.GetStoredCarcass();
			if (storedCarcass != null && !item2.HasDisposed && storedCarcass.Owner == null)
			{
				FVLogErrorInterpolationHandler messageBuilder2 = new FVLogErrorInterpolationHandler(53, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
				if (isEnabled)
				{
					messageBuilder2.AppendLiteral("Carcass pile at ");
					messageBuilder2.AppendFormatted(item2.GridDataPosition);
					messageBuilder2.AppendLiteral(" corrupted (owner is null), disposing");
				}
				Log.Error(messageBuilder2);
				item2.Dispose();
			}
		}
		MonoSingleton<ResourcePileController>.Instance.OnSavePilesLoaded();
	}

	public void DisposeAllPiles()
	{
		List<ResourcePileInstance> list = new List<ResourcePileInstance>();
		list.AddRange(spawnedPiles.Keys);
		foreach (ResourcePileInstance item in list)
		{
			item.Dispose();
		}
	}

	public void KillAllPilesGameplay()
	{
		List<ResourcePileInstance> list = new List<ResourcePileInstance>();
		list.AddRange(spawnedPiles.Keys);
		foreach (ResourcePileInstance item in list)
		{
			item.Stats.GetStat(StatType.Health).SetCurrent(0f);
		}
	}

	public void DisposePilesById(string id)
	{
		List<ResourcePileInstance> list = new List<ResourcePileInstance>();
		list.AddRange(spawnedPiles.Keys.Where((ResourcePileInstance e) => e.BlueprintId.Equals(id)));
		foreach (ResourcePileInstance item in list)
		{
			item.Dispose();
		}
	}

	public void ForceDisposePile(IGameDisposable disposable)
	{
		if (disposable != null)
		{
			OnPileDisposed(disposable);
		}
	}

	private void OnPileDisposed(IGameDisposable disposable)
	{
		if (LoadingController.IsSceneTransition || TestManager.DontDisposeResource)
		{
			return;
		}
		ResourcePileInstance resourcePileInstance = (ResourcePileInstance)disposable;
		if (lastSpawnedPile == resourcePileInstance)
		{
			lastSpawnedPile = null;
		}
		if (Thread.CurrentThread != mainThread)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(37, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Pile ");
				messageBuilder.AppendFormatted(resourcePileInstance.BlueprintId);
				messageBuilder.AppendLiteral("@");
				messageBuilder.AppendFormatted(resourcePileInstance.GridDataPosition);
				messageBuilder.AppendLiteral(" disposed from non-main thread.");
			}
			Log.Warning(messageBuilder);
		}
		ResourcePileView view = GetView(resourcePileInstance);
		if (view != null)
		{
			view.Dispose();
			UnityEngine.Object.Destroy(view.gameObject);
		}
		spawnedPiles.Remove(resourcePileInstance);
		if (spawnedPileInstances.Contains(resourcePileInstance))
		{
			spawnedPileInstances.Remove(resourcePileInstance);
		}
		lock (pilesByFactionLock)
		{
			if (pilesByFaction.TryGetValue(resourcePileInstance.FactionOwnership, out var value))
			{
				value.Remove(resourcePileInstance);
			}
		}
		if (resourcePileInstance.Blueprint != null)
		{
			lock (blueprintInstanceDictionaryLock)
			{
				if (blueprintInstanceDictionary.TryGetValue(resourcePileInstance.Blueprint, out var value2))
				{
					value2.Remove(resourcePileInstance);
				}
			}
			ResourceCategory category = resourcePileInstance.Blueprint.Category;
			ResourceCategory[] allResourceCategories = EnumValues.AllResourceCategories;
			foreach (ResourceCategory resourceCategory in allResourceCategories)
			{
				if (!category.HasFlag(resourceCategory))
				{
					continue;
				}
				lock (categoryInstanceDictionaryLock)
				{
					if (categoryInstanceDictionary.TryGetValue(resourceCategory, out var value3))
					{
						value3.Remove(resourcePileInstance);
					}
				}
			}
		}
		if (!resourcePileInstance.IsPlacedOnStorageBuilding && pilesByGridPosOnGround.ContainsKey(resourcePileInstance.GridDataPosition) && pilesByGridPosOnGround[resourcePileInstance.GridDataPosition] == resourcePileInstance)
		{
			pilesByGridPosOnGround.Remove(resourcePileInstance.GridDataPosition);
		}
		lock (pilesByGridPosLock)
		{
			if (pilesByGridPos.ContainsKey(resourcePileInstance.GridDataPosition))
			{
				pilesByGridPos[resourcePileInstance.GridDataPosition].Remove(resourcePileInstance);
			}
		}
		VillageManager.ActiveVillage.Map?.RemoveFromWorld(resourcePileInstance);
		if (MonoSingleton<ResourcePileController>.IsInstantiated())
		{
			MonoSingleton<ResourcePileController>.Instance.OnPileDestroyed(resourcePileInstance);
		}
	}

	private void Start()
	{
		mainThread = Thread.CurrentThread;
		MonoSingleton<FloraController>.Instance.SpawnCropEvent += OnSpawnCrop;
		MonoSingleton<World>.Instance.MapLoadedEvent += OnMapLoaded;
		MonoSingleton<LoadingController>.Instance.MainSceneLeavingEvent += OnMainSceneLeaving;
		MonoSingleton<RaidController>.Instance.OnWorkersWonRaidEvent += OnWorkersWonRaid;
		MonoSingleton<RaidController>.Instance.OnWorkersLostRaidEvent += OnWorkersLostRaid;
		MonoSingleton<RaidController>.Instance.RaidTieEvent += OnRaidTie;
		MonoSingleton<SceneController>.Instance.Tick += Tick;
	}

	private void Tick(float deltaTime)
	{
		lock (tempHaulingDisabledPilesLock)
		{
			for (int num = tempHaulingDisabledPiles.Count - 1; num >= 0; num--)
			{
				ResourcePileInstance resourcePileInstance = tempHaulingDisabledPiles[num];
				if (resourcePileInstance == null || resourcePileInstance.HasDisposed)
				{
					tempHaulingDisabledPiles.RemoveAt(num);
				}
				else
				{
					resourcePileInstance.HaulingDisabledSecondsRemaining -= deltaTime;
					if (resourcePileInstance.HaulingDisabledSecondsRemaining <= 0f)
					{
						bool isEnabled;
						FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(29, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
						if (isEnabled)
						{
							messageBuilder.AppendLiteral("Hauling Disabled expired for ");
							messageBuilder.AppendFormatted(resourcePileInstance);
						}
						Log.Debug(messageBuilder);
						resourcePileInstance.HaulingDisabledSecondsRemaining = 0f;
						tempHaulingDisabledPiles.RemoveAt(num);
					}
				}
			}
		}
		if (indicatorsToUpdate.IsEmpty)
		{
			return;
		}
		bool isEnabled2;
		FVLogInfoInterpolationHandler messageBuilder2 = new FVLogInfoInterpolationHandler(20, 1, out isEnabled2, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
		if (isEnabled2)
		{
			messageBuilder2.AppendLiteral("Updating ");
			messageBuilder2.AppendFormatted(indicatorsToUpdate.Count);
			messageBuilder2.AppendLiteral(" indicators");
		}
		Log.Info(messageBuilder2);
		ResourcePileIndicatorView result;
		while (indicatorsToUpdate.TryTake(out result))
		{
			if (!(result == null))
			{
				result.UpdateMeshRenderer();
			}
		}
	}

	private void OnMainSceneLeaving()
	{
		if (MonoSingleton<LoadingController>.IsInstantiated())
		{
			MonoSingleton<LoadingController>.Instance.MainSceneLeavingEvent -= OnMainSceneLeaving;
		}
	}

	private void OnMapLoaded(bool fromSave)
	{
		VillageManager.ActiveVillage.Map.WaterManager.WaterLevelChangedEvent += OnWaterLevelChanged;
		MonoSingleton<TaskController>.Instance.WaitForUnscaled(1f).Then(delegate
		{
			foreach (ResourcePileInstance spawnedPileInstance in spawnedPileInstances)
			{
				spawnedPileInstance.WaterLevelChanged();
			}
		});
		MonoSingleton<CombatController>.Instance.DealDrawbridgeDamageEvent += OnDealDrawbridgeDamage;
		if (MonoSingleton<GlobalSaveController>.Instance.CorruptedCarcassEquipment == null)
		{
			return;
		}
		foreach (ResourceInstance item in MonoSingleton<GlobalSaveController>.Instance.CorruptedCarcassEquipment)
		{
			item?.InitAfterLoadPile();
		}
		MonoSingleton<GlobalSaveController>.Instance.CorruptedCarcassEquipment.Clear();
	}

	private void OnWorkersWonRaid()
	{
		ConvertEnemyPilesToPlayerPiles();
	}

	private void OnWorkersLostRaid()
	{
		ConvertEnemyPilesToPlayerPiles();
	}

	private void OnRaidTie()
	{
		ConvertEnemyPilesToPlayerPiles();
	}

	private void ConvertEnemyPilesToPlayerPiles()
	{
		lock (pilesByFactionLock)
		{
			if (!pilesByFaction.TryGetValue(FactionOwnership.Enemy, out var value))
			{
				return;
			}
			using PooledList<ResourcePileInstance> pooledList = value.ToPooledListJanitor();
			foreach (ResourcePileInstance item in pooledList)
			{
				value.Remove(item);
				item.SetFaction(FactionOwnership.Player);
				pilesByFaction[FactionOwnership.Player].Add(item);
			}
		}
	}

	protected override void OnDestroy()
	{
		if (MonoSingleton<LoadingController>.IsInstantiated())
		{
			MonoSingleton<LoadingController>.Instance.MainSceneLeavingEvent -= OnMainSceneLeaving;
		}
		if (MonoSingleton<FloraController>.IsInstantiated())
		{
			MonoSingleton<FloraController>.Instance.SpawnCropEvent -= OnSpawnCrop;
		}
		if (MonoSingleton<World>.IsInstantiated())
		{
			MonoSingleton<World>.Instance.MapLoadedEvent -= OnMapLoaded;
		}
		if (MonoSingleton<VillageManager>.IsInstantiated() && VillageManager.ActiveVillage?.Map?.WaterManager != null)
		{
			VillageManager.ActiveVillage.Map.WaterManager.WaterLevelChangedEvent -= OnWaterLevelChanged;
		}
		if (MonoSingleton<RaidController>.IsInstantiated())
		{
			MonoSingleton<RaidController>.Instance.OnWorkersWonRaidEvent -= OnWorkersWonRaid;
			MonoSingleton<RaidController>.Instance.OnWorkersLostRaidEvent -= OnWorkersLostRaid;
			MonoSingleton<RaidController>.Instance.RaidTieEvent -= OnRaidTie;
		}
		if (MonoSingleton<CombatController>.IsInstantiated())
		{
			MonoSingleton<CombatController>.Instance.DealDrawbridgeDamageEvent -= OnDealDrawbridgeDamage;
		}
		if (MonoSingleton<SceneController>.IsInstantiated())
		{
			MonoSingleton<SceneController>.Instance.Tick -= Tick;
		}
		foreach (KeyValuePair<ResourcePileInstance, ResourcePileView> allPile in AllPiles)
		{
			allPile.Value?.OnLeavingMainScene();
		}
		spawnedPiles.Clear();
		spawnedPileInstances.Clear();
		lock (blueprintInstanceDictionaryLock)
		{
			foreach (HashSet<ResourcePileInstance> value in blueprintInstanceDictionary.Values)
			{
				value.Clear();
			}
			blueprintInstanceDictionary.Clear();
		}
		lock (placedOnStorageByCategoryLock)
		{
			foreach (HashSet<ResourcePileInstance> value2 in placedOnStorageByCategory.Values)
			{
				value2.Clear();
			}
			placedOnStorageByCategory.Clear();
		}
		pilesByGridPosOnGround.Clear();
		lock (categoryInstanceDictionaryLock)
		{
			foreach (HashSet<ResourcePileInstance> value3 in categoryInstanceDictionary.Values)
			{
				value3.Clear();
			}
			categoryInstanceDictionary.Clear();
		}
		lastSpawnedPile = null;
		lock (pilesByGridPosLock)
		{
			foreach (HashSet<ResourcePileInstance> value4 in pilesByGridPos.Values)
			{
				value4.Clear();
			}
			pilesByGridPos.Clear();
		}
		lock (tempHaulingDisabledPilesLock)
		{
			tempHaulingDisabledPiles.Clear();
		}
		base.OnDestroy();
	}

	private void OnSpawnCrop(PlantMapResourceInstance crop)
	{
		MapNode mapNode = crop?.GetNode();
		if (mapNode != null)
		{
			GetPileByGridPosition(mapNode.Position)?.HandleTeleportation(mapNode);
		}
	}

	private void OnWaterLevelChanged(HashSet<int> nodeIndexes, HashSet<int> nodeNeighborsIndices)
	{
		VillageMap map = VillageManager.ActiveVillage.Map;
		foreach (int nodeIndex in nodeIndexes)
		{
			OnWaterLevelChanged(nodeIndex, map);
		}
	}

	private void OnWaterLevelChanged(int nodeIndex, VillageMap map)
	{
		if (map == null || nodeIndex < 0 || nodeIndex >= map.GridSpaceData.Length)
		{
			return;
		}
		MapNode mapNode = map.GridSpaceData[nodeIndex];
		if (mapNode == null)
		{
			return;
		}
		foreach (ResourcePileInstance item in mapNode.GetWorldObjects(GridDataType.ResourcePile).OfType<ResourcePileInstance>())
		{
			item.WaterLevelChanged();
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
		DoorComponentBlueprint blueprint = componentInstance.Blueprint;
		foreach (Vec3Int drawbridgePosition in drawbridgeComponent.DrawbridgePositions)
		{
			if (pilesByGridPosOnGround.TryGetValue(drawbridgePosition, out var value) && !(UnityEngine.Random.value > blueprint.ChanceToHurt))
			{
				StatInstance statInstance = value.Stats?.GetStat(StatType.Health);
				if (statInstance != null)
				{
					float current2 = statInstance.Current - blueprint.PileDamage * damagePercent;
					statInstance.SetCurrent(current2);
				}
			}
		}
	}

	public void SpawnResourcesFromVillageMap()
	{
		if (GlobalSaveController.CurrentVillageData.FirstEnter)
		{
			return;
		}
		VillageMap map = VillageManager.ActiveVillage.Map;
		foreach (PlantMapResourceInstance worldObjects in map.GetWorldObjectsList<PlantMapResourceInstance>(GridDataType.PlantMapResource))
		{
			if (!worldObjects.HasDisposed)
			{
				if (worldObjects.CurrentPhase == -1 || worldObjects.GetNode().GetNodeBelow().IsVoxelAir())
				{
					worldObjects.Dispose();
				}
				else
				{
					MonoSingleton<FloraController>.Instance.ReinstanceResource(worldObjects);
				}
			}
		}
		foreach (FishMapResourceInstance worldObjects2 in map.GetWorldObjectsList<FishMapResourceInstance>(GridDataType.FishMapResource))
		{
			MonoSingleton<FishResourceController>.Instance.ReinstanceResource(worldObjects2);
		}
	}

	public static void TeleportPile(ResourceInstance resource, Vector3 position, Action<ResourceInstance, Vec3Int, ResourcePileInstance> spawnCallback)
	{
		if (resource == null || resource.HasDisposed || resource.Blueprint == null)
		{
			return;
		}
		resource = DoPileSpawnFloodFill(resource, position, spawnCallback, 16);
		if (resource == null || resource.Amount <= 0)
		{
			return;
		}
		resource = DoPileSpawnFloodFill(resource, new Vector3(position.x, position.y - 1f, position.z), spawnCallback, 16);
		if (resource == null || resource.Amount <= 0)
		{
			return;
		}
		resource = DoPileSpawnFloodFill(resource, new Vector3(position.x, position.y + 1f, position.z), spawnCallback, 16);
		if (resource == null || resource.Amount <= 0)
		{
			return;
		}
		resource = DoPileSpawnFloodFill(resource, new Vector3(position.x, position.y - 2f, position.z), spawnCallback, 16);
		if (resource != null && resource.Amount > 0)
		{
			resource = DoPileSpawnFloodFill(resource, new Vector3(position.x, position.y + 2f, position.z), spawnCallback, 16);
			if (resource != null && resource.Amount > 0)
			{
				SpawnResource(resource, position, spawnCallback);
			}
		}
	}

	public static void SpawnResource(ResourceInstance resource, Vector3 position, Action<ResourceInstance, Vec3Int, ResourcePileInstance> spawnCallback)
	{
		if (resource == null || resource.HasDisposed || resource.Blueprint == null || resource.Stats.GetStat(StatType.Health).Current == 0f)
		{
			return;
		}
		resource = DoPileSpawnFloodFill(resource, position, spawnCallback, 100);
		if (resource != null && resource.Amount > 0)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(98, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Could not find free node to spawn ");
				messageBuilder.AppendFormatted(resource.BlueprintId);
				messageBuilder.AppendLiteral(" resource pile on! Start position: ");
				messageBuilder.AppendFormatted(position);
				messageBuilder.AppendLiteral(", left over resource.Amount: ");
				messageBuilder.AppendFormatted(resource.Amount);
			}
			Log.Warning(messageBuilder);
		}
	}

	private static ResourceInstance DoPileSpawnFloodFill(ResourceInstance resource, Vector3 position, Action<ResourceInstance, Vec3Int, ResourcePileInstance> spawnCallback, int spawnRange)
	{
		VillageMap map = VillageManager.ActiveVillage.Map;
		Vec3Int gridPosition = GridUtils.GetGridPosition(position);
		MapNode node = map.GetNode(in gridPosition);
		bool flag = false;
		bool flag2 = false;
		if (node != null)
		{
			flag = node.WaterDepthLevel == WaterDepthLevel.High || node.WaterDepthLevel == WaterDepthLevel.Medium;
		}
		foreach (MapNode item in FloodFillUtil.IterateFloodFillConnections(node, spawnRange))
		{
			if (item == null || (!flag && (item.WaterDepthLevel == WaterDepthLevel.High || item.WaterDepthLevel == WaterDepthLevel.Medium)))
			{
				continue;
			}
			GridDataType dataType = item.DataType;
			if ((dataType & GridDataType.PlantMapResource) == GridDataType.PlantMapResource)
			{
				continue;
			}
			if ((dataType & GridDataType.ResourcePile) != 0)
			{
				ResourcePileInstance resourcePileInstance = null;
				foreach (WorldObject worldObject in item.WorldObjects)
				{
					if (worldObject is ResourcePileInstance { IsPlacedOnStorageBuilding: false } resourcePileInstance2)
					{
						resourcePileInstance = resourcePileInstance2;
						break;
					}
				}
				if (resourcePileInstance != null)
				{
					if (resourcePileInstance.HasDisposed || resourcePileInstance.Blueprint == null)
					{
						continue;
					}
					ResourceInstance storedResource = resourcePileInstance.GetStoredResource();
					if (storedResource != null && storedResource.Amount <= 0)
					{
						continue;
					}
					if (resourcePileInstance.Blueprint == resource.Blueprint)
					{
						TryAddToResourcePile(resourcePileInstance, resource);
					}
					if (resource.Amount <= 0)
					{
						if (!flag2)
						{
							spawnCallback(resourcePileInstance.GetStoredResource(), resourcePileInstance.GridDataPosition, resourcePileInstance);
						}
						break;
					}
					continue;
				}
			}
			if (dataType.HasFlag(GridDataType.ProductionBuilding) || dataType.HasFlag(GridDataType.Furniture) || dataType.HasFlag(GridDataType.Roof) || dataType.HasFlag(GridDataType.OthersUnfinished) || dataType.HasFlag(GridDataType.FurnitureGate) || dataType.HasFlag(GridDataType.Stairs) || dataType.HasFlag(GridDataType.Trap) || dataType.HasFlag(GridDataType.Grave))
			{
				continue;
			}
			if ((dataType & GridDataType.BuildingUnfinished) == GridDataType.BuildingUnfinished)
			{
				BaseBuildingInstance building = item.Map.BuildingsManagerMain.GetBuilding(item.Position, ConstructionPhase.Foundation);
				if (building != null && building.BuildingType != BuildingType.Floor)
				{
					continue;
				}
			}
			if ((dataType & GridDataType.BuildingFinished) == GridDataType.BuildingFinished)
			{
				BaseBuildingInstance building2 = item.Map.BuildingsManagerMain.GetBuilding(item.Position, ConstructionPhase.Finished);
				if (building2 == null || building2.Blueprint.BuildingType.HasFlag(BuildingType.Floor) || building2.Blueprint.BuildingType.HasFlag(BuildingType.Beam))
				{
					Vec3Int position2 = item.Position;
					flag2 = true;
					resource = SpawnNewPile(resource, position2, spawnCallback);
					if (resource == null || resource.Amount <= 0)
					{
						break;
					}
				}
				continue;
			}
			Vec3Int a = item.Position;
			Vec3Int b = Vec3Int.down;
			Vec3Int gridPosition2 = a + b;
			if (item.IsLayerRamp())
			{
				continue;
			}
			MapNode node2 = map.GetNode(in gridPosition2);
			if (node2 == null || node2.IsLayerRamp())
			{
				continue;
			}
			if (!MonoSingleton<GroundManager>.Instance.GroundExists(gridPosition2))
			{
				BaseBuildingInstance building3 = item.Map.BuildingsManagerMain.GetBuilding(gridPosition2, ConstructionPhase.Finished);
				if (building3 == null || !building3.Blueprint.IsWallTypeBuildingWithVerticalStability())
				{
					continue;
				}
			}
			if (!(item.VoxelType != null))
			{
				Vec3Int position3 = item.Position;
				flag2 = true;
				resource = SpawnNewPile(resource, position3, spawnCallback);
				if (resource == null || resource.Amount <= 0)
				{
					break;
				}
			}
		}
		return resource;
	}

	private static int TryAddToResourcePile(ResourcePileInstance pileToAddTo, ResourceInstance resource)
	{
		if (pileToAddTo == null || pileToAddTo.HasDisposed || pileToAddTo.Blueprint == null || pileToAddTo.GetStoredResource() == null)
		{
			return 0;
		}
		Storage storage = pileToAddTo.GetStorage();
		if (storage.CanStore(resource) && resource.BlueprintId == pileToAddTo.BlueprintId)
		{
			return resource.TransferTo(storage);
		}
		return 0;
	}

	private static ResourceInstance SpawnNewPile(ResourceInstance resource, Vec3Int position, Action<ResourceInstance, Vec3Int, ResourcePileInstance> spawnCallback)
	{
		if (resource == null)
		{
			return null;
		}
		if (resource.Amount > resource.Blueprint.StackingLimit)
		{
			ResourceInstance resourceInstance = resource.Clone(0);
			resource.TransferTo(resourceInstance, resource.Blueprint.StackingLimit);
			spawnCallback(resourceInstance, position, null);
			return resource;
		}
		spawnCallback(resource, position, null);
		return null;
	}

	public void InstantiateStartingResourcePiles()
	{
		int num = 0;
		if (startingResources.Count == 0)
		{
			AddResources();
		}
		while (num < startingResources.Count)
		{
			Vector2Int nextPosition = MonoSingleton<StartPositionManager>.Instance.GetNextPosition();
			if (MonoSingleton<SlopeManager>.Instance.IsSlopeAt(nextPosition.x, nextPosition.y) || MonoSingleton<FloraRegrowController>.Instance.GetPositionUsedBy(nextPosition.x, nextPosition.y) != null)
			{
				continue;
			}
			Vector3 position = new Vector3(nextPosition.x, MonoSingleton<Heightmap>.Instance.GetHeightAt(nextPosition) * World.MapBlockHeight, nextPosition.y);
			int num2 = 0;
			string blueprintId = startingResources[num].BlueprintId;
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(blueprintId);
			int num3 = startingResources[num].Amount;
			while (num3 > 0)
			{
				int amount;
				if (num3 > byID.StackingLimit)
				{
					num3 -= byID.StackingLimit;
					amount = byID.StackingLimit;
				}
				else
				{
					amount = num3;
					num3 = 0;
				}
				MonoSingleton<World>.Instance.LimitPositionToRange(ref position);
				ResourceInstance resource = new ResourceInstance(byID, amount)
				{
					ForbidOnInit = true
				};
				ResourcePileView resourcePileView = MonoSingleton<ResourcePileManager>.Instance.SpawnPile(resource, position);
				if (resourcePileView != null)
				{
					resourcePileView.ResourcePileInstance.IsForbidden = true;
				}
				num2++;
			}
			num++;
		}
	}

	private void AddResources()
	{
		Scenario scenario = GlobalSaveController.CurrentVillageData.Scenario;
		List<SerializableIdValuePair> list = new List<SerializableIdValuePair>();
		list.AddRange(scenario.StartingResources);
		list.AddRange(scenario.StartingEquipment);
		SerializableIdValuePair[] startingStructurePiles = scenario.StartingStructurePiles;
		foreach (SerializableIdValuePair serializableIdValuePair in startingStructurePiles)
		{
			if (Repository<ResourceRepository, Resource>.Instance.GetByID(serializableIdValuePair.Id) != null)
			{
				list.Add(serializableIdValuePair);
			}
		}
		foreach (SerializableIdValuePair item in list)
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(item.Id);
			if (byID == null)
			{
				bool isEnabled;
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(49, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Gameplay\\Resource\\ResourcePileManager.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("ScenarioGameSetup: resource with id '");
					messageBuilder.AppendFormatted(item.Id);
					messageBuilder.AppendLiteral("' not found.");
				}
				Log.Warning(messageBuilder);
			}
			else
			{
				startingResources.Add(new ResourceInstance(byID, (int)item.Value));
			}
		}
	}

	public void SpawnResourcesForSecondMap()
	{
		foreach (ResourceInstance resource2 in MonoSingleton<TravelManager>.Instance.Resources)
		{
			Vector3 position = MonoSingleton<TravelManager>.Instance.GetSpawnPoint(SpawnPointType.FriendlyResources).ToVector3World();
			string blueprintId = resource2.BlueprintId;
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(blueprintId);
			int num = resource2.Amount;
			while (num > 0)
			{
				int amount;
				if (num > byID.StackingLimit)
				{
					num -= byID.StackingLimit;
					amount = byID.StackingLimit;
				}
				else
				{
					amount = num;
					num = 0;
				}
				MonoSingleton<World>.Instance.LimitPositionToRange(ref position);
				ResourceInstance resource = new ResourceInstance(byID, amount)
				{
					ForbidOnInit = false
				};
				ResourcePileView resourcePileView = SpawnPile(resource, position);
				if (resourcePileView != null)
				{
					resourcePileView.ResourcePileInstance.IsForbidden = false;
				}
			}
		}
		MonoSingleton<TravelManager>.Instance.Resources.Clear();
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
