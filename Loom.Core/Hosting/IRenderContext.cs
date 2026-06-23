using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;

namespace Loom.Core.Hosting;

/// <summary>
/// The per-frame execution context handed to a pass's <c>.Draw</c>/<c>.Dispatch</c> callback. Callbacks
/// <em>pull</em> their inputs from here rather than capturing them, which is what keeps the graph description
/// frame-independent and cacheable. Techniques are addressed by name — the string a pass passes to a draw verb
/// matching a <see cref="IEngine.DefineTechnique"/> registration.
/// </summary>
public interface IRenderContext
{
	/// <summary>Seconds since the previous frame.</summary>
	float DeltaTime { get; }

	/// <summary>Elapsed seconds since startup — per-frame host data for time-driven effects (the GPU particle
	/// fountain animates from it). Pulled, not captured.</summary>
	float Time { get; }

	/// <summary>
	/// The default view to render from this frame (view + projection) — what a draw transforms by when it names no
	/// other. Per-frame data the host supplies; a pass's <c>.Draw</c> closure pulls it from here rather than
	/// capturing it, so the graph stays frame-independent. The engine carries no notion of <em>which</em> viewpoint
	/// this is (a player camera, a chase cam, a fixed view) — that is the app's to decide.
	/// </summary>
	Camera View { get; }

	/// <summary>
	/// The frame's additional named views, keyed by the app's own strings. A draw renders from one by passing it as
	/// the view argument; a pass that consumes view matrices (a shadow test, a reflection) reads one by key. This is
	/// the general channel for every viewpoint beyond <see cref="View"/> — a shadow light, a mirror, a security
	/// camera, each cascade slice of a cascaded shadow map — so the engine names none of them itself. Pulled, not
	/// captured. Empty when the frame draws only from <see cref="View"/>.
	/// </summary>
	IReadOnlyDictionary<string, Camera> Views { get; }

	/// <summary>
	/// True when the engine negotiated an HDR display output (requested via
	/// <c>EngineSettings.HdrOutput</c> and granted by the surface). The terminal tone-map pulls this to map the
	/// scene to display nits (extended-range, highlights above paper white) instead of compressing to SDR; false
	/// means the standard SDR path. Engine-provided frame context, like <see cref="View"/> — pulled, not
	/// captured, so the same graph renders correctly under either output.
	/// </summary>
	bool HdrOutput { get; }

	/// <summary>
	/// Draw a fullscreen triangle with the named technique, sampling zero or more <paramref name="inputs"/>
	/// (each bound, in the order given, as the technique's 0..N-1 sampled input — the backend maps that ordinal
	/// to its own binding model). With no inputs it's a from-scratch pass (a gradient); with
	/// one it's the engine's blit/upscale (a fullscreen sample, not a copy); with several it's deferred lighting and the like. Every input must be a resource the
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
	/// Dispatch a compute technique over a grid of <paramref name="groups"/> workgroups. <paramref name="sampled"/> resources are the technique's 0..S-1 sampled inputs
	/// (the pass's <c>.Samples</c>); <paramref name="storage"/> resources are its S..S+T-1 read-write inputs
	/// (the pass's <c>.ReadWrites</c>) — the graph derives the read-write storage transitions and the
	/// compute↔consumer barriers, and the backend maps the ordinals to its binding model. <paramref name="push"/>
	/// is the same up-to-four-float scalar channel as the fullscreen overload, delivered to the compute stage.
	/// Used by the auto-exposure reduction.
	/// </summary>
	void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage);

	/// <summary>
	/// Submit a single raster draw command described by <paramref name="call"/>. Handles every combination of
	/// geometry source (mesh / skinned override / procedural), instancing (none / CPU span / GPU buffer),
	/// material, view, and extra inputs (uniforms or sampled graph resources). The common case is terse:
	/// <c>ctx.Draw(DrawCall.Create(cube, "gbuffer"))</c>; richer combinations use an object initializer.
	/// Keep-separate verbs: <see cref="FullScreen(string, ReadOnlySpan{ResourceHandle})"/>, <see cref="Dispatch"/>, <see cref="DispatchSkin"/>, and
	/// <see cref="DrawList{TVertex}"/> are conceptually distinct and remain their own methods.
	/// </summary>
	void Draw(in DrawCall call);

	/// <summary>
	/// Compute-skin <paramref name="skinnedMesh"/>'s bind-pose vertices by <paramref name="palette"/> (the
	/// per-frame bone matrices) into <paramref name="output"/> — the GPU pre-skin pass: one dispatch transforms
	/// position/normal/tangent by each vertex's blended bone matrices and writes a plain
	/// <see cref="VertexLayout.MeshStandard"/>-shaped buffer, so the depth/g-buffer/shadow passes draw the skinned
	/// result with their existing techniques. <paramref name="output"/> is a buffer the pass declared with
	/// <c>.ReadWrites</c> (so the graph derives the compute→vertex-input barrier to each skinned
	/// <see cref="Draw(in DrawCall)"/> reader). <paramref name="palette"/> is uploaded to a per-frame buffer
	/// the compute reads; it is a method argument, pulled from app state refreshed before <c>Render</c> (not
	/// captured), so the graph stays frame-independent.
	/// </summary>
	void DispatchSkin(string technique, Mesh skinnedMesh, ReadOnlySpan<System.Numerics.Matrix4x4> palette, BufferHandle output, int vertexCount);

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
	/// context like <see cref="View"/> — pulled, not captured, so the graph stays frame-independent. Empty
	/// (the default) when the host supplied no lists.
	/// </summary>
	IReadOnlyList<(DrawListId Id, DrawListData Data)> DrawLists { get; }
}
