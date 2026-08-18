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

		// Everything the derived plan needs recording, in the order EngineBase fixes for every backend — which is also
		// what moved this backend's readback copies ahead of its capture copy, matching the other two.
		RecordFrame();

		var commands = _writer.Finish();
		WebGpuInterop.SubmitFrame(commands, _arena.Used);

		// Drive the async maps (started the frame after their copy submits, then polled to completion).
		ServiceCapture();
		ServiceGraphReadbacks();

		var errors = WebGpuInterop.TakeErrors();
		if (!string.IsNullOrEmpty(errors))
			LoomLog.Error($"[webgpu] {errors}");
	}

	// The shadows become queue-level WriteBuffer ops, which the replayer applies before every pass — so the last
	// IEngine.Write persists and what the GPU reads this frame is current (the write is ordered before this frame's
	// submit, and after the previous frame's reads, by WebGPU's queue ordering).
	protected override void WriteConstantsShadow(int id, ReadOnlySpan<byte> bytes) => EmitBufferUpload(id, bytes);

	protected override void WriteStorageShadow(int id, ReadOnlySpan<byte> bytes) => EmitBufferUpload(id, bytes);

	// Deliberately empty, not omitted: WebGPU tracks resource state itself, so the plan's transitions genuinely have
	// nothing to translate into here. Only the attachment load/store actions survive, into the render-pass descriptor.
	protected override void EmitTransitions(ReadOnlySpan<ResourceTransition> transitions) { }

	// Every g.Readback target (e.g. the picking id buffer) copies into its own map-read buffer.
	protected override void RecordReadback() => RecordGraphReadbacks();

	// A requested capture copies the backbuffer into a map-read buffer at the tail of this frame's stream. No final
	// transitions to emit — WebGPU tracks resource state itself, and the canvas is presented by the browser.
	protected override void FinishFrame() => RecordCaptureIfRequested();

	private void EmitBufferUpload(int id, ReadOnlySpan<byte> bytes)
	{
		var buffer = _transients!.BufferHandle(id);
		if (buffer < 0 || bytes.Length == 0)
			return;
		var offset = _arena.Append(bytes);
		_writer.WriteBufferFromArena(buffer, dstOffset: 0, offset, bytes.Length);
	}

	protected override void RecordComputePass(PassBarriers pass)
	{
		// The app-writable parameter and storage buffers this pass .Reads — bound to the technique's uniform and
		// storage-buffer slots at dispatch time.
		Span<int> constantsIds = stackalloc int[WebGpuRenderContext.MaxUniformBuffers];
		var constantsCount = pass.Pass.ResourceIdsWith(AccessKind.ReadUniform, constantsIds);
		Span<int> storageIds = stackalloc int[WebGpuRenderContext.MaxStorageBuffers];
		var storageCount = pass.Pass.ResourceIdsWith(AccessKind.ReadStorage, storageIds);

		_writer.BeginComputePass();
		// No colour/depth targets; keeps the bake key stable and supplies the pass's read buffers.
		_context.BeginPass(pass.Pass, [], -1, false, [], 0, 0, constantsIds[..constantsCount], storageIds[..storageCount]);
		pass.Pass.Record?.Invoke(_context);
		_writer.EndPass();
	}

	protected override void RecordRasterPass(PassBarriers pass)
	{
		// The app-writable parameter and storage buffers this pass .Reads — bound to the technique's uniform and
		// storage-buffer slots at draw time.
		Span<int> constantsIds = stackalloc int[WebGpuRenderContext.MaxUniformBuffers];
		var constantsCount = pass.Pass.ResourceIdsWith(AccessKind.ReadUniform, constantsIds);
		Span<int> storageIds = stackalloc int[WebGpuRenderContext.MaxStorageBuffers];
		var storageCount = pass.Pass.ResourceIdsWith(AccessKind.ReadStorage, storageIds);

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

	// Deliberately empty, not omitted: the derived label nesting is driven here exactly as on the other backends, and
	// dropping the annotation is this backend's choice rather than a gap in the loop. Carrying it would mean new
	// command-stream opcodes for the encoder's pushDebugGroup/popDebugGroup and a protocol-version bump; the scopes
	// are diagnostic-only, so nothing about the frame changes without them.
	protected override void BeginLabel(string name) { }

	protected override void EndLabel() { }

	private static int ToLoadOp(LoadAction load) => load switch
	{
		LoadAction.Clear => WebGpuCommandWriter.LoadClear,
		LoadAction.Load => WebGpuCommandWriter.LoadLoad,
		_ => WebGpuCommandWriter.LoadDontCare,
	};

	private static int ToStoreOp(StoreAction store) =>
		store == StoreAction.Store ? WebGpuCommandWriter.StoreStore : WebGpuCommandWriter.StoreDiscard;
}
