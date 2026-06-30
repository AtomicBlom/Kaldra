using System.Runtime.InteropServices;
using Loom.Assets;
using Loom.Core.Animation;
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
	private readonly DirectX12Swapchain _swapchain;
	private readonly DirectX12Triangle _triangle;
	private readonly DirectX12TechniqueCache _techniques;
	private readonly DirectX12DescriptorRing _ring;
	private readonly DirectX12MeshStore _meshes;
	private readonly DirectX12TextureStore _textures;
	private readonly MaterialStore _materials;
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
	{
		_settings = settings;
		_ctx = new DirectX12Context(settings);
		_swapchain = new DirectX12Swapchain(_ctx, hwnd, width, height, BufferCount);

		for (uint i = 0; i < BufferCount; i++)
		{
			SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandAllocator(CommandListType.Direct, out _allocators[i]));
			_ctx.SetObjectName((ID3D12Object*)_allocators[i].Handle, $"frame-alloc#{i}");
		}

		// Created in the recording state, then immediately closed — Render() resets it at the top of each frame.
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandList(
			0, CommandListType.Direct, _allocators[0], default(ComPtr<ID3D12PipelineState>), out _commandList));
		SilkMarshal.ThrowHResult(_commandList.Close());
		_ctx.SetObjectName((ID3D12Object*)_commandList.Handle, "graph-cmdlist");

		SilkMarshal.ThrowHResult(_ctx.Device.CreateFence(0, FenceFlags.None, out _fence));
		_fenceEvent = CreateEventW(0, 0, 0, 0);
		if (_fenceEvent == 0)
			throw new InvalidOperationException("CreateEvent failed for the DirectX12 frame-pacing fence.");

		_triangle = new DirectX12Triangle(_ctx, _swapchain.Format);
		_techniques = new DirectX12TechniqueCache(_ctx);
		_ring = new DirectX12DescriptorRing(_ctx, 512, BufferCount); // per-slot headroom for per-pass SRVs + the resource viewer's debug SRVs
		_meshes = new DirectX12MeshStore(_ctx);
		_textures = new DirectX12TextureStore(_ctx);
		_materials = new MaterialStore();
		_context = new DirectX12RenderContext(_ctx, _techniques, _ring, _meshes, _textures, _materials, (int)BufferCount);
		for (uint i = 0; i < BufferCount; i++)
			_backbufferState[i] = ResourceStates.Present; // a fresh flip-model buffer starts presentable

		// GPU-timing clock: the graphics (Direct) queue's timestamp frequency. A Direct queue always supports
		// timestamps on D3D12; 0 only if the call fails, which disables the profiler (the GpuTimingEnabled setter guards it).
		ulong frequency = 0;
		if (_ctx.GraphicsQueue.GetTimestampFrequency(ref frequency) >= 0)
			_timestampFrequency = frequency;

		Console.WriteLine($"[loom] DirectX12 backend up on {_ctx.DeviceName} ({BufferCount} buffers, {(settings.VSync ? "vsync" : "no-vsync")}).");

		// Shader hot-reload, opt-in. Watch the loose DXIL/reflection artifacts; an external process rewrites
		// them and this picks up the new bytecode + rebuilds affected PSOs. Off by default.
		if (settings.WatchShaderDirectory)
		{
			var source = new LayeredAssetSource(settings.ShaderDirectory);
			_shaderWatcher = new AssetWatcher(source, [".dxil", ".json"], debounceMs: 150);
			_shaderWatcher.Changed += OnShaderArtifactChanged;
			Console.WriteLine($"[loom] watching shaders for hot-reload: {settings.ShaderDirectory}");
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
	protected override void ResetPipelineCaches() => _techniques.Reset();

	public override string BackendName => "DirectX12";
	public override string DeviceName => _ctx.DeviceName;

	// 0 timestamp frequency ⇒ the queue can't timestamp, so the profiler stays permanently unavailable.
	protected override bool CanEnableGpuTiming => _timestampFrequency != 0;

	public override void Render(FrameInputs inputs)
	{
		if (Graph is not null)
			RenderGraphFrame(inputs);
		else
			RenderLegacy(inputs);
	}

	// Pre-executor path (slices 1-2): a fixed clear + hand-baked triangle, used when no graph is set.
	private void RenderLegacy(FrameInputs inputs)
	{
		var index = _swapchain.CurrentIndex;
		WaitForFrame(index); // the allocator we're about to reset must be free on the GPU

		SilkMarshal.ThrowHResult(_allocators[index].Reset());
		SilkMarshal.ThrowHResult(_commandList.Reset(_allocators[index], default(ComPtr<ID3D12PipelineState>)));

		var backbuffer = _swapchain.BackBuffer(index);
		Transition(backbuffer, ResourceStates.Present, ResourceStates.RenderTarget);

		var rtv = _swapchain.RtvHandle(index);
		_commandList.OMSetRenderTargets(1, &rtv, false, (CpuDescriptorHandle*)null);

		// Animated clear so a headless smoke run visibly varies frame-to-frame (and proves the loop is live).
		var t = inputs.Time;
		var color = stackalloc float[4];
		color[0] = 0.10f + 0.06f * MathF.Sin(t);
		color[1] = 0.11f;
		color[2] = 0.20f + 0.06f * MathF.Sin(t * 0.7f);
		color[3] = 1f;
		_commandList.ClearRenderTargetView(rtv, color, 0u, (Box2D<int>*)null);

		_triangle.Record(_commandList, _swapchain.Width, _swapchain.Height);

		// When capturing, copy the rendered backbuffer into a readback buffer in the same submission, then
		// present; otherwise just transition straight to PRESENT.
		var capturing = CapturePath is not null;
		var rowPitch = capturing ? RecordCapture(backbuffer) : 0u;
		if (!capturing)
			Transition(backbuffer, ResourceStates.RenderTarget, ResourceStates.Present);
		SilkMarshal.ThrowHResult(_commandList.Close());

		var list = (ID3D12CommandList*)_commandList.Handle;
		_ctx.GraphicsQueue.ExecuteCommandLists(1, &list);

		_swapchain.Present(_settings.VSync);

		_fenceValue++;
		SilkMarshal.ThrowHResult(_ctx.GraphicsQueue.Signal(_fence, _fenceValue));
		_frameFenceValues[index] = _fenceValue;

		_debugErrors += _ctx.DrainDebugMessages(); // surface any D3D12 debug-layer messages this frame produced

		if (capturing)
		{
			WaitForFrame(index); // block until this frame's copy finished, then map + encode
			WriteCapture(CapturePath!, rowPitch);
			CapturePath = null;
		}
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
		_swapchain.Resize(width, height);
		Array.Clear(_frameFenceValues);

		// Transients sized to the backbuffer must follow the new extent (re-derive at the new size).
		if (Graph is not null)
		{
			_transients?.Dispose();
			_transients = new DirectX12Transients(_ctx, Graph!, Aliases, BufferCount, _swapchain.Width, _swapchain.Height);
			_context.SetTransients(_transients);
			for (uint i = 0; i < BufferCount; i++)
				_backbufferState[i] = ResourceStates.Present;
		}
	}

	public override void WaitIdle()
	{
		_fenceValue++;
		SilkMarshal.ThrowHResult(_ctx.GraphicsQueue.Signal(_fence, _fenceValue));
		if (_fence.GetCompletedValue() < _fenceValue)
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*)_fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}
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
			Console.WriteLine($"[loom] DirectX12 debug layer: {(_debugErrors == 0 ? "clean (no errors/corruption)" : $"{_debugErrors} error/corruption message(s)")}.");

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
		_meshes.Dispose();
		_textures.Dispose();
		_ring.Dispose();
		_techniques.Dispose();
		_triangle.Dispose();
		_fence.Dispose();
		_commandList.Dispose();
		for (uint i = 0; i < BufferCount; i++)
			_allocators[i].Dispose();
		_swapchain.Dispose();
		_ctx.Dispose();
	}

	/// <summary>
	/// Register a technique under a logical name. Resolves the real shader the Loom build emits next to the
	/// SPIR-V — a cross-target <c>&lt;name&gt;.hlsl</c> (Slang→HLSL) plus its <c>&lt;name&gt;.json</c> reflection
	/// (which gives the sampled-input count, so the app states only pipeline intent) — exactly as the Vulkan backend
	/// resolves embedded SPIR-V. A shader with no resolvable/compilable HLSL falls back to the backend's built-in
	/// stand-in.
	/// </summary>
	protected override void BakeTechnique(string name, TechniqueDescriptor descriptor)
	{
		var dir = _settings.ShaderDirectory;
		var shader = descriptor.Shader;
		// The descriptor-bound resource counts come from the shader's reflection sidecar (the author states only
		// pipeline intent); a built-in stand-in with no sidecar reflects as empty.
		var reflection = DirectX12Shaders.ResolveReflection(dir, shader);

		// Prefer the build-emitted SIGNED DXIL (offline, shipped like the Vulkan .spv — no runtime compiler):
		// compute resolves <name>.cs.dxil; graphics resolves <name>.vs.dxil + <name>.ps.dxil. A shader with no
		// DXIL yet (not cross-targeted, so the build's emit produced nothing) falls back to the built-in stand-in
		// FXC-compiled here (d3dcompiler ships with Windows), so the port never breaks a demo mid-way.
		byte[] vertexBytecode, fragmentBytecode;
		int inputCount;
		var storageImages = 0;
		var storageBuffers = 0;
		var storageBufferStride = 0;
		// Descriptor-bound constant buffers (the per-frame uniform seam) — reflected from the sidecar, bound
		// as a root CBV at b1 (0 for a built-in stand-in, which has no UBO).
		var uniformBuffers = reflection.UniformBufferCount;
		// Read-only structured-buffer inputs, bound as root SRVs: a compute's inputs (skin's bind-pose verts + bone
		// palette) or a graphics technique's app-writable storage buffers (g.Storage SSBOs). 0 for a built-in
		// stand-in, whose empty reflection reports none.
		var inputBuffers = reflection.InputBufferCount;
		string source;

		if (descriptor.IsCompute)
		{
			var cs = DirectX12Shaders.TryResolveStageDxil(dir, shader, "cs");
			if (cs is not null)
			{
				(vertexBytecode, fragmentBytecode) = ([], cs);
				inputCount = reflection.SampleCount;
				// Compute storage shape from reflection: a RW texture (exposure) / RW structured buffer (particles)
				// → UAV table (the stride lets the shader's GetDimensions work); a raw buffer (stride 0) → root UAV.
				// RW structured/byte buffers = all storage buffers minus the read-only inputs; the stride lets the
				// shader's GetDimensions work (a UAV descriptor), else a raw buffer (stride 0) rides a root UAV.
				storageImages = reflection.StorageImageCount;
				storageBuffers = reflection.StorageBufferCount - reflection.InputBufferCount;
				storageBufferStride = reflection.StorageBufferStride;
				source = $"{shader}.cs.dxil (Slang→signed DXIL)";
			}
			else if (DirectX12Shaders.Builtins.TryGetValue(shader, out var builtin))
			{
				(vertexBytecode, fragmentBytecode) = ([], DirectX12Shaders.CompileToBytes(builtin.Hlsl, descriptor.FragmentEntry, "cs_5_1"));
				inputCount = builtin.InputCount;
				storageBuffers = 1; // the built-in compute stand-in is the GPU-particle buffer (root UAV at u0)
				source = "built-in (FXC)";
			}
			else
			{
				throw new NotSupportedException($"DirectX12: compute shader '{shader}' has no '{shader}.cs.dxil' in '{dir}' and no built-in stand-in.");
			}
		}
		else
		{
			var vs = DirectX12Shaders.TryResolveStageDxil(dir, shader, "vs");
			var ps = DirectX12Shaders.TryResolveStageDxil(dir, shader, "ps");
			if (vs is not null && ps is not null)
			{
				(vertexBytecode, fragmentBytecode) = (vs, ps);
				inputCount = reflection.SampleCount;
				source = $"{shader}.{{vs,ps}}.dxil (Slang→signed DXIL)";
			}
			else if (DirectX12Shaders.Builtins.TryGetValue(shader, out var builtin))
			{
				vertexBytecode = DirectX12Shaders.CompileToBytes(builtin.Hlsl, descriptor.VertexEntry, "vs_5_1");
				fragmentBytecode = DirectX12Shaders.CompileToBytes(builtin.Hlsl, descriptor.FragmentEntry, "ps_5_1");
				inputCount = builtin.InputCount;
				source = "built-in (FXC)";
			}
			else
			{
				throw new NotSupportedException($"DirectX12: shader '{shader}' has no '{shader}.[vs,ps].dxil' in '{dir}' and no built-in stand-in.");
			}
		}

		Console.WriteLine($"[loom] DirectX12 technique '{name}' ← {source}.");
		// Resolve the per-vertex layout (explicit, else canonical pos/normal/uv for a mesh technique, else none),
		// mirroring the Vulkan backend — a non-null Instance marks it instanced (per-technique layouts).
		var layout = descriptor.Layout ?? (descriptor.HasVertexInput ? VertexLayout.MeshStandard : null);
		_techniques.Register(name, new DirectX12TechniqueTemplate(
			vertexBytecode, fragmentBytecode, descriptor.VertexEntry, descriptor.FragmentEntry, inputCount,
			descriptor.HasVertexInput, descriptor.HasFragmentPush, descriptor.LinearFilter,
			descriptor.IsCompute, descriptor.Blend, storageImages, storageBuffers, storageBufferStride,
			descriptor.Address, layout, descriptor.Instance, uniformBuffers, inputBuffers, descriptor.HasListPush,
			descriptor.Cull));

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
		_transients = new DirectX12Transients(_ctx, executed, Aliases, BufferCount, _swapchain.Width, _swapchain.Height);
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
		SilkMarshal.ThrowHResult(_ctx.Device.CreateQueryHeap(&desc, out _timingHeap));
		for (uint i = 0; i < BufferCount; i++)
			_timingReadback[i] = CreateReadbackBuffer((ulong)perSlot * sizeof(ulong));
	}

	// Wire up GPU→CPU readback for this graph: each readback target gets a per-slot READBACK buffer the
	// executor copies into each frame. Mirrors the Vulkan backend (one per graph.ReadbackTargets entry).
	private void SetupReadback(RenderGraph graph)
	{
		foreach (var readback in _readbacks)
			foreach (ref var buffer in readback.Readback.AsSpan())
				if (buffer.Handle is not null) { buffer.Dispose(); buffer = default; }
		_readbacks.Clear();

		foreach (var target in graph.ReadbackTargets)
		{
			var (width, height) = _transients!.Extent(target.Id);
			var bpt = BytesPerTexel(target.Format);
			var rowPitch = Align(width * bpt, 256); // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT
			var readback = new ComPtr<ID3D12Resource>[BufferCount];
			for (uint i = 0; i < BufferCount; i++)
				readback[i] = CreateReadbackBuffer((ulong)rowPitch * height);
			_readbacks.Add(new ReadbackBuffers
			{
				Id = target.Id,
				Width = width,
				Height = height,
				RowPitch = rowPitch,
				BytesPerTexel = bpt,
				Readback = readback,
				Valid = new bool[BufferCount],
				Packed = new byte[(int)(width * bpt * height)],
			});
		}
	}

	public override Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) => _meshes.Create(vertices, indices);

	public override Mesh CreateMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices)
	{
		var stride = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
		if (stride != layout.Stride)
			throw new InvalidOperationException($"DirectX12: vertex element size ({stride} bytes) does not match the layout stride ({layout.Stride} bytes).");
		return _meshes.Create(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertices), stride, indices);
	}

	public override Mesh CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices) => _meshes.CreateSkinned(vertices, indices);

	public override Texture CreateTexture(TextureData data) => _textures.Create(data);

	public override Material CreateMaterial(params ReadOnlySpan<Texture> textures) => _materials.Create(textures);

	[DllImport("kernel32", SetLastError = true)]
	private static extern nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, nint lpName);

	[DllImport("kernel32", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32", SetLastError = true)]
	private static extern int CloseHandle(nint hObject);
}
