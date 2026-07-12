using System;
using System.Collections;
using System.Collections.Generic;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Scripts.Pooler;
using NSMedieval.State;
using NSMedieval.Types;
using NSMedieval.UI;
using NSMedieval.Village;
using UnityEngine;

namespace NSMedieval.BuildingComponents;

[RequireComponent(typeof(BuildingUsePositionsComponent))]
public class ProductionComponent : BaseComponent
{
	[SerializeField]
	private ProductionComponentInstance componentInstance;

	[NonSerialized]
	private BuildingUsePositionsComponent buildingUsePositionsComponent;

	[SerializeField]
	private string productionFinished = "production_finished_particles";

	[SerializeField]
	private MeshRenderer productionCircle;

	private MaterialPropertyBlock circleMaterialBlock;

	private Coroutine updateUICoroutine;

	public BuildingUsePositionsComponent BuildingUsePositionsComponent => buildingUsePositionsComponent;

	public ProductionComponentBlueprint ProductionComponentBlueprint => componentInstance.Blueprint;

	public event Action StartProductionProgressEffectEvent;

	public event Action<ActionCompletionStatus> FinishProductionProgressEffectEvent;

	protected override void OnDestroy()
	{
		base.OnDestroy();
		componentInstance = null;
		buildingUsePositionsComponent = null;
		this.FinishProductionProgressEffectEvent = null;
		this.StartProductionProgressEffectEvent = null;
		StopAllCoroutines();
		updateUICoroutine = null;
	}

	public ProductionInstance GetCurrentProduction()
	{
		return (componentInstance?.ProductionSystemInstance)?.CurrentProduction;
	}

	public bool AltAnimationProductionExists()
	{
		if (componentInstance == null)
		{
			return false;
		}
		return componentInstance.ProductionSystemInstance.Productions.Exists((ProductionInstance production) => production.Blueprint.UsesAltAnimation);
	}

	public void UpdateProductionCircle()
	{
		if (productionCircle == null)
		{
			return;
		}
		ProductionSystemInstance productionSystemInstance = componentInstance?.ProductionSystemInstance;
		if (productionSystemInstance?.CurrentProduction == null)
		{
			productionCircle.gameObject.SetActive(value: false);
			return;
		}
		ProductionInstance currentProduction = productionSystemInstance.CurrentProduction;
		float value = currentProduction.CurrentStep?.Progress ?? 0f;
		switch (currentProduction.State)
		{
		case ProductionState.Paused:
			if (circleMaterialBlock == null)
			{
				circleMaterialBlock = new MaterialPropertyBlock();
			}
			circleMaterialBlock.SetFloat("_Warning", 0f);
			circleMaterialBlock.SetFloat("_PausedFillbar", value);
			circleMaterialBlock.SetFloat("_CompleteFillbar", value);
			circleMaterialBlock.SetFloat("_ResourceFillbar", value);
			break;
		case ProductionState.TargetReached:
			productionCircle.gameObject.SetActive(value: false);
			return;
		case ProductionState.NoSkilledWorker:
			if (circleMaterialBlock == null)
			{
				circleMaterialBlock = new MaterialPropertyBlock();
			}
			circleMaterialBlock.SetFloat("_Warning", 1f);
			circleMaterialBlock.SetFloat("_CompleteFillbar", 0f);
			circleMaterialBlock.SetFloat("_ResourceFillbar", 0f);
			circleMaterialBlock.SetFloat("_PausedFillbar", 0f);
			break;
		case ProductionState.WaitingForWorker:
		case ProductionState.InProgress:
			if (circleMaterialBlock == null)
			{
				circleMaterialBlock = new MaterialPropertyBlock();
			}
			circleMaterialBlock.SetFloat("_Warning", 0f);
			circleMaterialBlock.SetFloat("_CompleteFillbar", value);
			circleMaterialBlock.SetFloat("_ResourceFillbar", 0f);
			circleMaterialBlock.SetFloat("_PausedFillbar", 0f);
			break;
		case ProductionState.WaitingForResources:
			if (circleMaterialBlock == null)
			{
				circleMaterialBlock = new MaterialPropertyBlock();
			}
			circleMaterialBlock.SetFloat("_Warning", 0f);
			circleMaterialBlock.SetFloat("_CompleteFillbar", 0f);
			circleMaterialBlock.SetFloat("_ResourceFillbar", value);
			circleMaterialBlock.SetFloat("_PausedFillbar", 0f);
			break;
		default:
			productionCircle.gameObject.SetActive(value: false);
			return;
		}
		productionCircle.gameObject.SetActive(value: true);
		productionCircle.SetPropertyBlock(circleMaterialBlock);
	}

	public void StartProductionProgressEffect()
	{
		this.StartProductionProgressEffectEvent?.Invoke();
	}

	public void FinishProductionProgressEffect(ActionCompletionStatus status = ActionCompletionStatus.None)
	{
		this.FinishProductionProgressEffectEvent?.Invoke(status);
		if (status == ActionCompletionStatus.Success)
		{
			PlayParticlesOnProductionFinished();
		}
	}

	public override void PreSpawnInitialization()
	{
		base.PreSpawnInitialization();
		buildingUsePositionsComponent = GetComponent<BuildingUsePositionsComponent>();
	}

	protected override void OnEnterPoolOnMainSceneLeaving()
	{
		base.OnEnterPoolOnMainSceneLeaving();
		componentInstance = null;
		if (buildingUsePositionsComponent != null)
		{
			buildingUsePositionsComponent.Dispose();
		}
	}

	protected override void OnReturnToPoolDuringGameplay()
	{
		base.OnReturnToPoolDuringGameplay();
		componentInstance = null;
		if (buildingUsePositionsComponent != null)
		{
			buildingUsePositionsComponent.Dispose();
		}
	}

	protected override void OnBaseBuildingEnterFinishedState(bool afterLoading = false)
	{
		if (!afterLoading)
		{
			ProductionComponentBlueprint byID = Repository<ProductionComponentsRepository, ProductionComponentBlueprint>.Instance.GetByID(base.OwnerBuilding.Blueprint.ProductionComponentID);
			componentInstance = ComponentFactory.CreateComponentInstance(base.OwnerBuilding, byID);
			componentInstance.InitializeProductionSystemInstance();
		}
		else
		{
			VillageInstance activeVillage = VillageManager.ActiveVillage;
			componentInstance = activeVillage.WorldObjectStorage.GetBaseComponentInstanceByUniqueId(base.OwnerBuilding.UniqueId) as ProductionComponentInstance;
			if (componentInstance == null)
			{
				bool isEnabled;
				FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(68, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Production\\ProductionComponent.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Couldn't find ProductionComponentInstance in component storage! ID: ");
					messageBuilder.AppendFormatted(base.OwnerBuilding.UniqueId);
				}
				Log.Error(messageBuilder);
				return;
			}
			componentInstance.SetupAfterLoading(base.OwnerBuilding);
		}
		base.BaseBuildingViewComponent.SetAdditionalMenuItemId("productionBuilding");
		base.BaseComponentInstance = componentInstance;
		buildingUsePositionsComponent.InitializePositions();
		componentInstance.WorkplacePositions.UnionWith(buildingUsePositionsComponent.WorkplacePositions);
		BoxCollider component = GetComponent<BoxCollider>();
		if (component != null && componentInstance.Blueprint != null)
		{
			productionCircle.transform.localPosition = component.center;
			componentInstance.Blueprint.ProductionMarkerOffset.ApplyToTransform(productionCircle.transform);
		}
		componentInstance.Map.ProductionComponentBuildingManager.AddToCache(this, componentInstance);
		componentInstance.ProductionSystemInstance.OnCurrentProductionChangedEvent += OnCurrentProductionChanged;
		base.OnBaseBuildingEnterFinishedState(afterLoading);
		base.BaseBuildingViewComponent.BuildingOcclusionCullingChangedEvent += OnOcclusionCullingChanged;
	}

	private void OnOcclusionCullingChanged(bool isCulled)
	{
		if (isCulled)
		{
			productionCircle.gameObject.SetActive(value: false);
		}
		else
		{
			UpdateProductionCircle();
		}
	}

	protected override void OnBuildingSelected()
	{
		updateUICoroutine = StartCoroutine(UpdateUI());
		float radius = ProductionComponentBlueprint.ProductionSpeedMultiplierSkep.Radius;
		if (radius > 0f)
		{
			MonoSingleton<SphereRenderManager>.Instance.Show(base.transform.position, radius, SphereRenderType.SkepRange);
		}
	}

	protected override void OnBuildingDeselected()
	{
		if (MonoSingleton<SphereRenderManager>.IsInstantiated())
		{
			MonoSingleton<SphereRenderManager>.Instance.Hide(SphereRenderType.SkepRange);
		}
	}

	protected override void OnInfoPanelDataRequested()
	{
		bool isEnabled;
		FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(31, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Constructables\\Building Components\\Production\\ProductionComponent.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Generating info panel data for ");
			messageBuilder.AppendFormatted(base.OwnerBuilding.UniqueId);
		}
		Log.Debug(messageBuilder);
		InfoPanelHeader headerData = base.BaseBuildingViewComponent.GetHeaderData();
		InfoPanelBody body = new InfoPanelBody(base.OwnerBuilding.BlueprintId, base.OwnerBuilding.GetBuildingName(), string.Empty, base.BaseBuildingViewComponent.GetInfoStats(), GetBuildPhaseInfo(), base.BaseBuildingViewComponent.GetResourcesInfo(), base.BaseBuildingViewComponent.GetDescriptions(), GetInfos(), BuildingSubCategoryUI.SubCtgProduction);
		InfoPanelFooter footer = new InfoPanelFooter(base.BaseBuildingViewComponent.GetInfoPanelActions(), base.OwnerBuilding);
		InfoPanelProduction infoPanelProduction = GetInfoPanelProduction();
		InfoPanelData infoPanelData = new InfoPanelData(InfoPanelDataType.General, headerData, body, footer, infoPanelProduction);
		base.BaseBuildingViewComponent.SetInfoPanelData(infoPanelData);
	}

	private List<string> GetBuildPhaseInfo()
	{
		List<string> buildPhaseInfo = base.BaseBuildingViewComponent.GetBuildPhaseInfo();
		buildPhaseInfo.AddRange(ProductionUtils.GetProductionInfo(componentInstance));
		return buildPhaseInfo;
	}

	private List<string> GetInfos()
	{
		return base.BaseBuildingViewComponent.GetInfos();
	}

	private void PlayParticlesOnProductionFinished()
	{
		GameObject gameObject = MonoSingleton<ParticleSystemPool>.Instance.PlayParticles(productionFinished, base.transform);
		if (!(gameObject == null))
		{
			gameObject.transform.localPosition = Vector3.zero;
			Collider clickCollider = base.BaseBuildingViewComponent.ClickCollider;
			MonoSingleton<ParticleSystemPool>.Instance.SetEmitterSize(productionFinished, gameObject, clickCollider, 0.8f);
		}
	}

	private InfoPanelProduction GetInfoPanelProduction()
	{
		return new InfoPanelProduction(componentInstance.Blueprint.Productions, componentInstance.ProductionSystemInstance);
	}

	private IEnumerator UpdateUI()
	{
		while (base.BaseBuildingViewComponent.Selected)
		{
			ProductionSystemInstance productionSystemInstance = componentInstance?.ProductionSystemInstance;
			if (productionSystemInstance != null && !productionSystemInstance.HasDisposed)
			{
				_ = componentInstance.ProductionSystemInstance.Productions.Count;
			}
			yield return new WaitForSeconds(0.15f);
		}
	}

	private void OnCurrentProductionChanged(ProductionSystemInstance system, ProductionInstance oldProduction)
	{
		UpdateProductionCircle();
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
