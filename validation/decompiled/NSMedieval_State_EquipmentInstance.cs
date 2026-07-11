using System;
using System.Collections.Generic;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Controllers;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Serialization;
using NSMedieval.StatsSystem;
using NSMedieval.Types;
using UnityEngine;

namespace NSMedieval.State;

[Serializable]
[FVSerializableKey("EquipmentInstance", "")]
public class EquipmentInstance : IGameDisposable, IDisposable, IStatsOwner, IFVSerializable
{
	[SerializeField]
	private string id;

	[SerializeField]
	private bool isManuallyEquiped;

	[SerializeField]
	private int producerUniqueId;

	[SerializeField]
	private bool isSecondaryWeaponModeActive;

	private Equipment blueprint;

	private bool isNextProjectileFlammable;

	[NonSerialized]
	private StatsInstance stats;

	[NonSerialized]
	private CombatProjectile cachedProjectilePrefab;

	public bool IsNextRoundFlammable => isNextProjectileFlammable;

	public string Id => id;

	public bool HasDisposed { get; protected set; }

	public bool IsManuallyEquiped => isManuallyEquiped;

	public Equipment Blueprint => blueprint = ((blueprint == null) ? Repository<EquipmentRepository, Equipment>.Instance.GetByID(id) : blueprint);

	public StatsInstance Stats
	{
		get
		{
			if (stats != null && stats.Owner == null)
			{
				stats.SetOwner(this);
			}
			return stats;
		}
	}

	public CombatProjectile ProjectilePrefab
	{
		get
		{
			if (cachedProjectilePrefab == null)
			{
				cachedProjectilePrefab = MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByAddress(ProjectilePrefabAddress)?.GetComponent<CombatProjectile>();
				if (cachedProjectilePrefab == null)
				{
					bool isEnabled;
					FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(104, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Items\\EquipmentInstance.cs");
					if (isEnabled)
					{
						messageBuilder.AppendLiteral("Failed to find projectile prefab '");
						messageBuilder.AppendFormatted(ProjectilePrefabAddress);
						messageBuilder.AppendLiteral("' or it has no ");
						messageBuilder.AppendFormatted("CombatProjectile");
						messageBuilder.AppendLiteral(" component, falling back to 'SimpleArrow' for weapon '");
						messageBuilder.AppendFormatted(Id);
						messageBuilder.AppendLiteral("'");
					}
					Log.Error(messageBuilder);
					cachedProjectilePrefab = MonoRepository<PrefabRepository, KeyGameObjectPair>.Instance.GetByAddress("SimpleArrow").GetComponent<CombatProjectile>();
				}
			}
			return cachedProjectilePrefab;
		}
	}

	public bool IsSecondaryWeaponModeActive => isSecondaryWeaponModeActive;

	public WeaponMode ActiveWeaponMode
	{
		get
		{
			if (!isSecondaryWeaponModeActive)
			{
				return Blueprint.PrimaryWeaponMode;
			}
			return Blueprint.SecondaryWeaponMode;
		}
	}

	public WeaponMode OtherWeaponMode
	{
		get
		{
			if (!isSecondaryWeaponModeActive)
			{
				return Blueprint.SecondaryWeaponMode;
			}
			return Blueprint.PrimaryWeaponMode;
		}
	}

	public int ProducerUniqueId => producerUniqueId;

	public float Damage => ActiveWeaponMode?.Damage ?? 0f;

	public float Range => ActiveWeaponMode?.Range ?? 0f;

	public WeaponType WeaponType => ActiveWeaponMode?.WeaponType ?? WeaponType.None;

	public float IgnoresArmor => ActiveWeaponMode?.IgnoresArmor ?? 0f;

	public float ArmorDamage => ActiveWeaponMode?.ArmorDamage ?? 0f;

	public float BuildingDamage => ActiveWeaponMode?.BuildingDamage ?? 0f;

	public float Precision => ActiveWeaponMode?.Precision ?? 0f;

	public float PrecisionFalloff => ActiveWeaponMode?.PrecisionFalloff ?? 0f;

	public float AttackSpeed => ActiveWeaponMode?.AttackSpeed ?? 0f;

	public float LadderDamageMod => ActiveWeaponMode?.LadderDamageMod ?? 0f;

	public float LadderCritMod => ActiveWeaponMode?.LadderCritMod ?? 0f;

	public float LadderSpeedMod => ActiveWeaponMode?.LadderSpeedMod ?? 0f;

	public float WaterDamageMod => ActiveWeaponMode?.WaterDamageMod ?? 0f;

	public float WaterCritMod => ActiveWeaponMode?.WaterCritMod ?? 0f;

	public float WaterSpeedMod => ActiveWeaponMode?.WaterSpeedMod ?? 0f;

	public float WaterPrecisionPenalty => ActiveWeaponMode?.WaterPrecisionPenalty ?? 0f;

	public Vector3 ProjectileOffset => ActiveWeaponMode?.ProjectileOffset ?? default(Vector3);

	public string[] HitEffectorGroupIDs => ActiveWeaponMode?.HitEffectorGroupIDs;

	public string[] CriticalHitEffectorGroupIDs => ActiveWeaponMode?.CriticalHitEffectorGroupIDs;

	public bool CanFireFlammableProjectiles => ActiveWeaponMode?.CanFireFlammableProjectiles ?? false;

	public HitEffector[] OnHitEffectors => ActiveWeaponMode?.OnHitEffectors;

	public HitEffector[] OnCriticalHitEffectors => ActiveWeaponMode?.OnCriticalHitEffectors;

	public WeaponTypeSettings WeaponTypeSettings => ActiveWeaponMode?.WeaponTypeSettings ?? Repository<WeaponTypeSettingsRepository, WeaponTypeSettings>.Instance.GetByID(WeaponType.None);

	public string ProjectileHitCreatureParticles => ActiveWeaponMode?.ProjectileHitCreatureParticles;

	public string ProjectileHitBuildingParticles => ActiveWeaponMode?.ProjectileHitBuildingParticles;

	public string ProjectileMissParticles => ActiveWeaponMode?.ProjectileMissParticles;

	public string ProjectileBreakParticles => ActiveWeaponMode?.ProjectileBreakParticles;

	public AttackType AttackType => WeaponTypeSettings.AttackType;

	private string ProjectilePrefabAddress => ActiveWeaponMode?.ProjectilePrefabAddress;

	public event Action<IGameDisposable> OnDisposedEvent;

	public EquipmentInstance(string id, bool isManuallyEquiped)
	{
		this.id = id;
		this.isManuallyEquiped = isManuallyEquiped;
		stats = ResourceStatsProducer.ProduceEquipmentStats(this, Blueprint);
		stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Health, OnDurabilityExpired);
	}

	public void CheckInitStats()
	{
		if (stats == null)
		{
			stats = ResourceStatsProducer.ProduceEquipmentStats(this, Blueprint);
		}
	}

	public void SetSecondaryWeaponModeActive(bool value)
	{
		isSecondaryWeaponModeActive = value;
		cachedProjectilePrefab = null;
	}

	public void ToggleWeaponMode()
	{
		SetSecondaryWeaponModeActive(!isSecondaryWeaponModeActive);
		bool isEnabled;
		FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(22, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Items\\EquipmentInstance.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("WeaponMode changed to ");
			messageBuilder.AppendFormatted(ActiveWeaponMode);
		}
		Log.Debug(messageBuilder);
	}

	public void SetIsManuallyEquipped(bool value)
	{
		isManuallyEquiped = value;
	}

	public StatInstance GetStat(StatType type)
	{
		return Stats.GetStat(type);
	}

	public NSMedieval.StatsSystem.Attribute GetAttributeOverride(AttributeType type)
	{
		return null;
	}

	public void CloneStatsCurrent(StatsInstance stats)
	{
		foreach (KeyValuePair<StatType, StatInstance> stat2 in stats.Stats)
		{
			StatInstance stat = GetStat(stat2.Key);
			if (stat != null)
			{
				stat.SetBlueprint(stat2.Value.Blueprint);
				stat.SetCurrent(stat2.Value.Current);
			}
		}
	}

	public void SetProducerUniqueId(int producerUniqueId)
	{
		if (ProducerUniqueId == 0)
		{
			this.producerUniqueId = producerUniqueId;
		}
	}

	public float GetStatValue(StatType type)
	{
		if (HasDisposed)
		{
			return 0f;
		}
		return Stats.GetStat(type)?.Current ?? 0f;
	}

	public void Dispose()
	{
		if (!HasDisposed)
		{
			stats?.Dispose();
			if (MonoSingleton<InventoryController>.IsInstantiated())
			{
				MonoSingleton<InventoryController>.Instance.DestroyEquipment(this);
			}
			HasDisposed = true;
			if (!LoadingController.IsLeavingMainScene)
			{
				this.OnDisposedEvent?.Invoke(this);
			}
			this.OnDisposedEvent = null;
			stats = null;
			cachedProjectilePrefab = null;
		}
	}

	public void StartEquipEffects(StatsInstance stats)
	{
		if (Blueprint.Effectors != null)
		{
			string[] effectors = Blueprint.Effectors;
			foreach (string effectorId in effectors)
			{
				stats.StartEffector(effectorId);
			}
		}
	}

	public void EndEquipEffects(StatsInstance stats)
	{
		if (Blueprint.Effectors != null)
		{
			string[] effectors = Blueprint.Effectors;
			foreach (string name in effectors)
			{
				stats.EndEffector(name);
			}
		}
	}

	public void Reinstantiate()
	{
		stats.SetOwner(this);
		ResourceStatsProducer.ProduceEquipmentStats(this, Blueprint, stats);
		stats.SetOwnerOnStats();
		stats.Controller.RegisterListener(StatEventType.MinimumValueReached, StatType.Health, OnDurabilityExpired);
		if (Blueprint == null)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(32, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Items\\EquipmentInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Equipment blueprint not found: ");
				messageBuilder.AppendFormatted(Id);
				messageBuilder.AppendLiteral(".");
			}
			Log.Warning(messageBuilder);
		}
	}

	private void OnDurabilityExpired(object data)
	{
		Dispose();
	}

	public float GetWealth()
	{
		if (HasDisposed)
		{
			return 0f;
		}
		return Blueprint.Resource.WealthPoints * GetStat(StatType.Health).GetNormalizedPercentage();
	}

	public float GetTotalDurability()
	{
		if (HasDisposed)
		{
			return 0f;
		}
		StatInstance stat = GetStat(StatType.Health);
		return Mathf.Clamp(Blueprint.ArmorRating * (stat.Current / stat.Max), 0f, 1f);
	}

	public float GetBeautyInput()
	{
		if (Blueprint == null)
		{
			return 0f;
		}
		return Blueprint.GetBeautyInputEquipped();
	}

	public void SetNextRoundFlammable(bool value)
	{
		if (!CanFireFlammableProjectiles)
		{
			Log.Warning("Tried to set next round flammable, but this weapon's blueprint doesn't support it", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Items\\EquipmentInstance.cs");
		}
		else
		{
			isNextProjectileFlammable = value;
		}
	}

	public bool ConsumeFlammableProjectile()
	{
		bool result = isNextProjectileFlammable;
		isNextProjectileFlammable = false;
		return result;
	}

	public float ApplyProjectileArchHeightModifier(float bezierPathApex)
	{
		if (ActiveWeaponMode.ProjectileArcHeight == 0f)
		{
			return bezierPathApex;
		}
		return bezierPathApex * ActiveWeaponMode.ProjectileArcHeight;
	}

	public float ApplyProjectileSpeedModifier(float speed)
	{
		if (ActiveWeaponMode.ProjectileSpeed == 0f)
		{
			return speed;
		}
		return speed * ActiveWeaponMode.ProjectileSpeed;
	}

	public void DealWeaponDurabilityDamage(bool isMiss, bool isFlammableProjectile = false)
	{
		if (!HasDisposed && ActiveWeaponMode != null && (!isMiss || ActiveWeaponMode.LoseHpOnMiss))
		{
			float num = ActiveWeaponMode.HpLossPerUse;
			if (isFlammableProjectile && ActiveWeaponMode.HpLossFlammableProjectileModifier > 0f)
			{
				num *= ActiveWeaponMode.HpLossFlammableProjectileModifier;
			}
			StatInstance stat = GetStat(StatType.Health);
			stat.SetCurrent(stat.Current - num);
			bool isEnabled;
			FVLogDebugInterpolationHandler messageBuilder = new FVLogDebugInterpolationHandler(52, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Items\\EquipmentInstance.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Dealt ");
				messageBuilder.AppendFormatted(num);
				messageBuilder.AppendLiteral(" durability damage to weapon '");
				messageBuilder.AppendFormatted(Id);
				messageBuilder.AppendLiteral("' (current hp: ");
				messageBuilder.AppendFormatted(stat.Current);
				messageBuilder.AppendLiteral(")");
			}
			Log.Debug(messageBuilder);
		}
	}

	public void Serialize(FVSerializer serializer)
	{
		serializer.Write("id", id);
		serializer.Write("isManuallyEquiped", isManuallyEquiped);
		serializer.Write("producerUniqueId", producerUniqueId);
		serializer.Write("stats", stats);
	}

	public EquipmentInstance(FVDeserializer deserializer)
	{
		id = deserializer.ReadString("id");
		isManuallyEquiped = deserializer.ReadBool("isManuallyEquiped");
		producerUniqueId = deserializer.ReadInt("producerUniqueId");
		stats = deserializer.ReadObject<StatsInstance>("stats");
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
