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

	/// <summary>
	/// Enable per-pass GPU timestamp collection (M13 profiling spine). Off by default; cheap to toggle live (an
	/// ImGui HUD checkbox) — no graph rebuild. A backend that cannot time (e.g. a queue with no timestamp support)
	/// keeps this false. When off, <see cref="LastFrameTimings"/> stays empty and the per-frame cost is nil.
	/// </summary>
	bool GpuTimingEnabled { get; set; }

	/// <summary>
	/// Per-pass GPU timings from the most recently <em>completed</em> frame (a frame or two stale, like
	/// <see cref="SamplePick"/> — timestamps are read back after the slot's fence). In execution order; the entries
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
	/// The active graph's image resources the debug viewer can preview (colour + depth; M13). Empty until
	/// <see cref="SetGraph"/>, rebuilt on each swap, and empty in a build without the ImGui debug UI. Neutral types.
	/// </summary>
	IReadOnlyList<InspectableResource> InspectableResources { get; }

	/// <summary>
	/// An opaque per-frame ImGui texture id (<c>ImTextureID</c>) for the resource, to pass to <c>ImGui.Image</c>;
	/// the backend maps it back to a GPU binding when it records the HUD (M13 resource viewer). Returns 0 if the id
	/// is not inspectable or the ImGui debug UI is not built/enabled. Call during the frame's ImGui build; the id is
	/// valid for that frame's draw list only. <c>nint</c> is the same opaque currency as
	/// <see cref="FrameInputs.ImGuiDrawData"/> — no graphics-API type leaks.
	/// </summary>
	nint GetDebugTextureId(int resourceId);

	/// <summary>
	/// Set the debug overlay's line segments for the next frame (M13 detached-camera frustum freeze): each
	/// <see cref="System.Numerics.Vector4"/> is a screen-space segment <c>(x0, y0, x1, y1)</c> in framebuffer pixels,
	/// drawn over the HUD. The host projects world-space geometry (e.g. a frozen frustum's edges) to pixels and calls
	/// this each frame; an empty span clears it. A no-op in a build without the ImGui debug UI.
	/// </summary>
	void SetDebugLines(ReadOnlySpan<System.Numerics.Vector4> segments);

	/// <summary>Block until the GPU is idle — call before disposing host-owned resources the engine may still be using.</summary>
	void WaitIdle();
}
