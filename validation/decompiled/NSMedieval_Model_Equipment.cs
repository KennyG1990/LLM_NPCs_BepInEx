using System;
using System.Collections.Generic;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix.Base;
using NSEipix.Model;
using NSEipix.Repository;
using NSMedieval.Repository;
using NSMedieval.Types;
using NSMedieval.UI.Utils;
using UnityEngine;

namespace NSMedieval.Model;

[Serializable]
public class Equipment : NSEipix.Base.Model, ISerializationCallbackReceiver
{
	[SerializeField]
	private string id;

	[SerializeField]
	private EquipmentSlotType equipmentSlots;

	[SerializeField]
	private ItemType itemType;

	[SerializeField]
	private WeaponMode primaryWeaponMode;

	[SerializeField]
	private WeaponMode secondaryWeaponMode;

	[SerializeField]
	private List<SkillLevelPair> requiredSkills = new List<SkillLevelPair>();

	[SerializeField]
	private float decompositionCoefficient;

	[SerializeField]
	private bool hideHair;

	[SerializeField]
	private bool hideFacialHair;

	[SerializeField]
	private bool hideHead;

	[SerializeField]
	private float headMaskAmount;

	[SerializeField]
	private List<BoneEquipmentPair> additionalPrefabs;

	[SerializeField]
	private FloatRange warmthModifier;

	[SerializeField]
	private ArmorType armorType;

	[SerializeField]
	private GarmentType garmentType;

	[SerializeField]
	private float armorRating;

	[SerializeField]
	private EquipmentSlotType carryHand;

	[SerializeField]
	private float meleeCover;

	[SerializeField]
	private float rangedCover;

	[SerializeField]
	private float coverAngle = 180f;

	[SerializeField]
	private string[] onEquipEffectors;

	[SerializeField]
	private bool hideInGame;

	[SerializeField]
	private float beautyInputEquipped;

	[SerializeField]
	private float agentFlammability = -1f;

	[SerializeField]
	private float agentFireDamageMultiplier = -1f;

	[NonSerialized]
	private Resource resource;

	[NonSerialized]
	private ItemQuality itemQuality;

	[NonSerialized]
	private MaterialSettings materialSettings;

	[NonSerialized]
	private bool tainted;

	public override bool HideInGame => hideInGame;

	public EquipmentSlotType EquipmentSlots => equipmentSlots;

	public ItemType ItemType => itemType;

	public List<SkillLevelPair> RequiredSkills => requiredSkills;

	public float DecompositionCoefficient => decompositionCoefficient;

	public bool HideHair => hideHair;

	public bool HideFacialHair => hideFacialHair;

	public bool HideHead => hideHead;

	public float HeadMaskAmount => headMaskAmount;

	public List<BoneEquipmentPair> AdditionalPrefabs => additionalPrefabs;

	public FloatRange WarmthModifier => warmthModifier;

	public WeaponMode PrimaryWeaponMode => primaryWeaponMode;

	public WeaponMode SecondaryWeaponMode => secondaryWeaponMode;

	public string[] Effectors => onEquipEffectors;

	public float ArmorRating => armorRating;

	public EquipmentSlotType CarryHand => carryHand;

	public float CoverAngle => coverAngle;

	public string[] OnEquipEffectors => onEquipEffectors;

	public Resource Resource
	{
		get
		{
			if (resource == null)
			{
				resource = Repository<ResourceRepository, Resource>.Instance.GetByID(id);
			}
			return resource;
		}
	}

	public ArmorType ArmorType => armorType;

	public GarmentType GarmentType => garmentType;

	public ItemQuality ItemQuality => itemQuality;

	public float BeautyInputEquipped => beautyInputEquipped;

	public MaterialSettings MaterialSettings => materialSettings;

	public bool Tainted => tainted;

	public WeaponType PrimaryWeaponType => PrimaryWeaponMode?.WeaponType ?? WeaponType.None;

	public float PrimaryDamage => PrimaryWeaponMode?.Damage ?? 0f;

	public float PrimaryRange => PrimaryWeaponMode?.Range ?? 0f;

	public float PrimaryPrecision => PrimaryWeaponMode?.Precision ?? 0f;

	public float PrimaryPrecisionFalloff => PrimaryWeaponMode?.PrecisionFalloff ?? 0f;

	public float PrimaryAttackSpeed => PrimaryWeaponMode?.AttackSpeed ?? 0f;

	public float PrimaryArmorDamage => PrimaryWeaponMode?.ArmorDamage ?? 0f;

	public float PrimaryBuildingDamage => PrimaryWeaponMode?.BuildingDamage ?? 0f;

	public float PrimaryIgnoresArmor => PrimaryWeaponMode?.IgnoresArmor ?? 0f;

	public float AgentFlammability
	{
		get
		{
			if (agentFlammability <= -1f)
			{
				return 1f;
			}
			return agentFlammability * itemQuality.AgentFlammability * materialSettings.AgentFlammability;
		}
	}

	public float AgentFireDamageMultiplier
	{
		get
		{
			if (agentFireDamageMultiplier <= -1f)
			{
				return 1f;
			}
			return agentFireDamageMultiplier * itemQuality.AgentFireDamageMultiplier * materialSettings.AgentFireDamageMultiplier;
		}
	}

	public void OnBeforeSerialize()
	{
	}

	public void OnAfterDeserialize()
	{
		WeaponMode weaponMode = secondaryWeaponMode;
		if (weaponMode != null && weaponMode.WeaponType == WeaponType.None)
		{
			secondaryWeaponMode = null;
		}
	}

	public override string GetID()
	{
		return id;
	}

	public string GetSlotIcons()
	{
		string text = string.Empty;
		EquipmentSlotType[] equipmentSlotTypes = EnumValues.EquipmentSlotTypes;
		for (int i = 0; i < equipmentSlotTypes.Length; i++)
		{
			EquipmentSlotType equipmentSlotType = equipmentSlotTypes[i];
			if (equipmentSlotType != 0 && equipmentSlots.HasFlag(equipmentSlotType))
			{
				text += AssetUtils.GetSpriteAsset(equipmentSlotType.ToString().ToLower() + "_icon");
			}
		}
		return text;
	}

	public float GetBeautyInputEquipped()
	{
		return BeautyInputEquipped + ((ItemQuality != null) ? ItemQuality.BeautyInputEquippedAdd : 0f) + ((MaterialSettings != null) ? MaterialSettings.BeautyInputEquippedAdd : 0f);
	}

	public float GetCoverChance(DamageType damageType)
	{
		switch (damageType)
		{
		case DamageType.Melee:
			return meleeCover;
		case DamageType.Ranged:
			return rangedCover;
		default:
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(52, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\Items\\Equipment.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Unsupported damage type supplied to GetCoverChance: ");
				messageBuilder.AppendFormatted(damageType);
			}
			Log.Error(messageBuilder);
			return 0f;
		}
		}
	}

	public float GetCoverChance(DamageType damageType, Vector2 agentLookDirection, Vector2 damageDirectionWorldSpace)
	{
		Vector2 to = -damageDirectionWorldSpace;
		float num = Vector2.Angle(agentLookDirection, to);
		float num2 = coverAngle / 2f;
		if (num <= num2)
		{
			return GetCoverChance(damageType);
		}
		return 0f;
	}

	public bool CanBlockAttacks(DamageType damageType)
	{
		return GetCoverChance(damageType) > 0f;
	}

	public bool CanBlockAttacks()
	{
		DamageType[] damageTypesExcludingNone = EnumValues.DamageTypesExcludingNone;
		foreach (DamageType damageType in damageTypesExcludingNone)
		{
			if (CanBlockAttacks(damageType))
			{
				return true;
			}
		}
		return false;
	}

	public void SetupEquipmentWithQuality(string id, Equipment proto, WeaponMode primaryWeaponMode, WeaponMode secondaryWeaponMode, float decompositionCoefficient, FloatRange warmthModifier, float armorRating, string[] onEquipEffectors, ItemQuality quality, MaterialSettings materialSettings, bool tainted = false)
	{
		this.id = id;
		this.primaryWeaponMode = primaryWeaponMode;
		this.secondaryWeaponMode = secondaryWeaponMode;
		additionalPrefabs = proto.additionalPrefabs;
		agentFireDamageMultiplier = proto.agentFireDamageMultiplier;
		agentFlammability = proto.agentFlammability;
		armorType = proto.armorType;
		beautyInputEquipped = proto.beautyInputEquipped;
		equipmentSlots = proto.equipmentSlots;
		garmentType = proto.garmentType;
		headMaskAmount = proto.headMaskAmount;
		hideFacialHair = proto.hideFacialHair;
		hideHair = proto.hideHair;
		hideHead = proto.hideHead;
		itemType = proto.itemType;
		requiredSkills = proto.requiredSkills;
		this.armorRating = armorRating;
		carryHand = proto.carryHand;
		meleeCover = proto.meleeCover;
		rangedCover = proto.rangedCover;
		coverAngle = proto.coverAngle;
		this.decompositionCoefficient = decompositionCoefficient;
		itemQuality = quality;
		this.materialSettings = materialSettings;
		this.onEquipEffectors = onEquipEffectors;
		this.warmthModifier = warmthModifier;
		this.tainted = tainted;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
