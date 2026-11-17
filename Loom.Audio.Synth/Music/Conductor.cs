namespace Loom.Audio.Synth.Music;

/// <summary>
/// What a song's <see cref="Song.Conduct"/> function is handed near the end of each section, to choose the next: the
/// section playing, how many times in a row, and the cues.
/// </summary>
public sealed class Conductor
{
	private readonly Func<CueBase, float> _cue;

	internal Conductor(Func<CueBase, float> cue) => _cue = cue;

	/// <summary>The section playing.</summary>
	public Section Current { get; internal set; } = null!;

	/// <summary>How many passes of <see cref="Current"/> in a row, this one included.</summary>
	public int Repeats { get; internal set; }

	/// <summary>A level cue's value now.</summary>
	public float this[Cue cue] => _cue(cue);

	/// <summary>A choice cue's value now.</summary>
	public T Get<T>(Cue<T> cue) where T : struct, Enum
	{
		var value = (int)_cue(cue);
		return (T)Enum.ToObject(typeof(T), value);
	}

	// Any cue's value as the sequencer holds it, for a condition a song file's rules ask
	internal float Value(CueBase cue) => _cue(cue);
}
