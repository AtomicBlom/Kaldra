using System.Diagnostics;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.Maths;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.Backend.DirectX12;

public sealed unsafe partial class DirectX12Engine
{
	// Graph-driven path: consume the neutral BarrierPlan derived in SetGraph. Per pass, emit its barriers, bind
	// its derived render target(s), run its record callback; then the final present transition. The DirectX12 mirror
	// of VulkanEngine's per-frame RecordGraph.
	private void RenderGraphFrame(FrameInputs inputs)
	{
		DrainShaderReloads(); // pick up any hot-reloaded shaders before recording this frame

		var index = _presentTarget.CurrentIndex;
		WaitForFrame(index);

		// This slot's GPU work (a couple frames back) is now done, so its readback buffers are safe to map — repack
		// the copies they hold and hand them to StoreReadback for IEngine.Read (a few frames stale; invisible for hover).
		DownloadReadback(index);
		// Same story for the timing queries the slot resolved.
		DownloadTiming(index);

		SilkMarshal.ThrowHResult(_allocators[index].Reset());
		SilkMarshal.ThrowHResult(_commandList.Reset(_allocators[index], default(ComPtr<ID3D12PipelineState>)));

		// Bind the shader-visible heap for this command list, then reset its per-frame bump allocator.
		var heap = _ring.Heap.Handle;
		_commandList.SetDescriptorHeaps(1, &heap);
		_ring.Reset(index); // this slot's own region — must not clobber the other in-flight frame's live descriptors
		_aliasBarriered.Clear(); // each aliased transient gets one aliasing barrier on its first use this frame
		_context.BeginFrame(_commandList, inputs, _settings.HdrOutput, index, _presentTarget.Width, _presentTarget.Height);

		// Flush each parameter buffer's CPU shadow into this slot's copy before any pass reads it (write-on-change:
		// the last IEngine.Write persists, re-uploaded each frame so the slot the GPU reads is always current).
		foreach (var (id, bytes) in ConstantsShadows)
			_transients!.WriteConstants(index, id, bytes);
		// Same for the app-writable storage buffers (IEngine.Write span).
		foreach (var (id, bytes) in StorageShadows)
			_transients!.WriteStorage(index, id, bytes);

		// GPU timing: bracket each pass's own work (after its pre-barriers) with a begin/end timestamp into this
		// slot's sub-range of the heap. Timestamp queries use EndQuery only (no BeginQuery).
		var timing = GpuTimingEnabled && _timingHeap.Handle is not null;
		var timingBase = (uint)(2 * _timingPassCount) * index;
		var passIndex = 0;

		foreach (var pass in Barriers.Passes)
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

		// GPU→CPU readback: copy each readback target into its slot's buffer (no-op if the graph declares none).
		RecordReadback(index);

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
			var backbuffer = _presentTarget.BackBuffer(index);
			if (_backbufferState[index] != ResourceStates.CopySource)
			{
				Transition(backbuffer, _backbufferState[index], ResourceStates.CopySource);
				_backbufferState[index] = ResourceStates.CopySource;
			}
			rowPitch = CopyBackbufferToReadback(backbuffer);
		}

		foreach (var transition in Barriers.FinalTransitions)
			EmitGraphTransition(index, transition);

		SilkMarshal.ThrowHResult(_commandList.Close());
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
		var colorCount = 0;
		uint width = 0, height = 0;

		CpuDescriptorHandle dsv = default;
		var hasDepth = false;
		var depthClear = false;
		var depthReadOnly = false;
		var depthFormat = DxgiFormat.FormatUnknown;
		Span<float> clearFloats = stackalloc float[4];

		foreach (var attachment in pass.Attachments)
		{
			var id = attachment.Resource.Id;
			if (attachment.State == ResourceState.ColorAttachment)
			{
				if (colorCount >= 8)
					throw new NotSupportedException("DirectX12: a pass declares more than 8 colour attachments; D3D12 caps simultaneous render targets at 8 (D3D12_SIMULTANEOUS_RENDER_TARGET_COUNT).");
				if (attachment.Resource.IsBackbuffer)
				{
					rtv[colorCount] = _presentTarget.RtvHandle(index);
					formats[colorCount] = _presentTarget.RenderTargetFormat; // the sRGB RTV format — the PSO must match the actual view
					width = _presentTarget.Width;
					height = _presentTarget.Height;
				}
				else
				{
					rtv[colorCount] = _transients!.View(index, id);
					formats[colorCount] = _transients.Format(index, id);
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

		Span<int> uniformIds = stackalloc int[DirectX12RenderContext.MaxUniformBuffers];
		var uniformCount = CollectUniformIds(pass.Pass, uniformIds);
		Span<int> storageIds = stackalloc int[DirectX12RenderContext.MaxStorageBuffers];
		var storageCount = CollectStorageIds(pass.Pass, storageIds);
		_context.BeginPass(new ReadOnlySpan<DxgiFormat>(formats, colorCount), depthFormat, depthReadOnly, uniformIds[..uniformCount], storageIds[..storageCount]);

		// Hand the context this pass's render-target handles + extent so a draw-list group-opacity layer can rebind
		// the pass scope after detouring through an offscreen target.
		_context.SetColorScope(new ReadOnlySpan<CpuDescriptorHandle>(rtv, colorCount), width, height);

		pass.Pass.Record?.Invoke(_context);
	}

	// A compute pass has no render targets and no render-target binding — its resource transitions (the storage
	// buffer → UNORDERED_ACCESS) were emitted before this; just record the dispatch. Empty attachments keep the
	// context's pass state stable (the DirectX12 mirror of VulkanEngine's compute branch in RecordPass).
	private void RecordComputePass(PassBarriers pass)
	{
		Span<int> uniformIds = stackalloc int[DirectX12RenderContext.MaxUniformBuffers];
		var uniformCount = CollectUniformIds(pass.Pass, uniformIds);
		Span<int> storageIds = stackalloc int[DirectX12RenderContext.MaxStorageBuffers];
		var storageCount = CollectStorageIds(pass.Pass, storageIds);
		_context.BeginPass(ReadOnlySpan<DxgiFormat>.Empty, DxgiFormat.FormatUnknown, false, uniformIds[..uniformCount], storageIds[..storageCount]);
		pass.Pass.Record?.Invoke(_context);
	}

	// Fill `ids` with the app-writable parameter buffers a pass .Reads (its ReadUniform edges, in .Reads order) —
	// they bind to the technique's uniform CBVs b1..; returns the count.
	private static int CollectUniformIds(GraphPass pass, Span<int> ids)
	{
		var n = 0;
		foreach (var access in pass.Accesses)
			if (access.Kind == AccessKind.ReadUniform && n < ids.Length)
				ids[n++] = access.Resource.Id;
		return n;
	}

	// Fill `ids` with the app-writable storage buffers a pass .Reads (its ReadStorage edges, in .Reads order) —
	// they bind to the technique's read-only structured-buffer root SRVs; returns the count.
	private static int CollectStorageIds(GraphPass pass, Span<int> ids)
	{
		var n = 0;
		foreach (var access in pass.Accesses)
			if (access.Kind == AccessKind.ReadStorage && n < ids.Length)
				ids[n++] = access.Resource.Id;
		return n;
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
		for (var i = 0; i < Barriers.TrailingLabelCloses; i++)
			_ctx.EndDebugLabel(_commandList);
	}
}
