using System;
using System.Linq;
using NSMedieval.Construction;
using NSMedieval.Enums;
using NSMedieval.GameEventSystem;
using NSMedieval.Manager;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.Objectives;
using NSMedieval.PlayerTriggeredEventSystem;
using NSMedieval.State;
using NSMedieval.State.WorkerJobs;
using NSMedieval.StatsSystem;
using NSMedieval.Types;
using NSMedieval.UI;
using NSMedieval.UI.Statistic;
using NSMedieval.Village.Map.Pathfinding;
using NSMedieval.Water;
using UI.Enums;

namespace NSMedieval;

public static class EnumValues
{
	public static readonly AnimalType[] AnimalTypes = (AnimalType[])Enum.GetValues(typeof(AnimalType));

	public static readonly EquipmentSlotType[] EquipmentSlotTypes = (EquipmentSlotType[])Enum.GetValues(typeof(EquipmentSlotType));

	public static readonly JobType[] AllJobTypes = (JobType[])Enum.GetValues(typeof(JobType));

	public static readonly BuildingType[] BuildingTypes = (BuildingType[])Enum.GetValues(typeof(BuildingType));

	public static readonly BodyType[] BodyTypes = (BodyType[])Enum.GetValues(typeof(BodyType));

	public static readonly ModifierType[] ModifierTypes = (ModifierType[])Enum.GetValues(typeof(ModifierType));

	public static readonly WarningMessageCategory[] WarningMessageCategories = (WarningMessageCategory[])Enum.GetValues(typeof(WarningMessageCategory));

	public static readonly ItemMaterialCategory[] ItemMaterialCategories = (ItemMaterialCategory[])Enum.GetValues(typeof(ItemMaterialCategory));

	public static readonly OrderType[] OrderTypes = (OrderType[])Enum.GetValues(typeof(OrderType));

	public static readonly SkillType[] SkillTypes = (SkillType[])Enum.GetValues(typeof(SkillType));

	public static readonly LinkType[] LinkTypes = (LinkType[])Enum.GetValues(typeof(LinkType));

	public static readonly ZonePriority[] ZonePriorities = (ZonePriority[])Enum.GetValues(typeof(ZonePriority));

	public static readonly BuildingCategoryUI[] BuildingCategoryUI = (BuildingCategoryUI[])Enum.GetValues(typeof(BuildingCategoryUI));

	public static readonly BuildingSubCategoryUI[] BuildingSubCategoryUI = (BuildingSubCategoryUI[])Enum.GetValues(typeof(BuildingSubCategoryUI));

	public static readonly WoundSeverity[] WoundSeverities = (WoundSeverity[])Enum.GetValues(typeof(WoundSeverity));

	public static readonly UnitCombatModeType[] UnitCombatModeTypes = (UnitCombatModeType[])Enum.GetValues(typeof(UnitCombatModeType));

	public static readonly AttributeGroup[] AttributeGroups = (AttributeGroup[])Enum.GetValues(typeof(AttributeGroup));

	public static readonly ProductionMode[] ProductionModes = (ProductionMode[])Enum.GetValues(typeof(ProductionMode));

	public static readonly SkillGrade[] SkillGrades = (SkillGrade[])Enum.GetValues(typeof(SkillGrade));

	public static readonly ResourceCategory[] AllResourceCategories = (ResourceCategory[])Enum.GetValues(typeof(ResourceCategory));

	public static readonly OrderDeconstructType[] OrderDeconstructTypes = (OrderDeconstructType[])Enum.GetValues(typeof(OrderDeconstructType));

	public static readonly OrderAllowType[] OrderAllowTypes = (OrderAllowType[])Enum.GetValues(typeof(OrderAllowType));

	public static readonly OrderLayerSelectionType[] OrderLayerSelectionTypes = (OrderLayerSelectionType[])Enum.GetValues(typeof(OrderLayerSelectionType));

	public static readonly PathType[] PathTypes = (PathType[])Enum.GetValues(typeof(PathType));

	public static readonly GridDataType[] GridDataTypes = (GridDataType[])Enum.GetValues(typeof(GridDataType));

	public static readonly ProductQuality[] ProductionQualities = (ProductQuality[])Enum.GetValues(typeof(ProductQuality));

	public static readonly StatisticGraphCategory[] StatisticGraphCategories = (StatisticGraphCategory[])Enum.GetValues(typeof(StatisticGraphCategory));

	public static readonly StatisticGraphType[] StatisticGraphTypes = (StatisticGraphType[])Enum.GetValues(typeof(StatisticGraphType));

	public static readonly ThermalModelIntensity[] ThermalModelIntensities = (ThermalModelIntensity[])Enum.GetValues(typeof(ThermalModelIntensity));

	public static readonly EventAttendeeType[] EventAttendeeTypes = (EventAttendeeType[])Enum.GetValues(typeof(EventAttendeeType));

	public static readonly DamageType[] DamageTypesExcludingNone = ((DamageType[])Enum.GetValues(typeof(DamageType))).Where((DamageType val) => val != DamageType.None).ToArray();

	public static readonly GameSpeedIndex[] GameSpeedIndex = (GameSpeedIndex[])Enum.GetValues(typeof(GameSpeedIndex));

	public static readonly GameEventOptionEffect[] GameEventOptionEffects = (GameEventOptionEffect[])Enum.GetValues(typeof(GameEventOptionEffect));

	public static readonly SpawnPointType[] SpawnPointTypes = (SpawnPointType[])Enum.GetValues(typeof(SpawnPointType));

	public static readonly WaterDepthLevel[] WaterDepthLevel = (WaterDepthLevel[])Enum.GetValues(typeof(WaterDepthLevel));

	public static readonly PlantLifePhaseType[] PlantLifePhaseTypes = (PlantLifePhaseType[])Enum.GetValues(typeof(PlantLifePhaseType));

	public static readonly WorldDirection[] WorldDirections = (WorldDirection[])Enum.GetValues(typeof(WorldDirection));

	public static readonly ObjectiveTaskRequirementType[] ObjectiveTaskRequirementTypes = (ObjectiveTaskRequirementType[])Enum.GetValues(typeof(ObjectiveTaskRequirementType));
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
