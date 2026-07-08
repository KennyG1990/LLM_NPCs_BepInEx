using System;
using System.Collections.Generic;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using Managers.Selection.EventData;
using NSEipix;
using NSEipix.Base;
using NSEipix.Repository;
using NSMedieval.BuildingComponents;
using NSMedieval.Construction;
using NSMedieval.Goap;
using NSMedieval.Managers.Selection;
using NSMedieval.Map;
using NSMedieval.Model;
using NSMedieval.Model.MapNew;
using NSMedieval.Repository;
using NSMedieval.Resources;
using NSMedieval.Scripts.Pooler;
using NSMedieval.State;
using NSMedieval.Terrain;
using NSMedieval.Types;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Views.Resources;
using NSMedieval.Village;
using NSMedieval.Village.Map;
using UnityEngine;

namespace NSMedieval.Manager;

public class DigMarkerResourceManager : MapResourceManager<DigMarkerResourceManager, DigMarkerResourceInstance, DigMarkerResourceView>
{
	private VillageMap cachedMap;

	private ConstructionJobManager constructionJobManagerCached;

	private VillageMap Map
	{
		get
		{
			if (cachedMap == null)
			{
				cachedMap = VillageManager.ActiveVillage.Map;
			}
			return cachedMap;
		}
	}

	private ConstructionJobManager ConstructionJobManager
	{
		get
		{
			if (constructionJobManagerCached == null)
			{
				constructionJobManagerCached = Map.BuildingsManagerMain.ConstructionJobManager;
			}
			return constructionJobManagerCached;
		}
	}

	public DigMarkerResourceInstance GetDigMarker(in Vec3Int gridPosition)
	{
		return base.PositionInstanceDictionary.GetValueOrDefault(gridPosition);
	}

	public bool DigMarkerExists(in Vec3Int gridPosition)
	{
		return base.PositionInstanceDictionary.ContainsKey(gridPosition);
	}

	public bool HasDigMarkers()
	{
		return base.PositionInstanceDictionary.Count > 0;
	}

	public DigMarkerResourceInstance CreateEnemyDigMarker(string modelId, string prefabId, Vector3 position)
	{
		DigMarkerResourceInstance digMarkerResourceInstance = new DigMarkerResourceInstance(Repository<DigMarkerResourceRepository, DigMarkerResource>.Instance.GetByID(modelId), prefabId, position, FactionOwnership.Enemy);
		InstantiateResource(digMarkerResourceInstance);
		return digMarkerResourceInstance;
	}

	public void LoadSavedDigMarkers()
	{
		Log.Info("LoadSavedDigMarkers", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\DigMarkerResourceManager.cs");
		VillageInstance activeVillage = VillageManager.ActiveVillage;
		bool flag = VillageManager.ActiveVillage.Map.RaidManager.RaidInProgress();
		WorldObject[] array = activeVillage.WorldObjectStorage.WorldObjects.ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			if (!(array[i] is DigMarkerResourceInstance digMarkerResourceInstance))
			{
				continue;
			}
			if (!digMarkerResourceInstance.OwnedByPlayer() && !flag)
			{
				activeVillage.WorldObjectStorage.WorldObjects.Remove(digMarkerResourceInstance);
				activeVillage.WorldObjectStorage.WorldObjects.Remove(digMarkerResourceInstance);
				activeVillage.Map.RemoveFromWorld(digMarkerResourceInstance);
				continue;
			}
			bool flag2 = false;
			foreach (MapNode item in digMarkerResourceInstance.Nodes())
			{
				foreach (WorldObject worldObject in item.WorldObjects)
				{
					if ((worldObject.GridDataType & (GridDataType.DigMarkerResource | GridDataType.DigMarkerResourceToMine)) != 0 && worldObject is DigMarkerResourceInstance digMarkerResourceInstance2 && digMarkerResourceInstance2 != digMarkerResourceInstance)
					{
						flag2 = true;
						break;
					}
				}
				if (flag2)
				{
					break;
				}
			}
			if (!flag2)
			{
				MapNode mapNode = digMarkerResourceInstance.GetNode()?.GetNodeBelow();
				if (mapNode != null && (mapNode.DataType & GridDataType.Slope) == 0 && mapNode.VoxelTypeIdByte == 0)
				{
					flag2 = true;
				}
			}
			if (flag2)
			{
				bool isEnabled;
				FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(45, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\Managers\\DigMarkerResourceManager.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Detected empty dig marker at ");
					messageBuilder.AppendFormatted(digMarkerResourceInstance.GetGridPosition());
					messageBuilder.AppendLiteral(". Deleting it...");
				}
				Log.Info(messageBuilder);
				activeVillage.WorldObjectStorage.WorldObjects.Remove(digMarkerResourceInstance);
				activeVillage.Map.RemoveFromWorld(digMarkerResourceInstance);
			}
			else
			{
				digMarkerResourceInstance.ReInstantiate();
				InstantiateResource(digMarkerResourceInstance);
			}
		}
	}

	protected override DigMarkerResourceInstance CreateInstance(string modelId, string prefabId, Vector3 position)
	{
		DigMarkerResourceInstance digMarkerResourceInstance = new DigMarkerResourceInstance(Repository<DigMarkerResourceRepository, DigMarkerResource>.Instance.GetByID(modelId), prefabId, position);
		ConstructionJobManager.CreateDigJobs(digMarkerResourceInstance);
		return digMarkerResourceInstance;
	}

	protected override GridDataType GetGridTypeData()
	{
		return GridDataType.DigMarkerResource;
	}

	protected override void OnResourceDestroyed(DigMarkerResourceInstance digMarker)
	{
		ConstructionJobManager.RemoveDigJobs(digMarker);
		if (!digMarker.HasDisposed)
		{
			digMarker.Dispose();
		}
	}

	protected override void ResourceInstantiated(DigMarkerResourceInstance resourceInstance)
	{
	}

	protected override void ResourceInstantiated(DigMarkerResourceInstance resourceInstance, DigMarkerResourceView resourceView)
	{
		resourceView.OnInstantiated();
	}

	private void Start()
	{
		MonoSingleton<ResourceCommonController>.Instance.CreateResourceEvent += base.OnCreateResource;
		MonoSingleton<ResourceCommonController>.Instance.CreateResourceListEvent += base.OnCreateResourceList;
		MonoSingleton<ResourceCommonController>.Instance.DestroyResourceEvent += base.OnDestroyResource;
		MonoSingleton<ResourceCommonController>.Instance.ReinstanceResourceEvent += base.OnReinstanceResource;
		MonoSingleton<GroundController>.Instance.OnPlayStabilityZeroGroundParticlesEvent += OnPlayStabilityZeroGroundParticles;
		MonoSingleton<GroundController>.Instance.OnPlayDugHoleGroundParticles += OnPlayDugHoleGroundParticles;
		MonoSingleton<GroundController>.Instance.OnGroundDestroyedSingleEvent += OnGroundDestroyedSingle;
		MonoSingleton<GroundController>.Instance.OnGroundDestroyedEvent += OnGroundDestroyed;
		MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent += new Action<BaseBuildingInstance>(OnDestroyBuilding);
		MonoSingleton<ConstructionController>.Instance.ConstructionCompletedEvent += OnConstructionCompleted;
		MonoSingleton<RaidController>.Instance.OnWorkersWonRaidEvent += OnWorkersWonRaid;
		MonoSingleton<RaidController>.Instance.OnWorkersLostRaidEvent += OnWorkersLostRaid;
		MonoSingleton<RaidController>.Instance.RaidTieEvent += OnRaidTie;
		MonoSingleton<World>.Instance.MapLoadedEvent += OnMapLoaded;
		MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent += new Action<BaseBuildingInstance>(OnBuildingDestroyed);
	}

	protected override void OnDestroy()
	{
		if (MonoSingleton<ConstructionController>.IsInstantiated())
		{
			MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent -= new Action<BaseBuildingInstance>(OnDestroyBuilding);
			MonoSingleton<ConstructionController>.Instance.ConstructionCompletedEvent -= OnConstructionCompleted;
		}
		if (MonoSingleton<ResourceCommonController>.IsInstantiated())
		{
			MonoSingleton<ResourceCommonController>.Instance.CreateResourceEvent -= base.OnCreateResource;
			MonoSingleton<ResourceCommonController>.Instance.CreateResourceListEvent -= base.OnCreateResourceList;
			MonoSingleton<ResourceCommonController>.Instance.DestroyResourceEvent -= base.OnDestroyResource;
			MonoSingleton<ResourceCommonController>.Instance.ReinstanceResourceEvent -= base.OnReinstanceResource;
		}
		if (MonoSingleton<GroundController>.IsInstantiated())
		{
			MonoSingleton<GroundController>.Instance.OnPlayStabilityZeroGroundParticlesEvent -= OnPlayStabilityZeroGroundParticles;
			MonoSingleton<GroundController>.Instance.OnPlayDugHoleGroundParticles -= OnPlayDugHoleGroundParticles;
			MonoSingleton<GroundController>.Instance.OnGroundDestroyedSingleEvent -= OnGroundDestroyedSingle;
			MonoSingleton<GroundController>.Instance.OnGroundDestroyedEvent -= OnGroundDestroyed;
		}
		if (MonoSingleton<RaidController>.IsInstantiated())
		{
			MonoSingleton<RaidController>.Instance.OnWorkersWonRaidEvent -= OnWorkersWonRaid;
			MonoSingleton<RaidController>.Instance.OnWorkersLostRaidEvent -= OnWorkersLostRaid;
			MonoSingleton<RaidController>.Instance.RaidTieEvent -= OnRaidTie;
		}
		if (MonoSingleton<World>.IsInstantiated())
		{
			MonoSingleton<World>.Instance.MapLoadedEvent -= OnMapLoaded;
		}
		if (MonoSingleton<ConstructionController>.IsInstantiated())
		{
			MonoSingleton<ConstructionController>.Instance.DestroyBuildingEvent -= new Action<BaseBuildingInstance>(OnBuildingDestroyed);
		}
		base.OnDestroy();
	}

	private void OnConstructionCompleted(BaseBuildingInstance buildable)
	{
		HashSet<Vec3Int> surroundingPositions = Singleton<GridTools>.Instance.GetSurroundingPositions(buildable.GridDataPosition, buildable.Size, buildable.Angle);
		foreach (Vec3Int item in surroundingPositions)
		{
			base.PositionInstanceDictionary.TryGetValue(item, out var value);
			value?.ReCalculateReachability();
			base.PositionInstanceDictionary.TryGetValue(new Vec3Int(item.x, item.y - 1, item.z), out var value2);
			value2?.ReCalculateReachability();
			base.PositionInstanceDictionary.TryGetValue(new Vec3Int(item.x, item.y + 1, item.z), out var value3);
			value3?.ReCalculateReachability();
		}
		HashSetPool<Vec3Int>.Return(surroundingPositions);
	}

	private void OnPlayStabilityZeroGroundParticles(Vec3Int gridPosition)
	{
		if (!MonoSingleton<World>.IsInstantiated() || !MonoSingleton<ParticleSystemPool>.IsInstantiated())
		{
			return;
		}
		VoxelType voxelType = VillageManager.ActiveVillage.Map.GetNode(in gridPosition)?.VoxelType;
		if (voxelType == null || !voxelType.IsDiggable)
		{
			return;
		}
		MapPropType byID = Repository<MapPropTypeRepository, MapPropType>.Instance.GetByID(voxelType.DigMarker);
		if (byID == null)
		{
			return;
		}
		string model = byID.Model;
		DigMarkerResource byID2 = Repository<DigMarkerResourceRepository, DigMarkerResource>.Instance.GetByID(model);
		if (byID2 == null)
		{
			return;
		}
		GameObject gameObject = MonoSingleton<ParticleSystemPool>.Instance.PlayParticles(byID2.DestroyParticles, GridUtils.GetWorldPosition(gridPosition));
		if (gameObject != null)
		{
			base.PositionInstanceDictionary.TryGetValue(gridPosition, out var value);
			if (value != null)
			{
				DigMarkerResourceView digMarkerResourceView = base.ResourcesInstanceViewDictionary[value];
				MonoSingleton<ParticleSystemPool>.Instance.SetEmitterSize(byID2.DestroyParticles, gameObject, digMarkerResourceView.GetCollider, 0.8f);
			}
		}
	}

	private void OnPlayDugHoleGroundParticles(DigMarkerResourceInstance digMarkerResourceInstance)
	{
		if (digMarkerResourceInstance == null || !MonoSingleton<World>.IsInstantiated() || !MonoSingleton<ParticleSystemPool>.IsInstantiated())
		{
			return;
		}
		DigMarkerResource blueprint = digMarkerResourceInstance.Blueprint;
		if (blueprint == null)
		{
			return;
		}
		Vector3 worldPosition = digMarkerResourceInstance.WorldPosition;
		worldPosition.y -= World.MapBlockHeight;
		GameObject gameObject = MonoSingleton<ParticleSystemPool>.Instance.PlayParticles(blueprint.DestroyParticles, worldPosition);
		if (gameObject != null)
		{
			DigMarkerResourceView digMarkerResourceView = base.ResourcesInstanceViewDictionary[digMarkerResourceInstance];
			if (digMarkerResourceView.GetCollider != null)
			{
				MonoSingleton<ParticleSystemPool>.Instance.SetEmitterSize(blueprint.DestroyParticles, gameObject, digMarkerResourceView.GetCollider, 0.8f);
			}
		}
	}

	public void OnGroundDestroyed(List<Vec3Int> positions)
	{
		using PooledList<DigMarkerResourceInstance> pooledList = ListPool<DigMarkerResourceInstance>.GetJanitor(base.ResourcesInstanceViewDictionary.Keys);
		foreach (DigMarkerResourceInstance item in pooledList)
		{
			Vec3Int a = item.GridDataPosition;
			Vec3Int b = Vec3Int.down;
			if (positions.Contains(a + b))
			{
				MonoSingleton<ResourceCommonController>.Instance.DestroyResource(item);
			}
		}
	}

	public void OnGroundDestroyedSingle(Vec3Int position)
	{
		Vec3Int b = Vec3Int.up;
		Vec3Int key = position + b;
		if (base.PositionInstanceDictionary.TryGetValue(key, out var value))
		{
			MonoSingleton<ResourceCommonController>.Instance.DestroyResource(value);
		}
		RecalculateReachability(position);
		using PooledList<DigMarkerResourceInstance> pooledList = ListPool<DigMarkerResourceInstance>.GetJanitor(base.ResourcesInstanceViewDictionary.Keys);
		foreach (DigMarkerResourceInstance item in pooledList)
		{
			b = item.GridDataPosition;
			Vec3Int b2 = Vec3Int.up;
			if (b.Equals(position + b2))
			{
				MonoSingleton<ResourceCommonController>.Instance.DestroyResource(item);
			}
		}
	}

	private void OnDestroyBuilding(BaseBuildingInstance baseBuildingInstance)
	{
		Vec3Int gridDataPosition = baseBuildingInstance.GridDataPosition;
		using PooledList<DigMarkerResourceInstance> pooledList = ListPool<DigMarkerResourceInstance>.GetJanitor(base.ResourcesInstanceViewDictionary.Keys);
		foreach (DigMarkerResourceInstance item in pooledList)
		{
			if (item.GridDataPosition.Equals(gridDataPosition))
			{
				item.ReCalculateReachability();
			}
		}
	}

	private void OnWorkersWonRaid()
	{
		CancelEnemyDigMarkers();
	}

	private void OnWorkersLostRaid()
	{
		CancelEnemyDigMarkers();
	}

	private void OnRaidTie()
	{
		CancelEnemyDigMarkers();
	}

	private void CancelEnemyDigMarkers()
	{
		using PooledList<DigMarkerResourceInstance> pooledList = ListPool<DigMarkerResourceInstance>.GetJanitor(base.ResourcesInstanceViewDictionary.Keys);
		foreach (DigMarkerResourceInstance item in pooledList)
		{
			if (!item.OwnedByPlayer())
			{
				item.Cancel();
			}
		}
	}

	private void OnMapLoaded(bool fromSave)
	{
		foreach (DigMarkerResourceInstance key in base.ResourcesInstanceViewDictionary.Keys)
		{
			key.ReCalculateReachability();
			Map.BuildingsManagerMain.ConstructionJobManager.CreateDigJobs(key);
		}
	}

	private void OnBuildingDestroyed(BaseBuildingInstance building)
	{
		RecalculateReachability(building.GridDataPosition);
	}

	private void RecalculateReachability(Vec3Int source)
	{
		using PooledList<Vec3Int> pooledList = source.GetPositionsInRange(new Vec3Int(1, 1, 1));
		foreach (Vec3Int item in pooledList)
		{
			if (base.PositionInstanceDictionary.TryGetValue(item, out var value) && value.Blueprint.UpdateReachablePositionAfterDig)
			{
				value.ReCalculateReachability();
			}
		}
	}

	protected override void OnOrderResourceCollectionEvent(OrderEventData eventData)
	{
		base.OnOrderResourceCollectionEvent(eventData);
		if (!MonoSingleton<SelectionManager>.IsInstantiated() || !eventData.OrderType.Equals(OrderType.Cancel))
		{
			return;
		}
		using PooledList<DigMarkerResourceInstance> pooledList = ListPool<DigMarkerResourceInstance>.GetJanitor(base.InstanceView.Keys);
		foreach (DigMarkerResourceInstance item in pooledList)
		{
			if (!item.HasDisposed)
			{
				SelectionManager selectionManager = MonoSingleton<SelectionManager>.Instance;
				Vec3Int a = item.GridDataPosition;
				Vec3Int b = Vec3Int.up;
				if (selectionManager.IsPositionInSelectedSlopes(a - b))
				{
					item.SetCurrentOrder(OrderType.None);
				}
			}
		}
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
