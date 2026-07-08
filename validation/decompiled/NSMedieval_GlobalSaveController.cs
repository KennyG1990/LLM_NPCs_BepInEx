using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FoxyVoxel.Logging;
using FoxyVoxel.Logging.Core.LogMessageInterpolationHandlers;
using Ionic.Zip;
using NSEipix;
using NSEipix.Base;
using NSEipix.ObjectMapper;
using NSEipix.Repository;
using NSMedieval.Controllers;
using NSMedieval.Enums;
using NSMedieval.Manager;
using NSMedieval.Map;
using NSMedieval.Model.SecondMap;
using NSMedieval.Serialization;
using NSMedieval.State;
using NSMedieval.Tools;
using NSMedieval.UI.Utils;
using NSMedieval.Utils.Pool;
using NSMedieval.Utils.Pool.Janitors;
using NSMedieval.Village;
using NSMedieval.WorldMap;
using Tools;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NSMedieval;

public class GlobalSaveController : MonoSingleton<GlobalSaveController>, IObserver, ISerializable
{
	public delegate void GlobalSaveUpdate();

	public delegate void SaveFailedToLoad();

	public delegate void SaveLoaded(VillageSaveData data);

	[Serializable]
	private class VillageSavesHolder
	{
		[SerializeField]
		private List<VillageSaveInfo> villageSaveInfo;

		public List<VillageSaveInfo> VillageSaveInfo => villageSaveInfo;

		public void InitEmpty()
		{
			villageSaveInfo = new List<VillageSaveInfo>();
		}

		public bool ContainsVillage(string folderName, string fileName)
		{
			foreach (VillageSaveInfo item in villageSaveInfo)
			{
				if (string.Equals(folderName, item.FolderName, StringComparison.OrdinalIgnoreCase) && string.Equals(fileName, item.FileName, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}
	}

	[Serializable]
	private class Data
	{
		[SerializeField]
		private GlobalSettings globalSettings;

		public GlobalSettings GlobalSettings => globalSettings;

		public void InitEmpty()
		{
			globalSettings = new GlobalSettings(this);
		}
	}

	[FVSerializableKey("UserData", "")]
	public class UserData : IFVSerializable
	{
		private const int PurchaseVersionThreshold = 1000000;

		private int purchaseVersion;

		private bool isEarlyBird;

		private HashSet<string> lockedBuildingsHash;

		public int PurchaseVersion => purchaseVersion;

		public bool IsEarlyBird => isEarlyBird;

		public bool HavePurchaseVersion => purchaseVersion != 0;

		public HashSet<string> LockedBuildingsHash => lockedBuildingsHash;

		public UserData()
		{
			purchaseVersion = ApplicationVersionUtils.GetVersionValue(Application.version);
			if (purchaseVersion < 1000000)
			{
				isEarlyBird = true;
			}
			lockedBuildingsHash = new HashSet<string>(LockedBuildingsManager.DefaultLockedBuildings);
		}

		public void SetIsEarlyBird(bool isEarlyBird)
		{
			this.isEarlyBird = isEarlyBird;
		}

		public void Serialize(FVSerializer serializer)
		{
			serializer.Write("purchaseVersion", purchaseVersion);
			serializer.Write("isEarlyBird", isEarlyBird);
			serializer.Write("lockedBuildingsHash", lockedBuildingsHash);
		}

		public UserData(FVDeserializer deserializer)
		{
			purchaseVersion = deserializer.ReadInt("purchaseVersion");
			isEarlyBird = deserializer.ReadBool("isEarlyBird");
			lockedBuildingsHash = deserializer.ReadStringHashSet("lockedBuildingsHash") ?? new HashSet<string>(LockedBuildingsManager.DefaultLockedBuildings);
			CheckEarlyAccessRewards();
		}

		private void CheckEarlyAccessRewards()
		{
			if (isEarlyBird)
			{
				string[] earlyAccessRewards = LockedBuildingsManager.EarlyAccessRewards;
				foreach (string item in earlyAccessRewards)
				{
					lockedBuildingsHash.Remove(item);
				}
			}
		}
	}

	public static VillageSaveData CurrentVillageData;

	public const string VillageSavesPath = "VillageSaves";

	public const string TutorialPersistantSavesPath = "TutorialSaves";

	public const string TutorialStreamingSavesPath = "Tutorial/Saves";

	public const string SecondMapSavesPath = "SecondMap/Saves";

	public const string GlobalSaveFilename = "global.config";

	public const string UserDataFilename = "user.bin";

	private const string StructurePresetsFilename = "structure_presets.json";

	private const string VillageSavesListFilename = "VillageSaves/VillageSavesList.sav";

	private JsonSerializer<Data> serializer;

	private readonly JsonSerializer<VillageSavesHolder> villageSavesSerializer = new JsonSerializer<VillageSavesHolder>.Builder("VillageSaves/VillageSavesList.sav").Build();

	private Data data;

	private UserData userData;

	public static VillageSaveData OriginalVillageData;

	private VillageSaveData tempVillageData;

	private VillageSaveData currentVillageData;

	private VillageSavesHolder villageSavesHolder;

	private bool isAutosaveEnabled;

	private VillageSaveInfo saveInfoToLoad;

	private SecondMapSaveInfo secondSaveInfoToLoad;

	private bool autosaveStartEventFired;

	private bool isSecondMapTransition;

	private bool forceOriginalVillage;

	private string saveErrorMessage = string.Empty;

	public string CurrentSaveVersion { get; private set; }

	public bool IsSecondMapTransition => isSecondMapTransition;

	public bool IsLoadingSecondMap => secondSaveInfoToLoad != null;

	public bool ForceOriginalVillage => forceOriginalVillage;

	public HashSet<string> CorruptedBlueprintIds { get; set; }

	public List<ResourceInstance> CorruptedCarcassEquipment { get; private set; }

	public HashSet<string> ReplacedBlueprintIds { get; set; }

	public List<VillageSaveInfo> SavesList
	{
		get
		{
			if (villageSavesHolder == null)
			{
				bool flag = false;
				try
				{
					villageSavesHolder = villageSavesSerializer.Deserialize();
				}
				catch
				{
					flag = true;
				}
				if (flag || villageSavesHolder == null)
				{
					villageSavesHolder = new VillageSavesHolder();
					villageSavesHolder.InitEmpty();
				}
				TryImportSaves();
			}
			return villageSavesHolder.VillageSaveInfo;
		}
	}

	private Data SaveData
	{
		get
		{
			if (data == null)
			{
				data = new Data();
				data.InitEmpty();
			}
			return data;
		}
		set
		{
			data = value;
		}
	}

	private JsonSerializer<Data> Serializer
	{
		get
		{
			return serializer ?? (serializer = new JsonSerializer<Data>.Builder("global.config").BuildOverwrite(SaveData));
		}
		set
		{
			serializer = value;
		}
	}

	public GlobalSettings GlobalSettings
	{
		get
		{
			if (data == null)
			{
				Deserialize();
			}
			return SaveData.GlobalSettings;
		}
	}

	public UserData UserDataInfo
	{
		get
		{
			if (userData == null)
			{
				DeserializeUserData();
			}
			return userData;
		}
	}

	public event GlobalSaveUpdate OnGlobalSaveUpdate;

	public event SaveFailedToLoad OnSaveFailedToValidate;

	public event SaveLoaded OnSaveLoaded;

	[field: NonSerialized]
	public event Action AutosaveStartEvent;

	[field: NonSerialized]
	public event Action QuicksaveStartEvent;

	[field: NonSerialized]
	public event Action AutosaveEndEvent;

	[field: NonSerialized]
	public event Action<string> BuildingUnlockedEvent;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private new static void OnDomainReload()
	{
		CurrentVillageData = null;
		OriginalVillageData = null;
	}

	private void TryImportSaves()
	{
		RelocateOldSavesFromVillageSaveData();
		ImportSavFilesFromRoot();
		ImportSavFilesFromSubfolders();
		DeleteOldSaveFiles();
		DeleteBugReporterSaves();
		SetObsolete();
		Serialize();
	}

	private void SetObsolete()
	{
		foreach (VillageSaveInfo item in villageSavesHolder.VillageSaveInfo)
		{
			item.InitModifiedVersion();
		}
	}

	private void RemoveDuplicatedSaves()
	{
		if (MonoSingleton<GlobalSaveController>.IsApplicationQuitting() || villageSavesHolder?.VillageSaveInfo == null)
		{
			return;
		}
		HashSet<string> hashSet = new HashSet<string>();
		foreach (VillageSaveInfo item in villageSavesHolder.VillageSaveInfo.ToList())
		{
			if (item == null || item.FileName == null || item.FolderName == null)
			{
				continue;
			}
			string text = item.FolderName + "/" + item.FileName;
			if (hashSet.Contains(text) || !item.FileName.ToLower().EndsWith(".sav"))
			{
				bool isEnabled;
				FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(43, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Removing duplicated save ");
					messageBuilder.AppendFormatted(text);
					messageBuilder.AppendLiteral(" before serialize.");
				}
				Log.Info(messageBuilder);
				villageSavesHolder.VillageSaveInfo.Remove(item);
			}
			else
			{
				hashSet.Add(text);
			}
		}
	}

	public void DeleteBugReporterSaves()
	{
		List<VillageSaveInfo> list = new List<VillageSaveInfo>();
		foreach (VillageSaveInfo saves in SavesList)
		{
			if ((!string.IsNullOrEmpty(saves.FolderName) && saves.FolderName.Replace("\\", "/").Equals("../_bug_reporter_save")) | (string.IsNullOrEmpty(saves.FolderName) && (saves.FileName.Equals("bug_reporter_save.sav") || saves.FileName.Equals("bug_reporter_save.sav.meta"))))
			{
				list.Add(saves);
			}
		}
		foreach (VillageSaveInfo item in list)
		{
			bool isEnabled;
			FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(32, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Deleting bug reporter save: ");
				messageBuilder.AppendFormatted(item.FolderName);
				messageBuilder.AppendLiteral("/");
				messageBuilder.AppendFormatted(item.FileName);
				messageBuilder.AppendLiteral(" [");
				messageBuilder.AppendFormatted(item.VillageName);
				messageBuilder.AppendLiteral("]");
			}
			Log.Info(messageBuilder);
			DeleteSave(item, serialize: false);
		}
		if (list.Count > 0)
		{
			Serialize();
		}
	}

	private void RelocateOldSavesFromVillageSaveData()
	{
		List<VillageSaveInfo> list = new List<VillageSaveInfo>();
		foreach (VillageSaveInfo item in villageSavesHolder.VillageSaveInfo)
		{
			if (string.IsNullOrEmpty(item.FolderName))
			{
				string absoluteSaveFilename = GetAbsoluteSaveFilename(item.FileName, item.FolderName);
				if (File.Exists(absoluteSaveFilename))
				{
					string folderName = TextFormatting.RemoveInvalidCharsFromString(item.VillageName);
					item.SetFolderName(folderName);
					string absoluteSaveFilename2 = GetAbsoluteSaveFilename(item.FileName, item.FolderName);
					FilePathUtils.CheckAndCreatePath(absoluteSaveFilename2);
					bool isEnabled;
					try
					{
						File.Move(absoluteSaveFilename, absoluteSaveFilename2);
						FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(32, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
						if (isEnabled)
						{
							messageBuilder.AppendLiteral("Successfully imported old save: ");
							messageBuilder.AppendFormatted(item.FileName);
						}
						Log.Info(messageBuilder);
					}
					catch (Exception ex)
					{
						FVLogWarningInterpolationHandler messageBuilder2 = new FVLogWarningInterpolationHandler(59, 3, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
						if (isEnabled)
						{
							messageBuilder2.AppendLiteral("Cannot move file ");
							messageBuilder2.AppendFormatted(item.FileName);
							messageBuilder2.AppendLiteral(" from VillageSaves to folder ");
							messageBuilder2.AppendFormatted(item.FolderName);
							messageBuilder2.AppendLiteral(". Exception: ");
							messageBuilder2.AppendFormatted(ex.Message);
						}
						Log.Warning(messageBuilder2);
					}
				}
			}
			if (!File.Exists(GetAbsoluteSaveFilename(item.FileName, item.FolderName)))
			{
				list.Add(item);
			}
		}
		foreach (VillageSaveInfo item2 in list)
		{
			Log.Info("Removed save entry because file does not exist: " + item2.FileName, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
			villageSavesHolder.VillageSaveInfo.Remove(item2);
		}
	}

	private string GetFullFilenameForCopiedSave(string filePath)
	{
		string text = filePath;
		int num = 1;
		while (File.Exists(text))
		{
			int startIndex = filePath.LastIndexOf(".sav", StringComparison.Ordinal);
			text = filePath.Insert(startIndex, num.ToString());
			num++;
		}
		return text;
	}

	private void DeleteOldSaveFiles()
	{
		if (!data.GlobalSettings.FirstLaunch)
		{
			return;
		}
		IEnumerable<string> enumerable = from fileName in Directory.EnumerateFiles(Application.persistentDataPath)
			where fileName.ToLower().EndsWith(".sav")
			select fileName;
		FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(38, 1, out var isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
		if (isEnabled)
		{
			messageBuilder.AppendLiteral("First launch: found ");
			messageBuilder.AppendFormatted(enumerable.Count());
			messageBuilder.AppendLiteral(" deprecated saves.");
		}
		Log.Info(messageBuilder);
		if (!enumerable.Any())
		{
			return;
		}
		foreach (string item in enumerable)
		{
			try
			{
				messageBuilder = new FVLogInfoInterpolationHandler(19, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Deleting old save ");
					messageBuilder.AppendFormatted(FilePathUtils.RemoveUserFromPath(item));
					messageBuilder.AppendLiteral(".");
				}
				Log.Info(messageBuilder);
				File.Delete(item);
			}
			catch (Exception ex)
			{
				FVLogWarningInterpolationHandler messageBuilder2 = new FVLogWarningInterpolationHandler(32, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				if (isEnabled)
				{
					messageBuilder2.AppendLiteral("Cannot delete old save ");
					messageBuilder2.AppendFormatted(FilePathUtils.RemoveUserFromPath(item));
					messageBuilder2.AppendLiteral(". Error: ");
					messageBuilder2.AppendFormatted(ex.Message);
				}
				Log.Warning(messageBuilder2);
				throw;
			}
		}
	}

	private void ImportSavFilesFromRoot()
	{
		DebugTimer.StartTimer("import_sav_villagesaves");
		string text = Path.Combine(Application.persistentDataPath, "VillageSaves").Replace("\\", "/");
		FilePathUtils.CheckAndCreatePath(text);
		IEnumerable<string> enumerable = null;
		try
		{
			enumerable = Directory.EnumerateFiles(text, "*.sav");
		}
		catch (DirectoryNotFoundException ex)
		{
			Log.Error(ex.Message, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
			Log.Error("Couldn't import .sav files.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
		}
		if (enumerable != null)
		{
			int num = 0;
			foreach (string item in enumerable)
			{
				if (!item.ToLower().EndsWith(".sav"))
				{
					continue;
				}
				string text2 = item.Replace("\\", "/");
				if (text2.EndsWith("VillageSaves/VillageSavesList.sav") || !File.Exists(text2))
				{
					continue;
				}
				bool isEnabled;
				try
				{
					string text3 = VillageSaveData.ReadProfileNameFromZip(text2);
					string fileName = Path.GetFileName(text2);
					FVLogInfoInterpolationHandler messageBuilder = new FVLogInfoInterpolationHandler(22, 1, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
					if (isEnabled)
					{
						messageBuilder.AppendLiteral("Trying to import save ");
						messageBuilder.AppendFormatted(fileName);
					}
					Log.Info(messageBuilder);
					bool autosave = text2.Contains("/Autosave-");
					DateTime lastWriteTime = File.GetLastWriteTime(text2);
					VillageSaveInfo villageSaveInfo = new VillageSaveInfo(text3, fileName, text3, lastWriteTime, autosave);
					string absoluteSaveFilename = GetAbsoluteSaveFilename(villageSaveInfo.FileName, villageSaveInfo.FolderName);
					string fullFilenameForCopiedSave = GetFullFilenameForCopiedSave(absoluteSaveFilename);
					villageSaveInfo.SetFileName(Path.GetFileName(fullFilenameForCopiedSave));
					try
					{
						FilePathUtils.CheckAndCreatePath(fullFilenameForCopiedSave);
					}
					catch (IOException ex2)
					{
						FVLogWarningInterpolationHandler messageBuilder2 = new FVLogWarningInterpolationHandler(80, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
						if (isEnabled)
						{
							messageBuilder2.AppendLiteral("Cannot create folder when creating path for ");
							messageBuilder2.AppendFormatted(FilePathUtils.RemoveUserFromPath(fullFilenameForCopiedSave));
							messageBuilder2.AppendLiteral(". Ignoring save. Exception message: ");
							messageBuilder2.AppendFormatted(ex2.Message);
						}
						Log.Warning(messageBuilder2);
						goto end_IL_00c3;
					}
					try
					{
						File.Move(text2, fullFilenameForCopiedSave);
						villageSavesHolder.VillageSaveInfo.Add(villageSaveInfo);
						num++;
					}
					catch (IOException ex3)
					{
						FVLogWarningInterpolationHandler messageBuilder2 = new FVLogWarningInterpolationHandler(46, 5, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
						if (isEnabled)
						{
							messageBuilder2.AppendLiteral("Failed to import save from ");
							messageBuilder2.AppendFormatted("VillageSaves");
							messageBuilder2.AppendLiteral("/");
							messageBuilder2.AppendFormatted(fileName);
							messageBuilder2.AppendLiteral(" to ");
							messageBuilder2.AppendFormatted(villageSaveInfo.FolderName);
							messageBuilder2.AppendLiteral("/");
							messageBuilder2.AppendFormatted(villageSaveInfo.FileName);
							messageBuilder2.AppendLiteral(". Exception: ");
							messageBuilder2.AppendFormatted(ex3.Message);
						}
						Log.Warning(messageBuilder2);
					}
					catch (UnauthorizedAccessException ex4)
					{
						FVLogWarningInterpolationHandler messageBuilder2 = new FVLogWarningInterpolationHandler(46, 5, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
						if (isEnabled)
						{
							messageBuilder2.AppendLiteral("Failed to import save from ");
							messageBuilder2.AppendFormatted("VillageSaves");
							messageBuilder2.AppendLiteral("/");
							messageBuilder2.AppendFormatted(fileName);
							messageBuilder2.AppendLiteral(" to ");
							messageBuilder2.AppendFormatted(villageSaveInfo.FolderName);
							messageBuilder2.AppendLiteral("/");
							messageBuilder2.AppendFormatted(villageSaveInfo.FileName);
							messageBuilder2.AppendLiteral(". Exception: ");
							messageBuilder2.AppendFormatted(ex4.Message);
						}
						Log.Warning(messageBuilder2);
					}
					end_IL_00c3:;
				}
				catch (ZipException ex5)
				{
					FVLogWarningInterpolationHandler messageBuilder2 = new FVLogWarningInterpolationHandler(49, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
					if (isEnabled)
					{
						messageBuilder2.AppendLiteral("Cannot read zip save, can't import save ");
						messageBuilder2.AppendFormatted(FilePathUtils.RemoveUserFromPath(text2));
						messageBuilder2.AppendLiteral(". Error: ");
						messageBuilder2.AppendFormatted(ex5.Message);
					}
					Log.Warning(messageBuilder2);
				}
			}
		}
		DebugTimer.EndTimer("import_sav_villagesaves");
	}

	private void ImportSavFilesFromSubfolders()
	{
		foreach (string item2 in Directory.EnumerateDirectories(Path.Combine(Application.persistentDataPath, "VillageSaves")))
		{
			string fileName = Path.GetFileName(item2);
			foreach (string item3 in Directory.EnumerateFiles(item2))
			{
				string fileName2 = Path.GetFileName(item3);
				if (fileName2.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) && !villageSavesHolder.ContainsVillage(fileName, fileName2))
				{
					bool autosave = fileName2.StartsWith("Autosave-");
					DateTime lastWriteTime = File.GetLastWriteTime(item3);
					VillageSaveInfo item = new VillageSaveInfo(fileName, fileName2, fileName, lastWriteTime, autosave);
					villageSavesHolder.VillageSaveInfo.Add(item);
				}
			}
		}
	}

	protected override void OnApplicationQuit()
	{
		data.GlobalSettings.SetFirstLaunch(value: false);
		Serialize();
		base.OnApplicationQuit();
		data = null;
		if (CurrentVillageData != null)
		{
			CurrentVillageData?.Destroy();
		}
		if (currentVillageData != null)
		{
			currentVillageData?.Destroy();
		}
		CurrentVillageData = null;
		currentVillageData = null;
		OriginalVillageData = null;
		tempVillageData = null;
		villageSavesHolder = null;
		saveInfoToLoad = null;
		Log.Info("Quitting application...", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
	}

	public KeyCode GetKeyCode(KeyInputEvent inputEvent)
	{
		Keybinding[] keybindings = GlobalSettings.Keybindings;
		foreach (Keybinding keybinding in keybindings)
		{
			if (keybinding.KeyInputEvent == inputEvent)
			{
				if (keybinding.PrimaryKey == KeyCode.None)
				{
					return keybinding.AlternativeKey;
				}
				return keybinding.PrimaryKey;
			}
		}
		return KeyCode.None;
	}

	public static string GetAbsoluteSaveFilename(string fileName, string folderName)
	{
		return Path.Combine(Application.persistentDataPath, "VillageSaves", folderName ?? string.Empty, fileName ?? string.Empty).Replace('\\', '/');
	}

	public static string GetCurrentSaveFilePath()
	{
		return GetAbsoluteSaveFilename(CurrentVillageData.FileName, CurrentVillageData.FolderName);
	}

	public static string GetAbsoluteStreamingAssetsFilename(string fileName, string folderName)
	{
		return Path.Combine(Application.streamingAssetsPath, folderName ?? string.Empty, fileName ?? string.Empty).Replace('\\', '/');
	}

	public static string GetDefaultSaveFolder()
	{
		return Path.Combine(Application.persistentDataPath, "VillageSaves").Replace('\\', '/');
	}

	public static string GetTutorialSaveFolder()
	{
		return Path.Combine(Application.persistentDataPath, "TutorialSaves").Replace('\\', '/');
	}

	public void CreateNewVillage(string villageName)
	{
		if (SavesList.Find((VillageSaveInfo entry) => entry.VillageName.Equals(villageName)) != null)
		{
			throw new Exception("Village with name " + villageName + " already exists! TODO: Handle this properly");
		}
		int index = 1;
		string text = "Start.sav";
		string folderNameForNewVillage = GetFolderNameForNewVillage(villageName);
		currentVillageData = new VillageSaveData(text, villageName, folderNameForNewVillage);
		CurrentVillageData = currentVillageData;
		VillageSaveInfo villageSaveInfo = new VillageSaveInfo(villageName, text, folderNameForNewVillage, DateTime.Now, autosave: false, index);
		villageSaveInfo.SetModifiedVersion(Application.version, isObsolete: false);
		SavesList.Add(villageSaveInfo);
		Serialize();
		this.OnSaveLoaded?.Invoke(currentVillageData);
	}

	public VillageSaveData CreateTempVillage()
	{
		isSecondMapTransition = true;
		tempVillageData = new VillageSaveData(currentVillageData, "tempVillage", "tempVillageFolder");
		return tempVillageData;
	}

	public void SwitchToTempVillage()
	{
		isSecondMapTransition = false;
		if (tempVillageData == null)
		{
			Log.Error("TempVillage data is null!", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
			return;
		}
		currentVillageData = tempVillageData;
		CurrentVillageData = currentVillageData;
		tempVillageData = null;
	}

	private bool CheckPathContainsInvalidChars(string path)
	{
		char[] invalidPathChars = Path.GetInvalidPathChars();
		foreach (char c in invalidPathChars)
		{
			if (path.Contains(c))
			{
				bool isEnabled;
				FVLogErrorInterpolationHandler messageBuilder = new FVLogErrorInterpolationHandler(29, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("path ");
					messageBuilder.AppendFormatted(FilePathUtils.RemoveUserFromPath(path));
					messageBuilder.AppendLiteral(" contains invalid char: ");
					messageBuilder.AppendFormatted(c);
				}
				Log.Error(messageBuilder);
				return true;
			}
		}
		return false;
	}

	private string GetFolderNameForNewVillage(string villageName)
	{
		int num = -1;
		CheckPathContainsInvalidChars(FileReaders.Get.GetPersistentDataPath());
		CheckPathContainsInvalidChars("VillageSaves");
		string text = Path.Combine(FileReaders.Get.GetPersistentDataPath(), "VillageSaves");
		string text2;
		string path;
		do
		{
			text2 = ((num == -1) ? villageName : $"{villageName}-{num}");
			num++;
			CheckPathContainsInvalidChars(text);
			CheckPathContainsInvalidChars(text2);
			path = Path.Combine(text, text2);
		}
		while (File.Exists(path) || Directory.Exists(path));
		return text2;
	}

	public bool DeleteSave(VillageSaveInfo info, bool serialize = true)
	{
		if (info == null || data == null || SavesList == null)
		{
			return false;
		}
		bool num = DeleteSaveFile(info);
		if (num)
		{
			SavesList.Remove(info);
		}
		else
		{
			string arg = info.FolderName + "/" + info.FileName;
			string text = MonoSingleton<LocalizationController>.Instance.GetText("delete_file_failed");
			MonoSingleton<BlackBarMessageController>.Instance.ShowBlackBarMessage(string.Format(text, arg));
		}
		if (num && serialize)
		{
			Serialize();
		}
		return num;
	}

	public VillageSaveInfo GetLastPlayedProfile()
	{
		if (MonoSingleton<GlobalSaveController>.Instance.SavesList == null || MonoSingleton<GlobalSaveController>.Instance.SavesList.Count == 0)
		{
			return null;
		}
		VillageSaveInfo villageSaveInfo = MonoSingleton<GlobalSaveController>.Instance.SavesList[0];
		foreach (VillageSaveInfo saves in MonoSingleton<GlobalSaveController>.Instance.SavesList)
		{
			if (saves.LastPlayedUnixSeconds > villageSaveInfo.LastPlayedUnixSeconds)
			{
				villageSaveInfo = saves;
			}
		}
		return villageSaveInfo;
	}

	public void SetSaveInfoToLoad(VillageSaveInfo saveInfoToLoad)
	{
		this.saveInfoToLoad = saveInfoToLoad;
		secondSaveInfoToLoad = null;
	}

	public bool SetOriginalSaveInfoToLoad()
	{
		saveInfoToLoad = new VillageSaveInfo(currentVillageData.Name, currentVillageData.FileName, currentVillageData.FolderName, DateTime.Now);
		OriginalVillageData = new VillageSaveData(currentVillageData.CachedSaveData);
		secondSaveInfoToLoad = null;
		saveInfoToLoad.SetModifiedVersion(Application.version, isObsolete: false);
		isSecondMapTransition = true;
		forceOriginalVillage = true;
		return true;
	}

	public void SetSaveInfoToLoad(SecondMapSaveInfo saveInfo)
	{
		isSecondMapTransition = true;
		secondSaveInfoToLoad = saveInfo;
		secondSaveInfoToLoad.OriginalFolderName = CurrentVillageData.FolderName;
		secondSaveInfoToLoad.OriginalFileName = CurrentVillageData.FileName;
		OriginalVillageData = CurrentVillageData;
		OriginalVillageData.SerializeToRam();
	}

	public bool LoadSavedVillageData()
	{
		if (forceOriginalVillage)
		{
			return LoadVillageDataFromCache(saveInfoToLoad);
		}
		if (tempVillageData != null)
		{
			SwitchToTempVillage();
			this.OnSaveLoaded?.Invoke(currentVillageData);
			return true;
		}
		if (secondSaveInfoToLoad != null)
		{
			return LoadVillageData(secondSaveInfoToLoad);
		}
		if (saveInfoToLoad != null)
		{
			return LoadVillageData(saveInfoToLoad);
		}
		return false;
	}

	public bool LoadVillageData(VillageSaveInfo info)
	{
		if (!VillageSaveData.ValidateSave(info))
		{
			this.OnSaveFailedToValidate?.Invoke();
			return false;
		}
		CorruptedBlueprintIds = new HashSet<string>();
		ReplacedBlueprintIds = new HashSet<string>();
		CorruptedCarcassEquipment = new List<ResourceInstance>();
		MonoSingleton<UniqueIdManager>.Instance.ClearData();
		CurrentSaveVersion = info.ModifiedVersion;
		VillageSaveData villageSaveData = new VillageSaveData(info.FileName, info.VillageName, info.FolderName);
		try
		{
			villageSaveData.Deserialize();
		}
		catch (Exception message)
		{
			this.OnSaveFailedToValidate?.Invoke();
			Debug.LogError(message);
			villageSaveData.ZipClose();
			return false;
		}
		if (!villageSaveData.HasData())
		{
			this.OnSaveFailedToValidate?.Invoke();
			villageSaveData.ZipClose();
			return false;
		}
		VillageSaveInfo villageSaveInfo = SavesList.Find((VillageSaveInfo entry) => entry.FileName.Equals(info.FileName) && entry.FolderName.Equals(info.FolderName));
		if (villageSaveInfo == null)
		{
			this.OnSaveFailedToValidate?.Invoke();
			Log.Error("This should never happen! Some weird error occured while loading! Check this line!", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
			villageSaveData.ZipClose();
			return false;
		}
		MonoSingleton<TravelManager>.Instance.JustLeftSecondMap = false;
		villageSaveInfo.SetLastPlayed(DateTime.Now);
		villageSaveData.SetFirstTime(value: false);
		villageSaveData.SetFolderName(info.FolderName);
		if (CurrentVillageData != null)
		{
			CurrentVillageData?.Destroy();
		}
		if (currentVillageData != null)
		{
			currentVillageData?.Destroy();
		}
		CurrentVillageData = villageSaveData;
		currentVillageData = villageSaveData;
		Serialize();
		new AutoFix().FixBorkedPilesData(villageSaveData);
		this.OnSaveLoaded?.Invoke(villageSaveData);
		return true;
	}

	public bool LoadVillageData(SecondMapSaveInfo info)
	{
		MonoSingleton<UniqueIdManager>.Instance.ClearData();
		isSecondMapTransition = false;
		CurrentSaveVersion = Application.version;
		VillageSaveData villageSaveData = new VillageSaveData(info.FileName, info.Name, "SecondMap/Saves", Application.streamingAssetsPath);
		try
		{
			villageSaveData.Deserialize(isSecondSave: true);
		}
		catch (Exception message)
		{
			Debug.LogError(message);
			villageSaveData.ZipClose();
			return false;
		}
		if (!villageSaveData.HasData())
		{
			villageSaveData.ZipClose();
			return false;
		}
		villageSaveData.SetFirstTime(value: false);
		villageSaveData.ScheduledGameEvents.Clear();
		using PooledHashSet<CreatureBase> pooledHashSet = HashSetPool<CreatureBase>.GetJanitor();
		pooledHashSet.AddRange(villageSaveData.Workers);
		pooledHashSet.AddRange(villageSaveData.Animals);
		pooledHashSet.AddRange(villageSaveData.NPCs);
		MonoSingleton<TravelManager>.Instance.SetEnemyOwnedAnimals(villageSaveData.Animals);
		using (ListPool<AnimalInstance>.GetJanitor())
		{
			WorldDate dateAndTime = villageSaveData.DateAndTime;
			WorldDate dateAndTime2 = MonoSingleton<TravelManager>.Instance.DateAndTime;
			foreach (AnimalInstance animal in villageSaveData.Animals)
			{
				animal.Stats.SetDateCache(dateAndTime2);
				animal.Stats.ResetActiveEffectorsStartTime(dateAndTime2.MinutesTotal);
				animal.ResetTimestamps(dateAndTime.MinutesTotal, dateAndTime2.MinutesTotal);
			}
			foreach (HumanoidInstance worker in MonoSingleton<TravelManager>.Instance.Workers)
			{
				villageSaveData.AddWorker(worker);
				villageSaveData.UniqueIdData.Providers[UniqueIdType.Creature].AddUsedId(worker.GetUniqueId());
			}
			foreach (HumanoidInstance prisoner in MonoSingleton<TravelManager>.Instance.Prisoners)
			{
				villageSaveData.AddNPC(prisoner);
				villageSaveData.UniqueIdData.Providers[UniqueIdType.Creature].AddUsedId(prisoner.GetUniqueId());
			}
			foreach (AnimalInstance animal2 in MonoSingleton<TravelManager>.Instance.Animals)
			{
				villageSaveData.AddAnimal(animal2);
				villageSaveData.UniqueIdData.Providers[UniqueIdType.Creature].AddUsedId(animal2.GetUniqueId());
			}
			villageSaveData.UniqueIdData.Providers[UniqueIdType.Creature].SetNextId(100000);
			villageSaveData.SecondMapId = info.Id;
			villageSaveData.SetWorldMapData(MonoSingleton<TravelManager>.Instance.WorldMapData);
			villageSaveData.SetDateAndTime(dateAndTime2);
			villageSaveData.SetScheduledWeatherEvents(MonoSingleton<TravelManager>.Instance.ScheduledWeatherEvents);
			villageSaveData.SetWeatherEventsHourly(MonoSingleton<TravelManager>.Instance.WeatherEventsHourly);
			villageSaveData.SetFileName(info.OriginalFileName, info.OriginalFolderName);
			villageSaveData.SetTemperatureHourly(MonoSingleton<TravelManager>.Instance.TemperatureHourly);
			villageSaveData.SetUnlockedNodes(MonoSingleton<TravelManager>.Instance.ResearchedNodes);
			villageSaveData.SetUnlockedItems(MonoSingleton<TravelManager>.Instance.UnlockedItems);
			villageSaveData.SetWorldMapPlaceReference(MonoSingleton<TravelManager>.Instance.DestinationPlace.CreateReference());
			villageSaveData.HistoryEntries = MonoSingleton<TravelManager>.Instance.HistoryEntries;
			villageSaveData.GameParametersCurrent = MonoSingleton<TravelManager>.Instance.GameParametersCurrent;
			if (OriginalVillageData != null && OriginalVillageData.HasCache())
			{
				villageSaveData.AddCachedSaveData(OriginalVillageData.CachedSaveData);
				OriginalVillageData = null;
			}
			if (CurrentVillageData != null)
			{
				CurrentVillageData?.Destroy();
			}
			if (currentVillageData != null)
			{
				currentVillageData?.Destroy();
			}
			villageSaveData.SetIsSecondMap();
			villageSaveData.SetIsSecondMapFirstTime();
			CurrentVillageData = villageSaveData;
			currentVillageData = villageSaveData;
			this.OnSaveLoaded?.Invoke(villageSaveData);
			foreach (CreatureBase item in pooledHashSet)
			{
				item.ReassignUniqueId();
			}
			return true;
		}
	}

	public bool LoadVillageDataFromCache(VillageSaveInfo info)
	{
		isSecondMapTransition = false;
		MonoSingleton<UniqueIdManager>.Instance.ClearData();
		CurrentSaveVersion = info.ModifiedVersion;
		VillageSaveData villageSaveData = new VillageSaveData(info.FileName, info.VillageName, info.FolderName);
		if (OriginalVillageData == null || !OriginalVillageData.HasCache())
		{
			Log.Error("Failed to load VillageData from cache!", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
			forceOriginalVillage = false;
			return false;
		}
		villageSaveData.AddCachedSaveData(OriginalVillageData.CachedSaveData);
		OriginalVillageData = null;
		try
		{
			villageSaveData.DeserializeFromCache();
			forceOriginalVillage = false;
		}
		catch (Exception message)
		{
			Debug.LogError(message);
			villageSaveData.ZipClose();
			forceOriginalVillage = false;
			return false;
		}
		if (!villageSaveData.HasData())
		{
			forceOriginalVillage = false;
			return false;
		}
		villageSaveData.SetWorldMapData(MonoSingleton<TravelManager>.Instance.WorldMapData);
		villageSaveData.HistoryEntries = MonoSingleton<TravelManager>.Instance.HistoryEntries;
		villageSaveData.GameParametersCurrent = MonoSingleton<TravelManager>.Instance.GameParametersCurrent;
		MonoSingleton<TravelManager>.Instance.ClearWorldMapData();
		villageSaveData.SetFirstTime(value: false);
		villageSaveData.SetFolderName(info.FolderName);
		if (CurrentVillageData != null)
		{
			CurrentVillageData?.Destroy();
		}
		if (currentVillageData != null)
		{
			currentVillageData?.Destroy();
		}
		CurrentVillageData = villageSaveData;
		currentVillageData = villageSaveData;
		this.OnSaveLoaded?.Invoke(villageSaveData);
		return true;
	}

	public bool LoadTutorialVillageData(string fileName)
	{
		MonoSingleton<UniqueIdManager>.Instance.ClearData();
		CurrentSaveVersion = Application.version;
		VillageSaveData villageSaveData = new VillageSaveData(fileName, fileName, "Tutorial/Saves", Application.streamingAssetsPath);
		try
		{
			villageSaveData.Deserialize(isSecondSave: false, isTutorial: true);
		}
		catch (Exception message)
		{
			Debug.LogError(message);
			villageSaveData.ZipClose();
			return false;
		}
		if (!villageSaveData.HasData())
		{
			villageSaveData.ZipClose();
			return false;
		}
		villageSaveData.SetFirstTime(value: false);
		if (CurrentVillageData != null)
		{
			CurrentVillageData?.Destroy();
		}
		if (currentVillageData != null)
		{
			currentVillageData?.Destroy();
		}
		CurrentVillageData = villageSaveData;
		currentVillageData = villageSaveData;
		this.OnSaveLoaded?.Invoke(villageSaveData);
		return true;
	}

	public bool LoadSavedTutorialVillageData(string fileName)
	{
		MonoSingleton<UniqueIdManager>.Instance.ClearData();
		CurrentSaveVersion = Application.version;
		VillageSaveData villageSaveData = new VillageSaveData(fileName, fileName, "TutorialSaves", Application.persistentDataPath);
		try
		{
			villageSaveData.Deserialize(isSecondSave: false, isTutorial: true);
		}
		catch (Exception message)
		{
			Debug.LogError(message);
			villageSaveData.ZipClose();
			return false;
		}
		if (!villageSaveData.HasData())
		{
			villageSaveData.ZipClose();
			return false;
		}
		villageSaveData.SetFirstTime(value: false);
		if (CurrentVillageData != null)
		{
			CurrentVillageData?.Destroy();
		}
		if (currentVillageData != null)
		{
			currentVillageData?.Destroy();
		}
		CurrentVillageData = villageSaveData;
		currentVillageData = villageSaveData;
		this.OnSaveLoaded?.Invoke(villageSaveData);
		return true;
	}

	public List<VillageSaveInfo> GetVillageInfoByName(string villageName)
	{
		return SavesList.FindAll((VillageSaveInfo village) => string.Compare(village.VillageName, villageName, StringComparison.CurrentCultureIgnoreCase) == 0);
	}

	public bool AnyVillageInfoByName(string villageName)
	{
		foreach (VillageSaveInfo saves in SavesList)
		{
			if (string.Compare(saves.VillageName, villageName, StringComparison.CurrentCultureIgnoreCase) == 0)
			{
				return true;
			}
		}
		return false;
	}

	public void Deserialize()
	{
		if (!File.Exists(Path.Combine(FileReaders.Get.GetPersistentDataPath(), "global.config")))
		{
			InitDefaultSave();
			return;
		}
		try
		{
			data = Serializer.Deserialize();
		}
		catch (Exception ex)
		{
			bool isEnabled;
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(57, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Error during deserializing ");
				messageBuilder.AppendFormatted("global.config");
				messageBuilder.AppendLiteral("; loading defaults. Exception ");
				messageBuilder.AppendFormatted(ex.Message);
			}
			Log.Warning(messageBuilder);
			InitDefaultSave();
		}
		if (data == null)
		{
			InitDefaultSave();
		}
	}

	public void Serialize()
	{
		try
		{
			bool isEnabled;
			try
			{
				Serializer.Serialize(data);
			}
			catch (Exception ex)
			{
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(26, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Cannot write ");
					messageBuilder.AppendFormatted("global.config");
					messageBuilder.AppendLiteral(". Exception: ");
					messageBuilder.AppendFormatted(ex.Message);
				}
				Log.Warning(messageBuilder);
			}
			RemoveDuplicatedSaves();
			try
			{
				villageSavesSerializer.Serialize(villageSavesHolder);
			}
			catch (UnauthorizedAccessException ex2)
			{
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(26, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Cannot write ");
					messageBuilder.AppendFormatted("VillageSaves/VillageSavesList.sav");
					messageBuilder.AppendLiteral(". Exception: ");
					messageBuilder.AppendFormatted(ex2.Message);
				}
				Log.Warning(messageBuilder);
			}
		}
		catch (IOException ex3)
		{
			if (ex3.Message.Contains("Win32") && ex3.Message.Contains("112"))
			{
				saveErrorMessage = "Not enough free space on drive!\nPlease free up some space and restart game";
				Log.Warning("Could not save. Not enough free space", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				return;
			}
			Console.WriteLine(ex3);
			throw;
		}
		if (this.OnGlobalSaveUpdate != null)
		{
			this.OnGlobalSaveUpdate();
		}
	}

	public void AutosaveCurrentVillage()
	{
		if (!isAutosaveEnabled || currentVillageData == null)
		{
			return;
		}
		if (!VillageManager.ActiveVillage.Map.WaterManager.WaterSimThreadFinished && !currentVillageData.FirstEnter)
		{
			Log.Info("*** Delaying autosave because water thread is not finished.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
			MonoSingleton<TaskController>.Instance.WaitForNextFrameUnscaled().Then(AutosaveCurrentVillage);
			return;
		}
		Log.Info("*** Autosaving.", "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
		if (currentVillageData.FirstEnter)
		{
			currentVillageData.SetFirstTime(value: false);
			currentVillageData.Serialize();
		}
		else
		{
			if (!data.GlobalSettings.AutosaveActive)
			{
				return;
			}
			this.AutosaveStartEvent?.Invoke();
			int index = GetHighestAutosaveIndex(currentVillageData.Name) + 1;
			string text = GenerateAutoSaveFilename(currentVillageData.Name, index);
			currentVillageData.SetFileName(text, currentVillageData.FolderName);
			VillageSaveInfo villageSaveInfo = new VillageSaveInfo(currentVillageData.Name, text, currentVillageData.FolderName, DateTime.Now, autosave: true, index);
			villageSaveInfo.SetModifiedVersion(Application.version, isObsolete: false);
			SavesList.Add(villageSaveInfo);
			List<VillageSaveInfo> list = GetVillageInfoByName(currentVillageData.Name).FindAll((VillageSaveInfo entry) => entry.AutoSave);
			if (list.Count > Repository<GameSettingsData, GameSettings>.Instance.GetData<GameSettings>().AllowedAutosaves)
			{
				list.Sort((VillageSaveInfo v1, VillageSaveInfo v2) => v1.LastPlayed.CompareTo(v2.LastPlayed));
				for (int i = 0; i < list.Count - Repository<GameSettingsData, GameSettings>.Instance.GetData<GameSettings>().AllowedAutosaves; i++)
				{
					if (DeleteSaveFile(list[i]))
					{
						SavesList.Remove(list[i]);
					}
				}
			}
			currentVillageData.Serialize();
			Serialize();
			MonoSingleton<AchievementManager>.Instance.ForceFlush();
			if (data.GlobalSettings.AutosaveActive)
			{
				this.AutosaveEndEvent?.Invoke();
				autosaveStartEventFired = false;
			}
		}
	}

	public void QuicksaveCurrentVillage()
	{
	}

	public VillageSaveInfo SaveCurrentVillage(string saveFilename)
	{
		if (!saveFilename.EndsWith(".sav"))
		{
			saveFilename += ".sav";
		}
		if (currentVillageData.IsSecondMap)
		{
			return SaveSecondVillage(saveFilename);
		}
		currentVillageData.SetFileName(saveFilename, currentVillageData.FolderName);
		VillageSaveInfo villageSaveInfo = new VillageSaveInfo(currentVillageData.Name, saveFilename, currentVillageData.FolderName, DateTime.Now);
		villageSaveInfo.SetModifiedVersion(Application.version, isObsolete: false);
		SavesList.Add(villageSaveInfo);
		currentVillageData.Serialize();
		Serialize();
		MonoSingleton<AchievementManager>.Instance.ForceFlush();
		return villageSaveInfo;
	}

	private VillageSaveInfo SaveSecondVillage(string saveFilename)
	{
		currentVillageData.SetFileName(saveFilename, currentVillageData.FolderName, GetDefaultSaveFolder());
		VillageSaveInfo villageSaveInfo = new VillageSaveInfo(currentVillageData.Name, saveFilename, currentVillageData.FolderName, DateTime.Now);
		villageSaveInfo.SetModifiedVersion(Application.version, isObsolete: false);
		SavesList.Add(villageSaveInfo);
		if (!currentVillageData.HasCache() && OriginalVillageData != null)
		{
			currentVillageData.AddCachedSaveData(OriginalVillageData.CachedSaveData);
		}
		currentVillageData.SerializeWithCache();
		Serialize();
		MonoSingleton<AchievementManager>.Instance.ForceFlush();
		return villageSaveInfo;
	}

	public VillageSaveInfo DebugSaveSecondVillage(string saveFilename)
	{
		if (!saveFilename.EndsWith(".sav"))
		{
			saveFilename += ".sav";
		}
		currentVillageData.SetFileName(saveFilename, "SecondMap/Saves", Application.streamingAssetsPath);
		VillageSaveInfo villageSaveInfo = new VillageSaveInfo(currentVillageData.Name, saveFilename, currentVillageData.FolderName, DateTime.Now);
		villageSaveInfo.SetModifiedVersion(Application.version, isObsolete: false);
		currentVillageData.Serialize(isSecondSave: true);
		MonoSingleton<AchievementManager>.Instance.ForceFlush();
		return villageSaveInfo;
	}

	public VillageSaveInfo DebugSaveTutorialVillage(string saveFilename)
	{
		if (!saveFilename.EndsWith(".sav"))
		{
			saveFilename += ".sav";
		}
		currentVillageData.SetFileName(saveFilename, "Tutorial/Saves", Application.streamingAssetsPath);
		VillageSaveInfo villageSaveInfo = new VillageSaveInfo(currentVillageData.Name, saveFilename, currentVillageData.FolderName, DateTime.Now);
		villageSaveInfo.SetModifiedVersion(Application.version, isObsolete: false);
		currentVillageData.Serialize();
		MonoSingleton<AchievementManager>.Instance.ForceFlush();
		return villageSaveInfo;
	}

	private VillageSaveInfo SaveTutorialVillage()
	{
		currentVillageData.SetFileName(currentVillageData.FileName, currentVillageData.FolderName, GetTutorialSaveFolder());
		VillageSaveInfo villageSaveInfo = new VillageSaveInfo(currentVillageData.Name, currentVillageData.FileName, currentVillageData.FolderName, DateTime.Now);
		villageSaveInfo.SetModifiedVersion(Application.version, isObsolete: false);
		currentVillageData.Serialize();
		MonoSingleton<AchievementManager>.Instance.ForceFlush();
		return villageSaveInfo;
	}

	public void SetIsEarlyBird(bool isEarlyBird)
	{
		UserDataInfo.SetIsEarlyBird(isEarlyBird);
		UnlockEarlyBirdLockedBuildings();
	}

	internal void UnlockEarlyBirdLockedBuildings()
	{
		if (UserDataInfo.IsEarlyBird)
		{
			string[] earlyAccessRewards = LockedBuildingsManager.EarlyAccessRewards;
			foreach (string id in earlyAccessRewards)
			{
				RemoveFromLockedBuildings(id);
			}
		}
	}

	public bool IsBuildingLocked(string id)
	{
		return UserDataInfo.LockedBuildingsHash.Contains(id);
	}

	public void RemoveFromLockedBuildings(string id)
	{
		if (UserDataInfo.LockedBuildingsHash.Remove(id))
		{
			this.BuildingUnlockedEvent?.Invoke(id);
		}
	}

	public bool TerrainExists(VillageSaveInfo info)
	{
		return true;
	}

	private void OnChangeScene(string scene)
	{
		isAutosaveEnabled = false;
	}

	private void OnSceneUnloaded(Scene scene)
	{
		if (scene.name.Equals("MainScene"))
		{
			if (MonoSingleton<NSMedieval.WorldMap.WorldMap>.IsInstantiated())
			{
				MonoSingleton<NSMedieval.WorldMap.WorldMap>.Instance.OnQuitToMain();
			}
			if (CurrentVillageData != null)
			{
				CurrentVillageData?.Destroy();
			}
			if (currentVillageData != null)
			{
				currentVillageData?.Destroy();
			}
			if (MonoSingleton<GlobalSaveController>.IsInstantiated() && !MonoSingleton<GlobalSaveController>.Instance.IsSecondMapTransition)
			{
				OriginalVillageData = null;
			}
			CurrentVillageData = null;
			currentVillageData = null;
		}
	}

	private void OnMainSceneLoaded()
	{
		MonoSingleton<World>.Instance.MapLoadedEvent += OnLoadingFinished;
	}

	private void OnLoadingFinished(bool afterLoad)
	{
		isAutosaveEnabled = true;
		MonoSingleton<World>.Instance.MapLoadedEvent -= OnLoadingFinished;
	}

	private void InitDefaultSave()
	{
		if (CurrentVillageData != null)
		{
			CurrentVillageData?.Destroy();
		}
		if (currentVillageData != null)
		{
			currentVillageData?.Destroy();
		}
		CurrentVillageData = null;
		currentVillageData = null;
		data = new Data();
		data.InitEmpty();
		if (Repository<DefaultPlayerControlsData, DefaultPlayerControls>.IsInstantiated())
		{
			data.GlobalSettings.SetKeybindings(Repository<DefaultPlayerControlsData, DefaultPlayerControls>.Instance.GetData<DefaultPlayerControls>().Keybindings);
		}
		SynchronizeWithFiles();
		Serialize();
	}

	private void Start()
	{
		if (userData == null)
		{
			DeserializeUserData();
		}
		if (data == null)
		{
			Deserialize();
		}
	}

	private void OnEnable()
	{
		MonoSingleton<LoadingController>.Instance.SceneUnloadedEvent += OnSceneUnloaded;
		MonoSingleton<LoadingController>.Instance.MainSceneLoadedEvent += OnMainSceneLoaded;
	}

	private void OnDisable()
	{
		MonoSingleton<LoadingController>.Instance.SceneUnloadedEvent -= OnSceneUnloaded;
		MonoSingleton<LoadingController>.Instance.MainSceneLoadedEvent -= OnMainSceneLoaded;
	}

	protected override void OnDestroy()
	{
		this.OnGlobalSaveUpdate = null;
		this.OnSaveFailedToValidate = null;
		this.OnSaveLoaded = null;
		this.AutosaveStartEvent = null;
		this.AutosaveEndEvent = null;
		this.BuildingUnlockedEvent = null;
		this.QuicksaveStartEvent = null;
		base.OnDestroy();
	}

	private bool DeleteSaveFile(VillageSaveInfo info)
	{
		if (info == null || string.IsNullOrEmpty(info.FileName))
		{
			return false;
		}
		bool result = false;
		string filepath = GetAbsoluteSaveFilename(info.FileName, info.FolderName);
		bool isEnabled;
		if (File.Exists(filepath))
		{
			try
			{
				FileUtils.SafeFileOperation(delegate
				{
					File.Delete(filepath);
				});
				result = true;
			}
			catch (Exception ex)
			{
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(37, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Failed to delete save \"");
					messageBuilder.AppendFormatted(FilePathUtils.RemoveUserFromPath(filepath));
					messageBuilder.AppendLiteral("\". Exception: ");
					messageBuilder.AppendFormatted(ex.Message);
				}
				Log.Warning(messageBuilder);
			}
		}
		string path = filepath + ".meta";
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (Exception ex2)
		{
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(42, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Failed to delete meta file \"");
				messageBuilder.AppendFormatted(FilePathUtils.RemoveUserFromPath(path));
				messageBuilder.AppendLiteral("\". Exception: ");
				messageBuilder.AppendFormatted(ex2.Message);
			}
			Log.Warning(messageBuilder);
		}
		string path2 = filepath.Replace(".sav", ".gmevents");
		try
		{
			if (File.Exists(path2))
			{
				File.Delete(path2);
			}
		}
		catch (Exception ex3)
		{
			FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(46, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
			if (isEnabled)
			{
				messageBuilder.AppendLiteral("Failed to delete gmevents file \"");
				messageBuilder.AppendFormatted(FilePathUtils.RemoveUserFromPath(path2));
				messageBuilder.AppendLiteral("\". Exception: ");
				messageBuilder.AppendFormatted(ex3.Message);
			}
			Log.Warning(messageBuilder);
		}
		string folderPath = GetAbsoluteSaveFilename(string.Empty, info.FolderName);
		if (Directory.Exists(folderPath) && Directory.GetFiles(folderPath).Length == 0 && Directory.GetDirectories(folderPath).Length == 0)
		{
			try
			{
				FileUtils.SafeFileOperation(delegate
				{
					Directory.Delete(folderPath);
				});
			}
			catch (IOException ex4)
			{
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(48, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Failed to delete folder ");
					messageBuilder.AppendFormatted(FilePathUtils.RemoveUserFromPath(info.FolderName));
					messageBuilder.AppendLiteral("\" from disk. Exception: ");
					messageBuilder.AppendFormatted(ex4.Message);
				}
				Log.Warning(messageBuilder);
			}
		}
		return result;
	}

	public bool DeleteSaveFile(SecondMapSaveInfo info)
	{
		if (info == null || string.IsNullOrEmpty(info.FileName))
		{
			return false;
		}
		bool result = false;
		string filePath = GetAbsoluteStreamingAssetsFilename(info.FileName, "SecondMap/Saves");
		bool isEnabled;
		if (File.Exists(filePath))
		{
			try
			{
				FileUtils.SafeFileOperation(delegate
				{
					File.Delete(filePath);
				});
				result = true;
			}
			catch (Exception ex)
			{
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(53, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Failed to delete save \"SecondMap/Saves/");
					messageBuilder.AppendFormatted(info.FileName);
					messageBuilder.AppendLiteral("\". Exception: ");
					messageBuilder.AppendFormatted(ex.Message);
				}
				Log.Warning(messageBuilder);
			}
		}
		string metaFilePath = filePath + ".meta";
		if (File.Exists(metaFilePath))
		{
			try
			{
				FileUtils.SafeFileOperation(delegate
				{
					File.Delete(metaFilePath);
				});
			}
			catch (Exception ex2)
			{
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(58, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Failed to delete meta \"SecondMap/Saves/");
					messageBuilder.AppendFormatted(info.FileName);
					messageBuilder.AppendLiteral(".meta\". Exception: ");
					messageBuilder.AppendFormatted(ex2.Message);
				}
				Log.Warning(messageBuilder);
			}
		}
		return result;
	}

	public bool DeleteTutorialSaveFile(string fileName)
	{
		if (string.IsNullOrEmpty(fileName))
		{
			return false;
		}
		bool result = false;
		string filepath = Path.Combine(GetTutorialSaveFolder(), fileName).Replace('\\', '/');
		if (File.Exists(filepath))
		{
			try
			{
				FileUtils.SafeFileOperation(delegate
				{
					File.Delete(filepath);
				});
				result = true;
			}
			catch (Exception ex)
			{
				bool isEnabled;
				FVLogWarningInterpolationHandler messageBuilder = new FVLogWarningInterpolationHandler(51, 2, out isEnabled, "D:\\Git\\GoingMedieval\\Assets\\Scripts\\State\\Profile\\GlobalSaveController.cs");
				if (isEnabled)
				{
					messageBuilder.AppendLiteral("Failed to delete save \"TutorialSaves/");
					messageBuilder.AppendFormatted(fileName);
					messageBuilder.AppendLiteral("\". Exception: ");
					messageBuilder.AppendFormatted(ex.Message);
				}
				Log.Warning(messageBuilder);
			}
		}
		return result;
	}

	private int GetHighestAutosaveIndex(string villageName)
	{
		List<VillageSaveInfo> villageInfoByName = GetVillageInfoByName(villageName);
		int num = 0;
		string text = "Autosave-";
		for (int i = 0; i < villageInfoByName.Count; i++)
		{
			if (!villageInfoByName[i].AutoSave)
			{
				continue;
			}
			if (villageInfoByName[i].Index > num)
			{
				num = Mathf.Max(num, villageInfoByName[i].Index);
			}
			try
			{
				int num2 = villageInfoByName[i].FileName.LastIndexOf(text, StringComparison.Ordinal);
				int num3 = villageInfoByName[i].FileName.LastIndexOf(".sav", StringComparison.Ordinal);
				if (num2 >= 0 && int.TryParse(villageInfoByName[i].FileName.Substring(num2 + text.Length, num3 - num2 - text.Length), out var result))
				{
					num = Math.Max(result, num);
				}
			}
			catch (Exception)
			{
			}
		}
		return num;
	}

	private string GenerateAutoSaveFilename(string villageName, int index)
	{
		string text = $"Autosave-{index}.sav";
		if (File.Exists(GetAbsoluteSaveFilename(text, currentVillageData.FolderName)))
		{
			text = $"Autosave-{index}-alt.sav";
		}
		return text;
	}

	public void SynchronizeWithFiles()
	{
		for (int num = SavesList.Count - 1; num > -1; num--)
		{
			if (!File.Exists(GetAbsoluteSaveFilename(SavesList[num].FileName, SavesList[num].FolderName)))
			{
				SavesList.RemoveAt(num);
			}
		}
		Serialize();
	}

	private void OnGUI()
	{
		if (!(saveErrorMessage == string.Empty))
		{
			float num = (float)Screen.width / 2f;
			float num2 = (float)Screen.height / 2f;
			GUI.Box(new Rect(num - num / 2f, num2 - num2 / 2f, num, num2), saveErrorMessage);
		}
	}

	private void DeserializeUserData()
	{
		string path = Path.Combine(FileReaders.Get.GetPersistentDataPath(), "user.bin");
		if (!File.Exists(path))
		{
			InitUserData();
			return;
		}
		byte[] array = File.ReadAllBytes(path);
		try
		{
			using FVDeserializer deserializer = new FVDeserializer("userData", array);
			userData = new UserData(deserializer);
		}
		catch (Exception)
		{
			InitUserData();
		}
	}

	private void InitUserData()
	{
		userData = new UserData();
		SerializeUserData();
	}

	public void SerializeUserData()
	{
		string path = Path.Combine(FileReaders.Get.GetPersistentDataPath(), "user.bin");
		using FVSerializer fVSerializer = new FVSerializer("userData");
		userData.Serialize(fVSerializer);
		File.WriteAllBytes(path, fVSerializer.GetBytes());
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
