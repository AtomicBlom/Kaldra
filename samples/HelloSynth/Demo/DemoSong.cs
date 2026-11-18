using Loom.Audio.Synth.Chip;
using Loom.Audio.Synth.Music;

namespace HelloSynth.Demo;

/// <summary>
/// The demo song: two sections over the same groove, a bass over the chords, a lead that comes in with the intensity, and
/// a motif per mood that comes in only where its phrase starts. A method that builds a new song each time: edit it under
/// dotnet watch, save, and it is swapped in on the next bar.
/// </summary>
internal static class DemoSong
{
	public static Song Build()
	{
		var song = new Song("demo") { Tempo = 112, Scale = Scale.Minor(Note.A) };
		var drums = song.Track("drums", DemoKit.Make()).Humanise(4f, 0.08f);
		var bass = song.Track("bass", DemoPatches.PickedBass).Octave(2);
		var keys = song.Track("keys", DemoPatches.ElectricPiano).Octave(4).Level(-4f);
		var lead = song.Track("lead", DemoPatches.SawLead).Octave(5).Level(-6f);
		var motif = song.Track("motif", DemoPatches.Pluck).Octave(5).Level(-3f);
		lead.Macro(DemoPatches.Bright).Follow(DemoCues.Intensity, 0.1f, 1f);

		var groove = Beat.Eighths(
			(Kit.Kick, "x... ..x. x... ...."),
			(Kit.Snare, "..x. ..x. ..x. ..xo"),
			(Kit.HatClosed, "xxxx xxx. xxxx xxx."),
			(Kit.HatOpen, ".... ...x .... ...x"));
		var fill = Beat.Sixteenths(
			(Kit.Snare, "..x. x.x. xxxx XXXX"),
			(Kit.Kick, "x... .... x... ...."));

		var verse = song.Section("verse", 4, "i | VI | III | VII");
		verse.Play(drums, groove).EndWith(fill);
		verse.Play(bass, Line.Eighths("1 . 1 1 5, . 1 . | 1 . 1 1 7, . 5, .").OverChords());
		verse.Play(keys, Line.Quarters("1 3 5 3").OverChords()).Named("keys");
		verse.Play(lead, Line.Eighths("5 - 3 5 7 - 5 3 | 4 - 2 4 6 - 4 2 | 3 - 1 3 5 - 3 1 | 2 - 7, 2 4 - 2 7,"))
			.When(DemoCues.Intensity.Above(0.5f));
		Motif(verse, motif, DemoMood.Calm, Line.Quarters("1 - 3 - | 5 - 3 - | 1 - 2 - | 3 - - -"));
		Motif(verse, motif, DemoMood.Bright, Line.Eighths("5 6 8 6 5 3 5 - | 6 8 9 8 6 5 3 - | 5 6 8 6 5 3 2 3 | 1 - - - . . . ."));
		Motif(verse, motif, DemoMood.Dark, Line.Quarters("1 b2 1 . | 7, - 5, . | 1 b2 3 b2 | 1 - - ."));

		var chorus = song.Section("chorus", 4, "VI VII | i | VI VII | i");
		chorus.Play(drums, Beat.Eighths((Kit.Kick, "x.x. x.x."), (Kit.Snare, "..X. ..X."), (Kit.HatOpen, "x.x. x.x.")));
		chorus.Play(bass, Line.Eighths("1 - 1 - 1 1 5, 1").OverChords());
		chorus.Play(keys, Line.Eighths("1 3 5 8 5 3 1 3").OverChords()).Named("keys");
		chorus.Play(lead, Line.Quarters("8 - 7 5 | 8 - - - | 8 - 9 7 | 8 - - -")).When(DemoCues.Intensity.Above(0.5f));

		song.Order(verse, verse, chorus);
		return song;
	}

	private static void Motif(Section section, SongTrack track, DemoMood mood, Line line) =>
		section.Play(track, line).When(DemoCues.Mood.Is(mood)).OnPhrase().Named($"{mood.ToString().ToLowerInvariant()} motif");
}
