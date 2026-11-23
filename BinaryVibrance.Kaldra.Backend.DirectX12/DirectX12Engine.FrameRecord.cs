using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.Maths;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace BinaryVibrance.Kaldra.Backend.DirectX12;

public sealed unsafe partial class DirectX12Engine
{
	// Graph-driven path: consume the neutral BarrierPlan derived in SetGraph. Per pass, emit its barriers, bind
	// its derived render target(s), run its record callback; then the final present transition. The DirectX12 mirror
	// of VulkanEngine's per-frame command-buffer recording.
	private void RenderGraphFrame(FrameInputs inputs)
	{
		DrainShaderReloads(); // pick up any hot-reloaded shaders before recording this frame

		var index = _presentTarget.CurrentIndex;
		WaitForFrame(index);
		var completed = _fence.GetCompletedValue();
		_meshes.CollectReleased(completed);
		_textures.CollectReleased(completed);

		// This slot's GPU work (a couple frames back) is now done, so its readback buffers are safe to map — repack
		// the copies they hold and hand them to StoreReadback for IEngine.Read (a few frames stale; invisible for hover).
		DownloadReadback(index);
		// Same story for the timing queries the slot resolved.
		DownloadTiming(index);

		SilkMarshal.ThrowHResult(_allocators[index].Reset());
		SilkMarshal.ThrowHResult(_commandList.Reset(_allocators[index], default(ComPtr<ID3D12PipelineState>)));

		// Bind the shader-visible heap for this command list and rewind this slot's own transient region — it must not
		// clobber the other in-flight frame's live descriptors. The heap keeps the command list so that growing
		// mid-frame can re-bind the replacement itself.
		_ring.BeginFrame(_commandList, index);
		_aliasBarriered.Clear(); // each aliased transient gets one aliasing barrier on its first use this frame
		_context.BeginFrame(_commandList, inputs, _presentTarget.HdrActive, index, _presentTarget.Width, _presentTarget.Height);

		// Everything the derived plan needs recording, in the order EngineBase fixes for every backend.
		var capturing = TakeCapturePath(CaptureSupported(), _presentTarget.Format) is not null;
		RecordFrame();

		SilkMarshal.ThrowHResult(_commandList.Close());
		// A mesh or texture created after the top of the frame (in a graph's prepare step) recorded its copy since the
		// drain; it goes to the queue ahead of the frame that draws it.
		_uploads.Flush();
		var list = (ID3D12CommandList*)_commandList.Handle;
		_ctx.GraphicsQueue.ExecuteCommandLists(1, &list);
		_presentTarget.Present(_settings.VSync);

		_fenceValue++;
		SilkMarshal.ThrowHResult(_ctx.GraphicsQueue.Signal(_fence, _fenceValue));
		_frameFenceValues[index] = _fenceValue;
		_debugErrors += _ctx.DrainDebugMessages();

		if (capturing)
		{
			WaitForFrame(index);
			WriteCapture(CapturePath!, _captureRowPitch);
			CapturePath = null;
		}
	}

	// The slot this frame is recording into. Read by the hooks rather than threaded through them: the shared record
	// spine deals in neutral terms, and the presentation index cannot advance mid-record (Present runs after Close).
	private uint RecordingIndex => _presentTarget.CurrentIndex;

	// Row pitch of the capture copy, kept for the WriteCapture that runs after the frame's fence signals.
	private uint _captureRowPitch;

	// Whether this frame can collect timestamps at all — the heap only exists on a queue that reports a frequency.
	private bool TimingActive => GpuTimingEnabled && _timingHeap.Handle is not null;

	// Each slot owns a sub-range of the one timestamp heap.
	private uint TimingBase => (uint)(2 * _timingPassCount) * RecordingIndex;

	// A persistently mapped copy of each Constants and Storage buffer per buffered frame
	protected override int ShadowSlot => (int)RecordingIndex;

	protected override int ShadowSlotCount => (int)BufferCount;

	protected override void WriteConstantsShadow(int id, ReadOnlySpan<byte> bytes) =>
		_transients!.WriteConstants(RecordingIndex, id, bytes);

	protected override void WriteStorageShadow(int id, ReadOnlySpan<byte> bytes) =>
		_transients!.WriteStorage(RecordingIndex, id, bytes);

	protected override void EmitTransitions(ReadOnlySpan<ResourceTransition> transitions) =>
		EmitGraphTransitions(RecordingIndex, transitions);

	protected override void RecordRasterPass(PassBarriers pass) => RecordRasterPass(RecordingIndex, pass);

	protected override void RecordReadback() => RecordReadback(RecordingIndex);

	// Timestamp queries use EndQuery only — there is no BeginQuery for them.
	protected override void WritePassBeginTimestamp(int passIndex)
	{
		if (TimingActive)
			_commandList.EndQuery(_timingHeap, QueryType.Timestamp, TimingBase + (uint)(2 * passIndex));
	}

	protected override void WritePassEndTimestamp(int passIndex)
	{
		if (TimingActive)
			_commandList.EndQuery(_timingHeap, QueryType.Timestamp, TimingBase + (uint)(2 * passIndex + 1));
	}

	// Resolve this slot's timestamps into its readback buffer, to be read after the slot's next fence.
	protected override void EndFrameTimestamps()
	{
		if (!TimingActive)
			return;
		var index = RecordingIndex;
		_commandList.ResolveQueryData(_timingHeap, QueryType.Timestamp, TimingBase, (uint)(2 * _timingPassCount), _timingReadback[index], 0);
		_timingValid[index] = true;
	}

	// Copy the present target out before the final transitions, which then run from COPY_SOURCE because
	// EmitGraphTransitions tracks the real state — so unlike Vulkan the capture does not have to replace them.
	protected override void FinishFrame()
	{
		var index = RecordingIndex;
		if (CapturePath is not null)
		{
			var backbuffer = _presentTarget.BackBuffer(index);
			if (_backbufferState[index] != ResourceStates.CopySource)
			{
				// Queued and flushed on the spot: the copy below reads the backbuffer immediately, so this one cannot
				// wait to be batched with the final transitions that follow it.
				QueueTransition(backbuffer, _backbufferState[index], ResourceStates.CopySource);
				FlushTransitions();
				_backbufferState[index] = ResourceStates.CopySource;
			}
			_captureRowPitch = CopyBackbufferToReadback(backbuffer);
		}

		EmitGraphTransitions(index, Barriers.FinalTransitions.AsSpan());
	}

	// Bind a raster pass's colour + depth target(s) from its derived AttachmentActions, apply load actions, then
	// run the callback. Colour targets bind RTVs; a depth target binds the DSV and bakes the depth test into the
	// pass's pipelines (via BeginPass). Targets may be the backbuffer (swapchain RTV) or graph transients.
	private void RecordRasterPass(uint index, PassBarriers pass)
	{
		var rtv = stackalloc CpuDescriptorHandle[GraphLimits.MaxColorAttachmentsPerPass];
		var formats = stackalloc DxgiFormat[GraphLimits.MaxColorAttachmentsPerPass];
		var colorCount = 0;
		uint width = 0, height = 0;

		CpuDescriptorHandle dsv = default;
		var hasDepth = false;
		float? depthClear = null;
		var depthReadOnly = false;
		var depthFormat = DxgiFormat.FormatUnknown;
		var samples = 1;   // all of a pass's attachments share a domain, and so its multisample count
		Span<float> clearFloats = stackalloc float[4];

		foreach (var attachment in pass.Attachments)
		{
			var id = attachment.Resource.Id;
			samples = attachment.Resource.Domain.Samples;
			if (attachment.State == ResourceState.ColorAttachment)
			{
				// Defensive: derivation already rejects a pass over the limit, and the stack arrays below are sized to
				// it. Kept so a change to that rule cannot silently overrun them.
				if (colorCount >= GraphLimits.MaxColorAttachmentsPerPass)
					throw new NotSupportedException($"DirectX12: a pass declares more than {GraphLimits.MaxColorAttachmentsPerPass} colour attachments, which schedule derivation should have rejected.");
				if (attachment.Resource.IsBackbuffer)
				{
					rtv[colorCount] = _presentTarget.RtvHandle(index);
					formats[colorCount] = _presentTarget.RenderTargetFormat; // the sRGB RTV format — the PSO must match the actual view
					if (width == 0)
						(width, height) = (_presentTarget.Width, _presentTarget.Height);
				}
				else
				{
					rtv[colorCount] = _transients!.View(index, id);
					formats[colorCount] = _transients.Format(index, id);
					// The first attachment supplies the extent, as on the other backends. Derivation now guarantees they
					// all agree, so this only matters for reading the same as they do rather than for the result.
					if (width == 0)
						(width, height) = _transients.Extent(id);
				}
				if (attachment.Load == LoadAction.Clear && attachment.ClearValue is { } clearColor)
				{
					clearFloats[0] = clearColor.X;
					clearFloats[1] = clearColor.Y;
					clearFloats[2] = clearColor.Z;
					clearFloats[3] = clearColor.W;
					fixed (float* c = clearFloats)
						_commandList.ClearRenderTargetView(rtv[colorCount], c, 0u, (Box2D<int>*)null);
				}
				colorCount++;
			}
			else // DepthAttachment or DepthReadOnly
			{
				dsv = _transients!.View(index, id);
				depthFormat = _transients.Format(index, id);
				depthReadOnly = attachment.State == ResourceState.DepthReadOnly;
				depthClear = attachment.Load == LoadAction.Clear ? attachment.DepthClear : null;
				if (width == 0)
					(width, height) = _transients.Extent(id);
				hasDepth = true;
			}
		}

		// Defensive, as above: an attachment-less raster pass is rejected at derive time now, on every backend rather
		// than only this one.
		if (colorCount == 0 && !hasDepth)
			throw new NotSupportedException("DirectX12: a raster pass with no colour or depth target, which schedule derivation should have rejected.");

		_commandList.OMSetRenderTargets((uint)colorCount, colorCount > 0 ? rtv : null, false, hasDepth ? &dsv : null);

		// Colour clears were emitted above (per attachment); clear depth here if the pass produces it. The value must
		// match the resource's optimized-clear hint (DirectX12Transients.BuildClear) or the debug layer warns on
		// every clear — both come from the DepthHandle's declared ClearDepth.
		if (hasDepth && depthClear is { } depthClearValue)
			_commandList.ClearDepthStencilView(dsv, ClearFlags.Depth, depthClearValue, 0, 0u, (Box2D<int>*)null);

		// Negative-height viewport (origin at the bottom): turns D3D12's (1-ndc.y) NDC→screen map into Vulkan's
		// (ndc.y+1). Paired with the Vulkan-style flipped projection (DirectX12RenderContext.ClipSpace) this renders
		// geometry upright AND makes every fullscreen pass map uv→screen exactly like Vulkan — so the shared
		// shaders need no per-backend Y handling (see ClipSpace). The scissor stays in pixel space (not flipped).
		var viewport = new Viewport { TopLeftX = 0, TopLeftY = height, Width = width, Height = -(float)height, MinDepth = 0, MaxDepth = 1 };
		var scissor = new Box2D<int>(0, 0, (int)width, (int)height);
		_commandList.RSSetViewports(1, &viewport);
		_commandList.RSSetScissorRects(1, &scissor);

		Span<int> uniformIds = stackalloc int[DirectX12RenderContext.MaxUniformBuffers];
		var uniformCount = pass.Pass.ResourceIdsWith(AccessKind.ReadUniform, uniformIds);
		Span<int> storageIds = stackalloc int[DirectX12RenderContext.MaxStorageBuffers];
		var storageCount = pass.Pass.ResourceIdsWith(AccessKind.ReadStorage, storageIds);
		_context.BeginPass(pass.Pass, new ReadOnlySpan<DxgiFormat>(formats, colorCount), depthFormat, depthReadOnly, uniformIds[..uniformCount], storageIds[..storageCount], samples);

		// Hand the context this pass's render-target handles + extent so a draw-list group-opacity layer can rebind
		// the pass scope after detouring through an offscreen target.
		_context.SetColorScope(new ReadOnlySpan<CpuDescriptorHandle>(rtv, colorCount), width, height);

		pass.Pass.Record?.Invoke(_context);
	}

	// A compute pass has no render targets and no render-target binding — its resource transitions (the storage
	// buffer → UNORDERED_ACCESS) were emitted before this; just record the dispatch. Empty attachments keep the
	// context's pass state stable (the DirectX12 mirror of VulkanEngine's compute branch in RecordPass).
	protected override void RecordComputePass(PassBarriers pass)
	{
		Span<int> uniformIds = stackalloc int[DirectX12RenderContext.MaxUniformBuffers];
		var uniformCount = pass.Pass.ResourceIdsWith(AccessKind.ReadUniform, uniformIds);
		Span<int> storageIds = stackalloc int[DirectX12RenderContext.MaxStorageBuffers];
		var storageCount = pass.Pass.ResourceIdsWith(AccessKind.ReadStorage, storageIds);
		_context.BeginPass(pass.Pass, ReadOnlySpan<DxgiFormat>.Empty, DxgiFormat.FormatUnknown, false, uniformIds[..uniformCount], storageIds[..storageCount]);
		pass.Pass.Record?.Invoke(_context);
	}

	// The label scopes annotate the one command list this backend records every frame into.
	protected override void BeginLabel(string name) => _ctx.BeginDebugLabel(_commandList, name);

	protected override void EndLabel() => _ctx.EndDebugLabel(_commandList);
}
