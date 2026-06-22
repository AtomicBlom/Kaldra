using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

	// CPU shadow of each active-graph Constants resource (id → its bytes), zero-initialised on SetGraph and
	// updated by Write. The backend uploads each into the current frame slot at frame start, so the last write
	// persists (write-on-change) without the CPU ever racing a frame still reading the previous value.
	private readonly Dictionary<int, byte[]> _constants = [];

	/// <summary>The active graph's parameter-buffer shadows (id → bytes) for the backend to upload into the current
	/// frame slot before recording. Empty until <see cref="SetGraph"/>; rebuilt (zeroed) on every swap.</summary>
	protected IReadOnlyDictionary<int, byte[]> ConstantsShadows => _constants;

	public void Write<T>(ConstantsHandle handle, in T value) where T : unmanaged
	{
		ArgumentNullException.ThrowIfNull(handle);
		if (!_constants.TryGetValue(handle.Id, out var shadow))
			throw new InvalidOperationException(
				$"Constants '{handle.Name}' is not part of the active graph — call SetGraph with a graph that declares it before writing.");
		var size = Unsafe.SizeOf<T>();
		if (size != shadow.Length)
			throw new ArgumentException(
				$"Constants '{handle.Name}' is {shadow.Length} bytes but the written {typeof(T).Name} is {size} bytes — the type must match the declared parameter-buffer size.", nameof(value));
		MemoryMarshal.Write(shadow, in value);
	}

	// CPU-side copy of each g.Readback target's contents (id → tightly-packed bytes in the resource's format),
	// filled by the backend once a frame's slot fence signals and returned by Read. Empty until the backend first
	// stores into it (the readback latency); rebuilt on every SetGraph.
	private readonly Dictionary<int, byte[]> _readback = [];

	public ReadOnlySpan<byte> Read(ResourceHandle handle)
	{
		ArgumentNullException.ThrowIfNull(handle);
		if (_readback.TryGetValue(handle.Id, out var bytes))
			return bytes;
		return default;
	}

	/// <summary>
	/// Store a <c>g.Readback</c> target's freshly-decoded CPU bytes (tightly packed in the resource's format) so
	/// <see cref="Read"/> returns them. The backend calls this after the target's slot fence signals; the byte
	/// count is constant for a given graph (the buffer is reused once allocated).
	/// </summary>
	protected void StoreReadback(int id, ReadOnlySpan<byte> bytes)
	{
		if (!_readback.TryGetValue(id, out var buffer) || buffer.Length != bytes.Length)
			_readback[id] = buffer = new byte[bytes.Length];
		bytes.CopyTo(buffer);
	}

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

		// Reset the parameter-buffer shadows for this graph (ids are graph-local, so a swap re-mints them). Each
		// starts zero-filled, so a technique that reads a buffer the app hasn't written yet sees a defined value.
		_constants.Clear();
		foreach (var resource in graph.Resources)
			if (resource.Kind == ResourceKind.Constants)
				_constants[resource.Id] = new byte[resource.ByteSize];

		// Drop the previous graph's readback bytes; the backend refills per target as frames retire.
		_readback.Clear();

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
	public abstract Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices);
	public abstract Mesh CreateMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices) where T : unmanaged;
	public abstract Mesh CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices);
	public abstract Texture CreateTexture(TextureData data);
	public abstract Material CreateMaterial(params ReadOnlySpan<Texture> textures);
	public abstract void Dispose();
}
