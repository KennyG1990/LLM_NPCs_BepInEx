using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using NSEipix.Base;
using NSEipix.ObjectMapper;
using Utils;

namespace NSEipix.Repository;

public abstract class Repository<T, TM> where T : Repository<T, TM> where TM : NSEipix.Base.Model
{
	private static T instance;

	private static readonly object Padlock = new object();

	[NonSerialized]
	protected readonly Dictionary<string, TM> dictionary = new Dictionary<string, TM>();

	private Dictionary<string, string> formerIdsMap;

	protected List<TM> repository = new List<TM>();

	public static T Instance
	{
		get
		{
			lock (Padlock)
			{
				if (instance == null)
				{
					throw new Exception($"{new StackFrame().GetMethod().DeclaringType} Repository not instantiated.");
				}
				return instance;
			}
		}
	}

	public string Name => "Instance";

	protected IEnumerable<TM> AllItems => repository;

	private static void OnDomainReload()
	{
		instance = null;
	}

	protected Repository()
	{
		lock (Padlock)
		{
			if (instance != null)
			{
				bool isEnabled;
				FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(28, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Externals\\EipixSDK\\Scripts\\Repository\\Repository.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Repository ");
					messageBuilder.AppendFormatted(GetType().Name);
					messageBuilder.AppendLiteral(" instance exists!");
				}
				Log.Error(messageBuilder);
				return;
			}
			instance = (T)this;
		}
		bool isEnabled2;
		FVLogTraceInterpolationHandler messageBuilder2 = new FVLogTraceInterpolationHandler(20, 1, out isEnabled2, "D:\\Git\\GoingMedieval\\Assets\\Externals\\EipixSDK\\Scripts\\Repository\\Repository.cs");
		if (isEnabled2)
		{
			messageBuilder2.AppendLiteral("Creating Repository ");
			messageBuilder2.AppendFormatted(GetType().Name);
		}
		Log.Trace(messageBuilder2);
		DomainReloadHelper.OnDomainReloadEvent = (Action)Delegate.Combine(DomainReloadHelper.OnDomainReloadEvent, new Action(OnDomainReload));
		Initialize();
	}

	public static bool IsInstantiated()
	{
		return instance != null;
	}

	public virtual IEnumerable<TM> GetAllItems()
	{
		return repository;
	}

	public TM GetFirst()
	{
		if (repository != null && repository.Count > 0)
		{
			return repository[0];
		}
		return null;
	}

	public TM GetFirst(Func<TM, bool> predicate)
	{
		foreach (TM item in repository)
		{
			if (predicate(item))
			{
				return item;
			}
		}
		return null;
	}

	public TM GetLast()
	{
		if (repository.Count == 0)
		{
			return null;
		}
		return repository[repository.Count - 1];
	}

	public TM GetAt(int index)
	{
		if (index >= repository.Count)
		{
			return null;
		}
		return repository[index];
	}

	public virtual bool TryGetValue(string id, out TM model)
	{
		if (dictionary.TryGetValue(id, out model))
		{
			return true;
		}
		if (formerIdsMap != null && formerIdsMap.TryGetValue(id, out var value))
		{
			return dictionary.TryGetValue(value, out model);
		}
		return false;
	}

	public virtual TM GetByID(string id)
	{
		if (dictionary.TryGetValue(id, out var value))
		{
			return value;
		}
		if (formerIdsMap != null && formerIdsMap.TryGetValue(id, out var value2))
		{
			return dictionary[value2];
		}
		return null;
	}

	public virtual TM GetByIdOrDefault(string id)
	{
		TM val = GetByID(id);
		if (val == null)
		{
			val = AllItems.FirstOrDefault();
		}
		return val;
	}

	public int GetCount()
	{
		return repository.Count;
	}

	public bool RemoveByID(string id)
	{
		return Remove(GetByID(id));
	}

	private bool Exists(TM model)
	{
		if (model == null)
		{
			return false;
		}
		return dictionary.ContainsKey(model.GetID());
	}

	protected bool ContainsKey(string id)
	{
		if (dictionary.ContainsKey(id))
		{
			return true;
		}
		if (formerIdsMap != null && formerIdsMap.TryGetValue(id, out var value))
		{
			return dictionary.ContainsKey(value);
		}
		return false;
	}

	protected bool Remove(TM model)
	{
		if (model != null && dictionary.ContainsKey(model.GetID()))
		{
			dictionary.Remove(model.GetID());
		}
		return repository.Remove(model);
	}

	protected bool Add(TM model)
	{
		if (model != null && dictionary.TryAdd(model.GetID(), model))
		{
			repository.Add(model);
			return true;
		}
		return false;
	}

	protected void RemoveAll()
	{
		dictionary.Clear();
		repository.Clear();
	}

	public virtual void Reload()
	{
	}

	public virtual void Deserialize()
	{
		RepositoryDto<TM> repositoryDto = Serializer().Deserialize();
		if (repositoryDto != null)
		{
			bool flag = typeof(TM).IsSubclassOf(typeof(IndexedModel));
			int num = 0;
			foreach (TM item in repositoryDto.Repository)
			{
				if (flag)
				{
					(item as IndexedModel).SetModelIndex(num++);
				}
				repository.Add(item);
			}
		}
		repository.RemoveAll((TM m) => m.HideInGame);
		formerIdsMap = new Dictionary<string, string>();
		foreach (TM item2 in repository)
		{
			bool isEnabled;
			if (item2.GetID() == null)
			{
				FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(59, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Externals\\EipixSDK\\Scripts\\Repository\\Repository.cs");
				if (isEnabled)
				{
					messageBuilder.AppendFormatted(GetType().Name);
					messageBuilder.AppendLiteral(" item model.GetID() in ");
					messageBuilder.AppendFormatted(item2.GetType().FullName);
					messageBuilder.AppendLiteral(" is null, this item will be skipped!");
				}
				Log.Error(messageBuilder);
				continue;
			}
			if (!dictionary.ContainsKey(item2.GetID()))
			{
				dictionary.Add(item2.GetID(), item2);
			}
			if (item2.FormerIDs == null || item2.FormerIDs.Length == 0)
			{
				continue;
			}
			string[] formerIDs = item2.FormerIDs;
			foreach (string key in formerIDs)
			{
				if (!formerIdsMap.TryAdd(key, item2.GetID()))
				{
					FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(39, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Externals\\EipixSDK\\Scripts\\Repository\\Repository.cs");
					if (isEnabled)
					{
						messageBuilder.AppendLiteral("Duplicate former ID detected for model ");
						messageBuilder.AppendFormatted(item2.GetID());
					}
					Log.Error(messageBuilder);
				}
			}
		}
	}

	protected virtual ISerializer<RepositoryDto<TM>> Serializer()
	{
		return NullSerializer<RepositoryDto<TM>>.Instance;
	}

	protected virtual void Initialize()
	{
	}

	protected virtual void Serialize()
	{
		RepositoryDto<TM> obj = new RepositoryDto<TM>(repository.ToList());
		Serializer().Serialize(obj);
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
