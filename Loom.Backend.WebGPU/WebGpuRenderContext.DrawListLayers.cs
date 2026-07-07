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
	// UI group-opacity layer markers (UIPrimKind, kept in sync with SUIsei): a BeginLayer/EndLayer pair carries no
	// indices and never reaches the fragment shader — the backend turns it into an offscreen render scope and a
	// composite. KindImage is the shader branch the composite blit uses (sample the layer, tint by group alpha).
	private const uint TagBeginLayer = 5;
	private const uint TagEndLayer = 6;
	private const uint KindImage = 4;

	// The offscreen layer stack: each entry is a suspended scope's resolved view + its premultiplied group-alpha tint.
	private readonly List<(int View, Half4 Tint)> _layerStack = [];

	/// <summary>
	/// Record a flattened draw list (the UI compositor's batched output): stage its vertex/index streams into the
	/// per-frame list rings, then walk its commands, each a scissor + texture + index range drawn with the shared
	/// per-list push (its last slot rewritten per command with the command's tag). <c>BeginLayer</c>/<c>EndLayer</c>
	/// markers become an offscreen render-to-texture group-opacity composite below the graph seam: the faded subtree
	/// renders into a fresh transparent layer at full alpha, then one KIND_IMAGE quad multiplies it by the group
	/// alpha back into the resumed parent — so the whole subtree fades once (overlaps stay a single colour).
	/// </summary>
	public void DrawList<TVertex>(string technique, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged
	{
		if (commands.IsEmpty || vertices.IsEmpty || indices.IsEmpty || textures.IsEmpty)
			return;

		var baked = Bake(technique);
		var template = baked.Template;
		var writer = _engine.Writer;

		// Stage this frame's geometry into the dynamic rings (bump-allocated: several draw lists in one frame append at
		// successive offsets rather than overwrite each other).
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
		_layerStack.Clear();

		foreach (var c in commands)
		{
			if (layersSupported && c.Tag == TagBeginLayer)
			{
				// Suspend the current scope; render the faded subtree into a fresh transparent offscreen target at full
				// alpha (so overlapping children blend against each other, not a faded backdrop).
				writer.EndPass();
				var target = _engine.LayerTargets.Acquire(_layerStack.Count);
				BeginColorScope([target.View], WebGpuCommandWriter.LoadClear);
				RebindList(writer, baked.Pipeline, vtxOffset, idxOffset);
				_layerStack.Add((target.View, overlay[c.VertexOffset].Color));
				continue;
			}
			if (layersSupported && c.Tag == TagEndLayer)
			{
				if (_layerStack.Count == 0)
					continue;
				var layer = _layerStack[^1];
				_layerStack.RemoveAt(_layerStack.Count - 1);

				// Resolve the layer, then resume the parent scope (LoadOp.Load — keep what it already holds).
				writer.EndPass();
				ResumeParentScope();
				RebindList(writer, baked.Pipeline, vtxOffset, idxOffset);
				CompositeLayer(baked, sampler, layer.View, layer.Tint, push);
				// Restore the list's geometry for the following commands (the composite bound its own quad buffer).
				writer.SetVertexBuffer(0, _engine.ListVertexRing.Buffer, vtxOffset);
				continue;
			}

			var clip = c.ClipRect;
			var minX = (int)MathF.Max(0, clip.X);
			var minY = (int)MathF.Max(0, clip.Y);
			var maxX = (int)MathF.Min(_scopeWidth, clip.X + clip.Width);
			var maxY = (int)MathF.Min(_scopeHeight, clip.Y + clip.Height);
			if (maxX <= minX || maxY <= minY)
				continue;

			var texId = textures[Math.Clamp(c.TextureIndex, 0, textures.Length - 1)].Id;
			BindCommand(baked, sampler, _engine.TextureView(texId), push, c.Tag);

			writer.SetScissor(minX, minY, maxX - minX, maxY - minY);
			writer.DrawIndexed(c.IndexCount, 1, c.IndexOffset, c.VertexOffset, 0);
		}

		// Defensive: a well-formed list balances its markers, but never leave a layer scope open — the engine ends
		// exactly one scope after the pass, which must be the parent (screen) scope.
		while (_layerStack.Count > 0)
		{
			_layerStack.RemoveAt(_layerStack.Count - 1);
			writer.EndPass();
			ResumeParentScope();
			RebindList(writer, baked.Pipeline, vtxOffset, idxOffset);
		}
	}

	// Index-buffer format id understood by the shim's OP_SET_INDEX_BUFFER: 0 = uint16.
	private const int IndexFormatUint16 = 0;

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

	// Resume whichever scope sits under the just-ended layer: the parent layer if nested, else the pass's own colour
	// attachments — both reloaded (LoadOp.Load) so the composite blends onto existing contents.
	private void ResumeParentScope()
	{
		if (_layerStack.Count > 0)
		{
			BeginColorScope([_layerStack[^1].View], WebGpuCommandWriter.LoadLoad);
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

	// Composite a resolved layer back into the (now-resumed) parent: one full-surface KIND_IMAGE quad that samples the
	// layer and multiplies by the premultiplied group-alpha tint, so the whole subtree fades once. The HDR paper-white
	// lift is forced off here — the layer's primitives already lifted, and the lift commutes with the premultiplied
	// alpha-over, so re-lifting would double it (the CPU rasteriser is the reference for this).
	private void CompositeLayer(WebGpuTechnique baked, int sampler, int layerView, Half4 tint, ReadOnlySpan<float> push)
	{
		var coordX = 2f / push[0];
		var coordY = 2f / push[1];
		var custom = default(Vector4);
		Span<OverlayVertex> quad =
		[
			new(new Vector2(0, 0), new Vector2(0, 0), tint, custom),
			new(new Vector2(coordX, 0), new Vector2(1, 0), tint, custom),
			new(new Vector2(coordX, coordY), new Vector2(1, 1), tint, custom),
			new(new Vector2(0, 0), new Vector2(0, 0), tint, custom),
			new(new Vector2(coordX, coordY), new Vector2(1, 1), tint, custom),
			new(new Vector2(0, coordY), new Vector2(0, 1), tint, custom),
		];
		var quadOffset = _engine.ListVertexRing.Reserve(MemoryMarshal.AsBytes(quad), MemoryMarshal.AsBytes(quad).Length);
		_engine.Writer.SetVertexBuffer(0, _engine.ListVertexRing.Buffer, quadOffset);

		// The composite push mirrors the list push but with isHdr forced off (slot 5) and the KIND_IMAGE tag.
		var bindings = baked.Template.Bindings;
		Span<byte> pushBytes = stackalloc byte[bindings.PushByteSize];
		WritePushBlock(pushBytes, push, KindImage, forceSdr: true);
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
	// the block size. forceSdr zeroes the isHdr slot (slot 5) for the layer composite.
	private static void WritePushBlock(Span<byte> dst, ReadOnlySpan<float> push, uint tag, bool forceSdr = false)
	{
		dst.Clear();
		var floats = MemoryMarshal.Cast<byte, float>(dst);
		var pushLen = Math.Min(push.Length, floats.Length - 1);
		for (var i = 0; i < pushLen; i++)
			floats[i] = push[i];
		if (forceSdr && pushLen > 5)
			floats[5] = 0f;
		MemoryMarshal.Write(dst[(pushLen * sizeof(float))..], in tag);
	}
}
