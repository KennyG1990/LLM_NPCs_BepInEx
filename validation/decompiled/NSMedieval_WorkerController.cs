using System;
using System.Linq;
using FoxyVoxel.Logging;
using NSEipix.Base;
using NSMedieval.Controllers;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Resources;
using NSMedieval.State;
using NSMedieval.State.WorkerJobs;
using NSMedieval.StatsSystem;
using NSMedieval.Types;
using UnityEngine;

namespace NSMedieval;

public class WorkerController : MonoSingleton<WorkerController>
{
	public delegate void HandlePresetUpdate(ManageGroupPreset preset);

	public delegate void HumanoidAtPositionHandler(HumanoidInstance humanoidInstance, Vector3 position);

	public delegate void HumanoidHandler(HumanoidInstance humanoidInstance);

	public delegate void HumanoidHourChangeHandler(HumanoidInstance humanoidInstance, HourType hourType);

	public delegate void HumanoidStringTriggerHandler(HumanoidInstance humanoidInstance, string id, Transform socket);

	public delegate void HumanoidTriggerHandler(HumanoidInstance humanoidInstance);

	public event Action<int> WorkerDiedEvent;

	public event Action<int, int> WorkerFaintedEvent;

	public event HumanoidAtPositionHandler FaceObjectEvent;

	public event HumanoidHandler CreateWorkerEvent;

	public event HumanoidHandler RemoveWorkerEvent;

	public event HumanoidHandler SpawnWorkerEvent;

	public event HumanoidStringTriggerHandler PullOutToolEvent;

	public event HumanoidTriggerHandler HideToolEvent;

	public event HumanoidHandler WorkerBanished;

	public event HandlePresetUpdate EquipmentPresetUpdatedEvent;

	public event HumanoidHourChangeHandler HourTypeChangeEvent;

	public event Action<HumanoidInstance, Transform> FaceObjectTransformEvent;

	public event Action<HumanoidInstance, Transform> LookAtTransformEvent;

	public event Action<HumanoidInstance, Vector3> SetEulerAngleEvent;

	public event HumanoidHandler FoodStorageChangedEvent;

	public event HumanoidHandler MedicineStorageChangedEvent;

	public event Action WorkerCountChangedEvent;

	public event Action<HumanoidInstance> WorkerNameChangedEvent;

	public event Action<HumanoidInstance, JobType, int> JobPriorityChangedEvent;

	private void Start()
	{
		MonoSingleton<GoapController>.Instance.OnGoalStartedEvent += OnGoalStarted;
		MonoSingleton<GoapController>.Instance.OnGoalEndedEvent += OnGoalEnded;
		MonoSingleton<LifeController>.Instance.DieFromStarvationEvent += OnDieFromStarvation;
		MonoSingleton<LifeController>.Instance.StartedStarvingEvent += OnStartStarvation;
		MonoSingleton<LifeController>.Instance.EndedStarvingEvent += OnEndStarvation;
	}

	protected override void OnDestroy()
	{
		if (MonoSingleton<GoapController>.IsInstantiated())
		{
			MonoSingleton<GoapController>.Instance.OnGoalStartedEvent -= OnGoalStarted;
			MonoSingleton<GoapController>.Instance.OnGoalEndedEvent -= OnGoalEnded;
		}
		if (MonoSingleton<LifeController>.IsInstantiated())
		{
			MonoSingleton<LifeController>.Instance.DieFromStarvationEvent -= OnDieFromStarvation;
			MonoSingleton<LifeController>.Instance.StartedStarvingEvent -= OnStartStarvation;
			MonoSingleton<LifeController>.Instance.EndedStarvingEvent -= OnEndStarvation;
		}
		this.FaceObjectEvent = null;
		this.CreateWorkerEvent = null;
		this.RemoveWorkerEvent = null;
		this.SpawnWorkerEvent = null;
		this.PullOutToolEvent = null;
		this.HideToolEvent = null;
		this.WorkerBanished = null;
		this.EquipmentPresetUpdatedEvent = null;
		this.HourTypeChangeEvent = null;
		this.FaceObjectTransformEvent = null;
		this.LookAtTransformEvent = null;
		this.SetEulerAngleEvent = null;
		this.FoodStorageChangedEvent = null;
		this.MedicineStorageChangedEvent = null;
		this.WorkerCountChangedEvent = null;
		this.WorkerNameChangedEvent = null;
		this.WorkerDiedEvent = null;
		this.WorkerFaintedEvent = null;
		this.JobPriorityChangedEvent = null;
		base.OnDestroy();
	}

	public void WorkerJobPriorityChanged(HumanoidInstance humanoidInstance, JobType jobType, int priority)
	{
		this.JobPriorityChangedEvent?.Invoke(humanoidInstance, jobType, priority);
	}

	public void WorkerNameChanged(HumanoidInstance humanoidInstance)
	{
		this.WorkerNameChangedEvent?.Invoke(humanoidInstance);
	}

	public void WorkerCountChanged()
	{
		this.WorkerCountChangedEvent?.Invoke();
	}

	public void WorkerDied(int remainingWorkersCount)
	{
		this.WorkerDiedEvent?.Invoke(remainingWorkersCount);
	}

	public void WorkerFainted(int faintedWorkersCount, int totalWorkersCount)
	{
		this.WorkerFaintedEvent?.Invoke(faintedWorkersCount, totalWorkersCount);
	}

	public void ChangeWorkerHour(HumanoidInstance humanoidInstance, HourType hourType)
	{
		this.HourTypeChangeEvent?.Invoke(humanoidInstance, hourType);
	}

	public void SpawnWorker(HumanoidInstance humanoid, bool startGoapTicker = true)
	{
		MonoSingleton<WorkerManager>.Instance.InstantiateWorker(humanoid, startGoapTicker);
		this.SpawnWorkerEvent?.Invoke(humanoid);
		if (MonoSingleton<WorkerManager>.Instance.AllWorkers != null)
		{
			MonoSingleton<AchievementManager>.Instance.SetStat("WORKER_CNT", MonoSingleton<WorkerManager>.Instance.AllWorkers.Count);
		}
	}

	public void CreateWorker(HumanoidInstance humanoid)
	{
		this.CreateWorkerEvent?.Invoke(humanoid);
		SpawnWorker(humanoid);
	}

	public void RemoveWorker(HumanoidInstance humanoid)
	{
		this.RemoveWorkerEvent?.Invoke(humanoid);
		MonoSingleton<WorkerManager>.Instance.RemoveWorker(humanoid);
	}

	public void PullOutTool(HumanoidInstance humanoid, string toolID, Transform socket)
	{
		this.PullOutToolEvent?.Invoke(humanoid, toolID, socket);
	}

	public void HideTool(HumanoidInstance humanoid)
	{
		this.HideToolEvent?.Invoke(humanoid);
	}

	public void DropItem(EquipmentInstance item, InventoryInstance inventory)
	{
		HumanoidInstance humanoidInstance = GlobalSaveController.CurrentVillageData?.Workers?.FirstOrDefault((HumanoidInstance worker) => worker.Inventory.Equals(inventory));
		if (humanoidInstance == null || item == null)
		{
			return;
		}
		humanoidInstance.Inventory?.DropItem(item);
		if (!humanoidInstance.HasDisposed)
		{
			if (item.Blueprint.ItemType == ItemType.Weapon)
			{
				humanoidInstance.SetWeaponVisibility(isVisible: false);
			}
			MonoSingleton<WorkerManager>.Instance.GetView(humanoidInstance).DropItem(item);
		}
	}

	public void EquipItem(EquipmentInstance item, InventoryInstance inventory)
	{
		HumanoidInstance humanoidInstance = GlobalSaveController.CurrentVillageData?.Workers.FirstOrDefault((HumanoidInstance worker) => worker.Inventory.Equals(inventory));
		if (humanoidInstance != null)
		{
			if (humanoidInstance.WorkerBehaviour.IsDrafting && item.Blueprint.ItemType == ItemType.Weapon)
			{
				humanoidInstance.SetWeaponVisibility(isVisible: true);
			}
			MonoSingleton<WorkerManager>.Instance.GetView(humanoidInstance).EquipItem(item);
		}
	}

	public void BanishWorker(HumanoidInstance humanoid)
	{
		this.WorkerBanished?.Invoke(humanoid);
	}

	public void FaceObject(HumanoidInstance humanoid, Vector3 objectPosition)
	{
		this.FaceObjectEvent?.Invoke(humanoid, objectPosition);
	}

	public void FaceObject(HumanoidInstance humanoid, Transform transform)
	{
		this.FaceObjectTransformEvent?.Invoke(humanoid, transform);
	}

	public void LookAt(HumanoidInstance humanoid, Transform transform)
	{
		this.LookAtTransformEvent?.Invoke(humanoid, transform);
	}

	public void SetEulerAngle(HumanoidInstance humanoid, Vector3 eulerAngle)
	{
		this.SetEulerAngleEvent?.Invoke(humanoid, eulerAngle);
	}

	public void OnEquipmentPresetUpdated(ManageGroupPreset preset)
	{
		this.EquipmentPresetUpdatedEvent?.Invoke(preset);
	}

	public void FoodStorageChanged(HumanoidInstance humanoidInstance)
	{
		this.FoodStorageChangedEvent?.Invoke(humanoidInstance);
	}

	public void MedicineStorageChanged(HumanoidInstance humanoidInstance)
	{
		this.MedicineStorageChangedEvent?.Invoke(humanoidInstance);
	}

	private void OnStartStarvation(StatsInstance instance)
	{
	}

	private void OnEndStarvation(StatsInstance instance)
	{
	}

	private void OnDieFromStarvation(StatsInstance instance)
	{
		if (instance.Owner is HumanoidInstance humanoid)
		{
			RemoveWorker(humanoid);
		}
	}

	private void OnGoalStarted(Agent agent, Goal goal)
	{
		if (agent.AgentOwner is HumanoidInstance { WorkerBehaviour: not null } humanoidInstance)
		{
			humanoidInstance.WorkerBehaviour.GoalUpdated(goal.Id, hasStarted: true);
		}
	}

	private void OnGoalEnded(Agent agent, string id, GoalCondition state)
	{
		if (agent.AgentOwner is HumanoidInstance { WorkerBehaviour: not null } humanoidInstance)
		{
			humanoidInstance.WorkerBehaviour.GoalUpdated(id, hasStarted: false);
		}
	}

	public void InitIncognitoWorker(HumanoidInstance humanoid)
	{
		if (humanoid.HasDied)
		{
			Log.Error("Cannot call InitIncognitoWorker on a human which has died.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Controller\\WorkerController.cs");
			return;
		}
		humanoid.WorkerBehaviour.Setup();
		humanoid.InitGoap();
		humanoid.AttachToDateUpdateEvent();
		humanoid.WorkerBehaviour.WorkerSocial.InitAffectionsIncognito();
		humanoid.InitBehaviourIncognito();
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
