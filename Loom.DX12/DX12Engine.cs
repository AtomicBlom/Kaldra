using System.Runtime.InteropServices;

using Loom.Core;
using Loom.Graph;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

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

	private readonly ComPtr<ID3D12CommandAllocator>[] _allocators = new ComPtr<ID3D12CommandAllocator>[BufferCount];
	private ComPtr<ID3D12GraphicsCommandList> _commandList;
	private ComPtr<ID3D12Fence> _fence;
	private readonly ulong[] _frameFenceValues = new ulong[BufferCount];
	private ulong _fenceValue;
	private nint _fenceEvent;
	private int _debugErrors;
	private bool _disposed;

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

		Console.WriteLine($"[loom] DX12 backend up on {_ctx.DeviceName} ({BufferCount} buffers, {(settings.VSync ? "vsync" : "no-vsync")}).");
	}

	public string BackendName => "DX12";
	public string DeviceName => _ctx.DeviceName;

	public void Render(FrameInputs inputs)
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

	/// <summary>
	/// Record (into the open command list) the copy of the just-rendered backbuffer into the readback buffer,
	/// leaving the backbuffer in PRESENT state. Returns the row pitch of the readback layout.
	/// </summary>
	private uint RecordCapture(ComPtr<ID3D12Resource> backbuffer)
	{
		var width = _swapchain.Width;
		var height = _swapchain.Height;
		var rowPitch = Align(width * 4, 256); // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT
		EnsureReadback((ulong)rowPitch * height);

		Transition(backbuffer, ResourceStates.RenderTarget, ResourceStates.CopySource);

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

		Transition(backbuffer, ResourceStates.CopySource, ResourceStates.Present);
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
		_triangle.Dispose();
		_fence.Dispose();
		_commandList.Dispose();
		for (uint i = 0; i < BufferCount; i++)
			_allocators[i].Dispose();
		_swapchain.Dispose();
		_ctx.Dispose();
	}

	// --- Graph-driven surface: lands in the next slice (see the type remarks). ---
	private const string NextSlice = "The DX12 backend's render-graph execution lands in the next slice; this first slice proves the seam with a cleared window + hand-baked triangle.";
	public void DefineTechnique(string name, TechniqueDescriptor descriptor) => throw new NotSupportedException(NextSlice);
	public Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) => throw new NotSupportedException(NextSlice);
	public void SetGraph(RenderGraph graph) => throw new NotSupportedException(NextSlice);
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
