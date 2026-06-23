using System.Diagnostics;

using Loom.Assets;
using Loom.Core;
using Loom.Core.Animation;
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
using VkFormat = Silk.NET.Vulkan.Format;
using GraphFormat = Loom.Core.Graph.Format;

namespace Loom.Vulkan;

/// <summary>
/// The Vulkan <see cref="IEngine"/> (Planning.md §2 Execute/Bake, §5 front door). It owns the long-lived
/// device (<see cref="VulkanContext"/>), the pipeline cache, the mesh store, and the descriptor allocator; the
/// <em>active graph</em> is swappable (<c>SetGraph</c>). On each <c>SetGraph</c> it derives a
/// <see cref="GraphSchedule"/>, <see cref="BarrierPlan"/>, and transient-memory plan once (the plan is
/// frame-stable, so the per-frame cost is recording, not deriving) and prepares the graph's transients; then
/// each <see cref="Render"/> records the plan into a command buffer — emitting <c>vkCmdPipelineBarrier2</c> from
/// the derived transitions, opening a dynamic-rendering scope per pass with the derived load/store ops, invoking
/// the pass's record callback, and presenting. The barriers and layout transitions M0/M1 wrote by hand are now
/// derived (Planning.md §2).
/// <para>
/// <b>Device vs graph.</b> Device-level resources (context, allocator, command buffers, sync, swapchain,
/// pipeline cache, meshes, descriptors, ImGui) live for the engine's lifetime and survive a graph swap;
/// graph-level resources (the derived plan, transients, readback buffers, persistent-state tracking) are rebuilt
/// by <c>SetGraph</c>, reusing the device and cache — Planning.md §8's "structural changes rebuild the
/// baked graph — cheap and cached," and the basis for runtime demo switching (M7-B7).
/// </para>
/// <para>
/// <b>Frames in flight (§12 M4).</b> Runs 2 frames in flight, paced on a per-frame fence. The safety this
/// needs — a frame's writes to a transient must not clobber a still-executing earlier frame's reads of the
/// same image — comes from <see cref="VulkanTransientResources"/> giving each transient one physical image per
/// frame slot; each frame records against its own slot (<see cref="_currentFrame"/>). The swapchain supplies
/// distinct backbuffer images itself, and recording is serial on the CPU (the image handle is baked into the
/// command buffer), so the backbuffer needs no extra buffering.
/// </para>
/// </summary>
internal sealed unsafe class VulkanEngine : EngineBase
{
	private const int MaxFramesInFlight = 2;
	private static readonly ClearValue ClearColor = new(color: new ClearColorValue(0, 0, 0, 1));
	private static readonly ClearValue ClearDepth = new(depthStencil: new ClearDepthStencilValue(1.0f, 0));

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanSwapchain _swapchain;
	private readonly string _shaderDir;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly VulkanTechniqueCache _techniques;
	private readonly VulkanMeshStore _meshes;
	private readonly VulkanTextureStore _textures;
	private readonly MaterialStore _materials;
	private readonly VulkanDescriptorAllocator _descriptors;
	private readonly VulkanRenderContext _context;
	private readonly Dictionary<string, VulkanTechniqueTemplate> _registry = []; // logical name → backend baked-input form

	// Shader hot-reload (M13), opt-in. The watcher fires on a background thread and maps the changed artifact to a
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

	// GPU→CPU readback (§7.4): each g.Readback target is copied to a per-slot host-visible buffer every frame; the
	// result is read back ~2 frames later (when the slot's fence next signals) and handed to StoreReadback as the
	// raw, tightly-packed texels IEngine.Read returns. Rebuilt by SetGraph; empty when the graph declares no readback.
	private readonly List<ReadbackBuffers> _readbacks = [];

	// One g.Readback target's GPU→CPU staging: a host-visible buffer per frame slot the executor copies the target's
	// image into. The copy is tightly packed (Vulkan's image→buffer copy carries no row padding), so a slot's buffer
	// is handed straight to StoreReadback once its fence signals.
	private sealed class ReadbackBuffers(int id, int width, int height, int bytesPerTexel, Buffer[] buffers, DeviceMemory[] memories)
	{
		public int Id => id;
		public int ByteCount => width * height * bytesPerTexel;
		public Buffer[] Buffers => buffers;
		public DeviceMemory[] Memories => memories;
		public bool[] Valid { get; } = new bool[buffers.Length];
	}

	// GPU per-pass timing (§12 M13 profiler): a TIMESTAMP query pool per frame slot, 2 queries (begin/end) per
	// pass. Decoded after the slot's fence — like the picking readback above — so the numbers are a frame or two
	// old. Runtime-toggled (off by default); device support (timestampPeriod / valid bits) is probed in the ctor.
	private QueryPool[] _timingPools = [];   // per frame slot, each sized 2*passCount
	private bool[] _timingValid = [];        // slot has recorded timestamps at least once
	private int _timingPassCount;
	private float _timestampPeriod;          // ns per tick (device limit)
	private uint _timestampValidBits;        // 0 ⇒ the graphics queue can't timestamp (timing unavailable)

	// This frame's host inputs, pulled from FrameInputs at the top of Render (cameras already clip-space-adjusted).
	private Camera _frameCamera = Camera.Identity;
	private Camera _frameSun = Camera.Identity;
	private Camera[] _frameCascades = [];
	private float _frameTime;
	private IReadOnlyList<(DrawListId Id, DrawListData Data)> _frameDrawLists = [];

	private int _currentFrame;
	private bool _framebufferResized;
	private Extent2D _windowExtent;

	public override string BackendName => "Vulkan";
	public override string DeviceName => _ctx.DeviceName;

	// 0 valid bits ⇒ the graphics queue can't timestamp, so the profiler stays permanently unavailable.
	protected override bool CanEnableGpuTiming => _timestampValidBits != 0;

	public VulkanEngine(IVkSurface surface, int width, int height, VulkanEngineSettings settings)
	{
		_ctx = new VulkanContext(surface, settings.EnableValidation, settings.PreferDiscreteGpu);
		_vk = _ctx.Vk;
		_shaderDir = settings.ShaderDirectory;

		// GPU-timing device support (M13). 0 valid bits ⇒ the graphics queue can't timestamp, so the profiler stays
		// permanently unavailable (the GpuTimingEnabled setter enforces this).
		_timestampPeriod = _ctx.TimestampPeriod;
		_timestampValidBits = _ctx.GraphicsTimestampValidBits;
		if (_timestampValidBits == 0)
			Console.WriteLine("[loom] GPU timing unavailable: the graphics queue reports 0 timestamp valid bits.");
		_windowExtent = new Extent2D((uint)width, (uint)height);
		_swapchain = new VulkanSwapchain(_ctx, _windowExtent, settings.VSync, settings.HdrOutput);

		// One VMA allocator backs both transients and meshes. The command pool must exist before the mesh
		// store (geometry uploads use a one-time copy command), so create it up front.
		_allocator = new VulkanMemoryAllocator(new VulkanMemoryAllocatorCreateInfo(
			Vk.Version13, _vk, _ctx.Instance, _ctx.PhysicalDevice, _ctx.Device));
		CreateCommandPool();

		_techniques = new VulkanTechniqueCache(_ctx);
		_meshes = new VulkanMeshStore(_ctx, _allocator, _commandPool);
		_textures = new VulkanTextureStore(_ctx, _allocator, _commandPool);
		_materials = new MaterialStore();
		_descriptors = new VulkanDescriptorAllocator(_ctx, MaxFramesInFlight);
		_context = new VulkanRenderContext(_ctx, _techniques, _registry, _descriptors, _meshes, _textures, _materials, _allocator, MaxFramesInFlight);

		CreateCommandBuffers();
		CreateSyncObjects();

		// Shader hot-reload (M13), opt-in. An external process rewrites the loose .spv/.json on disk; this watcher
		// (debounced) notifies us, and we pick up the new bytecode + rebuild affected pipelines. Off by default.
		if (settings.WatchShaderDirectory)
		{
			var source = new LayeredAssetSource(_shaderDir);
			_shaderWatcher = new AssetWatcher(source, [".spv", ".json"], debounceMs: 150);
			_shaderWatcher.Changed += OnShaderArtifactChanged;
			Console.WriteLine($"[loom] watching shaders for hot-reload: {_shaderDir}");
		}
	}

	// Background-thread watcher callback: map the changed artifact (e.g. "gbuffer.spv") back to its shader name and,
	// if a technique uses it, queue it for the render thread to reload. No GPU work here.
	private void OnShaderArtifactChanged(string leaf)
	{
		var shader = Path.GetFileNameWithoutExtension(leaf); // "gbuffer.spv"/"gbuffer.json" → "gbuffer"
		QueueShaderReload(shader);
	}

	protected override void BakeTechnique(string name, TechniqueDescriptor descriptor)
	{
		// Resolve the logical shader name to backend bytecode + reflection, and fold the neutral descriptor into
		// the backend's baked-input form. Shaders are named logically — no .spv path in app code (Planning.md §5).
		// The descriptor-binding counts come from the shader's reflection, not the app (M7-B4): the author states
		// only the pipeline intent (geometry/blend/filter/compute), and Slang's reflection says how many things
		// the shader binds.
		var reflection = ShaderReflection.FromBytes(ResolveShaderAsset(descriptor.Shader, ".json"));
		// Resolve the per-vertex layout: an explicit one, else the canonical pos/normal/uv when the technique
		// reads geometry, else none (a fullscreen/compute technique). A non-null Instance marks it instanced
		// (a second per-instance stream at binding 1). (M12 — per-technique vertex layouts.)
		var layout = descriptor.Layout ?? (descriptor.HasVertexInput ? VertexLayout.MeshStandard : null);
		_registry[name] = new VulkanTechniqueTemplate(
			ResolveShaderAsset(descriptor.Shader, ".spv"),
			descriptor.VertexEntry,
			descriptor.FragmentEntry,
			reflection.SampleCount,
			descriptor.HasVertexInput,
			descriptor.Blend,
			descriptor.LinearFilter,
			descriptor.HasFragmentPush,
			descriptor.IsCompute,
			reflection.StorageImageCount,
			reflection.StorageBufferCount,
			descriptor.Address,
			layout,
			descriptor.Instance,
			reflection.UniformBufferCount,
			descriptor.HasListPush,
			descriptor.Cull);

	}

	// Evict the baked pipelines after a hot-reload so the next bake rebuilds from the re-registered templates.
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

	public override Texture CreateTexture(TextureData data) => _textures.Create(data);

	public override Material CreateMaterial(params ReadOnlySpan<Texture> textures) => _materials.Create(textures);

	// Tear down the previous graph's transients/readback/timing pools before the base binds a new graph; the device
	// and caches survive. The base has already drained the GPU when a prior graph existed.
	protected override void ReleaseGraphResources()
	{
		DisposeGraphResources();
		_persistentState.Clear();
	}

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
		_transients = new VulkanTransientResources(_ctx, executed, Aliases, _swapchain.ImageFormat, _swapchain.Extent, MaxFramesInFlight, _allocator);
		_context.SetTransients(_transients);

		// Readback staging (§7.4): one host-visible buffer per frame slot for each g.Readback target.
		foreach (var target in authored.ReadbackTargets)
		{
			var (width, height) = target.Resolve();
			var bpt = BytesPerTexel(target.Format);
			var buffers = new Buffer[MaxFramesInFlight];
			var memories = new DeviceMemory[MaxFramesInFlight];
			for (var i = 0; i < MaxFramesInFlight; i++)
				(buffers[i], memories[i]) = CreateStagingBuffer((ulong)(width * height * bpt));
			_readbacks.Add(new ReadbackBuffers(target.Id, width, height, bpt, buffers, memories));
		}

		// GPU-timing pools (M13): 2 timestamps (begin/end) per pass, one pool per frame slot. Sized to this graph's
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

	public override void WaitIdle() => _vk.DeviceWaitIdle(_ctx.Device);

	// Resolve a logical shader name (TechniqueDescriptor.Shader) + asset extension (".spv" or its ".json"
	// reflection sidecar, M7-B4) to bytes. Engine shaders are embedded in this assembly; app shaders are loose
	// files under VulkanEngineSettings.ShaderDirectory. Shaders are named logically — no .spv path in app code
	// (Planning.md §5) — and the directory is a backend/host concern kept out of the describe layer.
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

		throw new VulkanException(
			$"Shader asset '{name}{extension}' not found: neither embedded ('{EmbeddedShaderPrefix}{name}{extension}') nor on disk ('{path}'). Did the app's shaders build, or is an engine shader missing from Loom.Vulkan?");
	}

	// Vulkan clip space is +Y-down, so the projection's Y row is negated on ingest. The host supplies a neutral
	// (+Y-up) projection in FrameInputs; doing the flip here keeps the API convention in the backend (M7-B5) and
	// applies it consistently to both the render MVP and any matrix a shader samples with (the sun's light-space
	// VP the lighting/shadow passes share). A +Y-up backend (D3D12) would apply identity here instead.
	private static Camera ClipSpace(Camera camera) => camera.FlipClipY();

	public override void Render(FrameInputs inputs)
	{
		ArgumentNullException.ThrowIfNull(inputs);
		if (Graph is null || _transients is null)
			throw new InvalidOperationException("No active graph; call SetGraph before Render.");

		_frameCamera = ClipSpace(inputs.Camera);
		_frameSun = ClipSpace(inputs.SunView);
		// Flip each cascade's clip space the same way (M15-3) — neutral matrices in, the backend's Y-flip applied.
		if (_frameCascades.Length != inputs.Cascades.Count)
			_frameCascades = new Camera[inputs.Cascades.Count];
		for (var i = 0; i < inputs.Cascades.Count; i++)
			_frameCascades[i] = ClipSpace(inputs.Cascades[i]);
		_frameTime = inputs.Time;
		_frameDrawLists = inputs.DrawLists;
		DrawFrame((float)inputs.DeltaSeconds);
	}

	private void DrawFrame(float deltaSeconds)
	{
		if (_windowExtent.Width == 0 || _windowExtent.Height == 0)
			return; // Minimized — nothing to draw.

		DrainShaderReloads(); // M13: pick up any hot-reloaded shaders before recording this frame

		_vk.WaitForFences(_ctx.Device, 1, in _inFlight[_currentFrame], true, ulong.MaxValue);

		// The fence just signalled, so this slot's readback buffers hold the copies from when it last ran (~2
		// frames ago). Hand them to StoreReadback before this frame overwrites them — that's the latency §7.4
		// is upfront about. (Done before recording, so IEngine.Read always sees the freshest available.)
		DownloadReadback(_currentFrame);
		// Same story for the timing pool: the fence guarantees this slot's last timestamps are complete.
		DownloadTimings(_currentFrame);

		uint imageIndex = 0;
		var acquire = _ctx.KhrSwapchain.AcquireNextImage(
			_ctx.Device, _swapchain.Handle, ulong.MaxValue,
			_imageAvailable[_currentFrame], default, ref imageIndex);

		if (acquire == Result.ErrorOutOfDateKhr)
		{
			RecreateSwapchain();
			return;
		}
		if (acquire is not (Result.Success or Result.SuboptimalKhr))
			acquire.Check("vkAcquireNextImageKHR");

		_vk.ResetFences(_ctx.Device, 1, in _inFlight[_currentFrame]);

		var capturePath = CapturePath;
		// PNG capture decodes an 8-bit RGBA/BGRA swapchain; an HDR swapchain (FP16 / 10-bit packed) would
		// decode to garbage, so skip it there rather than write a corrupt file (HDR is verified on-display).
		if (capturePath is not null && !CaptureSupported())
		{
			Console.WriteLine($"[loom] capture skipped: swapchain format {_swapchain.ImageFormat} is not an 8-bit RGBA/BGRA format (HDR output).");
			CapturePath = null;
			capturePath = null;
		}
		var captureSize = (ulong)_swapchain.Extent.Width * _swapchain.Extent.Height * 4;
		Buffer captureBuffer = default;
		DeviceMemory captureMemory = default;
		if (capturePath is not null)
			(captureBuffer, captureMemory) = CreateStagingBuffer(captureSize);

		// This frame slot's prior work is done (fence waited), so its descriptor sets are idle: recycle them.
		_descriptors.BeginFrame(_currentFrame);
		_transients!.SetBackbuffer(_currentFrame, _swapchain.Images[imageIndex], _swapchain.ImageViews[imageIndex], _swapchain.Extent);

		var cmd = _commandBuffers[_currentFrame];
		_vk.ResetCommandBuffer(cmd, 0);
		RecordGraph(cmd, captureBuffer, deltaSeconds);

		SemaphoreSubmitInfo waitInfo = new()
		{
			SType = StructureType.SemaphoreSubmitInfo,
			Semaphore = _imageAvailable[_currentFrame],
			StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
		};
		SemaphoreSubmitInfo signalInfo = new()
		{
			SType = StructureType.SemaphoreSubmitInfo,
			Semaphore = _renderFinished[imageIndex],
			StageMask = PipelineStageFlags2.AllCommandsBit,
		};
		CommandBufferSubmitInfo cmdInfo = new()
		{
			SType = StructureType.CommandBufferSubmitInfo,
			CommandBuffer = cmd,
		};
		SubmitInfo2 submit = new()
		{
			SType = StructureType.SubmitInfo2,
			WaitSemaphoreInfoCount = 1,
			PWaitSemaphoreInfos = &waitInfo,
			CommandBufferInfoCount = 1,
			PCommandBufferInfos = &cmdInfo,
			SignalSemaphoreInfoCount = 1,
			PSignalSemaphoreInfos = &signalInfo,
		};
		_vk.QueueSubmit2(_ctx.GraphicsQueue, 1, in submit, _inFlight[_currentFrame]).Check("vkQueueSubmit2");

		if (capturePath is not null)
		{
			_vk.WaitForFences(_ctx.Device, 1, in _inFlight[_currentFrame], true, ulong.MaxValue);
			WriteCapture(captureMemory, captureSize, _swapchain.Extent.Width, _swapchain.Extent.Height, capturePath);
			_vk.DestroyBuffer(_ctx.Device, captureBuffer, null);
			_vk.FreeMemory(_ctx.Device, captureMemory, null);
			CapturePath = null;
		}

		var swapchain = _swapchain.Handle;
		var renderFinished = _renderFinished[imageIndex];
		var presentIndex = imageIndex;
		PresentInfoKHR present = new()
		{
			SType = StructureType.PresentInfoKhr,
			WaitSemaphoreCount = 1,
			PWaitSemaphores = &renderFinished,
			SwapchainCount = 1,
			PSwapchains = &swapchain,
			PImageIndices = &presentIndex,
		};
		var result = _ctx.KhrSwapchain.QueuePresent(_ctx.PresentQueue, in present);
		if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr || _framebufferResized)
		{
			_framebufferResized = false;
			RecreateSwapchain();
		}
		else if (result != Result.Success)
		{
			result.Check("vkQueuePresentKHR");
		}

		_currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
	}

	private void RecordGraph(CommandBuffer cmd, Buffer captureBuffer, float deltaTime)
	{
		CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
		_vk.BeginCommandBuffer(cmd, in beginInfo).Check("vkBeginCommandBuffer");

		_context.BeginFrame(cmd, deltaTime, _frameTime, _currentFrame, _frameCamera, _frameSun, _frameCascades, _frameDrawLists, _swapchain.Extent, _swapchain.HdrActive);

		// Flush each parameter buffer's CPU shadow into this slot's copy before any pass reads it. Re-uploading
		// every frame (cheap — these are tiny) means the last IEngine.Write persists across frames, so a static
		// param is written once and an animated one is written per frame; either way the slot the GPU reads is current.
		foreach (var (id, bytes) in ConstantsShadows)
			_transients!.WriteConstants(_currentFrame, id, bytes);
		// Same for the app-writable storage buffers (IEngine.Write span).
		foreach (var (id, bytes) in StorageShadows)
			_transients!.WriteStorage(_currentFrame, id, bytes);

		// GPU timing (M13): reset this slot's pool on the device timeline, then bracket each pass's own work with a
		// begin/end timestamp. The reset is inside the command buffer (no hostQueryReset feature needed).
		var timing = GpuTimingEnabled && _timingPools.Length > 0;
		if (timing)
			_vk.CmdResetQueryPool(cmd, _timingPools[_currentFrame], 0, (uint)(2 * _timingPassCount));

		var passIndex = 0;
		foreach (var pass in Barriers.Passes)
		{
			// Wrap each pass (its pre-barriers + its draws/dispatch) in a debug-label scope, nested under groups
			// derived from the dotted pass name — so a RenderDoc capture reads as a "bloom" group containing
			// "down1", "up0", … instead of a flat command list. [Conditional]: vanishes without UseNamedGPUObjects.
			BeginPassLabels(cmd, pass);
			foreach (var transition in pass.Transitions)
				EmitTransition(cmd, transition);
			// Timestamp the pass's own work (after its pre-barriers, around RecordPass) — the barriers belong to the
			// transition, not the pass. Top/Bottom-of-pipe is the portable choice; the device coarsens as needed.
			if (timing)
				_vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.TopOfPipeBit, _timingPools[_currentFrame], (uint)(2 * passIndex));
			RecordPass(cmd, pass);
			if (timing)
				_vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.BottomOfPipeBit, _timingPools[_currentFrame], (uint)(2 * passIndex + 1));
			_ctx.EndDebugLabel(cmd); // close the leaf scope opened by BeginPassLabels
			passIndex++;
		}
		if (timing)
			_timingValid[_currentFrame] = true;
		CloseTrailingLabels(cmd); // close any groups still open after the last pass

		RecordReadback(cmd);
		FinishFrame(cmd, captureBuffer);

		_vk.EndCommandBuffer(cmd).Check("vkEndCommandBuffer");
	}

	// Open this pass's debug-label scopes: close the groups it leaves behind, open the ones it enters, then open
	// its own leaf scope (closed by the caller after the pass records). [Conditional] so the whole thing — loops
	// and all — compiles away without UseNamedGPUObjects.
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	private void BeginPassLabels(CommandBuffer cmd, PassBarriers pass)
	{
		for (var i = 0; i < pass.LabelCloses; i++)
			_ctx.EndDebugLabel(cmd);
		foreach (var group in pass.LabelOpens)
			_ctx.BeginDebugLabel(cmd, group);
		_ctx.BeginDebugLabel(cmd, pass.LeafLabel);
	}

	// Close the debug-label groups still open after the final pass (balances BeginPassLabels' opens).
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	private void CloseTrailingLabels(CommandBuffer cmd)
	{
		for (var i = 0; i < Barriers.TrailingLabelCloses; i++)
			_ctx.EndDebugLabel(cmd);
	}

	// Copy each readback target (its last graph use is a .Samples, so it's in SHADER_READ) into this slot's
	// host-visible buffer. The slot's fence guards them; DownloadReadback hands them to the app ~2 frames later. §7.4.
	private void RecordReadback(CommandBuffer cmd)
	{
		foreach (var readback in _readbacks)
		{
			ref var image = ref _transients!.Get(_currentFrame, readback.Id);
			var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
			TransitionImage(cmd, image.Image, range,
				ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal,
				PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit);

			BufferImageCopy region = new()
			{
				ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
				ImageExtent = new Extent3D(image.Extent.Width, image.Extent.Height, 1),
			};
			_vk.CmdCopyImageToBuffer(cmd, image.Image, ImageLayout.TransferSrcOptimal, readback.Buffers[_currentFrame], 1, in region);
			readback.Valid[_currentFrame] = true;
		}
	}

	// Hand this slot's readback buffers to StoreReadback as raw, tightly-packed bytes (Vulkan's image→buffer copy
	// carries no row padding). Called after the slot's fence signals, so the data is the copy from when the slot
	// last ran (a frame or two ago); the app decodes whatever format it declared.
	private void DownloadReadback(int slot)
	{
		foreach (var readback in _readbacks)
		{
			if (!readback.Valid[slot])
				continue;
			var count = readback.ByteCount;
			void* mapped;
			_vk.MapMemory(_ctx.Device, readback.Memories[slot], 0, (ulong)count, 0, &mapped).Check("vkMapMemory");
			StoreReadback(readback.Id, new ReadOnlySpan<byte>(mapped, count));
			_vk.UnmapMemory(_ctx.Device, readback.Memories[slot]);
		}
	}

	// Bytes per texel of a readback target's format — the stride StoreReadback hands the app, and the size of each
	// per-slot staging buffer. Extend as new readback formats appear (the picking id buffer is R16F).
	private static int BytesPerTexel(GraphFormat format) => format switch
	{
		GraphFormat.R16F => 2,
		GraphFormat.Rg16F => 4,
		GraphFormat.R32Uint => 4,
		GraphFormat.Rgba8Unorm or GraphFormat.Rgba8Srgb => 4,
		GraphFormat.Rgba16F => 8,
		_ => throw new NotSupportedException($"Vulkan: readback of format {format} is not supported."),
	};

	// Read this slot's timestamps (begin/end per pass) and decode them into _timings + the frame total. Called after
	// the slot's fence signals, so the queries are complete. Mask to the queue's valid bits before decoding; the
	// timestamp period is nanoseconds per tick, so ns/1e6 is the milliseconds-per-tick the decoder needs.
	private void DownloadTimings(int slot)
	{
		if (!GpuTimingEnabled || _timingPools.Length == 0 || _timingPassCount == 0 || !_timingValid[slot])
			return;

		var n = 2 * _timingPassCount;
		ulong* raw = stackalloc ulong[n];
		_vk.GetQueryPoolResults(_ctx.Device, _timingPools[slot], 0, (uint)n,
			(nuint)(n * sizeof(ulong)), raw, (ulong)sizeof(ulong), QueryResultFlags.Result64Bit).Check("vkGetQueryPoolResults");

		var mask = _timestampValidBits >= 64 ? ulong.MaxValue : (1UL << (int)_timestampValidBits) - 1UL;
		for (var i = 0; i < n; i++)
			raw[i] &= mask;
		DecodeTimings(new ReadOnlySpan<ulong>(raw, n), Barriers, _timestampPeriod / 1_000_000.0);
	}

	private void RecordPass(CommandBuffer cmd, PassBarriers pass)
	{
		// The app-writable parameter buffers this pass .Reads — bound to the technique's uniform slots 0..K-1.
		Span<int> uniformIds = stackalloc int[VulkanRenderContext.MaxUniformBuffers];
		var uniformCount = CollectUniformIds(pass.Pass, uniformIds);
		// The app-writable storage buffers this pass .Reads — bound to the technique's storage-buffer slots.
		Span<int> storageIds = stackalloc int[VulkanRenderContext.MaxStorageBuffers];
		var storageCount = CollectStorageIds(pass.Pass, storageIds);

		// Compute passes have no attachments and no dynamic-rendering scope — just bind + dispatch. The
		// resource transitions (storage GENERAL, the consumer's SHADER_READ) were already emitted before this.
		if (pass.Pass.Kind == PassKind.Compute)
		{
			_context.BeginPass([], default, false, uniformIds[..uniformCount], storageIds[..storageCount]); // no colour/depth targets; keeps the bake key stable
			pass.Pass.Record?.Invoke(_context);
			return;
		}

		var attachments = pass.Attachments;
		var color = stackalloc RenderingAttachmentInfo[attachments.Length];
		var colorCount = 0;
		RenderingAttachmentInfo depth = default;
		var hasDepth = false;
		var depthReadOnly = false;
		Extent2D area = default;
		var colorFormats = stackalloc VkFormat[attachments.Length];
		VkFormat depthFormat = default;

		foreach (var attachment in attachments)
		{
			ref var image = ref _transients!.Get(_currentFrame, attachment.Resource.Id);
			if (area.Width == 0)
				area = image.Extent; // all of a pass's attachments share a domain → one render area

			if (attachment.State == ResourceState.ColorAttachment)
			{
				colorFormats[colorCount] = image.Format;
				color[colorCount++] = new RenderingAttachmentInfo
				{
					SType = StructureType.RenderingAttachmentInfo,
					ImageView = image.View,
					ImageLayout = ImageLayout.ColorAttachmentOptimal,
					LoadOp = ToLoadOp(attachment.Load),
					StoreOp = ToStoreOp(attachment.Store),
					ClearValue = ClearColor,
				};
			}
			else
			{
				depthFormat = image.Format;
				depthReadOnly = attachment.State == ResourceState.DepthReadOnly;
				depth = new RenderingAttachmentInfo
				{
					SType = StructureType.RenderingAttachmentInfo,
					ImageView = image.View,
					ImageLayout = VulkanStateMapping.For(attachment.State).Layout,
					LoadOp = ToLoadOp(attachment.Load),
					StoreOp = ToStoreOp(attachment.Store),
					ClearValue = ClearDepth,
				};
				hasDepth = true;
			}
		}

		RenderingInfo info = new()
		{
			SType = StructureType.RenderingInfo,
			RenderArea = new Rect2D(new Offset2D(0, 0), area),
			LayerCount = 1,
			ColorAttachmentCount = (uint)colorCount,
			PColorAttachments = colorCount > 0 ? color : null,
			PDepthAttachment = hasDepth ? &depth : null,
		};
		_vk.CmdBeginRendering(cmd, in info);

		Viewport viewport = new()
		{
			X = 0,
			Y = 0,
			Width = area.Width,
			Height = area.Height,
			MinDepth = 0,
			MaxDepth = 1,
		};
		_vk.CmdSetViewport(cmd, 0, 1, in viewport);
		Rect2D scissor = new(new Offset2D(0, 0), area);
		_vk.CmdSetScissor(cmd, 0, 1, in scissor);

		_context.BeginPass(new ReadOnlySpan<VkFormat>(colorFormats, colorCount), depthFormat, depthReadOnly, uniformIds[..uniformCount], storageIds[..storageCount]);

		// Hand the context this pass's colour attachment views + area so a draw-list group-opacity layer can suspend
		// and resume the dynamic-rendering scope around its offscreen target.
		var scopeViews = stackalloc ImageView[colorCount];
		for (var i = 0; i < colorCount; i++)
			scopeViews[i] = color[i].ImageView;
		_context.SetColorScope(new ReadOnlySpan<ImageView>(scopeViews, colorCount), area);

		pass.Pass.Record?.Invoke(_context);

		_vk.CmdEndRendering(cmd);
	}

	// Fill `ids` with the app-writable parameter buffers a pass .Reads (its ReadUniform edges, in .Reads order) —
	// they bind to the technique's uniform slots 0..; returns the count.
	private static int CollectUniformIds(GraphPass pass, Span<int> ids)
	{
		var n = 0;
		foreach (var access in pass.Accesses)
			if (access.Kind == AccessKind.ReadUniform && n < ids.Length)
				ids[n++] = access.Resource.Id;
		return n;
	}

	// Fill `ids` with the app-writable storage buffers a pass .Reads (its ReadStorage edges, in .Reads order) —
	// they bind to the technique's storage-buffer slots; returns the count.
	private static int CollectStorageIds(GraphPass pass, Span<int> ids)
	{
		var n = 0;
		foreach (var access in pass.Accesses)
			if (access.Kind == AccessKind.ReadStorage && n < ids.Length)
				ids[n++] = access.Resource.Id;
		return n;
	}

	// Terminal: hand the present target to the swapchain, copying it out first if a capture was requested.
	private void FinishFrame(CommandBuffer cmd, Buffer captureBuffer)
	{
		if (Graph!.PresentTarget is not { } present)
			return; // Headless graph: nothing to present.

		ref var image = ref _transients!.Get(_currentFrame, present.Id);
		var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

		if (captureBuffer.Handle != 0)
		{
			TransitionImage(cmd, image.Image, range,
				ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
				PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit);

			BufferImageCopy region = new()
			{
				ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
				ImageExtent = new Extent3D(image.Extent.Width, image.Extent.Height, 1),
			};
			_vk.CmdCopyImageToBuffer(cmd, image.Image, ImageLayout.TransferSrcOptimal, captureBuffer, 1, in region);

			TransitionImage(cmd, image.Image, range,
				ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit,
				PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None);
		}
		else
		{
			foreach (var transition in Barriers.FinalTransitions)
				EmitTransition(cmd, transition);
		}
	}

	private void EmitTransition(CommandBuffer cmd, ResourceTransition transition)
	{
		var id = transition.Resource.Id;

		// Buffers have no layout — emit a buffer memory barrier scoped by the from/to stage+access. This is the
		// compute(StorageWrite) → vertex-input(VertexAttributeRead) hand-off for GPU particles (§7.6).
		if (_bufferIds.Contains(id))
		{
			EmitBufferBarrier(cmd, _transients!.GetBuffer(_currentFrame, id), VulkanStateMapping.For(transition.From), VulkanStateMapping.For(transition.To));
			return;
		}

		ref var image = ref _transients!.Get(_currentFrame, id);

		// Persistent resources survive across frames. The barrier plan, derived per-frame, thinks every
		// resource starts Undefined; for a persistent one that would discard the value the next frame's compute
		// needs to read. Substitute the real layout/state the previous frame left it in (tracked here), so the
		// transition preserves contents. First frame ever: no entry yet, so it stays Undefined (garbage, which
		// the exposure adaptation converges away from). The single shared image + in-order submission make the
		// resulting SHADER_READ→GENERAL barrier the cross-frame read/write hazard guard.
		var fromState = transition.From;
		if (_persistentIds.Contains(id) && fromState == ResourceState.Undefined && _persistentState.TryGetValue(id, out var carried))
			fromState = carried;
		if (_persistentIds.Contains(id))
			_persistentState[id] = transition.To;

		var src = VulkanStateMapping.For(fromState);
		var dst = VulkanStateMapping.For(transition.To);
		var srcStage = src.Stage;
		var srcAccess = src.Access;

		// Aliasing barrier: when a resource is born onto memory an earlier alias used this frame (an
		// Undefined→… transition for a non-first bucket member), the fresh-memory TopOfPipe source scope is
		// unsafe — the predecessor's GPU work on that memory may still be running. Widen the source scope to
		// "all prior commands / all memory" so this image's first write waits for the predecessor to finish.
		// (Conservative but correct; hand-offs are few. The Undefined old layout already discards the alias's
		// contents, which is exactly the aliasing semantic.)
		if (transition.From == ResourceState.Undefined && _aliasedHandoffIds.Contains(transition.Resource.Id))
		{
			srcStage = PipelineStageFlags2.AllCommandsBit;
			srcAccess = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
		}

		var range = new ImageSubresourceRange(image.Aspect, 0, 1, 0, 1);
		TransitionImage(cmd, image.Image, range, src.Layout, dst.Layout, srcStage, srcAccess, dst.Stage, dst.Access);
	}

	private void TransitionImage(
		CommandBuffer cmd, Image image, ImageSubresourceRange range,
		ImageLayout oldLayout, ImageLayout newLayout,
		PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
		PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
	{
		ImageMemoryBarrier2 barrier = new()
		{
			SType = StructureType.ImageMemoryBarrier2,
			SrcStageMask = srcStage,
			SrcAccessMask = srcAccess,
			DstStageMask = dstStage,
			DstAccessMask = dstAccess,
			OldLayout = oldLayout,
			NewLayout = newLayout,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Image = image,
			SubresourceRange = range,
		};
		DependencyInfo dependency = new()
		{
			SType = StructureType.DependencyInfo,
			ImageMemoryBarrierCount = 1,
			PImageMemoryBarriers = &barrier,
		};
		_vk.CmdPipelineBarrier2(cmd, in dependency);
	}

	// A buffer memory barrier (no layout) for the storage→vertex hand-off of a GPU-particle buffer.
	private void EmitBufferBarrier(CommandBuffer cmd, Buffer buffer, VulkanStateMapping.Scope from, VulkanStateMapping.Scope to)
	{
		BufferMemoryBarrier2 barrier = new()
		{
			SType = StructureType.BufferMemoryBarrier2,
			SrcStageMask = from.Stage,
			SrcAccessMask = from.Access,
			DstStageMask = to.Stage,
			DstAccessMask = to.Access,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Buffer = buffer,
			Offset = 0,
			Size = Vk.WholeSize,
		};
		DependencyInfo dependency = new()
		{
			SType = StructureType.DependencyInfo,
			BufferMemoryBarrierCount = 1,
			PBufferMemoryBarriers = &barrier,
		};
		_vk.CmdPipelineBarrier2(cmd, in dependency);
	}

	private static AttachmentLoadOp ToLoadOp(LoadAction load) => load switch
	{
		LoadAction.Clear => AttachmentLoadOp.Clear,
		LoadAction.Load => AttachmentLoadOp.Load,
		_ => AttachmentLoadOp.DontCare,
	};

	private static AttachmentStoreOp ToStoreOp(StoreAction store) =>
		store == StoreAction.Store ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare;

	private (Buffer Buffer, DeviceMemory Memory) CreateStagingBuffer(ulong size)
	{
		BufferCreateInfo info = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = size,
			Usage = BufferUsageFlags.TransferDstBit,
			SharingMode = SharingMode.Exclusive,
		};
		_vk.CreateBuffer(_ctx.Device, in info, null, out var buffer).Check("vkCreateBuffer");

		_vk.GetBufferMemoryRequirements(_ctx.Device, buffer, out var requirements);
		MemoryAllocateInfo alloc = new()
		{
			SType = StructureType.MemoryAllocateInfo,
			AllocationSize = requirements.Size,
			MemoryTypeIndex = _ctx.FindMemoryType(
				requirements.MemoryTypeBits,
				MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
		};
		_vk.AllocateMemory(_ctx.Device, in alloc, null, out var memory).Check("vkAllocateMemory");
		_vk.BindBufferMemory(_ctx.Device, buffer, memory, 0).Check("vkBindBufferMemory");
		return (buffer, memory);
	}

	// PNG capture only handles 8-bit RGBA/BGRA swapchains (the formats WriteCapture decodes); HDR swapchain
	// formats (FP16 / 10-bit packed) are not capturable to PNG here.
	private bool CaptureSupported() =>
		_swapchain.ImageFormat is VkFormat.B8G8R8A8Srgb or VkFormat.B8G8R8A8Unorm or VkFormat.R8G8B8A8Srgb or VkFormat.R8G8B8A8Unorm;

	private void WriteCapture(DeviceMemory memory, ulong size, uint width, uint height, string path)
	{
		void* mapped;
		_vk.MapMemory(_ctx.Device, memory, 0, size, 0, &mapped).Check("vkMapMemory");
		var src = new ReadOnlySpan<byte>(mapped, (int)size);

		// VulkanSwapchain is typically B8G8R8A8; PNG wants RGBA, so swap R/B for those formats.
		var swizzle = _swapchain.ImageFormat is VkFormat.B8G8R8A8Srgb or VkFormat.B8G8R8A8Unorm;
		var rgba = new byte[(int)size];
		for (var i = 0; i < (int)(width * height); i++)
		{
			var o = i * 4;
			rgba[o] = swizzle ? src[o + 2] : src[o];
			rgba[o + 1] = src[o + 1];
			rgba[o + 2] = swizzle ? src[o] : src[o + 2];
			rgba[o + 3] = src[o + 3];
		}
		_vk.UnmapMemory(_ctx.Device, memory);

		PngWriter.WriteRgba(path, (int)width, (int)height, rgba);
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
		_renderFinished = new Semaphore[_swapchain.ImageCount];
		SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };
		for (var i = 0; i < _renderFinished.Length; i++)
			_vk.CreateSemaphore(_ctx.Device, in semInfo, null, out _renderFinished[i]).Check("vkCreateSemaphore");
	}

	private void RecreateSwapchain()
	{
		if (_windowExtent.Width == 0 || _windowExtent.Height == 0)
			return;

		_vk.DeviceWaitIdle(_ctx.Device);
		var oldImageCount = _swapchain.ImageCount;
		_swapchain.Recreate(_windowExtent);
		_transients?.Recreate(_swapchain.Extent);

		if (_swapchain.ImageCount != oldImageCount)
		{
			DestroyRenderFinishedSemaphores();
			CreateRenderFinishedSemaphores();
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
		{
			for (var i = 0; i < readback.Buffers.Length; i++)
			{
				if (readback.Buffers[i].Handle != 0)
					_vk.DestroyBuffer(_ctx.Device, readback.Buffers[i], null);
				if (readback.Memories[i].Handle != 0)
					_vk.FreeMemory(_ctx.Device, readback.Memories[i], null);
			}
		}
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
		_shaderWatcher?.Dispose(); // stop the hot-reload watcher before tearing the device down (M13)
		_vk.DeviceWaitIdle(_ctx.Device);

		DisposeGraphResources();    // transients + readback staging (graph-level)
		_context.Dispose();
		_descriptors.Dispose();
		_meshes.Dispose();
		_textures.Dispose(); // before the allocator — it holds VMA image allocations
		_allocator.Dispose(); // after every image/buffer it backed is freed
		_techniques.Dispose();

		DestroyRenderFinishedSemaphores();
		foreach (var semaphore in _imageAvailable)
			if (semaphore.Handle != 0)
				_vk.DestroySemaphore(_ctx.Device, semaphore, null);
		foreach (var fence in _inFlight)
			if (fence.Handle != 0)
				_vk.DestroyFence(_ctx.Device, fence, null);

		if (_commandPool.Handle != 0)
			_vk.DestroyCommandPool(_ctx.Device, _commandPool, null);

		_swapchain.Dispose();
		_ctx.Dispose(); // the device + instance — last, after everything that used them
	}
}
