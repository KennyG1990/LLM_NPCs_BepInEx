using System;

namespace NSMedieval.State.WorkerJobs;

[Flags]
public enum JobType
{
	None = 0,
	Hauling = 1,
	Mining = 2,
	Construction = 4,
	Crafting = 8,
	Harvesting = 0x10,
	Hunting = 0x20,
	PlantCropfields = 0x40,
	PlantCutting = 0x80,
	Smithing = 0x100,
	Carpentry = 0x200,
	Cooking = 0x400,
	Tailoring = 0x800,
	Research = 0x1000,
	Rest = 0x2000,
	TendWounds = 0x4000,
	Basic = 0x8000,
	Animal = 0x10000,
	Art = 0x20000,
	Patient = 0x40000,
	UrgentHaul = 0x80000,
	Fishing = 0x100000,
	Alchemy = 0x200000,
	Gaoler = 0x400000,
	FireFight = 0x800000,
	Train = 0x1000000
}
You are not using the latest version of the tool, please update.
Latest version is '10.1.0.8386' (yours is '8.2.0.7535-95108c96')
