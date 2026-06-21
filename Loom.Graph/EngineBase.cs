using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Graph;

/// <summary>
/// The backend-neutral half of a live engine: technique definition, shader hot-reload, GPU-timing decode, and frame
/// capture. A backend derives from this, fills in the GPU hooks (<see cref="BakeTechnique"/>,
/// <see cref="ResetPipelineCaches"/>, <see cref="WaitIdle"/>, …), and inherits this lifecycle identically — so the
/// shared sequencing lives once and a new backend can't drift from it. Graph derivation and the per-frame record
/// loop stay the backend's own (the remaining abstract members); this owns the state and ordering that is the same
/// everywhere.
/// </summary>
public abstract class EngineBase : IEngine
{
	private readonly ShaderReloadRegistry _reload = new();
	private PassTiming[] _timings = [];
	private bool _gpuTimingEnabled;

	/// <summary>The backend's name (e.g. <c>"Vulkan"</c>), surfaced as <see cref="IEngine.BackendName"/>.</summary>
	public abstract string BackendName { get; }

	/// <summary>A human-readable name of the GPU the engine runs on.</summary>
	public abstract string DeviceName { get; }

	/// <summary>
	/// Enable per-pass GPU timestamp collection. Enabling is refused when the device/queue can't time
	/// (<see cref="CanEnableGpuTiming"/> is false), so <see cref="LastFrameTimings"/> then stays empty.
	/// </summary>
	public bool GpuTimingEnabled
	{
		get => _gpuTimingEnabled;
		set => _gpuTimingEnabled = value && CanEnableGpuTiming;
	}

	/// <summary>Whether the device/queue can collect timestamps at all; gates <see cref="GpuTimingEnabled"/>.</summary>
	protected abstract bool CanEnableGpuTiming { get; }

	/// <summary>Per-pass GPU timings from the most recently completed frame (empty when timing is disabled).</summary>
	public IReadOnlyList<PassTiming> LastFrameTimings => _timings;

	/// <summary>Total GPU time for the most recent completed frame, in milliseconds (0 when timing is disabled).</summary>
	public double LastFrameGpuMilliseconds { get; private set; }

	/// <summary>The path the next presented frame should be captured to, or null. A backend's frame loop reads and clears it.</summary>
	protected string? CapturePath { get; set; }

	/// <summary>Capture the next presented frame to a PNG at <paramref name="path"/>.</summary>
	public void RequestCapture(string path) => CapturePath = path;

	/// <summary>
	/// Register a technique under a logical name: bake its backend pipeline inputs, then record it for hot-reload
	/// so a later shader change can replay it. Idempotent per name.
	/// </summary>
	public void DefineTechnique(string name, TechniqueDescriptor descriptor)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		BakeTechnique(name, descriptor);
		_reload.Record(name, descriptor);
	}

	/// <summary>Bake a technique's backend pipeline/PSO inputs from its descriptor — the backend half of <see cref="DefineTechnique"/>.</summary>
	protected abstract void BakeTechnique(string name, TechniqueDescriptor descriptor);

	/// <summary>Queue logical shader <paramref name="shader"/> for hot-reload if a technique uses it. Call from the file-watcher callback.</summary>
	protected void QueueShaderReload(string shader) => _reload.QueueIfTracked(shader);

	/// <summary>
	/// Drain queued shader reloads on the render thread: drain the GPU, rebuild every affected technique, then reset
	/// the backend's pipeline caches so the next frame re-bakes from the new bytecode. A no-op when nothing changed.
	/// </summary>
	protected void DrainShaderReloads() => _reload.Drain(WaitIdle, DefineTechnique, ResetPipelineCaches);

	/// <summary>Evict the backend's baked pipelines/PSOs so the next bake rebuilds them from the re-registered templates.</summary>
	protected abstract void ResetPipelineCaches();

	/// <summary>
	/// Decode interleaved begin/end <paramref name="timestamps"/> (already masked to the queue's valid bits) against
	/// <paramref name="plan"/> into <see cref="LastFrameTimings"/> and <see cref="LastFrameGpuMilliseconds"/>.
	/// <paramref name="millisecondsPerTick"/> converts a tick delta to milliseconds. Call from the backend's
	/// timestamp readback.
	/// </summary>
	protected void DecodeTimings(ReadOnlySpan<ulong> timestamps, BarrierPlan plan, double millisecondsPerTick) =>
		LastFrameGpuMilliseconds = TimingDecode.DecodePassTimings(timestamps, plan, millisecondsPerTick, ref _timings);

	/// <summary>Clear the last frame's timings — on graph swap or teardown, before a new graph's passes are timed.</summary>
	protected void ResetTimings()
	{
		_timings = [];
		LastFrameGpuMilliseconds = 0;
	}

	// ── Active graph + its derived plan: rebuilt by SetGraph, read by the backend's record loop ──
	protected RenderGraph? Graph { get; private set; }
	protected BarrierPlan Barriers { get; private set; } = null!;
	protected AliasPlan Aliases { get; private set; } = null!;

	private readonly List<InspectableResource> _inspectable = [];

	public bool ResourceInspectionEnabled { get; set; }
	public IReadOnlyList<InspectableResource> InspectableResources => _inspectable;

	/// <summary>Prefix on the aliasing-summary log line for backends that tag it (e.g. <c>"DirectX12 "</c>); empty by default.</summary>
	protected virtual string AliasingLogPrefix => "";

	/// <summary>
	/// Make <paramref name="graph"/> the active graph: drain the GPU and release the previous graph's resources,
	/// enumerate inspectable resources, derive the schedule/barriers/aliasing once, then rebuild the per-graph GPU
	/// resources. The device and pipeline cache are reused — only the per-graph plan and resources are rebuilt.
	/// </summary>
	public void SetGraph(RenderGraph graph)
	{
		ArgumentNullException.ThrowIfNull(graph);
		if (Graph is not null)
			WaitIdle();
		ReleaseGraphResources();
		Graph = graph;

		// Resource viewer: enumerate the colour/depth resources the host can preview, and (when inspection is on)
		// inject debug-sample edges so the Execute layer keeps them alive in a sampleable layout. The author's graph
		// is untouched — the engine derives and renders `executed`.
		_inspectable.Clear();
		var executed = graph;
		if (ResourceInspectionEnabled)
		{
			foreach (var resource in graph.Resources)
				if (resource.Kind is ResourceKind.Color or ResourceKind.Depth)
					_inspectable.Add(new InspectableResource(resource.Id, resource.Name, resource.Kind, resource.Format));
			executed = graph.WithDebugSampling(_inspectable.ConvertAll(r => r.Id));
		}

		// Derive once — the graph is immutable, so order/barriers/load-store/lifetimes/aliasing are frame-invariant.
		var schedule = GraphSchedule.Derive(executed);
		Barriers = BarrierPlan.Derive(schedule);
		Aliases = AliasPlan.Derive(LifetimePlan.Derive(schedule));

		if (Aliases.AliasableCount > Aliases.Buckets.Length)
			Console.WriteLine($"[loom] {AliasingLogPrefix}transient aliasing: {Aliases.AliasableCount} transients folded into {Aliases.Buckets.Length} memory buckets.");

		RebuildGraphResources(graph, executed);
	}

	/// <summary>Release the previous graph's per-graph GPU resources (transients, readback, timing pools). Called after a GPU drain.</summary>
	protected abstract void ReleaseGraphResources();

	/// <summary>
	/// Build this graph's per-graph GPU resources from the derived plan (<see cref="Barriers"/> / <see cref="Aliases"/>):
	/// transients from <paramref name="executed"/> (its debug-sample edges widen inspected resources), plus readback,
	/// timing, and any backend-specific id-sets. <paramref name="authored"/> is the original graph (readback targets,
	/// resource kinds).
	/// </summary>
	protected abstract void RebuildGraphResources(RenderGraph authored, RenderGraph executed);

	// ── The backend's own half: the per-frame record loop, resource creation, teardown. ──
	public abstract void Render(FrameInputs inputs);
	public abstract void Resize(int width, int height);
	public abstract void WaitIdle();
	public abstract float SamplePick(int x, int y);
	public abstract Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices);
	public abstract Mesh CreateMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices) where T : unmanaged;
	public abstract Mesh CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices);
	public abstract Texture CreateTexture(TextureData data);
	public abstract Material CreateMaterial(params ReadOnlySpan<Texture> textures);
	public abstract void Dispose();
}
