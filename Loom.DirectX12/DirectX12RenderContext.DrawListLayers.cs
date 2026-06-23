using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Loom.Core;
using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.Maths;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DirectX12;

internal sealed unsafe partial class DirectX12RenderContext
{
	// UI group-opacity layer markers (UIPrimKind, kept in sync with SUIsei): zero-index commands that never reach the
	// fragment shader — the backend turns a BeginLayer/EndLayer pair into an offscreen target + a composite. KIND_IMAGE
	// is the shader branch the composite blit uses (sample the layer, tint by the group alpha).
	private const uint TagBeginLayer = 5;
	private const uint TagEndLayer = 6;
	private const uint KindImage = 4;

	public void DrawList<TVertex>(string technique, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged
	{
		if (commands.IsEmpty || vertices.IsEmpty || indices.IsEmpty || textures.IsEmpty)
			return;
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		// Stage this frame's geometry into the dynamic rings (bump-allocated: several draw lists in one frame
		// append at successive offsets rather than overwrite each other).
		var vtxBytes = MemoryMarshal.AsBytes(vertices);
		var idxBytes = MemoryMarshal.AsBytes(indices);
		var vtxAddr = _listVtx.Upload(_slot, vtxBytes);
		var idxAddr = _listIdx.Upload(_slot, idxBytes);

		var vbv = new VertexBufferView { BufferLocation = vtxAddr, SizeInBytes = (uint)vtxBytes.Length, StrideInBytes = (uint)Unsafe.SizeOf<TVertex>() };
		var ibv = new IndexBufferView { BufferLocation = idxAddr, SizeInBytes = (uint)idxBytes.Length, Format = DxgiFormat.FormatR16Uint };
		_cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		_cmd.IASetVertexBuffers(0, 1, &vbv);
		_cmd.IASetIndexBuffer(&ibv);

		// Negative-height viewport (origin at the bottom) so the same +Y-down clip math as the Vulkan backend applies.
		var viewport = new Viewport { TopLeftX = 0, TopLeftY = _fbHeight, Width = _fbWidth, Height = -(float)_fbHeight, MinDepth = 0, MaxDepth = 1 };
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
				_cmd.ClearRenderTargetView(rtv, layerClear, 0u, (Box2D<int>*)null);
				_layerStack.Add((depth, overlay[c.VertexOffset].Color));
				continue;
			}
			if (layersSupported && c.Tag == TagEndLayer)
			{
				if (_layerStack.Count == 0)
					continue;
				var (depth, tint) = _layerStack[^1];
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

				CompositeLayer(baked, target.Resource, tint, block, pushLen, push);

				// Restore the list's geometry for the following commands (the composite bound its own quad buffer).
				_cmd.IASetVertexBuffers(0, 1, &vbv);
				_cmd.IASetIndexBuffer(&ibv);
				continue;
			}

			var clip = c.ClipRect;
			var minX = MathF.Max(0, clip.X);
			var minY = MathF.Max(0, clip.Y);
			var maxX = MathF.Min(_fbWidth, clip.X + clip.Width);
			var maxY = MathF.Min(_fbHeight, clip.Y + clip.Height);
			if (maxX <= minX || maxY <= minY)
				continue;

			*(uint*)(block + pushLen) = c.Tag;
			if (baked.RootConstantParam >= 0)
				_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, (uint)(pushLen + 1), block, 0);

			// Write the bound texture's SRV (real format + full mip chain) into a fresh ring slot, then bind the table.
			if (baked.TableParam >= 0)
			{
				var (cpu, gpu) = _ring.Allocate(1);
				WriteListTextureSrv(textures[Math.Clamp(c.TextureIndex, 0, textures.Length - 1)].Id, cpu);
				_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, gpu);
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

	// Composite a resolved layer target back into the (now-rebound) parent: one full-surface KIND_IMAGE quad that
	// samples the layer and multiplies by the premultiplied group-alpha tint, so the whole subtree fades once. The HDR
	// paper-white lift is forced off here — the layer's primitives already lifted, and the lift commutes with the
	// premultiplied alpha-over, so re-lifting would double it (the CPU rasterizer is the reference).
	private void CompositeLayer(DirectX12Technique baked, ComPtr<ID3D12Resource> layer, Half4 tint, float* block, int pushLen, ReadOnlySpan<float> push)
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
			var (cpu, gpu) = _ring.Allocate(1);
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
