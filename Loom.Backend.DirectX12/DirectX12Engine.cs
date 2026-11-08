using System.Runtime.InteropServices;
using Loom.Assets;
using Loom.Core.Animation;
using Loom.Core.Diagnostics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.Maths;

namespace Loom.Backend.DirectX12;

/// <summary>
/// The DirectX 12 <see cref="IEngine"/>. Stands up the device + swapchain and records command lists with
/// fence-based frame pacing. Consumes the backend-neutral <see cref="GraphSchedule"/>/<see cref="BarrierPlan"/>/
/// <c>AliasPlan</c> products exactly as the Vulkan engine does.
/// </summary>
public sealed unsafe partial class DirectX12Engine : EngineBase
{
	private const uint BufferCount = 2; // matches the Vulkan backend's 2 frames in flight

	private readonly DirectX12EngineSettings _settings;
	private readonly DirectX12Context _ctx;
	private readonly IDirectX12PresentTarget _presentTarget;
	private readonly DirectX12TechniqueCache _techniques;
	private readonly DirectX12DescriptorHeap _ring;
	private readonly DirectX12PipelineBlobCache _pipelineBlobs;
	private readonly DirectX12UploadBatch _uploads;
	private readonly DirectX12MeshStore _meshes;
	private readonly DirectX12TextureStore _textures;
	private readonly DirectX12RenderContext _context;
	private DirectX12Transients? _transients;

	private readonly ComPtr<ID3D12CommandAllocator>[] _allocators = new ComPtr<ID3D12CommandAllocator>[BufferCount];
	private ComPtr<ID3D12GraphicsCommandList> _commandList;
	private ComPtr<ID3D12Fence> _fence;
	private readonly ulong[] _frameFenceValues = new ulong[BufferCount];
	private ulong _fenceValue;
	private nint _fenceEvent;
	private int _debugErrors;
	private bool _disposed;

	// Per-buffer backbuffer state is tracked because D3D12 transition barriers need the resource's exact current
	// state (see DirectX12StateMapping). The active graph + its derived plan live on EngineBase.
	private readonly ResourceStates[] _backbufferState = new ResourceStates[BufferCount];
	// Bucket members that reuse an earlier member's memory (AliasPlan.AliasedMemberIds — the same set the Vulkan
	// backend consumes), rebuilt per graph; each needs one D3D12 aliasing barrier before its first use this frame.
	private IReadOnlySet<int> _aliasedHandoffIds = new HashSet<int>();
	private readonly HashSet<int> _aliasBarriered = []; // of those, the ones already barriered this frame

	private ComPtr<ID3D12Resource> _readback;
	private ulong _readbackSize;

	// Each g.Readback target is copied into a per-slot READBACK buffer every frame; after that slot's fence the copy
	// is repacked tight (D3D12 pads rows to 256 bytes) and handed to StoreReadback, which IEngine.Read returns.
	// Empty when the active graph declares no readback target.
	private readonly List<ReadbackBuffers> _readbacks = [];

	// One g.Readback target's GPU→CPU staging: a per-slot READBACK buffer the executor copies the target into, plus
	// the dimensions/row-pitch the copy uses and a tightly-packed scratch buffer the row-padded copy is repacked into.
	private sealed class ReadbackBuffers
	{
		public required int Id;
		public required uint Width;
		public required uint Height;
		public required uint RowPitch;
		public required uint BytesPerTexel;
		public required ComPtr<ID3D12Resource>[] Readback; // per frame slot
		public required bool[] Valid;                      // per frame slot
		public required byte[] Packed;                     // tight (unpadded) bytes handed to StoreReadback
	}

	// GPU per-pass timing: one TIMESTAMP query heap with 2 queries (begin/end) per pass and a per-slot sub-range,
	// plus a per-slot READBACK buffer ResolveQueryData writes into. Decoded after the slot's fence — a frame or two
	// stale. Runtime-toggled (off by default).
	private ComPtr<ID3D12QueryHeap> _timingHeap;
	private readonly ComPtr<ID3D12Resource>[] _timingReadback = new ComPtr<ID3D12Resource>[BufferCount];
	private readonly bool[] _timingValid = new bool[BufferCount];
	private int _timingPassCount;
	private ulong _timestampFrequency; // ticks per second, from the graphics queue (0 ⇒ timing unavailable)

	// Shader hot-reload, opt-in. The watcher maps a changed artifact to a logical shader name; the base's
	// reload registry queues it and the render thread drains it (fence-safe).
	private AssetWatcher? _shaderWatcher;

	public DirectX12Engine(nint hwnd, int width, int height, DirectX12EngineSettings settings)
		: this(settings, new DirectX12Context(settings), offscreen: false, hwnd, width, height)
	{
	}

	/// <summary>
	/// Bring up a windowless engine (<see cref="Loom.Core.Hosting.Surface.Offscreen"/>): the present target is an
	/// owned <see cref="DirectX12OffscreenTarget"/> instead of a swapchain, so <see cref="IEngine.RequestCapture"/>
	/// reads back an owned resource rather than a presented one — no window, no <c>HWND</c>.
	/// </summary>
	public DirectX12Engine(int width, int height, DirectX12EngineSettings settings)
		: this(settings, new DirectX12Context(settings), offscreen: true, 0, width, height)
	{
	}

	private DirectX12Engine(DirectX12EngineSettings settings, DirectX12Context ctx, bool offscreen, nint hwnd, int width, int height)
		: base(settings)
	{
		_settings = settings;
		_ctx = ctx;
		_presentTarget = offscreen
			? new DirectX12OffscreenTarget(_ctx, width, height, BufferCount)
			: new DirectX12Swapchain(_ctx, hwnd, width, height, BufferCount, settings.HdrOutput);

		for (uint i = 0; i < BufferCount; i++)
		{
			SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandAllocator(CommandListType.Direct, out ComPtr<ID3D12CommandAllocator> madeAllocator));
			_allocators[i] = madeAllocator;
			_ctx.SetObjectName((ID3D12Object*)_allocators[i].Handle, $"frame-alloc#{i}");
		}

		// Created in the recording state, then immediately closed — Render() resets it at the top of each frame.
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandList(
			0, CommandListType.Direct, _allocators[0], default(ComPtr<ID3D12PipelineState>), out ComPtr<ID3D12GraphicsCommandList> madeCommandList));
		_commandList = madeCommandList;
		SilkMarshal.ThrowHResult(_commandList.Close());
		_ctx.SetObjectName((ID3D12Object*)_commandList.Handle, "graph-cmdlist");

		SilkMarshal.ThrowHResult(_ctx.Device.CreateFence(0, FenceFlags.None, out ComPtr<ID3D12Fence> madeFence));
		_fence = madeFence;
		_fenceEvent = CreateEventW(0, 0, 0, 0);
		if (_fenceEvent == 0)
			throw new InvalidOperationException("CreateEvent failed for the DirectX12 frame-pacing fence.");
		// Bind the wait now rather than on the frame that first needs it. The runtime binds a P/Invoke on its first call
		// and allocates while it does (the library's name, as a managed string), and a frame only waits once the GPU
		// falls behind, which can first happen long after the loop is warm. The event starts unsignalled, so a zero
		// timeout returns at once and leaves it that way.
		WaitForSingleObject(_fenceEvent, 0);

		_pipelineBlobs = new DirectX12PipelineBlobCache(_ctx, settings.PersistentPipelineCache);
		_techniques = new DirectX12TechniqueCache(_ctx, _pipelineBlobs);
		// Starting sizes, not budgets — the heap grows when a scene wants more. The persistent region holds one table
		// per (material, technique) and the transient region a frame's per-pass SRVs plus the resource viewer's debug SRVs.
		_ring = new DirectX12DescriptorHeap(_ctx, persistentCapacity: 256, transientPerFrame: 256, BufferCount);
		_uploads = new DirectX12UploadBatch(_ctx);
		_meshes = new DirectX12MeshStore(_ctx, _uploads);
		_textures = new DirectX12TextureStore(_ctx, _uploads);
		_context = new DirectX12RenderContext(_ctx, _techniques, _ring, _meshes, _textures, Materials, (int)BufferCount, Validation);
		for (uint i = 0; i < BufferCount; i++)
			_backbufferState[i] = ResourceStates.Present; // a fresh flip-model buffer starts presentable

		// GPU-timing clock: the graphics (Direct) queue's timestamp frequency. A Direct queue always supports
		// timestamps on D3D12; 0 only if the call fails, which disables the profiler (the GpuTimingEnabled setter guards it).
		ulong frequency = 0;
		if (_ctx.GraphicsQueue.GetTimestampFrequency(ref frequency) >= 0)
			_timestampFrequency = frequency;

		Capabilities = new EngineCapabilities
		{
			GpuTiming = _timestampFrequency != 0,
			// Queue.AsyncCompute derives no cross-queue synchronisation yet and the backend owns one queue.
			AsyncCompute = false,
			SampleCounts = _ctx.SampleCounts,
			MaxColorAttachments = DirectX12Context.MaxColorAttachments,
			MinUniformBufferOffsetAlignment = DirectX12Context.ConstantBufferOffsetAlignment,
			Float32Filterable = _ctx.Float32Filterable,
		};

		LoomLog.Info($"DirectX12 backend up on {_ctx.DeviceName} ({BufferCount} buffers, {(settings.VSync ? "vsync" : "no-vsync")}).");

		// Shader hot-reload, opt-in. Watch the loose DXIL/reflection artifacts; an external process rewrites
		// them and this picks up the new bytecode + rebuilds affected PSOs. Off by default.
		if (settings.WatchShaderDirectory)
		{
			var source = new LayeredAssetSource(settings.ShaderDirectory);
			_shaderWatcher = new AssetWatcher(source, [".dxil", ".json"], debounceMs: 150);
			_shaderWatcher.Changed += OnShaderArtifactChanged;
			LoomLog.Info($"watching shaders for hot-reload: {settings.ShaderDirectory}");
		}
	}

	// Background-thread watcher callback: map the changed artifact (e.g. "gbuffer.ps.dxil") back to its shader name
	// and queue it if a technique uses it. No GPU work here.
	private void OnShaderArtifactChanged(string leaf)
	{
		var name = Path.GetFileNameWithoutExtension(leaf); // "gbuffer.ps.dxil" → "gbuffer.ps"; "gbuffer.json" → "gbuffer"
		if (name.EndsWith(".vs", StringComparison.Ordinal) || name.EndsWith(".ps", StringComparison.Ordinal) || name.EndsWith(".cs", StringComparison.Ordinal))
			name = name[..^3];
		QueueShaderReload(name);
	}

	// Evict the baked PSOs after a hot-reload so the next GetOrBake rebuilds from the re-registered templates.
	protected override void FlushUploads() => _uploads.Flush();

	protected override void ResetPipelineCaches() => _techniques.Reset();

	public override string BackendName => "DirectX12";
	public override string DeviceName => _ctx.DeviceName;

	public override EngineCapabilities Capabilities { get; }

	protected override void RenderFrame(FrameInputs inputs)
	{
		ServicePendingUploads();
		if (Graph is null)
			throw new InvalidOperationException("No active graph; call SetGraph before Render.");
		RenderGraphFrame(inputs);
	}

	private void WaitForFrame(uint index)
	{
		var target = _frameFenceValues[index];
		if (target != 0 && _fence.GetCompletedValue() < target)
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(target, (void*)_fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}
	}

	public override void Resize(int width, int height)
	{
		WaitIdle();
		_presentTarget.Resize(width, height);
		Array.Clear(_frameFenceValues);

		// Transients sized to the backbuffer must follow the new extent (re-derive at the new size). The replacement is
		// built before the old one is released so it can adopt the persistents that survive a resize — their contents
		// are cross-frame history, and rebuilding them would throw it away.
		if (Graph is not null)
		{
			var previous = _transients;
			_transients = new DirectX12Transients(_ctx, Graph!, Aliases, BufferCount, _presentTarget.Width, _presentTarget.Height, previous);
			previous?.Dispose();
			_context.SetTransients(_transients);
			ForgetUploadedShadows();   // the new transients' Constants and Storage buffers hold nothing yet
			RecreateSwapchainSizedReadback();
			for (uint i = 0; i < BufferCount; i++)
				_backbufferState[i] = ResourceStates.Present;
		}
	}

	public override void WaitIdle()
	{
		// Execute any recorded uploads first, so the fence below covers them: idle means everything recorded has run, and
		// a texture released while its upload was still pending is freed against this signal.
		_uploads.Flush();
		_fenceValue++;
		SilkMarshal.ThrowHResult(_ctx.GraphicsQueue.Signal(_fence, _fenceValue));
		if (_fence.GetCompletedValue() < _fenceValue)
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*)_fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}

		_meshes.CollectReleased(_fenceValue);
		_textures.CollectReleased(_fenceValue);
	}

	public override void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;

		_shaderWatcher?.Dispose(); // stop the hot-reload watcher before tearing the device down

		try { WaitIdle(); }
		catch { /* the device may already be lost; tear down regardless */ }

		_debugErrors += _ctx.DrainDebugMessages();
		if (_ctx.InfoQueue.Handle is not null)
			if (_debugErrors == 0)
				LoomLog.Info("DirectX12 debug layer: clean (no errors/corruption).");
			else
				LoomLog.Error($"DirectX12 debug layer: {_debugErrors} error/corruption message(s).");

		if (_fenceEvent != 0)
			CloseHandle(_fenceEvent);
		if (_readback.Handle is not null)
			_readback.Dispose();
		foreach (var readback in _readbacks)
			foreach (ref var buffer in readback.Readback.AsSpan())
				if (buffer.Handle is not null)
					buffer.Dispose();
		if (_timingHeap.Handle is not null)
			_timingHeap.Dispose();
		foreach (ref var buffer in _timingReadback.AsSpan())
			if (buffer.Handle is not null)
				buffer.Dispose();
		_transients?.Dispose();
		_context.Dispose();
		_pipelineBlobs.Save(); // write back whatever this run compiled, before the device goes away
		_uploads.Dispose(); // before the stores — it holds their staging resources until the last execute retires
		_meshes.Dispose();
		_textures.Dispose();
		_ring.Dispose();
		_techniques.Dispose();
		_fence.Dispose();
		_commandList.Dispose();
		for (uint i = 0; i < BufferCount; i++)
			_allocators[i].Dispose();
		_presentTarget.Dispose();
		_ctx.Dispose();
	}

	/// <summary>
	/// Register a technique under a logical name. Resolves the build-emitted signed DXIL (<c>&lt;name&gt;.cs.dxil</c>
	/// for compute, <c>&lt;name&gt;.vs.dxil</c> + <c>&lt;name&gt;.ps.dxil</c> for graphics) plus its
	/// <c>&lt;name&gt;.json</c> reflection, which gives the resource counts so the app states only pipeline intent —
	/// exactly as the Vulkan backend resolves embedded SPIR-V. A missing artifact throws; there is no substitute
	/// shader, because one would shade differently and so produce a different image on this backend alone.
	/// </summary>
	protected override TechniqueShape BakeTechnique(string name, TechniqueDescriptor descriptor)
	{
		var dir = _settings.ShaderDirectory;
		var shader = descriptor.Shader;
		// The descriptor-bound resource counts come from the shader's reflection sidecar; the author states only
		// pipeline intent.
		var reflection = DirectX12Shaders.ResolveReflection(dir, shader);

		// The bytecode is compiled offline and shipped, like the Vulkan .spv — no runtime compiler is involved.
		byte[] vertexBytecode, fragmentBytecode;
		var inputCount = reflection.SampleCount;
		// Which inputs the shader samples by comparison, from its SPIR-V: the DXIL can't say, and the reflection describes
		// a shadow sampler as it does any single-channel texture. A shader that samples nothing compares nothing, so
		// needs no SPIR-V; one that samples throws without it rather than reading as comparing nothing.
		var compared = ShaderBindingMap.From(reflection,
			inputCount > 0 ? SpirvDepthTextures.Bindings(DirectX12Shaders.ResolveSpirv(dir, shader), shader) : null);
		ShaderBindingMap.CheckCompare(name, compared.ComparedCount, descriptor.SampleCompare);
		var storageImages = 0;
		var storageBuffers = 0;
		var storageBufferStride = 0;
		// Descriptor-bound constant buffers (the per-frame uniform seam) — reflected from the sidecar, bound as a
		// root CBV at b1.
		var uniformBuffers = reflection.UniformBufferCount;
		// Read-only structured-buffer inputs, bound as root SRVs: a compute's inputs (skin's bind-pose verts + bone
		// palette) or a graphics technique's app-writable storage buffers (g.Storage SSBOs).
		var inputBuffers = reflection.InputBufferCount;
		string source;

		if (descriptor.IsCompute)
		{
			var cs = DirectX12Shaders.TryResolveStageDxil(dir, shader, "cs")
				?? throw DirectX12Shaders.MissingShader(shader, dir, "cs");
			(vertexBytecode, fragmentBytecode) = ([], cs);
			// Compute storage shape from reflection: a RW texture (exposure) / RW structured buffer (particles)
			// → UAV table (the stride lets the shader's GetDimensions work); a raw buffer (stride 0) → root UAV.
			// RW structured/byte buffers = all storage buffers minus the read-only inputs.
			storageImages = reflection.StorageImageCount;
			storageBuffers = reflection.StorageBufferCount - reflection.InputBufferCount;
			storageBufferStride = reflection.StorageBufferStride;
			source = $"{shader}.cs.dxil (Slang→signed DXIL)";
		}
		else
		{
			vertexBytecode = DirectX12Shaders.TryResolveStageDxil(dir, shader, "vs")
				?? throw DirectX12Shaders.MissingShader(shader, dir, "vs");
			fragmentBytecode = DirectX12Shaders.TryResolveStageDxil(dir, shader, "ps")
				?? throw DirectX12Shaders.MissingShader(shader, dir, "ps");
			source = $"{shader}.{{vs,ps}}.dxil (Slang→signed DXIL)";
		}

		LoomLog.Info($"DirectX12 technique '{name}' ← {source}.");
		// Resolve the per-vertex layout (explicit, else canonical pos/normal/uv for a mesh technique, else none),
		// mirroring the Vulkan backend — a non-null Instance marks it instanced (per-technique layouts).
		var layout = descriptor.Layout ?? (descriptor.HasVertexInput ? VertexLayout.MeshStandard : null);
		_techniques.Register(name, new DirectX12TechniqueTemplate(
			vertexBytecode, fragmentBytecode, descriptor.VertexEntry, descriptor.FragmentEntry, inputCount,
			descriptor.HasVertexInput, descriptor.HasFragmentPush, descriptor.LinearFilter,
			descriptor.IsCompute, descriptor.Blend, storageImages, storageBuffers, storageBufferStride,
			descriptor.Address, layout, descriptor.Instance, uniformBuffers, inputBuffers, descriptor.HasListPush,
			descriptor.Cull, descriptor.DepthTest, descriptor.Bias,
			reflection.MaterialUniformSlot, reflection.MaterialParameterBytes,
			[.. compared.ComparedTextures], descriptor.SampleCompare, descriptor.AlphaToCoverage, compared));
		return new TechniqueShape(
			new MaterialLayout(inputCount, reflection.MaterialParameterBytes, compared.ComparedCount),
			reflection.UniformBufferBytes,
			descriptor.HasFragmentPush ? DirectX12Technique.ScalarPushSize : 0,
			descriptor.HasListPush ? DirectX12Technique.ListPushSize : 0);
	}


	protected override string AliasingLogPrefix => "DirectX12 ";

	// Tear down the previous graph's transients before the base binds a new graph; the device and caches survive.
	// The base has already drained the GPU when a prior graph existed.
	protected override void ReleaseGraphResources() => _transients?.Dispose();

	protected override void GrowStorage(int id, int newByteSize) => _transients!.GrowStorage(_ctx, id, newByteSize, Graph!.Resources[id].Name);

	protected override void RebuildGraphResources(RenderGraph authored, RenderGraph executed)
	{
		// Members after a bucket's first reuse an earlier member's memory mid-frame and need an aliasing barrier on
		// first use; the first member is the original user (its first use is a full write, fence-ordered across frames).
		_aliasedHandoffIds = Aliases.AliasedMemberIds();
		for (uint i = 0; i < BufferCount; i++)
			_backbufferState[i] = ResourceStates.Present;

		// Build transients from `executed` (same resources/ids; the injected edges only affect derived state). The
		// AliasPlan (the transient-memory partition) is the SAME backend-neutral product the Vulkan engine consumes;
		// this backend turns each bucket into a placed-resource heap shared by its members.
		_transients = new DirectX12Transients(_ctx, executed, Aliases, BufferCount, _presentTarget.Width, _presentTarget.Height);
		_context.SetTransients(_transients);
		SetupReadback(authored);
		SetupTiming();
	}

	// Allocate the GPU-timing query heap + per-slot readback buffers for this graph: 2 timestamps (begin/end)
	// per pass, a sub-range per frame slot. Resized with the graph's pass count; mirrors SetupReadback. WaitIdle()
	// already drained the GPU in SetGraph before this, so disposing the old heap/buffers is safe.
	private void SetupTiming()
	{
		if (_timingHeap.Handle is not null) { _timingHeap.Dispose(); _timingHeap = default; }
		foreach (ref var buffer in _timingReadback.AsSpan())
			if (buffer.Handle is not null) { buffer.Dispose(); buffer = default; }
		Array.Clear(_timingValid);
		ResetTimings();
		_timingPassCount = Barriers.Passes.Length;

		if (_timestampFrequency == 0 || _timingPassCount == 0)
			return;

		var perSlot = (uint)(2 * _timingPassCount);
		var desc = new QueryHeapDesc { Type = QueryHeapType.Timestamp, Count = perSlot * BufferCount, NodeMask = 0 };
		SilkMarshal.ThrowHResult(_ctx.Device.CreateQueryHeap(&desc, out ComPtr<ID3D12QueryHeap> madeTimingHeap));
		_timingHeap = madeTimingHeap;
		for (uint i = 0; i < BufferCount; i++)
			_timingReadback[i] = CreateReadbackBuffer((ulong)perSlot * sizeof(ulong));
	}

	// Wire up GPU→CPU readback for this graph: each readback target gets a per-slot READBACK buffer the
	// executor copies into each frame. Mirrors the Vulkan backend (one per graph.ReadbackTargets entry).
	private void SetupReadback(RenderGraph graph)
	{
		foreach (var readback in _readbacks)
			DestroyReadbackStaging(readback);
		_readbacks.Clear();

		foreach (var target in graph.ReadbackTargets)
			_readbacks.Add(CreateReadbackStaging(target));
	}

	// One g.Readback target's per-slot READBACK staging, sized from the transient's resolved extent. The dimensions
	// and row pitch are captured here and reused by every copy, so they have to be rebuilt whenever the target is.
	private ReadbackBuffers CreateReadbackStaging(ResourceHandle target)
	{
		var (width, height) = _transients!.Extent(target.Id);
		var bpt = (uint)target.Format.BytesPerTexel;
		var rowPitch = Align(width * bpt, 256); // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT
		var readback = new ComPtr<ID3D12Resource>[BufferCount];
		for (uint i = 0; i < BufferCount; i++)
			readback[i] = CreateReadbackBuffer((ulong)rowPitch * height);
		return new ReadbackBuffers
		{
			Id = target.Id,
			Width = width,
			Height = height,
			RowPitch = rowPitch,
			BytesPerTexel = bpt,
			Readback = readback,
			Valid = new bool[BufferCount],
			Packed = new byte[(int)(width * bpt * height)],
		};
	}

	private static void DestroyReadbackStaging(ReadbackBuffers readback)
	{
		foreach (ref var buffer in readback.Readback.AsSpan())
			if (buffer.Handle is not null) { buffer.Dispose(); buffer = default; }
	}

	// Rebuild the staging for every swapchain-sized readback target at the new extent, after Resize has rebuilt the
	// transients it mirrors. Unlike Vulkan, this backend copies with the width/height/row-pitch captured when the
	// staging was built — so stale dimensions here read the *old* footprint out of the *new* resource, silently.
	// A fixed-size target is left alone, exactly as its image is.
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

	protected override Mesh UploadMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) => _meshes.Create(vertices, indices);

	protected override Mesh UploadMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices)
	{
		var stride = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
		if (stride != layout.Stride)
			throw new InvalidOperationException($"DirectX12: vertex element size ({stride} bytes) does not match the layout stride ({layout.Stride} bytes).");
		return _meshes.Create(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertices), stride, indices);
	}

	protected override Mesh UploadSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices) => _meshes.CreateSkinned(vertices, indices);

	// As a texture's: the mesh's copy into its default-heap buffers may still be pending in the upload batch, which
	// executes ahead of the next frame. So its buffers wait for the next value the frame fence signals.
	protected override void RetireMesh(Mesh mesh) => _meshes.Release(mesh.Id, _fenceValue + 1);

	protected override Texture UploadTexture(TextureData data) => _textures.Create(data);

	// Unlike a mesh, a texture may still have a copy pending: its upload is recorded into a batch that executes ahead of
	// the next frame. So it waits for the next value the frame fence signals — after the next frame, which follows the
	// flushed batch on this queue, or after WaitIdle, which flushes the batch before signalling. Nothing else is keyed by
	// it: a material's SRV table can only name a texture no release can reach (a material holds its textures), and a
	// draw list writes its SRVs into this frame's ring slot, which outlives the texture by exactly the frames that read it.
	protected override void RetireTexture(Texture texture) => _textures.Release(texture.Id, _fenceValue + 1);

	[DllImport("kernel32", SetLastError = true)]
	private static extern nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, nint lpName);

	[DllImport("kernel32", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32", SetLastError = true)]
	private static extern int CloseHandle(nint hObject);
}
