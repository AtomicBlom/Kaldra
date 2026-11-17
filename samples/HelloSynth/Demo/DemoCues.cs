using Loom.Audio.Synth.Music;

namespace HelloSynth.Demo;

/// <summary>What the demo's music listens to. A game keeps its cues in static fields like these, and every song uses them.</summary>
internal static class DemoCues
{
	/// <summary>How much is going on, 0 to 1: the lead comes in above a half, and its filter opens with it.</summary>
	public static readonly Cue Intensity = new("intensity", 0.3f);

	/// <summary>Which motif plays.</summary>
	public static readonly Cue<DemoMood> Mood = new("mood");
}
