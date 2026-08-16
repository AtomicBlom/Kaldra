using System.Numerics;
using System.Runtime.Versioning;

using Loom.Core.Diagnostics;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Graph;

namespace Loom.Backend.WebGPU;

[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuEngine
{
	protected override void RenderFrame(FrameInputs inputs)
	{
		DrainShaderReloads();
		ServicePendingUploads();
		// Same answer as the other backends: rendering without a graph is a host sequencing bug, and silently
		// returning turns it into a canvas that never updates with nothing to explain why.
		if (Graph is null || _transients is null)
			throw new InvalidOperationException("No active graph; call SetGraph before Render.");

		_elapsed = (float)inputs.Time;
		_writer.Reset();
		_arena.Reset();
		_uniformRing.BeginFrame();
		_instanceRing.BeginFrame();
		_storageRing.BeginFrame();
		_listVertexRing.BeginFrame();
		_listIndexRing.BeginFrame();
		_context.BeginFrame(inputs.DeltaSeconds, inputs.Time);

		// Flush each app-writable buffer's CPU shadow into the frame's upload arena. These become queue-level
		// WriteBuffer ops, which the replayer applies before every pass — so the last IEngine.Write persists and
		// the buffer the GPU reads this frame is current (the write is ordered before this frame's submit, and
		// after the previous frame's reads, by WebGPU's queue ordering).
		foreach (var (id, bytes) in ConstantsShadows)
			EmitBufferUpload(id, bytes);
		foreach (var (id, bytes) in StorageShadows)
			EmitBufferUpload(id, bytes);

		foreach (var pass in Barriers.Passes)
			RecordPass(pass);

		// A requested capture copies the backbuffer into a map-read buffer at the tail of this frame's stream.
		RecordCaptureIfRequested();
		// Every g.Readback target (e.g. the picking id buffer) copies into its own map-read buffer the same way.
		RecordGraphReadbacks();

		var commands = _writer.Finish();
		WebGpuInterop.SubmitFrame(commands, _arena.Used);

		// Drive the async maps (started the frame after their copy submits, then polled to completion).
		ServiceCapture();
		ServiceGraphReadbacks();

		var errors = WebGpuInterop.TakeErrors();
		if (!string.IsNullOrEmpty(errors))
			LoomLog.Error($"[webgpu] {errors}");
	}

	private void EmitBufferUpload(int id, ReadOnlySpan<byte> bytes)
	{
		var buffer = _transients!.BufferHandle(id);
		if (buffer < 0 || bytes.Length == 0)
			return;
		var offset = _arena.Append(bytes);
		_writer.WriteBufferFromArena(buffer, dstOffset: 0, offset, bytes.Length);
	}

	private void RecordPass(PassBarriers pass)
	{
		// The app-writable parameter and storage buffers this pass .Reads — bound to the technique's uniform and
		// storage-buffer slots at draw/dispatch time.
		Span<int> constantsIds = stackalloc int[WebGpuRenderContext.MaxUniformBuffers];
		var constantsCount = CollectReads(pass.Pass, AccessKind.ReadUniform, constantsIds);
		Span<int> storageIds = stackalloc int[WebGpuRenderContext.MaxStorageBuffers];
		var storageCount = CollectReads(pass.Pass, AccessKind.ReadStorage, storageIds);

		// WebGPU tracks resource state itself, so the plan's transitions (pass.Transitions) are intentionally
		// not emitted — only the attachment load/store actions survive, into the render-pass descriptor.
		if (pass.Pass.Kind == PassKind.Compute)
		{
			_writer.BeginComputePass();
			// No colour/depth targets; keeps the bake key stable and supplies the pass's read buffers.
			_context.BeginPass(pass.Pass, [], -1, false, [], 0, 0, constantsIds[..constantsCount], storageIds[..storageCount]);
			pass.Pass.Record?.Invoke(_context);
			_writer.EndPass();
			return;
		}

		var attachments = pass.Attachments;
		var colorCount = 0;
		var hasDepth = false;
		var depthReadOnly = false;
		var depthFormatId = -1;
		Span<int> colorFormatIds = stackalloc int[attachments.Length];
		Span<int> colorViews = stackalloc int[attachments.Length];
		var area = (Width: 0, Height: 0);
		foreach (var a in attachments)
		{
			ref var tex = ref _transients!.Texture(a.Resource.Id);
			if (a.State == ResourceState.ColorAttachment)
			{
				colorViews[colorCount] = _transients!.ViewHandle(a.Resource.Id);
				colorFormatIds[colorCount++] = WebGpuFormatMapping.ToWebGpu(tex.Format);
			}
			else
			{
				hasDepth = true;
				depthFormatId = WebGpuFormatMapping.ToWebGpu(tex.Format);
				depthReadOnly = a.State == ResourceState.DepthReadOnly;
			}
			if (area.Width == 0)
				area = (tex.Width, tex.Height);
		}

		_writer.BeginRenderPass(colorCount, hasDepth);
		foreach (var a in attachments)
			if (a.State == ResourceState.ColorAttachment)
				_writer.ColorAttachment(_transients!.ViewHandle(a.Resource.Id), ToLoadOp(a.Load), ToStoreOp(a.Store), a.ClearValue ?? Vector4.Zero);
		foreach (var a in attachments)
			if (a.State != ResourceState.ColorAttachment)
				// The derived clear — 1 for a conventional depth target, 0 for a reversed-Z one. Ignored unless the
				// load op is Clear, in which case derivation always supplied a value.
				_writer.DepthAttachment(_transients!.ViewHandle(a.Resource.Id), ToLoadOp(a.Load), ToStoreOp(a.Store), a.DepthClear ?? 1f, a.State == ResourceState.DepthReadOnly);

		_writer.SetViewport(0, 0, area.Width, area.Height);
		_writer.SetScissor(0, 0, area.Width, area.Height);

		// The technique pipelines this pass bakes must match the attachment formats declared above. The colour
		// attachment views + pass area are handed through too, so a draw list's group-opacity layers can suspend and
		// resume this exact scope below the graph seam.
		_context.BeginPass(pass.Pass, colorFormatIds[..colorCount], depthFormatId, depthReadOnly, colorViews[..colorCount], area.Width, area.Height, constantsIds[..constantsCount], storageIds[..storageCount]);
		pass.Pass.Record?.Invoke(_context);
		_writer.EndPass();
	}

	// Fill `ids` with the resource ids a pass reads under `kind` (its ReadUniform / ReadStorage edges, in .Reads
	// order); returns the count. They bind to the technique's uniform / storage-buffer slots in this order.
	private static int CollectReads(GraphPass pass, AccessKind kind, Span<int> ids)
	{
		var n = 0;
		foreach (var access in pass.Accesses)
			if (access.Kind == kind)
				ids[n++] = access.Resource.Id;
		return n;
	}

	private static int ToLoadOp(LoadAction load) => load switch
	{
		LoadAction.Clear => WebGpuCommandWriter.LoadClear,
		LoadAction.Load => WebGpuCommandWriter.LoadLoad,
		_ => WebGpuCommandWriter.LoadDontCare,
	};

	private static int ToStoreOp(StoreAction store) =>
		store == StoreAction.Store ? WebGpuCommandWriter.StoreStore : WebGpuCommandWriter.StoreDiscard;
}
