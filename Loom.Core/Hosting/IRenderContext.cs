using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;

namespace Loom.Core.Hosting;

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
	/// The sun's per-cascade views this frame (M15-3 — cascaded shadow maps), nearest cascade first. A per-cascade
	/// depth pass draws the scene from <c>Cascades[i]</c> (as <see cref="SunView"/> drives the single shadow pass);
	/// the deferred lighting pass packs these into its uniform buffer and selects a cascade per pixel by view
	/// distance (<see cref="CascadeSplits"/>). Empty when the demo uses the single-map <see cref="SunView"/> path.
	/// Per-frame host data, pulled not captured (§8).
	/// </summary>
	IReadOnlyList<Camera> Cascades { get; }

	/// <summary>The far view-space distance each entry of <see cref="Cascades"/> covers — the split boundaries the lighting pass selects a cascade against (M15-3).</summary>
	IReadOnlyList<float> CascadeSplits { get; }

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
	/// Draw GPU-resident geometry with a <paramref name="material"/> whose textures fill the technique's
	/// reflected sampler slots, in order (texture 0 → binding 0, …) — the geometry-with-textures counterpart to
	/// <see cref="Draw(Mesh, string)"/>. The <paramref name="technique"/> still selects the pipeline; a technique
	/// that reflects no samplers (depth-only/shadow) ignores the material, so the same material draws into a
	/// G-buffer pass and a shadow pass unchanged. The material must supply at least as many textures as the
	/// technique samples (extras are ignored). Per-frame inputs are pulled from the context, not captured (§8).
	/// </summary>
	void Draw(Mesh mesh, Material material, string technique);

	/// <summary>
	/// As <see cref="Draw(Mesh, Material, string)"/>, but from an explicit <paramref name="view"/> instead of
	/// <see cref="Camera"/> (the §7.2 <c>view:</c> parameter) — the textured draw used when a pass renders from a
	/// non-camera viewpoint.
	/// </summary>
	void Draw(Mesh mesh, Material material, string technique, Camera view);

	/// <summary>
	/// Draw <paramref name="count"/> points whose per-vertex data is read straight from <paramref name="buffer"/>
	/// — a GPU storage buffer a compute pass just wrote (GPU particles, §7.6). The buffer must be one the pass
	/// declared with <c>.ReadsVertex</c>, so the graph derives the compute→vertex-input barrier. The technique
	/// transforms by <see cref="Camera"/> (pulled, not captured).
	/// </summary>
	void DrawParticles(ResourceHandle buffer, int count, string technique);

	/// <summary>
	/// As <see cref="DrawParticles(ResourceHandle, int, string)"/>, but binds <paramref name="material"/>'s
	/// textures into the technique's reflected sampler slots (texture 0 → binding 0, …) — the textured-particle
	/// path (M14): a sprite-sheet atlas the render shader samples for sub-UV frames + colour-over-life. The
	/// particle stream still rides in <paramref name="buffer"/> (a <c>.ReadsVertex</c> resource); the material
	/// only adds the sampled atlas, exactly as <see cref="Draw(Mesh, Material, string)"/> adds it to a mesh draw.
	/// </summary>
	void DrawParticles(ResourceHandle buffer, int count, Material material, string technique);

	/// <summary>
	/// Draw <paramref name="mesh"/> once per element of <paramref name="instances"/> (M12 — GPU instancing,
	/// CPU-driven). The engine uploads the span to a per-frame host-visible instance buffer and binds it as the
	/// technique's per-instance stream (binding 1). <paramref name="technique"/> must declare a per-instance
	/// <see cref="VertexLayout"/> whose <see cref="VertexLayout.Stride"/> equals <c>sizeof(T)</c> (e.g. a
	/// <see cref="System.Numerics.Matrix4x4"/> per instance for <see cref="VertexLayout.Transform"/>). This is
	/// the path the CPU frustum cull feeds: the app compacts the visible transforms and hands them here. No
	/// graph edge — the data is CPU-sourced, so no <c>.ReadsVertex</c> is needed.
	/// </summary>
	void DrawInstanced<T>(Mesh mesh, string technique, ReadOnlySpan<T> instances) where T : unmanaged;

	/// <summary>
	/// As <see cref="DrawInstanced{T}(Mesh, string, ReadOnlySpan{T})"/>, but binds <paramref name="material"/>'s
	/// textures into the technique's reflected sampler slots (texture 0 → binding 0, …) — the textured-instanced
	/// path (M14 sprite/tile batching): one draw places <paramref name="instances"/>-many quads, each sampling the
	/// shared atlas. The instance stream and the material are independent — the stream carries per-quad transform +
	/// uv-rect + tint, the material supplies the atlas every instance shares. This is
	/// <see cref="Draw(Mesh, Material, string)"/> + an instance stream; a technique that reflects no samplers
	/// ignores the material.
	/// </summary>
	void DrawInstanced<T>(Mesh mesh, Material material, string technique, ReadOnlySpan<T> instances) where T : unmanaged;

	/// <summary>
	/// As <see cref="DrawInstanced{T}(Mesh, Material, string, ReadOnlySpan{T})"/>, but also hands the technique a
	/// per-frame <paramref name="uniforms"/> block (M15-0 — the uniform seam): the engine stages it into a
	/// per-frame uniform buffer and binds it as the technique's constant buffer, visible to the vertex stage. The
	/// foliage path uses it for wind (time + direction + strength) read in the vertex shader to sway each blade,
	/// while <paramref name="material"/> supplies the alpha-test cutout texture. The per-instance MVP still comes
	/// from <see cref="Camera"/>. Per-frame inputs are pulled from the context, not captured (§8).
	/// </summary>
	void DrawInstanced<T>(Mesh mesh, Material material, string technique, ReadOnlySpan<T> instances, ReadOnlySpan<float> uniforms) where T : unmanaged;

	/// <summary>
	/// As <see cref="DrawInstanced{T}(Mesh, string, ReadOnlySpan{T})"/>, but the technique also samples the graph
	/// resources in <paramref name="sampled"/> (each bound in order as sampled input 0..N-1, exactly as
	/// <see cref="FullScreen(string, ReadOnlySpan{ResourceHandle})"/> binds them) — the instanced-draw-that-reads-
	/// the-G-buffer path (M14 2D lighting): each instance is one additive light volume, and the fragment samples the
	/// G-buffer normal/albedo at the covered pixel to shade it. Every entry must be a resource the pass declared
	/// with <c>.Samples</c>, so the graph derives the read dependency and shader-read transition.
	/// </summary>
	void DrawInstanced<T>(Mesh mesh, string technique, ReadOnlySpan<T> instances, ReadOnlySpan<ResourceHandle> sampled) where T : unmanaged;

	/// <summary>
	/// Draw <paramref name="mesh"/> <paramref name="count"/> times, reading the per-instance stream from
	/// <paramref name="instances"/> — a GPU storage buffer a compute pass filled (M12 — GPU-driven instancing,
	/// the foliage/placement path). The buffer must be one the pass declared with <c>.ReadsVertex</c>, so the
	/// graph derives the compute→vertex-input barrier exactly as for GPU particles. The technique must declare a
	/// per-instance <see cref="VertexLayout"/>.
	/// </summary>
	void DrawInstanced(Mesh mesh, string technique, BufferHandle instances, int count);

	/// <summary>
	/// Compute-skin <paramref name="skinnedMesh"/>'s bind-pose vertices by <paramref name="palette"/> (the
	/// per-frame bone matrices) into <paramref name="output"/> — the GPU pre-skin pass (M15): one dispatch
	/// transforms position/normal/tangent by each vertex's blended bone matrices and writes a plain
	/// <see cref="VertexLayout.MeshStandard"/>-shaped buffer, so the depth/g-buffer/shadow passes draw the skinned
	/// result with their existing techniques. <paramref name="output"/> is a buffer the pass declared with
	/// <c>.ReadWrites</c> (so the graph derives the compute→vertex-input barrier to each <see cref="DrawSkinned"/>
	/// reader). <paramref name="palette"/> is uploaded to a per-frame buffer the compute reads; it is a method
	/// argument, pulled from app state refreshed before <c>Render</c> (not captured — §8). One pre-skin feeds
	/// every cascade/depth/g-buffer pass that reads <paramref name="output"/>, so a skinned mesh is skinned once.
	/// </summary>
	void DispatchSkin(string technique, Mesh skinnedMesh, ReadOnlySpan<System.Numerics.Matrix4x4> palette, BufferHandle output, int vertexCount);

	/// <summary>
	/// Draw <paramref name="mesh"/>'s index buffer with its per-vertex stream sourced from
	/// <paramref name="skinnedVertices"/> — a buffer a <see cref="DispatchSkin"/> just wrote (M15) — instead of the
	/// mesh's own (bind-pose) vertex buffer. The buffer must be one the pass declared with <c>.ReadsVertex</c>, so
	/// the graph derives the compute→vertex barrier. The technique consumes the <see cref="VertexLayout.MeshStandard"/>
	/// layout (the skinned output is plain pos/normal/uv/tangent), so existing mesh techniques work unchanged.
	/// Unlike <see cref="DrawParticles"/>, this is an indexed per-vertex draw using the mesh's index count.
	/// </summary>
	void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, string technique);

	/// <summary>As <see cref="DrawSkinned(Mesh, BufferHandle, string)"/>, from an explicit <paramref name="view"/> (e.g. a shadow cascade's sun view) instead of <see cref="Camera"/>.</summary>
	void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, string technique, Camera view);

	/// <summary>As <see cref="DrawSkinned(Mesh, BufferHandle, string)"/>, binding <paramref name="material"/>'s textures into the technique's reflected sampler slots (the textured skinned-character g-buffer draw).</summary>
	void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, Material material, string technique);

	/// <summary>As <see cref="DrawSkinned(Mesh, BufferHandle, Material, string)"/>, from an explicit <paramref name="view"/>.</summary>
	void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, Material material, string technique, Camera view);

	/// <summary>
	/// Submit a per-frame, variable-size indexed vertex stream as a batched draw list into the current pass —
	/// the backend-neutral verb behind overlay/UI-style rendering (a draw list is just vertices + 16-bit indices +
	/// a list of <see cref="DrawCommand"/>s, each a clip rect + a texture + an index range). The geometry is staged
	/// fresh each frame, so the spans may differ every call; the engine owns the upload. <paramref name="technique"/>
	/// is a normal <c>DefineTechnique</c> name whose layout matches <typeparamref name="TVertex"/> and that declares
	/// one sampler slot; each command binds <paramref name="textures"/>[<see cref="DrawCommand.TextureIndex"/>] into
	/// it. <paramref name="push"/> is the shared per-list push block; the per-command <see cref="DrawCommand.Tag"/>
	/// rewrites the slot right after it (a list-push technique, <see cref="Materials.TechniqueDescriptor.HasListPush"/>).
	/// Several calls in one pass append rather than overwrite.
	/// </summary>
	void DrawList<TVertex>(
		string technique,
		ReadOnlySpan<Texture> textures,
		ReadOnlySpan<TVertex> vertices,
		ReadOnlySpan<ushort> indices,
		ReadOnlySpan<DrawCommand> commands,
		ReadOnlySpan<float> push) where TVertex : unmanaged;

	/// <summary>
	/// Resolve an inspectable graph resource (an <see cref="InspectableResource.Id"/> from
	/// <see cref="IEngine.InspectableResources"/>) to a <see cref="Texture"/> bindable in a
	/// <see cref="DrawList{TVertex}"/> this frame — the neutral hook a debug overlay uses to preview a live transient
	/// image (the resource viewer). Returns null if the id is not an inspectable resource or inspection was not enabled
	/// (<see cref="IEngine.ResourceInspectionEnabled"/>). The handle samples the resource's current contents and is valid
	/// for the current frame's draw lists only; with inspection enabled the engine keeps the resource alive and in a
	/// sampleable layout through the final pass, so binding it is safe.
	/// </summary>
	Texture? ResolveInspectable(int resourceId);

	/// <summary>
	/// This frame's draw lists, each <see cref="DrawListId"/> paired with the neutral <see cref="DrawListData"/> a
	/// producer composited for it — the per-frame channel behind overlay/UI-style rendering. The host fills it via
	/// <see cref="FrameInputs.DrawLists"/> off the render graph; a pass closure pulls a list out by id and submits it
	/// with <see cref="DrawList{TVertex}"/> (the UI does this through <c>SUIsei</c>'s painter). Engine-provided frame
	/// context like <see cref="Camera"/> — pulled, not captured (§8), so the graph stays frame-independent. Empty
	/// (the default) when the host supplied no lists.
	/// </summary>
	IReadOnlyList<(DrawListId Id, DrawListData Data)> DrawLists { get; }
}
