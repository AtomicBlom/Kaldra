using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.Maths;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.Backend.DirectX12;

internal sealed unsafe partial class DirectX12RenderContext
{
	public void DrawList<TVertex>(string technique, DrawListIdentity identity, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged
	{
		if (commands.IsEmpty || vertices.IsEmpty || indices.IsEmpty || textures.IsEmpty)
			return;
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		BindGraphicsState(baked);
		// The list brings its own geometry, so whatever mesh the store thought was bound no longer is.
		_meshes.ResetBindings();

		// A stream with a stable identity keeps its own buffers and re-uploads only when its revision moves; anything
		// else goes into the dynamic rings (bump-allocated: several transient lists in one frame append at
		// successive offsets rather than overwrite each other).
		var vtxBytes = MemoryMarshal.AsBytes(vertices);
		var idxBytes = MemoryMarshal.AsBytes(indices);
		var (vtxAddr, idxAddr) = identity.IsStable
			? _listStore.Resolve(identity.Stream, (int)_slot, identity.Revision, _frameIndex, vtxBytes, idxBytes)
			: (_listVtx.Upload(_slot, vtxBytes), _listIdx.Upload(_slot, idxBytes));

		var vbv = new VertexBufferView { BufferLocation = vtxAddr, SizeInBytes = (uint)vtxBytes.Length, StrideInBytes = (uint)Unsafe.SizeOf<TVertex>() };
		var ibv = new IndexBufferView { BufferLocation = idxAddr, SizeInBytes = (uint)idxBytes.Length, Format = DxgiFormat.FormatR16Uint };
		BindTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		_cmd.IASetVertexBuffers(0, 1, &vbv);
		_cmd.IASetIndexBuffer(&ibv);

		// Negative-height viewport (origin at the bottom) so the same +Y-down clip math as the Vulkan backend applies.
		// Use the current pass scope dimensions so DrawList targets off-screen domains at their native resolution.
		var viewport = new Viewport { TopLeftX = 0, TopLeftY = _scopeHeight, Width = _scopeWidth, Height = -(float)_scopeHeight, MinDepth = 0, MaxDepth = 1 };
		_cmd.RSSetViewports(1, &viewport);

		// The shared per-list push fills the block's first slots; each command rewrites the slot right after it with
		// its tag (the draw-list contract), then re-pushes the whole block (push count = the used slots + the tag).
		var block = stackalloc float[8];
		var pushLen = Math.Min(push.Length, 7);
		for (var i = 0; i < pushLen; i++)
			block[i] = push[i];

		// Group-opacity layers are only meaningful on an overlay-vertex list (the markers' only producer), with a
		// captured render-target scope to detour from, and a surface-pixel→clip push to size the composite quad.
		var layersSupported = Unsafe.SizeOf<TVertex>() == Unsafe.SizeOf<OverlayVertex>()
			&& _scopeRtvCount > 0 && _colorCount > 0 && push.Length >= 2 && push[0] != 0 && push[1] != 0;
		var overlay = layersSupported ? MemoryMarshal.Cast<TVertex, OverlayVertex>(vertices) : default;
		if (layersSupported)
			_layerTargets.Configure(_scopeWidth, _scopeHeight, _colorFormats[0]);
		var layerClear = stackalloc float[4] { 0f, 0f, 0f, 0f }; // transparent: a layer starts empty

		// The list's own coordinate space, recovered from the push that maps it to clip. A layer's bounds arrive in
		// these units; the walker scales them to framebuffer pixels against the scope extent.
		var surfaceWidth = layersSupported ? 2f / push[0] : _scopeWidth;
		var surfaceHeight = layersSupported ? 2f / push[1] : _scopeHeight;

		// The descriptor table describing the memoised texture. The walker says when it needs rewriting; the handle has
		// to be kept here because the ring rewinds per submission and a stale one points at another frame's descriptors.
		var listTable = default(GpuDescriptorHandle);

		// The command stream decodes to steps: the layer nesting and its cap, the scissor, and the texture memoisation
		// are the walker's, and what remains here is the DirectX 12 recording each step needs.
		var walker = new DrawListWalker(commands, textures, overlay, layersSupported,
			(int)_scopeWidth, (int)_scopeHeight, surfaceWidth, surfaceHeight);

		while (walker.MoveNext())
		{
			if (walker.Step == DrawListStep.BeginLayer)
			{
				// Detour into a fresh transparent offscreen target: render the faded subtree at full alpha so
				// overlapping children blend against each other, not a faded backdrop.
				ref var opened = ref _layerTargets.Acquire(_slot, walker.Depth);
				if (opened.State != ResourceStates.RenderTarget)
				{
					TransitionResource(opened.Resource, opened.State, ResourceStates.RenderTarget);
					opened.State = ResourceStates.RenderTarget;
				}
				var rtv = opened.Rtv;
				_cmd.OMSetRenderTargets(1, &rtv, false, (CpuDescriptorHandle*)null);
				// The group's own area, not the whole surface. A layer costs two render-target switches and a clear, and
				// on a tile-based GPU those are priced by area — a checkbox fading in should not clear a screen's worth
				// of tiles.
				var clearRect = ToBox(walker.LayerArea);
				_cmd.ClearRenderTargetView(rtv, layerClear, 1u, &clearRect);
				continue;
			}

			if (walker.Step == DrawListStep.EndLayer)
			{
				// Resolve the layer (render-target → shader-resource) and rebind the parent's render targets.
				ref var closed = ref _layerTargets.Acquire(_slot, walker.Depth);
				TransitionResource(closed.Resource, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
				closed.State = ResourceStates.PixelShaderResource;
				if (walker.Depth > 0)
				{
					var parentRtv = _layerTargets.Acquire(_slot, walker.Depth - 1).Rtv;
					_cmd.OMSetRenderTargets(1, &parentRtv, false, (CpuDescriptorHandle*)null);
				}
				else
				{
					fixed (CpuDescriptorHandle* pRtv = _scopeRtvs)
						_cmd.OMSetRenderTargets((uint)_scopeRtvCount, pRtv, false, (CpuDescriptorHandle*)null);
				}

				CompositeLayer(baked, closed.Resource, walker.Tint, block, pushLen, walker.Bounds, surfaceWidth, surfaceHeight);

				// Restore the list's geometry for the following commands — the composite bound its own quad buffer.
				_cmd.IASetVertexBuffers(0, 1, &vbv);
				_cmd.IASetIndexBuffer(&ibv);
				continue;
			}

			*(uint*)(block + pushLen) = walker.Tag;
			if (baked.RootConstantParam >= 0)
				_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, (uint)(pushLen + 1), block, 0);

			// Write the bound texture's SRV (real format + full mip chain) into a fresh ring slot, then bind the table.
			if (baked.TableParam >= 0)
			{
				if (walker.TextureChanged)
				{
					var (cpu, gpu) = _descriptors.Allocate(1);
					WriteListTextureSrv(walker.TextureId, cpu);
					listTable = gpu;
				}
				_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, listTable);
			}

			var scissor = ToBox(walker.Scissor);
			_cmd.RSSetScissorRects(1, &scissor);
			_cmd.DrawIndexedInstanced((uint)walker.IndexCount, 1, (uint)walker.IndexOffset, walker.VertexOffset, 0);
		}

		if (walker.NestingExceeded)
			DrawListGeometry.WarnLayerNestingExceeded(ref _warnedLayerNesting, walker.DeepestNesting);

		// Defensive: a well-formed list balances its markers, but never leave a layer's target bound — rebind the
		// captured pass scope so the engine's following passes (and present) write the screen, not an orphaned layer.
		// One rebind covers any depth: unlike a Vulkan rendering scope, a render-target binding does not nest.
		if (walker.OpenLayers > 0)
		{
			while (walker.TryUnwind(out _)) { }
			fixed (CpuDescriptorHandle* pRtv = _scopeRtvs)
				_cmd.OMSetRenderTargets((uint)_scopeRtvCount, pRtv, false, (CpuDescriptorHandle*)null);
		}
	}

	// Set once a list has nested layers past the cap, so the diagnostic is one line per context rather than per frame.
	private bool _warnedLayerNesting;

	// A decoded pixel rectangle in DirectX 12's corner form.
	private static Box2D<int> ToBox((int X, int Y, int Width, int Height) r) =>
		new(r.X, r.Y, r.X + r.Width, r.Y + r.Height);

	// Composite a resolved layer target back into the (now-rebound) parent: a KIND_IMAGE quad over the group that
	// samples the layer and multiplies by the premultiplied group-alpha tint, so the whole subtree fades once.
	private void CompositeLayer(DirectX12Technique baked, ComPtr<ID3D12Resource> layer, Half4 tint, float* block, int pushLen,
		(float X0, float Y0, float X1, float Y1) bounds, float surfaceWidth, float surfaceHeight)
	{
		Span<OverlayVertex> quad = stackalloc OverlayVertex[DrawListGeometry.CompositeQuadVertexCount];
		DrawListGeometry.WriteCompositeQuad(bounds, surfaceWidth, surfaceHeight, tint, quad);
		var quadBytes = MemoryMarshal.AsBytes(quad);
		var quadAddr = _listVtx.Upload(_slot, quadBytes);
		var qvbv = new VertexBufferView { BufferLocation = quadAddr, SizeInBytes = (uint)quadBytes.Length, StrideInBytes = (uint)Unsafe.SizeOf<OverlayVertex>() };
		_cmd.IASetVertexBuffers(0, 1, &qvbv);

		if (baked.RootConstantParam >= 0)
		{
			var cblock = stackalloc float[8];
			for (var i = 0; i < pushLen; i++)
				cblock[i] = block[i];
			if (pushLen > DrawListGeometry.HdrPushSlot)
				cblock[DrawListGeometry.HdrPushSlot] = 0f;
			*(uint*)(cblock + pushLen) = DrawListGeometry.KindImage;
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, (uint)(pushLen + 1), cblock, 0);
		}

		if (baked.TableParam >= 0)
		{
			var (cpu, gpu) = _descriptors.Allocate(1);
			var srv = new ShaderResourceViewDesc
			{
				Format = _layerTargets.Format,
				ViewDimension = SrvDimension.Texture2D,
				Shader4ComponentMapping = DefaultShader4ComponentMapping,
				Anonymous = new ShaderResourceViewDescUnion
				{
					Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f },
				},
			};
			_ctx.Device.CreateShaderResourceView(layer, &srv, cpu);
			_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, gpu);
		}

		var scissor = new Box2D<int>(0, 0, (int)_scopeWidth, (int)_scopeHeight);
		_cmd.RSSetScissorRects(1, &scissor);
		_cmd.DrawInstanced(6, 1, 0, 0);
	}

	// A resource-state transition for the renderer-private layer targets — recorded here because a layer's
	// choreography is the backend's, below the graph's barrier derivation.
	private void TransitionResource(ComPtr<ID3D12Resource> resource, ResourceStates before, ResourceStates after)
	{
		var barrier = new ResourceBarrier
		{
			Type = ResourceBarrierType.Transition,
			Flags = ResourceBarrierFlags.None,
			Anonymous = new ResourceBarrierUnion
			{
				Transition = new ResourceTransitionBarrier
				{
					PResource = resource.Handle,
					Subresource = 0xffffffff,
					StateBefore = before,
					StateAfter = after,
				},
			},
		};
		_cmd.ResourceBarrier(1, &barrier);
	}
}
