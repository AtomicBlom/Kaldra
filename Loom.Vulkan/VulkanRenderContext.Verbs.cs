using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Graph;
using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

internal sealed unsafe partial class VulkanRenderContext
{
	public void Draw(in DrawCall call)
	{
		var effectiveView = call.View ?? _view;
		var baked = Bake(call.Technique);
		var template = _registry[call.Technique];
		var mvp = effectiveView.ViewProjectionMatrix;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);

		// Bind material textures (+ optional UBO) or sampled graph resources — mutually exclusive.
		if (call.Material is { } material)
		{
			var ubo = call.Uniforms.Length > 0 ? UploadUniforms(call.Uniforms) : default;
			if (template.SampleCount > 0 || ubo.Present)
				BindMaterialTextures(baked, template, material, call.Technique, ubo);
		}
		else if (!call.Sampled.IsEmpty)
		{
			BindSampledInputs(baked, call.Sampled, template.LinearFilter);
		}

		if (call.Instances.IsEmpty)
		{
			// Single-instance draw: skinned override or standard mesh.
			if (call.SkinnedVertices is { } sv)
			{
				var vertexBuffer = _resources.GetBuffer(_frameSlot, sv.Id);
				_meshes.DrawSkinned(_cmd, call.Mesh!.Value.Id, vertexBuffer);
			}
			else
			{
				_meshes.Draw(_cmd, call.Mesh!.Value.Id);
			}
		}
		else if (call.Instances.GpuBuffer is { } gpuBuf)
		{
			var gpuBuffer = _resources.GetBuffer(_frameSlot, gpuBuf.Id);
			if (call.Mesh is { } mesh)
			{
				// GPU-resident instance stream + indexed mesh draw (foliage, GPU-driven placement).
				_meshes.DrawInstanced(_cmd, mesh.Id, gpuBuffer, 0ul, (uint)call.Instances.Count);
			}
			else
			{
				// Procedural: GPU buffer IS the per-instance vertex stream; vertexCount verts per instance
				// fabricated from SV_VertexID (the GPU-particle path).
				ulong offset = 0;
				_vk.CmdBindVertexBuffers(_cmd, 0, 1, in gpuBuffer, in offset);
				_vk.CmdDraw(_cmd, (uint)call.VertexCount, (uint)call.Instances.Count, 0, 0);
			}
		}
		else
		{
			// CPU-driven instances: upload the type-erased span to the per-frame ring.
			if (call.Instances.CpuData.IsEmpty)
				return;
			var (buffer, offset) = _instanceUpload.Upload(_frameSlot, call.Instances.CpuData);
			_meshes.DrawInstanced(_cmd, call.Mesh!.Value.Id, buffer, offset, (uint)call.Instances.Count);
		}
	}

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
	{
		var baked = Bake(technique);
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Compute, baked.Pipeline);

		if (push.Length > 0)
		{
			var block = stackalloc float[16];
			for (var i = 0; i < push.Length && i < 16; i++)
				block[i] = push[i];
			_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.ComputeBit, 0, (uint)VulkanTechnique.FragmentPushSize, block);
		}

		var total = sampled.Length + storage.Length;
		if (total > 0)
		{
			// Sampled inputs use the technique's declared filter (matching the Draw/FullScreen path and DirectX12's
			// static sampler) — not a hardcoded nearest, which silently diverged from DirectX12 for a LinearFilter pass.
			var sampler = _registry[technique].LinearFilter ? _linearSampler : _nearestSampler;
			// Binding order matches the layout: sampled inputs (combined image samplers, ShaderReadOnly), then
			// storage resources — a storage IMAGE (GENERAL) or, for a Buffer-kind resource, a storage BUFFER.
			var set = _descriptors.Allocate(baked.DescriptorSetLayout);
			var imageInfos = stackalloc DescriptorImageInfo[total];
			var bufferInfos = stackalloc DescriptorBufferInfo[total];
			var writes = stackalloc WriteDescriptorSet[total];
			for (var i = 0; i < total; i++)
			{
				var write = new WriteDescriptorSet
				{
					SType = StructureType.WriteDescriptorSet,
					DstSet = set,
					DstBinding = (uint)i,
					DescriptorCount = 1,
				};
				if (i < sampled.Length)
				{
					ref var image = ref _resources.Get(_frameSlot, sampled[i].Id);
					imageInfos[i] = new DescriptorImageInfo { Sampler = sampler, ImageView = image.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
					write.DescriptorType = DescriptorType.CombinedImageSampler;
					write.PImageInfo = &imageInfos[i];
				}
				else
				{
					var handle = storage[i - sampled.Length];
					if (handle.Kind == ResourceKind.Buffer)
					{
						bufferInfos[i] = new DescriptorBufferInfo { Buffer = _resources.GetBuffer(_frameSlot, handle.Id), Offset = 0, Range = Vk.WholeSize };
						write.DescriptorType = DescriptorType.StorageBuffer;
						write.PBufferInfo = &bufferInfos[i];
					}
					else
					{
						ref var image = ref _resources.Get(_frameSlot, handle.Id);
						imageInfos[i] = new DescriptorImageInfo { ImageView = image.View, ImageLayout = ImageLayout.General };
						write.DescriptorType = DescriptorType.StorageImage;
						write.PImageInfo = &imageInfos[i];
					}
				}
				writes[i] = write;
			}
			_vk.UpdateDescriptorSets(_ctx.Device, (uint)total, writes, 0, null);
			_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Compute, baked.Layout, 0, 1, in set, 0, null);
		}

		_vk.CmdDispatch(_cmd, groups.X, groups.Y, groups.Z);
	}


	public void DispatchSkin(string technique, Mesh skinnedMesh, ReadOnlySpan<Matrix4x4> palette, BufferHandle output, int vertexCount)
	{
		if (vertexCount <= 0 || palette.Length == 0)
			return;
		var baked = Bake(technique);
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Compute, baked.Pipeline);

		// vertexCount rides the compute scalar push (as a float — exact for any realistic count).
		var block = stackalloc float[16];
		block[0] = vertexCount;
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.ComputeBit, 0, (uint)VulkanTechnique.FragmentPushSize, block);

		// Three storage buffers at bindings 0,1,2: bind-pose (mesh, long-lived) · palette (per-frame ring) · output
		// (the transient the pass .ReadWrites). None but the output is a graph resource, so DispatchSkin writes its
		// own set rather than going through the generic Dispatch storage path.
		var paletteBytes = MemoryMarshal.AsBytes(palette);
		var (paletteBuffer, paletteOffset) = _storageUpload.Upload(_frameSlot, paletteBytes);
		var inBuffer = _meshes.BindPoseBuffer(skinnedMesh.Id);
		var outBuffer = _resources.GetBuffer(_frameSlot, output.Id);

		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var bufferInfos = stackalloc DescriptorBufferInfo[3];
		bufferInfos[0] = new DescriptorBufferInfo { Buffer = inBuffer, Offset = 0, Range = Vk.WholeSize };
		bufferInfos[1] = new DescriptorBufferInfo { Buffer = paletteBuffer, Offset = paletteOffset, Range = (ulong)paletteBytes.Length };
		bufferInfos[2] = new DescriptorBufferInfo { Buffer = outBuffer, Offset = 0, Range = Vk.WholeSize };
		var writes = stackalloc WriteDescriptorSet[3];
		for (var i = 0; i < 3; i++)
			writes[i] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)i,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.StorageBuffer,
				PBufferInfo = &bufferInfos[i],
			};
		_vk.UpdateDescriptorSets(_ctx.Device, 3, writes, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Compute, baked.Layout, 0, 1, in set, 0, null);

		_vk.CmdDispatch(_cmd, (uint)((vertexCount + 63) / 64), 1, 1);
	}
}
