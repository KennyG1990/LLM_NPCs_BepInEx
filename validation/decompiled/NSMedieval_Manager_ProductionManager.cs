using System;
using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using Models.Production;
using NSEipix;
using NSEipix.Base;
using NSMedieval.BuildingComponents;
using NSMedieval.Enums;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Model;
using NSMedieval.Research;
using NSMedieval.Resources;
using NSMedieval.State;
using NSMedieval.State.WorkerJobs;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Village.Map.Pathfinding;

namespace NSMedieval.Manager;

public class ProductionManager : MonoSingleton<ProductionManager>
{
	private readonly HashSet<ProductionSystemInstance> productionSystems = new HashSet<ProductionSystemInstance>();

	private readonly Dictionary<JobType, HashSet<ProductionInstance>> all = new Dictionary<JobType, HashSet<ProductionInstance>>();

	private readonly Dictionary<JobType, HashSet<ProductionInstance>> available = new Dictionary<JobType, HashSet<ProductionInstance>>();

	private float globalSpeedMultiplier = 1f;

	public List<ProductionInstance> CopyBuffer;

	public Dictionary<JobType, HashSet<ProductionInstance>> All => all;

	public Dictionary<JobType, HashSet<ProductionInstance>> AllAvailable => available;

	public float GlobalSpeedMultiplier
	{
		get
		{
			return globalSpeedMultiplier;
		}
		set
		{
			globalSpeedMultiplier = value;
		}
	}

	public event Action<ProductionInstance> ProductionStateChangedEvent;

	public void RegisterSystem(ProductionSystemInstance systemInstance)
	{
		productionSystems.Add(systemInstance);
		systemInstance.OnDisposedEvent += OnSystemDisposed;
		systemInstance.OnCurrentProductionChangedEvent += OnSystemCurrentProductionChanged;
		systemInstance.OnNewProductionEvent += OnNewProductionInstanceEvent;
		foreach (ProductionInstance production in systemInstance.Productions)
		{
			AddToDict(production, all);
			production.OnDisposedEvent += OnProductionInstanceDestroyed;
		}
	}

	public (ProductionInstance, Vec3Int) FindAvailableProduction(JobType type, HumanoidInstance humanoid)
	{
		if (!available.TryGetValue(type, out var value) || humanoid.WorkerBehaviour == null)
		{
			return (null, Vec3Int.zero);
		}
		using PooledList<ProductionInstance> pooledList = value.ToPooledListJanitor();
		float num = float.MaxValue;
		ProductionInstance productionInstance = null;
		Vec3Int item = Vec3Int.zero;
		PathLengthEstimator pathLengthEstimator = humanoid.Map.PathLengthEstimator;
		foreach (ProductionInstance item2 in pooledList)
		{
			if (!item2.HasDisposed && item2.Blueprint.HasSkillsRequired(humanoid) && item2.CurrentStep != null)
			{
				ProductionComponentInstance productionComponentInstance = item2?.OwnerSystem?.Owner;
				if (productionComponentInstance != null && !productionComponentInstance.IsDisposedOrNull() && !productionComponentInstance.OwnerBuilding.HasConstructionOrder() && humanoid.WorkerBehaviour.IsJobActive(item2.Blueprint.JobType) && item2.OwnerProductionComponentInstance.TemperatureAllowsProduction() && !item2.OwnerProductionComponentInstance.OwnerBuilding.UnderWater() && !item2.OwnerProductionComponentInstance.IsOnFire && CommonGoalMethods.CheckPrisonConditions(humanoid, item2.OwnerProductionComponentInstance?.GetRoom()) && CanPrepareProduction(humanoid, item2) && pathLengthEstimator.TryEstimateInstant(humanoid, item2, out var shortestEstimatedPath, out var reachablePosition) && shortestEstimatedPath < num)
				{
					num = shortestEstimatedPath;
					productionInstance = item2;
					item = reachablePosition;
				}
			}
		}
		if (productionInstance == null)
		{
			return (null, Vec3Int.zero);
		}
		return (productionInstance, item);
	}

	private static bool CanPrepareProduction(HumanoidInstance worker, ProductionInstance production)
	{
		ProductionStepInstance productionStepInstance = production?.CurrentStep;
		if (productionStepInstance == null || productionStepInstance.IsCompleted || productionStepInstance.Type == ProductionStepType.PassiveProduce)
		{
			return false;
		}
		if (!production.Blueprint.HasSkillsRequired(worker))
		{
			return false;
		}
		if (production.OwnerCreatureId != 0 && production.OwnerCreatureId != worker.UniqueId)
		{
			return false;
		}
		if (production.Blueprint.RequiredSkills != null && production.Blueprint.RequiredSkills.Count > 0)
		{
			SkillLevelPair skillLevelPair = production.Blueprint.RequiredSkills.First();
			if (production.SkillLevelRange != null && !production.SkillLevelRange.InRange(((IProductionAgent)worker).GetSkillLevel(skillLevelPair.Key)))
			{
				return false;
			}
		}
		if (!PathfinderUtil.IsPathPossibleProduction(worker, production))
		{
			return false;
		}
		return MonoSingleton<ReservationManager>.Instance.CanReserve(production.OwnerProductionComponentInstance, worker);
	}

	public void UpdateAllProductionStates()
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "skill_refresh", delegate
		{
			foreach (KeyValuePair<JobType, HashSet<ProductionInstance>> item in all)
			{
				foreach (ProductionInstance item2 in item.Value)
				{
					if (!item2.HasDisposed && item2.State == ProductionState.NoSkilledWorker)
					{
						item2.UpdateState();
					}
				}
			}
		}, 0.5f);
	}

	public void ProductionStateChanged(ProductionInstance productionInstance)
	{
		this.ProductionStateChangedEvent?.Invoke(productionInstance);
	}

	private void OnSystemCurrentProductionChanged(ProductionSystemInstance system, ProductionInstance oldProduction)
	{
		if (oldProduction != null)
		{
			RemoveFromAvailable(oldProduction);
		}
		ProductionInstance currentProduction = system.CurrentProduction;
		if (currentProduction != null)
		{
			TrackCurrentProduction(currentProduction);
		}
	}

	private void OnProductionInstanceStateChanged(ProductionInstance instance, ProductionState oldState)
	{
		if (instance.State != oldState)
		{
			RemoveFromAvailable(instance);
			if (instance.IsCurrentProduction() && !instance.HasDisposed)
			{
				TrackCurrentProduction(instance);
			}
		}
	}

	private void OnSystemDisposed(IGameDisposable disposable)
	{
		ProductionSystemInstance instance = (ProductionSystemInstance)disposable;
		productionSystems.Remove(instance);
		foreach (KeyValuePair<JobType, HashSet<ProductionInstance>> item in all)
		{
			item.Value?.RemoveWhere((ProductionInstance item) => item.OwnerSystem == instance);
		}
		foreach (KeyValuePair<JobType, HashSet<ProductionInstance>> item2 in available)
		{
			item2.Value?.RemoveWhere((ProductionInstance item) => item.OwnerSystem == instance);
		}
	}

	private void TrackCurrentProduction(ProductionInstance production)
	{
		production.OnStateChangedEvent -= OnProductionInstanceStateChanged;
		production.OnStateChangedEvent += OnProductionInstanceStateChanged;
		AddToDict(production, all);
		switch (production.State)
		{
		case ProductionState.WaitingForResources:
		case ProductionState.WaitingForWorker:
			AddToDict(production, available);
			return;
		case ProductionState.Paused:
		case ProductionState.TargetReached:
		case ProductionState.NoSkilledWorker:
		case ProductionState.InProgress:
			return;
		}
		bool isEnabled;
		FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(65, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionManager.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Production: ");
			messageBuilder.AppendFormatted(production.BlueprintId);
			messageBuilder.AppendLiteral(" at pos: ");
			messageBuilder.AppendFormatted(production.OwnerProductionComponentInstance.GetGridPosition());
			messageBuilder.AppendLiteral(" in invalid state(");
			messageBuilder.AppendFormatted(production.State);
			messageBuilder.AppendLiteral("), and can not be tracked.");
		}
		Log.Error(messageBuilder);
	}

	private void RemoveFromAvailable(ProductionInstance production)
	{
		JobType jobType = production.Blueprint.JobType;
		if (available.TryGetValue(jobType, out var value))
		{
			value.Remove(production);
		}
	}

	private void OnProductionInstanceDestroyed(IDisposable item)
	{
		ProductionInstance productionInstance = (ProductionInstance)item;
		productionInstance.OnDisposedEvent -= OnProductionInstanceDestroyed;
		RemoveFromAvailable(productionInstance);
		if (all.TryGetValue(productionInstance.Blueprint.JobType, out var value))
		{
			value.Remove(productionInstance);
			productionInstance.OnStateChangedEvent -= OnProductionInstanceStateChanged;
		}
	}

	private void OnNewProductionInstanceEvent(ProductionSystemInstance system, ProductionInstance production)
	{
		AddToDict(production, all);
		production.OnDisposedEvent += OnProductionInstanceDestroyed;
	}

	private static void AddToDict(ProductionInstance production, Dictionary<JobType, HashSet<ProductionInstance>> dict)
	{
		if (!dict.TryGetValue(production.Blueprint.JobType, out var value))
		{
			value = new HashSet<ProductionInstance>();
			dict.Add(production.Blueprint.JobType, value);
		}
		if (!value.Contains(production))
		{
			value.Add(production);
		}
	}

	private void OnAllPilesRecount()
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "all_piles_recount", delegate
		{
			if (!MonoSingleton<ProductionManager>.IsInstantiated())
			{
				return;
			}
			foreach (KeyValuePair<JobType, HashSet<ProductionInstance>> item in all)
			{
				foreach (ProductionInstance item2 in item.Value)
				{
					if (item2.Mode == ProductionMode.Until && item2.State != ProductionState.InProgress)
					{
						item2.UpdateState();
					}
				}
			}
		}, 1f);
	}

	private void OnResourcePileDisposed(ResourcePileInstance pile)
	{
		OnAllPilesRecount();
	}

	private void OnActiveResearchEvent(ResearchNodeInstance node, bool value1, bool value2)
	{
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "research_spent", delegate
		{
			if (!MonoSingleton<ProductionManager>.IsInstantiated())
			{
				return;
			}
			foreach (KeyValuePair<JobType, HashSet<ProductionInstance>> item in all)
			{
				foreach (ProductionInstance item2 in item.Value)
				{
					if (item2.Blueprint.JobType == JobType.Research && item2.Mode == ProductionMode.Until)
					{
						item2.UpdateState();
					}
				}
			}
		}, 1f);
	}

	private void Start()
	{
		MonoSingleton<ResourcePileController>.Instance.AllPilesCountEvent += OnAllPilesRecount;
		MonoSingleton<ResourcePileController>.Instance.DestroyPileEvent += OnResourcePileDisposed;
		MonoSingleton<ResearchController>.Instance.ActivateResearchEvent += OnActiveResearchEvent;
	}

	protected override void OnDestroy()
	{
		CopyBuffer.Clear();
		CopyBuffer = null;
		if (MonoSingleton<ResourcePileController>.IsInstantiated())
		{
			MonoSingleton<ResourcePileController>.Instance.AllPilesCountEvent -= OnAllPilesRecount;
			MonoSingleton<ResourcePileController>.Instance.DestroyPileEvent -= OnResourcePileDisposed;
		}
		if (MonoSingleton<ResearchController>.IsInstantiated())
		{
			MonoSingleton<ResearchController>.Instance.ActivateResearchEvent -= OnActiveResearchEvent;
		}
		base.OnDestroy();
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
