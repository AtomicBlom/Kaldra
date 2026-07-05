using System.Numerics;
using System.Runtime.Versioning;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Graph;

namespace Loom.Backend.WebGPU;

[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuEngine
{
	// Loom clears depth to the far plane; WebGPU's default depthClearValue matches, but pass it explicitly.
	private const float ClearDepth = 1.0f;

	public override void Render(FrameInputs inputs)
	{
		DrainShaderReloads();
		if (_transients is null || Graph is null)
			return;

		_elapsed = inputs.Time;
		_writer.Reset();
		_arena.Reset();
		_context.BeginFrame((float)inputs.DeltaSeconds, inputs.Time, inputs.DrawLists);

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

		var commands = _writer.Finish();
		WebGpuInterop.SubmitFrame(commands, _arena.Used);

		var errors = WebGpuInterop.TakeErrors();
		if (!string.IsNullOrEmpty(errors))
			Console.WriteLine($"[loom][webgpu] {errors}");
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
		// WebGPU tracks resource state itself, so the plan's transitions (pass.Transitions) are intentionally
		// not emitted — only the attachment load/store actions survive, into the render-pass descriptor.
		if (pass.Pass.Kind == PassKind.Compute)
		{
			_writer.BeginComputePass();
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
		var area = (Width: 0, Height: 0);
		foreach (var a in attachments)
		{
			ref var tex = ref _transients!.Texture(a.Resource.Id);
			if (a.State == ResourceState.ColorAttachment)
			{
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
				_writer.DepthAttachment(_transients!.ViewHandle(a.Resource.Id), ToLoadOp(a.Load), ToStoreOp(a.Store), ClearDepth, a.State == ResourceState.DepthReadOnly);

		_writer.SetViewport(0, 0, area.Width, area.Height);
		_writer.SetScissor(0, 0, area.Width, area.Height);

		// The technique pipelines this pass bakes must match the attachment formats declared above.
		_context.BeginPass(colorFormatIds[..colorCount], depthFormatId, depthReadOnly);
		pass.Pass.Record?.Invoke(_context);
		_writer.EndPass();
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
