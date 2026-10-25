using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;

namespace Loom.Core.Hosting;

/// <summary>
/// A live rendering engine — the backend-neutral front door the host codes against. It owns
/// the long-lived device and the pipeline cache; the <em>active graph</em> is swappable (<see cref="SetGraph"/>),
/// so a structural change re-derives the schedule/barriers/aliasing while the device and cache are reused.
/// Obtain one from
/// <see cref="IRenderBackend.Create"/> via <see cref="Backends.Available"/>; the host names no
/// graphics-API type to drive it. Dispose it to tear the device down (drains the GPU first).
/// </summary>
public interface IEngine : IEngineClipSpace, IDisposable
{
	/// <summary>The backend that created this engine (<see cref="IRenderBackend.Name"/>, e.g. <c>"Vulkan"</c>).</summary>
	string BackendName { get; }

	/// <summary>A human-readable name of the GPU the engine runs on, for HUDs and logs.</summary>
	string DeviceName { get; }

	/// <summary>
	/// What the device behind this engine can do. Immutable for the engine's lifetime, and consulted by
	/// <see cref="SetGraph"/>, which rejects a graph needing something absent rather than degrading silently — so a
	/// host may either ask first and declare accordingly, or declare and let the throw name the problem.
	/// </summary>
	EngineCapabilities Capabilities { get; }

	/// <summary>
	/// Register a technique under a logical <paramref name="name"/> the pass closures reference
	/// (<c>ctx.FullScreen(name, …)</c>). Idempotent per name. Call before the graph draws with it; the backend
	/// resolves the shader bytecode and bakes pipelines lazily, cached for the engine's lifetime.
	/// </summary>
	void DefineTechnique(string name, TechniqueDescriptor descriptor);

	/// <summary>
	/// Upload geometry once and get a stable <see cref="Mesh"/> handle that passes draw via <c>ctx.Draw</c>.
	/// Call between frames — after creation, or before any <see cref="Render"/> — never from a pass callback, where it
	/// throws (see <see cref="CreateTexture"/>). The handle lives until <see cref="ReleaseMesh"/> or the engine's end
	/// (it survives graph swaps). Uses the canonical <see cref="VertexLayout.MeshStandard"/> layout.
	/// </summary>
	Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices);

	/// <summary>Async counterpart to <see cref="CreateMesh(ReadOnlySpan{Vertex}, ReadOnlySpan{uint})"/> — see <see cref="CreateTextureAsync"/>'s remarks (the same "any calling thread, one GPU-servicing thread" contract applies to every <c>*Async</c> creation method, not just textures).</summary>
	Task<Mesh> CreateMeshAsync(Vertex[] vertices, uint[] indices);

	/// <summary>
	/// Upload geometry with an explicit <paramref name="layout"/>. The
	/// vertex element type <typeparamref name="T"/> must be laid out to match <paramref name="layout"/>
	/// (<c>sizeof(T) == layout.Stride</c>), and the technique the mesh is drawn with must declare the same
	/// per-vertex layout. The neutral path for skinned meshes, tangents, and glTF imports.
	/// </summary>
	Mesh CreateMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices) where T : unmanaged;

	/// <summary>
	/// Upload a skinned mesh's bind-pose vertices (the <see cref="VertexLayout.MeshSkinned"/> layout) once and get
	/// a <see cref="Mesh"/> handle. Unlike <see cref="CreateMesh{T}"/>, the bind-pose vertex buffer is made
	/// readable by a compute shader (the pre-skin pass reads it as a structured buffer), and the returned mesh's
	/// index buffer is what <see cref="IRenderContext.Draw"/> draws — its
	/// vertex stream comes from the compute's skinned output, not this buffer. The vertices' bone indices
	/// reference the model's skeleton (the palette <see cref="IRenderContext.DispatchSkin"/> applies).
	/// </summary>
	Mesh CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices);

	/// <summary>Async counterpart to <see cref="CreateSkinnedMesh"/> — see <see cref="CreateTextureAsync"/>'s remarks.</summary>
	Task<Mesh> CreateSkinnedMeshAsync(SkinnedVertex[] vertices, uint[] indices);

	/// <summary>
	/// Free a mesh's GPU geometry — the counterpart of <see cref="CreateMesh(ReadOnlySpan{Vertex}, ReadOnlySpan{uint})"/>
	/// for an app whose geometry does not live as long as the engine (a level, a generated world, a streamed chunk). The
	/// handle is dead from this call: drawing it, or releasing it again, throws rather than drawing whatever happens to
	/// occupy the memory. The buffers themselves are freed once every frame that may still read them has finished on the
	/// GPU, so a mesh can be released the frame after its last draw without waiting for the GPU first. Ids are not
	/// reused, so a stale handle always fails loudly. Call it between frames: from inside a pass callback it throws,
	/// because the frame being recorded may already have drawn the mesh.
	/// </summary>
	void ReleaseMesh(Mesh mesh);

	/// <summary>
	/// Upload a texture (and its mip chain) once and get a stable <see cref="Texture"/> handle to bundle into a
	/// <see cref="Material"/> or sample from a draw list. Takes neutral <see cref="TextureData"/> — no graphics-API
	/// type crosses the boundary; the backend's texture store decodes nothing and just uploads the levels. The handle
	/// lives until <see cref="ReleaseTexture"/> or the engine's end (it survives graph swaps).
	/// <para>
	/// Call it between frames, never from a pass callback, where it throws. An upload is submitted ahead of the next
	/// frame, so a texture created while a frame is recording would be sampled by that frame before its pixels arrive.
	/// A producer that discovers what it needs while drawing — a UI painter meeting a new image — resolves it in a step
	/// of its own before <see cref="Render"/>.
	/// </para>
	/// </summary>
	Texture CreateTexture(TextureData data);

	/// <summary>
	/// Queue a texture upload and return a <see cref="Task{Texture}"/> that completes once <see cref="ServicePendingUploads"/>
	/// drains it — safe to call from any thread (e.g. a background decode, or a demo's <c>PreloadAsync</c> racing
	/// another demo's), unlike <see cref="CreateTexture"/>, which is not safe to call concurrently with any other
	/// GPU-creating call. The actual GPU upload still only ever runs on whichever single thread calls
	/// <see cref="ServicePendingUploads"/>; this only decouples requesting an upload from performing it. Use for
	/// bytes fetched/decoded off the calling thread; keep using <see cref="CreateTexture"/> directly for
	/// already-in-memory data (e.g. a procedurally generated texture) created from code that's already on the one
	/// thread driving <see cref="ServicePendingUploads"/> (e.g. a demo's synchronous <c>Build</c>). Like
	/// <see cref="CreateTexture"/>, it throws when called from a pass callback on the render thread; another thread
	/// may queue an upload while a frame records.
	/// </summary>
	Task<Texture> CreateTextureAsync(TextureData data);

	/// <summary>
	/// Free a texture's GPU image — the counterpart of <see cref="CreateTexture"/> for a texture that does not live as
	/// long as the engine (an image replaced when its source changes, a level's art). The handle is dead from this call:
	/// a draw list that samples it, a material built from it afterwards, or releasing it again throws rather than
	/// sampling whatever happens to occupy the memory. The image is destroyed once every frame that may still read it
	/// has finished on the GPU, so a texture can be released the frame after its last draw without waiting for the GPU
	/// first. Ids are not reused, so a stale handle always fails loudly.
	/// <para>
	/// A texture bundled into a <see cref="Material"/> cannot be released: a material keeps its textures for as long as
	/// it lives, which is the engine's lifetime, so releasing one of them throws and names the material. Call it between
	/// frames: from inside a pass callback it throws, because the frame being recorded may already sample it.
	/// </para>
	/// </summary>
	void ReleaseTexture(Texture texture);

	/// <summary>
	/// Run every GPU creation call queued by a <c>*Async</c> method (<see cref="CreateMeshAsync"/>,
	/// <see cref="CreateSkinnedMeshAsync"/>, <see cref="CreateTextureAsync"/>, <see cref="CreateMaterialAsync(Texture[])"/>)
	/// since the last call, completing each one's <see cref="Task"/>. Every backend calls this once per frame from
	/// <see cref="Render"/>; call it explicitly (in a small pump loop) while awaiting an async load before any frame
	/// loop is running yet — e.g. at startup, or the first demo a host builds. Draining every backend's GPU-creating
	/// calls from this single method, on whichever one thread calls it, is what makes it safe for several demos'
	/// <c>PreloadAsync</c> to genuinely run concurrently (their I/O overlaps; their GPU calls never do).
	/// </summary>
	void ServicePendingUploads();

	/// <summary>
	/// Bundle textures into a stable <see cref="Material"/> handle, in the order a material technique's shader
	/// binds them (texture 0 → sampler slot 0, …). Passed to <see cref="IRenderContext.Draw"/>;
	/// the technique still picks the pipeline, so a depth-only/shadow pass (no reflected samplers) ignores the
	/// material. Call up front like <see cref="CreateMesh"/>; the handle lives for the engine's lifetime.
	/// </summary>
	Material CreateMaterial(params ReadOnlySpan<Texture> textures);

	/// <summary>Async counterpart to <see cref="CreateMaterial(ReadOnlySpan{Texture})"/> — see <see cref="CreateTextureAsync"/>'s remarks.</summary>
	Task<Material> CreateMaterialAsync(params Texture[] textures);

	/// <summary>Async counterpart to <see cref="CreateMaterial(string, ReadOnlySpan{Texture})"/> — see <see cref="CreateTextureAsync"/>'s remarks.</summary>
	Task<Material> CreateMaterialAsync(string technique, params Texture[] textures);

	/// <summary>
	/// Async counterpart to <see cref="CreateMaterial{T}(string, in T, ReadOnlySpan{Texture})"/> — see
	/// <see cref="CreateTextureAsync"/>'s remarks. Takes <paramref name="parameters"/> by value, not by <c>in</c>:
	/// the call is deferred onto the servicing thread, so the block is captured rather than referenced.
	/// </summary>
	Task<Material> CreateMaterialAsync<T>(string technique, T parameters, params Texture[] textures) where T : unmanaged;

	/// <summary>
	/// Bundle textures into a <see cref="Material"/> <em>for a named technique</em>, so the pairing is recorded and
	/// checked rather than discovered at draw time. Otherwise identical to
	/// <see cref="CreateMaterial(ReadOnlySpan{Texture})"/>.
	/// <para>
	/// Recording the pairing is what makes a shader edit fail loudly: because <see cref="DefineTechnique"/> is
	/// replayed on hot reload, adding or reordering a sampler in the shader is caught at the reload, against every
	/// material already built for that technique — instead of silently sampling the wrong texture.
	/// </para>
	/// </summary>
	/// <param name="technique">The technique this material is built for; it must already be defined.</param>
	/// <param name="textures">The textures, in the order the technique's shader binds them.</param>
	Material CreateMaterial(string technique, params ReadOnlySpan<Texture> textures);

	/// <summary>
	/// Bundle textures plus a per-material parameter block into a <see cref="Material"/> for a named technique. The
	/// technique's shader opts into the parameter channel by declaring a constant buffer named <c>loomMaterial</c>;
	/// <typeparamref name="T"/> is the app's own struct matching that block, and <c>sizeof(T)</c> must equal the size
	/// reflection reports for it. The engine keeps a CPU copy and stages it into the per-frame uniform ring on every
	/// draw that uses this material, so there is no new GPU object and no frames-in-flight hazard.
	/// <para>
	/// The engine has no material model: <typeparamref name="T"/> is whatever the shader declares. Lay it out so the
	/// shader's constant-buffer packing agrees on every backend — 16-byte-aligned members are the safe rule.
	/// </para>
	/// </summary>
	Material CreateMaterial<T>(string technique, in T parameters, params ReadOnlySpan<Texture> textures) where T : unmanaged;

	/// <summary>
	/// Overwrite a material's parameter block — the material counterpart of
	/// <see cref="Write{T}(ConstantsHandle, in T)"/>, for a tint or roughness that changes at runtime. Takes effect on
	/// the next draw that uses the material; the graph is untouched, so nothing rebuilds.
	/// <typeparamref name="T"/> must be the same size as the block the material was created with.
	/// </summary>
	void Write<T>(Material material, in T parameters) where T : unmanaged;

	/// <summary>
	/// What a technique expects a material to supply — its reflected texture count and parameter-block size. Useful
	/// for building a material generically (an asset importer sizing its upload) and for diagnostics. Throws if the
	/// technique is not defined.
	/// </summary>
	MaterialLayout MaterialLayoutOf(string technique);

	/// <summary>
	/// Mint a draw-list stream id, unique on this engine, for a producer that submits a stable
	/// <see cref="DrawListIdentity"/> — one whose geometry a backend keeps across frames. Each producer allocates its
	/// own, so no two can submit the same stream: two UI hosts that each number their surfaces from zero would otherwise
	/// both claim stream 0, and either fail as one stream submitted twice in a frame or, drawn on alternate frames at
	/// matching revisions, show each other's geometry. A stable identity whose stream was not minted here is refused by
	/// <see cref="IRenderContext.DrawList{TVertex}"/>. Safe to call from any thread.
	/// </summary>
	int AllocateDrawListStream();

	/// <summary>
	/// Make <paramref name="graph"/> the active graph: derive its schedule, barriers, and transient-memory plan
	/// and prepare per-graph GPU resources, reusing the device and pipeline cache. Call once before the first
	/// frame; calling it again swaps the active graph at runtime (the engine drains the GPU, releases the old
	/// graph's transients, and binds the new one) — the basis for runtime demo switching.
	/// <para>
	/// The GPU half is cached by <see cref="RenderGraph.Hash"/>: a graph structurally equal to the active one reuses
	/// its transients, readback buffers, and cross-frame <see cref="ResourceKind.Persistent"/> contents instead of
	/// draining and rebuilding them, so re-setting an equivalent graph costs only the (CPU-only) re-derivation and
	/// causes no frame hitch. That makes it safe to rebuild and set the graph whenever the structure might have
	/// changed, rather than tracking structural changes by hand.
	/// </para>
	/// </summary>
	void SetGraph(RenderGraph graph);

	/// <summary>
	/// Write <paramref name="value"/> into an app-writable parameter buffer (<see cref="GraphBuilder.Constants{T}"/>)
	/// of the active graph. The value reaches the technique that <c>.Reads</c> the buffer on the next frame(s); the
	/// last write persists until overwritten, so write every frame for animated params or only on change for static
	/// ones. <typeparamref name="T"/> must match the buffer's declared size (and the shader's uniform-block layout).
	/// The graph captures only the stable handle, so this never rebuilds it — it just updates the bytes the shader reads.
	/// </summary>
	void Write<T>(ConstantsHandle handle, in T value) where T : unmanaged;

	/// <summary>
	/// Write <paramref name="values"/> into an app-writable storage buffer (<see cref="GraphBuilder.Storage{T}"/>)
	/// of the active graph — the array counterpart to the parameter-buffer <see cref="Write{T}(ConstantsHandle, in T)"/>.
	/// The data reaches the technique that <c>.Reads</c> the buffer on the next frame(s); the last write persists, so
	/// write every frame for animated data or only on change for static data. The span's byte length must not exceed
	/// the buffer's declared size; <typeparamref name="T"/> must match the shader's structured-buffer element layout.
	/// The graph captures only the stable handle, so this never rebuilds it.
	/// </summary>
	void Write<T>(StorageBufferHandle handle, ReadOnlySpan<T> values) where T : unmanaged;

	/// <summary>Render one frame of the active graph from <paramref name="inputs"/>.</summary>
	void Render(FrameInputs inputs);

	/// <summary>Notify the engine the output surface resized to <paramref name="width"/>×<paramref name="height"/> pixels (drives swapchain + relative-domain recreation).</summary>
	void Resize(int width, int height);

	/// <summary>
	/// Read back the CPU-side copy of a <c>g.Readback</c> target's contents — the GPU→CPU edge.
	/// Returns the resource's texels tightly packed in its declared format (no row padding, the same byte layout on
	/// every backend), so the app decodes whatever format it chose.
	/// The bytes are a frame or two old (the latency is real) and empty before the first frame populates the target
	/// or when <paramref name="handle"/> is not one the graph declared with <c>g.Readback</c>. The copy is from a
	/// frame the GPU has finished, so it never races in-flight work.
	/// </summary>
	ReadOnlySpan<byte> Read(ResourceHandle handle);

	/// <summary>Capture the next presented frame to a PNG at <paramref name="path"/> (debug/validation). Synchronous on that frame.</summary>
	void RequestCapture(string path);

	/// <summary>
	/// Enable per-pass GPU timestamp collection. Off by default; cheap to toggle live (an
	/// ImGui HUD checkbox) — no graph rebuild. A device that cannot time keeps this false however it is set; ask
	/// <see cref="EngineCapabilities.GpuTiming"/> rather than writing <c>true</c> and reading the value back. When
	/// off, <see cref="LastFrameTimings"/> stays empty and the per-frame cost is nil.
	/// </summary>
	bool GpuTimingEnabled { get; set; }

	/// <summary>
	/// Per-pass GPU timings from the most recently <em>completed</em> frame (a frame or two stale, like
	/// <see cref="Read"/> — timestamps are read back after the slot's fence). In execution order; the entries
	/// align with the active graph's leaf passes. Empty when timing is disabled or no timed frame has retired.
	/// </summary>
	IReadOnlyList<PassTiming> LastFrameTimings { get; }

	/// <summary>
	/// Total GPU time for the most recent completed frame, in milliseconds — the span from the first pass's begin
	/// timestamp to the last pass's end timestamp (so it includes inter-pass gaps the per-pass sum misses). 0 when
	/// timing is disabled or no timed frame has retired.
	/// </summary>
	double LastFrameGpuMilliseconds { get; }

	/// <summary>
	/// Enable previewing the active graph's intermediate images (the resource viewer). When set, <see cref="SetGraph"/>
	/// populates <see cref="InspectableResources"/> and keeps each colour/depth resource alive and in a sampleable
	/// layout through the final pass (it injects the read edges the Execute layer needs), so a debug overlay can bind a
	/// resource via <see cref="IRenderContext.ResolveInspectable"/>. Off by default — a graph that never inspects keeps
	/// its exact aliasing and pays nothing. Set it before <see cref="SetGraph"/>; a change takes effect on the next swap.
	/// </summary>
	bool ResourceInspectionEnabled { get; set; }

	/// <summary>
	/// The active graph's image resources the debug viewer can preview. Empty until
	/// <see cref="SetGraph"/>, rebuilt on each swap, and empty unless <see cref="ResourceInspectionEnabled"/>. Neutral types.
	/// </summary>
	IReadOnlyList<InspectableResource> InspectableResources { get; }

	/// <summary>Block until the GPU is idle — call before disposing host-owned resources the engine may still be using.</summary>
	void WaitIdle();
}
