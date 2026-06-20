using System.Collections.Concurrent;
using System.Diagnostics;

using Loom.Assets;
using Loom.Core;
using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.UI;
using Loom.Graph;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using VMASharp;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// The Vulkan <see cref="IEngine"/> (Planning.md §2 Execute/Bake, §5 front door). It owns the long-lived
/// device (<see cref="VulkanContext"/>), the pipeline cache, the mesh store, and the descriptor allocator; the
/// <em>active graph</em> is swappable (<see cref="SetGraph"/>). On each <see cref="SetGraph"/> it derives a
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
/// by <see cref="SetGraph"/>, reusing the device and cache — Planning.md §8's "structural changes rebuild the
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
internal sealed unsafe class VulkanEngine : IEngine
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
	private readonly VulkanMaterialStore _materials;
	private readonly VulkanDescriptorAllocator _descriptors;
	private readonly VulkanRenderContext _context;
	private readonly Dictionary<string, VulkanTechniqueTemplate> _registry = []; // logical name → backend baked-input form

	// Shader hot-reload (M13): reverse map shader name → the techniques that use it, plus each technique's original
	// descriptor (so a reload can rebuild the template). The watcher fires on a background thread → it enqueues the
	// changed shader name; the render thread drains the queue at the top of the frame (fence-safe).
	private readonly Dictionary<string, List<string>> _techniquesByShader = [];
	private readonly Dictionary<string, TechniqueDescriptor> _descriptorsByName = [];
	private readonly ConcurrentQueue<string> _reloadQueue = new();
	private AssetWatcher? _shaderWatcher;
#if LOOM_IMGUI
	private VulkanImGuiRenderer? _imgui; // created on demand when the host calls EnableImGui
#endif
	private VulkanUIRenderer? _ui; // created when the host calls EnableUI (MU0 Phase A: a no-op renderer)

	// Engine-owned shaders (tonemap/blit/fullscreen/exposure/imgui/debugview) ship embedded in THIS assembly
	// under the manifest prefix "<assembly>.Shaders." (the Loom SDK's Embed mode sets the same prefix from
	// $(AssemblyName)); app shaders are loose .spv/.json on disk. ResolveShaderAsset tries embedded first.
	private static readonly string EmbeddedShaderPrefix = typeof(VulkanEngine).Assembly.GetName().Name + ".Shaders.";

	// Graph-level state — rebuilt by SetGraph, reusing the device above.
	private RenderGraph? _graph;
	private BarrierPlan _plan = null!;
	private AliasPlan _aliasPlan = null!;
	private HashSet<int> _aliasedHandoffIds = []; // resources that reuse an earlier alias's memory this frame
	private HashSet<int> _persistentIds = [];     // resources whose contents survive across frames
	private readonly Dictionary<int, ResourceState> _persistentState = []; // last state, carried across frames
	private HashSet<int> _bufferIds = [];          // storage/vertex buffer resources (emit buffer barriers, not image)
	private VulkanTransientResources? _transients;

	private CommandPool _commandPool;
	private CommandBuffer[] _commandBuffers = [];
	private Semaphore[] _imageAvailable = []; // per frame-in-flight
	private Semaphore[] _renderFinished = []; // per swapchain image
	private Fence[] _inFlight = [];           // per frame-in-flight

	// GPU→CPU readback (§7.4 picking): the first g.Readback target is copied to a per-slot host-visible
	// buffer each frame; the result is read back ~2 frames later (when the slot's fence next signals), kept
	// in _readbackCpu as decoded floats. -1 = no readback declared. Graph-level (rebuilt by SetGraph).
	private int _readbackId = -1;
	private Buffer[] _readbackBuffers = [];
	private DeviceMemory[] _readbackMemories = [];
	private bool[] _readbackValid = [];
	private float[] _readbackCpu = [];
	private int _readbackWidth;
	private int _readbackHeight;

	// GPU per-pass timing (§12 M13 profiler): a TIMESTAMP query pool per frame slot, 2 queries (begin/end) per
	// pass. Decoded after the slot's fence — like the picking readback above — so the numbers are a frame or two
	// old. Runtime-toggled (off by default); device support (timestampPeriod / valid bits) is probed in the ctor.
	private bool _gpuTimingEnabled;
	private QueryPool[] _timingPools = [];   // per frame slot, each sized 2*passCount
	private bool[] _timingValid = [];        // slot has recorded timestamps at least once
	private int _timingPassCount;
	private float _timestampPeriod;          // ns per tick (device limit)
	private uint _timestampValidBits;        // 0 ⇒ the graphics queue can't timestamp (timing unavailable)
	private PassTiming[] _timings = [];      // decoded, reused as the LastFrameTimings backing
	private double _frameGpuMs;

	// M13 resource viewer: the active graph's colour/depth resources the host can preview. Rebuilt each SetGraph.
	private readonly List<InspectableResource> _inspectable = [];

	// This frame's host inputs, pulled from FrameInputs at the top of Render (cameras already clip-space-adjusted).
	private Camera _frameCamera = Camera.Identity;
	private Camera _frameSun = Camera.Identity;
	private Camera[] _frameCascades = [];
	private IReadOnlyList<float> _frameCascadeSplits = [];
	private float _frameTime;
	private uint _frameHover;
	private nint _frameImGui;
	private IReadOnlyList<(UISurfaceId Id, UIDrawData Draw)> _frameUISurfaces = [];

	private int _currentFrame;
	private bool _framebufferResized;
	private Extent2D _windowExtent;
	private string? _capturePath;

	public string BackendName => "Vulkan";
	public string DeviceName => _ctx.DeviceName;

	public bool GpuTimingEnabled
	{
		get => _gpuTimingEnabled;
		set => _gpuTimingEnabled = value && _timestampValidBits != 0; // can't enable on a queue with no timestamps
	}
	public IReadOnlyList<PassTiming> LastFrameTimings => _timings;
	public double LastFrameGpuMilliseconds => _frameGpuMs;

	public IReadOnlyList<InspectableResource> InspectableResources => _inspectable;

	public nint GetDebugTextureId(int resourceId)
	{
#if LOOM_IMGUI
		if (_imgui is not null && resourceId >= 0 && _inspectable.Exists(r => r.Id == resourceId))
		{
			_context.RequestThumbnail(resourceId); // engine draws the image in the HUD pass (ImGui.Image no-ops on arm64)
			return (nint)(resourceId + VulkanRenderContext.DebugTextureBase);
		}
#endif
		return 0;
	}

	public void SetDebugLines(ReadOnlySpan<System.Numerics.Vector4> segments)
	{
#if LOOM_IMGUI
		_context.SetDebugLines(segments);
#endif
	}

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
		_materials = new VulkanMaterialStore();
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
		if (_techniquesByShader.ContainsKey(shader))
			_reloadQueue.Enqueue(shader);
	}

	public void DefineTechnique(string name, TechniqueDescriptor descriptor)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		// Resolve the logical shader name to backend bytecode + reflection, and fold the neutral descriptor into
		// the backend's baked-input form. Shaders are named logically — no .spv path in app code (Planning.md §5).
		// The descriptor-binding counts come from the shader's reflection, not the app (M7-B4): the author states
		// only the pipeline intent (geometry/blend/filter/compute), and Slang's reflection says how many things
		// the shader binds.
		var reflection = VulkanShaderReflection.FromBytes(ResolveShaderAsset(descriptor.Shader, ".json"));
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
			descriptor.ParticleInput,
			descriptor.Address,
			layout,
			descriptor.Instance,
			reflection.UniformBufferCount);

		// Hot-reload bookkeeping (M13): remember which techniques a shader backs (one shader → many techniques, e.g.
		// bloom_sample → bloom.down + bloom.up) and each technique's descriptor, so a reload can rebuild the template.
		_descriptorsByName[name] = descriptor;
		if (!_techniquesByShader.TryGetValue(descriptor.Shader, out var users))
			_techniquesByShader[descriptor.Shader] = users = [];
		if (!users.Contains(name))
			users.Add(name);
	}

	// Drain queued shader reloads on the render thread (M13). Rare, human-paced events, so we drain the GPU once and
	// rebuild every affected technique template, then clear both pipeline-cache layers — the next frame re-bakes from
	// the new bytecode. Called at the top of the frame, before any recording.
	private void DrainShaderReloads()
	{
		if (_reloadQueue.IsEmpty)
			return;

		var shaders = new HashSet<string>();
		while (_reloadQueue.TryDequeue(out var shader))
			shaders.Add(shader);
		if (shaders.Count == 0)
			return;

		_vk.DeviceWaitIdle(_ctx.Device); // no pipeline in flight before we drop them
		foreach (var shader in shaders)
		{
			if (!_techniquesByShader.TryGetValue(shader, out var techniques))
				continue;
			foreach (var name in techniques)
				DefineTechnique(name, _descriptorsByName[name]); // re-resolve bytecode + reflection, rebuild the template
			Console.WriteLine($"[loom] reloaded shader '{shader}' → {string.Join(", ", techniques)}");
		}
		_context.ClearBaked();   // drop per-(name,formats) cached pipelines
		_techniques.Reset();     // dispose the stale VkPipelines; next Bake rebuilds from the new templates
	}

	public Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) => _meshes.Create(vertices, indices);

	public Mesh CreateMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices) where T : unmanaged
	{
		var stride = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
		if (stride != layout.Stride)
			throw new VulkanException($"Vertex element size ({stride} bytes) does not match the layout stride ({layout.Stride} bytes).");
		return _meshes.Create(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertices), stride, indices);
	}

	public Mesh CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices) => _meshes.CreateSkinned(vertices, indices);

	public Texture CreateTexture(TextureData data) => _textures.Create(data);

	public Material CreateMaterial(params ReadOnlySpan<Texture> textures) => _materials.Create(textures);

	public void SetGraph(RenderGraph graph)
	{
		ArgumentNullException.ThrowIfNull(graph);

		// Swapping the active graph mid-run: drain the GPU before releasing the old graph's transients/readback,
		// then bind the new graph's derived plan + resources. The device, pipeline cache, meshes, and descriptors
		// are untouched (Planning.md §8). The first call (no prior graph) skips the drain.
		if (_graph is not null)
			_vk.DeviceWaitIdle(_ctx.Device);
		DisposeGraphResources();
		_persistentState.Clear();

		_graph = graph;

		// M13 resource viewer: enumerate the colour/depth resources the host can preview, and (in an ImGui build)
		// inject HUD .Samples edges for them so the Execute layer derives their keep-alive + ShaderRead transition —
		// the viewer samples live images, not aliased corpses. The author's graph is untouched; we execute `executed`.
		_inspectable.Clear();
		var executed = graph;
#if LOOM_IMGUI
		// Gate on ImGui being live: graphs set before EnableImGui (the DirectX12 aliasing diagnostics) keep their exact
		// aliasing; gallery graphs (set/switched with the UI up) get the inspection edges.
		if (_imgui is not null)
		{
			foreach (var resource in graph.Resources)
				if (resource.Kind is ResourceKind.Color or ResourceKind.Depth)
					_inspectable.Add(new InspectableResource(resource.Id, resource.Name, resource.Kind, resource.Format));
			executed = graph.WithDebugSampling(_inspectable.ConvertAll(r => r.Id));
		}
#endif

		// Derive once — the graph is immutable, so order/barriers/load-store/lifetimes are frame-invariant.
		var schedule = GraphSchedule.Derive(executed);
		_plan = BarrierPlan.Derive(schedule);
		_aliasPlan = AliasPlan.Derive(LifetimePlan.Derive(schedule));

		// Each bucket's members share one block; every member after the first reuses an earlier alias's memory
		// this frame, so its Undefined→… birth transition must wait on all prior work to that memory (the
		// aliasing barrier — see EmitTransition). The first member is covered by the per-frame fence instead.
		_aliasedHandoffIds = [];
		foreach (var bucket in _aliasPlan.Buckets)
			for (var i = 1; i < bucket.Members.Length; i++)
				_aliasedHandoffIds.Add(bucket.Members[i].Id);

		// Persistent resources (auto-exposure history) keep their contents across frames; storage/vertex buffers
		// emit buffer barriers rather than image transitions. Classify the graph's resources once.
		_persistentIds = [];
		_bufferIds = [];
		foreach (var resource in graph.Resources)
		{
			if (resource.Kind == ResourceKind.Persistent)
				_persistentIds.Add(resource.Id);
			if (resource.Kind == ResourceKind.Buffer)
				_bufferIds.Add(resource.Id);
		}
		if (_aliasPlan.AliasableCount > _aliasPlan.Buckets.Length)
			Console.WriteLine($"[loom] transient aliasing: {_aliasPlan.AliasableCount} transients folded into {_aliasPlan.Buckets.Length} memory buckets.");

		// Build transients from `executed` so the injected debug-sample edges add SampledBit to inspected resources.
		_transients = new VulkanTransientResources(_ctx, executed, _aliasPlan, _swapchain.ImageFormat, _swapchain.Extent, MaxFramesInFlight, _allocator);
		_context.SetTransients(_transients);

		// Readback staging (§7.4): one host-visible buffer per frame slot for the first g.Readback target.
		if (graph.ReadbackTargets.Length > 0)
		{
			var target = graph.ReadbackTargets[0];
			_readbackId = target.Id;
			(_readbackWidth, _readbackHeight) = target.Resolve();
			_readbackCpu = new float[_readbackWidth * _readbackHeight];
			var bytes = (ulong)_readbackWidth * (ulong)_readbackHeight * 2; // R16F = 2 bytes/texel
			_readbackBuffers = new Buffer[MaxFramesInFlight];
			_readbackMemories = new DeviceMemory[MaxFramesInFlight];
			_readbackValid = new bool[MaxFramesInFlight];
			for (var i = 0; i < MaxFramesInFlight; i++)
				(_readbackBuffers[i], _readbackMemories[i]) = CreateStagingBuffer(bytes);
		}

		// GPU-timing pools (M13): 2 timestamps (begin/end) per pass, one pool per frame slot. Sized to this graph's
		// pass count, so a swap to a graph with a different count reallocates (DisposeGraphResources freed the old).
		_timingPassCount = _plan.Passes.Length;
		_timings = [];
		_frameGpuMs = 0;
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

	public void EnableImGui()
	{
#if LOOM_IMGUI
		var spirv = ResolveShaderAsset("imgui", ".spv");
		_imgui = new VulkanImGuiRenderer(_ctx, _allocator, _commandPool, _swapchain.ImageFormat, spirv, MaxFramesInFlight);
		_context.SetImGui(_imgui);
#endif
		// UseImGui=false: the engine is built without ImGui, so this is a no-op and ctx.DrawImGui() draws nothing.
	}

	public void EnableUI(TextureData? fontAtlas = null)
	{
		if (_ui is not null)
			return; // idempotent — enabling twice keeps the existing renderer (pass the atlas on the first call)

		// MU1: stand up the real ui.slang renderer (white-texel + optional MTSDF atlas) and wire it to the context.
		var spirv = ResolveShaderAsset("ui", ".spv");
		_ui = new VulkanUIRenderer(_ctx, _allocator, _commandPool, _swapchain.ImageFormat, spirv, MaxFramesInFlight, fontAtlas);
		_context.SetUI(_ui);
	}

	public float SamplePick(int x, int y)
	{
		if (_readbackId < 0 || (uint)x >= (uint)_readbackWidth || (uint)y >= (uint)_readbackHeight)
			return 0f;
		return _readbackCpu[y * _readbackWidth + x];
	}

	public void Resize(int width, int height)
	{
		_windowExtent = new Extent2D((uint)width, (uint)height);
		_framebufferResized = true;
	}

	public void WaitIdle() => _vk.DeviceWaitIdle(_ctx.Device);

	public void RequestCapture(string path) => _capturePath = path;

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
	private static Camera ClipSpace(Camera camera)
	{
		var projection = camera.Projection;
		projection.M22 *= -1f;
		return new Camera(camera.View, projection);
	}

	public void Render(FrameInputs inputs)
	{
		ArgumentNullException.ThrowIfNull(inputs);
		if (_graph is null || _transients is null)
			throw new InvalidOperationException("No active graph; call SetGraph before Render.");

		_frameCamera = ClipSpace(inputs.Camera);
		_frameSun = ClipSpace(inputs.SunView);
		// Flip each cascade's clip space the same way (M15-3) — neutral matrices in, the backend's Y-flip applied.
		if (_frameCascades.Length != inputs.Cascades.Count)
			_frameCascades = new Camera[inputs.Cascades.Count];
		for (var i = 0; i < inputs.Cascades.Count; i++)
			_frameCascades[i] = ClipSpace(inputs.Cascades[i]);
		_frameCascadeSplits = inputs.CascadeSplits;
		_frameTime = inputs.Time;
		_frameHover = inputs.HoverId;
		_frameImGui = inputs.ImGuiDrawData;
		_frameUISurfaces = inputs.UISurfaces;
		DrawFrame((float)inputs.DeltaSeconds);
	}

	private void DrawFrame(float deltaSeconds)
	{
		if (_windowExtent.Width == 0 || _windowExtent.Height == 0)
			return; // Minimized — nothing to draw.

		DrainShaderReloads(); // M13: pick up any hot-reloaded shaders before recording this frame

		_vk.WaitForFences(_ctx.Device, 1, in _inFlight[_currentFrame], true, ulong.MaxValue);

		// The fence just signalled, so this slot's readback buffer holds the copy from when it last ran (~2
		// frames ago). Decode it into _readbackCpu before this frame overwrites it — that's the latency §7.4
		// is upfront about. (Done before recording, so SamplePick always sees the freshest available.)
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

		var capturePath = _capturePath;
		// PNG capture decodes an 8-bit RGBA/BGRA swapchain; an HDR swapchain (FP16 / 10-bit packed) would
		// decode to garbage, so skip it there rather than write a corrupt file (HDR is verified on-display).
		if (capturePath is not null && !CaptureSupported())
		{
			Console.WriteLine($"[loom] capture skipped: swapchain format {_swapchain.ImageFormat} is not an 8-bit RGBA/BGRA format (HDR output).");
			_capturePath = null;
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
			_capturePath = null;
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

		_context.BeginFrame(cmd, deltaTime, _frameTime, _currentFrame, _frameCamera, _frameSun, _frameCascades, _frameCascadeSplits, _frameHover, _frameImGui, _frameUISurfaces, _swapchain.Extent, _swapchain.HdrActive);

		// GPU timing (M13): reset this slot's pool on the device timeline, then bracket each pass's own work with a
		// begin/end timestamp. The reset is inside the command buffer (no hostQueryReset feature needed).
		var timing = _gpuTimingEnabled && _timingPools.Length > 0;
		if (timing)
			_vk.CmdResetQueryPool(cmd, _timingPools[_currentFrame], 0, (uint)(2 * _timingPassCount));

		var passIndex = 0;
		foreach (var pass in _plan.Passes)
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
		for (var i = 0; i < _plan.TrailingLabelCloses; i++)
			_ctx.EndDebugLabel(cmd);
	}

	// Copy the readback target (its last graph use is a .Samples, so it's in SHADER_READ) into this slot's
	// host-visible buffer. The slot's fence guards it; DownloadReadback decodes it ~2 frames later. §7.4.
	private void RecordReadback(CommandBuffer cmd)
	{
		if (_readbackId < 0)
			return;

		ref var image = ref _transients!.Get(_currentFrame, _readbackId);
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
		_vk.CmdCopyImageToBuffer(cmd, image.Image, ImageLayout.TransferSrcOptimal, _readbackBuffers[_currentFrame], 1, in region);
		_readbackValid[_currentFrame] = true;
	}

	// Decode this slot's readback buffer (R16F halves → floats) into _readbackCpu. Called after the slot's
	// fence signals, so the data is the copy from when the slot last ran (a frame or two ago).
	private void DownloadReadback(int slot)
	{
		if (_readbackId < 0 || !_readbackValid[slot])
			return;

		var count = _readbackWidth * _readbackHeight;
		void* mapped;
		_vk.MapMemory(_ctx.Device, _readbackMemories[slot], 0, (ulong)count * 2, 0, &mapped).Check("vkMapMemory");
		var halves = new ReadOnlySpan<Half>(mapped, count);
		for (var i = 0; i < count; i++)
			_readbackCpu[i] = (float)halves[i];
		_vk.UnmapMemory(_ctx.Device, _readbackMemories[slot]);
	}

	// Read this slot's timestamps (begin/end per pass) into _timings and the frame total (M13). Called after the
	// slot's fence signals, so the queries are complete. Mask to the queue's valid bits; guard end>=begin against
	// wrap / driver oddities (Adreno). ticks * timestampPeriod(ns) / 1e6 = milliseconds.
	private void DownloadTimings(int slot)
	{
		if (!_gpuTimingEnabled || _timingPools.Length == 0 || _timingPassCount == 0 || !_timingValid[slot])
			return;

		var n = 2 * _timingPassCount;
		ulong* raw = stackalloc ulong[n];
		_vk.GetQueryPoolResults(_ctx.Device, _timingPools[slot], 0, (uint)n,
			(nuint)(n * sizeof(ulong)), raw, (ulong)sizeof(ulong), QueryResultFlags.Result64Bit).Check("vkGetQueryPoolResults");

		var mask = _timestampValidBits >= 64 ? ulong.MaxValue : (1UL << (int)_timestampValidBits) - 1UL;
		if (_timings.Length != _timingPassCount)
			_timings = new PassTiming[_timingPassCount];
		for (var i = 0; i < _timingPassCount; i++)
		{
			var begin = raw[2 * i] & mask;
			var end = raw[2 * i + 1] & mask;
			var ticks = end >= begin ? end - begin : 0UL;
			_timings[i] = new PassTiming(_plan.Passes[i].Pass.Name, ticks * _timestampPeriod / 1_000_000.0);
		}
		var first = raw[0] & mask;
		var last = raw[n - 1] & mask;
		_frameGpuMs = (last >= first ? last - first : 0UL) * _timestampPeriod / 1_000_000.0;
	}

	private void RecordPass(CommandBuffer cmd, PassBarriers pass)
	{
		// Compute passes have no attachments and no dynamic-rendering scope — just bind + dispatch. The
		// resource transitions (storage GENERAL, the consumer's SHADER_READ) were already emitted before this.
		if (pass.Pass.Kind == PassKind.Compute)
		{
			_context.BeginPass([], default, false); // no colour/depth targets; keeps the bake key stable
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

		_context.BeginPass(new ReadOnlySpan<VkFormat>(colorFormats, colorCount), depthFormat, depthReadOnly);
		pass.Pass.Record?.Invoke(_context);

		_vk.CmdEndRendering(cmd);
	}

	// Terminal: hand the present target to the swapchain, copying it out first if a capture was requested.
	private void FinishFrame(CommandBuffer cmd, Buffer captureBuffer)
	{
		if (_graph!.PresentTarget is not { } present)
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
			foreach (var transition in _plan.FinalTransitions)
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

		VulkanPngWriter.WriteRgba(path, (int)width, (int)height, rgba);
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

		for (var i = 0; i < _readbackBuffers.Length; i++)
		{
			if (_readbackBuffers[i].Handle != 0)
				_vk.DestroyBuffer(_ctx.Device, _readbackBuffers[i], null);
			if (_readbackMemories[i].Handle != 0)
				_vk.FreeMemory(_ctx.Device, _readbackMemories[i], null);
		}
		_readbackBuffers = [];
		_readbackMemories = [];
		_readbackValid = [];
		_readbackCpu = [];
		_readbackId = -1;

		foreach (var pool in _timingPools)
			if (pool.Handle != 0)
				_vk.DestroyQueryPool(_ctx.Device, pool, null);
		_timingPools = [];
		_timingValid = [];
		_timings = [];
		_timingPassCount = 0;
	}

	public void Dispose()
	{
		_shaderWatcher?.Dispose(); // stop the hot-reload watcher before tearing the device down (M13)
		_vk.DeviceWaitIdle(_ctx.Device);

		DisposeGraphResources();    // transients + readback staging (graph-level)
		_context.Dispose();
#if LOOM_IMGUI
		_imgui?.Dispose(); // before the allocator — it holds VMA font/vertex/index allocations
#endif
		_ui?.Dispose(); // likewise before the allocator — the UI renderer holds VMA atlas/vertex/index allocations
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
