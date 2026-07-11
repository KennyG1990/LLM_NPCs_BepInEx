using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.Controllers;
using NSMedieval.Manager;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Serialization;
using NSMedieval.StatsSystem;
using NSMedieval.Tools;
using NSMedieval.Types;
using NSMedieval.UI.Utils;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Views.Resources;
using UnityEngine;

namespace NSMedieval.State;

[Serializable]
[FVSerializableKey("InventoryInstance", "")]
public class InventoryInstance : IGameDisposable, IDisposable, IFVSerializable
{
	public delegate void OnItemDropped(EquipmentInstance instance);

	[SerializeField]
	private EquipmentSlotType equipmentSlots;

	[SerializeField]
	private List<EquipmentInstance> equipments;

	[NonSerialized]
	private List<ResourcePileInstance> equipOrders;

	[NonSerialized]
	private List<EquipmentSlotType> availableSlots;

	[NonSerialized]
	private Func<Vector3> getPosition;

	private EquipmentSlotType occupiedSlots;

	private bool isInitialized;

	public bool HasDisposed { get; protected set; }

	public List<ResourcePileInstance> EquipOrders => equipOrders;

	public EquipmentSlotType EquipmentSlots => equipmentSlots;

	public EquipmentSlotType OccupiedSlots => occupiedSlots;

	public Vector3 Position => getPosition();

	public List<EquipmentInstance> Equipments => equipments;

	public List<EquipmentSlotType> AvailableSlots
	{
		get
		{
			if (availableSlots == null)
			{
				availableSlots = new List<EquipmentSlotType>();
				EquipmentSlotType[] equipmentSlotTypes = EnumValues.EquipmentSlotTypes;
				foreach (EquipmentSlotType equipmentSlotType in equipmentSlotTypes)
				{
					if (equipmentSlots.HasFlag(equipmentSlotType))
					{
						availableSlots.Add(equipmentSlotType);
					}
				}
			}
			return availableSlots;
		}
	}

	public event Action<IGameDisposable> OnDisposedEvent;

	public event OnItemDropped OnEquipedEvent;

	public event OnItemDropped OnDroppedEvent;

	public event OnItemDropped OnDestroyEvent;

	public InventoryInstance(EquipmentSlotType equipmentSlots, Func<Vector3> getPosition)
	{
		this.equipmentSlots = equipmentSlots;
		equipments = new List<EquipmentInstance>();
		this.getPosition = getPosition;
	}

	public void Init()
	{
		if (!isInitialized)
		{
			isInitialized = true;
			equipOrders = new List<ResourcePileInstance>();
			MonoSingleton<WorldTimeManager>.Instance.TimeUpdateEvent += OnMinutePassed;
			MonoSingleton<InventoryController>.Instance.OnEquipmentDestroyEvent += OnEquipmentDestroyed;
		}
	}

	public List<EquipmentInstance> GetEquipments()
	{
		return equipments;
	}

	public void AddEquipOrder(ResourcePileInstance item)
	{
		if (!equipOrders.Contains(item))
		{
			equipOrders.Add(item);
		}
	}

	public void DestroyEquipmentSilent()
	{
		using PooledList<EquipmentInstance> pooledList = ListPool<EquipmentInstance>.GetJanitor(equipments);
		foreach (EquipmentInstance item in pooledList)
		{
			OnEquipmentDestroyed(item, isSilent: true);
		}
		GetEquipments().Clear();
	}

	public void ClearEquipOrders()
	{
		equipOrders.Clear();
	}

	public void RemoveEquipOrder(ResourcePileInstance item)
	{
		equipOrders.Remove(item);
	}

	public EquipmentInstance GetItem(EquipmentSlotType slot)
	{
		return equipments?.FirstOrDefault((EquipmentInstance item) => item.Blueprint.EquipmentSlots.HasFlag(slot));
	}

	public EquipmentInstance GetItem(ItemType type)
	{
		return equipments?.FirstOrDefault((EquipmentInstance item) => item.Blueprint.ItemType == type);
	}

	public bool IsSlotBlocked(EquipmentSlotType slot)
	{
		if (equipments == null)
		{
			return false;
		}
		EquipmentInstance equipmentInstance = null;
		foreach (EquipmentInstance equipment in equipments)
		{
			if (equipment.Blueprint.EquipmentSlots.HasFlag(slot))
			{
				equipmentInstance = equipment;
				break;
			}
		}
		if (equipmentInstance == null)
		{
			return false;
		}
		EquipmentSlotType equipmentSlotType = EquipmentSlotType.None;
		foreach (EquipmentSlotType availableSlot in AvailableSlots)
		{
			if (availableSlot != 0 && equipmentInstance.Blueprint.EquipmentSlots.HasFlag(availableSlot))
			{
				equipmentSlotType = availableSlot;
				break;
			}
		}
		if ((equipmentInstance.Blueprint.EquipmentSlots & ~equipmentSlotType) == slot)
		{
			return true;
		}
		return false;
	}

	public void Equip(EquipmentInstance item, bool removeInsteadOfDrop = false)
	{
		EquipmentSlotType equipmentSlotType = item.Blueprint.EquipmentSlots;
		if (!equipmentSlots.HasFlag(equipmentSlotType))
		{
			return;
		}
		for (int i = 1; i < AvailableSlots.Count; i++)
		{
			if (!item.Blueprint.EquipmentSlots.HasFlag(AvailableSlots[i]))
			{
				continue;
			}
			foreach (EquipmentInstance item2 in equipments.IterateInReverseDynamic())
			{
				if (item2.Blueprint.EquipmentSlots.HasFlag(availableSlots[i]))
				{
					if (removeInsteadOfDrop)
					{
						item2.Dispose();
						continue;
					}
					MonoSingleton<WorkerController>.Instance.DropItem(item2, this);
					MonoSingleton<NPCController>.Instance.DropItem(item2, this);
					DropItem(item2);
				}
			}
		}
		occupiedSlots |= item.Blueprint.EquipmentSlots;
		equipments.Add(item);
		MonoSingleton<WorkerController>.Instance.EquipItem(item, this);
		MonoSingleton<NPCController>.Instance.EquipItem(item, this);
		this.OnEquipedEvent?.Invoke(item);
	}

	public bool DropItem(EquipmentInstance item, bool forbidDroppedItem = false, ILifeLogOwner resourceLogOwner = null, bool shouldSpawnDroppedItem = true)
	{
		if (!equipments.Remove(item) || item.HasDisposed)
		{
			return false;
		}
		occupiedSlots &= ~item.Blueprint.EquipmentSlots;
		if (shouldSpawnDroppedItem)
		{
			ResourceInstance resourceInstance = new ResourceInstance(Repository<ResourceRepository, Resource>.Instance.GetByID(item.Id), 1, resourceLogOwner);
			resourceInstance.CloneStatsCurrent(item.Stats);
			resourceInstance.SetProducerUniqueId(item.ProducerUniqueId);
			ResourcePileView resourcePileView = MonoSingleton<ResourcePileManager>.Instance.SpawnPile(resourceInstance, Position);
			if (resourcePileView != null)
			{
				resourcePileView.ResourcePileInstance.IsForbidden = forbidDroppedItem;
			}
			this.OnDroppedEvent?.Invoke(item);
		}
		item.Dispose();
		return true;
	}

	public void ClearInventoryOnDeath()
	{
		for (int num = equipments.Count - 1; num >= 0; num--)
		{
			equipments[num].Dispose();
		}
		equipments.Clear();
	}

	public void DropItemFromEquipmentSlot(EquipmentSlotType slotType, bool forbidDroppedItem = false)
	{
		while (GetItem(slotType) != null)
		{
			EquipmentInstance item = GetItem(slotType);
			DropItem(item, forbidDroppedItem);
			MonoSingleton<NPCController>.Instance.DropItem(item, this);
		}
	}

	public void OnMinutePassed()
	{
	}

	public void Reinstance(Func<Vector3> getPosition)
	{
		if (equipOrders == null)
		{
			equipOrders = new List<ResourcePileInstance>();
		}
		this.getPosition = getPosition;
		MonoSingleton<WorldTimeManager>.Instance.TimeUpdateEvent -= OnMinutePassed;
		MonoSingleton<WorldTimeManager>.Instance.TimeUpdateEvent += OnMinutePassed;
		foreach (EquipmentInstance item in new List<EquipmentInstance>(equipments))
		{
			if (item != null && !item.HasDisposed)
			{
				item.CheckInitStats();
				StatInstance stat = item.GetStat(StatType.Health);
				if (stat != null && stat.Current < 0.5f)
				{
					OnEquipmentDestroyed(item, isSilent: true);
					item.Dispose();
				}
				else
				{
					item.Reinstantiate();
				}
			}
		}
	}

	public bool HasCombatCoverItemEquipped(DamageType damageType)
	{
		return equipments.Find((EquipmentInstance item) => item.Blueprint.CanBlockAttacks(damageType) && (item.Blueprint.EquipmentSlots & (EquipmentSlotType.RightHand | EquipmentSlotType.LeftHand)) != 0) != null;
	}

	public void Dispose()
	{
		if (MonoSingleton<InventoryController>.IsInstantiated())
		{
			MonoSingleton<InventoryController>.Instance.OnEquipmentDestroyEvent -= OnEquipmentDestroyed;
		}
		if (MonoSingleton<WorldTimeManager>.IsInstantiated())
		{
			MonoSingleton<WorldTimeManager>.Instance.TimeUpdateEvent -= OnMinutePassed;
		}
		this.OnDroppedEvent = null;
		this.OnEquipedEvent = null;
		this.OnDestroyEvent = null;
		HasDisposed = true;
		if (equipments != null)
		{
			foreach (EquipmentInstance equipment in equipments)
			{
				equipment.Dispose();
			}
			equipments.Clear();
		}
		if (!LoadingController.IsLeavingMainScene)
		{
			this.OnDisposedEvent?.Invoke(this);
		}
		this.OnDisposedEvent = null;
		getPosition = null;
		equipOrders?.Clear();
	}

	public void DisposeEvents()
	{
		this.OnDroppedEvent = null;
		this.OnEquipedEvent = null;
		this.OnDestroyEvent = null;
	}

	public void DropAllItems(bool forbidDroppedItems, ILifeLogOwner resourceLogOwner = null, bool shouldSpawnDroppedItem = true)
	{
		if (equipments == null)
		{
			return;
		}
		foreach (EquipmentInstance item in new List<EquipmentInstance>(equipments))
		{
			DropItem(item, forbidDroppedItems, resourceLogOwner, shouldSpawnDroppedItem);
		}
	}

	private void OnEquipmentDestroyed(EquipmentInstance instance)
	{
		OnEquipmentDestroyed(instance, isSilent: false);
	}

	private void OnEquipmentDestroyed(EquipmentInstance instance, bool isSilent)
	{
		if (!equipments.Contains(instance))
		{
			return;
		}
		equipments.Remove(instance);
		occupiedSlots &= ~instance.Blueprint.EquipmentSlots;
		this.OnDestroyEvent?.Invoke(instance);
		instance.Dispose();
		if (isSilent)
		{
			return;
		}
		HumanoidInstance humanoidInstance = GlobalSaveController.CurrentVillageData?.Workers?.FirstOrDefault((HumanoidInstance w) => w.Inventory.Equals(this));
		if (humanoidInstance != null)
		{
			string text = MonoSingleton<LocalizationController>.Instance.GetText("equipment_decomposed");
			text = TextFormatting.FormatEquippedDestroyed(text, humanoidInstance.Info.GetFullName(), EquipmentUtils.GetTooltipTitle(instance.Blueprint));
			MonoSingleton<BlackBarMessageController>.Instance.ShowClickableBlackBarMessage(text, MonoSingleton<WorkerManager>.Instance.GetView(humanoidInstance), follow: true);
		}
		switch (instance.Blueprint.ItemType)
		{
		case ItemType.Armor:
			if (instance.Blueprint.GetID().Contains("_shield"))
			{
				DamagePopup.Create(Position, AssetUtils.GetSpriteAsset("shield_destroy") + "   ");
			}
			else if (instance.Blueprint.GetID().Contains("_helmet"))
			{
				DamagePopup.Create(Position, AssetUtils.GetSpriteAsset("helmet_destroy") + "   ");
			}
			else if (instance.Blueprint.GetID().Contains("_hat"))
			{
				DamagePopup.Create(Position, AssetUtils.GetSpriteAsset("hat_destroy") + "   ");
			}
			else
			{
				DamagePopup.Create(Position, AssetUtils.GetSpriteAsset("armor_destroy") + "   ");
			}
			break;
		case ItemType.Garment:
			DamagePopup.Create(Position, AssetUtils.GetSpriteAsset("clothing_destroy") + "   ");
			break;
		}
	}

	[OnDeserialized]
	private void OnDeserialized()
	{
		if (equipments == null)
		{
			return;
		}
		equipments.RemoveAll((EquipmentInstance item) => item == null || item.Blueprint == null);
		foreach (EquipmentInstance equipment in equipments)
		{
			occupiedSlots |= equipment.Blueprint.EquipmentSlots;
		}
	}

	public void Serialize(FVSerializer serializer)
	{
		serializer.WriteEnum("equipmentSlots", equipmentSlots);
		serializer.Write("equipments", equipments);
	}

	public InventoryInstance(FVDeserializer deserializer)
	{
		equipmentSlots = deserializer.ReadEnum("equipmentSlots", EquipmentSlotType.None);
		equipments = deserializer.ReadObjectList<EquipmentInstance>("equipments");
		OnDeserialized();
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
