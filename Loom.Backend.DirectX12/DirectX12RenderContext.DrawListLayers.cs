using System.Numerics;
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
	// UI group-opacity layer markers (UIPrimKind, kept in sync with SUIsei): zero-index commands that never reach the
	// fragment shader — the backend turns a BeginLayer/EndLayer pair into an offscreen target + a composite. KIND_IMAGE
	// is the shader branch the composite blit uses (sample the layer, tint by the group alpha).
	private const uint TagBeginLayer = 5;
	private const uint TagEndLayer = 6;
	private const uint KindImage = 4;

	public void DrawList<TVertex>(string technique, DrawListIdentity identity, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged
	{
		if (commands.IsEmpty || vertices.IsEmpty || indices.IsEmpty || textures.IsEmpty)
			return;
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		BindGraphicsState(baked);
		// The list brings its own geometry, so whatever mesh the store thought was bound no longer is.
		_meshes.ResetBindings();

		// Stage this frame's geometry into the dynamic rings (bump-allocated: several draw lists in one frame
		// append at successive offsets rather than overwrite each other).
		var vtxBytes = MemoryMarshal.AsBytes(vertices);
		var idxBytes = MemoryMarshal.AsBytes(indices);
		var vtxAddr = _listVtx.Upload(_slot, vtxBytes);
		var idxAddr = _listIdx.Upload(_slot, idxBytes);

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
		_layerStack.Clear();
		var layerClear = stackalloc float[4] { 0f, 0f, 0f, 0f }; // transparent: a layer starts empty

		// The list's own coordinate space, recovered from the push that maps it to clip, and the scale from it to
		// framebuffer pixels. A layer's bounds arrive in surface pixels; its clear rect is needed in framebuffer ones.
		var surfaceWidth = layersSupported ? 2f / push[0] : _scopeWidth;
		var surfaceHeight = layersSupported ? 2f / push[1] : _scopeHeight;
		var toFramebufferX = _scopeWidth / surfaceWidth;
		var toFramebufferY = _scopeHeight / surfaceHeight;

		// The texture whose SRV the bound table currently describes, and that table's handle. -1 = nothing bound yet.
		var listTextureId = -1;
		var listTable = default(GpuDescriptorHandle);

		foreach (var c in commands)
		{
			if (layersSupported && c.Tag == TagBeginLayer && _layerStack.Count < DirectX12LayerTargets.MaxNesting)
			{
				// Detour into a fresh transparent offscreen target: render the faded subtree at full alpha so
				// overlapping children blend against each other, not a faded backdrop.
				var depth = _layerStack.Count;
				ref var target = ref _layerTargets.Acquire(_slot, depth);
				if (target.State != ResourceStates.RenderTarget)
				{
					TransitionResource(target.Resource, target.State, ResourceStates.RenderTarget);
					target.State = ResourceStates.RenderTarget;
				}
				var rtv = target.Rtv;
				_cmd.OMSetRenderTargets(1, &rtv, false, (CpuDescriptorHandle*)null);
				// The group's own bounds, not the whole surface. A layer costs two render-target switches and a clear,
				// and on a tile-based GPU those are priced by area — a checkbox fading in should not clear a screen's
				// worth of tiles. The marker carries the bounds as its ClipRect precisely so a backend can use them.
				var bounds = LayerBounds(c.ClipRect, surfaceWidth, surfaceHeight);
				var clearRect = FramebufferRect(bounds, toFramebufferX, toFramebufferY, _scopeWidth, _scopeHeight);
				_cmd.ClearRenderTargetView(rtv, layerClear, 1u, &clearRect);
				_layerStack.Add((depth, overlay[c.VertexOffset].Color, bounds));
				continue;
			}
			if (layersSupported && c.Tag == TagEndLayer)
			{
				if (_layerStack.Count == 0)
					continue;
				var (depth, tint, bounds) = _layerStack[^1];
				_layerStack.RemoveAt(_layerStack.Count - 1);

				// Resolve the layer (render-target → shader-resource) and rebind the parent's render targets.
				ref var target = ref _layerTargets.Acquire(_slot, depth);
				TransitionResource(target.Resource, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
				target.State = ResourceStates.PixelShaderResource;
				if (_layerStack.Count > 0)
				{
					var parentRtv = _layerTargets.Acquire(_slot, _layerStack[^1].Depth).Rtv;
					_cmd.OMSetRenderTargets(1, &parentRtv, false, (CpuDescriptorHandle*)null);
				}
				else
				{
					fixed (CpuDescriptorHandle* pRtv = _scopeRtvs)
						_cmd.OMSetRenderTargets((uint)_scopeRtvCount, pRtv, false, (CpuDescriptorHandle*)null);
				}

				CompositeLayer(baked, target.Resource, tint, block, pushLen, bounds, surfaceWidth, surfaceHeight);

				// Restore the list's geometry for the following commands (the composite bound its own quad buffer),
				// and forget the memoised texture table — the composite replaced it with the layer's own.
				_cmd.IASetVertexBuffers(0, 1, &vbv);
				_cmd.IASetIndexBuffer(&ibv);
				listTextureId = -1;
				continue;
			}

			var clip = c.ClipRect;
			var minX = MathF.Max(0, clip.X);
			var minY = MathF.Max(0, clip.Y);
			var maxX = MathF.Min(_scopeWidth, clip.X + clip.Width);
			var maxY = MathF.Min(_scopeHeight, clip.Y + clip.Height);
			if (maxX <= minX || maxY <= minY)
				continue;

			*(uint*)(block + pushLen) = c.Tag;
			if (baked.RootConstantParam >= 0)
				_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, (uint)(pushLen + 1), block, 0);

			// Write the bound texture's SRV (real format + full mip chain) into a fresh ring slot, then bind the table.
			// A UI list is mostly one texture — the glyph atlas — for run after run of commands, so the descriptor is
			// only rewritten when the texture actually changes; otherwise the table from the previous command still
			// describes it. Reset per submission, since the ring rewinds and a stale handle would point at another
			// frame's descriptors.
			if (baked.TableParam >= 0)
			{
				var texId = textures[Math.Clamp(c.TextureIndex, 0, textures.Length - 1)].Id;
				if (texId != listTextureId)
				{
					var (cpu, gpu) = _descriptors.Allocate(1);
					WriteListTextureSrv(texId, cpu);
					listTextureId = texId;
					listTable = gpu;
				}
				_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, listTable);
			}

			var scissor = new Box2D<int>((int)minX, (int)minY, (int)maxX, (int)maxY);
			_cmd.RSSetScissorRects(1, &scissor);
			_cmd.DrawIndexedInstanced((uint)c.IndexCount, 1, (uint)c.IndexOffset, c.VertexOffset, 0);
		}

		// Defensive: a well-formed list balances its markers, but never leave a layer's target bound — rebind the
		// captured pass scope so the engine's following passes (and present) write the screen, not an orphaned layer.
		if (_layerStack.Count > 0)
		{
			_layerStack.Clear();
			fixed (CpuDescriptorHandle* pRtv = _scopeRtvs)
				_cmd.OMSetRenderTargets((uint)_scopeRtvCount, pRtv, false, (CpuDescriptorHandle*)null);
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

	// The same bounds as a framebuffer-pixel rect: floor the origin and ceil the far edge so the rect never covers
	// less than the bounds it came from, then clamp to the target.
	private static Box2D<int> FramebufferRect((float X0, float Y0, float X1, float Y1) bounds, float scaleX, float scaleY, uint width, uint height)
	{
		var x = Math.Clamp((int)MathF.Floor(bounds.X0 * scaleX), 0, (int)width);
		var y = Math.Clamp((int)MathF.Floor(bounds.Y0 * scaleY), 0, (int)height);
		var right = Math.Clamp((int)MathF.Ceiling(bounds.X1 * scaleX), x, (int)width);
		var bottom = Math.Clamp((int)MathF.Ceiling(bounds.Y1 * scaleY), y, (int)height);
		return new Box2D<int>(x, y, right, bottom);
	}

	// Composite a resolved layer target back into the (now-rebound) parent: a KIND_IMAGE quad over the group that
	// samples the layer and multiplies by the premultiplied group-alpha tint, so the whole subtree fades once. The HDR
	// paper-white lift is forced off here — the layer's primitives already lifted, and the lift commutes with the
	// premultiplied alpha-over, so re-lifting would double it (the CPU rasterizer is the reference).
	private void CompositeLayer(DirectX12Technique baked, ComPtr<ID3D12Resource> layer, Half4 tint, float* block, int pushLen,
		(float X0, float Y0, float X1, float Y1) bounds, float surfaceWidth, float surfaceHeight)
	{
		// The quad covers the group's bounds rather than the surface. The layer target still holds its content at the
		// content's own position — the clear rect restricted which pixels were reset, it did not move them — so the
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
		var quadBytes = MemoryMarshal.AsBytes(quad);
		var quadAddr = _listVtx.Upload(_slot, quadBytes);
		var qvbv = new VertexBufferView { BufferLocation = quadAddr, SizeInBytes = (uint)quadBytes.Length, StrideInBytes = (uint)Unsafe.SizeOf<OverlayVertex>() };
		_cmd.IASetVertexBuffers(0, 1, &qvbv);

		if (baked.RootConstantParam >= 0)
		{
			var cblock = stackalloc float[8];
			for (var i = 0; i < pushLen; i++)
				cblock[i] = block[i];
			if (pushLen > 5)
				cblock[5] = 0f;                 // isHdr off — do not re-lift the already-lifted layer
			*(uint*)(cblock + pushLen) = KindImage;
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
