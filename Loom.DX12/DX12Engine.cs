using System.Runtime.InteropServices;

using Loom.Core;
using Loom.Graph;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DX12;

/// <summary>
/// The DirectX 12 <see cref="IEngine"/> (Planning.md §2 "bake + record" layer, DX12 edition). Stands up the
/// device + swapchain and records command lists with fence-based frame pacing. This first slice mirrors how the
/// Vulkan backend started (M0/M1): it clears the backbuffer (and, next slice, draws a hand-baked triangle)
/// before the render-graph executor is ported. The graph-driven members therefore throw
/// <see cref="NotSupportedException"/> for now — they land in the following slice, when
/// <see cref="GraphSchedule"/>/<see cref="BarrierPlan"/>/<c>AliasPlan</c> are consumed here exactly as the
/// Vulkan engine consumes them.
/// </summary>
public sealed unsafe class DX12Engine : IEngine
{
	private const uint BufferCount = 2; // matches the Vulkan backend's 2 frames in flight

	private readonly DX12EngineSettings _settings;
	private readonly DX12Context _ctx;
	private readonly DX12Swapchain _swapchain;
	private readonly DX12Triangle _triangle;
	private readonly DX12TechniqueCache _techniques;
	private readonly DX12DescriptorRing _ring;
	private readonly DX12MeshStore _meshes;
	private readonly DX12RenderContext _context;
	private DX12Transients? _transients;

	private readonly ComPtr<ID3D12CommandAllocator>[] _allocators = new ComPtr<ID3D12CommandAllocator>[BufferCount];
	private ComPtr<ID3D12GraphicsCommandList> _commandList;
	private ComPtr<ID3D12Fence> _fence;
	private readonly ulong[] _frameFenceValues = new ulong[BufferCount];
	private ulong _fenceValue;
	private nint _fenceEvent;
	private int _debugErrors;
	private bool _disposed;

	// The active graph + its derived plans (null until SetGraph). Per-buffer backbuffer state is tracked because
	// D3D12 transition barriers need the resource's exact current state (see DX12StateMapping).
	private RenderGraph? _graph;
	private BarrierPlan? _barrierPlan;
	private AliasPlan? _aliasPlan;
	private readonly ResourceStates[] _backbufferState = new ResourceStates[BufferCount];
	// Aliased transients re-barriered this frame: a bucket member that shares memory needs one D3D12 aliasing
	// barrier before its first (re)activation each frame (the hand-off from whatever member last used the memory).
	private readonly HashSet<int> _aliasBarriered = [];

	private string? _capturePath;
	private ComPtr<ID3D12Resource> _readback;
	private ulong _readbackSize;

	public DX12Engine(nint hwnd, int width, int height, DX12EngineSettings settings)
	{
		_settings = settings;
		_ctx = new DX12Context(settings);
		_swapchain = new DX12Swapchain(_ctx, hwnd, width, height, BufferCount);

		for (uint i = 0; i < BufferCount; i++)
			SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandAllocator(CommandListType.Direct, out _allocators[i]));

		// Created in the recording state, then immediately closed — Render() resets it at the top of each frame.
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandList(
			0, CommandListType.Direct, _allocators[0], default(ComPtr<ID3D12PipelineState>), out _commandList));
		SilkMarshal.ThrowHResult(_commandList.Close());

		SilkMarshal.ThrowHResult(_ctx.Device.CreateFence(0, FenceFlags.None, out _fence));
		_fenceEvent = CreateEventW(0, 0, 0, 0);
		if (_fenceEvent == 0)
			throw new InvalidOperationException("CreateEvent failed for the DX12 frame-pacing fence.");

		_triangle = new DX12Triangle(_ctx, _swapchain.Format);
		_techniques = new DX12TechniqueCache(_ctx);
		_ring = new DX12DescriptorRing(_ctx, 256);
		_meshes = new DX12MeshStore(_ctx);
		_context = new DX12RenderContext(_ctx, _techniques, _ring, _meshes);
		for (uint i = 0; i < BufferCount; i++)
			_backbufferState[i] = ResourceStates.Present; // a fresh flip-model buffer starts presentable

		Console.WriteLine($"[loom] DX12 backend up on {_ctx.DeviceName} ({BufferCount} buffers, {(settings.VSync ? "vsync" : "no-vsync")}).");
	}

	public string BackendName => "DX12";
	public string DeviceName => _ctx.DeviceName;

	public void Render(FrameInputs inputs)
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
		var capturing = _capturePath is not null;
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
			WriteCapture(_capturePath!, rowPitch);
			_capturePath = null;
		}
	}

	// Graph-driven path: consume the neutral BarrierPlan derived in SetGraph. Per pass, emit its barriers, bind
	// its derived render target(s), run its record callback; then the final present transition. The DX12 mirror
	// of VulkanEngine's per-frame RecordGraph.
	private void RenderGraphFrame(FrameInputs inputs)
	{
		var index = _swapchain.CurrentIndex;
		WaitForFrame(index);

		SilkMarshal.ThrowHResult(_allocators[index].Reset());
		SilkMarshal.ThrowHResult(_commandList.Reset(_allocators[index], default(ComPtr<ID3D12PipelineState>)));

		// Bind the shader-visible heap for this command list, then reset its per-frame bump allocator.
		var heap = _ring.Heap.Handle;
		_commandList.SetDescriptorHeaps(1, &heap);
		_ring.Reset();
		_aliasBarriered.Clear(); // each aliased transient gets one aliasing barrier on its first use this frame
		_context.BeginFrame(_commandList, inputs, _settings.HdrOutput, index);

		foreach (var pass in _barrierPlan!.Passes)
		{
			foreach (var transition in pass.Transitions)
				EmitGraphTransition(index, transition);

			if (pass.Pass.Kind == PassKind.Compute)
				RecordComputePass(pass);
			else
				RecordRasterPass(index, pass);
		}

		// Optional capture: copy the present target before the final present transition (which then runs from
		// COPY_SOURCE, since EmitGraphTransition tracks the real state).
		var capturing = _capturePath is not null;
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
			WriteCapture(_capturePath!, rowPitch);
			_capturePath = null;
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
				if (attachment.Resource.IsBackbuffer)
				{
					rtv[colorCount] = _swapchain.RtvHandle(index);
					formats[colorCount] = _swapchain.Format;
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
			throw new NotSupportedException("DX12: a raster pass with no colour or depth target is not supported yet.");

		_commandList.OMSetRenderTargets((uint)colorCount, colorCount > 0 ? rtv : null, false, hasDepth ? &dsv : null);

		// Colour clears were emitted above (per attachment); clear depth to 1.0 here if the pass produces it.
		if (hasDepth && depthClear)
			_commandList.ClearDepthStencilView(dsv, ClearFlags.Depth, 1f, 0, 0u, (Box2D<int>*)null);

		var viewport = new Viewport { TopLeftX = 0, TopLeftY = 0, Width = width, Height = height, MinDepth = 0, MaxDepth = 1 };
		var scissor = new Box2D<int>(0, 0, (int)width, (int)height);
		_commandList.RSSetViewports(1, &viewport);
		_commandList.RSSetScissorRects(1, &scissor);

		_context.BeginPass(new ReadOnlySpan<DxgiFormat>(formats, colorCount), depthFormat, depthReadOnly);
		pass.Pass.Record?.Invoke(_context);
	}

	// A compute pass has no render targets and no render-target binding — its resource transitions (the storage
	// buffer → UNORDERED_ACCESS) were emitted before this; just record the dispatch. Empty attachments keep the
	// context's pass state stable (the DX12 mirror of VulkanEngine's compute branch in RecordPass).
	private void RecordComputePass(PassBarriers pass)
	{
		_context.BeginPass(ReadOnlySpan<DxgiFormat>.Empty, DxgiFormat.FormatUnknown, false);
		pass.Pass.Record?.Invoke(_context);
	}

	// Track the resource's real D3D12 state and emit a transition only when the required state differs (D3D12
	// barriers need the exact current state, unlike Vulkan's "Undefined" old layout — see DX12StateMapping).
	private void EmitGraphTransition(uint index, ResourceTransition transition)
	{
		var target = DX12StateMapping.For(transition.To);
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
		// contents undefined, which the pass's clear/full-write then initialises). The DX12 analogue of the
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
		Console.WriteLine($"[loom] DX12 captured {width}x{height} -> {path}");
	}

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

	public void Resize(int width, int height)
	{
		WaitIdle();
		_swapchain.Resize(width, height);
		Array.Clear(_frameFenceValues);

		// Transients sized to the backbuffer must follow the new extent (re-derive at the new size).
		if (_graph is not null)
		{
			_transients?.Dispose();
			_transients = new DX12Transients(_ctx, _graph, _aliasPlan!, BufferCount, _swapchain.Width, _swapchain.Height);
			_context.SetTransients(_transients);
			for (uint i = 0; i < BufferCount; i++)
				_backbufferState[i] = ResourceStates.Present;
		}
	}

	public void WaitIdle()
	{
		_fenceValue++;
		SilkMarshal.ThrowHResult(_ctx.GraphicsQueue.Signal(_fence, _fenceValue));
		if (_fence.GetCompletedValue() < _fenceValue)
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*)_fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;

		try { WaitIdle(); }
		catch { /* the device may already be lost; tear down regardless */ }

		_debugErrors += _ctx.DrainDebugMessages();
		if (_ctx.InfoQueue.Handle is not null)
			Console.WriteLine($"[loom] DX12 debug layer: {(_debugErrors == 0 ? "clean (no errors/corruption)" : $"{_debugErrors} error/corruption message(s)")}.");

		if (_fenceEvent != 0)
			CloseHandle(_fenceEvent);
		if (_readback.Handle is not null)
			_readback.Dispose();
		_transients?.Dispose();
		_meshes.Dispose();
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
	/// Register a technique under a logical name. This first executor step resolves the built-in HLSL the demo
	/// graphs use (the interim stand-in for the offline Slang→DXIL pipeline); a future slice resolves real
	/// shader bytecode by name exactly as the Vulkan backend resolves embedded SPIR-V.
	/// </summary>
	public void DefineTechnique(string name, TechniqueDescriptor descriptor)
	{
		if (!DX12Shaders.Builtins.TryGetValue(descriptor.Shader, out var builtin))
			throw new NotSupportedException($"DX12: no built-in HLSL for shader '{descriptor.Shader}' (the offline DXIL pipeline that resolves real shaders is a later slice).");

		_techniques.Register(name, new DX12TechniqueTemplate(
			builtin.Hlsl, descriptor.VertexEntry, descriptor.FragmentEntry,
			builtin.InputCount, descriptor.HasVertexInput, descriptor.HasFragmentPush, descriptor.LinearFilter,
			descriptor.IsCompute, descriptor.ParticleInput, descriptor.Blend));
	}

	/// <summary>
	/// Make <paramref name="graph"/> the active graph: derive its schedule + barrier plan (the backend-neutral
	/// Execute-layer products, identical to what the Vulkan engine consumes) and route <see cref="Render"/>
	/// through the executor. Transient-resource allocation (aliasing, the sample/blit path) lands next slice.
	/// </summary>
	public void SetGraph(RenderGraph graph)
	{
		ArgumentNullException.ThrowIfNull(graph);
		if (_graph is not null)
			WaitIdle();

		_graph = graph;
		// Derive once — the graph is immutable, so order/barriers/lifetimes/aliasing are frame-invariant. The
		// AliasPlan (the §8/M4 transient-memory partition) is the SAME backend-neutral product the Vulkan engine
		// consumes; this backend turns each bucket into a placed-resource heap shared by its members.
		var schedule = GraphSchedule.Derive(graph);
		_barrierPlan = BarrierPlan.Derive(schedule);
		_aliasPlan = AliasPlan.Derive(LifetimePlan.Derive(schedule));
		for (uint i = 0; i < BufferCount; i++)
			_backbufferState[i] = ResourceStates.Present;

		if (_aliasPlan.AliasableCount > _aliasPlan.Buckets.Length)
			Console.WriteLine($"[loom] DX12 transient aliasing: {_aliasPlan.AliasableCount} transients folded into {_aliasPlan.Buckets.Length} memory buckets.");

		_transients?.Dispose();
		_transients = new DX12Transients(_ctx, graph, _aliasPlan, BufferCount, _swapchain.Width, _swapchain.Height);
		_context.SetTransients(_transients);
	}

	private const string NextSlice = "DX12: this IEngine member lands in a later executor slice.";
	public Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) => _meshes.Create(vertices, indices);
	public void EnableImGui() => throw new NotSupportedException(NextSlice);
	public float SamplePick(int x, int y) => throw new NotSupportedException(NextSlice);

	/// <summary>Capture the next rendered frame to a PNG at <paramref name="path"/> (Planning.md §12 M10 verification).</summary>
	public void RequestCapture(string path) => _capturePath = path;

	[DllImport("kernel32", SetLastError = true)]
	private static extern nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, nint lpName);

	[DllImport("kernel32", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32", SetLastError = true)]
	private static extern int CloseHandle(nint hObject);
}
