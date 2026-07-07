using System;
using System.Collections.Generic;
using System.Linq;
using FloatingOverlaySystem.Elements;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using Models.Type;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Controllers;
using NSMedieval.Enums;
using NSMedieval.EnvironmentEffects;
using NSMedieval.Extensions;
using NSMedieval.FloatingOverlaySystem;
using NSMedieval.Goap;
using NSMedieval.Goap.Goals;
using NSMedieval.Manager;
using NSMedieval.Managers.Selection;
using NSMedieval.Model;
using NSMedieval.PlayerTriggeredEventSystem;
using NSMedieval.Repository;
using NSMedieval.Roles;
using NSMedieval.RoomDetection;
using NSMedieval.Scripts.Pooler;
using NSMedieval.Sound;
using NSMedieval.State;
using NSMedieval.State.Timers;
using NSMedieval.State.WorkerJobs;
using NSMedieval.StatsSystem;
using NSMedieval.Tutorial;
using NSMedieval.Types;
using NSMedieval.UI;
using NSMedieval.UI.Utils;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Village;
using NSMedieval.Village.Map.Pathfinding;
using NSMedieval.WorldMap;
using UnityEngine;

namespace NSMedieval.View;

[SelectionBase]
public sealed class WorkerView : HumanoidView, IObserver, IAdditionalMenuOwner, IGameDisposable, IDisposable
{
	[SerializeField]
	private Material bodyMaterial;

	[SerializeField]
	private Transform gameplayOverlayHook;

	[SerializeField]
	private BleedingIntensity bleedingParticle;

	[SerializeField]
	private string skillupParticles = "skillup_particles";

	[SerializeField]
	private Transform rootBone;

	[SerializeField]
	private Transform pelvisBone;

	[SerializeField]
	private Transform mouthBone;

	private GameObject eatP;

	private string currentRightHandItem;

	private Dictionary<string, GameObject> tools = new Dictionary<string, GameObject>();

	private AgentCircleIndicator draftIndicator;

	private GameObject psychoticIndicator;

	private LineRenderer lineRendererCache;

	private float hideLineTimer;

	private LinearProgressBarFloatingElement healthBar;

	private IconCircleFloatingElement hasFlammableProjectileIcon;

	private TextFloatingElement workerNameElement;

	private bool manualAttackTargeting;

	private TextFloatingElement psychoticTextOverlayElement;

	private DamagePopupFloatingElement damagePopup;

	private XpPopupFloatingElement xpPopup;

	private bool isTargetingLineShown;

	private bool isRangeSphereShown;

	private Timer visualsUpdateTimer;

	private Timer overheadUIUpdateTimer;

	private Sprite iconHasFlammableProjectile;

	public override bool Visible => true;

	public bool ManualAttackTargeting => manualAttackTargeting;

	public Transform RootBone => rootBone;

	public event Action<IGameDisposable> OnDisposedEvent;

	public override WorldObject GetAsWorldObject()
	{
		return null;
	}

	public override CreatureBase GetAsCreature()
	{
		return humanoidInstance;
	}

	protected override bool IsSelectionNull()
	{
		if (humanoidInstance != null)
		{
			return humanoidInstance.HasDisposed;
		}
		return true;
	}

	protected override void OnItemDropped(EquipmentInstance item)
	{
		if (item.Blueprint.ItemType == ItemType.Weapon)
		{
			SetWeaponAnimationParams();
			if (base.Selected)
			{
				AttackType attackType = item.WeaponTypeSettings.AttackType;
				UpdateRangeSphere(attackType == AttackType.RangeChargeBefore || attackType == AttackType.RangeChargeAfter);
			}
		}
	}

	protected override void OnItemEquipped(EquipmentInstance item)
	{
		base.OnItemEquipped(item);
		if (item.Blueprint.ItemType == ItemType.Weapon)
		{
			SetWeaponAnimationParams(item);
			UpdateRangeSphere();
		}
	}

	private void OnRoleOwnerChanged(Role obj, HumanoidInstance humanoidInstance)
	{
		TrySetRoleActive();
	}

	public string GetActiveToolName()
	{
		foreach (KeyValuePair<string, GameObject> tool in tools)
		{
			if (tool.Value.activeInHierarchy)
			{
				return tool.Key;
			}
		}
		return string.Empty;
	}

	public void SetBuildProgress()
	{
		humanoidInstance.IsBulidProgressAlowed = true;
	}

	public void TrySetRoleActive()
	{
		HourType currentHourType = ((WorkerGoapAgent)base.HumanoidInstance.GetGoapAgent()).CurrentHourType;
		bool flag = base.HumanoidInstance.ActiveBehaviour.HumanoidRoleOwner.AssignedRole && currentHourType == HourType.RoleJob;
		TrySetParameter("RoleActive", flag);
		if (base.HumanoidInstance.ActiveBehaviour.HumanoidRoleOwner.RoleInstance == null)
		{
			base.BodyPreview.SetLuteEnabled(enabled: false);
			base.BodyPreview.SetDrumsEnabled(enabled: false);
			base.BodyPreview.SetPriestPropsEnabled(enabled: false);
			return;
		}
		switch (base.HumanoidInstance.ActiveBehaviour.HumanoidRoleOwner.RoleInstance.Blueprint.GetID())
		{
		case "bard":
			base.BodyPreview.SetLuteEnabled(flag);
			break;
		case "shaman":
			base.BodyPreview.SetDrumsEnabled(flag);
			break;
		case "priest":
			base.BodyPreview.SetPriestPropsEnabled(flag);
			break;
		}
	}

	public void Setup(CreatureBase creature)
	{
		HumanoidInstance humanoidInstance = (base.humanoidInstance = (HumanoidInstance)creature);
		base.BodyPreview.Setup(base.humanoidInstance);
		base.BodyPreview.ShowEntity();
		base.humanoidInstance.InitGoap();
		SetupGoapView();
		MonoSingleton<WorkerController>.Instance.WorkerNameChangedEvent += OnWorkerNameChange;
		MonoSingleton<OptionsController>.Instance.ToggleWorkerNames += OnToggleWorkerNames;
		GenerateWorkerNameGuiOverlayElements();
		SetWeaponAnimationParams();
		if (bleedingParticle != null)
		{
			bleedingParticle.gameObject.SetActive(value: false);
		}
		humanoidInstance.Stats.Controller.RegisterListener(StatEventType.ValueUpdated, StatType.Health, OnStatUpdated);
		humanoidInstance.Stats.Controller.RegisterListener(StatEventType.ValueUpdated, StatType.Sleep, OnStatUpdated);
		humanoidInstance.Stats.Controller.RegisterListener(StatEventType.ValueUpdated, StatType.Mood, OnStatUpdated);
		humanoidInstance.Stats.Controller.RegisterListener(StatEventType.AttributeModiferAdded, OnStatUpdated);
		humanoidInstance.Stats.Controller.RegisterListener(StatEventType.AttributeModiferRemoved, OnStatUpdated);
		MonoSingleton<CombatController>.Instance.DamageTakenEvent += OnHitTaken;
		MonoSingleton<CombatController>.Instance.HitBlockedEvent += OnHitTaken;
		MonoSingleton<CombatController>.Instance.HitMissedEvent += OnHitMissed;
		MonoSingleton<WorkerController>.Instance.HourTypeChangeEvent += OnHourChange;
		MonoSingleton<LifeController>.Instance.OnFaintEvent += OnFaint;
		base.humanoidInstance.Stats.OnEffectorStartEvent += OnEffector;
		base.humanoidInstance.Stats.OnEffectorEndEvent += OnEffector;
		MonoSingleton<RoleManager>.Instance.RoleChangedEvent += OnRoleOwnerChanged;
		base.humanoidInstance.WorkerBehaviour.CombatModeChangeEvent += OnCombatModeChanged;
		visualsUpdateTimer = new Timer(1f);
		visualsUpdateTimer.AddCallback(UpdateParticlesAndAnimationParams);
		visualsUpdateTimer.SetRestartOnEnd(value: true);
		UpdateParticlesAndAnimationParams();
		iconHasFlammableProjectile = AssetUtils.GetSprite("icon_has_flammable_projectile");
		overheadUIUpdateTimer = new Timer(0.2f);
		overheadUIUpdateTimer.AddCallback(UpdateOverheadUI);
		overheadUIUpdateTimer.SetRestartOnEnd(value: true);
		UpdateOverheadUI();
		WorkerGoapAgent workerGoapAgent = (WorkerGoapAgent)base.humanoidInstance.GetGoapAgent();
		if (workerGoapAgent != null)
		{
			OnHourChange(base.humanoidInstance, workerGoapAgent.CurrentHourType);
		}
		MonoSingleton<SceneController>.Instance.SceneSetup += RebindOnGameplayStart;
		float num = base.humanoidInstance.Info.Height / Repository<GenerationSettingsRepository, GenerationSettings>.Instance.Settings.DefaultHeight[(int)base.humanoidInstance.Info.BodyType];
		base.gameObject.transform.localScale = new Vector3(num, num, num);
		base.BodyPreview.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, base.humanoidInstance.Info.GetBlendShapeWeight());
	}

	private void OnCombatModeChanged(HumanoidInstance instance)
	{
		if (instance != null && !instance.HasDied && !instance.HasDisposed && !(draftIndicator == null))
		{
			UpdateCombatFootCircleIndicator();
			UpdateViewImmediate();
		}
	}

	private void RebindOnGameplayStart()
	{
		animator.RebindKeepState();
	}

	private void OnEffector(StatEffector effector)
	{
		if (effector.GetID().Equals("AgentMoodGood"))
		{
			string style = (humanoidInstance.Stats.IsEffectorActive("AgentMoodGood") ? "DefaultGreen" : "Normal");
			WorkerNameColorChange(style);
		}
	}

	private void OnHourChange(HumanoidInstance humanoidInstance, HourType hourType)
	{
		if (humanoidInstance == base.humanoidInstance)
		{
			TrySetRoleActive();
			if (hourType == HourType.PsyhoticCrazy)
			{
				GeneratePsychoticOverlayElement();
				CancelManualAttack();
			}
			else
			{
				DestroyPsychoticOverlayElement();
			}
		}
	}

	private void OnFaint(StatsInstance stats)
	{
		if (stats.Owner == humanoidInstance && humanoidInstance != null && !humanoidInstance.HasDisposed)
		{
			UpdateRangeSphere(itemDropped: true);
		}
	}

	protected override void UpdateCombatFootCircleIndicator()
	{
		if (!(draftIndicator == null) && humanoidInstance != null && !humanoidInstance.HasDisposed)
		{
			EquipmentInstance bestCombatCoverEquipment = humanoidInstance.GetBestCombatCoverEquipment(DamageType.Melee);
			if (bestCombatCoverEquipment == null)
			{
				draftIndicator.SetCoverAngle(0f);
			}
			else
			{
				draftIndicator.SetCoverAngle(bestCombatCoverEquipment.Blueprint.CoverAngle);
			}
			draftIndicator.SetHoldGroundVisual(humanoidInstance.WorkerBehaviour.CombatMode == UnitCombatModeType.DraftedHoldGround);
		}
	}

	public string GetAdditionalMenuId()
	{
		return humanoidInstance.GetGoapAgentID();
	}

	public IGoapTargetable GetAsTarget()
	{
		return humanoidInstance;
	}

	public override Transform GetGuiOverlayHookTransform()
	{
		if (gameplayOverlayHook != null)
		{
			return gameplayOverlayHook;
		}
		return base.BodyPreview.HeadSocket;
	}

	public override void EatParticles()
	{
		if (!(mouthBone == null))
		{
			eatP = MonoSingleton<ParticleSystemPool>.Instance.PlayParticles("eating", mouthBone, autoStop: false);
		}
	}

	public override void StopEatParticles()
	{
		if (!(eatP == null))
		{
			MonoSingleton<ParticleSystemPool>.Instance.ReturnToPool(eatP);
			eatP = null;
		}
	}

	public override void OnCarcassProximityEnter(WorldObject worldObject)
	{
		if (worldObject is ResourcePileInstance resourcePileInstance)
		{
			Transform transform = resourcePileInstance.GetTransform();
			if (!(transform == null) && transform.TryGetComponent<CarcassBirds>(out var component))
			{
				component.ScareEatingBirds();
			}
		}
	}

	public bool ShouldMenuFollowHookTransform()
	{
		return true;
	}

	public void OnPullOutTool(string toolID, Transform socket = null)
	{
		if (string.IsNullOrEmpty(toolID))
		{
			return;
		}
		if (socket == null)
		{
			socket = base.BodyPreview.RightHandSocket;
		}
		if (!tools.ContainsKey(toolID))
		{
			GameObject byAddress = MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByAddress(toolID);
			if (byAddress == null)
			{
				return;
			}
			GameObject gameObject = UnityEngine.Object.Instantiate(byAddress, socket);
			gameObject.SetClearName();
			tools.Add(toolID, gameObject);
		}
		animator.RebindKeepState();
		tools[toolID].SetActive(value: true);
		currentRightHandItem = toolID;
		MonoSingleton<AnimationController>.Instance.SetAnimatorParameter(GetAgentOwner(), toolID, value: true);
	}

	public void OnHideTool()
	{
		if (currentRightHandItem != null && tools.ContainsKey(currentRightHandItem))
		{
			tools[currentRightHandItem].SetActive(value: false);
			MonoSingleton<AnimationController>.Instance.SetAnimatorParameter(GetAgentOwner(), currentRightHandItem, value: false);
		}
	}

	public GameObject GetCurrentTool()
	{
		if (currentRightHandItem != null && tools.ContainsKey(currentRightHandItem))
		{
			return tools[currentRightHandItem];
		}
		return null;
	}

	public void OnStorageChange(SimpleResourceCount count)
	{
		RefreshBagPack();
	}

	public void OnStorageChange(ResourceInstance resource)
	{
		RefreshBagPack();
	}

	public void OnFoodStorageChange()
	{
		WorkerBodyPreview workerBodyPreview = (WorkerBodyPreview)base.BodyPreview;
		if (!(workerBodyPreview == null))
		{
			workerBodyPreview.SetFoodPouchEnabled(!humanoidInstance.FoodStorage.IsEmpty());
		}
	}

	public void OnMedicineStorageChange()
	{
		WorkerBodyPreview workerBodyPreview = (WorkerBodyPreview)base.BodyPreview;
		if (!(workerBodyPreview == null))
		{
			workerBodyPreview.SetMedicinePouchEnabled(!humanoidInstance.MedicineStorage.IsEmpty());
		}
	}

	public void RefreshBagPack()
	{
		if (humanoidInstance == null || humanoidInstance.HasDisposed || humanoidInstance.HasDied)
		{
			return;
		}
		bool active = ((WorkerGoapAgent)humanoidInstance.GetGoapAgent()).IsFormingCaravan || !humanoidInstance.Storage.IsEmpty();
		Transform transform = ((WorkerBodyPreview)base.BodyPreview)?.Basket;
		if (transform != null)
		{
			transform.gameObject.SetActive(active);
			return;
		}
		bool isEnabled;
		FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(44, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\View\\Humanoid\\WorkerView.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("Basket transform is destroyed for humanoid ");
			messageBuilder.AppendFormatted(humanoidInstance.Info.GetFullName());
			messageBuilder.AppendLiteral("!");
		}
		Log.Error(messageBuilder);
	}

	public void StopCoroutineExecution(string id)
	{
		StopCoroutine(id);
	}

	public void GenerateInDraftOverlayElement()
	{
		if (draftIndicator != null)
		{
			draftIndicator.gameObject.SetActive(value: true);
			return;
		}
		GameObject byAddress = MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByAddress("drafted_indicator");
		if (!(byAddress == null))
		{
			draftIndicator = UnityEngine.Object.Instantiate(byAddress, Vector3.zero, Quaternion.identity, base.transform).GetComponent<AgentCircleIndicator>();
			draftIndicator.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
			draftIndicator.gameObject.SetActive(value: true);
			UpdateCombatFootCircleIndicator();
		}
	}

	public void DestroyInDraftOverlayElement()
	{
		if (draftIndicator != null)
		{
			draftIndicator.gameObject.SetActive(value: false);
		}
	}

	public void GeneratePsychoticOverlayElement()
	{
		if (this == null)
		{
			Log.Warning("Humanoid view is NULL! This should never happen " + humanoidInstance, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\View\\Humanoid\\WorkerView.cs");
		}
		else
		{
			if (psychoticTextOverlayElement != null)
			{
				return;
			}
			if (humanoidInstance == null || humanoidInstance.HasDisposed)
			{
				Log.Error("Tried to generate psyhotic element on disposed humanoid " + humanoidInstance, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\View\\Humanoid\\WorkerView.cs");
				return;
			}
			psychoticTextOverlayElement = FloatingElementFactory.ProduceTextElement(OverlayTextElementType.Default, FloatingElementHolderType.Default, GetGuiOverlayHookTransform(), AssetUtils.GetSpriteAsset("psychotic"));
			if (psychoticTextOverlayElement == null)
			{
				Log.Error("This should never happen! " + humanoidInstance, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\View\\Humanoid\\WorkerView.cs");
				return;
			}
			if (workerNameElement != null)
			{
				psychoticTextOverlayElement.SetIndex(workerNameElement.GetIndex() + 1);
			}
			else
			{
				psychoticTextOverlayElement.SetIndex(0);
			}
			WorkerNameColorChange("Rebellious");
			if (psychoticIndicator != null)
			{
				psychoticIndicator.SetActive(value: true);
				return;
			}
			GameObject byAddress = MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByAddress("psychotic_indicator");
			if (byAddress == null)
			{
				Log.Warning("Could not generate 'psychotic_indicator'. Prefab not found!", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\View\\Humanoid\\WorkerView.cs");
				return;
			}
			psychoticIndicator = UnityEngine.Object.Instantiate(byAddress, Vector3.zero, Quaternion.identity, base.transform);
			psychoticIndicator.transform.localPosition = Vector3.zero;
			psychoticIndicator.SetActive(value: true);
		}
	}

	public void DestroyPsychoticOverlayElement()
	{
		if (!(psychoticTextOverlayElement == null))
		{
			psychoticTextOverlayElement.Dispose();
			psychoticTextOverlayElement = null;
			if (psychoticIndicator != null)
			{
				psychoticIndicator.SetActive(value: false);
			}
			WorkerNameColorChange("Normal");
		}
	}

	public void ShowPathDestinationLine(Vector3 pos)
	{
		if (lineRendererCache == null)
		{
			GameObject gameObject = UnityEngine.Object.Instantiate(MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByID("DraftPathLine").Value, base.transform);
			lineRendererCache = gameObject.GetComponentInChildren<LineRenderer>();
		}
		Vector3 position = humanoidInstance.GetPosition();
		lineRendererCache.SetPosition(0, position);
		lineRendererCache.SetPosition(1, pos);
		lineRendererCache.positionCount = 2;
		lineRendererCache.gameObject.SetActive(value: true);
		hideLineTimer = 1f;
		MonoSingleton<PathRenderManager>.Instance.HideDriverPath(humanoidInstance.PathDriver);
		if (!PathfinderUtil.IsPathPossible(humanoidInstance, GridUtils.GetGridPosition(pos)))
		{
			LineRenderer lineRenderer = lineRendererCache;
			Color startColor = (lineRendererCache.endColor = Color.red);
			lineRenderer.startColor = startColor;
		}
		else
		{
			LineRenderer lineRenderer2 = lineRendererCache;
			Color startColor = (lineRendererCache.endColor = Color.white);
			lineRenderer2.startColor = startColor;
		}
	}

	public void CancelManualAttack()
	{
		if (manualAttackTargeting)
		{
			manualAttackTargeting = false;
			MonoSingleton<UIController>.Instance.ToggleInfoCursor(active: false);
			MonoSingleton<InputManager>.Instance.ClearTemporaryListeners();
			SetActionInfo(new List<string>
			{
				ActionInfoUtils.RightClickEquip,
				ActionInfoUtils.RightClickAttack
			});
		}
	}

	public void Dispose()
	{
		Dispose(disposeInstance: true);
	}

	public void Dispose(bool disposeInstance)
	{
		if (this == null || base.HasDisposed)
		{
			return;
		}
		UpdateRangeSphere(itemDropped: true);
		CancelManualAttack();
		DestroyAnimatedAgent();
		if (MonoSingleton<OptionsController>.IsInstantiated())
		{
			MonoSingleton<OptionsController>.Instance.ToggleWorkerNames -= OnToggleWorkerNames;
		}
		if (MonoSingleton<CombatController>.IsInstantiated())
		{
			MonoSingleton<CombatController>.Instance.DamageTakenEvent -= OnHitTaken;
			MonoSingleton<CombatController>.Instance.HitBlockedEvent -= OnHitTaken;
			MonoSingleton<CombatController>.Instance.HitMissedEvent -= OnHitMissed;
		}
		if (MonoSingleton<WorkerController>.IsInstantiated())
		{
			MonoSingleton<WorkerController>.Instance.HourTypeChangeEvent -= OnHourChange;
			MonoSingleton<WorkerController>.Instance.WorkerNameChangedEvent -= OnWorkerNameChange;
		}
		if (MonoSingleton<LifeController>.IsInstantiated())
		{
			MonoSingleton<LifeController>.Instance.OnFaintEvent -= OnFaint;
		}
		if (MonoSingleton<SceneController>.IsInstantiated())
		{
			MonoSingleton<SceneController>.Instance.SceneSetup -= RebindOnGameplayStart;
		}
		visualsUpdateTimer?.Dispose();
		visualsUpdateTimer = null;
		overheadUIUpdateTimer?.Dispose();
		overheadUIUpdateTimer = null;
		HumanoidInstance humanoidInstance = base.humanoidInstance;
		if (humanoidInstance != null && humanoidInstance.IsStatsInitialized && humanoidInstance.Stats != null && !humanoidInstance.Stats.HasDisposed)
		{
			humanoidInstance.Stats.OnEffectorStartEvent -= OnEffector;
			humanoidInstance.Stats.OnEffectorEndEvent -= OnEffector;
			humanoidInstance.Stats.Controller?.RemoveListener(OnStatUpdated);
			humanoidInstance.WorkerBehaviour.CombatModeChangeEvent -= OnCombatModeChanged;
			if (MonoSingleton<RoleManager>.IsInstantiated())
			{
				MonoSingleton<RoleManager>.Instance.RoleChangedEvent -= OnRoleOwnerChanged;
			}
		}
		if (healthBar != null)
		{
			healthBar.OnDisposedEvent -= OnHealthBarDisposed;
			healthBar.Dispose();
			healthBar = null;
		}
		if (disposeInstance)
		{
			base.HumanoidInstance?.Dispose();
		}
		base.HasDisposed = true;
		if (!LoadingController.IsLeavingMainScene)
		{
			this.OnDisposedEvent?.Invoke(this);
		}
		this.OnDisposedEvent = null;
		if (base.gameObject != null)
		{
			UnityEngine.Object.Destroy(base.gameObject);
		}
	}

	public void AddEffectors(ref List<string> infos)
	{
		if (humanoidInstance.Stats.GetActiveEffectors().Count == 0)
		{
			return;
		}
		List<string> list = new List<string>();
		infos.Add("-----Classic effectors:");
		foreach (ActiveEffectorInfo effector in humanoidInstance.Stats.GetActiveEffectors())
		{
			if (effector.Blueprint is StatEffectorWound)
			{
				float f = ((StatEffectorWound)effector.Blueprint).SeverityMin * (float)GlobalSaveController.CurrentVillageData.DateAndTime.MinutesInHour;
				float f2 = effector.TimeLeft();
				list.Add($" {effector.Name} STACK: {effector.StackCount + 1}");
				list.Add($"  Severity: {Mathf.RoundToInt(f2)} Min: {Mathf.RoundToInt(f)}");
				list.Add(humanoidInstance.GetTendedWounds().Any((ActiveEffectorInfo item) => item.Name.Equals(effector.Name)) ? "  Tended" : "  Not tended! Bleeding");
			}
			else if (effector.Duration > 0f)
			{
				infos.Add($" {effector.Name}X{effector.StackCount + 1} Time:{1f - effector.ExpirationPercentage()}");
			}
			else
			{
				infos.Add($" {effector.Name}X{effector.StackCount + 1} T:inf");
			}
		}
		if (list.Count > 0)
		{
			infos.Add("-----Wounds: ");
			infos.AddRange(list);
		}
	}

	public override string GetMultiselectName()
	{
		return "worker";
	}

	public override string GetSimpleName()
	{
		return MonoSingleton<LocalizationController>.Instance.GetText("general_villager");
	}

	public void StartDraft()
	{
		if (!humanoidInstance.WorkerBehaviour.IsDrafting)
		{
			MonoSingleton<AudioManager>.Instance.PlaySound("UI_Worker_Draft");
			MonoSingleton<DraftController>.Instance.OnStartDraft(humanoidInstance);
			UpdateRangeSphere();
			SetActionInfo(new List<string>
			{
				ActionInfoUtils.RightClickMove,
				ActionInfoUtils.RightClickEquip,
				ActionInfoUtils.RightClickAttack
			});
			StopEatParticles();
		}
	}

	public void EndDraft()
	{
		if (humanoidInstance.WorkerBehaviour.IsDrafting)
		{
			MonoSingleton<AudioManager>.Instance.PlaySound("UI_Worker_Undraft");
			MonoSingleton<DraftController>.Instance.OnEndDraft(humanoidInstance);
			UpdateRangeSphere();
			SetActionInfo(new List<string> { ActionInfoUtils.RightClickEquip });
		}
	}

	public void ShowExperienceAddedPopup(SkillType skill, int amount)
	{
		if (!LoadingController.IsSceneTransition)
		{
			if (xpPopup == null)
			{
				xpPopup = FloatingElementFactory.ProduceXpPopupElement(GetGuiOverlayHookTransform());
			}
			if (!(xpPopup == null))
			{
				xpPopup.XpGained(skill, amount);
			}
		}
	}

	public void PlayParticlesOnSkillUp()
	{
		MonoSingleton<ParticleSystemPool>.Instance.PlayParticles(skillupParticles, base.transform);
		MonoSingleton<CameraManager>.Instance.OnCameraShakeEvent(base.transform.position, CameraShakeStrength.Weak);
	}

	protected override void OnPointerEnter(Vector3 pos)
	{
		if (MonoSingleton<SelectionManager>.Instance.OrderType == OrderType.None)
		{
			base.OnPointerEnter(pos);
		}
	}

	internal override void Select()
	{
		if (MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.IsWorldMapVisible)
		{
			MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.SetWorldMapVisible(isWorldMapVisible: false);
		}
		if (MonoSingleton<KeybindingManager>.Instance.IsKeybindingKeyDown(KeyInputEvent.LeftControl))
		{
			ClickedJumpToUpperLayer();
			return;
		}
		base.Select();
		if (base.Selected)
		{
			UpdateRangeSphere();
			if (humanoidInstance.PathDriver != null)
			{
				MonoSingleton<PathRenderManager>.Instance.RenderDriverPath(humanoidInstance.PathDriver);
			}
			if (MonoSingleton<SelectableObjectManager>.Instance.IsMultipleSelected)
			{
				SetActionInfo(null);
				return;
			}
			SetActionInfo(new List<string> { ActionInfoUtils.RightClickEquip });
		}
	}

	internal override void Deselect(bool isSilent = false)
	{
		base.Deselect(isSilent);
		CancelManualAttack();
		UpdateRangeSphere();
		SetActionInfo(null);
		MonoSingleton<PathRenderManager>.Instance.HideDriverPath(humanoidInstance.PathDriver);
	}

	protected override void MouseOnEnter()
	{
		base.MouseOnEnter();
		UpdateRangeSphere();
	}

	protected override void MouseOnExit()
	{
		base.MouseOnExit();
		if (!humanoidInstance.WorkerBehaviour.IsDrafting || !base.Selected)
		{
			UpdateRangeSphere();
		}
	}

	protected override string GetGoapAgentId()
	{
		return "worker";
	}

	protected override StatsInstance GetAgentStats()
	{
		return humanoidInstance.Stats;
	}

	protected override string GetAnimatedAgentDataId()
	{
		return humanoidInstance.GetGoapAgentID();
	}

	protected override void OnDestroy()
	{
		if (MonoSingleton<OptionsController>.IsInstantiated())
		{
			MonoSingleton<OptionsController>.Instance.ToggleWorkerNames -= OnToggleWorkerNames;
		}
		humanoidInstance = null;
		base.OnDestroy();
		if (MonoSingleton<WorkerController>.IsInstantiated())
		{
			MonoSingleton<WorkerController>.Instance.WorkerNameChangedEvent -= OnWorkerNameChange;
		}
	}

	private void UpdateRangeSphere(bool itemDropped = false)
	{
		if (!itemDropped && base.Selected)
		{
			WorkerBehaviour workerBehaviour = humanoidInstance.WorkerBehaviour;
			if (workerBehaviour != null && workerBehaviour.IsDrafting && CombatUtils.GetAttackType(humanoidInstance) != AttackType.Melee)
			{
				ShowRangeSphere();
				return;
			}
		}
		HideRangeSphere();
	}

	private void ShowRangeSphere()
	{
		float range = CombatUtils.GetRange(humanoidInstance, null);
		MonoSingleton<SphereRenderManager>.Instance.Show(base.transform, range + 0.25f, SphereRenderType.ArcherRange);
		isRangeSphereShown = true;
	}

	private void HideRangeSphere()
	{
		if (isRangeSphereShown && MonoSingleton<SphereRenderManager>.IsInstantiated())
		{
			MonoSingleton<SphereRenderManager>.Instance.Hide(SphereRenderType.ArcherRange);
			isRangeSphereShown = false;
		}
	}

	private void SetActionInfo(List<string> actionInfos)
	{
		if (actionInfos == null)
		{
			MonoSingleton<UIController>.Instance.HideActionInfo();
		}
		else
		{
			MonoSingleton<UIController>.Instance.ShowActionInfo(actionInfos);
		}
	}

	private void GenerateWorkerNameGuiOverlayElements()
	{
		if (MonoSingleton<GlobalSaveController>.Instance.GlobalSettings.ShowWorkerNames && !(workerNameElement != null))
		{
			workerNameElement = FloatingElementFactory.ProduceTextElement(OverlayTextElementType.WorkerName, FloatingElementHolderType.Default, GetGuiOverlayHookTransform());
			OnWorkerNameChange();
		}
	}

	private void OnWorkerNameChange(HumanoidInstance humanoidInstance = null)
	{
		if ((humanoidInstance == null || base.humanoidInstance == humanoidInstance) && !(workerNameElement == null))
		{
			workerNameElement.SetText(base.HumanoidInstance.ActiveBehaviour.HumanoidRoleOwner.GetDefaultDisplayNameRole());
		}
	}

	private void WorkerNameColorChange(string style)
	{
		if (workerNameElement != null)
		{
			workerNameElement.SetStyle(style);
		}
	}

	private void DestroyWorkerNameGuiOverlayElement()
	{
		if (!(workerNameElement == null))
		{
			workerNameElement.Dispose();
			workerNameElement = null;
		}
	}

	private void OnToggleWorkerNames(bool isOn)
	{
		if (!(this == null) && !(base.transform == null) && !(workerNameElement != null && isOn) && (isOn || !(workerNameElement == null)))
		{
			if (isOn)
			{
				GenerateWorkerNameGuiOverlayElements();
			}
			else
			{
				DestroyWorkerNameGuiOverlayElement();
			}
		}
	}

	private void OnHitMissed(IDamageDealAgent deal, IDamageTakingAgent take, CombatMissType missType)
	{
		if (take == humanoidInstance && (CombatUtils.GetAttackType(deal) == AttackType.Melee || missType == CombatMissType.Evade) && HandleDamagePopup())
		{
			damagePopup.HitMessed(missType);
		}
	}

	private void OnHitTaken(IDamageDealAgent deal, IDamageTakingAgent take, CombatHitInfo hitInfo)
	{
		if (take == humanoidInstance)
		{
			if (healthBar != null && hitInfo.Damage > 0f)
			{
				QuickWiggleEffect.WiggleX(healthBar.transform.GetChild(0), 0.25f, 16f);
			}
			if (HandleDamagePopup())
			{
				damagePopup.FireDamage(hitInfo);
			}
		}
	}

	private bool HandleDamagePopup()
	{
		if (damagePopup == null)
		{
			damagePopup = FloatingElementFactory.ProduceDamagePopupElement(GetGuiOverlayHookTransform());
		}
		return damagePopup != null;
	}

	public override InfoPanelData GetInfoPanelData()
	{
		InfoPanelHeader header = new InfoPanelHeader("Worker_" + GlobalSaveController.CurrentVillageData.Workers.IndexOf(humanoidInstance) + ".png", humanoidInstance.Info.GetFullName(), "worker");
		InfoPanelWorkerBody body = new InfoPanelWorkerBody(stats: HumanoidView.GetInfoStats(humanoidInstance), infos: GetInfos(), iconId: GlobalSaveController.CurrentVillageData.Workers.IndexOf(humanoidInstance), humanoid: humanoidInstance);
		InfoPanelFooter footer = new InfoPanelFooter(GetFooterData(), humanoidInstance);
		return new InfoPanelData(header, body, footer);
	}

	public override InfoPanelData UpdateCallback()
	{
		if (humanoidInstance == null || humanoidInstance.HasDisposed || humanoidInstance.HasDied)
		{
			return null;
		}
		return GetInfoPanelData();
	}

	private List<string> GetInfos()
	{
		List<string> infos = new List<string>();
		HourType currentHour = humanoidInstance.ScheduleHours[GlobalSaveController.CurrentVillageData.DateAndTime.HoursSinceDay];
		ScheduleData scheduleData = Repository<ScheduleModelRepository, ScheduleModel>.Instance.GetByID("worker").Schedule.FirstOrDefault((ScheduleData sd) => sd.HourType == currentHour);
		infos.Add(MonoSingleton<LocalizationController>.Instance.GetText("hud_lb_schedule") + ": <style=AltColor>" + MonoSingleton<LocalizationController>.Instance.GetText(LocKeyUtils.GetName(scheduleData.LocKeys), humanoidInstance) + "</style>");
		Room singleOwnerRoom = humanoidInstance.Map.RoomDetection.GetSingleOwnerRoom(humanoidInstance);
		string text = MonoSingleton<LocalizationController>.Instance.GetText("room_type_bedroom_individual").Replace("<name>", humanoidInstance.Info.FirstName);
		string text2 = MonoSingleton<LocalizationController>.Instance.GetText((singleOwnerRoom != null) ? "room_in_selection" : "general_none");
		if (singleOwnerRoom != null && singleOwnerRoom.RoomType != null)
		{
			string text3 = ColorUtility.ToHtmlStringRGB(singleOwnerRoom.RoomType.Color);
			infos.Add(text + ": <color=#" + text3 + "><link=\"select_room\"><style=LinkRoom>" + text2 + "</style></link></color>");
		}
		else
		{
			infos.Add(text + ": " + text2);
		}
		string localizedCurrentActionInfo = CreatureBaseUtils.GetLocalizedCurrentActionInfo(humanoidInstance);
		if (!string.IsNullOrEmpty(localizedCurrentActionInfo))
		{
			infos.Add("<style=Desc>" + localizedCurrentActionInfo + "</style>");
		}
		string overweightMessage = HumanoidView.GetOverweightMessage(humanoidInstance);
		if (!string.IsNullOrEmpty(overweightMessage))
		{
			infos.Add(overweightMessage);
		}
		AnimatedAgentView.FillAttackersInfo(ref infos, humanoidInstance);
		return infos;
	}

	private void FillDebugInfo(ref List<string> infos)
	{
		if (base.humanoidInstance == null)
		{
			return;
		}
		using PooledList<IReservable> pooledList = MonoSingleton<ReservationManager>.Instance.GetReservedBy(base.humanoidInstance);
		List<string> list = new List<string>();
		foreach (IReservable item2 in pooledList)
		{
			string item = item2.GetType().Name;
			if (item2 is WorldObject worldObject)
			{
				item = $"({item2.GetType().Name}, {worldObject.GridDataPosition})";
			}
			if (item2 is AnimalInstance animalInstance)
			{
				item = $"({animalInstance.Blueprint.GetID()}: {animalInstance.AnimalType}, {animalInstance.GetFullName()})";
			}
			if (item2 is HumanoidInstance { WorkerBehaviour: not null } humanoidInstance)
			{
				item = "(" + humanoidInstance.Info.GetFullName() + ")";
			}
			if (item2 is HumanoidInstance humanoidInstance2 && humanoidInstance2.IsNpc())
			{
				item = $"({humanoidInstance2.Info.GetFullName()} (hostile: {humanoidInstance2.Faction.IsHostile()}, aggressive: {humanoidInstance2.IsEnemy()}))";
			}
			list.Add(item);
		}
		infos.Add("[Dev] climbing: " + base.humanoidInstance.PathDriver.ClimbDirection);
		infos.Add($"[Dev] on fire: {base.humanoidInstance.IsOnFire}");
		infos.Add("[Dev] reserved: " + string.Join(", ", list));
	}

	private List<InfoPanelAction> GetFooterData()
	{
		if (TutorialManager.IsTutorialActive && !MonoSingleton<TutorialManager>.Instance.AllowCreatureCommands)
		{
			return new List<InfoPanelAction>();
		}
		if (base.HumanoidInstance.WorkerBehaviour.IsBanished)
		{
			return new List<InfoPanelAction>();
		}
		if (base.HumanoidInstance.IsAtEvent())
		{
			KeyValuePair<SelectionInputActionData, Action>[] objectActions = new KeyValuePair<SelectionInputActionData, Action>[1]
			{
				new KeyValuePair<SelectionInputActionData, Action>(Repository<ObjectActionDataRepository, SelectionInputActionData>.Instance.GetByID("AbortEventParticipation"), delegate
				{
					List<KeyValuePair<string, Action>> buttonActions = new List<KeyValuePair<string, Action>>
					{
						new KeyValuePair<string, Action>(MonoSingleton<LocalizationController>.Instance.GetText("general_yes"), delegate
						{
							OnEventAbort();
						}),
						new KeyValuePair<string, Action>(MonoSingleton<LocalizationController>.Instance.GetText("general_no"), delegate
						{
						})
					};
					MonoSingleton<UIController>.Instance.ShowPrompt(new PromptPanelData("abort_event_participation_confirm", buttonActions));
				})
			};
			return new List<InfoPanelAction>
			{
				new InfoPanelAction(objectActions)
			};
		}
		List<InfoPanelAction> list = new List<InfoPanelAction>();
		int currentIndex = (base.HumanoidInstance.WorkerBehaviour.IsDrafting ? 1 : 0);
		KeyValuePair<SelectionInputActionData, Action>[] objectActions2 = new KeyValuePair<SelectionInputActionData, Action>[2]
		{
			new KeyValuePair<SelectionInputActionData, Action>(Repository<ObjectActionDataRepository, SelectionInputActionData>.Instance.GetByID("Draft"), ToggleDraftMultiple),
			new KeyValuePair<SelectionInputActionData, Action>(Repository<ObjectActionDataRepository, SelectionInputActionData>.Instance.GetByID("Drafted"), ToggleDraftMultiple)
		};
		list.Add(new InfoPanelAction(objectActions2, currentIndex));
		if (base.HumanoidInstance != null && base.HumanoidInstance.WorkerBehaviour.IsDrafting)
		{
			WorkerView agentView = base.HumanoidInstance.GetAgentView<WorkerView>();
			int currentIndex2 = (((object)agentView != null && agentView.ManualAttackTargeting) ? 1 : 0);
			KeyValuePair<SelectionInputActionData, Action>[] objectActions3 = new KeyValuePair<SelectionInputActionData, Action>[2]
			{
				new KeyValuePair<SelectionInputActionData, Action>(Repository<ObjectActionDataRepository, SelectionInputActionData>.Instance.GetByID("Attack"), ManualAttack),
				new KeyValuePair<SelectionInputActionData, Action>(Repository<ObjectActionDataRepository, SelectionInputActionData>.Instance.GetByID("Attacking"), ManualAttack)
			};
			list.Add(new InfoPanelAction(objectActions3, currentIndex2));
			bool flag = false;
			EquipmentInstance weapon = CombatUtils.GetWeapon(humanoidInstance);
			if (weapon != null)
			{
				flag = weapon.CanFireFlammableProjectiles;
			}
			if (flag)
			{
				int currentIndex3 = (base.HumanoidInstance.FlammableProjectilesAllowed ? 1 : 0);
				KeyValuePair<SelectionInputActionData, Action>[] objectActions4 = new KeyValuePair<SelectionInputActionData, Action>[2]
				{
					new KeyValuePair<SelectionInputActionData, Action>(Repository<ObjectActionDataRepository, SelectionInputActionData>.Instance.GetByID("AllowFlammableProjectile"), OnFlammableProjectileButtonToggle),
					new KeyValuePair<SelectionInputActionData, Action>(Repository<ObjectActionDataRepository, SelectionInputActionData>.Instance.GetByID("FlammableProjectileAllowed"), OnFlammableProjectileButtonToggle)
				};
				list.Add(new InfoPanelAction(objectActions4, currentIndex3));
			}
		}
		else
		{
			KeyValuePair<SelectionInputActionData, Action>[] objectActions5 = new KeyValuePair<SelectionInputActionData, Action>[1]
			{
				new KeyValuePair<SelectionInputActionData, Action>(Repository<ObjectActionDataRepository, SelectionInputActionData>.Instance.GetByID("Banish"), Banish)
			};
			list.Add(new InfoPanelAction(objectActions5));
		}
		KeyValuePair<SelectionInputActionData, Action>[] objectActions6 = new KeyValuePair<SelectionInputActionData, Action>[1]
		{
			new KeyValuePair<SelectionInputActionData, Action>(Repository<ObjectActionDataRepository, SelectionInputActionData>.Instance.GetByID("AbortGoal"), AbortGoalActionHandler)
		};
		list.Add(new InfoPanelAction(objectActions6));
		return list;
	}

	private void OnEventAbort()
	{
		MonoSingleton<PlayerTriggeredEventManager>.Instance.CurrentEvent?.RemoveParticipantFromEvent(humanoidInstance);
		UpdateViewImmediate();
	}

	private void Banish()
	{
		MonoSingleton<AudioManager>.Instance.PlaySound("UI_Worker_Banish");
		humanoidInstance.WorkerBehaviour.Banish();
	}

	private void ManualAttack()
	{
		if (manualAttackTargeting || humanoidInstance.WorkerBehaviour.IsBanished)
		{
			CancelManualAttack();
			return;
		}
		if (humanoidInstance.WorkerBehaviour.IsCrazy)
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("settler_is_rebellious"));
			CancelManualAttack();
			return;
		}
		if (humanoidInstance.HasFainted)
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("has_fainted", humanoidInstance) ?? "");
			CancelManualAttack();
			return;
		}
		MonoSingleton<AudioManager>.Instance.PlaySound("UI_Worker_Attack");
		manualAttackTargeting = true;
		MonoSingleton<UIController>.Instance.UpdateInfoCursorContent("order_attack", background: false, 2f);
		MonoSingleton<InputManager>.Instance.RegisterTemporaryListener(new ManualAttackInputListener(CancelManualAttack));
		SetActionInfo(new List<string>
		{
			ActionInfoUtils.RightClickEquip,
			ActionInfoUtils.Attack
		});
	}

	private void AbortGoalActionHandler()
	{
		if (humanoidInstance == null || humanoidInstance.HasDisposed)
		{
			return;
		}
		if (humanoidInstance.HasFainted)
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("has_fainted", humanoidInstance) ?? "");
			return;
		}
		if (humanoidInstance.WorkerBehaviour.IsCrazy)
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("settler_is_rebellious"));
			return;
		}
		WorkerGoapAgent goapAgent = (WorkerGoapAgent)humanoidInstance.GetGoapAgent();
		if (goapAgent == null)
		{
			return;
		}
		if (goapAgent.IsFormingCaravan)
		{
			List<KeyValuePair<string, Action>> list = new List<KeyValuePair<string, Action>>();
			list.Add(new KeyValuePair<string, Action>(MonoSingleton<LocalizationController>.Instance.GetText("general_yes"), delegate
			{
				if (goapAgent.IsFormingCaravan)
				{
					MonoSingleton<CaravanFormingManager>.Instance.CancelCaravanForming(goapAgent.PreparingForCaravan);
				}
			}));
			list.Add(new KeyValuePair<string, Action>(MonoSingleton<LocalizationController>.Instance.GetText("general_no"), null));
			MonoSingleton<UIController>.Instance.ShowPrompt(new PromptPanelData("cancel_caravan_confirm", list));
		}
		else
		{
			MonoSingleton<CombatTargetManager>.Instance.RemovePreferredTarget(humanoidInstance);
			MonoSingleton<ReservationManager>.Instance.ClearPreferredReservable(humanoidInstance);
			humanoidInstance.ForceEatPile = null;
			humanoidInstance.CombatAi?.Abort();
			humanoidInstance.WorkerBehaviour.LastDraftOrder?.OnDraftEnd(humanoidInstance);
			goapAgent.ForceNextGoalExclusive(null);
			goapAgent.Abort();
			goapAgent.DelayNextTick(1.5f);
		}
	}

	public void ToggleDraftSingle()
	{
		CancelManualAttack();
		UpdateViewImmediate();
		if (humanoidInstance.WorkerBehaviour.ForcedWorkHour == HourType.PsyhoticCrazy)
		{
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("can_not_draft_crazy_worker"));
		}
		else if (base.HumanoidInstance.WorkerBehaviour.ForcedWorkHour != HourType.Draft)
		{
			StartDraft();
		}
		else
		{
			EndDraft();
		}
	}

	public void ToggleDraftMultiple()
	{
		using PooledList<WorkerView> pooledList = MonoSingleton<SelectableObjectManager>.Instance.GetSelectedPooled<WorkerView>();
		if (pooledList.Count == 0)
		{
			pooledList.Add(this);
		}
		if (this != pooledList[0])
		{
			return;
		}
		WorkerView workerView = null;
		foreach (WorkerView item in pooledList)
		{
			if (item != null && item.HumanoidInstance != null && !item.HumanoidInstance.HasDisposed && item.humanoidInstance.WorkerBehaviour != null)
			{
				workerView = item;
				break;
			}
		}
		if (workerView == null)
		{
			return;
		}
		bool flag = workerView.HumanoidInstance.WorkerBehaviour.ForcedWorkHour != HourType.Draft;
		foreach (WorkerView item2 in pooledList)
		{
			if (!(item2 == null) && item2.humanoidInstance?.WorkerBehaviour != null)
			{
				item2.CancelManualAttack();
				item2.UpdateViewImmediate();
				if (item2.humanoidInstance.WorkerBehaviour.ForcedWorkHour == HourType.PsyhoticCrazy)
				{
					MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(MonoSingleton<LocalizationController>.Instance.GetText("can_not_draft_crazy_worker"));
				}
				else if (flag)
				{
					item2.StartDraft();
				}
				else
				{
					item2.EndDraft();
				}
			}
		}
	}

	private void OnFlammableProjectileButtonToggle()
	{
		using PooledList<WorkerView> pooledList = MonoSingleton<SelectableObjectManager>.Instance.GetSelectedPooled<WorkerView>();
		if (pooledList.Count == 0)
		{
			Log.Error("Flammable projectiles button clicked, but no workers selected. This should not happen, aborting action", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\View\\Humanoid\\WorkerView.cs");
		}
		else
		{
			if (this != pooledList[0])
			{
				return;
			}
			bool flammableProjectilesAllowed = !base.HumanoidInstance.FlammableProjectilesAllowed;
			{
				foreach (WorkerView item in pooledList)
				{
					if (item.HumanoidInstance.WorkerBehaviour.IsDrafting)
					{
						item.HumanoidInstance.FlammableProjectilesAllowed = flammableProjectilesAllowed;
						item.UpdateViewImmediate();
					}
				}
				return;
			}
		}
	}

	private void OnStatUpdated(object statInstance)
	{
		if (statInstance == null || !(statInstance is StatInstance { Type: StatType.Health } statInstance2))
		{
			return;
		}
		if (statInstance2.Current.IsCloseTo(statInstance2.Max, 0.1f))
		{
			if (!(healthBar == null))
			{
				healthBar.OnDisposedEvent -= OnHealthBarDisposed;
				healthBar.Dispose();
				healthBar = null;
			}
			return;
		}
		if (healthBar != null)
		{
			healthBar.SetValue(statInstance2.Current / statInstance2.Max);
			return;
		}
		healthBar = FloatingElementFactory.ProduceProgressBarElement<LinearProgressBarFloatingElement>(OverlayProgressBarType.LineGreen, FloatingElementHolderType.Default, GetGuiOverlayHookTransform());
		if (healthBar != null)
		{
			healthBar.SetIndex(0);
			healthBar.OnDisposedEvent += OnHealthBarDisposed;
			healthBar.SetTrailEnabled(enabled: true);
		}
	}

	private void OnHealthBarDisposed(IGameDisposable disposable)
	{
		healthBar = null;
	}

	public void UpdateParticlesAndAnimationParams()
	{
		if (humanoidInstance == null || humanoidInstance.HasDisposed)
		{
			visualsUpdateTimer?.Dispose();
			visualsUpdateTimer = null;
			return;
		}
		float value = 0f;
		if (humanoidInstance.IsBleeding)
		{
			value = humanoidInstance.Stats.GetStat(StatType.Blood).Step * -1f;
			if (value > 3f)
			{
				value = 3f;
			}
			value /= 3f;
			bleedingParticle.gameObject.SetActive(value: true);
		}
		else
		{
			bleedingParticle.gameObject.SetActive(value: false);
		}
		value = Mathf.Clamp(value, 0f, 1f);
		bleedingParticle.SetIntensity(value);
		bool value2 = !humanoidInstance.IsFallingDown && !humanoidInstance.OperatingSiegeWeapon && base.BodyPreview.IsWeaponVisible && (humanoidInstance.WorkerBehaviour.CombatMode.IsDrafted() || humanoidInstance.GetGoapAgent()?.GetCurrentGoal() is HuntingGoal);
		TrySetParameter("IsCombatAlert", value2);
	}

	private void UpdateOverheadUI()
	{
		if (humanoidInstance == null || humanoidInstance.HasDisposed)
		{
			overheadUIUpdateTimer?.Dispose();
			overheadUIUpdateTimer = null;
			return;
		}
		if (!humanoidInstance.IsNextRoundFlammable())
		{
			DestroyIconCircle(OverlayIconCircleType.IconHasFlammableProjectile);
			hasFlammableProjectileIcon = null;
			return;
		}
		hasFlammableProjectileIcon = GetIconCircle(OverlayIconCircleType.IconHasFlammableProjectile);
		if (!(hasFlammableProjectileIcon == null))
		{
			hasFlammableProjectileIcon.SetSprite(iconHasFlammableProjectile);
		}
	}

	protected override void Update()
	{
		base.Update();
		if ((object)lineRendererCache == null || hideLineTimer <= 0f)
		{
			if (base.Selected)
			{
				MonoSingleton<PathRenderManager>.Instance.RenderDriverPath(humanoidInstance.PathDriver);
			}
			return;
		}
		hideLineTimer -= Time.deltaTime;
		if (lineRendererCache.gameObject.activeSelf)
		{
			if (hideLineTimer <= 0f)
			{
				lineRendererCache.gameObject.SetActive(value: false);
				hideLineTimer = 0f;
				MonoSingleton<PathRenderManager>.Instance.RenderDriverPath(humanoidInstance.PathDriver);
			}
			else if (!base.Selected)
			{
				lineRendererCache.gameObject.SetActive(value: false);
				MonoSingleton<PathRenderManager>.Instance.HideDriverPath(humanoidInstance.PathDriver);
			}
			else
			{
				lineRendererCache.SetPosition(0, humanoidInstance.GetPosition());
			}
		}
		else if (base.Selected && hideLineTimer > 0f)
		{
			lineRendererCache.gameObject.SetActive(value: true);
			MonoSingleton<PathRenderManager>.Instance.HideDriverPath(humanoidInstance.PathDriver);
			hideLineTimer = 1f;
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
