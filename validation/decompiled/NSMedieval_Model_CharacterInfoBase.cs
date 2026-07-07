using System;
using NSEipix.Repository;
using NSMedieval.Repository;
using NSMedieval.Serialization;
using UnityEngine;

namespace NSMedieval.Model;

[Serializable]
[FVSerializableKey("CharacterInfoBase", "")]
public class CharacterInfoBase : CreatureInfoBase
{
	[SerializeField]
	private string firstName;

	[SerializeField]
	private string lastName;

	[SerializeField]
	private float height;

	[SerializeField]
	private float weightCoefficient;

	[SerializeField]
	private WorkerPhysicalLook physicalLook;

	[SerializeField]
	private int creationPoints;

	public string FirstName => firstName;

	public string LastName => lastName;

	public float Height => height;

	public float WeightCoefficient => weightCoefficient;

	public WorkerPhysicalLook PhysicalLook => physicalLook;

	public int CreationPoints => creationPoints;

	public CharacterInfoBase(string firstName, string lastName, BodyType bodyType, int age, float height, float weightCoefficient, WorkerPhysicalLook look)
		: base(bodyType, age)
	{
		this.firstName = firstName;
		this.lastName = lastName;
		this.height = height;
		this.weightCoefficient = weightCoefficient;
		physicalLook = look;
		physicalLook.Initialize();
	}

	public CharacterInfoBase()
	{
	}

	public void SetPhysicalLook(WorkerPhysicalLook physicalLook)
	{
		this.physicalLook = physicalLook;
	}

	public virtual string GetPhysicalLookKey()
	{
		return null;
	}

	public void SetFirstName(string name)
	{
		firstName = name;
	}

	public void SetLastName(string lastName)
	{
		this.lastName = lastName;
	}

	public void SetHeight(float value)
	{
		height = Mathf.Clamp(value, Repository<GenerationSettingsRepository, GenerationSettings>.Instance.Settings.HeightRange.Min, Repository<GenerationSettingsRepository, GenerationSettings>.Instance.Settings.HeightRange.Max);
	}

	public void SetWeight(float value)
	{
		weightCoefficient = ClampWeight(value) / Mathf.Pow(Height / 100f, 2f);
	}

	public float GetWeight()
	{
		return GetWeight(WeightCoefficient, Height);
	}

	public static float GetWeight(float weightCoefficient, float height)
	{
		return ClampWeight(weightCoefficient * Mathf.Pow(height / 100f, 2f));
	}

	public float GetBlendShapeWeight()
	{
		float weight = GetWeight();
		float weight2 = GetWeight(Repository<GenerationSettingsRepository, GenerationSettings>.Instance.Settings.WeightCoefficientRange.Min, height);
		float weight3 = GetWeight(Repository<GenerationSettingsRepository, GenerationSettings>.Instance.Settings.WeightCoefficientRange.Max, height);
		return 100f * Mathf.Clamp01((weight - weight2) / (weight3 - weight2));
	}

	private static float ClampWeight(float output)
	{
		return Mathf.Clamp(output, Repository<GenerationSettingsRepository, GenerationSettings>.Instance.Settings.WeightRange.Min, Repository<GenerationSettingsRepository, GenerationSettings>.Instance.Settings.WeightRange.Max);
	}

	public override void SetAge(int age)
	{
		int num = Mathf.Clamp(age, Repository<GenerationSettingsRepository, GenerationSettings>.Instance.Settings.AgeRange.Min, Repository<GenerationSettingsRepository, GenerationSettings>.Instance.Settings.AgeRange.Max);
		base.SetAge(num);
	}

	public virtual string GetFullName()
	{
		return string.Empty;
	}

	public void SetCreationPoints(int points)
	{
		creationPoints = points;
	}

	public override void Serialize(FVSerializer serializer)
	{
		base.Serialize(serializer);
		serializer.Write("firstName", firstName);
		serializer.Write("lastName", lastName);
		serializer.Write("height", height);
		serializer.Write("weightCoefficient", weightCoefficient);
		serializer.Write("physicalLook", physicalLook);
		serializer.Write("creationPoints", creationPoints);
	}

	public CharacterInfoBase(FVDeserializer deserializer)
		: base(deserializer)
	{
		firstName = deserializer.ReadString("firstName");
		lastName = deserializer.ReadString("lastName");
		height = deserializer.ReadFloat("height");
		weightCoefficient = deserializer.ReadFloat("weightCoefficient");
		physicalLook = deserializer.ReadObject<WorkerPhysicalLook>("physicalLook");
		creationPoints = deserializer.ReadInt("creationPoints");
	}
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
