using System;
using System.Collections.Generic;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix;
using NSEipix.Base;
using NSMedieval.BuildingComponents;
using NSMedieval.Controllers;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.Serialization;
using UnityEngine;

namespace NSMedieval.State;

[Serializable]
[FVSerializableKey("ProductionSystemInstance", "")]
public class ProductionSystemInstance : IGameDisposable, IDisposable, IFVSerializable
{
	[SerializeField]
	private List<ProductionInstance> productions = new List<ProductionInstance>();

	[NonSerialized]
	private ProductionComponentInstance owner;

	[NonSerialized]
	private IProductionAgent operatingAgent;

	[NonSerialized]
	private ProductionInstance currentProduction;

	public bool HasDisposed { get; private set; }

	public ProductionComponentInstance Owner => owner;

	public List<ProductionInstance> Productions => productions;

	public ProductionInstance CurrentProduction => currentProduction;

	public IProductionAgent OperatingAgent => operatingAgent;

	public event Action<IGameDisposable> OnDisposedEvent;

	public event Action<ProductionSystemInstance, ProductionInstance> OnNewProductionEvent;

	public event Action<ProductionSystemInstance, ProductionInstance> OnCurrentProductionChangedEvent;

	public event Action<ProductionSystemInstance, ProductionInstance> OnProductionCompletedEvent;

	public ProductionSystemInstance()
	{
	}

	public void Initialize(ProductionComponentInstance owner, bool afterLoading = false)
	{
		if (this.owner != null && this.owner != owner)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(66, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionSystemInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("ProductionSystem Owner changed. This should never happen new:");
				messageBuilder.AppendFormatted(owner);
				messageBuilder.AppendLiteral(" old:");
				messageBuilder.AppendFormatted(this.owner);
			}
			Log.Warning(messageBuilder);
			return;
		}
		if (this.owner == owner)
		{
			Log.Warning("ProductionSystem double initialization. This should never happen.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionSystemInstance.cs");
			return;
		}
		this.owner = owner;
		BaseBuildingInstance ownerBuilding = this.owner.OwnerBuilding;
		ownerBuilding.OnReleasedEvent += OnReservationReleasedEvent;
		ownerBuilding.OnReservedEvent += OnReservedEvent;
		foreach (ProductionInstance production in productions)
		{
			production.Initialize(this, afterLoading);
			if (!production.HasDisposed)
			{
				production.OnStateChangedEvent += OnProductionStateChanged;
				production.OnLastStepCompleted += OnLastStepCompleted;
			}
		}
		MonoSingleton<ProductionManager>.Instance.RegisterSystem(this);
		if (ownerBuilding == null || ownerBuilding.Village.SavedObjectsSpawned)
		{
			MonoSingleton<TaskController>.Instance.WaitForNextFrame().Then(PickCurrentActiveProduction);
		}
		else
		{
			MonoSingleton<World>.Instance.MapLoadedEvent += OnMapLoaded;
		}
	}

	private void OnMapLoaded(bool fromSave)
	{
		if (MonoSingleton<World>.IsInstantiated())
		{
			MonoSingleton<World>.Instance.MapLoadedEvent -= OnMapLoaded;
		}
		PickCurrentActiveProduction();
	}

	private void OnReservedEvent(IReservable reservable, IGoapAgentOwner agent)
	{
		operatingAgent = agent as IProductionAgent;
		ProductionInstance productionInstance = CurrentProduction;
		if (productionInstance != null && productionInstance.State == ProductionState.InProgress)
		{
			CurrentProduction.UpdateState();
		}
	}

	private void OnReservationReleasedEvent(IReservable reservable, IGoapAgentOwner agent)
	{
		operatingAgent = null;
		ProductionInstance productionInstance = CurrentProduction;
		if (productionInstance != null && productionInstance.State == ProductionState.InProgress)
		{
			CurrentProduction.UpdateState();
		}
	}

	public void StartAgentProduction(IProductionAgent agent)
	{
		bool isEnabled;
		if (CurrentProduction == null)
		{
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(63, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionSystemInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Agent tried to start production, but there is none. Pos:");
				messageBuilder.AppendFormatted(owner.GetGridPosition());
				messageBuilder.AppendLiteral(" Agent:");
				messageBuilder.AppendFormatted(agent);
			}
			Log.Warning(messageBuilder);
			operatingAgent = null;
		}
		else if (CurrentProduction.State != ProductionState.WaitingForWorker)
		{
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(97, 4, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionSystemInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Agent tried to start production, but production is not ready for humanoid. ID:");
				messageBuilder.AppendFormatted(CurrentProduction.BlueprintId);
				messageBuilder.AppendLiteral(" State:");
				messageBuilder.AppendFormatted(CurrentProduction.State);
				messageBuilder.AppendLiteral(" Pos:");
				messageBuilder.AppendFormatted(owner.GetGridPosition());
				messageBuilder.AppendLiteral(" Agent:");
				messageBuilder.AppendFormatted(agent);
			}
			Log.Warning(messageBuilder);
			operatingAgent = null;
		}
		else
		{
			operatingAgent = agent;
			CurrentProduction.UpdateState();
		}
	}

	public void Dispose()
	{
		if (HasDisposed)
		{
			return;
		}
		foreach (ProductionInstance item in productions.IterateInReverseDynamic())
		{
			item.Dispose();
		}
		BaseBuildingInstance baseBuildingInstance = owner?.OwnerBuilding;
		if (baseBuildingInstance != null && !baseBuildingInstance.HasDisposed)
		{
			baseBuildingInstance.OnReleasedEvent -= OnReservationReleasedEvent;
			baseBuildingInstance.OnReservedEvent -= OnReservedEvent;
		}
		productions.Clear();
		if (!LoadingController.IsLeavingMainScene)
		{
			this.OnDisposedEvent?.Invoke(this);
		}
		this.OnDisposedEvent = null;
		HasDisposed = true;
		this.OnCurrentProductionChangedEvent = null;
		this.OnNewProductionEvent = null;
		this.OnProductionCompletedEvent = null;
		currentProduction = null;
		operatingAgent = null;
		owner = null;
	}

	public void PasteProductionQueue()
	{
		foreach (ProductionInstance item in MonoSingleton<ProductionManager>.Instance.CopyBuffer)
		{
			if (owner.Blueprint.Productions.Contains(item.BlueprintId))
			{
				ProductionInstance productionInstance = CopyProductionItem(item);
				PasteNewProduction(productionInstance);
				productionInstance.UpdateState();
			}
		}
		PickCurrentActiveProduction();
	}

	public void CopyProductionQueue()
	{
		List<ProductionInstance> list = new List<ProductionInstance>();
		foreach (ProductionInstance production in productions)
		{
			list.Add(CopyProductionItem(production));
		}
		MonoSingleton<ProductionManager>.Instance.CopyBuffer.Clear();
		MonoSingleton<ProductionManager>.Instance.CopyBuffer = list;
	}

	public void CopyProduction(ProductionInstance instanceToCopy)
	{
		ProductionInstance item = CopyProductionItem(instanceToCopy);
		MonoSingleton<ProductionManager>.Instance.CopyBuffer.Clear();
		MonoSingleton<ProductionManager>.Instance.CopyBuffer.Add(item);
	}

	private ProductionInstance CopyProductionItem(ProductionInstance instanceToCopy)
	{
		ProductionInstance productionInstance = new ProductionInstance(instanceToCopy.Blueprint);
		productionInstance.SetResourceFilter(instanceToCopy.ResourceFilter.DeepCopy());
		productionInstance.SetOrder(instanceToCopy.Order, updateState: false);
		productionInstance.SetMode(instanceToCopy.Mode, updateState: false);
		productionInstance.SetProductTargetCount(instanceToCopy.ProductTargetCount, updateState: false);
		productionInstance.SetOwnerCreatureId(instanceToCopy.OwnerCreatureId);
		productionInstance.SetSkillRange(instanceToCopy.SkillLevelRange);
		return productionInstance;
	}

	public ProductionInstance AddNewProduction(NSMedieval.Model.Production blueprint)
	{
		if (blueprint == null)
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(61, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionSystemInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Tried to add NULL blueprint production to production at pos: ");
				messageBuilder.AppendFormatted(owner.GetGridPosition());
			}
			Log.Error(messageBuilder);
			return null;
		}
		ProductionInstance productionInstance = new ProductionInstance(blueprint);
		PasteNewProduction(productionInstance);
		PickCurrentActiveProduction();
		return productionInstance;
	}

	public void PasteNewProduction(ProductionInstance productionInstance)
	{
		productionInstance.Initialize(this);
		productionInstance.OnStateChangedEvent += OnProductionStateChanged;
		productionInstance.OnLastStepCompleted += OnLastStepCompleted;
		productions.Add(productionInstance);
		this.OnNewProductionEvent?.Invoke(this, productionInstance);
	}

	public void ChangePriority(ProductionInstance instance, int direction)
	{
		bool isEnabled;
		if (instance == null || (direction != -1 && direction != 1))
		{
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(59, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionSystemInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Production 'change priority' failsafe triggered. Pos:");
				messageBuilder.AppendFormatted(owner.GetGridPosition());
				messageBuilder.AppendLiteral(" prod:");
				messageBuilder.AppendFormatted(instance?.BlueprintId);
			}
			Log.Warning(messageBuilder);
			return;
		}
		int num = productions.IndexOf(instance);
		if (num < 0)
		{
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(92, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionSystemInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Tried to change production priority, but production dose not exist in the system. Pos:");
				messageBuilder.AppendFormatted(owner.GetGridPosition());
				messageBuilder.AppendLiteral(" prod:");
				messageBuilder.AppendFormatted(instance.BlueprintId);
			}
			Log.Warning(messageBuilder);
		}
		else if ((num != 0 || direction != -1) && (num < productions.Count - 1 || direction != 1))
		{
			if (num > 0 && direction == -1)
			{
				productions.Swap(num, num - 1);
				PickCurrentActiveProduction();
			}
			else if (direction == 1)
			{
				productions.Swap(num, num + 1);
				PickCurrentActiveProduction();
			}
		}
	}

	internal void RemoveProduction(ProductionInstance instance)
	{
		if (productions.Contains(instance))
		{
			bool num = instance.State == ProductionState.InProgress;
			productions.Remove(instance);
			PickCurrentActiveProduction();
			if (num)
			{
				owner.OwnerBuilding.ForceRefreshTemperatureInput();
			}
			if (MonoSingleton<ProductionManager>.IsInstantiated())
			{
				MonoSingleton<ProductionManager>.Instance.ProductionStateChanged(instance);
			}
			instance.Dispose();
		}
	}

	private void PickCurrentActiveProduction()
	{
		if (productions.Count == 0)
		{
			SetCurrentActiveProduction(null);
			return;
		}
		ProductionInstance productionInstance = currentProduction;
		if (productionInstance != null && !productionInstance.HasDisposed && productionInstance.State == ProductionState.InProgress)
		{
			return;
		}
		for (int i = 0; i < productions.Count; i++)
		{
			productions[i].OnStateChangedEvent -= OnProductionStateChanged;
		}
		for (int j = 0; j < productions.Count; j++)
		{
			ProductionInstance productionInstance2 = productions[j];
			productionInstance2.UpdateState();
			productionInstance2.OnStateChangedEvent += OnProductionStateChanged;
		}
		ProductionInstance productionInstance3 = null;
		for (int k = 0; k < productions.Count; k++)
		{
			ProductionInstance productionInstance4 = productions[k];
			if (productionInstance4.Storage.GetTotalStoredCount() > 0 || productionInstance4.SecondaryIngredientStorage.GetTotalStoredCount() > 0)
			{
				productionInstance3 = productionInstance4;
				break;
			}
			if (productionInstance3 == null && (productionInstance4.RequireInteraction() || productionInstance4.State == ProductionState.InProgress))
			{
				productionInstance3 = productionInstance4;
			}
		}
		if (productionInstance3 != null && productionInstance3.State != ProductionState.InProgress && !productionInstance3.RequireInteraction())
		{
			SetCurrentActiveProduction(null);
		}
		else if (productionInstance3 != productionInstance)
		{
			SetCurrentActiveProduction(null);
			SetCurrentActiveProduction(productionInstance3);
		}
	}

	private void SetCurrentActiveProduction(ProductionInstance instance)
	{
		if (currentProduction != instance)
		{
			bool isEnabled;
			FVLogTraceInterpolationHandler messageBuilder = new FVLogTraceInterpolationHandler(85, 6, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Production\\ProductionSystemInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("SetCurrentActiveProduction for prod system ");
				messageBuilder.AppendFormatted(GetHashCode());
				messageBuilder.AppendLiteral(" at ");
				messageBuilder.AppendFormatted(owner.GridPosition);
				messageBuilder.AppendLiteral(" from '");
				messageBuilder.AppendFormatted(currentProduction?.BlueprintId);
				messageBuilder.AppendLiteral("' (obj hash ");
				messageBuilder.AppendFormatted(currentProduction?.GetHashCode());
				messageBuilder.AppendLiteral(") to '");
				messageBuilder.AppendFormatted(instance?.BlueprintId);
				messageBuilder.AppendLiteral("' (obj hash ");
				messageBuilder.AppendFormatted(instance?.GetHashCode());
				messageBuilder.AppendLiteral(")");
			}
			Log.Trace(messageBuilder);
			ProductionInstance productionInstance = currentProduction;
			if (productionInstance != null && !productionInstance.HasDisposed)
			{
				productionInstance.OnBecomeInactive();
			}
			currentProduction = instance;
			instance?.OnBecomeActive();
			if (MonoSingleton<ProductionManager>.IsInstantiated())
			{
				MonoSingleton<ProductionManager>.Instance.ProductionStateChanged(instance);
			}
			this.OnCurrentProductionChangedEvent?.Invoke(this, productionInstance);
		}
	}

	private void OnProductionStateChanged(ProductionInstance instance, ProductionState oldState)
	{
		if (MonoSingleton<ProductionManager>.IsInstantiated())
		{
			MonoSingleton<ProductionManager>.Instance.ProductionStateChanged(instance);
		}
		if (currentProduction != null && currentProduction.State == ProductionState.InProgress)
		{
			owner?.OwnerBuilding?.ForceRefreshTemperatureInput();
		}
		else if (instance.State == oldState)
		{
			if (CurrentProduction == instance)
			{
				PickCurrentActiveProduction();
			}
			else if (instance.RequireInteraction())
			{
				if (CurrentProduction == null)
				{
					PickCurrentActiveProduction();
				}
				else if (productions.IndexOf(CurrentProduction) > productions.IndexOf(instance))
				{
					PickCurrentActiveProduction();
				}
			}
		}
		else
		{
			owner?.OwnerBuilding?.ForceRefreshTemperatureInput();
			PickCurrentActiveProduction();
		}
	}

	private void OnLastStepCompleted(ProductionInstance production, ProductionStepInstance step)
	{
		if (MonoSingleton<ProductionManager>.IsInstantiated())
		{
			MonoSingleton<ProductionManager>.Instance.ProductionStateChanged(production);
		}
		this.OnProductionCompletedEvent?.Invoke(this, production);
	}

	public void Serialize(FVSerializer serializer)
	{
		serializer.Write("productions", productions);
	}

	public ProductionSystemInstance(FVDeserializer deserializer)
	{
		productions = deserializer.ReadObjectList<ProductionInstance>("productions");
		productions.RemoveFirstWhere(delegate(ProductionInstance production)
		{
			if (production == null || production.HasDisposed)
			{
				return true;
			}
			foreach (ProductionStepInstance step in production.Steps)
			{
				if (step == null || step.HasDisposed)
				{
					return true;
				}
			}
			return false;
		});
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
