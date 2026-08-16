using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;
using Silk.NET.Vulkan;

namespace Loom.Backend.Vulkan;

internal sealed unsafe partial class VulkanRenderContext
{
	// UI group-opacity layer markers (UIPrimKind, kept in sync with SUIsei): a BeginLayer/EndLayer pair carries no
	// indices and never reaches the fragment shader — the backend turns it into an offscreen render scope and a
	// composite. KIND_IMAGE is the shader branch the composite blit uses (sample the layer, tint by group alpha).
	private const uint TagBeginLayer = 5;
	private const uint TagEndLayer = 6;
	private const uint KindImage = 4;

	public void DrawList<TVertex>(string technique, DrawListIdentity identity, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged
	{
		if (commands.IsEmpty || vertices.IsEmpty || indices.IsEmpty || textures.IsEmpty)
			return;
		var baked = Bake(technique);
		var template = _registry[technique];

		// A stream with a stable identity keeps its own buffers and re-uploads only when its revision moves; anything
		// else goes into the dynamic rings (bump-allocated: several transient lists in one frame append at
		// successive offsets rather than overwrite each other).
		Silk.NET.Vulkan.Buffer vtxBuffer, idxBuffer;
		ulong vtxOffset = 0, idxOffset = 0;
		if (identity.IsStable)
		{
			(vtxBuffer, idxBuffer) = _listStore.Resolve(identity.Stream, _frameSlot, identity.Revision, _frameIndex,
				MemoryMarshal.AsBytes(vertices), MemoryMarshal.AsBytes(indices));
		}
		else
		{
			(vtxBuffer, vtxOffset) = _listVtx.Upload(_frameSlot, MemoryMarshal.AsBytes(vertices));
			(idxBuffer, idxOffset) = _listIdx.Upload(_frameSlot, MemoryMarshal.AsBytes(indices));
		}

		BindPipeline(PipelineBindPoint.Graphics, baked.Pipeline);
		// The list brings its own geometry, so whatever mesh the store thought was bound no longer is.
		_meshes.ResetBindings();
		_vk.CmdBindVertexBuffers(_cmd, 0, 1, in vtxBuffer, in vtxOffset);
		_vk.CmdBindIndexBuffer(_cmd, idxBuffer, idxOffset, IndexType.Uint16);

		// Use the current pass scope dimensions for the viewport; fall back to the framebuffer extent for
		// passes that don't set a scope (e.g. direct screen passes without a domain).
		var layerArea = _scopeArea.Width != 0 ? _scopeArea : _fbExtent;
		Viewport viewport = new() { X = 0, Y = 0, Width = layerArea.Width, Height = layerArea.Height, MinDepth = 0, MaxDepth = 1 };
		_vk.CmdSetViewport(_cmd, 0, 1, in viewport);

		// The shared per-list push fills the block's first slots; each command rewrites the slot right after it with
		// its tag (the draw-list contract). Pad to the layout's fixed range so the shader always reads defined memory.
		const int blockFloats = VulkanTechnique.ListPushSize / sizeof(float);
		var block = stackalloc float[blockFloats];
		var pushLen = Math.Min(push.Length, blockFloats - 1);
		for (var i = 0; i < pushLen; i++)
			block[i] = push[i];

		var sampler = template.Address switch
		{
			SamplerAddress.Clamp => template.LinearFilter ? _linearSampler : _nearestSampler,
			SamplerAddress.Repeat => template.LinearFilter ? _repeatLinearSampler : _repeatNearestSampler,
			SamplerAddress.MirrorRepeat => template.LinearFilter ? _mirrorLinearSampler : _mirrorNearestSampler,
			SamplerAddress.ClampToBorder => template.LinearFilter ? _borderLinearSampler : _borderNearestSampler,
			SamplerAddress.MirrorClampToEdge => template.LinearFilter ? _mirrorEdgeLinearSampler : _mirrorEdgeNearestSampler,
			_ => throw new VulkanException($"SamplerAddress '{template.Address}' is not mapped to a sampler.")
		};

		// Group-opacity layers are only meaningful on an overlay-vertex list (the only producer of the markers); a
		// scope to suspend; and a surface-pixel→clip push to size the composite quad. Without all three, markers fold
		// to no-ops (a zero-index draw).
		var layersSupported = Unsafe.SizeOf<TVertex>() == Unsafe.SizeOf<OverlayVertex>()
			&& _scopeColorCount > 0 && _colorCount > 0 && push.Length >= 2 && push[0] != 0 && push[1] != 0;
		var overlay = layersSupported ? MemoryMarshal.Cast<TVertex, OverlayVertex>(vertices) : default;
		if (layersSupported)
			_layerTargets.Configure(layerArea, _colorFormats[0]);
		_layerStack.Clear();

		// The list's own coordinate space, recovered from the push that maps it to clip, and the scale from it to
		// framebuffer pixels. A layer's bounds arrive in surface pixels; its render area is needed in framebuffer ones.
		var surfaceWidth = layersSupported ? 2f / push[0] : layerArea.Width;
		var surfaceHeight = layersSupported ? 2f / push[1] : layerArea.Height;
		var toFramebufferX = layerArea.Width / surfaceWidth;
		var toFramebufferY = layerArea.Height / surfaceHeight;

		// The texture the currently bound descriptor set describes. -1 = nothing bound yet.
		var listTextureId = -1;

		foreach (var c in commands)
		{
			if (layersSupported && c.Tag == TagBeginLayer)
			{
				// Suspend the current scope, render the faded subtree into a fresh transparent offscreen target at
				// full alpha (so overlapping children blend against each other, not a faded backdrop).
				_vk.CmdEndRendering(_cmd);
				var target = _layerTargets.Acquire(_frameSlot, _layerStack.Count);
				TransitionImage(target.Image, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
					PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
					PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);

				// The group's own bounds, not the whole surface. A layer costs two render-pass breaks and a clear, and
				// on a tile-based GPU those are priced by area — a checkbox fading in should not clear and store a
				// screen's worth of tiles. The marker carries the bounds as its ClipRect precisely so a backend can
				// use them.
				var bounds = LayerBounds(c.ClipRect, surfaceWidth, surfaceHeight);
				BeginColorScope(target.View, AttachmentLoadOp.Clear, FramebufferRect(bounds, toFramebufferX, toFramebufferY, layerArea));
				_vk.CmdSetViewport(_cmd, 0, 1, in viewport);
				_layerStack.Add((target.Image, target.View, overlay[c.VertexOffset].Color, bounds));
				continue;
			}
			if (layersSupported && c.Tag == TagEndLayer)
			{
				if (_layerStack.Count == 0)
					continue;
				var layer = _layerStack[^1];
				_layerStack.RemoveAt(_layerStack.Count - 1);

				// Resolve the layer target, then resume the parent scope (LoadOp.Load — keep what it already holds).
				_vk.CmdEndRendering(_cmd);
				TransitionImage(layer.Image, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
					PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
					PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
				// The parent resumes over its whole area — commands after this one draw anywhere — but only the
				// layer's own bounds are composited back into it.
				if (_layerStack.Count > 0)
					BeginColorScope(_layerStack[^1].View, AttachmentLoadOp.Load, new Rect2D(default, layerArea));
				else
					BeginColorScope(_scopeColorViews.AsSpan(0, _scopeColorCount), AttachmentLoadOp.Load, new Rect2D(default, layerArea));
				_vk.CmdSetViewport(_cmd, 0, 1, in viewport);

				CompositeLayer(baked, layer.View, layer.Tint, sampler, block, pushLen, layer.Bounds, surfaceWidth, surfaceHeight, layerArea);

				// Restore the list's geometry for the following commands (the composite bound its own quad buffer),
				// and forget the memoised texture set — the composite replaced it with the layer's own.
				_vk.CmdBindVertexBuffers(_cmd, 0, 1, in vtxBuffer, in vtxOffset);
				_vk.CmdBindIndexBuffer(_cmd, idxBuffer, idxOffset, IndexType.Uint16);
				listTextureId = -1;
				continue;
			}

			var clip = c.ClipRect;
			var minX = MathF.Max(0, clip.X);
			var minY = MathF.Max(0, clip.Y);
			var maxX = MathF.Min(layerArea.Width, clip.X + clip.Width);
			var maxY = MathF.Min(layerArea.Height, clip.Y + clip.Height);
			if (maxX <= minX || maxY <= minY)
				continue;

			*(uint*)(block + pushLen) = c.Tag;
			_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)VulkanTechnique.ListPushSize, block);

			var texId = textures[Math.Clamp(c.TextureIndex, 0, textures.Length - 1)].Id;
			// A UI list is mostly one texture — the glyph atlas — for run after run of commands, and the set holds
			// nothing else, so a run of same-texture commands can keep the set the first of them bound. Only a change
			// of texture costs an allocate + update + bind.
			if (texId != listTextureId)
			{
				// An id offset by DebugTextureBase is an inspectable graph resource (the resource viewer): sample this
				// frame's live transient image instead of a loaded texture-store entry.
				var view = texId >= InspectableResource.DebugTextureBase
					? _resources.Get(_frameSlot, texId - InspectableResource.DebugTextureBase).View
					: _textures.View(texId);
				var set = _descriptors.Allocate(baked.DescriptorSetLayout);
				var imageInfo = new DescriptorImageInfo { Sampler = sampler, ImageView = view, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
				var write = new WriteDescriptorSet
				{
					SType = StructureType.WriteDescriptorSet,
					DstSet = set,
					DstBinding = 0,
					DescriptorCount = 1,
					DescriptorType = DescriptorType.CombinedImageSampler,
					PImageInfo = &imageInfo,
				};
				_vk.UpdateDescriptorSets(_ctx.Device, 1, in write, 0, null);
				// No dynamic offsets: a draw-list technique's layout is one combined image sampler plus a push block,
				// so it declares no uniform buffer — the only descriptor type Loom binds dynamically. One that did
				// would fail validation here for supplying none, which is the loud failure that should follow.
				_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);
				listTextureId = texId;
			}

			Rect2D scissor = new(new Offset2D((int)minX, (int)minY), new Extent2D((uint)(maxX - minX), (uint)(maxY - minY)));
			_vk.CmdSetScissor(_cmd, 0, 1, in scissor);

			_vk.CmdDrawIndexed(_cmd, (uint)c.IndexCount, 1, (uint)c.IndexOffset, c.VertexOffset, 0);
		}

		// Defensive: a well-formed list balances its markers, but never leave a layer scope open — the engine ends
		// exactly one scope after the pass, which must be the parent (screen) scope.
		while (_layerStack.Count > 0)
		{
			_layerStack.RemoveAt(_layerStack.Count - 1);
			_vk.CmdEndRendering(_cmd);
			BeginColorScope(((ReadOnlySpan<ImageView>)_scopeColorViews)[.._scopeColorCount], AttachmentLoadOp.Load, new Rect2D(default, layerArea));
		}
	}

	// A layer's bounds in the list's own coordinate space, clamped to the surface and grown by a pixel so a rounded
	// edge cannot clip the antialiased fringe of whatever sits against the group's boundary. Degenerate bounds fall
	// back to the whole surface, which is what this path did before it looked at the rect at all.
	private static (float X0, float Y0, float X1, float Y1) LayerBounds(Rect clip, float surfaceWidth, float surfaceHeight)
	{
		const float Slop = 1f;
		var x0 = MathF.Max(0f, clip.X - Slop);
		var y0 = MathF.Max(0f, clip.Y - Slop);
		var x1 = MathF.Min(surfaceWidth, clip.X + clip.Width + Slop);
		var y1 = MathF.Min(surfaceHeight, clip.Y + clip.Height + Slop);
		return x1 > x0 && y1 > y0 ? (x0, y0, x1, y1) : (0f, 0f, surfaceWidth, surfaceHeight);
	}

	// The same bounds as a framebuffer-pixel render area: floor the origin and ceil the far edge so the area never
	// covers less than the bounds it came from, then clamp to the attachment.
	private static Rect2D FramebufferRect((float X0, float Y0, float X1, float Y1) bounds, float scaleX, float scaleY, Extent2D area)
	{
		var x = Math.Clamp((int)MathF.Floor(bounds.X0 * scaleX), 0, (int)area.Width);
		var y = Math.Clamp((int)MathF.Floor(bounds.Y0 * scaleY), 0, (int)area.Height);
		var right = Math.Clamp((int)MathF.Ceiling(bounds.X1 * scaleX), x, (int)area.Width);
		var bottom = Math.Clamp((int)MathF.Ceiling(bounds.Y1 * scaleY), y, (int)area.Height);
		return new Rect2D(new Offset2D(x, y), new Extent2D((uint)(right - x), (uint)(bottom - y)));
	}

	// Begin a dynamic-rendering scope over the given colour attachment view(s) at the given render area — used to
	// open a layer's offscreen target (LoadOp.Clear, sized to the group) and to resume the suspended parent scope
	// (LoadOp.Load, over everything).
	private void BeginColorScope(ReadOnlySpan<ImageView> views, AttachmentLoadOp load, Rect2D area)
	{
		var color = stackalloc RenderingAttachmentInfo[views.Length];
		for (var i = 0; i < views.Length; i++)
			color[i] = new RenderingAttachmentInfo
			{
				SType = StructureType.RenderingAttachmentInfo,
				ImageView = views[i],
				ImageLayout = ImageLayout.ColorAttachmentOptimal,
				LoadOp = load,
				StoreOp = AttachmentStoreOp.Store,
				ClearValue = default, // transparent-black premultiplied — a layer starts empty
			};
		RenderingInfo info = new()
		{
			SType = StructureType.RenderingInfo,
			RenderArea = area,
			LayerCount = 1,
			ColorAttachmentCount = (uint)views.Length,
			PColorAttachments = color,
		};
		_vk.CmdBeginRendering(_cmd, in info);
	}

	private void BeginColorScope(ImageView view, AttachmentLoadOp load, Rect2D area) =>
		BeginColorScope(new ReadOnlySpan<ImageView>(in view), load, area);

	// Composite a resolved layer target back into the (now-resumed) parent: one full-surface KIND_IMAGE quad that
	// samples the layer and multiplies by the premultiplied group-alpha tint, so the whole subtree fades once. The
	// HDR paper-white lift is forced off here — the layer's primitives already lifted, and the lift commutes with
	// the premultiplied alpha-over, so re-lifting would double it (the CPU rasterizer is the reference for this).
	private void CompositeLayer(VulkanTechnique baked, ImageView layerView, Half4 tint, Sampler sampler, float* block, int pushLen,
		(float X0, float Y0, float X1, float Y1) bounds, float surfaceWidth, float surfaceHeight, Extent2D area)
	{
		// The quad covers the group's bounds rather than the surface. The layer target still holds its content at the
		// content's own position — the render area restricted which pixels were written, it did not move them — so the
		// texture coordinates are the bounds as a fraction of the surface, not a fixed 0..1.
		var custom = default(Vector4);
		var (x0, y0, x1, y1) = bounds;
		var u0 = x0 / surfaceWidth;
		var v0 = y0 / surfaceHeight;
		var u1 = x1 / surfaceWidth;
		var v1 = y1 / surfaceHeight;
		Span<OverlayVertex> quad =
		[
			new(new Vector2(x0, y0), new Vector2(u0, v0), tint, custom),
			new(new Vector2(x1, y0), new Vector2(u1, v0), tint, custom),
			new(new Vector2(x1, y1), new Vector2(u1, v1), tint, custom),
			new(new Vector2(x0, y0), new Vector2(u0, v0), tint, custom),
			new(new Vector2(x1, y1), new Vector2(u1, v1), tint, custom),
			new(new Vector2(x0, y1), new Vector2(u0, v1), tint, custom),
		];
		var (quadBuffer, quadOffset) = _listVtx.Upload(_frameSlot, MemoryMarshal.AsBytes(quad));
		_vk.CmdBindVertexBuffers(_cmd, 0, 1, in quadBuffer, in quadOffset);

		const int blockFloats = VulkanTechnique.ListPushSize / sizeof(float);
		var cblock = stackalloc float[blockFloats];
		for (var i = 0; i < pushLen; i++)
			cblock[i] = block[i];
		if (pushLen > 5)
			cblock[5] = 0f;                    // isHdr off — do not re-lift the already-lifted layer
		*(uint*)(cblock + pushLen) = KindImage;
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)VulkanTechnique.ListPushSize, cblock);

		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var imageInfo = new DescriptorImageInfo { Sampler = sampler, ImageView = layerView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
		var write = new WriteDescriptorSet
		{
			SType = StructureType.WriteDescriptorSet,
			DstSet = set,
			DstBinding = 0,
			DescriptorCount = 1,
			DescriptorType = DescriptorType.CombinedImageSampler,
			PImageInfo = &imageInfo,
		};
		_vk.UpdateDescriptorSets(_ctx.Device, 1, in write, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);

		Rect2D scissor = new(new Offset2D(0, 0), area);
		_vk.CmdSetScissor(_cmd, 0, 1, in scissor);
		_vk.CmdDraw(_cmd, 6, 1, 0, 0);
	}

	// An image-layout transition (sync2) for the renderer-private layer targets — the same shape as the engine's
	// transitions, recorded here because a layer's choreography is the backend's, below the graph's barrier derivation.
	private void TransitionImage(Image image, ImageLayout oldLayout, ImageLayout newLayout,
		PipelineStageFlags2 srcStage, AccessFlags2 srcAccess, PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
	{
		var barrier = new ImageMemoryBarrier2
		{
			SType = StructureType.ImageMemoryBarrier2,
			SrcStageMask = srcStage,
			SrcAccessMask = srcAccess,
			DstStageMask = dstStage,
			DstAccessMask = dstAccess,
			OldLayout = oldLayout,
			NewLayout = newLayout,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Image = image,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
		};
		var dependency = new DependencyInfo
		{
			SType = StructureType.DependencyInfo,
			ImageMemoryBarrierCount = 1,
			PImageMemoryBarriers = &barrier,
		};
		_vk.CmdPipelineBarrier2(_cmd, in dependency);
	}
}
