using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Loom.Core.Animation;
using Loom.Core.Diagnostics;
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

	/// <summary>
	/// The materials this engine has minted — their texture ids, parameter bytes, and technique pairings. Owned here
	/// rather than per backend so the material/technique contract is checked identically everywhere; a backend passes
	/// this to its render context to resolve a draw's material.
	/// </summary>
	protected MaterialStore Materials { get; } = new();

	/// <summary>The backend's name (e.g. <c>"Vulkan"</c>), surfaced as <see cref="IEngine.BackendName"/>.</summary>
	public abstract string BackendName { get; }

	/// <summary>A human-readable name of the GPU the engine runs on.</summary>
	public abstract string DeviceName { get; }

	/// <summary>
	/// What the device this engine runs on can do, probed once during backend creation. <see cref="SetGraph"/>
	/// validates the graph against it, so a backend that under-reports here rejects graphs it could have run.
	/// </summary>
	public abstract EngineCapabilities Capabilities { get; }

	/// <summary>
	/// The engine family's clip-space convention: negate clip-space Y. Both current backends adopt it (Vulkan's
	/// native +Y-down NDC; DirectX12 pairs it with a negative-height viewport), so the shared shaders need no
	/// per-backend branch. A backend with a different convention overrides this; the app composes it into its
	/// view-projection rather than hardcoding any NDC.
	/// </summary>
	public virtual Matrix4x4 ClipSpaceCorrection => Matrix4x4.CreateScale(1f, -1f, 1f);

	/// <summary>
	/// Enable per-pass GPU timestamp collection. Enabling is refused when the device/queue can't time
	/// (<see cref="EngineCapabilities.GpuTiming"/> is false), so <see cref="LastFrameTimings"/> then stays empty.
	/// </summary>
	public bool GpuTimingEnabled
	{
		get => _gpuTimingEnabled;
		set => _gpuTimingEnabled = value && Capabilities.GpuTiming;
	}

	/// <inheritdoc/>
	public float? DisplaySdrWhiteNits { get; set; }

	/// <inheritdoc/>
	public float? DisplayPeakNits { get; set; }

	/// <inheritdoc/>
	public float? HdrPaperWhiteChoice { get; set => field = HdrLevels.Choice(value, nameof(HdrPaperWhiteChoice)); }

	/// <inheritdoc/>
	public float? HdrPeakChoice { get; set => field = HdrLevels.Choice(value, nameof(HdrPeakChoice)); }

	/// <inheritdoc/>
	public float HdrPaperWhiteNits => HdrLevels.PaperWhite(HdrPaperWhiteChoice, DisplaySdrWhiteNits);

	/// <inheritdoc/>
	public float HdrPeakNits => HdrLevels.Peak(HdrPeakChoice, DisplayPeakNits, HdrPaperWhiteNits);

	/// <summary>Per-pass GPU timings from the most recently completed frame (empty when timing is disabled).</summary>
	public IReadOnlyList<PassTiming> LastFrameTimings => _timings;

	/// <summary>Total GPU time for the most recent completed frame, in milliseconds (0 when timing is disabled).</summary>
	public double LastFrameGpuMilliseconds { get; private set; }

	/// <summary>The path the next presented frame should be captured to, or null. A backend's frame loop reads and clears it.</summary>
	protected string? CapturePath { get; set; }

	private readonly Action<string, TechniqueDescriptor> _defineTechniqueDelegate;
	private readonly Action _waitIdleDelegate;
	private readonly Action _resetPipelineCachesDelegate;

	/// <summary>
	/// The settings the engine was created with. Held here rather than per backend so a base-level knob cannot be
	/// reachable on one backend and forgotten on another.
	/// </summary>
	protected EngineSettings Settings { get; }

	/// <summary>Checks each recorded draw against its pass's declarations; handed to the backend's render context.</summary>
	protected RecordValidator Validation { get; }

	protected EngineBase(EngineSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		Settings = settings;
		Validation = new RecordValidator(settings.ValidateRecording, Techniques, _drawListStreams);
		_defineTechniqueDelegate = DefineTechnique;
		_waitIdleDelegate = WaitIdle;
		_resetPipelineCachesDelegate = ResetPipelineCaches;
	}

	/// <summary>Capture the next presented frame to a PNG at <paramref name="path"/>.</summary>
	public void RequestCapture(string path) => CapturePath = path;

	/// <summary>
	/// The path this frame should capture to, or null — dropping the request, with a warning naming the format, when
	/// the present target is one the capture path cannot decode.
	/// <para>
	/// PNG capture reads an 8-bit RGBA/BGRA present target. An HDR swapchain (FP16, or 10-bit packed) unpacked at four
	/// bytes per pixel produces a plausible-looking corrupt file, which is worse than no file — so the request is
	/// refused instead, and HDR output is verified on a display. The decision lives here so both halves of it (skip,
	/// and say why) are the same wherever a backend asks.
	/// </para>
	/// </summary>
	/// <param name="supported">Whether this backend's present format is one its capture path can decode.</param>
	/// <param name="presentFormat">
	/// The backend's own present-format enum, named in the message. Taken generically rather than as a string so the
	/// name is only produced on the path that reports it — this runs every frame, and the steady loop must not allocate.
	/// </param>
	protected string? TakeCapturePath<TFormat>(bool supported, TFormat presentFormat) where TFormat : struct, Enum
	{
		if (CapturePath is null)
			return null;
		if (supported)
			return CapturePath;
		LoomLog.Warning($"capture skipped: present format {presentFormat} is not an 8-bit RGBA/BGRA format (HDR output).");
		CapturePath = null;
		return null;
	}

	/// <summary>
	/// Register a technique under a logical name: bake its backend pipeline inputs, then record it for hot-reload
	/// so a later shader change can replay it. Idempotent per name.
	/// </summary>
	public void DefineTechnique(string name, TechniqueDescriptor descriptor)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		// A draw list's textures are loaded images and UI layers, colour formats all, and its one sampler slot is plain on
		// every backend; a list technique that compared would read undefined results.
		if (descriptor.HasListPush && descriptor.SampleCompare is not null)
			throw new InvalidOperationException($"Technique '{name}' is a draw-list technique (HasListPush) and names SampleCompare; a draw list samples colour textures, which no backend can compare.");
		var shape = BakeTechnique(name, descriptor);
		// Recording what the shader expects of a material is what lets a mismatch be caught at *definition* — and
		// because a hot reload replays this, at reload too, against every material already built for the name.
		Materials.RegisterTechnique(name, shape.Material);
		Techniques.Register(name, descriptor, shape);
		_reload.Record(name, descriptor);
	}

	/// <summary>
	/// Every technique defined on this engine, for the checks that must consult a technique by name without going
	/// through a backend's own pipeline cache.
	/// </summary>
	protected TechniqueRegistry Techniques { get; } = new();

	/// <summary>
	/// Bake a technique's backend pipeline/PSO inputs from its descriptor — the backend half of
	/// <see cref="DefineTechnique"/>. Returns what the baked technique can accept, which the backend already has in
	/// hand: it all comes from the same reflection sidecar the descriptor counts come from. Return <c>default</c> for
	/// a technique with no reflection, which reads as "nothing is known", not as "it accepts nothing".
	/// </summary>
	protected abstract TechniqueShape BakeTechnique(string name, TechniqueDescriptor descriptor);

	/// <inheritdoc/>
	public MaterialLayout MaterialLayoutOf(string technique)
	{
		ArgumentException.ThrowIfNullOrEmpty(technique);
		return Materials.LayoutOf(technique)
			?? throw new InvalidOperationException($"Technique '{technique}' is not defined — call DefineTechnique before asking what it expects of a material.");
	}

	/// <inheritdoc/>
	public Material CreateMaterial(string technique, params ReadOnlySpan<Texture> textures)
	{
		ArgumentException.ThrowIfNullOrEmpty(technique);
		return Materials.Create(technique, textures, default);
	}

	/// <inheritdoc/>
	public Material CreateMaterial<T>(string technique, in T parameters, params ReadOnlySpan<Texture> textures) where T : unmanaged
	{
		ArgumentException.ThrowIfNullOrEmpty(technique);
		Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
		MemoryMarshal.Write(bytes, in parameters);
		return Materials.Create(technique, textures, bytes);
	}

	// The thread that constructed the engine, which the contract makes the render thread. Captured here rather than
	// on the first Render so a Write before the first frame is checked too.
	private readonly int _renderThreadId = Environment.CurrentManagedThreadId;

	/// <summary>
	/// Enforce the single-threaded record contract in Debug: everything that mutates engine state read during
	/// recording — the <c>Write</c> family, <see cref="SetGraph"/>, and <see cref="Render"/> itself — happens on
	/// one thread, the one that created the engine. Nothing in the recording path is externally synchronized (a
	/// backend's command pool and queue are not, and the shadow dictionaries are plain <see cref="Dictionary{K,V}"/>),
	/// so a second thread does not race a lock — it corrupts state, usually far from the call that did it.
	/// <para>
	/// The deliberate exception is asset creation: <c>Create*Async</c> may be called from any thread and defers the
	/// GPU work onto a queue that <see cref="ServicePendingUploads"/> drains on this one. That is the seam, and it
	/// exists precisely because the rest of the surface has this affinity.
	/// </para>
	/// <para>
	/// Debug-only: the check is one integer compare, but it is a contract violation the caller must fix rather than
	/// something to pay for in a shipped frame loop.
	/// </para>
	/// </summary>
	[Conditional("DEBUG")]
	protected void AssertRenderThread(string member)
	{
		if (Environment.CurrentManagedThreadId == _renderThreadId)
			return;
		throw new InvalidOperationException(
			$"{GetType().Name}.{member} was called from thread {Environment.CurrentManagedThreadId}, but the engine " +
			$"was created on thread {_renderThreadId} and its recording path is single-threaded. Marshal the call " +
			"onto the render thread; for asset creation use the Create*Async overloads, which are thread-safe by design.");
	}

	/// <inheritdoc/>
	public void Write<T>(Material material, in T parameters) where T : unmanaged
	{
		AssertRenderThread(nameof(Write));
		Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
		MemoryMarshal.Write(bytes, in parameters);
		Materials.WriteParameters(material.Id, bytes);
	}

	/// <summary>Queue logical shader <paramref name="shader"/> for hot-reload if a technique uses it. Call from the file-watcher callback.</summary>
	protected void QueueShaderReload(string shader) => _reload.QueueIfTracked(shader);

	/// <summary>
	/// Drain queued shader reloads on the render thread: drain the GPU, rebuild every affected technique, then reset
	/// the backend's pipeline caches so the next frame re-bakes from the new bytecode. A no-op when nothing changed.
	/// </summary>
	protected void DrainShaderReloads() => _reload.Drain(_waitIdleDelegate, _defineTechniqueDelegate, _resetPipelineCachesDelegate);

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
	protected ShadowBuffers ConstantsShadows => new(_constants);

	public void Write<T>(ConstantsHandle handle, in T value) where T : unmanaged
	{
		AssertRenderThread(nameof(Write));
		ArgumentNullException.ThrowIfNull(handle);
		if (!_constants.TryGetValue(handle.Id, out var shadow))
			throw new InvalidOperationException(
				$"Constants '{handle.Name}' is not part of the active graph — call SetGraph with a graph that declares it before writing.");
		var size = Unsafe.SizeOf<T>();
		if (size != shadow.Length)
			throw new ArgumentException(
				$"Constants '{handle.Name}' is {shadow.Length} bytes but the written {typeof(T).Name} is {size} bytes — the type must match the declared parameter-buffer size.", nameof(value));
		MemoryMarshal.Write(shadow, in value);
		Written(handle.Id);
	}

	// How many times each app-writable buffer has been written (id → count), and per frame slot the count its GPU copy
	// holds (-1: none yet). A frame uploads only the buffers its slot's copy is behind on, so a buffer written once
	// costs nothing per frame after every slot has caught up, however large it is; and the last Write still persists.
	private readonly Dictionary<int, int> _writes = [];
	private readonly Dictionary<int, int[]> _uploadedWrites = [];

	private void Written(int id) => CollectionsMarshal.GetValueRefOrNullRef(_writes, id)++;

	// Whether this slot's copy of a buffer is behind its last Write
	private bool Behind(int id, int slot) => _uploadedWrites[id][slot] != _writes[id];

	// This slot's copy of a buffer now holds its last Write. Marked after the backend's write rather than before, so a write
	// that throws leaves the slot behind and the next frame tries again.
	private void CaughtUp(int id, int slot) => _uploadedWrites[id][slot] = _writes[id];

	/// <summary>Which of the per-frame-slot copies of each Constants and Storage buffer the frame being recorded reads.</summary>
	protected abstract int ShadowSlot { get; }

	/// <summary>How many per-frame-slot copies of each Constants and Storage buffer the backend keeps (1 where the API
	/// orders a buffer write before the next frame's reads itself).</summary>
	protected abstract int ShadowSlotCount { get; }

	/// <summary>
	/// Every Constants and Storage buffer's GPU copies are new (a resize rebuilt them, say): upload each again into
	/// every slot, whether or not it has been written since. A backend calls this wherever it replaces those buffers
	/// outside <see cref="SetGraph"/>.
	/// </summary>
	protected void ForgetUploadedShadows()
	{
		foreach (var uploaded in _uploadedWrites.Values)
			Array.Fill(uploaded, -1);
	}

	// CPU shadow of each active-graph Storage resource (id → its bytes), like _constants but an array; the backend
	// uploads each into the current frame slot at frame start. The shadow is the buffer's full declared size, so a
	// partial write leaves the tail at its previous (or zero) value.
	private readonly Dictionary<int, byte[]> _storage = [];

	/// <summary>The active graph's storage-buffer shadows (id → bytes) for the backend to upload into the current frame slot before recording. Empty until <see cref="SetGraph"/>; rebuilt (zeroed) on every swap.</summary>
	protected ShadowBuffers StorageShadows => new(_storage);

	/// <summary>
	/// A read-only view over a shadow dictionary that a backend walks once per frame. It exists so that walk costs
	/// nothing: exposing the shadows as an <see cref="IReadOnlyDictionary{TKey,TValue}"/> would box a fresh enumerator
	/// on every frame for every shadow set, which the no-allocations-on-the-hot-path pillar does not allow — and
	/// handing out the <see cref="Dictionary{TKey,TValue}"/> itself would hand out the ability to mutate it.
	/// </summary>
	protected readonly struct ShadowBuffers(Dictionary<int, byte[]> shadows)
	{
		/// <summary>The concrete struct enumerator, so <c>foreach</c> over this allocates nothing.</summary>
		public Dictionary<int, byte[]>.Enumerator GetEnumerator() => shadows.GetEnumerator();

		/// <summary>How many shadows the active graph declared.</summary>
		public int Count => shadows.Count;
	}

	public void Write<T>(StorageBufferHandle handle, ReadOnlySpan<T> values) where T : unmanaged
	{
		AssertRenderThread(nameof(Write));
		ArgumentNullException.ThrowIfNull(handle);
		if (!_storage.TryGetValue(handle.Id, out var shadow))
			throw new InvalidOperationException(
				$"Storage '{handle.Name}' is not part of the active graph — call SetGraph with a graph that declares it before writing.");
		var bytes = MemoryMarshal.AsBytes(values);
		if (bytes.Length > shadow.Length)
		{
			if (!handle.Growable)
				throw new ArgumentException(
					$"Storage '{handle.Name}' holds {shadow.Length} bytes but the written {values.Length} × {typeof(T).Name} is {bytes.Length} bytes — the data must fit the declared buffer size. Declare it with growable: true to let it grow.", nameof(values));
			shadow = GrowStorageBuffer(handle.Id, bytes.Length, shadow);
		}
		bytes.CopyTo(shadow);
		Written(handle.Id);
	}

	// Grow a growable storage buffer to hold at least requiredBytes: enlarge the CPU shadow (geometric — at least
	// double — so reallocations are amortised), preserving its current contents, then drain the GPU and have the
	// backend reallocate the resource's per-slot GPU buffers to match. The drain is needed because the old buffers
	// may still be read by a frame in flight; Write runs before Render (outside command recording), so it's safe here
	// and rare (geometric growth). The next frame re-uploads the larger shadow and rebinds the new buffer.
	private byte[] GrowStorageBuffer(int id, int requiredBytes, byte[] current)
	{
		var newCapacity = Math.Max(requiredBytes, current.Length * 2);
		var grown = new byte[newCapacity];
		current.CopyTo(grown.AsSpan());
		_storage[id] = grown;
		WaitIdle();
		GrowStorage(id, newCapacity);
		Array.Fill(_uploadedWrites[id], -1);   // new buffers in every slot
		return grown;
	}

	/// <summary>
	/// Reallocate a growable <see cref="ResourceKind.Storage"/> resource's per-slot GPU buffers to
	/// <paramref name="newByteSize"/> bytes. Called after a GPU drain, so the backend may free and recreate the
	/// buffers directly; the next frame re-uploads the (now larger) shadow and rebinds the new buffers (both backends
	/// fetch the storage buffer afresh each draw).
	/// </summary>
	protected abstract void GrowStorage(int id, int newByteSize);

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

	/// <summary>
	/// Drop a readback target's cached bytes, so <see cref="Read"/> returns an empty span until a fresh copy lands.
	/// Called by a backend when it reallocates the target at a new size: the bytes it holds describe the <em>old</em>
	/// dimensions, and an app that indexed them by a cursor position against the new ones would read the wrong texels
	/// with nothing to signal it. Empty is what <see cref="Read"/> already returns before any graph's first copy
	/// completes, so every consumer handles it.
	/// </summary>
	protected void InvalidateReadback(int id) => _readback.Remove(id);

	/// <summary>Prefix on the aliasing-summary log line for backends that tag it (e.g. <c>"DirectX12 "</c>); empty by default.</summary>
	protected virtual string AliasingLogPrefix => "";

	// The bake key of the active graph: its structural hash plus the inspection flag, because inspection injects
	// debug-sample edges and so changes the graph that is actually derived and executed. Null until the first SetGraph.
	private string? _activeBakeKey;

	private string BakeKey(RenderGraph graph) => $"{graph.Hash()}|inspect={ResourceInspectionEnabled}";

	/// <summary>
	/// Make <paramref name="graph"/> the active graph: drain the GPU and release the previous graph's resources,
	/// enumerate inspectable resources, derive the schedule/barriers/aliasing, then rebuild the per-graph GPU
	/// resources. The device and pipeline cache are reused — only the per-graph plan and resources are rebuilt.
	/// <para>
	/// <b>Baked and cached by hash.</b> A graph whose <see cref="RenderGraph.Hash"/> matches the active one derives to
	/// the same schedule, barriers and aliasing, and needs the same GPU resources — so the GPU half is skipped
	/// entirely: no drain, no reallocation, and no loss of what those resources hold (cross-frame
	/// <see cref="ResourceKind.Persistent"/> history, the app's written parameter buffers, pending readback bytes). The
	/// derivation itself still re-runs, cheaply and on the CPU only, because the plan carries <em>this</em> graph's
	/// <c>.Draw</c>/<c>.Dispatch</c> callbacks — reusing the previous graph's would silently render whatever the old
	/// closures captured. Setting an equivalent graph is therefore cheap, and safe to do per frame.
	/// </para>
	/// </summary>
	public void SetGraph(RenderGraph graph)
	{
		AssertRenderThread(nameof(SetGraph));
		ArgumentNullException.ThrowIfNull(graph);

		// Above the bake-cache check rather than inside the derivation: an equal bake key does imply an equal verdict
		// today (the hash covers both the sample count and the queue), but resting a correctness guarantee on the hash
		// continuing to cover them is a worse trade than a handful of integer comparisons on a path that already
		// re-derives the whole plan.
		GraphCapabilities.Validate(graph, Capabilities);

		var key = BakeKey(graph);

		if (Graph is not null && key == _activeBakeKey)
		{
			Graph = graph;
			DeriveGraphPlans(graph);
			return;
		}

		if (Graph is not null)
			WaitIdle();
		ReleaseGraphResources();
		Graph = graph;
		_activeBakeKey = key;

		// Reset the app-writable buffer shadows for this graph (ids are graph-local, so a swap re-mints them). Each
		// starts zero-filled, so a technique that reads a buffer the app hasn't written yet sees a defined value.
		_constants.Clear();
		_storage.Clear();
		_writes.Clear();
		_uploadedWrites.Clear();
		foreach (var resource in graph.Resources)
		{
			if (resource.Kind == ResourceKind.Constants)
				_constants[resource.Id] = new byte[resource.ByteSize];
			else if (resource.Kind == ResourceKind.Storage)
				_storage[resource.Id] = new byte[resource.ByteSize];
			else
				continue;
			_writes[resource.Id] = 0;
			var uploaded = new int[ShadowSlotCount];
			Array.Fill(uploaded, -1);
			_uploadedWrites[resource.Id] = uploaded;
		}

		// Drop the previous graph's readback bytes; the backend refills per target as frames retire.
		_readback.Clear();

		var executed = DeriveGraphPlans(graph);

		if (Aliases.AliasableCount > Aliases.Buckets.Length)
			LoomLog.Info($"{AliasingLogPrefix}transient aliasing: {Aliases.AliasableCount} transients folded into {Aliases.Buckets.Length} memory buckets.");

		RebuildGraphResources(graph, executed);
	}

	// Enumerate the inspectable resources and derive the schedule/barrier/alias plans, returning the graph the engine
	// actually executes (the author's, widened with debug-sample edges when inspection is on). Pure CPU work over
	// immutable data — no GPU calls — which is what lets a bake-cache hit re-run it to pick up the new graph's
	// per-frame callbacks without touching a single GPU resource.
	private RenderGraph DeriveGraphPlans(RenderGraph graph)
	{
		// Resource viewer: enumerate the colour/depth resources the host can preview, and (when inspection is on)
		// inject debug-sample edges so the Execute layer keeps them alive in a sampleable layout. The author's graph
		// is untouched — the engine derives and renders `executed`.
		_inspectable.Clear();
		var executed = graph;
		if (ResourceInspectionEnabled)
		{
			// A multisampled depth target is left out: only a colour target is resolved for sampling, and the derivation
			// refuses a sampled multisampled depth target.
			foreach (var resource in graph.Resources)
				if (resource.Kind == ResourceKind.Color || (resource.Kind == ResourceKind.Depth && resource.Domain.Samples == 1))
					_inspectable.Add(new InspectableResource(resource.Id, resource.Name, resource.Kind, resource.Format));
			executed = graph.WithDebugSampling(_inspectable.ConvertAll(r => r.Id));
		}

		// The graph is immutable, so order/barriers/load-store/lifetimes/aliasing are frame-invariant.
		var schedule = GraphSchedule.Derive(executed);
		Barriers = BarrierPlan.Derive(schedule);
		Aliases = AliasPlan.Derive(LifetimePlan.Derive(schedule));
		return executed;
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

	// ── GPU debug-label scopes: derived nesting, driven identically on every backend ──

	/// <summary>
	/// Open a pass's debug-label scopes on the command stream being recorded: close the groups the previous pass
	/// leaves behind, open the ones this pass enters, then open the pass's own leaf scope. Paired with
	/// <see cref="EndPassLabel"/> once the pass has recorded, and with <see cref="CloseTrailingLabels"/> after the
	/// last pass of the frame.
	/// <para>
	/// The nesting is derived once from the dotted pass names (<see cref="PassBarriers.LabelOpens"/> /
	/// <see cref="PassBarriers.LabelCloses"/>), so a capture reads as a <c>bloom</c> group containing <c>down1</c>,
	/// <c>up0</c>, … rather than a flat command list.
	/// </para>
	/// <para>
	/// <b><c>[Conditional]</c> belongs here, not on the hooks.</b> The attribute is honoured where the <em>call</em>
	/// is compiled, so carrying it on this method makes <c>LOOM_NAMED_GPU_OBJECTS</c> as this assembly sees it the one
	/// symbol that governs every backend — and an <c>override</c> cannot carry the attribute at all, so the hooks
	/// could not have been the gate.
	/// </para>
	/// </summary>
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	protected void BeginPassLabels(PassBarriers pass)
	{
		for (var i = 0; i < pass.LabelCloses; i++)
			EndLabel();
		foreach (var group in pass.LabelOpens)
			BeginLabel(group);
		BeginLabel(pass.LeafLabel);
	}

	/// <summary>Close the leaf scope <see cref="BeginPassLabels"/> opened, once the pass has finished recording.</summary>
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	protected void EndPassLabel() => EndLabel();

	/// <summary>Close the label groups still open after the final pass, balancing <see cref="BeginPassLabels"/>' opens.</summary>
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	protected void CloseTrailingLabels()
	{
		for (var i = 0; i < Barriers.TrailingLabelCloses; i++)
			EndLabel();
	}

	/// <summary>
	/// Open a named scope on the command stream this frame is recording into. Called only from the label bookkeeping
	/// above and only mid-record, which is why nothing is handed in: a backend reaches for its own current command
	/// buffer/list. A backend whose API carries no such annotation implements this as an explicit no-op.
	/// </summary>
	protected abstract void BeginLabel(string name);

	/// <summary>Close the most recently opened scope — see <see cref="BeginLabel"/>.</summary>
	protected abstract void EndLabel();

	// ── The per-frame record spine, shared by every backend ──

	/// <summary>
	/// Record the derived plan for one frame: flush the app-writable buffer shadows, then walk
	/// <see cref="BarrierPlan.Passes"/> emitting each pass's transitions, debug-label scope and timestamps around its
	/// own recording, then the readback copies and the frame's tail. A backend calls this once from
	/// <see cref="RenderFrame"/>, with its own command buffer/list already open, and fills in the hooks below.
	/// <para>
	/// The value is the <em>order</em>, not the loop. Every step here has to happen in this sequence on every backend —
	/// a pass's barriers before its timestamp because the barriers belong to the transition rather than the pass, the
	/// readback copies before the frame's tail because the tail may transition the very targets being copied — and
	/// three transliterations of a fixed sequence are three chances to get one of them subtly wrong. They already had:
	/// two backends recorded readback before capture and the third recorded capture before readback, which is harmless
	/// only for as long as those copies stay independent.
	/// </para>
	/// </summary>
	protected void RecordFrame()
	{
		// Bring this frame slot's copy of each app-writable buffer up to its last IEngine.Write, which is what makes that
		// write persist: a static parameter is written once and an animated one per frame, and either way what the GPU
		// reads this frame is current. A copy already up to date is left alone, so a large buffer written once (a table
		// of thousands of elements filled at load) costs nothing per frame once every slot holds it.
		var slot = ShadowSlot;
		foreach (var (id, bytes) in ConstantsShadows)
		{
			if (!Behind(id, slot))
				continue;
			WriteConstantsShadow(id, bytes);
			CaughtUp(id, slot);
		}
		foreach (var (id, bytes) in StorageShadows)
		{
			if (!Behind(id, slot))
				continue;
			WriteStorageShadow(id, bytes);
			CaughtUp(id, slot);
		}

		BeginFrameTimestamps();

		var passIndex = 0;
		try
		{
			foreach (var pass in Barriers.Passes)
			{
				BeginPassLabels(pass);
				EmitTransitions(pass.Transitions.AsSpan());
				// The timestamps bracket the pass's own work, after its pre-barriers: those barriers are the cost of the
				// transition into this pass, not of the pass.
				WritePassBeginTimestamp(passIndex);
				_recordingPass = pass.Pass.Name;
				if (pass.Pass.Kind == PassKind.Compute)
					RecordComputePass(pass);
				else
					RecordRasterPass(pass);
				_recordingPass = null;
				WritePassEndTimestamp(passIndex);
				EndPassLabel();
				passIndex++;
			}
		}
		finally
		{
			// A pass that throws abandons the frame, not the engine: the caller may catch, release and render again.
			_recordingPass = null;
		}

		EndFrameTimestamps();
		CloseTrailingLabels();

		// Every g.Readback target copies out before the tail, which may transition those same targets.
		RecordReadback();
		FinishFrame();
	}

	/// <summary>Upload one <see cref="ResourceKind.Constants"/> shadow into what this frame's passes will read.</summary>
	protected abstract void WriteConstantsShadow(int id, ReadOnlySpan<byte> bytes);

	/// <summary>Upload one <see cref="ResourceKind.Storage"/> shadow into what this frame's passes will read.</summary>
	protected abstract void WriteStorageShadow(int id, ReadOnlySpan<byte> bytes);

	/// <summary>
	/// Emit the derived resource transitions for the pass about to record. A backend whose API tracks resource state
	/// itself implements this as an explicit no-op — the plan's transitions are then genuinely not needed, which is
	/// worth saying rather than leaving as a gap in the loop.
	/// </summary>
	protected abstract void EmitTransitions(ReadOnlySpan<ResourceTransition> transitions);

	/// <summary>
	/// Record a compute pass: no attachments and no render scope, so bind and dispatch. Its resource transitions were
	/// emitted already.
	/// </summary>
	protected abstract void RecordComputePass(PassBarriers pass);

	/// <summary>
	/// Record a raster pass: open a render scope over its derived attachments, set the viewport and scissor, then run
	/// its record callback. Left whole to the backend rather than templated further — the attachment descriptors,
	/// formats and views are almost entirely API structs, and forcing them into one signature is what would push
	/// graphics types into this layer.
	/// </summary>
	protected abstract void RecordRasterPass(PassBarriers pass);

	/// <summary>Copy each <c>g.Readback</c> target into this frame slot's readback buffer; a no-op when the graph declares none.</summary>
	protected abstract void RecordReadback();

	/// <summary>
	/// The frame's tail: the requested capture, if any, and the derived final transitions — including whatever the
	/// present target needs to be handed to the presentation engine in. One hook rather than two because the backends
	/// legitimately fuse them differently: one replaces the final transition with the capture copy's own barriers, the
	/// other tracks resource state and so can run the final transitions from wherever the copy left the target.
	/// </summary>
	protected abstract void FinishFrame();

	/// <summary>Prepare this frame's timestamp storage (e.g. reset a query pool on the device timeline). A no-op when timing is off or unsupported.</summary>
	protected virtual void BeginFrameTimestamps() { }

	/// <summary>Write the opening timestamp for pass <paramref name="passIndex"/>.</summary>
	protected virtual void WritePassBeginTimestamp(int passIndex) { }

	/// <summary>Write the closing timestamp for pass <paramref name="passIndex"/>.</summary>
	protected virtual void WritePassEndTimestamp(int passIndex) { }

	/// <summary>Finish this frame's timing (resolve the queries, mark the slot readable). A no-op when timing is off or unsupported.</summary>
	protected virtual void EndFrameTimestamps() { }

	// ── The backend's own half: the per-frame record loop, resource creation, teardown. ──

	/// <inheritdoc/>
	public void Render(FrameInputs inputs)
	{
		AssertRenderThread(nameof(Render));
		RenderFrame(inputs);
	}

	/// <summary>Record and submit one frame. The backend's half of <see cref="Render"/>, which has already
	/// established that the caller is the render thread.</summary>
	protected abstract void RenderFrame(FrameInputs inputs);
	public abstract void Resize(int width, int height);
	public abstract void WaitIdle();

	// ── Resource creation and release: the neutral rules here, the GPU work in the backend's hook below each ──

	/// <inheritdoc/>
	public Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
	{
		ThrowIfRecording(nameof(CreateMesh));
		return UploadMesh(vertices, indices);
	}

	/// <inheritdoc/>
	public Mesh CreateMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices) where T : unmanaged
	{
		ThrowIfRecording(nameof(CreateMesh));
		return UploadMesh(vertices, layout, indices);
	}

	/// <inheritdoc/>
	public Mesh CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices)
	{
		ThrowIfRecording(nameof(CreateSkinnedMesh));
		return UploadSkinnedMesh(vertices, indices);
	}

	/// <inheritdoc/>
	public void ReleaseMesh(Mesh mesh)
	{
		AssertRenderThread(nameof(ReleaseMesh));
		ThrowIfRecording(nameof(ReleaseMesh));
		RetireMesh(mesh);
	}

	/// <inheritdoc/>
	public InstanceBuffer CreateInstances<T>(ReadOnlySpan<T> instances, VertexLayout layout) where T : unmanaged
	{
		ThrowIfRecording(nameof(CreateInstances));
		ArgumentNullException.ThrowIfNull(layout);
		if (instances.IsEmpty)
			throw new ArgumentException("An instance buffer holds at least one instance.", nameof(instances));
		// Stored as a mesh whose vertices are the instances: every backend already uploads those to GPU-resident memory,
		// resolves them at draw time and frees them after the frames in flight. The one index is never drawn.
		var storage = UploadMesh(instances, layout, [0u]);
		return new InstanceBuffer(storage, instances.Length, layout.Stride);
	}

	/// <inheritdoc/>
	public void ReleaseInstances(InstanceBuffer instances)
	{
		AssertRenderThread(nameof(ReleaseInstances));
		ThrowIfRecording(nameof(ReleaseInstances));
		RetireMesh(instances.Storage);
	}

	/// <inheritdoc/>
	public Texture CreateTexture(TextureData data)
	{
		ThrowIfRecording(nameof(CreateTexture));
		return UploadTexture(data);
	}

	/// <inheritdoc/>
	public void ReleaseTexture(Texture texture)
	{
		AssertRenderThread(nameof(ReleaseTexture));
		ThrowIfRecording(nameof(ReleaseTexture));
		Materials.ThrowIfHeld(texture);
		RetireTexture(texture);
	}

	/// <summary>Upload canonical-layout geometry and mint its handle — the backend half of <see cref="CreateMesh(ReadOnlySpan{Vertex}, ReadOnlySpan{uint})"/>.</summary>
	protected abstract Mesh UploadMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices);

	/// <summary>Upload geometry in an explicit layout — the backend half of <see cref="CreateMesh{T}"/>.</summary>
	protected abstract Mesh UploadMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices) where T : unmanaged;

	/// <summary>Upload a skinned mesh's bind pose — the backend half of <see cref="CreateSkinnedMesh"/>.</summary>
	protected abstract Mesh UploadSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices);

	/// <summary>Upload a texture and its mips and mint its handle — the backend half of <see cref="CreateTexture"/>.</summary>
	protected abstract Texture UploadTexture(TextureData data);

	/// <summary>
	/// Kill a mesh's handle at once and free its buffers once no frame in flight can read them — the backend half of
	/// <see cref="ReleaseMesh"/>. Called between frames, so nothing being recorded names the mesh. Every cached binding
	/// object that names one of its buffers is evicted before the buffer's handle can be reused.
	/// </summary>
	protected abstract void RetireMesh(Mesh mesh);

	/// <summary>
	/// Kill a texture's handle at once and destroy its image once no frame in flight, and no pending upload, can read it
	/// — the backend half of <see cref="ReleaseTexture"/>. Called between frames, after the check that no material holds
	/// it. Every cached binding object that names its view is evicted before the view's handle can be reused.
	/// </summary>
	protected abstract void RetireTexture(Texture texture);

	// The pass whose callback is running, or null between passes and between frames. Set by RecordFrame; read by
	// ThrowIfRecording on the render thread only, so it needs no synchronisation.
	private string? _recordingPass;

	/// <summary>
	/// Refuse a resource creation or release made from inside a pass callback. Recording is where the frame's commands
	/// are being written: an upload made there is submitted only ahead of the <em>next</em> frame, so this frame would
	/// sample memory its contents have not reached, and a release could free what this frame's commands already name.
	/// Neither shows as an error on its own — the first is one frame of garbage, the second a fault a frame or two later
	/// — so the call is refused where it is made. Only the render thread is checked: a background decode may queue an
	/// upload through a <c>*Async</c> creation while a frame records, which is exactly what those are for.
	/// </summary>
	private void ThrowIfRecording(string member)
	{
		if (_recordingPass is null || Environment.CurrentManagedThreadId != _renderThreadId)
			return;
		throw new InvalidOperationException(
			$"IEngine.{member} was called while pass '{_recordingPass}' was recording. A pass callback runs while the " +
			"frame's commands are written: an upload made there is submitted only ahead of the next frame, so this frame " +
			"would sample it before its contents arrive, and a release could free what this frame already draws with. " +
			"Create and release resources between frames, before IEngine.Render; a producer that discovers what it needs " +
			"while drawing resolves it in a step of its own before Render.");
	}

	// Mints and checks the stream ids stable draw lists are kept under; shared with the record validator.
	private readonly DrawListStreams _drawListStreams = new();

	/// <inheritdoc/>
	public int AllocateDrawListStream() => _drawListStreams.Allocate();

	/// <inheritdoc/>
	public Material CreateMaterial(params ReadOnlySpan<Texture> textures) => Materials.Create(textures);
	public abstract void Dispose();

	// ── Async GPU resource creation: a caller (any thread — a background decode, or a concurrently-preloading demo)
	// asks for a mesh/texture/material and gets a Task back; the actual GPU call only ever runs when
	// ServicePendingUploads drains the queue, which every backend's Render calls once per frame, and which host
	// setup code calls explicitly while no frame loop is running yet. GPU calls are NOT safe to issue from multiple
	// threads concurrently (a backend's command pool/queue isn't externally synchronized) — and since several
	// demos' PreloadAsync can genuinely run concurrently (their I/O overlaps by design), it's not just the upload
	// bytes that need marshaling onto one thread, it's every *Async creation call. Draining this one queue from one
	// place is what keeps that invariant intact without any backend needing its own queue/synchronization. ──
	private readonly ConcurrentQueue<Action> _pendingGpuWork = new();

	private Task<T> EnqueueGpuWork<T>(Func<T> work)
	{
		var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingGpuWork.Enqueue(() =>
		{
			try { tcs.SetResult(work()); }
			catch (Exception ex) { tcs.SetException(ex); }
		});
		return tcs.Task;
	}

	/// <summary>Async counterpart to <see cref="CreateMesh(ReadOnlySpan{Vertex}, ReadOnlySpan{uint})"/> — see the class-level remarks above.</summary>
	public Task<Mesh> CreateMeshAsync(Vertex[] vertices, uint[] indices)
	{
		ThrowIfRecording(nameof(CreateMeshAsync));
		return EnqueueGpuWork(() => CreateMesh(vertices, indices));
	}

	/// <summary>Async counterpart to <see cref="CreateSkinnedMesh"/> — see the class-level remarks above.</summary>
	public Task<Mesh> CreateSkinnedMeshAsync(SkinnedVertex[] vertices, uint[] indices)
	{
		ThrowIfRecording(nameof(CreateSkinnedMeshAsync));
		return EnqueueGpuWork(() => CreateSkinnedMesh(vertices, indices));
	}

	/// <summary>
	/// Queue a texture upload and return a <see cref="Task{Texture}"/> that completes once <see cref="ServicePendingUploads"/>
	/// drains it — safe to call from any thread (e.g. a background decode). See the class-level remarks above.
	/// </summary>
	public Task<Texture> CreateTextureAsync(TextureData data)
	{
		ThrowIfRecording(nameof(CreateTextureAsync));
		return EnqueueGpuWork(() => CreateTexture(data));
	}

	/// <summary>Async counterpart to <see cref="CreateMaterial(ReadOnlySpan{Texture})"/> — see the class-level remarks above.</summary>
	public Task<Material> CreateMaterialAsync(params Texture[] textures) => EnqueueGpuWork(() => CreateMaterial(textures));

	/// <summary>Async counterpart to <see cref="CreateMaterial(string, ReadOnlySpan{Texture})"/> — see the class-level remarks above.</summary>
	public Task<Material> CreateMaterialAsync(string technique, params Texture[] textures) =>
		EnqueueGpuWork(() => CreateMaterial(technique, textures));

	/// <summary>
	/// Async counterpart to <see cref="CreateMaterial{T}(string, in T, ReadOnlySpan{Texture})"/> — see the
	/// class-level remarks above. <paramref name="parameters"/> is taken by value rather than by <c>in</c> because
	/// the call is deferred onto the servicing thread, so the block has to be captured rather than referenced.
	/// </summary>
	public Task<Material> CreateMaterialAsync<T>(string technique, T parameters, params Texture[] textures) where T : unmanaged =>
		EnqueueGpuWork(() => CreateMaterial(technique, in parameters, textures));

	/// <summary>
	/// Run every GPU creation call queued by a <c>*Async</c> method since the last call, completing each one's
	/// <see cref="Task"/>. Called once per frame by every backend's <see cref="Render"/> (so calls requested during
	/// live rendering complete within a frame or two), and by host setup code in a small pump loop while awaiting a
	/// demo's preload before any frame loop exists yet.
	/// </summary>
	public void ServicePendingUploads()
	{
		while (_pendingGpuWork.TryDequeue(out var work))
			work();
		FlushUploads();
	}

	/// <summary>
	/// Submit whatever the drained creations recorded. A backend batches a whole drain's copies into one submission
	/// rather than one per resource, so this is where that batch goes to the queue — before the frame that will read
	/// from it, which is what orders the two without the CPU waiting on either.
	/// </summary>
	protected virtual void FlushUploads() { }
}
