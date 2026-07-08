using System;
using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Controllers;
using NSMedieval.Goap.Goals;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.State;
using NSMedieval.State.WorkerJobs;
using NSMedieval.View;
using NSMedieval.WorldMap;
using UnityEngine;

namespace NSMedieval.Goap;

public class WorkerGoapAgent : Agent, IFormCaravanGoapAgent
{
	private HumanoidInstance humanoid;

	private ScheduleData currentScheduleData;

	private Goal exclusiveGoal;

	private CaravanInstance preparingForCaravan;

	public float TempDisableHaulingSecondsForNextDrop { get; set; }

	public HourType CurrentHourType { get; private set; } = HourType.None;


	public Goal ExclusiveGoal => exclusiveGoal;

	internal bool IsFormingCaravan => preparingForCaravan != null;

	public CaravanInstance PreparingForCaravan => preparingForCaravan;

	public WorkerGoapAgent(HumanoidInstance humanoid)
		: base(humanoid, new WorkerGoalExecutionManager(humanoid))
	{
		this.humanoid = humanoid;
	}

	public override void StartTicker()
	{
		base.StartTicker();
		MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent += new Action(RefreshWorkHour);
	}

	public override void StopTicker()
	{
		base.StopTicker();
		if (MonoSingleton<WorldTimeManager>.IsInstantiated())
		{
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(RefreshWorkHour);
		}
	}

	public override void Dispose()
	{
		if (MonoSingleton<WorldTimeManager>.IsInstantiated())
		{
			MonoSingleton<WorldTimeManager>.Instance.HourUpdateEvent -= new Action(RefreshWorkHour);
		}
		exclusiveGoal = null;
		humanoid = null;
		preparingForCaravan = null;
		base.Dispose();
	}

	public override AnimatedAgentView GetView()
	{
		return humanoid?.GetAgentView<AnimatedAgentView>();
	}

	public void RefreshActiveJobs()
	{
		JobType activeJobCombination = humanoid.WorkerBehaviour.ActiveJobCombination;
		JobType[] allJobTypes = EnumValues.AllJobTypes;
		foreach (JobType jobType in allJobTypes)
		{
			if (jobType != 0 && (activeJobCombination & jobType) == 0)
			{
				ForbidJob(jobType);
			}
		}
		allJobTypes = EnumValues.AllJobTypes;
		foreach (JobType jobType2 in allJobTypes)
		{
			if (jobType2 != 0 && (activeJobCombination & jobType2) != 0)
			{
				AllowJob(jobType2);
			}
		}
	}

	public void RefreshWorkHour()
	{
		if (humanoid != null && !humanoid.HasDisposed && humanoid.WorkerBehaviour != null && !humanoid.HasDied)
		{
			if (humanoid.WorkerBehaviour.ForcedWorkHour != HourType.None)
			{
				ChangeCurrentHourType(humanoid.WorkerBehaviour.ForcedWorkHour);
				return;
			}
			WorldDate dateAndTime = GlobalSaveController.CurrentVillageData.DateAndTime;
			int num = Mathf.Clamp(dateAndTime.HoursSinceDay, 0, dateAndTime.HoursInDay - 1);
			HourType newHourType = humanoid.ScheduleHours[num];
			ChangeCurrentHourType(newHourType);
		}
	}

	public float GetJobPriority(JobType job)
	{
		string goalId = Repository<JobRepository, Job>.Instance.GetByJobType(job).Goals.FirstOrDefault((string g) => g != string.Empty);
		return base.GoalScheduler.GetJobPriority(goalId);
	}

	public void ChangeJobPriority(JobType job, int priority)
	{
		string[] goals = Repository<JobRepository, Job>.Instance.GetByJobType(job).Goals;
		foreach (string goalId in goals)
		{
			base.GoalScheduler.ModifyJobPriority(goalId, (float)((double)priority * 0.1));
		}
		RefreshActiveJobs();
	}

	public Goal ForceNextGoalExclusive(string goalId)
	{
		if (string.IsNullOrEmpty(goalId))
		{
			exclusiveGoal = null;
			return null;
		}
		exclusiveGoal = ForceNextGoal(goalId);
		return exclusiveGoal;
	}

	public void StartCaravanFormation(CaravanInstance caravan)
	{
		preparingForCaravan = caravan;
		if (!humanoid.WorkerBehaviour.IsCrazy)
		{
			humanoid.WorkerBehaviour.ForcedWorkHour = HourType.Caravan;
			RefreshWorkHour();
			humanoid.GetAgentView<WorkerView>()?.RefreshBagPack();
			if (humanoid.HasFainted)
			{
				ForceNextGoal("FaintGoal");
			}
			else
			{
				ForceNextGoalExclusive("FormCaravanGoal");
			}
		}
	}

	public void ClearCaravanFormingData()
	{
		preparingForCaravan = null;
		humanoid.WorkerBehaviour.ForcedWorkHour = HourType.None;
		humanoid.GetAgentView<WorkerView>()?.RefreshBagPack();
		RefreshWorkHour();
		Abort();
	}

	internal override void OnGoalEnded(Goal goal, GoalCondition condition)
	{
		HumanoidInstance humanoidInstance = humanoid;
		if (humanoidInstance != null)
		{
			float tempDisableHaulingSecondsForNextDrop = TempDisableHaulingSecondsForNextDrop;
			humanoidInstance.DropStorage(default(Vec3Int), tempDisableHaulingSecondsForNextDrop);
		}
		TempDisableHaulingSecondsForNextDrop = 0f;
		base.OnGoalEnded(goal, condition);
		HandleGoalPreferenceEffectors(goal);
		if (goal == exclusiveGoal)
		{
			exclusiveGoal = null;
		}
	}

	public void AttendPlayerTriggeredEvent(string goalId)
	{
		if (!humanoid.WorkerBehaviour.IsCrazy)
		{
			humanoid.WorkerBehaviour.ForcedWorkHour = HourType.PlayerTriggeredEvent;
			RefreshWorkHour();
			if (humanoid.HasFainted)
			{
				ForceNextGoal("FaintGoal");
			}
			else
			{
				ForceNextGoalExclusive(goalId);
			}
		}
	}

	public void LeavePlayerTriggeredEvent()
	{
		humanoid.WorkerBehaviour.ForcedWorkHour = HourType.None;
		RefreshWorkHour();
	}

	public override void Tick(float deltaTime)
	{
		if (humanoid == null || humanoid.IsInIncognitoMode() || base.HasDisposed)
		{
			return;
		}
		base.Tick(deltaTime);
		if (base.HasDisposed)
		{
			return;
		}
		List<ResourcePileInstance> list = ((IEquipableAgent)base.AgentOwner)?.Inventory?.EquipOrders;
		if (base.HasDisposed || base.IsGoalPreparing || list == null || list.Count <= 0)
		{
			return;
		}
		bool flag = false;
		foreach (ResourcePileInstance item in list)
		{
			flag |= !item.HasDisposed && MonoSingleton<ReservationManager>.Instance.CanReserve(item, base.AgentOwner);
		}
		if (flag && !base.CurrentGoalName.Equals("EquipGoal"))
		{
			Abort();
			ForceNextGoal("EquipGoal");
		}
	}

	protected override bool CanGoalStart(Goal goal)
	{
		if (humanoid?.WorkerBehaviour != null && humanoid.WorkerBehaviour.IsBanished && !(goal is BanishGoal))
		{
			return false;
		}
		return true;
	}

	private void ChangeCurrentHourType(HourType newHourType)
	{
		if (MonoSingleton<WorkerController>.IsInstantiated() && !LoadingController.IsSceneTransition && !base.HasDisposed && newHourType != CurrentHourType)
		{
			CurrentHourType = newHourType;
			if (CurrentHourType == HourType.None)
			{
				Log.Error("Hour type is none!", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GOAP\\Agent\\WorkerGoapAgent.cs");
				return;
			}
			ClearWorkHourEffectors();
			currentScheduleData = GetCurrentWorkHourData();
			ApplyWorkHourEffectors();
			RefreshHourGoals();
			RefreshDisableThreshold();
			MonoSingleton<WorkerController>.Instance.ChangeWorkerHour(humanoid, CurrentHourType);
		}
	}

	private void ClearWorkHourEffectors()
	{
		if (currentScheduleData.ActiveEffectors != null)
		{
			string[] activeEffectors = currentScheduleData.ActiveEffectors;
			foreach (string name in activeEffectors)
			{
				humanoid.Stats.EndEffector(name);
			}
		}
	}

	private void ApplyWorkHourEffectors()
	{
		if (currentScheduleData.ActiveEffectors != null)
		{
			string[] activeEffectors = currentScheduleData.ActiveEffectors;
			foreach (string effectorId in activeEffectors)
			{
				humanoid.Stats.StartEffector(effectorId);
			}
		}
	}

	private void RefreshDisableThreshold()
	{
		string settingsParameter = currentScheduleData.GetSettingsParameter(ScheduleDataSettingsType.GoalSettings, "DisableBelow", "Priority");
		if (!string.IsNullOrEmpty(settingsParameter) && float.TryParse(settingsParameter, out var result))
		{
			base.GoalExecutionManager.SetStartableGoalMaximumPriority(result);
		}
		else
		{
			base.GoalExecutionManager.SetStartableGoalMaximumPriority(float.MaxValue);
		}
	}

	private void RefreshHourGoals()
	{
		base.GoalScheduler.DisableAllGoals();
		ScheduleData.GoalPriority[] goals = currentScheduleData.Goals;
		for (int i = 0; i < goals.Length; i++)
		{
			ScheduleData.GoalPriority goalPriority = goals[i];
			string goal = goalPriority.Goal;
			if (!base.GoalScheduler.ExistInPool(goal))
			{
				bool isEnabled;
				if (!GoalsMap.Constuctors.ContainsKey(goal))
				{
					FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(55, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GOAP\\Agent\\WorkerGoapAgent.cs");
					if (isEnabled)
					{
						messageBuilder.AppendLiteral("Goal '");
						messageBuilder.AppendFormatted(goal);
						messageBuilder.AppendLiteral("' exists in scheduled data but not found in code!");
					}
					Log.Warning(messageBuilder);
					continue;
				}
				GoalPriorityData goalPriorityData = new GoalPriorityData((Goal)GoalsMap.Constuctors[goal].Invoke(new object[1] { this }), goalPriority.Priority);
				if (!goalPriorityData.Goal.AgentTypeCheck())
				{
					FVLogErrorInterpolationHandler messageBuilder2 = new FVLogErrorInterpolationHandler(29, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GOAP\\Agent\\WorkerGoapAgent.cs");
					if (isEnabled)
					{
						messageBuilder2.AppendLiteral("Invalid agent type for goal: ");
						messageBuilder2.AppendFormatted(this);
					}
					Log.Error(messageBuilder2);
					continue;
				}
				if (!goalPriorityData.Goal.ShouldBeAdded())
				{
					FVLogInfoInterpolationHandler messageBuilder3 = new FVLogInfoInterpolationHandler(65, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GOAP\\Agent\\WorkerGoapAgent.cs");
					if (isEnabled)
					{
						messageBuilder3.AppendLiteral("Skipping goal '");
						messageBuilder3.AppendFormatted(goalPriorityData.GoalId);
						messageBuilder3.AppendLiteral("' because ShouldBeAdded() returned false (agent: ");
						messageBuilder3.AppendFormatted(base.AgentOwner);
						messageBuilder3.AppendLiteral(")");
					}
					Log.Info(messageBuilder3);
					continue;
				}
				base.GoalScheduler.AddToPool(goalPriorityData);
			}
			base.GoalScheduler.SetBasePriority(goal, goalPriority.Priority);
			base.GoalScheduler.EnableGoal(goal);
		}
		RefreshActiveJobs();
	}

	private ScheduleData GetCurrentWorkHourData()
	{
		ScheduleModel scheduleModel = humanoid.WorkerBehaviour.HumanType.ScheduleModel;
		if (scheduleModel == null)
		{
			Log.Error("WorkerGoapAgent: ScheduleModel not found.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\GOAP\\Agent\\WorkerGoapAgent.cs");
			return default(ScheduleData);
		}
		return scheduleModel.GetScheduleData(CurrentHourType);
	}

	private void ForbidJob(JobType job)
	{
		string[] goals = Repository<JobRepository, Job>.Instance.GetByJobType(job).Goals;
		foreach (string text in goals)
		{
			base.GoalScheduler.DisableGoal(text);
			Goal goal = exclusiveGoal;
			if (goal != null && goal.Id.Equals(text))
			{
				exclusiveGoal = null;
			}
		}
	}

	private void AllowJob(JobType job)
	{
		string[] goals = Repository<JobRepository, Job>.Instance.GetByJobType(job).Goals;
		foreach (string text in goals)
		{
			if (currentScheduleData.ContainsGoal(text))
			{
				base.GoalScheduler.EnableGoal(text);
			}
		}
	}

	private void HandleGoalPreferenceEffectors(Goal goal)
	{
		if (humanoid == null || humanoid.HasDied || humanoid.HasDisposed)
		{
			return;
		}
		foreach (GoalPreferenceLevelData item in humanoid.GoalPreferences.GetGoalPrefLevelByGoalId(goal.Id))
		{
			if (item == null)
			{
				break;
			}
			string[] effectors = item.Effectors;
			foreach (string effectorId in effectors)
			{
				humanoid.Stats.StartEffector(effectorId);
			}
		}
	}

	protected override void GoalInitSequenceFailed(ThreadSequenceJobCompleteStatus status, Goal goal)
	{
		base.GoalInitSequenceFailed(status, goal);
		if (status != ThreadSequenceJobCompleteStatus.Success && ExclusiveGoal == goal)
		{
			exclusiveGoal = null;
		}
	}

	public override string ToString()
	{
		return $"Worker({humanoid})";
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
