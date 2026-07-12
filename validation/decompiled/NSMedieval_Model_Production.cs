using System;
using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix;
using NSEipix.Base;
using NSEipix.Model;
using NSEipix.Repository;
using NSMedieval.Controllers;
using NSMedieval.Enums;
using NSMedieval.Goap;
using NSMedieval.Manager;
using NSMedieval.Production;
using NSMedieval.Repository;
using NSMedieval.State;
using NSMedieval.State.WorkerJobs;
using NSMedieval.StatsSystem;
using NSMedieval.Stockpiles;
using NSMedieval.Types;
using NSMedieval.UI;
using NSMedieval.UI.Utils;
using UnityEngine;

namespace NSMedieval.Model;

[Serializable]
public class Production : NSEipix.Base.Model
{
	[SerializeField]
	private string id;

	[SerializeField]
	private List<SkillLevelPair> requiredSkills;

	[SerializeField]
	private List<ProductModel> produced = new List<ProductModel>();

	[SerializeField]
	private List<CustomProduct> customProducts;

	[SerializeField]
	private List<KeyIntPair> recipe = new List<KeyIntPair>();

	[SerializeField]
	private List<KeyIntPair> secondaryRecipe = new List<KeyIntPair>();

	[SerializeField]
	private List<ProductionStep> productionSteps = new List<ProductionStep>();

	[SerializeField]
	private JobType jobType;

	[SerializeField]
	private List<string> forbiddenOnStart = new List<string>();

	[SerializeField]
	private ItemMaterialCategory itemMaterialCategory;

	[SerializeField]
	private bool hasFreshness;

	[SerializeField]
	private List<ProductionMode> ignoreProductionModeUI;

	[SerializeField]
	private LocKeys[] locKeys;

	[SerializeField]
	private string iconPath;

	[SerializeField]
	private string iconColorValue;

	[SerializeField]
	private string iconBackgroundPath;

	[SerializeField]
	private bool productInheritsName;

	[SerializeField]
	private bool useIngredientCombo;

	[SerializeField]
	private int ingredientComboSpawnAmount;

	[SerializeField]
	private AttributeType ingredientComboModifyingAttribute;

	[SerializeField]
	private bool repair;

	[SerializeField]
	private bool usesAltAnimation;

	[SerializeField]
	private bool skipHumanCarcassesInFilter;

	[NonSerialized]
	private HashSet<Resource> allUsableResourcesCache;

	[NonSerialized]
	private HashSet<Resource> allUsableResourcesRequiredForRepairingCache;

	[NonSerialized]
	private int recipeTotalCountCache = -1;

	[NonSerialized]
	private HashSet<ResourceCategory> allRecipeCategories;

	[NonSerialized]
	private List<ResourceGroups> storableResourceGroups;

	public HashSet<Resource> AllUsableResources
	{
		get
		{
			if (allUsableResourcesCache == null)
			{
				CacheUsableResources();
			}
			return allUsableResourcesCache;
		}
	}

	public List<ResourceGroups> StorableResourceGroups
	{
		get
		{
			if (storableResourceGroups == null || storableResourceGroups.Count == 0)
			{
				InitializeStorableResourceGroups();
			}
			return storableResourceGroups;
		}
	}

	public LocKeys[] LocKeys => locKeys;

	public string IconPath => iconPath;

	public List<SkillLevelPair> RequiredSkills => requiredSkills;

	public List<ProductModel> Products => produced;

	public List<KeyIntPair> Recipe => recipe;

	public List<KeyIntPair> SecondaryRecipe => secondaryRecipe;

	public ItemMaterialCategory ItemMaterialCategory => itemMaterialCategory;

	public bool HasFreshness => hasFreshness;

	public List<ProductionStep> ProductionSteps => productionSteps;

	public List<CustomProduct> CustomProducts => customProducts;

	public JobType JobType => jobType;

	public List<string> ForbiddenOnStart => forbiddenOnStart;

	public List<ProductionMode> IgnoreProductionModeUI => ignoreProductionModeUI;

	public string IconBackgroundPath => iconBackgroundPath;

	public bool ProductInheritsName => productInheritsName;

	public string IconColorValue => iconColorValue;

	public bool UseIngredientCombo => useIngredientCombo;

	public int IngredientComboSpawnAmount => ingredientComboSpawnAmount;

	public AttributeType IngredientComboModifyingAttribute => ingredientComboModifyingAttribute;

	public bool Repair => repair;

	public bool UsesAltAnimation => usesAltAnimation;

	public bool SkipHumanCarcassesInFilter => skipHumanCarcassesInFilter;

	public bool RequiresNoResources()
	{
		if (recipe != null)
		{
			return recipe.Count == 0;
		}
		return true;
	}

	public HitpointsPercentRangeType GetHitpointsPercentRangeType(Resource resource)
	{
		if (resource == null)
		{
			return HitpointsPercentRangeType.DefaultHitpointsRange;
		}
		if (repair && resource.ItemMaterialCategory > ItemMaterialCategory.None)
		{
			return HitpointsPercentRangeType.RepairItemHitpointsRange;
		}
		return HitpointsPercentRangeType.DefaultHitpointsRange;
	}

	public HashSet<ResourceCategory> GetAllRecipeCategories()
	{
		if (allRecipeCategories != null)
		{
			return allRecipeCategories;
		}
		allRecipeCategories = new HashSet<ResourceCategory>();
		foreach (KeyIntPair item in recipe)
		{
			string iD = item.GetID();
			if (!int.TryParse(iD, out var result))
			{
				foreach (ResourceCategory allCategory in Repository<ResourceRepository, Resource>.Instance.GetByID(iD).AllCategories)
				{
					allRecipeCategories.Add(allCategory);
				}
				continue;
			}
			ResourceCategory resourceCategory = (ResourceCategory)result;
			ResourceCategory[] allResourceCategories = EnumValues.AllResourceCategories;
			foreach (ResourceCategory resourceCategory2 in allResourceCategories)
			{
				if ((resourceCategory & resourceCategory2) != 0 && resourceCategory2 != 0)
				{
					allRecipeCategories.Add(resourceCategory2);
				}
			}
		}
		foreach (KeyIntPair item2 in secondaryRecipe)
		{
			string iD2 = item2.GetID();
			if (!int.TryParse(iD2, out var result2))
			{
				foreach (ResourceCategory allCategory2 in Repository<ResourceRepository, Resource>.Instance.GetByID(iD2).AllCategories)
				{
					allRecipeCategories.Add(allCategory2);
				}
				continue;
			}
			ResourceCategory resourceCategory3 = (ResourceCategory)result2;
			ResourceCategory[] allResourceCategories = EnumValues.AllResourceCategories;
			foreach (ResourceCategory resourceCategory4 in allResourceCategories)
			{
				if ((resourceCategory3 & resourceCategory4) != 0 && resourceCategory4 != 0)
				{
					allRecipeCategories.Add(resourceCategory4);
				}
			}
		}
		return allRecipeCategories;
	}

	public int GetRecipeTotalResourceCount()
	{
		if (IsDismantle())
		{
			return 1;
		}
		if (recipe.Count == 0)
		{
			return 0;
		}
		if (recipeTotalCountCache < 0)
		{
			recipeTotalCountCache = 0;
			if (repair && itemMaterialCategory > ItemMaterialCategory.None)
			{
				recipeTotalCountCache++;
			}
			foreach (KeyIntPair item in recipe)
			{
				recipeTotalCountCache += item.Value;
			}
			foreach (KeyIntPair item2 in secondaryRecipe)
			{
				recipeTotalCountCache += item2.Value;
			}
		}
		return recipeTotalCountCache;
	}

	public int GetAllProductsCount()
	{
		if (!Repository<ResourceRepository, Resource>.Instance.TryGetValue(GetID(), out var model))
		{
			model = Repository<ResourceRepository, Resource>.Instance.GetByGroupIdentifier(GetID());
		}
		if (model != null)
		{
			if (!string.IsNullOrEmpty(model.GroupIdentifier))
			{
				return MonoSingleton<ResourcePileTracker>.Instance.GetCount(GetID()).TotalCount + MonoSingleton<WorkerManager>.Instance.GetResourceGroupCountFromWorkerStorage(GetID());
			}
			return MonoSingleton<ResourcePileTracker>.Instance.GetCount(model).TotalCount + MonoSingleton<WorkerManager>.Instance.GetResourceCountFromWorkerStorage(model);
		}
		return MonoSingleton<ResourcePileTracker>.Instance.GetCount(GetID()).TotalCount + MonoSingleton<WorkerManager>.Instance.GetResourceGroupCountFromWorkerStorage(GetID());
	}

	public CustomProduct GetCustomProduct(string id)
	{
		foreach (CustomProduct customProduct in customProducts)
		{
			if (customProduct.GetID().Equals(id))
			{
				return customProduct;
			}
		}
		return null;
	}

	public bool IsDismantle()
	{
		if (ItemMaterialCategory > ItemMaterialCategory.None)
		{
			return recipe.Count == 0;
		}
		return false;
	}

	public override string GetID()
	{
		return id;
	}

	public void GenerateProductionModeTooltipData(ProductionInstance productionInstance, Action<string> onNewLine)
	{
		switch (productionInstance.Mode)
		{
		case ProductionMode.Amount:
			onNewLine(TooltipStyles.ApplyStyle(MonoSingleton<LocalizationController>.Instance.GetText("production_amount_info"), TooltipStyles.TooltipDescriptionLine));
			break;
		case ProductionMode.Forever:
			onNewLine(TooltipStyles.ApplyStyle(MonoSingleton<LocalizationController>.Instance.GetText("production_forever_info"), TooltipStyles.TooltipDescriptionLine));
			break;
		case ProductionMode.Until:
			onNewLine((productionInstance.Blueprint.JobType == JobType.Research) ? TooltipStyles.ApplyStyle(MonoSingleton<LocalizationController>.Instance.GetText("production_until_info_books"), TooltipStyles.TooltipDescriptionLine) : TooltipStyles.ApplyStyle(MonoSingleton<LocalizationController>.Instance.GetText("production_until_info"), TooltipStyles.TooltipDescriptionLine));
			break;
		}
	}

	public void GenerateCurrentTooltipData(ProductionInstance productionInstance, Action<string> onNewLine)
	{
		onNewLine(TooltipStyles.ApplyStyle(GetProduct(), TooltipStyles.TooltipTitle));
		if (recipe.Count == 0)
		{
			return;
		}
		onNewLine(TooltipStyles.ApplyStyle(MonoSingleton<LocalizationController>.Instance.GetText("menu_recipe"), TooltipStyles.TooltipSubtitleLineStyle));
		if (repair && itemMaterialCategory > ItemMaterialCategory.None)
		{
			onNewLine(MonoSingleton<LocalizationController>.Instance.GetText("repair_item_description_" + itemMaterialCategory.ToString().ToLower()));
		}
		foreach (KeyIntPair item in secondaryRecipe)
		{
			onNewLine(GetCurrentSecondaryRecipe(item.GetID(), item.Value, productionInstance));
		}
		foreach (KeyIntPair item2 in recipe)
		{
			onNewLine(GetCurrentRecipe(item2.GetID(), item2.Value, productionInstance));
		}
	}

	public List<string> GenerateTooltipData()
	{
		List<string> list = new List<string> { TooltipStyles.ApplyStyle(GetProduct(), TooltipStyles.TooltipTitle) };
		if (recipe.Count > 0)
		{
			list.Add(TooltipStyles.ApplyStyle(MonoSingleton<LocalizationController>.Instance.GetText("menu_recipe"), TooltipStyles.TooltipSubtitleLineStyle));
			if (repair && itemMaterialCategory > ItemMaterialCategory.None)
			{
				list.Add(MonoSingleton<LocalizationController>.Instance.GetText("repair_item_description_" + itemMaterialCategory.ToString().ToLower()));
			}
			foreach (KeyIntPair item in recipe)
			{
				list.Add(GetRecipe(item.GetID(), item.Value));
			}
		}
		if (secondaryRecipe.Count > 0)
		{
			foreach (KeyIntPair item2 in secondaryRecipe)
			{
				list.Add(GetRecipe(item2.GetID(), item2.Value));
			}
		}
		list.Add(TooltipStyles.ApplyStyle(MonoSingleton<LocalizationController>.Instance.GetText("menu_produced") + ":", TooltipStyles.TooltipSubtitleLineStyle));
		if (useIngredientCombo)
		{
			list.Add(GetProduct());
		}
		if (produced.Count > 0)
		{
			foreach (ProductModel item3 in produced)
			{
				list.Add(GetProduct(item3.GetID(), item3.Amount));
			}
		}
		else if (repair)
		{
			list.Add(GetRepairProduct());
		}
		else
		{
			list.Add(GetProduct());
		}
		if (jobType != 0)
		{
			list.Add(TooltipStyles.ApplyStyle(MonoSingleton<LocalizationController>.Instance.GetText("general_job_type") + ":", TooltipStyles.TooltipSubtitleLineStyle));
			list.Add(GetRequiredJob());
		}
		List<string> requiredSkillLevel = GetRequiredSkillLevel();
		if (requiredSkillLevel.Count > 0)
		{
			list.Add(TooltipStyles.ApplyStyle(MonoSingleton<LocalizationController>.Instance.GetText("needed_skills") + ":", TooltipStyles.TooltipSubtitleLineStyle));
			list.AddRange(requiredSkillLevel);
		}
		return list;
	}

	public bool IsResourceUsableByDefault(Resource resource)
	{
		if (AllUsableResources.Contains(resource))
		{
			return !ForbiddenOnStart.Contains(resource.GetID());
		}
		return false;
	}

	public bool HasSkillsRequired(IProductionAgent agent)
	{
		if (agent == null)
		{
			return false;
		}
		if (RequiredSkills == null)
		{
			return true;
		}
		foreach (SkillLevelPair requiredSkill in RequiredSkills)
		{
			if (!(requiredSkill == null) && (agent.SkillIsBlocked(requiredSkill.Key) || agent.GetSkillLevel(requiredSkill.Key) < requiredSkill.Value))
			{
				return false;
			}
		}
		return true;
	}

	public SkillLevelPair FindFirstUnmetSkillRequirement(IProductionAgent agent)
	{
		if (agent == null)
		{
			return null;
		}
		if (RequiredSkills == null)
		{
			return null;
		}
		foreach (SkillLevelPair requiredSkill in RequiredSkills)
		{
			if (!(requiredSkill == null) && (agent.SkillIsBlocked(requiredSkill.Key) || agent.GetSkillLevel(requiredSkill.Key) < requiredSkill.Value))
			{
				return requiredSkill;
			}
		}
		return null;
	}

	public bool RecipeIsExactResource()
	{
		if (recipe.Count == 0)
		{
			return false;
		}
		foreach (KeyIntPair item in recipe)
		{
			if (int.TryParse(item.GetID(), out var _))
			{
				return false;
			}
		}
		return true;
	}

	public bool RecipeIsNoResources()
	{
		return recipe.Count == 0;
	}

	public List<ProductModel> GetProducts(ProductionInstance instance)
	{
		if (instance.HasDisposed)
		{
			return null;
		}
		if (Products != null && Products.Count > 0)
		{
			return Products;
		}
		return null;
	}

	public CustomProduct GetCustomProduct(ProductionInstance instance)
	{
		if (instance.HasDisposed)
		{
			return null;
		}
		if (CustomProducts == null || CustomProducts.Count <= 0 || instance.Storage.IsEmpty())
		{
			return null;
		}
		if (recipe.Count == 0)
		{
			bool isEnabled;
			FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(54, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\Production\\Production.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Invalid entry in production repository. No recipe. ID:");
				messageBuilder.AppendFormatted(id);
			}
			Log.Error(messageBuilder);
			return null;
		}
		if (!int.TryParse(recipe[0].GetID(), out var result))
		{
			return null;
		}
		ResourceCategory customProductCategory = (ResourceCategory)result;
		ResourceInstance resourceInstance = instance.Storage.Resources.FirstOrDefault((ResourceInstance ri) => (ri.Blueprint.Category & customProductCategory) != 0);
		if (resourceInstance == null)
		{
			return null;
		}
		return GetCustomProduct(resourceInstance.Blueprint.GetID());
	}

	private void InitializeStorableResourceGroups()
	{
		storableResourceGroups = new List<ResourceGroups>();
		ResourceGroupsModel byID = Repository<ResourceGroupsRepository, ResourceGroupsModel>.Instance.GetByID("all_resource_groups");
		if (byID == null)
		{
			Log.Warning("No resource groups found for 'all_resource_groups'.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Models\\Production\\Production.cs");
			return;
		}
		foreach (Resource allUsableResource in AllUsableResources)
		{
			ResourceGroups byID2 = byID.GetByID(allUsableResource.NewSortingGroup);
			if (!storableResourceGroups.Contains(byID2))
			{
				storableResourceGroups.Add(byID2);
			}
		}
		Queue<ResourceGroups> queue = new Queue<ResourceGroups>();
		queue.EnqueueRange(storableResourceGroups);
		while (queue.Count > 0)
		{
			ResourceGroups existingParent = queue.Dequeue();
			ResourceGroups resourceGroups = byID.ResourceGroups.FirstOrDefault((ResourceGroups group) => group.SubGroupIDs.Contains(existingParent.GetID()));
			if (!(resourceGroups == null) && !storableResourceGroups.Contains(resourceGroups))
			{
				storableResourceGroups.Add(resourceGroups);
				queue.Enqueue(resourceGroups);
			}
		}
	}

	private void CacheUsableResources()
	{
		allUsableResourcesCache = new HashSet<Resource>();
		if (ItemMaterialCategory > ItemMaterialCategory.None)
		{
			foreach (Resource item in Repository<ResourceRepository, Resource>.Instance.GetAllResourcesByMaterial(ItemMaterialCategory))
			{
				allUsableResourcesCache.Add(item);
			}
		}
		if (recipe == null)
		{
			return;
		}
		foreach (KeyIntPair item2 in recipe)
		{
			string iD = item2.GetID();
			if (int.TryParse(iD, out var result))
			{
				ResourceCategory resourceCategory = (ResourceCategory)result;
				if (resourceCategory.Equals(ResourceCategory.None))
				{
					continue;
				}
				foreach (Resource item3 in Repository<ResourceRepository, Resource>.Instance.GetAllResourcesByResourceCategory(resourceCategory))
				{
					allUsableResourcesCache.Add(item3);
				}
			}
			else
			{
				Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(iD);
				if (!ForbiddenOnStart.Contains(byID.GetID()))
				{
					allUsableResourcesCache.Add(byID);
				}
			}
		}
		foreach (KeyIntPair item4 in secondaryRecipe)
		{
			string iD2 = item4.GetID();
			if (int.TryParse(iD2, out var result2))
			{
				ResourceCategory resourceCategory2 = (ResourceCategory)result2;
				if (resourceCategory2.Equals(ResourceCategory.None))
				{
					continue;
				}
				foreach (Resource item5 in Repository<ResourceRepository, Resource>.Instance.GetAllResourcesByResourceCategory(resourceCategory2))
				{
					allUsableResourcesCache.Add(item5);
				}
			}
			else
			{
				Resource byID2 = Repository<ResourceRepository, Resource>.Instance.GetByID(iD2);
				if (!ForbiddenOnStart.Contains(byID2.GetID()))
				{
					allUsableResourcesCache.Add(byID2);
				}
			}
		}
	}

	private string GetRequiredJob()
	{
		return TooltipStyles.ApplyStyle(MonoSingleton<LocalizationController>.Instance.GetText("job_name_" + jobType) ?? "", TooltipStyles.TooltipDescriptionLine);
	}

	private List<string> GetRequiredSkillLevel()
	{
		List<string> list = new List<string>();
		List<SkillLevelPair> list2 = new List<SkillLevelPair>();
		bool flag = false;
		SkillLevelPair skillToUpdate = null;
		foreach (ProductionStep productionStep in ProductionSteps)
		{
			_ = productionStep;
			if (requiredSkills == null)
			{
				continue;
			}
			foreach (SkillLevelPair requiredSkill in requiredSkills)
			{
				if (list2.Count == 0)
				{
					list2.Add(requiredSkill);
				}
				for (int i = 0; i < list2.Count; i++)
				{
					if (list2[i].Key == requiredSkill.Key)
					{
						flag = true;
						if (list2[i].Value < requiredSkill.Value)
						{
							skillToUpdate = list2[i];
						}
					}
					else
					{
						flag = false;
					}
				}
				if (!flag)
				{
					list2.Add(requiredSkill);
				}
				if (skillToUpdate != null)
				{
					list2[list2.FindIndex((SkillLevelPair ind) => ind.Equals(skillToUpdate))] = requiredSkill;
					skillToUpdate = null;
				}
			}
		}
		foreach (SkillLevelPair item in list2)
		{
			list.Add(TooltipStyles.ApplyStyle(string.Format("{0} {1}: {2}", AssetUtils.GetSpriteAsset(item.Key.ToString().ToLower() ?? ""), MonoSingleton<LocalizationController>.Instance.GetText($"skill_name_{item.Key}"), item.Value), TooltipStyles.TooltipDescriptionLine));
		}
		return list;
	}

	private string GetProduct()
	{
		return MonoSingleton<LocalizationController>.Instance.GetText(LocKeyUtils.GetName(locKeys)) ?? "";
	}

	private string GetRepairProduct()
	{
		return MonoSingleton<LocalizationController>.Instance.GetText(LocKeyUtils.GetName(locKeys) + "_produced") ?? "";
	}

	private string GetProduct(string id)
	{
		Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(id);
		if (byID == null)
		{
			return string.Empty;
		}
		return ResourceUtils.GetTextIcon(byID) + " " + ResourceUtils.GetLocalizedResourceName(byID, showQuality: false);
	}

	private string GetProduct(string id, int value)
	{
		if (Repository<ResourceRepository, Resource>.Instance.GetByID(id) == null)
		{
			return string.Empty;
		}
		return $"{ResourceUtils.GetTextIcon(id)} {value} {ResourceUtils.GetLocalizedResourceName(id, showQuality: false)}";
	}

	private string GetProduct(Resource resource)
	{
		if (resource == null)
		{
			return string.Empty;
		}
		string iD = resource.GetID();
		return ResourceUtils.GetTextIcon(iD) + " " + ResourceUtils.GetLocalizedResourceName(iD, showQuality: false);
	}

	private string GetRecipe(string id, int value)
	{
		string text;
		if (!int.TryParse(id, out var result))
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(id);
			if (byID == null)
			{
				return string.Empty;
			}
			text = ((value <= MonoSingleton<ResourcePileTracker>.Instance.GetCount(byID).TotalCount) ? "Normal" : "DefaultRed");
			return $"{ResourceUtils.GetTextIcon(byID)} <style={text}>{value}</style>  {ResourceUtils.GetLocalizedResourceName(byID)}";
		}
		string text2 = MonoSingleton<LocalizationController>.Instance.GetText($"resource_category_name_{(ResourceCategory)result}");
		text = ((value <= MonoSingleton<ResourcePileTracker>.Instance.GetCount((ResourceCategory)result).TotalCount) ? "Normal" : "DefaultRed");
		return $"<style={text}>{value}</style> <style=ResourceCategory>{text2}</style>";
	}

	private string GetCurrentRecipe(string id, int value, ProductionInstance productionInstance)
	{
		if (productionInstance == null || productionInstance.HasDisposed)
		{
			return string.Empty;
		}
		string text;
		if (!int.TryParse(id, out var result))
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(id);
			if (byID == null)
			{
				return string.Empty;
			}
			int num = productionInstance.Storage.GetById(byID.GetID())?.Amount ?? 0;
			text = ((num >= value || value <= MonoSingleton<ResourcePileTracker>.Instance.GetCount(byID).AllowedCount) ? "Normal" : "DefaultRed");
			return $"{ResourceUtils.GetTextIcon(byID)} <style={text}>{num}/{value}</style> {ResourceUtils.GetLocalizedResourceName(byID)}";
		}
		int num2 = 0;
		foreach (Resource allItem in Repository<ResourceRepository, Resource>.Instance.GetAllItems())
		{
			if (allItem.Category.HasFlag((ResourceCategory)result))
			{
				num2 += productionInstance.Storage.GetById(allItem.GetID())?.Amount ?? 0;
			}
		}
		string text2 = MonoSingleton<LocalizationController>.Instance.GetText($"resource_category_name_{(ResourceCategory)result}");
		text = ((num2 >= value || value <= MonoSingleton<ResourcePileTracker>.Instance.GetCount((ResourceCategory)result).AllowedCount) ? "Normal" : "DefaultRed");
		return $"<style={text}>{num2}/{value}</style> <style=ResourceCategory>{text2}</style>";
	}

	private string GetCurrentSecondaryRecipe(string id, int value, ProductionInstance productionInstance)
	{
		if (productionInstance == null || productionInstance.HasDisposed)
		{
			return string.Empty;
		}
		string text;
		if (!int.TryParse(id, out var result))
		{
			Resource byID = Repository<ResourceRepository, Resource>.Instance.GetByID(id);
			if (byID == null)
			{
				return string.Empty;
			}
			int num = productionInstance.SecondaryIngredientStorage.GetById(byID.GetID())?.Amount ?? 0;
			text = ((num >= value || value <= MonoSingleton<ResourcePileTracker>.Instance.GetCount(byID).AllowedCount) ? "Normal" : "DefaultRed");
			return $"{ResourceUtils.GetTextIcon(byID)} <style={text}>{num}/{value}</style> {ResourceUtils.GetLocalizedResourceName(byID)}";
		}
		int num2 = 0;
		foreach (Resource allItem in Repository<ResourceRepository, Resource>.Instance.GetAllItems())
		{
			if (allItem.Category.HasFlag((ResourceCategory)result))
			{
				num2 += productionInstance.SecondaryIngredientStorage.GetById(allItem.GetID())?.Amount ?? 0;
			}
		}
		string text2 = MonoSingleton<LocalizationController>.Instance.GetText($"resource_category_name_{(ResourceCategory)result}");
		text = ((num2 >= value || value <= MonoSingleton<ResourcePileTracker>.Instance.GetCount((ResourceCategory)result).AllowedCount) ? "Normal" : "DefaultRed");
		return $"<style={text}>{num2}/{value}</style> <style=ResourceCategory>{text2}</style>";
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
