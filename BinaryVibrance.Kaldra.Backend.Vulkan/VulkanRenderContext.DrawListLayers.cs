using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
using BinaryVibrance.Kaldra.Core.Primitives;
using Silk.NET.Vulkan;

namespace BinaryVibrance.Kaldra.Backend.Vulkan;

internal sealed unsafe partial class VulkanRenderContext
{
	public void DrawList<TVertex>(string technique, DrawListIdentity identity, ReadOnlySpan<TextureRef> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged
	{
		_validate.DrawList(technique, identity, textures);
		if (commands.IsEmpty || vertices.IsEmpty || indices.IsEmpty || textures.IsEmpty)
			return;
		var baked = Bake(technique);
		var drawing = baked; // the pipeline drawing now: the pass's, or inside a layer the layer target's
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

		// The current pass scope's extent, which is always set: a raster pass is now rejected at derive time unless it
		// has an attachment, an attachment has a positive extent, and the engine hands the scope over before running the
		// pass callback this is called from. The other backends read their scope extent unguarded for the same reason,
		// and a fallback here only made the three look like they disagreed.
		var layerArea = _scopeArea;
		Viewport viewport = new() { X = 0, Y = 0, Width = layerArea.Width, Height = layerArea.Height, MinDepth = 0, MaxDepth = 1 };
		_vk.CmdSetViewport(_cmd, 0, 1, in viewport);

		// The shared per-list push fills the block's first slots; each command rewrites the slot right after it with
		// its tag (the draw-list contract). Pad to the layout's fixed range so the shader always reads defined memory.
		const int blockFloats = VulkanTechnique.ListPushSize / sizeof(float);
		var block = stackalloc float[blockFloats];
		var pushLen = Math.Min(push.Length, DrawListGeometry.MaxListPushFloats);
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

		// The list's own coordinate space, recovered from the push that maps it to clip. A layer's bounds arrive in
		// these units; the walker scales them to framebuffer pixels against the area below.
		var surfaceWidth = layersSupported ? 2f / push[0] : layerArea.Width;
		var surfaceHeight = layersSupported ? 2f / push[1] : layerArea.Height;

		// The command stream decodes to steps: the layer nesting and its cap, the scissor, and the texture memoisation
		// are the walker's, and what remains here is the Vulkan recording each step needs.
		var walker = new DrawListWalker(commands, textures, overlay, layersSupported,
			(int)layerArea.Width, (int)layerArea.Height, surfaceWidth, surfaceHeight);

		while (walker.MoveNext())
		{
			if (walker.Step == DrawListStep.BeginLayer)
			{
				// Suspend the current scope, render the faded subtree into a fresh transparent offscreen target at
				// full alpha (so overlapping children blend against each other, not a faded backdrop).
				_vk.CmdEndRendering(_cmd);
				var opened = _layerTargets.Acquire(_frameSlot, walker.Depth);
				TransitionImage(opened.Image, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
					PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
					PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);

				// The group's own area, not the whole surface. A layer costs two render-pass breaks and a clear, and on
				// a tile-based GPU those are priced by area — a checkbox fading in should not clear and store a
				// screen's worth of tiles.
				BeginColorScope(opened.View, AttachmentLoadOp.Clear, ToRect(walker.LayerArea));
				_vk.CmdSetViewport(_cmd, 0, 1, in viewport);
				// The layer target is one single-sample attachment with no depth, however the pass's are made, and a
				// pipeline must be baked for the attachments it draws into.
				drawing = BakeLayer(technique);
				BindPipeline(PipelineBindPoint.Graphics, drawing.Pipeline);
				continue;
			}

			if (walker.Step == DrawListStep.EndLayer)
			{
				var closed = _layerTargets.Acquire(_frameSlot, walker.Depth);

				// Resolve the layer target, then resume the parent scope (LoadOp.Load — keep what it already holds).
				_vk.CmdEndRendering(_cmd);
				TransitionImage(closed.Image, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
					PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
					PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
				// The parent resumes over its whole area — commands after this one draw anywhere — but only the
				// layer's own bounds are composited back into it.
				if (walker.Depth > 0)
					BeginColorScope(_layerTargets.Acquire(_frameSlot, walker.Depth - 1).View, AttachmentLoadOp.Load, new Rect2D(default, layerArea));
				else
					BeginColorScope(_scopeColorViews.AsSpan(0, _scopeColorCount), AttachmentLoadOp.Load, new Rect2D(default, layerArea));
				_vk.CmdSetViewport(_cmd, 0, 1, in viewport);

				// Composited, and drawn on, by the parent's pipeline: a nested layer's parent is a layer target too
				drawing = walker.Depth > 0 ? BakeLayer(technique) : baked;
				BindPipeline(PipelineBindPoint.Graphics, drawing.Pipeline);
				CompositeLayer(drawing, closed.View, walker.Tint, sampler, block, pushLen, walker.Bounds, surfaceWidth, surfaceHeight, layerArea);

				// Restore the list's geometry for the following commands — the composite bound its own quad buffer.
				_vk.CmdBindVertexBuffers(_cmd, 0, 1, in vtxBuffer, in vtxOffset);
				_vk.CmdBindIndexBuffer(_cmd, idxBuffer, idxOffset, IndexType.Uint16);
				continue;
			}

			*(uint*)(block + pushLen) = walker.Tag;
			_vk.CmdPushConstants(_cmd, drawing.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)VulkanTechnique.ListPushSize, block);

			if (walker.TextureChanged)
			{
				// An id offset by DebugTextureBase is a graph resource — a TextureRef the pass declared with .Samples, or
				// an inspectable one (the resource viewer): sample this frame's live image instead of a loaded
				// texture-store entry. The graph has already put a declared one in ShaderReadOnlyOptimal for this pass.
				var view = walker.TextureId >= InspectableResource.DebugTextureBase
					? _resources.Get(_frameSlot, walker.TextureId - InspectableResource.DebugTextureBase).SampledView
					: _textures.View(walker.TextureId);
				var set = _descriptors.Allocate(drawing.DescriptorSetLayout);
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
				// so it declares no uniform buffer — the only descriptor type Kaldra binds dynamically. One that did
				// would fail validation here for supplying none, which is the loud failure that should follow.
				_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, drawing.Layout, 0, 1, in set, 0, null);
			}

			var scissor = ToRect(walker.Scissor);
			_vk.CmdSetScissor(_cmd, 0, 1, in scissor);

			_vk.CmdDrawIndexed(_cmd, (uint)walker.IndexCount, 1, (uint)walker.IndexOffset, walker.VertexOffset, 0);
		}

		if (walker.NestingExceeded)
			DrawListGeometry.WarnLayerNestingExceeded(ref _warnedLayerNesting, walker.DeepestNesting);

		// Defensive: a well-formed list balances its markers, but never leave a layer scope open — the engine ends
		// exactly one scope after the pass, which must be the parent (screen) scope.
		while (walker.TryUnwind(out _))
		{
			_vk.CmdEndRendering(_cmd);
			BeginColorScope(((ReadOnlySpan<ImageView>)_scopeColorViews)[.._scopeColorCount], AttachmentLoadOp.Load, new Rect2D(default, layerArea));
			BindPipeline(PipelineBindPoint.Graphics, baked.Pipeline);
		}
	}

	// Set once a list has nested layers past the cap, so the diagnostic is one line per context rather than per frame.
	private bool _warnedLayerNesting;

	// A decoded pixel rectangle in Vulkan's origin+extent form.
	private static Rect2D ToRect((int X, int Y, int Width, int Height) r) =>
		new(new Offset2D(r.X, r.Y), new Extent2D((uint)r.Width, (uint)r.Height));

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

	// Composite a resolved layer target back into the (now-resumed) parent: one KIND_IMAGE quad over the group that
	// samples the layer and multiplies by the premultiplied group-alpha tint, so the whole subtree fades once.
	private void CompositeLayer(VulkanTechnique baked, ImageView layerView, Half4 tint, Sampler sampler, float* block, int pushLen,
		(float X0, float Y0, float X1, float Y1) bounds, float surfaceWidth, float surfaceHeight, Extent2D area)
	{
		Span<OverlayVertex> quad = stackalloc OverlayVertex[DrawListGeometry.CompositeQuadVertexCount];
		DrawListGeometry.WriteCompositeQuad(bounds, surfaceWidth, surfaceHeight, tint, quad);
		var (quadBuffer, quadOffset) = _listVtx.Upload(_frameSlot, MemoryMarshal.AsBytes(quad));
		_vk.CmdBindVertexBuffers(_cmd, 0, 1, in quadBuffer, in quadOffset);

		const int blockFloats = VulkanTechnique.ListPushSize / sizeof(float);
		var cblock = stackalloc float[blockFloats];
		for (var i = 0; i < pushLen; i++)
			cblock[i] = block[i];
		if (pushLen > DrawListGeometry.HdrPushSlot)
			cblock[DrawListGeometry.HdrPushSlot] = 0f;
		*(uint*)(cblock + pushLen) = DrawListGeometry.KindImage;
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
