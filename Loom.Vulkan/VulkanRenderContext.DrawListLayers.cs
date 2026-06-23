using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Loom.Core;
using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;
using Silk.NET.Vulkan;

namespace Loom.Vulkan;

internal sealed unsafe partial class VulkanRenderContext
{
	// UI group-opacity layer markers (UIPrimKind, kept in sync with SUIsei): a BeginLayer/EndLayer pair carries no
	// indices and never reaches the fragment shader — the backend turns it into an offscreen render scope and a
	// composite. KIND_IMAGE is the shader branch the composite blit uses (sample the layer, tint by group alpha).
	private const uint TagBeginLayer = 5;
	private const uint TagEndLayer = 6;
	private const uint KindImage = 4;

	public void DrawList<TVertex>(string technique, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged
	{
		if (commands.IsEmpty || vertices.IsEmpty || indices.IsEmpty || textures.IsEmpty)
			return;
		var baked = Bake(technique);
		var template = _registry[technique];

		// Stage this frame's geometry into the dynamic rings (bump-allocated: several draw lists in one frame
		// append at successive offsets rather than overwrite each other).
		var (vtxBuffer, vtxOffset) = _listVtx.Upload(_frameSlot, MemoryMarshal.AsBytes(vertices));
		var (idxBuffer, idxOffset) = _listIdx.Upload(_frameSlot, MemoryMarshal.AsBytes(indices));

		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdBindVertexBuffers(_cmd, 0, 1, in vtxBuffer, in vtxOffset);
		_vk.CmdBindIndexBuffer(_cmd, idxBuffer, idxOffset, IndexType.Uint16);

		Viewport viewport = new() { X = 0, Y = 0, Width = _fbExtent.Width, Height = _fbExtent.Height, MinDepth = 0, MaxDepth = 1 };
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
			_ => throw new VulkanException($"SamplerAddress '{template.Address}' is not mapped to a sampler."),
		};

		// Group-opacity layers are only meaningful on an overlay-vertex list (the only producer of the markers); a
		// scope to suspend; and a surface-pixel→clip push to size the composite quad. Without all three, markers fold
		// to no-ops (a zero-index draw).
		var layerArea = _scopeArea.Width != 0 ? _scopeArea : _fbExtent;
		var layersSupported = Unsafe.SizeOf<TVertex>() == Unsafe.SizeOf<OverlayVertex>()
			&& _scopeColorCount > 0 && _colorCount > 0 && push.Length >= 2 && push[0] != 0 && push[1] != 0;
		var overlay = layersSupported ? MemoryMarshal.Cast<TVertex, OverlayVertex>(vertices) : default;
		if (layersSupported)
			_layerTargets.Configure(layerArea, _colorFormats[0]);
		_layerStack.Clear();

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
				BeginColorScope(target.View, AttachmentLoadOp.Clear, layerArea);
				_vk.CmdSetViewport(_cmd, 0, 1, in viewport);
				_layerStack.Add((target.Image, target.View, overlay[c.VertexOffset].Color));
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
				if (_layerStack.Count > 0)
					BeginColorScope(_layerStack[^1].View, AttachmentLoadOp.Load, layerArea);
				else
					BeginColorScope(_scopeColorViews.AsSpan(0, _scopeColorCount), AttachmentLoadOp.Load, layerArea);
				_vk.CmdSetViewport(_cmd, 0, 1, in viewport);

				CompositeLayer(baked, layer.View, layer.Tint, sampler, block, pushLen, push, layerArea);

				// Restore the list's geometry for the following commands (the composite bound its own quad buffer).
				_vk.CmdBindVertexBuffers(_cmd, 0, 1, in vtxBuffer, in vtxOffset);
				_vk.CmdBindIndexBuffer(_cmd, idxBuffer, idxOffset, IndexType.Uint16);
				continue;
			}

			var clip = c.ClipRect;
			var minX = MathF.Max(0, clip.X);
			var minY = MathF.Max(0, clip.Y);
			var maxX = MathF.Min(_fbExtent.Width, clip.X + clip.Width);
			var maxY = MathF.Min(_fbExtent.Height, clip.Y + clip.Height);
			if (maxX <= minX || maxY <= minY)
				continue;

			*(uint*)(block + pushLen) = c.Tag;
			_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)VulkanTechnique.ListPushSize, block);

			var texId = textures[Math.Clamp(c.TextureIndex, 0, textures.Length - 1)].Id;
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
			_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);

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
			BeginColorScope(((ReadOnlySpan<ImageView>)_scopeColorViews)[.._scopeColorCount], AttachmentLoadOp.Load, layerArea);
		}
	}

	// Begin a dynamic-rendering scope over the given colour attachment view(s) at the pass render area — used to
	// open a layer's offscreen target (LoadOp.Clear) and to resume the suspended parent scope (LoadOp.Load).
	private void BeginColorScope(ReadOnlySpan<ImageView> views, AttachmentLoadOp load, Extent2D area)
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
			RenderArea = new Rect2D(new Offset2D(0, 0), area),
			LayerCount = 1,
			ColorAttachmentCount = (uint)views.Length,
			PColorAttachments = color,
		};
		_vk.CmdBeginRendering(_cmd, in info);
	}

	private void BeginColorScope(ImageView view, AttachmentLoadOp load, Extent2D area) =>
		BeginColorScope(new ReadOnlySpan<ImageView>(in view), load, area);

	// Composite a resolved layer target back into the (now-resumed) parent: one full-surface KIND_IMAGE quad that
	// samples the layer and multiplies by the premultiplied group-alpha tint, so the whole subtree fades once. The
	// HDR paper-white lift is forced off here — the layer's primitives already lifted, and the lift commutes with
	// the premultiplied alpha-over, so re-lifting would double it (the CPU rasterizer is the reference for this).
	private void CompositeLayer(VulkanTechnique baked, ImageView layerView, Half4 tint, Sampler sampler, float* block, int pushLen, ReadOnlySpan<float> push, Extent2D area)
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
