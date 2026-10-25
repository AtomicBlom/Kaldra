using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;

namespace Loom.Backend.WebGPU;

[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuRenderContext
{
	/// <summary>
	/// Record a flattened draw list (the UI compositor's batched output): stage its vertex/index streams into the
	/// per-frame list rings, then walk its commands, each a scissor + texture + index range drawn with the shared
	/// per-list push (its last slot rewritten per command with the command's tag). <c>BeginLayer</c>/<c>EndLayer</c>
	/// markers become an offscreen render-to-texture group-opacity composite below the graph seam: the faded subtree
	/// renders into a fresh transparent layer at full alpha, then one KIND_IMAGE quad multiplies it by the group
	/// alpha back into the resumed parent — so the whole subtree fades once (overlaps stay a single colour).
	/// </summary>
	public void DrawList<TVertex>(string technique, DrawListIdentity identity, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged
	{
		if (commands.IsEmpty || vertices.IsEmpty || indices.IsEmpty || textures.IsEmpty)
			return;

		var baked = Bake(technique);
		var template = baked.Template;
		var writer = _engine.Writer;

		// Stage this frame's geometry into the dynamic rings (bump-allocated: several draw lists in one frame append at
		// successive offsets rather than overwrite each other).
		//
		// `identity` is accepted and ignored here, so a stable stream re-uploads every frame as it always has. That is
		// correct, only not yet optimal — the desktop backends keep a stream's buffers and skip the copy while its
		// revision holds. Doing the same here means writing a persistent buffer through the command stream's
		// OP_WRITE_BUFFER, whose ordering relative to an open pass encoder needs establishing, and the browser parity
		// leg currently runs no UI-heavy demo that would show a mistake. Not worth shipping unverified.
		var vtxOffset = _engine.ListVertexRing.Reserve(MemoryMarshal.AsBytes(vertices), MemoryMarshal.AsBytes(vertices).Length);
		var idxOffset = _engine.ListIndexRing.Reserve(MemoryMarshal.AsBytes(indices), MemoryMarshal.AsBytes(indices).Length);

		writer.SetRenderPipeline(baked.Pipeline);
		writer.SetVertexBuffer(0, _engine.ListVertexRing.Buffer, vtxOffset);
		writer.SetIndexBuffer(_engine.ListIndexRing.Buffer, IndexFormatUint16, idxOffset);

		var sampler = _engine.Samplers.Get(template.LinearFilter, template.Address);

		// Group-opacity layers are only meaningful on an overlay-vertex list (the only producer of the markers), a
		// colour scope to suspend + resume, and a surface-pixel→clip push to size the composite quad. Without all
		// three, markers fold to no-op zero-index draws (matching the desktop backends).
		var layersSupported = Unsafe.SizeOf<TVertex>() == Unsafe.SizeOf<OverlayVertex>()
			&& _colorCount > 0 && _scopeWidth > 0 && _scopeHeight > 0 && push.Length >= 2 && push[0] != 0 && push[1] != 0;
		var overlay = layersSupported ? MemoryMarshal.Cast<TVertex, OverlayVertex>(vertices) : default;
		if (layersSupported)
			_engine.LayerTargets.Configure(_scopeWidth, _scopeHeight, _colorFormatIds[0]);

		// The list's own coordinate space, recovered from the push that maps it to clip. A layer's bounds arrive in
		// these units; the walker scales them to target pixels.
		var surfaceWidth = layersSupported ? 2f / push[0] : _scopeWidth;
		var surfaceHeight = layersSupported ? 2f / push[1] : _scopeHeight;

		// The command stream decodes to steps: the layer nesting and its cap, the scissor, and the per-layer tint and
		// bounds are the walker's, and what remains here is the command-stream writing each step needs.
		var walker = new DrawListWalker(commands, textures, overlay, layersSupported,
			_scopeWidth, _scopeHeight, surfaceWidth, surfaceHeight);

		while (walker.MoveNext())
		{
			if (walker.Step == DrawListStep.BeginLayer)
			{
				// Suspend the current scope; render the faded subtree into a fresh transparent offscreen target at full
				// alpha (so overlapping children blend against each other, not a faded backdrop).
				writer.EndPass();
				BeginColorScope([_engine.LayerTargets.Acquire(walker.Depth).View], WebGpuCommandWriter.LoadClear);
				RebindList(writer, baked.Pipeline, vtxOffset, idxOffset);
				// A WebGPU render pass covers its whole attachment — there is no render area, so unlike the desktop
				// backends the clear cannot be narrowed to the group (walker.LayerArea is ignored here). The composite
				// still is.
				continue;
			}

			if (walker.Step == DrawListStep.EndLayer)
			{
				// Resolve the layer, then resume the parent scope (LoadOp.Load — keep what it already holds).
				var closed = _engine.LayerTargets.Acquire(walker.Depth).View;
				writer.EndPass();
				ResumeParentScope(walker.Depth - 1);
				RebindList(writer, baked.Pipeline, vtxOffset, idxOffset);
				CompositeLayer(baked, sampler, closed, walker.Tint, push, walker.Bounds, surfaceWidth, surfaceHeight);
				// Restore the list's geometry for the following commands — the composite bound its own quad buffer.
				writer.SetVertexBuffer(0, _engine.ListVertexRing.Buffer, vtxOffset);
				continue;
			}

			// Bound unconditionally, so walker.TextureChanged has nothing to save here: the push block carries this
			// command's tag at a fresh dynamic offset, so the bind group has to be set either way — and the group itself
			// is already cached by the resources it names, which is what the desktop backends' memoisation buys them.
			BindCommand(baked, sampler, _engine.TextureView(walker.TextureId), push, walker.Tag);

			var (clipX, clipY, clipW, clipH) = walker.Scissor;
			writer.SetScissor(clipX, clipY, clipW, clipH);
			writer.DrawIndexed(walker.IndexCount, 1, walker.IndexOffset, walker.VertexOffset, 0);
		}

		if (walker.NestingExceeded)
			DrawListGeometry.WarnLayerNestingExceeded(ref _warnedLayerNesting, walker.DeepestNesting);

		// Defensive: a well-formed list balances its markers, but never leave a layer scope open — the engine ends
		// exactly one scope after the pass, which must be the parent (screen) scope.
		while (walker.TryUnwind(out var abandoned))
		{
			writer.EndPass();
			ResumeParentScope(abandoned - 1);
			RebindList(writer, baked.Pipeline, vtxOffset, idxOffset);
		}
	}

	// Index-buffer format id understood by the shim's OP_SET_INDEX_BUFFER: 0 = uint16.
	private const int IndexFormatUint16 = 0;

	// Set once a list has nested layers past the cap, so the diagnostic is one line per context rather than per frame.
	private bool _warnedLayerNesting;

	// Re-set the pipeline + list geometry after a fresh render pass (WebGPU render passes start with no bound state).
	private void RebindList(WebGpuCommandWriter writer, int pipeline, int vtxOffset, int idxOffset)
	{
		writer.SetRenderPipeline(pipeline);
		writer.SetVertexBuffer(0, _engine.ListVertexRing.Buffer, vtxOffset);
		writer.SetIndexBuffer(_engine.ListIndexRing.Buffer, IndexFormatUint16, idxOffset);
		if (_scopeWidth > 0)
			writer.SetViewport(0, 0, _scopeWidth, _scopeHeight);
	}

	// Begin a render scope over the given colour attachment view(s) at the pass render area — used to open a layer's
	// offscreen target (LoadOp.Clear, transparent-black) and to resume the suspended parent scope (LoadOp.Load).
	private void BeginColorScope(ReadOnlySpan<int> views, int loadOp)
	{
		var writer = _engine.Writer;
		writer.BeginRenderPass(views.Length, hasDepth: false);
		foreach (var view in views)
			writer.ColorAttachment(view, loadOp, WebGpuCommandWriter.StoreStore, Vector4.Zero);
	}

	// Resume whichever scope sits under the just-ended layer: the layer at <paramref name="parentDepth"/> if the ended
	// one was nested, else (a negative depth) the pass's own colour attachments — both reloaded (LoadOp.Load) so the
	// composite blends onto existing contents.
	private void ResumeParentScope(int parentDepth)
	{
		if (parentDepth >= 0)
		{
			BeginColorScope([_engine.LayerTargets.Acquire(parentDepth).View], WebGpuCommandWriter.LoadLoad);
			return;
		}
		Span<int> views = stackalloc int[_colorCount];
		for (var i = 0; i < _colorCount; i++)
			views[i] = _colorViews[i];
		BeginColorScope(views, WebGpuCommandWriter.LoadLoad);
	}

	// Bind one draw command: the command's texture + the technique sampler + the shared push block with its last used
	// slot rewritten to this command's tag (the draw-list contract). The push rides the uniform ring at a per-command
	// dynamic offset, so the bind group stays cached per (texture view) across the frame.
	private void BindCommand(WebGpuTechnique baked, int sampler, int textureView, ReadOnlySpan<float> push, uint tag)
	{
		var bindings = baked.Template.Bindings;
		Span<byte> pushBytes = stackalloc byte[bindings.PushByteSize];
		WritePushBlock(pushBytes, push, tag);
		var pushOffset = _engine.UniformRing.Reserve(pushBytes, bindings.PushByteSize);

		Span<int> entries = stackalloc int[bindings.Entries.Count * 5];
		var e = 0;
		AddEntry(entries, ref e, bindings.SampledTextureBindings[0], EntryView, textureView, 0, 0);
		AddEntry(entries, ref e, bindings.SampledSamplerBindings[0], EntrySampler, sampler, 0, 0);
		AddEntry(entries, ref e, bindings.PushBinding, EntryBuffer, _engine.UniformRing.Buffer, 0, bindings.PushByteSize);

		var bindGroup = GetOrCreateBindGroup(baked.Template.BindGroupLayout, entries[..e]);
		Span<int> dyn = [pushOffset];
		_engine.Writer.SetBindGroup(0, bindGroup, dyn);
	}

	// Composite a resolved layer back into the (now-resumed) parent: one KIND_IMAGE quad over the group that samples
	// the layer and multiplies by the premultiplied group-alpha tint, so the whole subtree fades once.
	private void CompositeLayer(WebGpuTechnique baked, int sampler, int layerView, Half4 tint, ReadOnlySpan<float> push,
		(float X0, float Y0, float X1, float Y1) bounds, float surfaceWidth, float surfaceHeight)
	{
		Span<OverlayVertex> quad = stackalloc OverlayVertex[DrawListGeometry.CompositeQuadVertexCount];
		DrawListGeometry.WriteCompositeQuad(bounds, surfaceWidth, surfaceHeight, tint, quad);

		var quadOffset = _engine.ListVertexRing.Reserve(MemoryMarshal.AsBytes(quad), MemoryMarshal.AsBytes(quad).Length);
		_engine.Writer.SetVertexBuffer(0, _engine.ListVertexRing.Buffer, quadOffset);

		// The composite push mirrors the list push but with isHdr forced off (slot 5) and the KIND_IMAGE tag.
		var bindings = baked.Template.Bindings;
		Span<byte> pushBytes = stackalloc byte[bindings.PushByteSize];
		WritePushBlock(pushBytes, push, DrawListGeometry.KindImage, forceSdr: true);
		var pushOffset = _engine.UniformRing.Reserve(pushBytes, bindings.PushByteSize);

		Span<int> entries = stackalloc int[bindings.Entries.Count * 5];
		var e = 0;
		AddEntry(entries, ref e, bindings.SampledTextureBindings[0], EntryView, layerView, 0, 0);
		AddEntry(entries, ref e, bindings.SampledSamplerBindings[0], EntrySampler, sampler, 0, 0);
		AddEntry(entries, ref e, bindings.PushBinding, EntryBuffer, _engine.UniformRing.Buffer, 0, bindings.PushByteSize);
		var bindGroup = GetOrCreateBindGroup(baked.Template.BindGroupLayout, entries[..e]);
		Span<int> dyn = [pushOffset];
		_engine.Writer.SetBindGroup(0, bindGroup, dyn);

		_engine.Writer.SetScissor(0, 0, _scopeWidth, _scopeHeight);
		_engine.Writer.Draw(6, 1, 0, 0);
	}

	// Fill the push block: the shared per-list floats, then the per-command tag in the slot right after them, padded to
	// the block size. forceSdr zeroes the isHdr slot for the layer composite.
	private static void WritePushBlock(Span<byte> dst, ReadOnlySpan<float> push, uint tag, bool forceSdr = false)
	{
		dst.Clear();
		var floats = MemoryMarshal.Cast<byte, float>(dst);
		var pushLen = Math.Min(push.Length, DrawListGeometry.MaxListPushFloats);
		for (var i = 0; i < pushLen; i++)
			floats[i] = push[i];
		if (forceSdr && pushLen > DrawListGeometry.HdrPushSlot)
			floats[DrawListGeometry.HdrPushSlot] = 0f;
		MemoryMarshal.Write(dst[(pushLen * sizeof(float))..], in tag);
	}
}
