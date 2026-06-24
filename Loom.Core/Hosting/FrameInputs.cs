namespace Loom.Core.Hosting;

/// <summary>
/// The per-frame data a host hands the engine for one <see cref="IEngine.Render"/> call. This is the single
/// channel for everything that varies frame to frame — timing and the UI draw data — so pass closures stay pure:
/// they <em>pull</em> these from <see cref="IRenderContext"/> rather than capturing host state. Immutable: the
/// host builds a fresh one each frame. View-projections are not here: the app publishes them as ordinary data
/// through the <c>g.Constants</c> buffers its passes read, so the engine carries no camera or view concept.
/// </summary>
public sealed record FrameInputs
{
	/// <summary>Seconds elapsed since the previous frame; surfaced as <see cref="IRenderContext.DeltaTime"/>.</summary>
	public double DeltaSeconds { get; init; }

	/// <summary>Elapsed seconds since startup, for time-driven effects; surfaced as <see cref="IRenderContext.Time"/>.</summary>
	public float Time { get; init; }

	/// <summary>
	/// This frame's draw lists — each <see cref="DrawListId"/> paired with the neutral <see cref="DrawListData"/> a
	/// producer composited for it. Surfaced as <see cref="IRenderContext.DrawLists"/>; a pass pulls one out by id and
	/// submits it (the game UI does so via <c>SUIsei</c>'s painter). Empty (the default) renders no lists. Pulled in
	/// the pass closure, never captured, so the graph stays frame-independent.
	/// </summary>
	public IReadOnlyList<(DrawListId Id, DrawListData Data)> DrawLists { get; init; } = [];
}
