namespace Loom.Core.Hosting;

/// <summary>
/// The per-frame data a host hands the engine for one <see cref="IEngine.Render"/> call. This is the single
/// channel for everything that varies frame to frame — timing — so pass closures stay pure: they <em>pull</em>
/// these from <see cref="IRenderContext"/> rather than capturing host state. A small value type the host builds
/// fresh each frame at no allocation cost. View-projections are not here: the app publishes them as ordinary data
/// through the <c>g.Constants</c> buffers its passes read, so the engine carries no camera or view concept.
/// </summary>
public readonly record struct FrameInputs
{
	/// <summary>Seconds elapsed since the previous frame; surfaced as <see cref="IRenderContext.DeltaTime"/>.</summary>
	public double DeltaSeconds { get; init; }

	/// <summary>Elapsed seconds since startup, for time-driven effects; surfaced as <see cref="IRenderContext.Time"/>.</summary>
	public double Time { get; init; }
}
