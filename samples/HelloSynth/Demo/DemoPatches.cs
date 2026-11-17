using Loom.Audio.Synth.Chip;

namespace HelloSynth.Demo;

/// <summary>
/// The demo's instruments. Each is a property that builds a new patch every time it is read: hot reload re-runs a
/// property's body, never a static field's initialiser, so an edit here is heard at the next bar.
/// </summary>
internal static class DemoPatches
{
	/// <summary>The macro that opens the lead's filter.</summary>
	public static Macro Bright { get; } = new(0);

	/// <summary>A tine piano: a struck sine modulated by a fast-dying one at fourteen times, bell-like when hard.</summary>
	public static Patch ElectricPiano => Patch.Build("electric piano", p =>
	{
		var tine = p.Sine(14f, Env.Decay(0.12f), 0.6f, velocity: 0.8f);
		var body = p.Sine(1f, Env.Adsr(0.002f, 1.6f, 0.2f, 0.3f)).ModulatedBy(tine, 1.2f);
		p.Out(body);
		p.Send(Loom.Audio.Synth.Chip.ChipSend.Space, 0.2f);
	});

	/// <summary>A picked bass: a sine with a little feedback, a bright pick at the start of each note.</summary>
	public static Patch PickedBass => Patch.Build("picked bass", p =>
	{
		var pick = p.Sine(3f, Env.Decay(0.06f), 0.8f, velocity: 0.7f);
		p.Out(p.Sine(1f, Env.Adsr(0.002f, 0.9f, 0.3f, 0.08f)).Feedback(0.15f).ModulatedBy(pick, 2f));
		p.Drive(0.2f);
		p.Mono();
	});

	/// <summary>A plucked string, its pluck shaped at the pick point.</summary>
	public static Patch Pluck => Patch.Build("pluck", p =>
	{
		p.Out(p.String(1f, decaySeconds: 2.5f, damping: 0.4f));
		p.Filter(Filter.LowPass(5000f, keyTrack: 0.3f));
		p.Send(Loom.Audio.Synth.Chip.ChipSend.Space, 0.25f);
	});

	/// <summary>A saw lead whose filter the Bright macro opens.</summary>
	public static Patch SawLead => Patch.Build("saw lead", p =>
	{
		// Each saw starting at a different place in its cycle: two starting together are one loud wave, a click
		p.Out(p.Wave(Waveform.Saw, 1f, Env.Adsr(0.01f, 0.4f, 0.7f, 0.15f), 0.5f),
			p.Wave(Waveform.Saw, 1f, Env.Adsr(0.01f, 0.4f, 0.7f, 0.15f), 0.5f).Detune(9f).Phase(0.37f));
		p.Filter(Filter.LowPass(900f, q: 1.2f, envelopeOctaves: 1f, envelope: Env.Decay(0.5f)));
		p.Macro(Bright, MacroTarget.FilterCutoff, 0.5f, 3f);
		p.Vibrato(5f, 0.1f, 0.3f);
		p.Send(Loom.Audio.Synth.Chip.ChipSend.Echo, 0.15f);
	});
}
