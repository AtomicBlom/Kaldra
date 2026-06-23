using Loom.Core.Geometry;

namespace Loom.Core.Hosting;

/// <summary>
/// The per-frame data a host hands the engine for one <see cref="IEngine.Render"/> call (Planning.md §5's
/// <c>graph.Render(frameInputs)</c>). This is the single channel for everything that varies frame to frame —
/// the view(s), timing, the UI draw data — so pass closures stay pure: they <em>pull</em>
/// these from <see cref="IRenderContext"/> rather than capturing host state (Planning.md §8). Immutable: the
/// host builds a fresh one each frame.
/// <para>
/// Matrices are stated in a backend-neutral convention (right-handed, +Y up; <c>System.Numerics</c>); the
/// backend applies its own clip-space fix-up (the Vulkan Y-flip, identity on D3D12) on ingest, so the host
/// never bakes an API-specific NDC convention (Planning.md §8 portability; M7-B5).
/// </para>
/// </summary>
public sealed record FrameInputs
{
	/// <summary>Seconds elapsed since the previous frame; surfaced as <see cref="IRenderContext.DeltaTime"/>.</summary>
	public double DeltaSeconds { get; init; }

	/// <summary>Elapsed seconds since startup, for time-driven effects; surfaced as <see cref="IRenderContext.Time"/>.</summary>
	public float Time { get; init; }

	/// <summary>
	/// The default view a pass renders from this frame — the one a draw uses when it names no other; surfaced as
	/// <see cref="IRenderContext.View"/>. Neutral convention (right-handed, +Y up); the backend applies its
	/// clip-space fix-up on ingest. A renderer with one viewpoint sets only this.
	/// </summary>
	public Camera View { get; init; } = Camera.Identity;

	/// <summary>
	/// The frame's additional named views, keyed by an app-chosen string; surfaced as <see cref="IRenderContext.Views"/>.
	/// A draw renders from one by passing it as the view argument, and a pass that consumes view matrices (a shadow
	/// test, a reflection) reads them by key. This is how the host supplies any extra viewpoint — a shadow light, a
	/// mirror, a security camera, a cascade slice — without the engine naming any of them. Each is a neutral
	/// <see cref="Camera"/> the backend flips on ingest, exactly as <see cref="View"/>. Empty (the default) when the
	/// frame draws only from <see cref="View"/>.
	/// </summary>
	public IReadOnlyDictionary<string, Camera> Views { get; init; } = new Dictionary<string, Camera>();

	/// <summary>
	/// This frame's draw lists — each <see cref="DrawListId"/> paired with the neutral <see cref="DrawListData"/> a
	/// producer composited for it. Surfaced as <see cref="IRenderContext.DrawLists"/>; a pass pulls one out by id and
	/// submits it (the game UI does so via <c>SUIsei</c>'s painter). Empty (the default) renders no lists. Pulled in
	/// the pass closure, never captured, so the graph stays frame-independent (Planning.md §8).
	/// </summary>
	public IReadOnlyList<(DrawListId Id, DrawListData Data)> DrawLists { get; init; } = [];
}
