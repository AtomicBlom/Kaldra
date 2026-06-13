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
	/// The camera to render from this frame (view + projection). Per-frame data the host supplies; a pass's
	/// <c>.Draw</c> closure pulls it from here rather than capturing it, so the graph stays frame-independent
	/// (§8). <see cref="Draw"/> uses it to transform geometry into clip space.
	/// </summary>
	Camera Camera { get; }

	/// <summary>
	/// Draw a fullscreen triangle with the named technique, sampling zero or more <paramref name="inputs"/>
	/// (each bound as a combined image sampler at set 0, bindings 0..N-1, in the order given). §5's
	/// <c>ctx.FullScreen(Technique.X, ...args)</c>. With no inputs it's a from-scratch pass (a gradient); with
	/// one it's the engine's blit/upscale (a fullscreen sample, not a copy — a nearest variant is the §9
	/// pixelation step); with several it's deferred lighting and the like. Every input must be a resource the
	/// pass declared with <c>.Samples</c>, so the graph derives the read dependency and shader-read transition.
	/// The render target is the pass's declared <c>.Writes</c> target.
	/// </summary>
	void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs);

	/// <summary>
	/// Draw GPU-resident geometry with the named technique (the geometry counterpart to
	/// <see cref="FullScreen(string)"/> — §5's <c>ctx.Draw(drawList, Technique.X)</c>). The mesh is a stable
	/// engine-minted handle; per-frame inputs (camera, transforms) are pulled from the context, not captured,
	/// to keep the pass closure frame-independent (§8). The render target is the pass's declared output.
	/// </summary>
	void Draw(Mesh mesh, string technique);
}
