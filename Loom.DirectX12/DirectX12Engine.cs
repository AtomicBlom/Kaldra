using System.Diagnostics;
using System.Runtime.InteropServices;

using Loom.Assets;
using Loom.Core;
using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Graph;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

using DxgiFormat = Silk.NET.DXGI.Format;
using Format = Loom.Core.Graph.Format;

namespace Loom.DirectX12;

/// <summary>
/// The DirectX 12 <see cref="IEngine"/> (Planning.md §2 "bake + record" layer, DirectX12 edition). Stands up the
/// device + swapchain and records command lists with fence-based frame pacing. This first slice mirrors how the
/// Vulkan backend started (M0/M1): it clears the backbuffer (and, next slice, draws a hand-baked triangle)
/// before the render-graph executor is ported. The graph-driven members therefore throw
/// <see cref="NotSupportedException"/> for now — they land in the following slice, when
/// <see cref="GraphSchedule"/>/<see cref="BarrierPlan"/>/<c>AliasPlan</c> are consumed here exactly as the
/// Vulkan engine consumes them.
/// </summary>
public sealed unsafe class DirectX12Engine : EngineBase
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

	// The active graph + its derived plans (null until SetGraph). Per-buffer backbuffer state is tracked because
	// D3D12 transition barriers need the resource's exact current state (see DirectX12StateMapping).
	private RenderGraph? _graph;
	private BarrierPlan? _barrierPlan;
	private AliasPlan? _aliasPlan;
	private readonly ResourceStates[] _backbufferState = new ResourceStates[BufferCount];
	// Aliased transients re-barriered this frame: a bucket member that shares memory needs one D3D12 aliasing
	// barrier before its first (re)activation each frame (the hand-off from whatever member last used the memory).
	private readonly HashSet<int> _aliasBarriered = [];

	private ComPtr<ID3D12Resource> _readback;
	private ulong _readbackSize;

	// Picking (§7.4): the graph's first readback target (the object-id buffer) is copied into a per-slot READBACK
	// buffer each frame; after that slot's fence the copy is decoded to _pickCpu, which SamplePick reads (a few
	// frames stale — invisible for hover). _pickId < 0 when the active graph declares no readback target.
	private int _pickId = -1;
	private uint _pickWidth, _pickHeight, _pickRowPitch;
	private Format _pickFormat;
	private readonly ComPtr<ID3D12Resource>[] _pickReadback = new ComPtr<ID3D12Resource>[BufferCount];
	private readonly bool[] _pickValid = new bool[BufferCount];
	private float[] _pickCpu = [];

	// GPU per-pass timing (§12 M13 profiler): one TIMESTAMP query heap with 2 queries (begin/end) per pass and a
	// per-slot sub-range, plus a per-slot READBACK buffer ResolveQueryData writes into. Decoded after the slot's
	// fence (like the pick readback) — a frame or two stale. Runtime-toggled (off by default).
	private ComPtr<ID3D12QueryHeap> _timingHeap;
	private readonly ComPtr<ID3D12Resource>[] _timingReadback = new ComPtr<ID3D12Resource>[BufferCount];
	private readonly bool[] _timingValid = new bool[BufferCount];
	private int _timingPassCount;
	private ulong _timestampFrequency; // ticks per second, from the graphics queue (0 ⇒ timing unavailable)

	// M13 resource viewer: the active graph's colour/depth resources the host can preview. Rebuilt each SetGraph.
	private readonly List<InspectableResource> _inspectable = [];

	// Shader hot-reload (M13), opt-in. The watcher maps a changed artifact to a logical shader name; the base's
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
		_ring = new DirectX12DescriptorRing(_ctx, 512, BufferCount); // per-slot headroom for per-pass SRVs + the M13 resource viewer's debug SRVs
		_meshes = new DirectX12MeshStore(_ctx);
		_textures = new DirectX12TextureStore(_ctx);
		_materials = new MaterialStore();
		_context = new DirectX12RenderContext(_ctx, _techniques, _ring, _meshes, _textures, _materials, (int)BufferCount);
		for (uint i = 0; i < BufferCount; i++)
			_backbufferState[i] = ResourceStates.Present; // a fresh flip-model buffer starts presentable

		// GPU-timing clock (M13): the graphics (Direct) queue's timestamp frequency. A Direct queue always supports
		// timestamps on D3D12; 0 only if the call fails, which disables the profiler (the GpuTimingEnabled setter guards it).
		ulong frequency = 0;
		if (_ctx.GraphicsQueue.GetTimestampFrequency(ref frequency) >= 0)
			_timestampFrequency = frequency;

		Console.WriteLine($"[loom] DirectX12 backend up on {_ctx.DeviceName} ({BufferCount} buffers, {(settings.VSync ? "vsync" : "no-vsync")}).");

		// Shader hot-reload (M13), opt-in. Watch the loose DXIL/reflection artifacts; an external process rewrites
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

	public override IReadOnlyList<InspectableResource> InspectableResources => _inspectable;

	public override void Render(FrameInputs inputs)
	{
		if (_graph is not null)
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

	// Graph-driven path: consume the neutral BarrierPlan derived in SetGraph. Per pass, emit its barriers, bind
	// its derived render target(s), run its record callback; then the final present transition. The DirectX12 mirror
	// of VulkanEngine's per-frame RecordGraph.
	private void RenderGraphFrame(FrameInputs inputs)
	{
		DrainShaderReloads(); // M13: pick up any hot-reloaded shaders before recording this frame

		var index = _swapchain.CurrentIndex;
		WaitForFrame(index);

		// This slot's GPU work (a couple frames back) is now done, so its readback buffer is safe to map — decode
		// the object-id copy it holds into _pickCpu for SamplePick (a few frames stale; invisible for hover).
		DownloadPick(index);
		// Same story for the timing queries the slot resolved.
		DownloadTiming(index);

		SilkMarshal.ThrowHResult(_allocators[index].Reset());
		SilkMarshal.ThrowHResult(_commandList.Reset(_allocators[index], default(ComPtr<ID3D12PipelineState>)));

		// Bind the shader-visible heap for this command list, then reset its per-frame bump allocator.
		var heap = _ring.Heap.Handle;
		_commandList.SetDescriptorHeaps(1, &heap);
		_ring.Reset(index); // this slot's own region — must not clobber the other in-flight frame's live descriptors
		_aliasBarriered.Clear(); // each aliased transient gets one aliasing barrier on its first use this frame
		_context.BeginFrame(_commandList, inputs, _settings.HdrOutput, index, _swapchain.Width, _swapchain.Height);

		// GPU timing (M13): bracket each pass's own work (after its pre-barriers) with a begin/end timestamp into this
		// slot's sub-range of the heap. Timestamp queries use EndQuery only (no BeginQuery).
		var timing = GpuTimingEnabled && _timingHeap.Handle is not null;
		var timingBase = (uint)(2 * _timingPassCount) * index;
		var passIndex = 0;

		foreach (var pass in _barrierPlan!.Passes)
		{
			// Wrap each pass in a debug-label scope, nested under groups derived from its dotted name, so a PIX
			// capture reads as a "bloom" group containing "down1", … instead of a flat list. [Conditional]:
			// vanishes without UseNamedGPUObjects.
			BeginPassLabels(pass);
			foreach (var transition in pass.Transitions)
				EmitGraphTransition(index, transition);

			if (timing)
				_commandList.EndQuery(_timingHeap, QueryType.Timestamp, timingBase + (uint)(2 * passIndex));
			if (pass.Pass.Kind == PassKind.Compute)
				RecordComputePass(pass);
			else
				RecordRasterPass(index, pass);
			if (timing)
				_commandList.EndQuery(_timingHeap, QueryType.Timestamp, timingBase + (uint)(2 * passIndex + 1));
			_ctx.EndDebugLabel(_commandList); // close the leaf scope opened by BeginPassLabels
			passIndex++;
		}
		CloseTrailingLabels(); // close any groups still open after the last pass

		// Picking: copy this frame's object-id target into the slot's readback buffer (no-op if the graph has none).
		RecordPick(index);

		// Resolve this slot's timestamps into its readback buffer (read back after the slot's next fence).
		if (timing)
		{
			_commandList.ResolveQueryData(_timingHeap, QueryType.Timestamp, timingBase, (uint)(2 * _timingPassCount), _timingReadback[index], 0);
			_timingValid[index] = true;
		}

		// Optional capture: copy the present target before the final present transition (which then runs from
		// COPY_SOURCE, since EmitGraphTransition tracks the real state).
		var capturing = CapturePath is not null;
		uint rowPitch = 0;
		if (capturing)
		{
			var backbuffer = _swapchain.BackBuffer(index);
			if (_backbufferState[index] != ResourceStates.CopySource)
			{
				Transition(backbuffer, _backbufferState[index], ResourceStates.CopySource);
				_backbufferState[index] = ResourceStates.CopySource;
			}
			rowPitch = CopyBackbufferToReadback(backbuffer);
		}

		foreach (var transition in _barrierPlan.FinalTransitions)
			EmitGraphTransition(index, transition);

		SilkMarshal.ThrowHResult(_commandList.Close());
		var list = (ID3D12CommandList*)_commandList.Handle;
		_ctx.GraphicsQueue.ExecuteCommandLists(1, &list);
		_swapchain.Present(_settings.VSync);

		_fenceValue++;
		SilkMarshal.ThrowHResult(_ctx.GraphicsQueue.Signal(_fence, _fenceValue));
		_frameFenceValues[index] = _fenceValue;
		_debugErrors += _ctx.DrainDebugMessages();

		if (capturing)
		{
			WaitForFrame(index);
			WriteCapture(CapturePath!, rowPitch);
			CapturePath = null;
		}
	}

	// Bind a raster pass's colour + depth target(s) from its derived AttachmentActions, apply load actions, then
	// run the callback. Colour targets bind RTVs; a depth target binds the DSV and bakes the depth test into the
	// pass's pipelines (via BeginPass). Targets may be the backbuffer (swapchain RTV) or graph transients.
	private void RecordRasterPass(uint index, PassBarriers pass)
	{
		var rtv = stackalloc CpuDescriptorHandle[8];
		var formats = stackalloc DxgiFormat[8];
		var black = stackalloc float[4] { 0f, 0f, 0f, 1f };
		var colorCount = 0;
		uint width = 0, height = 0;

		CpuDescriptorHandle dsv = default;
		var hasDepth = false;
		var depthClear = false;
		var depthReadOnly = false;
		var depthFormat = DxgiFormat.FormatUnknown;

		foreach (var attachment in pass.Attachments)
		{
			var id = attachment.Resource.Id;
			if (attachment.State == ResourceState.ColorAttachment)
			{
				if (colorCount >= 8)
					throw new NotSupportedException("DirectX12: a pass declares more than 8 colour attachments; D3D12 caps simultaneous render targets at 8 (D3D12_SIMULTANEOUS_RENDER_TARGET_COUNT).");
				if (attachment.Resource.IsBackbuffer)
				{
					rtv[colorCount] = _swapchain.RtvHandle(index);
					formats[colorCount] = _swapchain.RenderTargetFormat; // the sRGB RTV format — the PSO must match the actual view
					width = _swapchain.Width;
					height = _swapchain.Height;
				}
				else
				{
					rtv[colorCount] = _transients!.View(index, id);
					formats[colorCount] = _transients.Format(index, id);
					(width, height) = _transients.Extent(id);
				}
				if (attachment.Load == LoadAction.Clear)
					_commandList.ClearRenderTargetView(rtv[colorCount], black, 0u, (Box2D<int>*)null);
				colorCount++;
			}
			else // DepthAttachment or DepthReadOnly
			{
				dsv = _transients!.View(index, id);
				depthFormat = _transients.Format(index, id);
				depthReadOnly = attachment.State == ResourceState.DepthReadOnly;
				depthClear = attachment.Load == LoadAction.Clear;
				if (width == 0)
					(width, height) = _transients.Extent(id);
				hasDepth = true;
			}
		}

		if (colorCount == 0 && !hasDepth)
			throw new NotSupportedException("DirectX12: a raster pass with no colour or depth target is not supported yet.");

		_commandList.OMSetRenderTargets((uint)colorCount, colorCount > 0 ? rtv : null, false, hasDepth ? &dsv : null);

		// Colour clears were emitted above (per attachment); clear depth to 1.0 here if the pass produces it.
		if (hasDepth && depthClear)
			_commandList.ClearDepthStencilView(dsv, ClearFlags.Depth, 1f, 0, 0u, (Box2D<int>*)null);

		// Negative-height viewport (origin at the bottom): turns D3D12's (1-ndc.y) NDC→screen map into Vulkan's
		// (ndc.y+1). Paired with the Vulkan-style flipped projection (DirectX12RenderContext.ClipSpace) this renders
		// geometry upright AND makes every fullscreen pass map uv→screen exactly like Vulkan — so the shared
		// shaders need no per-backend Y handling (see ClipSpace). The scissor stays in pixel space (not flipped).
		var viewport = new Viewport { TopLeftX = 0, TopLeftY = height, Width = width, Height = -(float)height, MinDepth = 0, MaxDepth = 1 };
		var scissor = new Box2D<int>(0, 0, (int)width, (int)height);
		_commandList.RSSetViewports(1, &viewport);
		_commandList.RSSetScissorRects(1, &scissor);

		_context.BeginPass(new ReadOnlySpan<DxgiFormat>(formats, colorCount), depthFormat, depthReadOnly);
		pass.Pass.Record?.Invoke(_context);
	}

	// A compute pass has no render targets and no render-target binding — its resource transitions (the storage
	// buffer → UNORDERED_ACCESS) were emitted before this; just record the dispatch. Empty attachments keep the
	// context's pass state stable (the DirectX12 mirror of VulkanEngine's compute branch in RecordPass).
	private void RecordComputePass(PassBarriers pass)
	{
		_context.BeginPass(ReadOnlySpan<DxgiFormat>.Empty, DxgiFormat.FormatUnknown, false);
		pass.Pass.Record?.Invoke(_context);
	}

	// Open this pass's debug-label scopes: close the groups it leaves behind, open the ones it enters, then open
	// its own leaf scope (closed by the caller after the pass records). [Conditional] so the whole thing — loops
	// and all — compiles away without UseNamedGPUObjects.
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	private void BeginPassLabels(PassBarriers pass)
	{
		for (var i = 0; i < pass.LabelCloses; i++)
			_ctx.EndDebugLabel(_commandList);
		foreach (var group in pass.LabelOpens)
			_ctx.BeginDebugLabel(_commandList, group);
		_ctx.BeginDebugLabel(_commandList, pass.LeafLabel);
	}

	// Close the debug-label groups still open after the final pass (balances BeginPassLabels' opens).
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	private void CloseTrailingLabels()
	{
		for (var i = 0; i < _barrierPlan!.TrailingLabelCloses; i++)
			_ctx.EndDebugLabel(_commandList);
	}

	// Track the resource's real D3D12 state and emit a transition only when the required state differs (D3D12
	// barriers need the exact current state, unlike Vulkan's "Undefined" old layout — see DirectX12StateMapping).
	private void EmitGraphTransition(uint index, ResourceTransition transition)
	{
		var target = DirectX12StateMapping.For(transition.To);
		var id = transition.Resource.Id;

		if (transition.Resource.IsBackbuffer)
		{
			if (_backbufferState[index] == target)
				return;
			Transition(_swapchain.BackBuffer(index), _backbufferState[index], target);
			_backbufferState[index] = target;
			return;
		}

		// An aliased transient reuses memory another bucket member held — before its first (re)activation this
		// frame, an aliasing barrier orders this use after whatever last touched the shared memory (and marks its
		// contents undefined, which the pass's clear/full-write then initialises). The DirectX12 analogue of the
		// Vulkan backend's widened-source-scope birth barrier; once per resource per frame.
		if (_transients!.IsAliasedMember(id) && _aliasBarriered.Add(id))
			EmitAliasingBarrier(_transients.Resource(index, id));

		var current = _transients.State(index, id);
		if (current == target)
			return;
		Transition(_transients.Resource(index, id), current, target);
		_transients.SetState(index, id, target);
	}

	// A D3D12 aliasing barrier handing the bucket's shared memory to <paramref name="after"/> (before = null:
	// any resource previously on that memory is deactivated — conservative but correct, and hand-offs are few).
	private void EmitAliasingBarrier(ComPtr<ID3D12Resource> after)
	{
		var barrier = new ResourceBarrier
		{
			Type = ResourceBarrierType.Aliasing,
			Flags = ResourceBarrierFlags.None,
			Anonymous = new ResourceBarrierUnion
			{
				Aliasing = new ResourceAliasingBarrier { PResourceBefore = (ID3D12Resource*)null, PResourceAfter = after.Handle }
			}
		};
		_commandList.ResourceBarrier(1, &barrier);
	}

	/// <summary>
	/// Record (into the open command list) the copy of the just-rendered backbuffer into the readback buffer,
	/// leaving the backbuffer in PRESENT state. Returns the row pitch of the readback layout.
	/// </summary>
	private uint RecordCapture(ComPtr<ID3D12Resource> backbuffer)
	{
		Transition(backbuffer, ResourceStates.RenderTarget, ResourceStates.CopySource);
		var rowPitch = CopyBackbufferToReadback(backbuffer);
		Transition(backbuffer, ResourceStates.CopySource, ResourceStates.Present);
		return rowPitch;
	}

	/// <summary>Record the copy of <paramref name="backbuffer"/> (already in COPY_SOURCE) into the readback buffer; returns the row pitch.</summary>
	private uint CopyBackbufferToReadback(ComPtr<ID3D12Resource> backbuffer)
	{
		var width = _swapchain.Width;
		var height = _swapchain.Height;
		var rowPitch = Align(width * 4, 256); // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT
		EnsureReadback((ulong)rowPitch * height);

		var dst = new TextureCopyLocation
		{
			PResource = _readback.Handle,
			Type = TextureCopyType.PlacedFootprint,
			Anonymous = new TextureCopyLocationUnion
			{
				PlacedFootprint = new PlacedSubresourceFootprint
				{
					Offset = 0,
					Footprint = new SubresourceFootprint { Format = _swapchain.Format, Width = width, Height = height, Depth = 1, RowPitch = rowPitch }
				}
			}
		};
		var src = new TextureCopyLocation
		{
			PResource = backbuffer.Handle,
			Type = TextureCopyType.SubresourceIndex,
			Anonymous = new TextureCopyLocationUnion { SubresourceIndex = 0 }
		};
		_commandList.CopyTextureRegion(&dst, 0, 0, 0, &src, (Box*)null);
		return rowPitch;
	}

	private void EnsureReadback(ulong size)
	{
		if (_readback.Handle is not null && _readbackSize >= size)
			return;
		if (_readback.Handle is not null)
			_readback.Dispose();

		var heapProps = new HeapProperties
		{
			Type = HeapType.Readback,
			CPUPageProperty = CpuPageProperty.Unknown,
			MemoryPoolPreference = MemoryPool.Unknown,
			CreationNodeMask = 1,
			VisibleNodeMask = 1
		};
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = size,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Silk.NET.DXGI.Format.FormatUnknown,
			SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.CopyDest, (ClearValue*)null, out _readback));
		_readbackSize = size;
	}

	private void WriteCapture(string path, uint rowPitch)
	{
		var width = (int)_swapchain.Width;
		var height = (int)_swapchain.Height;

		void* mapped;
		SilkMarshal.ThrowHResult(_readback.Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped));
		var rgba = new byte[width * height * 4];
		var src = (byte*)mapped;
		for (var y = 0; y < height; y++)
			new ReadOnlySpan<byte>(src + (long)y * rowPitch, width * 4).CopyTo(rgba.AsSpan(y * width * 4));
		_readback.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

		PngWriter.WriteRgba(path, width, height, rgba);
		Console.WriteLine($"[loom] DirectX12 captured {width}x{height} -> {path}");
	}

	// Record the copy of this frame's object-id target into the slot's READBACK buffer (after the passes that wrote
	// + read it). Transition the id target to COPY_SOURCE and track the new state, so next frame's first use of it
	// transitions from the right state (D3D12 needs the exact current state). No-op when the graph has no readback.
	private void RecordPick(uint index)
	{
		if (_pickId < 0)
			return;

		var current = _transients!.State(index, _pickId);
		if (current != ResourceStates.CopySource)
		{
			Transition(_transients.Resource(index, _pickId), current, ResourceStates.CopySource);
			_transients.SetState(index, _pickId, ResourceStates.CopySource);
		}

		var dst = new TextureCopyLocation
		{
			PResource = _pickReadback[index].Handle,
			Type = TextureCopyType.PlacedFootprint,
			Anonymous = new TextureCopyLocationUnion
			{
				PlacedFootprint = new PlacedSubresourceFootprint
				{
					Offset = 0,
					Footprint = new SubresourceFootprint { Format = _transients.Format(index, _pickId), Width = _pickWidth, Height = _pickHeight, Depth = 1, RowPitch = _pickRowPitch }
				}
			}
		};
		var src = new TextureCopyLocation
		{
			PResource = _transients.Resource(index, _pickId).Handle,
			Type = TextureCopyType.SubresourceIndex,
			Anonymous = new TextureCopyLocationUnion { SubresourceIndex = 0 }
		};
		_commandList.CopyTextureRegion(&dst, 0, 0, 0, &src, (Box*)null);
		_pickValid[index] = true;
	}

	// Decode the slot's readback buffer (this frame's wait guaranteed its copy finished) into _pickCpu. The id
	// targets are R16F (Half); read row by row to skip the 256-aligned row padding. No-op until the slot's first copy.
	private void DownloadPick(uint index)
	{
		if (_pickId < 0 || !_pickValid[index])
			return;

		void* mapped;
		SilkMarshal.ThrowHResult(_pickReadback[index].Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped));
		var src = (byte*)mapped;
		for (uint y = 0; y < _pickHeight; y++)
		{
			var row = new ReadOnlySpan<Half>(src + (long)y * _pickRowPitch, (int)_pickWidth);
			for (uint x = 0; x < _pickWidth; x++)
				_pickCpu[y * _pickWidth + x] = (float)row[(int)x];
		}
		_pickReadback[index].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
	}

	// Read this slot's resolved timestamps and decode them into _timings + the frame total. Called after the slot's
	// fence, so the resolve is complete. The readback buffer is tightly-packed UINT64 (no row pitch); the timestamp
	// frequency is ticks per second, so 1000/frequency is the milliseconds-per-tick the decoder needs.
	private void DownloadTiming(uint index)
	{
		if (!GpuTimingEnabled || _timingHeap.Handle is null || _timingPassCount == 0 || !_timingValid[index])
			return;

		void* mapped;
		SilkMarshal.ThrowHResult(_timingReadback[index].Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped));
		var t = new ReadOnlySpan<ulong>(mapped, 2 * _timingPassCount);
		DecodeTimings(t, _barrierPlan!, 1000.0 / _timestampFrequency);
		_timingReadback[index].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
	}

	private ComPtr<ID3D12Resource> CreateReadbackBuffer(ulong size)
	{
		var heapProps = new HeapProperties { Type = HeapType.Readback, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = size,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = DxgiFormat.FormatUnknown,
			SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};
		ComPtr<ID3D12Resource> buffer = default;
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.CopyDest, (ClearValue*)null, out buffer));
		return buffer;
	}

	private static uint BytesPerTexel(Format format) => format switch
	{
		Format.R16F => 2,
		Format.R32Uint => 4,
		_ => throw new NotSupportedException($"DirectX12: readback of format {format} is not supported (the id buffer is R16F)."),
	};

	private static uint Align(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

	private void WaitForFrame(uint index)
	{
		var target = _frameFenceValues[index];
		if (target != 0 && _fence.GetCompletedValue() < target)
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(target, (void*)_fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}
	}

	private void Transition(ComPtr<ID3D12Resource> resource, ResourceStates before, ResourceStates after)
	{
		var barrier = new ResourceBarrier
		{
			Type = ResourceBarrierType.Transition,
			Flags = ResourceBarrierFlags.None,
			Anonymous = new ResourceBarrierUnion
			{
				Transition = new ResourceTransitionBarrier
				{
					PResource = resource.Handle,
					Subresource = 0xffffffff, // D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES
					StateBefore = before,
					StateAfter = after
				}
			}
		};
		_commandList.ResourceBarrier(1, &barrier);
	}

	public override void Resize(int width, int height)
	{
		WaitIdle();
		_swapchain.Resize(width, height);
		Array.Clear(_frameFenceValues);

		// Transients sized to the backbuffer must follow the new extent (re-derive at the new size).
		if (_graph is not null)
		{
			_transients?.Dispose();
			_transients = new DirectX12Transients(_ctx, _graph, _aliasPlan!, BufferCount, _swapchain.Width, _swapchain.Height);
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

		_shaderWatcher?.Dispose(); // stop the hot-reload watcher before tearing the device down (M13)

		try { WaitIdle(); }
		catch { /* the device may already be lost; tear down regardless */ }

		_debugErrors += _ctx.DrainDebugMessages();
		if (_ctx.InfoQueue.Handle is not null)
			Console.WriteLine($"[loom] DirectX12 debug layer: {(_debugErrors == 0 ? "clean (no errors/corruption)" : $"{_debugErrors} error/corruption message(s)")}.");

		if (_fenceEvent != 0)
			CloseHandle(_fenceEvent);
		if (_readback.Handle is not null)
			_readback.Dispose();
		foreach (ref var buffer in _pickReadback.AsSpan())
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
	/// (which gives the sampled-input count, so the app states only pipeline intent — M7-B4) — exactly as the
	/// Vulkan backend resolves embedded SPIR-V. A shader with no resolvable/compilable HLSL falls back to the
	/// backend's built-in stand-in, so the real-shader pipeline can be adopted shader-by-shader without breaking
	/// a demo mid-port.
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
		// M15-0: descriptor-bound constant buffers (the per-frame uniform seam) — reflected from the sidecar, bound
		// as a root CBV at b1 (0 for a built-in stand-in, which has no UBO).
		var uniformBuffers = reflection.UniformBufferCount;
		// M15-2: read-only structured-buffer compute inputs (skin's bind-pose verts + bone palette) — root SRVs.
		var inputBuffers = descriptor.IsCompute ? reflection.InputBufferCount : 0;
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
		// mirroring the Vulkan backend — a non-null Instance marks it instanced (M12 — per-technique layouts).
		var layout = descriptor.Layout ?? (descriptor.HasVertexInput ? VertexLayout.MeshStandard : null);
		_techniques.Register(name, new DirectX12TechniqueTemplate(
			vertexBytecode, fragmentBytecode, descriptor.VertexEntry, descriptor.FragmentEntry, inputCount,
			descriptor.HasVertexInput, descriptor.HasFragmentPush, descriptor.LinearFilter,
			descriptor.IsCompute, descriptor.ParticleInput, descriptor.Blend, storageImages, storageBuffers, storageBufferStride,
			descriptor.Address, layout, descriptor.Instance, uniformBuffers, inputBuffers, descriptor.HasListPush));

	}

	public override bool ResourceInspectionEnabled { get; set; }

	/// <summary>
	/// Make <paramref name="graph"/> the active graph: derive its schedule + barrier plan (the backend-neutral
	/// Execute-layer products, identical to what the Vulkan engine consumes) and route <see cref="Render"/>
	/// through the executor. Transient-resource allocation (aliasing, the sample/blit path) lands next slice.
	/// </summary>
	public override void SetGraph(RenderGraph graph)
	{
		ArgumentNullException.ThrowIfNull(graph);
		if (_graph is not null)
			WaitIdle();

		_graph = graph;

		// Resource viewer: enumerate the colour/depth resources the host can preview, and (when inspection is enabled)
		// inject HUD .Samples edges so the Execute layer derives their keep-alive + PixelShaderResource transition —
		// the viewer samples live images, not aliased corpses. Graphs set with inspection off (the DirectX12 aliasing
		// diagnostics) keep their exact aliasing. The author's graph is untouched; we execute `executed`.
		_inspectable.Clear();
		var executed = graph;
		if (ResourceInspectionEnabled)
		{
			foreach (var resource in graph.Resources)
				if (resource.Kind is ResourceKind.Color or ResourceKind.Depth)
					_inspectable.Add(new InspectableResource(resource.Id, resource.Name, resource.Kind, resource.Format));
			executed = graph.WithDebugSampling(_inspectable.ConvertAll(r => r.Id));
		}

		// Derive once — the graph is immutable, so order/barriers/lifetimes/aliasing are frame-invariant. The
		// AliasPlan (the §8/M4 transient-memory partition) is the SAME backend-neutral product the Vulkan engine
		// consumes; this backend turns each bucket into a placed-resource heap shared by its members.
		var schedule = GraphSchedule.Derive(executed);
		_barrierPlan = BarrierPlan.Derive(schedule);
		_aliasPlan = AliasPlan.Derive(LifetimePlan.Derive(schedule));
		for (uint i = 0; i < BufferCount; i++)
			_backbufferState[i] = ResourceStates.Present;

		if (_aliasPlan.AliasableCount > _aliasPlan.Buckets.Length)
			Console.WriteLine($"[loom] DirectX12 transient aliasing: {_aliasPlan.AliasableCount} transients folded into {_aliasPlan.Buckets.Length} memory buckets.");

		_transients?.Dispose();
		// Build transients from `executed` (same resources/ids; the injected edges only affect derived state).
		_transients = new DirectX12Transients(_ctx, executed, _aliasPlan, BufferCount, _swapchain.Width, _swapchain.Height);
		_context.SetTransients(_transients);
		SetupPick(graph);
		SetupTiming();
	}

	// Allocate the GPU-timing query heap + per-slot readback buffers for this graph (M13): 2 timestamps (begin/end)
	// per pass, a sub-range per frame slot. Resized with the graph's pass count; mirrors SetupPick. WaitIdle()
	// already drained the GPU in SetGraph before this, so disposing the old heap/buffers is safe.
	private void SetupTiming()
	{
		if (_timingHeap.Handle is not null) { _timingHeap.Dispose(); _timingHeap = default; }
		foreach (ref var buffer in _timingReadback.AsSpan())
			if (buffer.Handle is not null) { buffer.Dispose(); buffer = default; }
		Array.Clear(_timingValid);
		ResetTimings();
		_timingPassCount = _barrierPlan!.Passes.Length;
		if (_timestampFrequency == 0 || _timingPassCount == 0)
			return;

		var perSlot = (uint)(2 * _timingPassCount);
		var desc = new QueryHeapDesc { Type = QueryHeapType.Timestamp, Count = perSlot * BufferCount, NodeMask = 0 };
		SilkMarshal.ThrowHResult(_ctx.Device.CreateQueryHeap(&desc, out _timingHeap));
		for (uint i = 0; i < BufferCount; i++)
			_timingReadback[i] = CreateReadbackBuffer((ulong)perSlot * sizeof(ulong));
	}

	// Wire up GPU→CPU picking for this graph: its first readback target (the object-id buffer, §7.4) gets a per-slot
	// READBACK buffer the executor copies into each frame. Mirrors the Vulkan backend (graph.ReadbackTargets[0]).
	private void SetupPick(RenderGraph graph)
	{
		foreach (ref var buffer in _pickReadback.AsSpan())
			if (buffer.Handle is not null) { buffer.Dispose(); buffer = default; }
		Array.Clear(_pickValid);
		_pickId = -1;
		if (graph.ReadbackTargets.Length == 0)
			return;

		var target = graph.ReadbackTargets[0];
		_pickId = target.Id;
		_pickFormat = target.Format;
		(_pickWidth, _pickHeight) = _transients!.Extent(_pickId);
		_pickRowPitch = Align(_pickWidth * BytesPerTexel(_pickFormat), 256); // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT
		_pickCpu = new float[_pickWidth * _pickHeight];
		for (uint i = 0; i < BufferCount; i++)
			_pickReadback[i] = CreateReadbackBuffer((ulong)_pickRowPitch * _pickHeight);
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

	/// <summary>
	/// The object id at picking-domain pixel (<paramref name="x"/>, <paramref name="y"/>) — read from the CPU copy
	/// of the graph's first readback target, a few frames stale (invisible for hover). 0 when out of bounds, before
	/// the first copy lands, or when the active graph declares no readback target. The DirectX12 twin of Vulkan's SamplePick.
	/// </summary>
	public override float SamplePick(int x, int y)
	{
		if (_pickId < 0 || (uint)x >= _pickWidth || (uint)y >= _pickHeight)
			return 0f;
		return _pickCpu[y * _pickWidth + x];
	}

	[DllImport("kernel32", SetLastError = true)]
	private static extern nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, nint lpName);

	[DllImport("kernel32", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32", SetLastError = true)]
	private static extern int CloseHandle(nint hObject);
}
