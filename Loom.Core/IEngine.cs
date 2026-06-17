namespace Loom.Core;

/// <summary>
/// A live rendering engine — the backend-neutral front door the host codes against (Planning.md §5). It owns
/// the long-lived device and the pipeline cache; the <em>active graph</em> is swappable (<see cref="SetGraph"/>),
/// so a structural change re-derives the schedule/barriers/aliasing while the device and cache are reused
/// (Planning.md §8's "structural changes rebuild the baked graph — cheap and cached"). Obtain one from
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
	/// Register a technique under a logical <paramref name="name"/> the pass closures reference
	/// (<c>ctx.FullScreen(name, …)</c>). Idempotent per name. Call before the graph draws with it; the backend
	/// resolves the shader bytecode and bakes pipelines lazily, cached for the engine's lifetime.
	/// </summary>
	void DefineTechnique(string name, TechniqueDescriptor descriptor);

	/// <summary>
	/// Upload geometry once and get a stable <see cref="Mesh"/> handle that passes draw via <c>ctx.Draw</c>.
	/// Call after creation, before the first <see cref="Render"/>; the handle lives for the engine's lifetime
	/// (it survives graph swaps).
	/// </summary>
	Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices);

	/// <summary>
	/// Upload a texture (and its mip chain) once and get a stable <see cref="Texture"/> handle to bundle into a
	/// <see cref="Material"/>. Takes neutral <see cref="TextureData"/> — no graphics-API type crosses the
	/// boundary; the backend's texture store decodes nothing and just uploads the levels. Call after creation,
	/// before the first <see cref="Render"/>; the handle lives for the engine's lifetime (survives graph swaps).
	/// </summary>
	Texture CreateTexture(TextureData data);

	/// <summary>
	/// Bundle textures into a stable <see cref="Material"/> handle, in the order a material technique's shader
	/// binds them (texture 0 → sampler slot 0, …). Passed to <see cref="IRenderContext.Draw(Mesh, Material, string)"/>;
	/// the technique still picks the pipeline, so a depth-only/shadow pass (no reflected samplers) ignores the
	/// material. Call up front like <see cref="CreateMesh"/>; the handle lives for the engine's lifetime.
	/// </summary>
	Material CreateMaterial(params ReadOnlySpan<Texture> textures);

	/// <summary>
	/// Make <paramref name="graph"/> the active graph: derive its schedule, barriers, and transient-memory plan
	/// and prepare per-graph GPU resources, reusing the device and pipeline cache. Call once before the first
	/// frame; calling it again swaps the active graph at runtime (the engine drains the GPU, releases the old
	/// graph's transients, and binds the new one) — the basis for runtime demo switching (M7-B7).
	/// </summary>
	void SetGraph(RenderGraph graph);

	/// <summary>
	/// Stand up the backend's Dear ImGui renderer and upload the current ImGui context's font atlas, so a pass
	/// that <c>.Blends</c> the backbuffer can call <c>ctx.DrawImGui()</c>. Call once after creation; the host
	/// must have created and configured the ImGui context first, then feeds draw data via
	/// <see cref="FrameInputs.ImGuiDrawData"/> each frame.
	/// </summary>
	void EnableImGui();

	/// <summary>Render one frame of the active graph from <paramref name="inputs"/> (Planning.md §5 <c>graph.Render</c>).</summary>
	void Render(FrameInputs inputs);

	/// <summary>Notify the engine the output surface resized to <paramref name="width"/>×<paramref name="height"/> pixels (drives swapchain + relative-domain recreation).</summary>
	void Resize(int width, int height);

	/// <summary>
	/// Sample the CPU-side readback of the graph's <c>g.Readback</c> target at a target-space pixel (Planning.md
	/// §7.4 picking). Returns the value from a frame or two ago (the readback latency is real); 0 outside the
	/// populated region, before warm-up, or when the graph declared no readback.
	/// </summary>
	float SamplePick(int x, int y);

	/// <summary>Capture the next presented frame to a PNG at <paramref name="path"/> (debug/validation). Synchronous on that frame.</summary>
	void RequestCapture(string path);

	/// <summary>Block until the GPU is idle — call before disposing host-owned resources the engine may still be using.</summary>
	void WaitIdle();
}
