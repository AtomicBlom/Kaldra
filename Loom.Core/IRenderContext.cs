namespace Loom.Core;

/// <summary>
/// The per-frame execution context handed to a pass's <c>.Draw</c>/<c>.Dispatch</c> callback.
/// Callbacks <em>pull</em> their inputs from here rather than capturing them, which is what keeps the
/// graph description frame-independent and cacheable (Planning.md §8).
/// <para>
/// The full surface (<c>ctx.Scene</c>, <c>ctx.Camera</c>, <c>ctx.Draw</c>, <c>ctx.Run</c>,
/// <c>ctx.Hover</c> — Planning.md §5) lands as the milestones that need it arrive (real geometry,
/// compute, the app-owned <c>Scene</c>/<c>Camera</c> types). M3 adds only the fullscreen-pass verbs —
/// enough to drive the offscreen→backbuffer reference. Techniques are addressed by <em>name</em> until
/// the describe-layer <c>Technique</c> builder lands (M5); the strings here are that placeholder.
/// </para>
/// </summary>
public interface IRenderContext
{
	/// <summary>Seconds since the previous frame.</summary>
	float DeltaTime { get; }

	/// <summary>
	/// Draw a fullscreen triangle with the named technique, taking no sampled input — for a pass that
	/// computes its output from scratch (a gradient, a procedural fill). Pairs with §5's
	/// <c>.FullScreenTriangle()</c> techniques. The render target is the pass's declared <c>.Writes</c> target.
	/// </summary>
	void FullScreen(string technique);

	/// <summary>
	/// Draw a fullscreen triangle with the named technique, sampling <paramref name="input"/> (bound as a
	/// combined image sampler at set 0, binding 0). This is the engine's blit/upscale — a fullscreen sample,
	/// not a copy — so a nearest-filtered variant is the §9 pixelation step. <paramref name="input"/> must be
	/// a resource the pass declared with <c>.Samples</c>, so the graph derives the read dependency and the
	/// shader-read layout transition.
	/// </summary>
	void FullScreen(string technique, ResourceHandle input);

	/// <summary>
	/// Draw GPU-resident geometry with the named technique (the geometry counterpart to
	/// <see cref="FullScreen(string)"/> — §5's <c>ctx.Draw(drawList, Technique.X)</c>). The mesh is a stable
	/// engine-minted handle; per-frame inputs (camera, transforms) are pulled from the context, not captured,
	/// to keep the pass closure frame-independent (§8). The render target is the pass's declared output.
	/// </summary>
	void Draw(Mesh mesh, string technique);
}
