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

	/// <summary>Elapsed seconds since startup — per-frame host data for time-driven effects (the GPU particle
	/// fountain animates from it). Pulled, not captured (§8).</summary>
	float Time { get; }

	/// <summary>
	/// The object id currently under the cursor (0 = nothing), supplied per-frame by the host (Planning.md §5
	/// <c>ctx.Hover</c>). The JFA outline sub-graph's seed pass pulls it from here to decide which silhouette
	/// to grow a border around — pulled, not captured, so the graph stays frame-independent (§8).
	/// </summary>
	uint HoverId { get; }

	/// <summary>
	/// The sun's view + projection for this frame (Planning.md §7.2's <c>ctx.Scene.Sun.View</c>). The shadow
	/// pass draws the scene from <em>this</em> view into the shadow map; deferred lighting reuses it to project
	/// world positions into light space for the shadow test. Per-frame host data, pulled not captured (§8).
	/// </summary>
	Camera SunView { get; }

	/// <summary>
	/// The camera to render from this frame (view + projection). Per-frame data the host supplies; a pass's
	/// <c>.Draw</c> closure pulls it from here rather than capturing it, so the graph stays frame-independent
	/// (§8). <see cref="Draw"/> uses it to transform geometry into clip space.
	/// </summary>
	Camera Camera { get; }

	/// <summary>
	/// True when the engine negotiated an HDR display output (Planning.md §3 — requested via
	/// <c>EngineSettings.HdrOutput</c> and granted by the surface). The terminal tone-map pulls this to map the
	/// scene to display nits (extended-range, highlights above paper white) instead of compressing to SDR; false
	/// means the standard SDR path. Engine-provided frame context, like <see cref="Camera"/> — pulled, not
	/// captured (§8), so the same graph renders correctly under either output.
	/// </summary>
	bool HdrOutput { get; }

	/// <summary>
	/// Draw a fullscreen triangle with the named technique, sampling zero or more <paramref name="inputs"/>
	/// (each bound, in the order given, as the technique's 0..N-1 sampled input — the backend maps that ordinal
	/// to its own binding model). §5's
	/// <c>ctx.FullScreen(Technique.X, ...args)</c>. With no inputs it's a from-scratch pass (a gradient); with
	/// one it's the engine's blit/upscale (a fullscreen sample, not a copy — a nearest variant is the §9
	/// pixelation step); with several it's deferred lighting and the like. Every input must be a resource the
	/// pass declared with <c>.Samples</c>, so the graph derives the read dependency and shader-read transition.
	/// The render target is the pass's declared <c>.Writes</c> target.
	/// </summary>
	void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs);

	/// <summary>
	/// As the input-only <c>FullScreen</c> overload, but also supplies a small inline block of
	/// scalar parameters to the fragment stage (a push/root-constant block, up to four floats). This is the
	/// per-pass uniform channel the post passes need — the JFA flood's jump step, the seed's hovered id, an
	/// outline's width/glow — without a full resource binding. The technique must be registered as taking a
	/// fragment-stage constant block (the backend reserves the range in its pipeline layout).
	/// </summary>
	void FullScreen(string technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs);

	/// <summary>
	/// Dispatch a compute technique over a grid of <paramref name="groups"/> workgroups (§5's
	/// <c>ctx.Dispatch</c>). <paramref name="sampled"/> resources are the technique's 0..S-1 sampled inputs
	/// (the pass's <c>.Samples</c>); <paramref name="storage"/> resources are its S..S+T-1 read-write inputs
	/// (the pass's <c>.ReadWrites</c>) — the graph derives the read-write storage transitions and the
	/// compute↔consumer barriers, and the backend maps the ordinals to its binding model. <paramref name="push"/>
	/// is the same up-to-four-float scalar channel as the fullscreen overload, delivered to the compute stage.
	/// Used by the auto-exposure reduction.
	/// </summary>
	void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage);

	/// <summary>
	/// Draw GPU-resident geometry with the named technique (the geometry counterpart to
	/// <see cref="FullScreen(string)"/> — §5's <c>ctx.Draw(drawList, Technique.X)</c>). The mesh is a stable
	/// engine-minted handle; per-frame inputs (camera, transforms) are pulled from the context, not captured,
	/// to keep the pass closure frame-independent (§8). The render target is the pass's declared output.
	/// </summary>
	void Draw(Mesh mesh, string technique);

	/// <summary>
	/// Draw GPU-resident geometry from an explicit <paramref name="view"/> instead of <see cref="Camera"/> —
	/// §7.2's <c>view:</c> parameter. "The scene" and "the scene from the sun" are the same draw with a
	/// different view; the shadow pass uses this with <see cref="SunView"/> to render the depth map.
	/// </summary>
	void Draw(Mesh mesh, string technique, Camera view);

	/// <summary>
	/// Draw <paramref name="count"/> points whose per-vertex data is read straight from <paramref name="buffer"/>
	/// — a GPU storage buffer a compute pass just wrote (GPU particles, §7.6). The buffer must be one the pass
	/// declared with <c>.ReadsVertex</c>, so the graph derives the compute→vertex-input barrier. The technique
	/// transforms by <see cref="Camera"/> (pulled, not captured).
	/// </summary>
	void DrawParticles(ResourceHandle buffer, int count, string technique);

	/// <summary>
	/// Render the host's Dear ImGui draw data into the current pass's target — the screen-space HUD/debug
	/// overlay (Planning.md §6/§9: a native-resolution UI layer on top of the pixelated world). The host owns
	/// the ImGui context and produces the draw data each frame (<c>ImGui.Render()</c>); this verb is the neutral
	/// hand-off so Loom.Core stays UI-agnostic — the backend translates it into GPU work. Used from a pass that
	/// <c>.Blends</c> the backbuffer (the "hud" pass). A no-op if the host hasn't enabled ImGui.
	/// </summary>
	void DrawImGui();
}
