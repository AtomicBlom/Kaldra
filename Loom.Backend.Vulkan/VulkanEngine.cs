using Loom.Assets;
using Loom.Core.Animation;
using Loom.Core.Diagnostics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Graph;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using VMASharp;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Loom.Backend.Vulkan;

/// <summary>
/// The Vulkan <see cref="IEngine"/>. It owns the long-lived
/// device (<see cref="VulkanContext"/>), the pipeline cache, the mesh store, and the descriptor allocator; the
/// <em>active graph</em> is swappable (<c>SetGraph</c>). On each <c>SetGraph</c> it derives a
/// <see cref="GraphSchedule"/>, <see cref="BarrierPlan"/>, and transient-memory plan once (the plan is
/// frame-stable, so the per-frame cost is recording, not deriving) and prepares the graph's transients; then
/// each <see cref="RenderFrame"/> records the plan into a command buffer — emitting <c>vkCmdPipelineBarrier2</c> from
/// the derived transitions, opening a dynamic-rendering scope per pass with the derived load/store ops, invoking
/// the pass's record callback, and presenting. The barriers and layout transitions are derived rather than
/// written by hand.
/// <para>
/// <b>Device vs graph.</b> Device-level resources (context, allocator, command buffers, sync, swapchain,
/// pipeline cache, meshes, descriptors, ImGui) live for the engine's lifetime and survive a graph swap;
/// graph-level resources (the derived plan, transients, readback buffers, persistent-state tracking) are rebuilt
/// by <c>SetGraph</c>, reusing the device and cache — structural changes rebuild the baked graph (cheap and
/// cached), and that's also the basis for runtime demo switching.
/// </para>
/// <para>
/// <b>Frames in flight.</b> Runs 2 frames in flight, paced on a per-frame fence. The safety this
/// needs — a frame's writes to a transient must not clobber a still-executing earlier frame's reads of the
/// same image — comes from <see cref="VulkanTransientResources"/> giving each transient one physical image per
/// frame slot; each frame records against its own slot (<see cref="_currentFrame"/>). The swapchain supplies
/// distinct backbuffer images itself, and recording is serial on the CPU (the image handle is baked into the
/// command buffer), so the backbuffer needs no extra buffering.
/// </para>
/// </summary>
internal sealed unsafe partial class VulkanEngine : EngineBase
{
	private const int MaxFramesInFlight = 2;

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly IVulkanPresentTarget _presentTarget;
	private readonly bool _offscreen;
	private readonly string _shaderDir;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly VulkanTechniqueCache _techniques;
	private readonly VulkanPipelineCache _pipelineCache;
	private readonly VulkanUploadBatch _uploads;
	private readonly VulkanMeshStore _meshes;
	// Frames numbered from 1 as they are submitted: the newest submitted, the one each in-flight slot last carried,
	// and the newest known complete. A released mesh is freed once the last frame that could read it completes.
	private ulong _submittedFrames;
	private readonly ulong[] _slotFrame = new ulong[MaxFramesInFlight];
	private ulong _completedFrame;
	private readonly VulkanTextureStore _textures;
	private readonly VulkanDescriptorAllocator _descriptors;
	private readonly VulkanRenderContext _context;
	private readonly Dictionary<string, VulkanTechniqueTemplate> _registry = []; // logical name → backend baked-input form

	// Shader hot-reload, opt-in. The watcher fires on a background thread and maps the changed artifact to a
	// logical shader name; the base's reload registry queues it, and the render thread drains it (fence-safe).
	private AssetWatcher? _shaderWatcher;

	// Engine-owned shaders (tonemap/blit/fullscreen/exposure/imgui/debugview) ship embedded in THIS assembly
	// under the manifest prefix "<assembly>.Shaders." (the Loom SDK's Embed mode sets the same prefix from
	// $(AssemblyName)); app shaders are loose .spv/.json on disk. ResolveShaderAsset tries embedded first.
	private static readonly string EmbeddedShaderPrefix = typeof(VulkanEngine).Assembly.GetName().Name + ".Shaders.";

	// Graph-level state — rebuilt by RebuildGraphResources; the active graph + derived plan live on EngineBase.
	private IReadOnlySet<int> _aliasedHandoffIds = new HashSet<int>(); // resources that reuse an earlier alias's memory this frame
	private HashSet<int> _persistentIds = [];     // resources whose contents survive across frames
	private readonly Dictionary<int, ResourceState> _persistentState = []; // last state, carried across frames
	private HashSet<int> _bufferIds = [];          // storage/vertex buffer resources (emit buffer barriers, not image)
	private VulkanTransientResources? _transients;

	private CommandPool _commandPool;
	private CommandBuffer[] _commandBuffers = [];
	private Semaphore[] _imageAvailable = []; // per frame-in-flight
	private Semaphore[] _renderFinished = []; // per swapchain image
	private Fence[] _inFlight = [];           // per frame-in-flight

	// GPU→CPU readback: each g.Readback target is copied to a per-slot host-visible buffer every frame; the
	// result is read back ~2 frames later (when the slot's fence next signals) and handed to StoreReadback as the
	// raw, tightly-packed texels IEngine.Read returns. Rebuilt by SetGraph; empty when the graph declares no readback.
	private readonly List<ReadbackBuffers> _readbacks = [];

	// One g.Readback target's GPU→CPU staging: a host-visible buffer per frame slot the executor copies the target's
	// image into. The copy is tightly packed (Vulkan's image→buffer copy carries no row padding), so a slot's buffer
	// is handed straight to StoreReadback once its fence signals.
	private sealed class ReadbackBuffers(int id, int width, int height, int bytesPerTexel, Buffer[] buffers, Allocation[] allocations)
	{
		public int Id => id;
		public int ByteCount => width * height * bytesPerTexel;
		public Buffer[] Buffers => buffers;
		public Allocation[] Allocations => allocations;
		public bool[] Valid { get; } = new bool[buffers.Length];
	}

	// GPU per-pass timing: a TIMESTAMP query pool per frame slot, 2 queries (begin/end) per
	// pass. Decoded after the slot's fence — like the picking readback above — so the numbers are a frame or two
	// old. Runtime-toggled (off by default); device support (timestampPeriod / valid bits) is probed in the ctor.
	private QueryPool[] _timingPools = [];   // per frame slot, each sized 2*passCount
	private bool[] _timingValid = [];        // slot has recorded timestamps at least once
	private int _timingPassCount;
	private float _timestampPeriod;          // ns per tick (device limit)
	private uint _timestampValidBits;        // 0 ⇒ the graphics queue can't timestamp (timing unavailable)

	// This frame's host inputs, pulled from FrameInputs at the top of Render (cameras already clip-space-adjusted).
	private double _frameTime;

	private int _currentFrame;
	private bool _framebufferResized;
	private Extent2D _windowExtent;

	public override string BackendName => "Vulkan";
	public override string DeviceName => _ctx.DeviceName;

	public override EngineCapabilities Capabilities { get; }

	// The real swapchain, for the two calls only it (not VulkanOffscreenTarget) supports — vkAcquireNextImageKHR
	// and vkQueuePresentKHR. Only ever accessed when !_offscreen, where _presentTarget is guaranteed to be one.
	private VulkanSwapchain WindowSwapchain => (VulkanSwapchain)_presentTarget;

	public VulkanEngine(IVkSurface surface, int width, int height, VulkanEngineSettings settings)
		: this(new VulkanContext(surface, settings.EnableValidation, settings.PreferDiscreteGpu, settings.PreferSoftwareAdapter), offscreen: false, width, height, settings)
	{
	}

	/// <summary>
	/// Bring up a windowless engine (<see cref="Loom.Core.Hosting.Surface.Offscreen"/>): the present target is an
	/// owned <see cref="VulkanOffscreenTarget"/> instead of a swapchain, so <see cref="IEngine.RequestCapture"/>
	/// reads back an owned image rather than a presented one — no window, no <c>VkSurfaceKHR</c>.
	/// </summary>
	public VulkanEngine(int width, int height, VulkanEngineSettings settings)
		: this(new VulkanContext(settings.EnableValidation, settings.PreferDiscreteGpu, settings.PreferSoftwareAdapter), offscreen: true, width, height, settings)
	{
	}

	private VulkanEngine(VulkanContext ctx, bool offscreen, int width, int height, VulkanEngineSettings settings)
		: base(settings)
	{
		_ctx = ctx;
		_offscreen = offscreen;
		_vk = _ctx.Vk;
		_shaderDir = settings.ShaderDirectory;

		// GPU-timing device support. 0 valid bits ⇒ the graphics queue can't timestamp, so the profiler stays
		// permanently unavailable (the GpuTimingEnabled setter enforces this).
		_timestampPeriod = _ctx.TimestampPeriod;
		_timestampValidBits = _ctx.GraphicsTimestampValidBits;
		if (_timestampValidBits == 0)
			LoomLog.Warning("GPU timing unavailable: the graphics queue reports 0 timestamp valid bits.");

		Capabilities = new EngineCapabilities
		{
			GpuTiming = _timestampValidBits != 0,
			// Queue.AsyncCompute derives no cross-queue synchronisation yet and the backend owns one queue.
			AsyncCompute = false,
			SampleCounts = MapSampleCounts(_ctx.FramebufferSampleCounts),
			MaxColorAttachments = (int)_ctx.MaxColorAttachments,
			MinUniformBufferOffsetAlignment = (int)_ctx.MinUniformBufferOffsetAlignment,
			Float32Filterable = _ctx.Float32Filterable,
		};
		_windowExtent = new Extent2D((uint)width, (uint)height);
		_presentTarget = offscreen
			? new VulkanOffscreenTarget(_ctx, _windowExtent, MaxFramesInFlight)
			: new VulkanSwapchain(_ctx, _windowExtent, settings.VSync, settings.HdrOutput);

		// One VMA allocator backs both transients and meshes. The command pool must exist before the mesh
		// store (geometry uploads use a one-time copy command), so create it up front.
		_allocator = new VulkanMemoryAllocator(new VulkanMemoryAllocatorCreateInfo(
			Vk.Version13, _vk, _ctx.Instance, _ctx.PhysicalDevice, _ctx.Device));
		CreateCommandPool();

		_pipelineCache = new VulkanPipelineCache(_ctx, settings.PersistentPipelineCache);
		_techniques = new VulkanTechniqueCache(_ctx, _pipelineCache.Handle);
		_uploads = new VulkanUploadBatch(_ctx, _commandPool);
		_meshes = new VulkanMeshStore(_ctx, _allocator, _uploads);
		_textures = new VulkanTextureStore(_ctx, _allocator, _uploads);
		_descriptors = new VulkanDescriptorAllocator(_ctx, MaxFramesInFlight);
		_context = new VulkanRenderContext(_ctx, _techniques, _registry, _descriptors, _meshes, _textures, Materials, _allocator, MaxFramesInFlight, Validation);

		CreateCommandBuffers();
		CreateSyncObjects();

		// Shader hot-reload, opt-in. An external process rewrites the loose .spv/.json on disk; this watcher
		// (debounced) notifies us, and we pick up the new bytecode + rebuild affected pipelines. Off by default.
		if (settings.WatchShaderDirectory)
		{
			var source = new LayeredAssetSource(_shaderDir);
			_shaderWatcher = new AssetWatcher(source, [".spv", ".json"], debounceMs: 150);
			_shaderWatcher.Changed += OnShaderArtifactChanged;
			LoomLog.Info($"watching shaders for hot-reload: {_shaderDir}");
		}
	}

	// Vulkan's sample-count flag values are the counts themselves, and so are the neutral enum's, so this filters
	// rather than translates. Counts above 16 have no neutral spelling and are dropped: the graph's own ceiling is
	// 16, so reporting 32x would advertise something a domain cannot ask for.
	private static SampleCounts MapSampleCounts(SampleCountFlags flags)
	{
		var counts = SampleCounts.None;
		for (var count = 1; count <= 16; count <<= 1)
			if (((uint)flags & (uint)count) != 0)
				counts |= (SampleCounts)count;
		return counts;
	}

	// Background-thread watcher callback: map the changed artifact (e.g. "gbuffer.spv") back to its shader name and,
	// if a technique uses it, queue it for the render thread to reload. No GPU work here.
	private void OnShaderArtifactChanged(string leaf)
	{
		var shader = Path.GetFileNameWithoutExtension(leaf); // "gbuffer.spv"/"gbuffer.json" → "gbuffer"
		QueueShaderReload(shader);
	}

	protected override TechniqueShape BakeTechnique(string name, TechniqueDescriptor descriptor)
	{
		// Resolve the logical shader name to backend bytecode + reflection, and fold the neutral descriptor into
		// the backend's baked-input form. Shaders are named logically — no .spv path in app code.
		// The descriptor-binding counts come from the shader's reflection, not the app: the author states
		// only the pipeline intent (geometry/blend/filter/compute), and Slang's reflection says how many things
		// the shader binds.
		var reflection = ShaderReflection.FromBytes(ResolveShaderAsset(descriptor.Shader, ".json"), ShaderBindingTarget.SpirV);
		// Resolve the per-vertex layout: an explicit one, else the canonical pos/normal/uv when the technique
		// reads geometry, else none (a fullscreen/compute technique). A non-null Instance marks it instanced
		// (a second per-instance stream at binding 1).
		var layout = descriptor.Layout ?? (descriptor.HasVertexInput ? VertexLayout.MeshStandard : null);
		var spirv = ResolveShaderAsset(descriptor.Shader, ".spv");
		// Resolve the descriptor's source-level entry names against the ones the module actually declares: slangc
		// renames a lone entry point to "main" in SPIR-V, so a compute shader's declared `computeMain` is not
		// necessarily the name pipeline creation must be given. A two-entry graphics module matches verbatim and
		// passes through unchanged.
		var vertexEntry = descriptor.IsCompute
			? descriptor.VertexEntry
			: SpirvEntryPoints.Resolve(spirv, descriptor.VertexEntry, SpirvEntryPoints.Stage.Vertex, descriptor.Shader);
		var fragmentEntry = SpirvEntryPoints.Resolve(
			spirv,
			descriptor.IsCompute ? descriptor.ComputeEntry : descriptor.FragmentEntry,
			descriptor.IsCompute ? SpirvEntryPoints.Stage.Compute : SpirvEntryPoints.Stage.Fragment,
			descriptor.Shader);
		_registry[name] = new VulkanTechniqueTemplate(
			spirv,
			vertexEntry,
			fragmentEntry,
			ShaderBindingMap.From(reflection),
			descriptor.HasVertexInput,
			descriptor.Blend,
			descriptor.LinearFilter,
			descriptor.HasFragmentPush,
			descriptor.IsCompute,
			descriptor.Address,
			layout,
			descriptor.Instance,
			descriptor.HasListPush,
			descriptor.Cull,
			descriptor.DepthTest,
			descriptor.Bias,
			reflection.MaterialUniformSlot,
			reflection.MaterialParameterBytes);
		return new TechniqueShape(
			new MaterialLayout(reflection.SampleCount, reflection.MaterialParameterBytes),
			reflection.UniformBufferBytes,
			descriptor.HasFragmentPush ? VulkanTechnique.FragmentPushSize : 0,
			descriptor.HasListPush ? VulkanTechnique.ListPushSize : 0);
	}

	// Evict the baked pipelines after a hot-reload so the next bake rebuilds from the re-registered templates.
	protected override void FlushUploads() => _uploads.Flush();

	protected override void ResetPipelineCaches()
	{
		_context.ClearBaked(); // drop per-(name,formats) cached pipelines
		_techniques.Reset();   // dispose the stale VkPipelines; next Bake rebuilds from the new templates
	}

	public override Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) => _meshes.Create(vertices, indices);

	public override Mesh CreateMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices)
	{
		var stride = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
		if (stride != layout.Stride)
			throw new VulkanException($"Vertex element size ({stride} bytes) does not match the layout stride ({layout.Stride} bytes).");
		return _meshes.Create(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertices), stride, indices);
	}

	public override Mesh CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices) => _meshes.CreateSkinned(vertices, indices);

	// The next frame is the last that can read the mesh: it is the one the upload batch filling a just-created mesh
	// is submitted ahead of.
	public override void ReleaseMesh(Mesh mesh) => _meshes.Release(mesh.Id, _submittedFrames + 1);

	public override Texture CreateTexture(TextureData data) => _textures.Create(data);

	// Tear down the previous graph's transients/readback/timing pools before the base binds a new graph; the device
	// and caches survive. The base has already drained the GPU when a prior graph existed.
	protected override void ReleaseGraphResources()
	{
		DisposeGraphResources();
		_persistentState.Clear();
	}

	protected override void GrowStorage(int id, int newByteSize) => _transients!.GrowStorage(id, newByteSize);

	protected override void RebuildGraphResources(RenderGraph authored, RenderGraph executed)
	{
		// Every bucket member after the first reuses an earlier alias's memory this frame, so its Undefined→… birth
		// transition must wait on all prior work to that memory (the aliasing barrier — see EmitTransition); the first
		// member is covered by the per-frame fence instead.
		_aliasedHandoffIds = Aliases.AliasedMemberIds();

		// Persistent resources (auto-exposure history) keep their contents across frames; storage/vertex buffers
		// emit buffer barriers rather than image transitions. Classify the graph's resources once.
		_persistentIds = [];
		_bufferIds = [];
		foreach (var resource in authored.Resources)
		{
			if (resource.Kind == ResourceKind.Persistent)
				_persistentIds.Add(resource.Id);
			if (resource.Kind == ResourceKind.Buffer)
				_bufferIds.Add(resource.Id);
		}

		// Build transients from `executed` so the injected debug-sample edges add SampledBit to inspected resources.
		_transients = new VulkanTransientResources(_ctx, executed, Aliases, _presentTarget.ImageFormat, _presentTarget.Extent, MaxFramesInFlight, _allocator);
		_context.SetTransients(_transients);

		// Readback staging: one host-visible buffer per frame slot for each g.Readback target.
		foreach (var target in authored.ReadbackTargets)
			_readbacks.Add(CreateReadbackStaging(target));

		// GPU-timing pools: 2 timestamps (begin/end) per pass, one pool per frame slot. Sized to this graph's
		// pass count, so a swap to a graph with a different count reallocates (DisposeGraphResources freed the old).
		_timingPassCount = Barriers.Passes.Length;
		ResetTimings();
		if (_timestampValidBits != 0 && _timingPassCount > 0)
		{
			_timingPools = new QueryPool[MaxFramesInFlight];
			_timingValid = new bool[MaxFramesInFlight];
			var info = new QueryPoolCreateInfo
			{
				SType = StructureType.QueryPoolCreateInfo,
				QueryType = QueryType.Timestamp,
				QueryCount = (uint)(2 * _timingPassCount),
			};
			for (var i = 0; i < MaxFramesInFlight; i++)
				_vk.CreateQueryPool(_ctx.Device, in info, null, out _timingPools[i]).Check("vkCreateQueryPool");
		}
	}

	public override void Resize(int width, int height)
	{
		_windowExtent = new Extent2D((uint)width, (uint)height);
		_framebufferResized = true;
	}

	public override void WaitIdle()
	{
		_vk.DeviceWaitIdle(_ctx.Device);
		_completedFrame = _submittedFrames;
		_meshes.CollectReleased(_completedFrame);
	}

	// Resolve a logical shader name (TechniqueDescriptor.Shader) + asset extension (".spv" or its ".json"
	// reflection sidecar) to bytes. Engine shaders are embedded in this assembly; app shaders are loose
	// files under VulkanEngineSettings.ShaderDirectory. Shaders are named logically — no .spv path in app code
	// — and the directory is a backend/host concern kept out of the describe layer.
	private byte[] ResolveShaderAsset(string name, string extension)
	{
		var assembly = typeof(VulkanEngine).Assembly;
		using var embedded = assembly.GetManifestResourceStream(EmbeddedShaderPrefix + name + extension);
		if (embedded is not null)
		{
			var bytes = new byte[embedded.Length];
			embedded.ReadExactly(bytes);
			return bytes;
		}

		var path = Path.Combine(_shaderDir, name + extension);
		if (File.Exists(path))
			return File.ReadAllBytes(path);

		throw new ShaderAssetMissingException(
			name,
			$"Shader asset '{name}{extension}' not found: neither embedded ('{EmbeddedShaderPrefix}{name}{extension}') nor on disk ('{path}'). Did the app's shaders build, or is an engine shader missing from Loom.Backend.Vulkan?");
	}

	protected override void RenderFrame(FrameInputs inputs)
	{
		ServicePendingUploads();
		if (Graph is null || _transients is null)
			throw new InvalidOperationException("No active graph; call SetGraph before Render.");

		_frameTime = inputs.Time;
		DrawFrame(inputs.DeltaSeconds);
	}

	private void CreateCommandPool()
	{
		CommandPoolCreateInfo info = new()
		{
			SType = StructureType.CommandPoolCreateInfo,
			Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
			QueueFamilyIndex = _ctx.GraphicsFamily,
		};
		_vk.CreateCommandPool(_ctx.Device, in info, null, out _commandPool).Check("vkCreateCommandPool");
	}

	private void CreateCommandBuffers()
	{
		_commandBuffers = new CommandBuffer[MaxFramesInFlight];
		CommandBufferAllocateInfo info = new()
		{
			SType = StructureType.CommandBufferAllocateInfo,
			CommandPool = _commandPool,
			Level = CommandBufferLevel.Primary,
			CommandBufferCount = MaxFramesInFlight,
		};
		fixed (CommandBuffer* p = _commandBuffers)
			_vk.AllocateCommandBuffers(_ctx.Device, in info, p).Check("vkAllocateCommandBuffers");
		for (var i = 0; i < _commandBuffers.Length; i++)
			_ctx.SetObjectName(ObjectType.CommandBuffer, (ulong)_commandBuffers[i].Handle, $"frame#{i}");
	}

	private void CreateSyncObjects()
	{
		_imageAvailable = new Semaphore[MaxFramesInFlight];
		_inFlight = new Fence[MaxFramesInFlight];

		SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };
		FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };

		for (var i = 0; i < MaxFramesInFlight; i++)
		{
			_vk.CreateSemaphore(_ctx.Device, in semInfo, null, out _imageAvailable[i]).Check("vkCreateSemaphore");
			_vk.CreateFence(_ctx.Device, in fenceInfo, null, out _inFlight[i]).Check("vkCreateFence");
		}

		CreateRenderFinishedSemaphores();
	}

	private void CreateRenderFinishedSemaphores()
	{
		_renderFinished = new Semaphore[_presentTarget.ImageCount];
		SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };
		for (var i = 0; i < _renderFinished.Length; i++)
			_vk.CreateSemaphore(_ctx.Device, in semInfo, null, out _renderFinished[i]).Check("vkCreateSemaphore");
	}

	// Rebuild the present target at the current extent — the windowed swapchain (on resize or ErrorOutOfDateKhr/
	// SuboptimalKhr) or the offscreen target's owned images (on Resize); both implement IVulkanPresentTarget.
	private void RecreatePresentTarget()
	{
		if (_windowExtent.Width == 0 || _windowExtent.Height == 0)
			return;

		_vk.DeviceWaitIdle(_ctx.Device);
		var oldImageCount = _presentTarget.ImageCount;
		_presentTarget.Recreate(_windowExtent);
		// A reallocated persistent is a different image with no contents, so the layout carried over from the previous
		// frame no longer describes it — drop it and let the plan's Undefined stand, which is what a fresh image needs.
		foreach (var id in _transients?.Recreate(_presentTarget.Extent) ?? [])
			_persistentState.Remove(id);
		// Reallocating a transient destroys its image view, and descriptor sets are cached by the views they name.
		// The DeviceWaitIdle above is what makes freeing those sets safe here.
		_context.FlushDescriptorCache();
		RecreateSwapchainSizedReadback();

		if (_presentTarget.ImageCount != oldImageCount)
		{
			DestroyRenderFinishedSemaphores();
			CreateRenderFinishedSemaphores();
		}
	}

	// One g.Readback target's per-slot host-visible staging, sized against the live swapchain extent — the same extent
	// the target's own transient image is sized to, which is what the copy region is built from at record time.
	private ReadbackBuffers CreateReadbackStaging(ResourceHandle target)
	{
		var (width, height) = target.Resolve((int)_presentTarget.Extent.Width, (int)_presentTarget.Extent.Height);
		var bpt = target.Format.BytesPerTexel;
		var buffers = new Buffer[MaxFramesInFlight];
		var allocations = new Allocation[MaxFramesInFlight];
		for (var i = 0; i < MaxFramesInFlight; i++)
			(buffers[i], allocations[i]) = CreateStagingBuffer((ulong)(width * height * bpt));
		return new ReadbackBuffers(target.Id, width, height, bpt, buffers, allocations);
	}

	private void DestroyReadbackStaging(ReadbackBuffers readback)
	{
		for (var i = 0; i < readback.Buffers.Length; i++)
		{
			if (readback.Buffers[i].Handle != 0)
				_vk.DestroyBuffer(_ctx.Device, readback.Buffers[i], null);
			readback.Allocations[i]?.Dispose();
		}
	}

	// Rebuild the staging for every swapchain-sized readback target at the new extent. Its image was just reallocated
	// by the transients' own resize, and RecordReadback builds the copy region from that live extent — so a buffer
	// still sized for the old one is overrun the moment the window grows. A fixed-size target is left alone, exactly
	// as its image is. The caller has already drained the GPU, so freeing in place is safe.
	private void RecreateSwapchainSizedReadback()
	{
		if (Graph is null)
			return;

		foreach (var target in Graph.ReadbackTargets)
		{
			if (!target.FollowsSwapchain)
				continue;
			var index = _readbacks.FindIndex(r => r.Id == target.Id);
			if (index < 0)
				continue;
			DestroyReadbackStaging(_readbacks[index]);
			_readbacks[index] = CreateReadbackStaging(target);
			// The bytes already handed to the app describe the old size; an empty Read is honest, a wrong-sized one isn't.
			InvalidateReadback(target.Id);
		}
	}

	private void DestroyRenderFinishedSemaphores()
	{
		foreach (var semaphore in _renderFinished)
			if (semaphore.Handle != 0)
				_vk.DestroySemaphore(_ctx.Device, semaphore, null);
		_renderFinished = [];
	}

	// Release the current graph's GPU resources (transients + readback staging). Used both when swapping the
	// active graph and on teardown. The caller drains the GPU first.
	private void DisposeGraphResources()
	{
		_transients?.Dispose();
		_transients = null;

		foreach (var readback in _readbacks)
			DestroyReadbackStaging(readback);
		_readbacks.Clear();

		foreach (var pool in _timingPools)
			if (pool.Handle != 0)
				_vk.DestroyQueryPool(_ctx.Device, pool, null);
		_timingPools = [];
		_timingValid = [];
		ResetTimings();
		_timingPassCount = 0;
	}

	public override void Dispose()
	{
		_shaderWatcher?.Dispose(); // stop the hot-reload watcher before tearing the device down
		_vk.DeviceWaitIdle(_ctx.Device);

		DisposeGraphResources();    // transients + readback staging (graph-level)
		_context.Dispose();
		_descriptors.Dispose();
		_uploads.Dispose(); // before the stores — it holds their staging buffers until the last submission retires
		_meshes.Dispose();
		_textures.Dispose(); // before the allocator — it holds VMA image allocations
		_allocator.Dispose(); // after every image/buffer it backed is freed
		_techniques.Dispose();
		_pipelineCache.Dispose(); // after the pipelines it seeded — this is where the blob is written back out

		DestroyRenderFinishedSemaphores();
		foreach (var semaphore in _imageAvailable)
			if (semaphore.Handle != 0)
				_vk.DestroySemaphore(_ctx.Device, semaphore, null);
		foreach (var fence in _inFlight)
			if (fence.Handle != 0)
				_vk.DestroyFence(_ctx.Device, fence, null);

		if (_commandPool.Handle != 0)
			_vk.DestroyCommandPool(_ctx.Device, _commandPool, null);

		_presentTarget.Dispose();
		_ctx.Dispose(); // the device + instance — last, after everything that used them
	}
}
