using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.Manager;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.Repository;
using NSMedieval.Resources;
using NSMedieval.State;
using NSMedieval.Stockpiles;
using NSMedieval.Types;
using UnityEngine;

namespace NSMedieval.Research;

public class ResearchManager : MonoSingleton<ResearchManager>
{
	private readonly Dictionary<ResearchNodeInstance, ResearchNodeView> dictionary = new Dictionary<ResearchNodeInstance, ResearchNodeView>();

	private readonly Dictionary<Resource, int> researchAllocatedResources = new Dictionary<Resource, int>();

	private readonly Dictionary<string, ResearchNodeInstance> instancesById = new Dictionary<string, ResearchNodeInstance>();

	private readonly HashSet<string> researchedTech = new HashSet<string>();

	private readonly List<string> researchOrder = new List<string>();

	public Dictionary<Resource, int> ResearchAllocatedResources
	{
		get
		{
			if (researchAllocatedResources.Count == 0)
			{
				foreach (ResearchModel allItem in Repository<ResearchRepository, ResearchModel>.Instance.GetAllItems())
				{
					foreach (KeyValuePair<string, int> item in allItem.RequiredResources.Dictionary)
					{
						Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(item.Key);
						if (!researchAllocatedResources.ContainsKey(byID))
						{
							researchAllocatedResources.Add(byID, 0);
						}
					}
				}
			}
			return researchAllocatedResources;
		}
	}

	public RectTransform GetArchitectureRect()
	{
		return dictionary.Values.FirstOrDefault()?.transform as RectTransform;
	}

	public ResearchNodeInstance GetInstanceById(string id)
	{
		return instancesById.GetValueOrDefault(id);
	}

	private void Start()
	{
		MonoSingleton<ResearchController>.Instance.UnlockResearchEvent += OnResearchUnlocked;
		MonoSingleton<ResearchController>.Instance.LockResearchEvent += OnResearchLocked;
		MonoSingleton<ResearchController>.Instance.ActivateResearchEvent += OnResearchActivated;
		MonoSingleton<ResearchController>.Instance.DeactivateResearchEvent += OnResearchDeactivated;
		MonoSingleton<ResearchController>.Instance.ResetAllResearchEvent += OnResetAllResearchEvent;
		MonoSingleton<ResearchController>.Instance.ActivateAllResearchEvent += OnActivateAllResearch;
		MonoSingleton<ResourcePileController>.Instance.ResourceCountChangeEvent += ResourceCountChanged;
		LoadDefaultUnlocked();
		MonoSingleton<World>.Instance.MapLoadedEvent += OnMapLoaded;
	}

	protected override void OnDestroy()
	{
		if (MonoSingleton<ResearchController>.IsInstantiated())
		{
			MonoSingleton<ResearchController>.Instance.UnlockResearchEvent -= OnResearchUnlocked;
			MonoSingleton<ResearchController>.Instance.LockResearchEvent -= OnResearchLocked;
			MonoSingleton<ResearchController>.Instance.ActivateResearchEvent -= OnResearchActivated;
			MonoSingleton<ResearchController>.Instance.DeactivateResearchEvent -= OnResearchDeactivated;
			MonoSingleton<ResearchController>.Instance.ResetAllResearchEvent -= OnResetAllResearchEvent;
			MonoSingleton<ResearchController>.Instance.ActivateAllResearchEvent -= OnActivateAllResearch;
		}
		if (MonoSingleton<ResourcePileController>.IsInstantiated())
		{
			MonoSingleton<ResourcePileController>.Instance.ResourceCountChangeEvent -= ResourceCountChanged;
		}
		if (MonoSingleton<World>.IsInstantiated())
		{
			MonoSingleton<World>.Instance.MapLoadedEvent -= OnMapLoaded;
		}
		base.OnDestroy();
	}

	public HashSet<ResearchNodeInstance> GetUnlockableResearchNodes()
	{
		HashSet<ResearchNodeInstance> hashSet = new HashSet<ResearchNodeInstance>();
		foreach (ResearchNodeInstance key in dictionary.Keys)
		{
			if (key != null && key.ResearchState == ResearchState.Unlocked && HasEnoughResources(key))
			{
				hashSet.Add(key);
			}
		}
		return hashSet;
	}

	public int GetUnlockableNodesCount()
	{
		return GetUnlockableResearchNodes().Count;
	}

	public int GetAllocatedResources(string id)
	{
		if (!ResearchAllocatedResources.ContainsKey(ResourceById(id)))
		{
			return 0;
		}
		return ResearchAllocatedResources[ResourceById(id)];
	}

	public bool ContainsResource(string id)
	{
		return ResearchAllocatedResources.ContainsKey(ResourceById(id));
	}

	public ResearchNodeView GetView(ResearchNodeInstance nodeInstance)
	{
		if (nodeInstance != null)
		{
			return dictionary.GetValueOrDefault(nodeInstance);
		}
		return null;
	}

	public bool Researched(string id)
	{
		return researchedTech.Contains(id);
	}

	public bool UnlockedByDefault(string id)
	{
		foreach (ResearchModel allItem in Repository<ResearchRepository, ResearchModel>.Instance.GetAllItems())
		{
			if (allItem.Unlocks.Any((ResearchUnlock ru) => ru.UnlockId.Equals(id)))
			{
				return false;
			}
		}
		return true;
	}

	public int GetAvailableResources(string resourceModelId)
	{
		Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(resourceModelId);
		if (byID == null)
		{
			Log.Error("Failed to get available resources for " + resourceModelId, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Research\\ResearchManager.cs");
			return 0;
		}
		return GetAvailableResources(byID);
	}

	public int GetAvailableResources(Resource resourceModel)
	{
		int num = MonoSingleton<ResourcePileManager>.Instance.GetTotalAmount(resourceModel) + MonoSingleton<WorkerManager>.Instance.GetResourceCountFromWorkerStorage(resourceModel);
		int num2 = (researchAllocatedResources.ContainsKey(resourceModel) ? researchAllocatedResources[resourceModel] : 0);
		return Mathf.Abs(num - num2);
	}

	public void AddToDictionary(ResearchNodeInstance instance, ResearchNodeView view)
	{
		dictionary.TryAdd(instance, view);
		instancesById.TryAdd(instance.Blueprint.GetID(), instance);
	}

	public void RedrawLines()
	{
		foreach (ResearchNodeInstance key in dictionary.Keys)
		{
			dictionary[key].RedrawLines();
		}
	}

	public bool HasEnough(KeyValuePair<string, int> pair)
	{
		return pair.Value <= GetAvailableResources(pair.Key);
	}

	public bool HasEnoughResources(ResearchNodeInstance instance)
	{
		foreach (KeyValuePair<string, int> item in instance.Blueprint.RequiredResources.Dictionary)
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(item.Key);
			if (byID == null)
			{
				return false;
			}
			if (researchAllocatedResources.ContainsKey(byID))
			{
				int num = MonoSingleton<ResourcePileManager>.Instance.GetTotalAmount(byID) + MonoSingleton<WorkerManager>.Instance.GetResourceCountFromWorkerStorage(byID);
				if (researchAllocatedResources[byID] > num)
				{
					return false;
				}
			}
			if (GetAvailableResources(byID) < item.Value)
			{
				return false;
			}
		}
		return true;
	}

	public void LoadSavedResearch()
	{
		ResearchNodeInstance[] array = GlobalSaveController.CurrentVillageData.GetUnlockedNodes().ToArray();
		foreach (ResearchNodeInstance researchNodeInstance in array)
		{
			foreach (ResearchNodeInstance key in dictionary.Keys)
			{
				if (researchNodeInstance == null || researchNodeInstance.Blueprint == null)
				{
					GlobalSaveController.CurrentVillageData.RemoveResearchedNode(researchNodeInstance);
				}
				else if (researchNodeInstance.Blueprint.Equals(key.Blueprint))
				{
					GlobalSaveController.CurrentVillageData.GetUnlockedNodes().Remove(researchNodeInstance);
					if (GlobalSaveController.CurrentVillageData.Scenario.TechnologyUnlocked.Contains(key.Blueprint.GetID()))
					{
						key.SetActiveByDefault();
					}
					MonoSingleton<ResearchController>.Instance.Activate(key, afterLoading: true);
				}
			}
		}
	}

	private ResearchNodeInstance GetNode(string id)
	{
		foreach (ResearchNodeInstance key in dictionary.Keys)
		{
			if (key.Blueprint.GetID().Equals(id))
			{
				return key;
			}
		}
		return null;
	}

	private void ResourcePileDestroyed(ResourcePileInstance destroyedResource)
	{
		CheckForNodeDeactivation(researchOrder.Count - 1);
	}

	private void SetParentsAndChildren(ResearchNodeInstance node)
	{
		foreach (ResearchNodeInstance key in dictionary.Keys)
		{
			if (key.Equals(node))
			{
				continue;
			}
			foreach (string nextNodesID in key.Blueprint.NextNodesIDs)
			{
				if (nextNodesID.Equals(node.Blueprint.GetID()) && !node.Parents.Contains(key))
				{
					node.Parents.Add(key);
				}
			}
			foreach (string nextNodesID2 in node.Blueprint.NextNodesIDs)
			{
				if (nextNodesID2.Equals(key.Blueprint.GetID()) && !node.Children.Contains(key))
				{
					node.Children.Add(key);
				}
			}
		}
	}

	private void OnMapLoaded(bool loadedFromSave)
	{
		MonoSingleton<TaskController>.Instance.WaitFor(1f).Then(LockUnlockNodes);
	}

	private void OnResearchUnlocked(ResearchNodeInstance node)
	{
		if (dictionary.ContainsKey(node))
		{
			node.Unlock();
			dictionary[node].Unlock();
			if (node.ActiveByDefault)
			{
				OnResearchActivated(node, afterLoading: false, forceUnlock: true);
			}
		}
	}

	private void OnResearchLocked(ResearchNodeInstance node)
	{
		if (!dictionary.ContainsKey(node) || node.ResearchState == ResearchState.Locked)
		{
			return;
		}
		node.Lock();
		dictionary[node].Lock();
		GlobalSaveController.CurrentVillageData.RemoveResearchedNode(node);
		foreach (ResearchNodeInstance child in node.Children)
		{
			MonoSingleton<ResearchController>.Instance.Lock(child);
		}
	}

	private void OnResearchActivated(ResearchNodeInstance node, bool afterLoading = false, bool forceUnlock = false)
	{
		if ((!forceUnlock && !afterLoading && !HasEnoughResources(node) && !MonoSingleton<ResearchController>.Instance.DebugAllUnlocked) || !dictionary.ContainsKey(node) || node.ResearchState.Equals(ResearchState.Activated))
		{
			return;
		}
		if (!node.ActiveByDefault)
		{
			foreach (Resource key in node.RequiredResources.Keys)
			{
				if (researchAllocatedResources.ContainsKey(key))
				{
					researchAllocatedResources[key] += node.RequiredResources[key];
				}
				else
				{
					researchAllocatedResources.Add(key, node.RequiredResources[key]);
				}
				MonoSingleton<ResearchUIController>.Instance.UpdateResources(researchAllocatedResources);
			}
		}
		node.Activate();
		dictionary[node].Activate();
		MonoSingleton<AchievementManager>.Instance.SetStat("RSRC_CNT", dictionary.Keys.Count((ResearchNodeInstance item) => item.IsActivated));
		GlobalSaveController.CurrentVillageData.AddResearchedNode(node);
		if (!researchOrder.Contains(node.Blueprint.GetID()))
		{
			researchOrder.Add(node.Blueprint.GetID());
		}
		foreach (ResearchUnlock unlock in node.Blueprint.Unlocks)
		{
			AddToUnlocked(unlock.UnlockId);
			MonoSingleton<ResearchUIController>.Instance.ShowBaseConstructionButton(GetUICategory(unlock.UnlockId));
		}
		if (!MonoSingleton<ResearchController>.Instance.DebugAllUnlocked)
		{
			LockUnlockNodes();
		}
	}

	private bool AllParentsActive(ResearchNodeInstance node)
	{
		foreach (ResearchNodeInstance parent in node.Parents)
		{
			if (!parent.ResearchState.Equals(ResearchState.Activated))
			{
				return false;
			}
		}
		return true;
	}

	private void OnResearchDeactivated(ResearchNodeInstance node)
	{
		if (!dictionary.ContainsKey(node))
		{
			return;
		}
		if (AllParentsActive(node) && HasEnoughResources(node))
		{
			node.Unlock();
			dictionary[node].Unlock();
		}
		else
		{
			node.Lock();
			dictionary[node].Lock();
		}
		ReleaseNodeAllocatedResources(node);
		if (researchOrder.Contains(node.Blueprint.GetID()))
		{
			researchOrder.Remove(node.Blueprint.GetID());
		}
		GlobalSaveController.CurrentVillageData.RemoveResearchedNode(node);
		foreach (ResearchNodeInstance child in node.Children)
		{
			MonoSingleton<ResearchController>.Instance.Lock(child);
		}
	}

	private void ReleaseNodeAllocatedResources(ResearchNodeInstance instance)
	{
		foreach (KeyValuePair<string, int> item in instance.Blueprint.RequiredResources.Dictionary)
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(item.Key);
			if (!(byID == null) && researchAllocatedResources.ContainsKey(byID))
			{
				researchAllocatedResources[byID] -= item.Value;
				if (researchAllocatedResources[byID] < 0)
				{
					researchAllocatedResources[byID] = 0;
				}
			}
		}
		MonoSingleton<ResearchUIController>.Instance.UpdateResources(researchAllocatedResources);
	}

	private void OnResetAllResearchEvent()
	{
		researchAllocatedResources.Clear();
		researchedTech.Clear();
		researchOrder.Clear();
		GlobalSaveController.CurrentVillageData.GetUnlockedItems().Clear();
		LoadDefaultUnlocked();
		MonoSingleton<ResearchUIController>.Instance.UpdateResources(researchAllocatedResources);
		foreach (ResearchNodeInstance key in dictionary.Keys)
		{
			if (key.Root)
			{
				OnResearchDeactivated(key);
			}
		}
	}

	public void InitializeNodes()
	{
		foreach (ResearchNodeInstance key in dictionary.Keys)
		{
			SetParentsAndChildren(key);
		}
		foreach (ResearchNodeInstance key2 in dictionary.Keys)
		{
			dictionary[key2].Setup();
		}
	}

	public void SetupScenarioUnlocked()
	{
		foreach (string item in GlobalSaveController.CurrentVillageData.Scenario.TechnologyUnlocked)
		{
			ResearchNodeInstance node = GetNode(item);
			if (node != null)
			{
				node.SetActiveByDefault();
				MonoSingleton<ResearchController>.Instance.ActivateScenarioResearch(GetNode(item));
			}
		}
	}

	public void SetTutorialUnlocked(string blueprintId)
	{
		ResearchNodeInstance node = GetNode(blueprintId);
		if (node == null)
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(24, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Research\\ResearchManager.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Node with id ");
				messageBuilder.AppendFormatted(blueprintId);
				messageBuilder.AppendLiteral(" not found.");
			}
			Log.Error(messageBuilder);
		}
		else
		{
			node.SetActiveByDefault();
		}
	}

	private void LockUnlockNodes()
	{
		foreach (ResearchNodeInstance key in dictionary.Keys)
		{
			if (key.ResearchState != ResearchState.Activated)
			{
				if (AllParentsActive(key) && HasEnoughResources(key))
				{
					OnResearchUnlocked(key);
				}
				else
				{
					OnResearchLocked(key);
				}
			}
		}
	}

	private void ResourceCountChanged(Resource resource, ResourcePileCount pileCount)
	{
		if ((resource.Category & ResourceCategory.CtgResearch) != ResourceCategory.CtgResearch)
		{
			return;
		}
		MonoSingleton<TaskController>.Instance.OptimizedCall(this, "update_count", delegate
		{
			if (MonoSingleton<ResearchManager>.IsInstantiated())
			{
				MonoSingleton<ResearchUIController>.Instance.UpdateResources(researchAllocatedResources);
				LockUnlockNodes();
			}
		});
	}

	private void CheckForNodeDeactivation(int index)
	{
		if (index >= 0)
		{
			ResearchNodeInstance node = GetNode(researchOrder[index]);
			if (!CanRemainActivated(node))
			{
				OnResearchDeactivated(node);
			}
			index--;
			CheckForNodeDeactivation(index);
		}
	}

	private BuildingCategoryUI GetUICategory(string id)
	{
		foreach (BaseBuildingBlueprint allItem in Repository<BaseBuildingRepository, BaseBuildingBlueprint>.Instance.GetAllItems())
		{
			if (allItem.GetID() == id)
			{
				return allItem.BuildingCategoryUI;
			}
		}
		return BuildingCategoryUI.None;
	}

	private bool CanRemainActivated(ResearchNodeInstance node)
	{
		foreach (KeyValuePair<string, int> item in node.Blueprint.RequiredResources.Dictionary)
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(item.Key);
			if (!(byID == null) && researchAllocatedResources.ContainsKey(byID) && researchAllocatedResources[byID] > MonoSingleton<ResourcePileTracker>.Instance.GetCount(byID).TotalCount)
			{
				return false;
			}
		}
		return true;
	}

	private void LoadDefaultUnlocked()
	{
		foreach (BaseBuildingBlueprint allItem in Repository<BaseBuildingRepository, BaseBuildingBlueprint>.Instance.GetAllItems())
		{
			TryAddToUnlocked(allItem.GetID());
		}
		foreach (Stockpile allItem2 in Repository<StockpileRepository, Stockpile>.Instance.GetAllItems())
		{
			AddToUnlocked(allItem2.GetID());
		}
		foreach (string item in GlobalSaveController.CurrentVillageData.Scenario.TechnologyUnlocked)
		{
			ResearchModel byID = Repository<ResearchRepository, ResearchModel>.Instance.GetByID(item);
			if (!(byID != null))
			{
				continue;
			}
			foreach (ResearchUnlock unlock in byID.Unlocks)
			{
				AddToUnlocked(unlock.UnlockId);
			}
		}
	}

	private void TryAddToUnlocked(string id)
	{
		if (UnlockedByDefault(id))
		{
			AddToUnlocked(id);
		}
	}

	private void AddToUnlocked(string id)
	{
		GlobalSaveController.CurrentVillageData.AddUnlockedItem(id);
		researchedTech.Add(id);
	}

	private Resource ResourceById(string resourceId)
	{
		return Repository<ResourceRepository, Resource>.Instance.GetByID(resourceId);
	}

	private void OnActivateAllResearch(bool activated)
	{
		if (activated)
		{
			foreach (ResearchNodeInstance key in dictionary.Keys)
			{
				if (!key.ResearchState.Equals(ResearchState.Activated))
				{
					key.SetUnlockedUsingDebug(unlockedUsingDebug: true);
					OnResearchActivated(key);
				}
			}
			return;
		}
		foreach (ResearchNodeInstance key2 in dictionary.Keys)
		{
			if (key2.UnlockedUsingDebug)
			{
				key2.SetUnlockedUsingDebug(unlockedUsingDebug: false);
				OnResearchDeactivated(key2);
			}
		}
	}

	public int GetAvailableBookCount(string id)
	{
		foreach (Resource key in researchAllocatedResources.Keys)
		{
			if (key.GetID().Equals(id))
			{
				int num = researchAllocatedResources[key];
				return Mathf.Clamp(MonoSingleton<ResourcePileTracker>.Instance.GetCount(key).TotalCount + MonoSingleton<ResourcePileTracker>.Instance.GetCount(key).UnreachableCount + MonoSingleton<WorkerManager>.Instance.GetResourceCountFromWorkerStorage(key) - num, 0, int.MaxValue);
			}
		}
		return 0;
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
