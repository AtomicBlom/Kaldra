using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.Backend.DirectX12;

internal sealed unsafe partial class DirectX12RenderContext
{
	public void Draw(in DrawCall call)
	{
		var baked = _techniques.GetOrBake(call.Technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		// The technique's CBVs (b1..) in slot order: the pass's .Reads constants (e.g. a view-projection) first,
		// then the per-draw Uniforms block (model, object data). Bound for material and sampled draws alike.
		if (baked.CbvRootParam >= 0)
		{
			var bound = 0;
			for (var j = 0; j < _activeConstantsCount; j++)
				_cmd.SetGraphicsRootConstantBufferView((uint)(baked.CbvRootParam + bound++), _transients!.Resource(_slot, _activeConstantsIds[j]).GetGPUVirtualAddress());
			if (call.Uniforms.Length > 0 && baked.UniformBufferCount > bound)
				_cmd.SetGraphicsRootConstantBufferView((uint)(baked.CbvRootParam + bound), _uniformUpload.Upload(_slot, call.Uniforms));
		}

		// Material textures or sampled graph resources fill the SRV table — mutually exclusive texture source.
		if (call.Material is { } material)
		{
			if (baked.TableParam >= 0 && baked.InputCount > 0)
				BindMaterialTable(baked, material, call.Technique);
		}
		else if (!call.Sampled.IsEmpty)
		{
			BindInputs(baked, call.Sampled);
		}

		_cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

		if (call.Instances.IsEmpty)
		{
			// Single-instance draw: skinned override or standard mesh.
			if (call.SkinnedVertices is { } sv)
			{
				if (_transients is null)
					throw new InvalidOperationException("DirectX12: skinned draw requires an active graph (SetGraph).");
				_meshes.DrawSkinned(_cmd, call.Mesh!.Value.Id, SkinnedView(sv));
			}
			else
			{
				_meshes.Draw(_cmd, call.Mesh!.Value.Id);
			}
		}
		else if (call.Instances.GpuBuffer is { } gpuBuf)
		{
			if (_transients is null)
				throw new InvalidOperationException("DirectX12: a GPU-buffer instanced draw requires an active graph (SetGraph).");
			gpuBuf.CheckInstanceStride(baked.InstanceStride, call.Technique);
			if (call.Mesh is { } mesh)
			{
				// GPU-resident instance stream + indexed mesh draw (foliage, GPU-driven placement).
				var byteSize = _transients.ByteSize(gpuBuf.Id);
				var count = call.Instances.Count;
				var instanceView = new VertexBufferView
				{
					BufferLocation = _transients.Resource(_slot, gpuBuf.Id).GetGPUVirtualAddress(),
					SizeInBytes = byteSize,
					StrideInBytes = baked.InstanceStride > 0 ? (uint)baked.InstanceStride : (count > 0 ? byteSize / (uint)count : byteSize),
				};
				_meshes.DrawInstanced(_cmd, mesh.Id, instanceView, (uint)count);
			}
			else
			{
				// Procedural: GPU buffer IS the per-instance vertex stream; vertexCount verts per instance
				// fabricated from SV_VertexID (the GPU-particle path).
				var vbv = new VertexBufferView
				{
					BufferLocation = _transients.Resource(_slot, gpuBuf.Id).GetGPUVirtualAddress(),
					SizeInBytes = _transients.ByteSize(gpuBuf.Id),
					StrideInBytes = (uint)baked.InstanceStride,
				};
				_cmd.IASetVertexBuffers(0, 1, &vbv);
				_cmd.DrawInstanced((uint)call.VertexCount, (uint)call.Instances.Count, 0, 0);
			}
		}
		else
		{
			// CPU-driven instances: upload the type-erased span to the per-frame ring.
			if (call.Instances.CpuData.IsEmpty)
				return;
			var bytes = call.Instances.CpuData;
			var gpuAddress = _instanceUpload.Upload(_slot, bytes);
			var instanceView = new VertexBufferView
			{
				BufferLocation = gpuAddress,
				SizeInBytes = (uint)bytes.Length,
				StrideInBytes = (uint)call.Instances.Stride,
			};
			_meshes.DrawInstanced(_cmd, call.Mesh!.Value.Id, instanceView, (uint)call.Instances.Count);
		}
	}

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: compute requires an active graph (SetGraph).");

		var baked = _techniques.GetOrBake(technique, ReadOnlySpan<DxgiFormat>.Empty, DxgiFormat.FormatUnknown, false);
		_cmd.SetComputeRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		if (push.Length > 0 && baked.RootConstantParam >= 0)
		{
			var block = stackalloc float[16];
			for (var i = 0; i < push.Length && i < 16; i++)
				block[i] = push[i];
			_cmd.SetComputeRoot32BitConstants((uint)baked.RootConstantParam, 16, block, 0);
		}

		// Sampled inputs → an SRV descriptor table (e.g. the auto-exposure compute metering the lit scene).
		if (sampled.Length > 0 && baked.TableParam >= 0)
		{
			var (cpu, gpu) = _ring.Allocate((uint)sampled.Length);
			for (var i = 0; i < sampled.Length; i++)
				WriteSrv(sampled[i].Id, _ring.Offset(cpu, (uint)i));
			_cmd.SetComputeRootDescriptorTable((uint)baked.TableParam, gpu);
		}

		// Storage (the graph put each resource in UNORDERED_ACCESS first), bound in declaration order to match the
		// pass's .ReadWrites (as Vulkan binds them after the sampled inputs):
		//  * a RAW buffer (the FXC built-in's ByteAddressBuffer, no reflected stride) binds as a single root UAV by
		//    address — that root signature reserves one root UAV, so this path is inherently one resource.
		//  * RW STRUCTURED buffers (the particle stream) and RW textures (the 1×1 exposure image) bind as a UAV
		//    descriptor table, one descriptor per resource at u0.. — the count the root signature reserved. A
		//    structured-buffer descriptor carries StructureByteStride + NumElements so the shader's GetDimensions
		//    reports the real count (a root UAV is a raw address with no metadata → GetDimensions 0 → the particle
		//    update early-outs every thread, writes nothing).
		if (storage.Length > 0 && baked.UavRootParam >= 0)
		{
			_cmd.SetComputeRootUnorderedAccessView((uint)baked.UavRootParam, _transients.Resource(_slot, storage[0].Id).GetGPUVirtualAddress());
		}
		else if (storage.Length > 0 && baked.UavTableParam >= 0)
		{
			var (cpu, gpu) = _ring.Allocate((uint)storage.Length);
			for (var i = 0; i < storage.Length; i++)
			{
				var id = storage[i].Id;
				UnorderedAccessViewDesc uav;
				if (storage[i].Kind == ResourceKind.Buffer)
				{
					var stride = (uint)baked.StorageBufferStride;
					uav = new UnorderedAccessViewDesc
					{
						Format = DxgiFormat.FormatUnknown, // a structured buffer is format-less; the stride defines the element
						ViewDimension = UavDimension.Buffer,
						Anonymous = new UnorderedAccessViewDescUnion
						{
							Buffer = new BufferUav { FirstElement = 0, NumElements = _transients.ByteSize(id) / stride, StructureByteStride = stride, CounterOffsetInBytes = 0, Flags = BufferUavFlags.None }
						}
					};
				}
				else
				{
					uav = new UnorderedAccessViewDesc
					{
						Format = _transients.SrvFormat(_slot, id),
						ViewDimension = UavDimension.Texture2D,
						Anonymous = new UnorderedAccessViewDescUnion { Texture2D = new Tex2DUav { MipSlice = 0, PlaneSlice = 0 } }
					};
				}
				_ctx.Device.CreateUnorderedAccessView(_transients.Resource(_slot, id), default(ComPtr<ID3D12Resource>), &uav, _ring.Offset(cpu, (uint)i));
			}
			_cmd.SetComputeRootDescriptorTable((uint)baked.UavTableParam, gpu);
		}

		_cmd.Dispatch(groups.X, groups.Y, groups.Z);
	}


	public void DispatchSkin(string technique, Mesh skinnedMesh, ReadOnlySpan<Matrix4x4> palette, BufferHandle output, int vertexCount)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: skinning requires an active graph (SetGraph).");
		if (vertexCount <= 0 || palette.Length == 0)
			return;

		var baked = _techniques.GetOrBake(technique, ReadOnlySpan<DxgiFormat>.Empty, DxgiFormat.FormatUnknown, false);
		_cmd.SetComputeRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		// vertexCount rides the compute root constant (as a float — exact for any realistic count).
		if (baked.RootConstantParam >= 0)
		{
			var count = (float)vertexCount;
			_cmd.SetComputeRoot32BitConstants((uint)baked.RootConstantParam, 1, &count, 0);
		}

		// All three buffers bind as root descriptors by GPU address — no descriptor heap. They are raw
		// ByteAddressBuffers (the skin shader Loads/Stores at explicit byte offsets), so the output is a root UAV
		// (stride 0): t0 = bind-pose verts, t1 = bone palette, u0 = output.
		var paletteBytes = MemoryMarshal.AsBytes(palette);
		var paletteAddress = _storageUpload.Upload(_slot, paletteBytes);
		_cmd.SetComputeRootShaderResourceView((uint)baked.InputBufferBaseParam, _meshes.BindPoseAddress(skinnedMesh.Id));
		_cmd.SetComputeRootShaderResourceView((uint)(baked.InputBufferBaseParam + 1), paletteAddress);
		_cmd.SetComputeRootUnorderedAccessView((uint)baked.UavRootParam, _transients.Resource(_slot, output.Id).GetGPUVirtualAddress());

		_cmd.Dispatch((uint)((vertexCount + 63) / 64), 1, 1);
	}


	// The compute pre-skin output as a per-vertex stream: a MeshStandard-strided view over the transient buffer.
	private VertexBufferView SkinnedView(BufferHandle skinnedVertices) => new()
	{
		BufferLocation = _transients!.Resource(_slot, skinnedVertices.Id).GetGPUVirtualAddress(),
		SizeInBytes = _transients.ByteSize(skinnedVertices.Id),
		StrideInBytes = (uint)VertexLayout.MeshStandard.Stride,
	};
}
