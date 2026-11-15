using Loom.Audio.Workbench;

namespace HelloSynth.Demo;

/// <summary>What the demo workbench offers: factories, so hot reload's edits are built again.</summary>
internal static class DemoCatalog
{
	public static WorkbenchCatalog Make() => new()
	{
		Songs = [("demo", DemoSong.Build)],
		Patches =
		[
			("electric piano", () => DemoPatches.ElectricPiano),
			("picked bass", () => DemoPatches.PickedBass),
			("pluck", () => DemoPatches.Pluck),
			("saw lead", () => DemoPatches.SawLead),
		],
		Kits = [("demo kit", DemoKit.Make)],
		Cues = [DemoCues.Intensity, DemoCues.Mood],
	};
}
