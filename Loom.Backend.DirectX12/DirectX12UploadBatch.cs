using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Loom.Backend.DirectX12;

/// <summary>
/// One command list that every resource upload records into, executed once and never waited on.
/// <para>
/// Each <c>CreateTexture</c> used to reset its own allocator and list, execute it alone, and block on a fence until
/// the GPU had finished — a full round trip per texture. Loading a scene's worth of assets meant dozens of them;
/// streaming one texture mid-gameplay meant stalling the frame that asked for it.
/// </para>
/// <para>
/// <b>Why no wait is needed.</b> Uploads are drained at the top of a frame, so this list is executed on the graphics
/// queue before that frame's own list, and command lists on one D3D12 queue complete in submission order. Each
/// upload ends by transitioning its resource to <c>PixelShaderResource</c>, so the frame's draws see a finished
/// texture in the right state without the CPU waiting for anything.
/// </para>
/// <para>
/// The fence exists only to know when the staging buffers may be released, and it is checked one batch late — by the
/// time a frame's uploads are flushed, the previous frame's copies have long finished, so the wait costs nothing in
/// the steady state while remaining correct if they somehow have not.
/// </para>
/// </summary>
internal sealed unsafe class DirectX12UploadBatch : IDisposable
{
	private readonly DirectX12Context _ctx;
	private ComPtr<ID3D12CommandAllocator> _allocator;
	private ComPtr<ID3D12GraphicsCommandList> _cmd;
	private ComPtr<ID3D12Fence> _fence;
	private readonly nint _fenceEvent;
	private ulong _fenceValue;
	private ulong _submittedValue;

	private bool _recording;
	private bool _submitted;

	// Staging resources the open batch will need until its copies have run. They move to _inFlight on execute and
	// are released once the fence for that execute is known signalled.
	private readonly List<ComPtr<ID3D12Resource>> _staging = [];
	private readonly List<ComPtr<ID3D12Resource>> _inFlight = [];

	public DirectX12UploadBatch(DirectX12Context ctx)
	{
		_ctx = ctx;
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandAllocator(CommandListType.Direct, out _allocator));
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandList(0, CommandListType.Direct, _allocator, default(ComPtr<ID3D12PipelineState>), out _cmd));
		SilkMarshal.ThrowHResult(_cmd.Close());
		SilkMarshal.ThrowHResult(_ctx.Device.CreateFence(0, FenceFlags.None, out _fence));
		_fenceEvent = CreateEventW(0, 0, 0, 0);
		if (_fenceEvent == 0)
			throw new InvalidOperationException("DirectX12: CreateEvent failed for the upload-batch fence.");
	}

	/// <summary>The command list to record copies and transitions into, opening the batch if it is not already open.
	/// Every upload in one drain shares it.</summary>
	public ComPtr<ID3D12GraphicsCommandList> Recorder()
	{
		if (_recording)
			return _cmd;

		ReclaimStaging(); // the allocator cannot be reset while a submission still reads what it recorded
		SilkMarshal.ThrowHResult(_allocator.Reset());
		SilkMarshal.ThrowHResult(_cmd.Reset(_allocator, default(ComPtr<ID3D12PipelineState>)));
		_recording = true;
		return _cmd;
	}

	/// <summary>Hand the batch a staging resource to release once its copies have run.</summary>
	public void Retire(ComPtr<ID3D12Resource> staging) => _staging.Add(staging);

	/// <summary>Execute whatever has been recorded. No-op when nothing has. Does not wait.</summary>
	public void Flush()
	{
		if (!_recording)
			return;

		SilkMarshal.ThrowHResult(_cmd.Close());
		_recording = false;

		var list = (ID3D12CommandList*)_cmd.Handle;
		_ctx.GraphicsQueue.ExecuteCommandLists(1, &list);

		_fenceValue++;
		SilkMarshal.ThrowHResult(_ctx.GraphicsQueue.Signal(_fence, _fenceValue));
		_submittedValue = _fenceValue;
		_submitted = true;

		_inFlight.AddRange(_staging);
		_staging.Clear();
	}

	// Release the staging of the last executed batch, waiting for its fence if it somehow has not signalled yet.
	private void ReclaimStaging()
	{
		if (!_submitted)
			return;

		if (_fence.GetCompletedValue() < _submittedValue)
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_submittedValue, (void*)_fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}
		_submitted = false;

		foreach (var staging in _inFlight)
			staging.Dispose();
		_inFlight.Clear();
	}

	public void Dispose()
	{
		// Anything still recorded but unexecuted has no GPU work behind it; drop it and release what it staged.
		if (_recording)
		{
			_cmd.Close();
			_recording = false;
			foreach (var staging in _staging)
				staging.Dispose();
			_staging.Clear();
		}
		ReclaimStaging();
		if (_fenceEvent != 0)
			CloseHandle(_fenceEvent);
		_cmd.Dispose();
		_allocator.Dispose();
		_fence.Dispose();
	}

	[DllImport("kernel32", SetLastError = true)]
	private static extern nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, nint lpName);

	[DllImport("kernel32", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32", SetLastError = true)]
	private static extern int CloseHandle(nint hObject);
}
