using System;
using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using Models.Production;
using NSEipix.Base;
using NSEipix.Model;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.Components;
using NSMedieval.Components.Base;
using NSMedieval.Controllers;
using NSMedieval.Enums;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Research;
using NSMedieval.Serialization;
using NSMedieval.State.WorkerJobs;
using NSMedieval.Stockpiles;
using NSMedieval.Types;
using UnityEngine;

namespace NSMedieval.State;

[Serializable]
[FVSerializableKey("ProductionInstance", "")]
public class ProductionInstance : IGameDisposable, IDisposable, IFVSerializable
{
	private const int DefaultProductTargetCount = 1;

	private static readonly StorageBase InstanceStorageBase = new StorageBase(0, ignoreWeigth: true, infinite: true);

	private static readonly ProductionStepType[] ForceFinishSteps = new ProductionStepType[3]
	{
		ProductionStepType.SpawnProduct,
		ProductionStepType.SpawnDismantleProduct,
		ProductionStepType.PassiveProduce
	};

	[SerializeField]
	private string blueprintId;

	[SerializeField]
	private Storage storage = new Storage(InstanceStorageBase);

	[SerializeField]
	private Storage secondaryIngredientStorage = new Storage(InstanceStorageBase);

	[SerializeField]
	private List<ProductionStepInstance> steps = new List<ProductionStepInstance>();

	[SerializeField]
	private ProductionMode mode;

	[SerializeField]
	private int productTargetCount = 1;

	[SerializeField]
	private ProductionState state;

	[SerializeField]
	private ResourcesFilter resourceFilter = new ResourcesFilter();

	[SerializeField]
	private ProductionOrder order;

	[SerializeField]
	private int ownerCreatureId;

	[SerializeField]
	private IntRange skillLevelRange;

	[NonSerialized]
	private ProductionComponentInstance ownerProductionComponentInstance;

	[NonSerialized]
	private ProductionSystemInstance ownerSystem;

	[NonSerialized]
	private NSMedieval.Model.Production blueprint;

	[NonSerialized]
	private int currentStepIndex = -1;

	public ProductionState State => state;

	public bool HasDisposed { get; private set; }

	public ProductionMode Mode => mode;

	public ProductionOrder Order => order;

	public ResourcesFilter ResourceFilter => resourceFilter;

	public string BlueprintId => blueprintId;

	public NSMedieval.Model.Production Blueprint => blueprint ?? (blueprint = Repository<ProductionRepository, NSMedieval.Model.Production>.Instance.GetByID(blueprintId));

	public Storage Storage => storage;

	public Storage SecondaryIngredientStorage => secondaryIngredientStorage;

	public ProductionComponentInstance OwnerProductionComponentInstance => ownerProductionComponentInstance;

	public ProductionSystemInstance OwnerSystem => ownerSystem;

	public int ProductTargetCount => productTargetCount;

	public ProductionStepInstance CurrentStep
	{
		get
		{
			if (currentStepIndex <= -1)
			{
				return null;
			}
			return steps[currentStepIndex];
		}
	}

	public int CurrentStepIndex => currentStepIndex;

	public List<ProductionStepInstance> Steps => steps;

	public int OwnerCreatureId => ownerCreatureId;

	public IntRange SkillLevelRange => skillLevelRange;

	public event Action<ProductionInstance, ProductionState> OnStateChangedEvent;

	public event Action<ProductionInstance, ProductionStepInstance> OnLastStepCompleted;

	public event Action<IGameDisposable> OnDisposedEvent;

	public event Action<ProductionInstance, int> OnTargetCountChange;

	public event Action<ProductionInstance, ProductionMode> ProductionModeChangeEvent;

	public ProductionInstance(NSMedieval.Model.Production blueprint)
	{
		blueprintId = blueprint.GetID();
		this.blueprint = blueprint;
		order = ProductionOrder.Work;
		foreach (Resource allUsableResource in this.blueprint.AllUsableResources)
		{
			if (!this.blueprint.ForbiddenOnStart.Contains(allUsableResource.GetID()))
			{
				resourceFilter.AddAllowedResource(allUsableResource);
			}
		}
	}

	public ProductionInstance()
	{
	}

	public void Initialize(ProductionSystemInstance system, bool afterLoading = false)
	{
		if (HasDisposed)
		{
			return;
		}
		if (ownerSystem != null)
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(73, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Tried to change production instance's (");
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral(") owner. This should never happen.");
			}
			Log.Error(messageBuilder);
			return;
		}
		if (afterLoading)
		{
			foreach (ResourceInstance resource in storage.Resources)
			{
				ResourceStatsProducer.ProduceResourceStats(resource, resource.Blueprint, resource.Stats);
			}
			storage.SetResourcesOwner(initializeStats: true);
			foreach (ResourceInstance resource2 in secondaryIngredientStorage.Resources)
			{
				ResourceStatsProducer.ProduceResourceStats(resource2, resource2.Blueprint, resource2.Stats);
			}
			secondaryIngredientStorage.SetResourcesOwner(initializeStats: true);
		}
		ownerSystem = system;
		ownerProductionComponentInstance = system.Owner;
		currentStepIndex = -1;
		InitializeSteps();
		Storage.SetResourcesOwner();
		Storage.ResourceAddedEvent += StorageItemUpdated;
		Storage.ResourceTakenEvent += StorageItemUpdated;
		SecondaryIngredientStorage.SetResourcesOwner();
		SecondaryIngredientStorage.ResourceAddedEvent += StorageItemUpdated;
		SecondaryIngredientStorage.ResourceTakenEvent += StorageItemUpdated;
		resourceFilter.OnParamsChangedEvent += ResourceFilterChanged;
		MonoSingleton<WorkerController>.Instance.CreateWorkerEvent += OnSpawnWorker;
		MonoSingleton<WorkerController>.Instance.RemoveWorkerEvent += OnRemoveWorker;
	}

	public void Dispose()
	{
		if (!HasDisposed)
		{
			currentStepIndex = -1;
			if (!LoadingController.IsLeavingMainScene)
			{
				storage?.DropAll(ownerProductionComponentInstance.GetGridPosition());
				secondaryIngredientStorage?.DropAll(ownerProductionComponentInstance.GetGridPosition());
			}
			if (storage != null)
			{
				storage.ResourceAddedEvent -= StorageItemUpdated;
				storage.ResourceTakenEvent -= StorageItemUpdated;
			}
			if (secondaryIngredientStorage != null)
			{
				secondaryIngredientStorage.ResourceAddedEvent -= StorageItemUpdated;
				secondaryIngredientStorage.ResourceTakenEvent -= StorageItemUpdated;
			}
			HasDisposed = true;
			if (!LoadingController.IsLeavingMainScene)
			{
				this.OnDisposedEvent?.Invoke(this);
			}
			DisposeSteps();
			steps = null;
			this.OnDisposedEvent = null;
			this.OnLastStepCompleted = null;
			ownerProductionComponentInstance = null;
			ownerSystem = null;
			storage = null;
			secondaryIngredientStorage = null;
			if (resourceFilter != null)
			{
				resourceFilter.OnParamsChangedEvent -= ResourceFilterChanged;
			}
			if (MonoSingleton<WorkerController>.IsInstantiated())
			{
				MonoSingleton<WorkerController>.Instance.CreateWorkerEvent -= OnSpawnWorker;
				MonoSingleton<WorkerController>.Instance.RemoveWorkerEvent -= OnRemoveWorker;
			}
		}
	}

	public int GetStorageItemsCount()
	{
		Storage obj = storage;
		if (obj == null)
		{
			int? num = secondaryIngredientStorage?.ResourceCount;
			int? num2 = num;
			return num2.GetValueOrDefault();
		}
		return obj.ResourceCount;
	}

	private void DisposeSteps()
	{
		if (steps == null)
		{
			return;
		}
		foreach (ProductionStepInstance step in steps)
		{
			if (step.IsActive && !step.IsCompleted)
			{
				step.OnBecomeInactive();
				step.OnEnd();
			}
			step.Dispose();
		}
		steps.Clear();
	}

	public void DeliverResource(ResourceInstance resourceInstance)
	{
		if (!blueprint.UseIngredientCombo)
		{
			storage.Add(resourceInstance);
			return;
		}
		foreach (KeyIntPair item in Blueprint.Recipe)
		{
			string iD = item.GetID();
			int num;
			if (int.TryParse(iD, out var result))
			{
				ResourceCategory resourceCategory = (ResourceCategory)result;
				if ((resourceInstance.Blueprint.Category & resourceCategory) == 0)
				{
					continue;
				}
				num = Storage.GetTotalStoredCount(resourceCategory);
			}
			else
			{
				if (resourceInstance.BlueprintId != iD)
				{
					continue;
				}
				num = Storage.GetById(iD)?.Amount ?? 0;
			}
			if (item.Value - num > 0)
			{
				storage.Add(resourceInstance);
				return;
			}
		}
		secondaryIngredientStorage.Add(resourceInstance);
	}

	public void SetProductTargetCount(int count, bool updateState = true)
	{
		bool isEnabled;
		FVLogTraceInterpolationHandler messageBuilder = new FVLogTraceInterpolationHandler(17, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Production Count ");
			messageBuilder.AppendFormatted(count);
		}
		Log.Trace(messageBuilder);
		int num = productTargetCount;
		productTargetCount = count;
		if (num != count && updateState)
		{
			this.OnTargetCountChange?.Invoke(this, productTargetCount);
			UpdateState();
		}
	}

	public bool IsProductionTargetCountReached()
	{
		if (CurrentStep != null && (CurrentStep.Type == ProductionStepType.PassiveProduce || CurrentStep.Type == ProductionStepType.WorkerProduce))
		{
			bool forceComplete = false;
			MonoSingleton<ReservationManager>.Instance.ForEachReserver(OwnerProductionComponentInstance, delegate(IGoapAgentOwner agent)
			{
				forceComplete |= ((IProductionAgent)agent).IsProducing;
			});
			if (forceComplete)
			{
				return false;
			}
		}
		if (Mode.Equals(ProductionMode.Amount))
		{
			return ProductTargetCount <= 0;
		}
		if (Mode.Equals(ProductionMode.Until))
		{
			if (Blueprint.JobType == JobType.Research)
			{
				return MonoSingleton<ResearchManager>.Instance.GetAvailableBookCount(Blueprint.GetID()) >= productTargetCount;
			}
			return blueprint.GetAllProductsCount() >= productTargetCount;
		}
		return false;
	}

	public void SetMode(ProductionMode mode, bool updateState = true)
	{
		if (this.mode != mode)
		{
			this.mode = mode;
			this.ProductionModeChangeEvent?.Invoke(this, mode);
			if (updateState)
			{
				UpdateState();
			}
		}
	}

	public void SetOrder(ProductionOrder order, bool updateState = true)
	{
		if (this.order != order && order != 0)
		{
			this.order = order;
			if (updateState)
			{
				UpdateState();
			}
		}
	}

	public void SetResourceFilter(ResourcesFilter newResourceFilter)
	{
		resourceFilter = newResourceFilter;
	}

	public void SetOwnerCreatureId(int newOwnerCreatureId)
	{
		ownerCreatureId = newOwnerCreatureId;
	}

	public void SetSkillRange(IntRange newSkillLevelRange)
	{
		skillLevelRange = newSkillLevelRange;
	}

	public void Cancel()
	{
		if (!HasDisposed)
		{
			ownerSystem.RemoveProduction(this);
		}
	}

	public bool IsCurrentProduction()
	{
		return ownerSystem.CurrentProduction == this;
	}

	public bool RequireInteraction()
	{
		switch (State)
		{
		case ProductionState.Paused:
		case ProductionState.TargetReached:
		case ProductionState.NoSkilledWorker:
		case ProductionState.InProgress:
			return false;
		case ProductionState.WaitingForResources:
		{
			if (CurrentStep is ProductionStepCollect productionStepCollect)
			{
				return productionStepCollect.ResourcesAllowedAvailable;
			}
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(78, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("No Collect step found in RequireInteraction(). This should never happen... ");
				messageBuilder.AppendFormatted(BlueprintId);
				messageBuilder.AppendLiteral("@(");
				messageBuilder.AppendFormatted(ownerProductionComponentInstance.GetGridPosition());
				messageBuilder.AppendLiteral(")");
			}
			Log.Warning(messageBuilder);
			return false;
		}
		case ProductionState.WaitingForWorker:
			return true;
		default:
			return false;
		}
	}

	public void DebugUpdateState()
	{
		UpdateState();
	}

	internal void OnBecomeActive()
	{
		CurrentStep?.OnBecomeActive();
		ownerProductionComponentInstance?.OwnerBuilding?.ForceRefreshTemperatureInput();
	}

	internal void OnBecomeInactive()
	{
		CurrentStep?.OnBecomeInactive();
		ownerProductionComponentInstance?.OwnerBuilding?.ForceRefreshTemperatureInput();
	}

	internal void OnSpawnWorker(HumanoidInstance humanoidInstance)
	{
		UpdateState();
	}

	private void OnRemoveWorker(HumanoidInstance humanoidInstance)
	{
		if (ownerCreatureId != 0 && humanoidInstance.UniqueId == ownerCreatureId && !humanoidInstance.IsInIncognitoMode())
		{
			SetOwnerCreatureId(0);
		}
	}

	internal void UpdateState()
	{
		if (HasDisposed)
		{
			return;
		}
		if (order == ProductionOrder.Pause)
		{
			SetState(ProductionState.Paused);
			return;
		}
		bool flag = CurrentStep != null && ForceFinishSteps.Contains(CurrentStep.Type);
		if (!flag && CurrentStep != null)
		{
			ProductionStepType type = CurrentStep.Type;
			if (type == ProductionStepType.Collect || type == ProductionStepType.WorkerProduce)
			{
				flag = CurrentStep.Progress > Mathf.Epsilon;
			}
		}
		if (!flag && IsProductionTargetCountReached())
		{
			SetState(ProductionState.TargetReached);
			if (CurrentStep != null && CurrentStep.IsCompleted && CurrentStep.Type != ProductionStepType.Collect)
			{
				ResetSteps();
				PickStep();
			}
			return;
		}
		List<SkillLevelPair> requiredSkills = Blueprint.RequiredSkills;
		if (requiredSkills != null && requiredSkills.Count > 0 && !ProductionUtils.CanAnyWorkerDoThisProduction(this))
		{
			SetState(ProductionState.NoSkilledWorker);
			EndCurrentStep();
			return;
		}
		ProductionStepInstance currentStep = CurrentStep;
		if (currentStep == null || currentStep.IsCompleted)
		{
			PickStep();
			if (CurrentStep != null)
			{
				UpdateState();
			}
			return;
		}
		switch (currentStep.Type)
		{
		case ProductionStepType.Collect:
			SetState(ProductionState.WaitingForResources);
			break;
		case ProductionStepType.WorkerProduce:
			SetState((((ProductionStepWorker)currentStep).State == ProductionStepWorker.ProcessState.InProgress) ? ProductionState.InProgress : ProductionState.WaitingForWorker);
			break;
		case ProductionStepType.PassiveProduce:
			SetState(ProductionState.InProgress);
			break;
		case ProductionStepType.SpawnProduct:
		case ProductionStepType.SpawnDismantleProduct:
		{
			if (!OwnerProductionComponentInstance.OwnerBuilding.Village.SavedObjectsSpawned)
			{
				currentStep.OnBecomeActive();
				ResetSteps();
				SetState(ProductionState.None);
				break;
			}
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(45, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("This should never happen. StateTypeHit: ");
				messageBuilder.AppendFormatted(currentStep.Type);
				messageBuilder.AppendLiteral("; ");
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral("@(");
				messageBuilder.AppendFormatted(ownerProductionComponentInstance.GetGridPosition());
				messageBuilder.AppendLiteral(")");
			}
			Log.Warning(messageBuilder);
			break;
		}
		default:
			throw new ArgumentOutOfRangeException();
		}
	}

	private void InitializeSteps()
	{
		bool isEnabled;
		if (Blueprint == null)
		{
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(21, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Blueprint not found: ");
				messageBuilder.AppendFormatted(blueprintId);
			}
			Log.Info(messageBuilder);
			Cancel();
			return;
		}
		if (Blueprint.ProductionSteps == null || Blueprint.ProductionSteps.Count == 0)
		{
			throw new Exception("Production blueprint " + BlueprintId + " steps not found!");
		}
		if (steps == null || steps.Count == 0)
		{
			currentStepIndex = -1;
			steps = new List<ProductionStepInstance>();
			foreach (ProductionStep productionStep in blueprint.ProductionSteps)
			{
				if (productionStep.Type != 0)
				{
					ProductionStepInstance item = ProductionStepFactory.ProduceStep(this, productionStep);
					steps.Add(item);
				}
			}
		}
		for (int i = 0; i < steps.Count; i++)
		{
			ProductionStepInstance productionStepInstance = steps[i];
			if (productionStepInstance == null || i >= Blueprint.ProductionSteps.Count || blueprint.ProductionSteps[i].Type != productionStepInstance.Type)
			{
				FVLogWarningInterpolationHandler messageBuilder2 = new FVLogWarningInterpolationHandler(56, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionInstance.cs");
				if (isEnabled)
				{
					messageBuilder2.AppendLiteral("Production ");
					messageBuilder2.AppendFormatted(blueprintId);
					messageBuilder2.AppendLiteral(" had step removed. Production has been reset.");
				}
				Log.Warning(messageBuilder2);
				Storage.DropAll(ownerProductionComponentInstance.GetGridPosition());
				SecondaryIngredientStorage.DropAll(ownerProductionComponentInstance.GetGridPosition());
				DisposeSteps();
				InitializeSteps();
				break;
			}
			productionStepInstance.Initialize(this, blueprint.ProductionSteps[i]);
			productionStepInstance.OnCompletedEvent -= OnStepCompletedEvent;
			productionStepInstance.OnCompletedEvent += OnStepCompletedEvent;
		}
	}

	private void OnStepCompletedEvent(ProductionStepInstance step)
	{
		if (CurrentStep != step)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(47, 4, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Production ");
				messageBuilder.AppendFormatted(BlueprintId);
				messageBuilder.AppendLiteral("@(");
				messageBuilder.AppendFormatted(OwnerProductionComponentInstance.GetGridPosition());
				messageBuilder.AppendLiteral(" completed 'non-current' step. ");
				messageBuilder.AppendFormatted(step.Type);
				messageBuilder.AppendLiteral(" / ");
				messageBuilder.AppendFormatted(CurrentStep?.Type);
			}
			Log.Warning(messageBuilder);
		}
		else
		{
			if (CurrentStep == Steps.Last())
			{
				LastStepCompleted(CurrentStep);
			}
			UpdateState();
		}
	}

	private void SetState(ProductionState newState)
	{
		ProductionState arg = state;
		state = newState;
		if (state == ProductionState.InProgress)
		{
			ownerProductionComponentInstance.OwnerBuilding.OverrideThermalModel(ownerProductionComponentInstance.Blueprint.ThermalModel);
		}
		else
		{
			ownerProductionComponentInstance.OwnerBuilding.LoadDefaultThermalModel();
		}
		ownerProductionComponentInstance.OwnerBuilding.ForceRefreshTemperatureInput();
		this.OnStateChangedEvent?.Invoke(this, arg);
	}

	private void StorageItemUpdated(SimpleResourceCount count)
	{
		DropInvalidResources();
		UpdateState();
	}

	private void ResourceFilterChanged()
	{
		DropInvalidResources();
		UpdateState();
	}

	private void PickStep()
	{
		int num = -1;
		bool isEnabled;
		if (steps.Count == 0)
		{
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(28, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Production with 0 steps. ");
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral("@(");
				messageBuilder.AppendFormatted(ownerProductionComponentInstance.GetGridPosition());
				messageBuilder.AppendLiteral(")");
			}
			Log.Error(messageBuilder);
			return;
		}
		for (int i = 0; i < steps.Count; i++)
		{
			if (!steps[i].IsCompleted)
			{
				num = i;
				break;
			}
		}
		if (num == -1)
		{
			ResetSteps();
			PickStep();
		}
		else if (currentStepIndex == num)
		{
			FVLogWarningInterpolationHandler messageBuilder2 = new FVLogWarningInterpolationHandler(50, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionInstance.cs");
			if (isEnabled)
			{
				messageBuilder2.AppendLiteral("Production tried to start already active step. ");
				messageBuilder2.AppendFormatted(blueprintId);
				messageBuilder2.AppendLiteral("@(");
				messageBuilder2.AppendFormatted(ownerProductionComponentInstance.GetGridPosition());
				messageBuilder2.AppendLiteral(")");
			}
			Log.Warning(messageBuilder2);
		}
		else
		{
			StartStep(num);
		}
	}

	internal void ResetSteps()
	{
		EndCurrentStep();
		foreach (ProductionStepInstance step in steps)
		{
			step.Reset();
		}
	}

	private void LastStepCompleted(ProductionStepInstance step)
	{
		this.OnLastStepCompleted?.Invoke(this, step);
		if (mode == ProductionMode.Amount && productTargetCount > 0)
		{
			productTargetCount--;
		}
	}

	private void StartStep(int stepIndex)
	{
		if (stepIndex < 0 || stepIndex >= steps.Count)
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(48, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Tried to start invalid production step index ");
				messageBuilder.AppendFormatted(stepIndex);
				messageBuilder.AppendLiteral(" : ");
				messageBuilder.AppendFormatted(BlueprintId);
			}
			Log.Error(messageBuilder);
			currentStepIndex = -1;
		}
		else
		{
			EndCurrentStep();
			currentStepIndex = stepIndex;
			steps[stepIndex].OnStart();
			if (IsCurrentProduction())
			{
				steps[stepIndex].OnBecomeActive();
			}
		}
	}

	private void EndCurrentStep()
	{
		if (CurrentStep == null || !CurrentStep.IsActive)
		{
			currentStepIndex = -1;
			return;
		}
		CurrentStep.OnBecomeInactive();
		CurrentStep.OnEnd();
		currentStepIndex = -1;
	}

	private void DropInvalidResources()
	{
		if (state == ProductionState.InProgress || ((CurrentStep?.Type ?? ProductionStepType.None) == ProductionStepType.WorkerProduce && CurrentStep.Progress > 0.001f))
		{
			return;
		}
		if (storage == null)
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(83, 5, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendFormatted(this);
				messageBuilder.AppendLiteral(".storage is null. HasDisposed = ");
				messageBuilder.AppendFormatted(HasDisposed);
				messageBuilder.AppendLiteral(", BlueprintId = ");
				messageBuilder.AppendFormatted(BlueprintId);
				messageBuilder.AppendLiteral(", CurrentStep = ");
				messageBuilder.AppendFormatted(CurrentStep);
				messageBuilder.AppendLiteral(", ResourceFilter = ");
				messageBuilder.AppendFormatted(ResourceFilter);
			}
			Log.Error(messageBuilder);
		}
		while (true)
		{
			IL_00c2:
			foreach (ResourceInstance resource in storage.Resources)
			{
				if (!resourceFilter.IsValid(resource))
				{
					storage.DropResourceInstance(resource, OwnerProductionComponentInstance.GetGridPosition());
					goto IL_00c2;
				}
			}
			break;
		}
		while (true)
		{
			using IEnumerator<ResourceInstance> enumerator = secondaryIngredientStorage.Resources.GetEnumerator();
			ResourceInstance current2;
			do
			{
				if (enumerator.MoveNext())
				{
					current2 = enumerator.Current;
					continue;
				}
				return;
			}
			while (resourceFilter.IsValid(current2));
			secondaryIngredientStorage.DropResourceInstance(current2, OwnerProductionComponentInstance.GetGridPosition());
		}
	}

	public override string ToString()
	{
		return string.Format("{0}: {1}, ResourcesDelivered: {2} {3}: {4}, {5}: {6}, {7}: {8}", "CurrentStep", CurrentStep, Storage, "State", State, "Mode", Mode, "Order", Order);
	}

	public void Serialize(FVSerializer serializer)
	{
		serializer.Write("blueprintId", blueprintId);
		serializer.Write("storage", storage);
		serializer.Write("secondaryIngredientStorage", secondaryIngredientStorage);
		serializer.WriteEnum("mode", mode);
		serializer.Write("productTargetCount", productTargetCount);
		serializer.WriteEnum("state", state);
		serializer.Write("resourceFilter", resourceFilter);
		serializer.WriteEnum("order", order);
		serializer.Write("steps", steps);
		serializer.Write("ownerCreatureId", ownerCreatureId);
		serializer.Write("skillLevelRange", skillLevelRange);
	}

	public ProductionInstance(FVDeserializer deserializer)
	{
		blueprintId = deserializer.ReadString("blueprintId");
		storage = deserializer.ReadObject<Storage>("storage");
		secondaryIngredientStorage = deserializer.ReadObject<Storage>("secondaryIngredientStorage") ?? new Storage(InstanceStorageBase);
		mode = deserializer.ReadEnum("mode", ProductionMode.Amount);
		productTargetCount = deserializer.ReadInt("productTargetCount");
		state = deserializer.ReadEnum("state", ProductionState.None);
		resourceFilter = deserializer.ReadObject<ResourcesFilter>("resourceFilter");
		order = deserializer.ReadEnum("order", ProductionOrder.None);
		steps = deserializer.ReadObjectList<ProductionStepInstance>("steps");
		ownerCreatureId = deserializer.ReadInt("ownerCreatureId");
		skillLevelRange = deserializer.ReadObject<IntRange>("skillLevelRange");
		CheckForResourceFilterReload();
	}

	private void CheckForResourceFilterReload()
	{
		if (blueprintId != "ice_block")
		{
			return;
		}
		foreach (Resource allUsableResource in Blueprint.AllUsableResources)
		{
			if (!blueprint.ForbiddenOnStart.Contains(allUsableResource.GetID()))
			{
				resourceFilter.AddAllowedResource(allUsableResource);
			}
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
