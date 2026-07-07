using System.Text;
using FoxyVoxel.Logging;
using NSEipix;
using NSEipix.Base;
using NSMedieval.Controllers;
using NSMedieval.Manager;
using NSMedieval.Utils.Pool;
using NSMedieval.Village.Map;
using NSMedieval.Village.Map.Pathfinding;

namespace NSMedieval.Village;

public class VillageManager : MonoSingleton<VillageManager>
{
	public static VillageInstance ActiveVillage => GlobalSaveController.CurrentVillageData?.PlayerVillage;

	protected override void Awake()
	{
		base.Awake();
		MonoSingleton<LoadingController>.Instance.MainSceneLoadedEvent += OnMainSceneLoaded;
		MonoSingleton<SceneController>.Instance.SceneSetup += OnSceneSetup;
	}

	private void OnSceneSetup()
	{
		VillageMap map = ActiveVillage.Map;
		if (map != null && !map.HasDisposed)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("VillageMap stats:");
			stringBuilder.AppendLine($"Building count: {map.BuildingsManagerMain.UniqueIdBuildingDictionary.Count}");
			stringBuilder.AppendLine($"Resource pile count (including shelves): {MonoSingleton<ResourcePileManager>.Instance.GetPilesCount()}");
			stringBuilder.AppendLine($"Animal count: {MonoSingleton<AnimalManager>.Instance.Animals.Count}");
			stringBuilder.AppendLine($"Worker count: {WorkerManager.WorkersEverywhere.Count()}");
			stringBuilder.AppendLine($"Plant count: {MonoSingleton<PlantResourceManager>.Instance.GetTotalPlantCount()}");
			Log.Info(stringBuilder.ToString(), "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Village\\VillageManager.cs");
		}
	}

	private void OnMainSceneLoaded()
	{
		MonoSingleton<LoadingController>.Instance.MainSceneLoadedEvent -= OnMainSceneLoaded;
		PathPool.Initialize();
		ListPool<MapNode>.Initialize(128);
	}

	protected override void OnDestroy()
	{
		if (MonoSingleton<LoadingController>.IsInstantiated())
		{
			MonoSingleton<LoadingController>.Instance.MainSceneLoadedEvent -= OnMainSceneLoaded;
		}
		if (MonoSingleton<SceneController>.IsInstantiated())
		{
			MonoSingleton<SceneController>.Instance.SceneSetup -= OnSceneSetup;
		}
		base.OnDestroy();
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
