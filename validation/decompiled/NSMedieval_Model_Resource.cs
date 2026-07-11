using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NSEipix.Base;
using NSEipix.Model;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.Construction;
using NSMedieval.Repository;
using NSMedieval.State;
using NSMedieval.StatsSystem;
using NSMedieval.Types;
using UnityEngine;

namespace NSMedieval.Model;

[Serializable]
public class Resource : NSEipix.Base.Model
{
	private const string PileTransformSettingsName = "PileTransformSettings";

	private const string EquippedTransformSettingsName = "EquippedTransformSettings";

	[SerializeField]
	private string id = string.Empty;

	[SerializeField]
	private float weight;

	[SerializeField]
	private ResourceCategory category;

	[SerializeField]
	private string sortingGroup;

	[SerializeField]
	private string newSortingGroup;

	[SerializeField]
	private float nutrition;

	[SerializeField]
	private float nutritionPerHp;

	[SerializeField]
	private int stackingLimit;

	[SerializeField]
	private float hitpoints;

	[SerializeField]
	private string decomposeModifiersId;

	[SerializeField]
	private string rotModifiersId;

	[SerializeField]
	private string fermentModifiersId;

	[SerializeField]
	private string rottenId = string.Empty;

	[SerializeField]
	private string fermentedId = string.Empty;

	[SerializeField]
	private string[] onUseEffects;

	[SerializeField]
	private ProductQuality quality;

	[SerializeField]
	private string prefabPileID;

	[SerializeField]
	private string equippedPrefabID;

	[SerializeField]
	private string skinnedMeshID;

	[SerializeField]
	private string iconPath;

	[SerializeField]
	private string iconColorOverlay;

	[SerializeField]
	private string iconBackgroundPath;

	[SerializeField]
	private bool hasQuality;

	[SerializeField]
	private string groupIdentifier;

	[SerializeField]
	private string newGroupIdentifier;

	[SerializeField]
	private string protoId;

	[SerializeField]
	private float healing;

	[SerializeField]
	private float wealthPoints = 10f;

	[SerializeField]
	private string proximityEffector;

	[SerializeField]
	private string proximityEnterEffector;

	[SerializeField]
	private ItemMaterialCategory itemMaterialCategory;

	[SerializeField]
	private List<KeyIntPair> dismantledProducts = new List<KeyIntPair>();

	[SerializeField]
	private float caloriesCount;

	[SerializeField]
	private IngredientFlags ingredientFlag;

	[SerializeField]
	private List<IngredientFlags> producedFromIngredients;

	private List<ResourceCategory> allCategories;

	[NonSerialized]
	private Equipment equipmentBlueprint;

	[SerializeField]
	private string thermalModelID = string.Empty;

	[SerializeField]
	private List<string> almanacTags;

	[SerializeField]
	private string buildingBlueprintID;

	[SerializeField]
	private LocKeys[] locKeys;

	[SerializeField]
	private string[] materials;

	[SerializeField]
	private bool hideInGame;

	[SerializeField]
	private AssetByQuality[] equippedTexturePaths;

	[SerializeField]
	private int haulPriority;

	[SerializeField]
	private float beautyInput;

	[SerializeField]
	private float beautyInputInside;

	[SerializeField]
	private float beautyInputOnShelf;

	[SerializeField]
	private bool logOwner;

	[SerializeField]
	private bool isHumanSource;

	[SerializeField]
	private bool constantWealthPoints;

	[SerializeField]
	private int razzleDazzle;

	[SerializeField]
	private int alcoholStrength;

	[SerializeField]
	private float wetnessOnDestroy;

	[SerializeField]
	private float flammability;

	[SerializeField]
	private float fireExtinguisher;

	[SerializeField]
	private float spawnOilRadius;

	[SerializeField]
	private byte oilType;

	[SerializeField]
	private List<MeshVariationList> variationLists;

	[SerializeField]
	private TransformSettings[] transformSettingsArray;

	[SerializeField]
	private bool uniqueResource;

	[SerializeField]
	private SkillType affectedSkillType;

	[SerializeField]
	private bool isArt;

	[SerializeField]
	private bool generateTaintedVersion;

	[SerializeField]
	private string upcookedVersion;

	[NonSerialized]
	private bool tainted;

	[NonSerialized]
	private bool isBuildingStructure;

	[NonSerialized]
	private bool isBuildingStructureInitialized;

	[NonSerialized]
	private ThermalModel thermalModelCache;

	[NonSerialized]
	private bool thermalModelCacheInitialized;

	[NonSerialized]
	private DecayModifiers decomposeModifiers;

	[NonSerialized]
	private DecayModifiers rottingModifiers;

	[NonSerialized]
	private DecayModifiers fermentingModifiers;

	[NonSerialized]
	private float walkSpeedMultiplier = 0.75f;

	[NonSerialized]
	private bool beautyInitialized;

	[NonSerialized]
	private ProductQualityBase productQualityBase;

	[NonSerialized]
	private MaterialSettings materialSettings;

	[NonSerialized]
	private bool materialSettingsInitialized;

	[NonSerialized]
	private Dictionary<string, MeshVariationList> variationsById;

	private bool failedToFindEquipmentBlueprint;

	private bool blueprintInitSuccessful;

	private const float DefaultWeight = 10f;

	public override bool HideInGame => hideInGame;

	public string ProximityEffector => proximityEffector;

	public string ProximityEnterEffector => proximityEnterEffector;

	public float Healing => healing;

	public bool IsBuildingStructure
	{
		get
		{
			if (isBuildingStructureInitialized)
			{
				return isBuildingStructure;
			}
			isBuildingStructureInitialized = true;
			isBuildingStructure = Repository<BaseBuildingRepository, BaseBuildingBlueprint>.Instance.GetByID(GetID()) != null;
			return isBuildingStructure;
		}
	}

	public int HaulPriority => haulPriority;

	public Equipment EquipmentBlueprint
	{
		get
		{
			if (failedToFindEquipmentBlueprint)
			{
				return null;
			}
			if (blueprintInitSuccessful)
			{
				return equipmentBlueprint;
			}
			if (!Repository<EquipmentRepository, Equipment>.Instance.TryGetValue(id, out var model))
			{
				failedToFindEquipmentBlueprint = true;
				return null;
			}
			equipmentBlueprint = model;
			blueprintInitSuccessful = true;
			return equipmentBlueprint;
		}
	}

	public float Weight => weight;

	public ResourceCategory Category => category;

	public string SortingGroup => sortingGroup;

	public string NewSortingGroup
	{
		get
		{
			if (!string.IsNullOrEmpty(newSortingGroup))
			{
				return newSortingGroup;
			}
			return sortingGroup;
		}
	}

	public float Nutrition => nutrition;

	public float NutritionPerHp => nutritionPerHp;

	public int StackingLimit => stackingLimit;

	public float Hitpoints => hitpoints;

	public string RottenId => rottenId;

	public string[] OnUseEffects => onUseEffects;

	public DecayModifiers DecomposeModifiers
	{
		get
		{
			if (decomposeModifiers == null && !string.IsNullOrEmpty(decomposeModifiersId))
			{
				decomposeModifiers = Repository<DecayModifiersRepository, DecayModifiers>.Instance.GetByID(decomposeModifiersId);
			}
			return decomposeModifiers;
		}
	}

	public DecayModifiers RottingModifiers
	{
		get
		{
			if (rottingModifiers == null && !string.IsNullOrEmpty(rotModifiersId))
			{
				rottingModifiers = Repository<DecayModifiersRepository, DecayModifiers>.Instance.GetByID(rotModifiersId);
			}
			return rottingModifiers;
		}
	}

	public DecayModifiers FermentingModifiers
	{
		get
		{
			if (fermentingModifiers == null && !string.IsNullOrEmpty(fermentModifiersId))
			{
				fermentingModifiers = Repository<DecayModifiersRepository, DecayModifiers>.Instance.GetByID(fermentModifiersId);
			}
			return fermentingModifiers;
		}
	}

	public ProductQuality Quality => quality;

	public string PrefabPileID => prefabPileID;

	public string EquippedPrefabID => equippedPrefabID;

	public string IconPath => iconPath;

	public bool UniqueResource => uniqueResource;

	public IngredientFlags IngredientFlag => ingredientFlag;

	public List<IngredientFlags> ProducedFromIngredients => producedFromIngredients;

	public List<ResourceCategory> AllCategories
	{
		get
		{
			if (allCategories == null)
			{
				allCategories = new List<ResourceCategory>();
			}
			if (allCategories.Count == 0)
			{
				ResourceCategory[] allResourceCategories = EnumValues.AllResourceCategories;
				foreach (ResourceCategory resourceCategory in allResourceCategories)
				{
					if (category.HasFlag(resourceCategory) && !allCategories.Contains(resourceCategory))
					{
						allCategories.Add(resourceCategory);
					}
				}
			}
			return allCategories;
		}
	}

	public bool HasQuality => hasQuality;

	public string GroupIdentifier
	{
		get
		{
			if (!string.IsNullOrEmpty(groupIdentifier))
			{
				return groupIdentifier;
			}
			return id;
		}
	}

	public string NewGroupIdentifier
	{
		get
		{
			if (!string.IsNullOrEmpty(newGroupIdentifier))
			{
				return newGroupIdentifier;
			}
			return groupIdentifier;
		}
	}

	public string ProtoId => protoId;

	public float WealthPoints => wealthPoints;

	public ItemMaterialCategory ItemMaterialCategory => itemMaterialCategory;

	public List<KeyIntPair> DismantledProduct => dismantledProducts;

	public ThermalModel ThermalModel
	{
		get
		{
			if (!thermalModelCacheInitialized)
			{
				thermalModelCacheInitialized = true;
				if (!string.IsNullOrEmpty(thermalModelID))
				{
					thermalModelCache = Repository<ThermalModelRepository, ThermalModel>.Instance.GetByID(thermalModelID);
				}
			}
			return thermalModelCache;
		}
	}

	public List<string> AlmanacTags => almanacTags;

	public string BuildingBlueprintID => buildingBlueprintID;

	public float CaloriesCount => caloriesCount;

	public LocKeys[] LocKeys => locKeys;

	public string[] Materials => materials ?? new string[1] { string.Empty };

	public string DecomposeModifiersId => decomposeModifiersId;

	public string IconBackgroundPath => iconBackgroundPath;

	public AssetByQuality[] EquippedTexturePaths => equippedTexturePaths;

	public float WalkSpeedMultiplier => walkSpeedMultiplier;

	public string Material => Materials.FirstOrDefault() ?? string.Empty;

	public string FermentedId => fermentedId;

	public ProductQualityBase ProductQualityBase => productQualityBase;

	public MaterialSettings MaterialSettings
	{
		get
		{
			if (!materialSettingsInitialized)
			{
				materialSettingsInitialized = true;
				materialSettings = (string.IsNullOrEmpty(Material) ? null : Repository<MaterialSettingsRepository, MaterialSettings>.Instance.GetByID(Material));
			}
			return materialSettings;
		}
	}

	public float BeautyInput
	{
		get
		{
			float num = (hasQuality ? productQualityBase.BeautyInputAdd : 0f) + ((MaterialSettings != null) ? materialSettings.BeautyInputAdd : 0f);
			TryInitBeautyValues();
			return num + beautyInput;
		}
	}

	public float BeautyInputInside
	{
		get
		{
			float num = (hasQuality ? productQualityBase.BeautyInputInsideAdd : 0f) + ((MaterialSettings != null) ? materialSettings.BeautyInputInsideAdd : 0f);
			TryInitBeautyValues();
			return num + beautyInputInside;
		}
	}

	public float BeautyInputOnShelf
	{
		get
		{
			float num = (hasQuality ? productQualityBase.BeautyInputOnShelfAdd : 0f) + ((MaterialSettings != null) ? materialSettings.BeautyInputOnShelfAdd : 0f);
			TryInitBeautyValues();
			return num + beautyInputOnShelf;
		}
	}

	public bool HasOnShelfBeauty => !Mathf.Approximately(BeautyInputOnShelf, BeautyInput);

	public bool HasInsideBeauty => !Mathf.Approximately(BeautyInputOnShelf, BeautyInput);

	public bool LodOwner => logOwner;

	public bool IsHumanSource => isHumanSource;

	public bool ConstantWealthPoints => constantWealthPoints;

	public int RazzleDazzle => razzleDazzle;

	public int AlcoholStrength => alcoholStrength;

	public float WetnessOnDestroy => wetnessOnDestroy;

	public float Flammability => flammability;

	public float FireExtinguisher => fireExtinguisher;

	public float SpawnOilRadius => spawnOilRadius;

	public byte OilType => oilType;

	public List<MeshVariationList> VariationLists => variationLists;

	public TransformSettings[] TransformSettingsArray => transformSettingsArray;

	public string SkinnedMeshID => skinnedMeshID;

	public Dictionary<string, MeshVariationList> VariationsById
	{
		get
		{
			if (variationsById == null)
			{
				variationsById = new Dictionary<string, MeshVariationList>();
				foreach (MeshVariationList variationList in variationLists)
				{
					variationsById.TryAdd(variationList.Name, variationList);
				}
			}
			return variationsById;
		}
	}

	public string IconColorOverlay => iconColorOverlay;

	public bool GenerateTaintedVersion => generateTaintedVersion;

	public bool Tainted => tainted;

	public SkillType AffectedSkillType => affectedSkillType;

	public bool IsArt => isArt;

	public string UpcookedVersion => upcookedVersion;

	public string GetEquippedTexturePath(BodyType bodyType)
	{
		List<AssetByQuality> list = equippedTexturePaths.Where((AssetByQuality equippedPrefabID) => equippedPrefabID.Quality == quality).ToList();
		if (list.Count == 0)
		{
			list = equippedTexturePaths.ToList();
		}
		AssetByQuality assetByQuality = list.FirstOrDefault();
		if (bodyType != 0)
		{
			assetByQuality = list.FirstOrDefault((AssetByQuality i) => i.BodyType.Equals(bodyType));
		}
		if (assetByQuality == null)
		{
			return string.Empty;
		}
		return assetByQuality.TexturePath;
	}

	public TransformSettings GetPileTransformSettings()
	{
		return transformSettingsArray?.FirstOrDefault((TransformSettings transformSettings) => transformSettings.GetID() == "PileTransformSettings");
	}

	public TransformSettings GetEquippedTransformSettings()
	{
		return transformSettingsArray?.FirstOrDefault((TransformSettings transformSettings) => transformSettings.GetID() == "EquippedTransformSettings");
	}

	public override string GetID()
	{
		return id;
	}

	public void SetCustomSortingGroup(string customSortingGroup)
	{
		newSortingGroup = customSortingGroup;
	}

	public void SetCustomGroupIdentifier(string customGroupIdentifier)
	{
		newGroupIdentifier = customGroupIdentifier;
	}

	public void SetupResourceWithQuality(string id, string iconPath, string iconBackgroundPath, LocKeys[] locKeys, string groupIdentifier, string protoId, bool hasQuality, ProductQuality quality, float weight, int stackingLimit, float hitpoints, string decomposeModifiersId, float wealthPoints, string sortingGroup, List<string> almanacTags, ItemMaterialCategory itemMaterialCategory, ResourceCategory category, int haulPriority, List<KeyIntPair> dismantledProducts, string[] materials, AssetByQuality[] equippedTexturePaths, ItemQuality itemQuality, Resource proto, bool tainted = false)
	{
		float num = (tainted ? 0.1f : 1f);
		this.id = id;
		this.iconPath = iconPath;
		this.iconBackgroundPath = iconBackgroundPath;
		this.locKeys = locKeys;
		this.groupIdentifier = ((!tainted) ? groupIdentifier : (groupIdentifier + "_tainted"));
		this.protoId = protoId;
		this.hasQuality = hasQuality;
		this.quality = quality;
		this.weight = weight;
		this.stackingLimit = stackingLimit;
		this.hitpoints = hitpoints;
		this.decomposeModifiersId = decomposeModifiersId;
		this.wealthPoints = wealthPoints * num;
		this.category = category;
		this.haulPriority = haulPriority;
		this.sortingGroup = sortingGroup;
		this.almanacTags = almanacTags;
		this.itemMaterialCategory = itemMaterialCategory;
		this.dismantledProducts = dismantledProducts;
		this.materials = materials;
		this.equippedTexturePaths = equippedTexturePaths;
		productQualityBase = itemQuality;
		prefabPileID = proto.PrefabPileID;
		equippedPrefabID = proto.EquippedPrefabID;
		skinnedMeshID = proto.SkinnedMeshID;
		beautyInput = proto.beautyInput;
		beautyInputInside = proto.beautyInputInside;
		beautyInputOnShelf = proto.beautyInputOnShelf;
		variationLists = proto.variationLists;
		transformSettingsArray = proto.transformSettingsArray;
		this.tainted = tainted;
	}

	public void SetupNewBuildingResource(string id, ResourceCategory category, float hitpoints, string prefabPileID, List<MeshVariationList> pileVariationLists, TransformSettings[] transformSettingsArray, string iconPath, float wealthPoints, List<string> almanacTags, string decomposeModifiersId, float weight, string buildingBlueprintID, string sortingGroup, string groupIdentifier, string protoId, bool hasQuality, ProductQuality productQuality, ProductQualityBase productQualityBase, LocKeys[] locKeys, bool isArt)
	{
		this.id = id;
		this.category = category;
		this.hitpoints = hitpoints;
		this.prefabPileID = prefabPileID;
		equippedPrefabID = string.Empty;
		skinnedMeshID = string.Empty;
		variationLists = pileVariationLists;
		this.transformSettingsArray = transformSettingsArray;
		this.iconPath = iconPath;
		this.wealthPoints = wealthPoints;
		this.almanacTags = almanacTags;
		this.decomposeModifiersId = (string.IsNullOrEmpty(decomposeModifiersId) ? "structure_pile_decay" : decomposeModifiersId);
		decomposeModifiers = Repository<DecayModifiersRepository, DecayModifiers>.Instance.GetByID(this.decomposeModifiersId);
		this.weight = ((weight <= 0f) ? 10f : weight);
		this.buildingBlueprintID = buildingBlueprintID;
		this.sortingGroup = sortingGroup;
		stackingLimit = 1;
		this.groupIdentifier = groupIdentifier;
		nutrition = 0f;
		rottenId = string.Empty;
		onUseEffects = new string[0];
		quality = productQuality;
		this.hasQuality = hasQuality;
		this.protoId = protoId;
		healing = 0f;
		proximityEffector = string.Empty;
		itemMaterialCategory = ItemMaterialCategory.None;
		dismantledProducts = new List<KeyIntPair>();
		this.productQualityBase = productQualityBase;
		this.locKeys = locKeys;
		this.isArt = isArt;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void TryInitBeautyValues()
	{
		if (!beautyInitialized)
		{
			beautyInitialized = true;
			if (beautyInputInside == 0f)
			{
				beautyInputInside = beautyInput;
			}
			if (beautyInputOnShelf == 0f)
			{
				beautyInputOnShelf = beautyInput;
			}
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
