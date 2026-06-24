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
/// <see cref="IRenderBackend.Create"/> via <see cref="Engine.AvailableEngines"/>; the host names no
/// graphics-API type to drive it. Dispose it to tear the device down (drains the GPU first).
/// </summary>
public interface IEngine : IDisposable
{
	/// <summary>The backend that created this engine (<see cref="IRenderBackend.Name"/>, e.g. <c>"Vulkan"</c>).</summary>
	string BackendName { get; }

	/// <summary>A human-readable name of the GPU the engine runs on, for HUDs and logs.</summary>
	string DeviceName { get; }

	/// <summary>
	/// The engine's clip-space convention as a neutral matrix to compose into a host-built view-projection before
	/// handing it to a shader (e.g. through a <see cref="GraphBuilder.Constants{T}"/> buffer the pass reads): the app
	/// computes <c>view * projection * ClipSpaceCorrection</c> so the result lands in the engine's clip space on
	/// every backend, without the host ever naming a backend's NDC convention. A right-handed, +Y-up projection
	/// composes correctly on both Vulkan and DirectX12.
	/// </summary>
	System.Numerics.Matrix4x4 ClipSpaceCorrection { get; }

	/// <summary>
	/// Register a technique under a logical <paramref name="name"/> the pass closures reference
	/// (<c>ctx.FullScreen(name, …)</c>). Idempotent per name. Call before the graph draws with it; the backend
	/// resolves the shader bytecode and bakes pipelines lazily, cached for the engine's lifetime.
	/// </summary>
	void DefineTechnique(string name, TechniqueDescriptor descriptor);

	/// <summary>
	/// Upload geometry once and get a stable <see cref="Mesh"/> handle that passes draw via <c>ctx.Draw</c>.
	/// Call after creation, before the first <see cref="Render"/>; the handle lives for the engine's lifetime
	/// (it survives graph swaps). Uses the canonical <see cref="VertexLayout.MeshStandard"/> layout.
	/// </summary>
	Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices);

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

	/// <summary>
	/// Upload a texture (and its mip chain) once and get a stable <see cref="Texture"/> handle to bundle into a
	/// <see cref="Material"/>. Takes neutral <see cref="TextureData"/> — no graphics-API type crosses the
	/// boundary; the backend's texture store decodes nothing and just uploads the levels. Call after creation,
	/// before the first <see cref="Render"/>; the handle lives for the engine's lifetime (survives graph swaps).
	/// </summary>
	Texture CreateTexture(TextureData data);

	/// <summary>
	/// Bundle textures into a stable <see cref="Material"/> handle, in the order a material technique's shader
	/// binds them (texture 0 → sampler slot 0, …). Passed to <see cref="IRenderContext.Draw"/>;
	/// the technique still picks the pipeline, so a depth-only/shadow pass (no reflected samplers) ignores the
	/// material. Call up front like <see cref="CreateMesh"/>; the handle lives for the engine's lifetime.
	/// </summary>
	Material CreateMaterial(params ReadOnlySpan<Texture> textures);

	/// <summary>
	/// Make <paramref name="graph"/> the active graph: derive its schedule, barriers, and transient-memory plan
	/// and prepare per-graph GPU resources, reusing the device and pipeline cache. Call once before the first
	/// frame; calling it again swaps the active graph at runtime (the engine drains the GPU, releases the old
	/// graph's transients, and binds the new one) — the basis for runtime demo switching.
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
	/// ImGui HUD checkbox) — no graph rebuild. A backend that cannot time (e.g. a queue with no timestamp support)
	/// keeps this false. When off, <see cref="LastFrameTimings"/> stays empty and the per-frame cost is nil.
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
