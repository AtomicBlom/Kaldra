using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Silk.NET.Vulkan;

namespace Loom.Backend.Vulkan;

internal sealed unsafe partial class VulkanRenderContext
{
	public void Draw(in DrawCall call)
	{
		var baked = Bake(call.Technique);
		var template = _registry[call.Technique];
		_validate.Draw(call);
		BindPipeline(PipelineBindPoint.Graphics, baked.Pipeline);

		// Gather the technique's uniform buffers in slot order. The slot reflection named as the material block (when
		// the draw carries a material) takes that slot; every other slot takes the next of the pass's .Reads constants
		// (e.g. a view-projection), then the per-draw Uniforms block (model, object data). Both material and sampled
		// draws bind them.
		Span<UniformBinding> ubos = stackalloc UniformBinding[MaxUniformBuffers];
		var uboCount = FillUniformSlots(ubos, call, template);

		// The storage buffers the pass .Reads, as many as the technique declares, in the order the pass read them
		Span<Silk.NET.Vulkan.Buffer> storageBuffers = stackalloc Silk.NET.Vulkan.Buffer[MaxStorageBuffers];
		var storageCount = PassStorage(storageBuffers, template.Bindings, call.Technique);

		// Material textures (+ those UBOs) or sampled graph resources (+ those UBOs and storage buffers) — mutually
		// exclusive texture source. An empty combination leaves the draw unbound (the depth-only path with no constants).
		if (call.Material is { } material)
		{
			if (storageCount > 0)
				throw new VulkanException($"Technique '{call.Technique}' reads a storage buffer and draws with a material; a material draw binds no storage buffers.");
			if (template.SampleCount > 0 || uboCount > 0)
				BindMaterialTextures(baked, template, material, call.Technique, ubos[..uboCount]);
		}
		else if (!call.Sampled.IsEmpty || uboCount > 0 || storageCount > 0)
		{
			BindSampledInputs(baked, template.Bindings, call.Sampled, template.LinearFilter, storageBuffers[..storageCount], ubos[..uboCount], template.SampleCompare);
		}

		if (call.Instances.IndirectBuffer is { } args)
		{
			// The counts live in the buffer; the CPU supplies only where to read them. The graph put the buffer in
			// IndirectArgument before this pass, so the compute that filled it has completed.
			_meshes.DrawIndirect(_cmd, _validate.RequireMesh(call).Id,
				_resources.GetBuffer(_frameSlot, args.Id), (ulong)call.Instances.IndirectOffset);
		}
		else if (call.Instances.IsEmpty)
		{
			// Single-instance draw: skinned override or standard mesh.
			if (call.SkinnedVertices is { } sv)
			{
				var vertexBuffer = _resources.GetBuffer(_frameSlot, sv.Id);
				_meshes.DrawSkinned(_cmd, _validate.RequireMesh(call).Id, vertexBuffer);
			}
			else
			{
				_meshes.Draw(_cmd, _validate.RequireMesh(call).Id, call.Indices);
			}
		}
		else if (call.Instances.GpuBuffer is { } gpuBuf)
		{
			gpuBuf.CheckInstanceStride(template.Instance?.Stride ?? 0, call.Technique);
			var gpuBuffer = _resources.GetBuffer(_frameSlot, gpuBuf.Id);
			if (call.Mesh is { } mesh)
			{
				// GPU-resident instance stream + indexed mesh draw (foliage, GPU-driven placement).
				_meshes.DrawInstanced(_cmd, mesh.Id, gpuBuffer, 0ul, (uint)call.Instances.Count, call.Indices);
			}
			else
			{
				// Procedural: GPU buffer IS the per-instance vertex stream; vertexCount verts per instance
				// fabricated from SV_VertexID (the GPU-particle path).
				ulong offset = 0;
				_meshes.ResetBindings(); // binding 0 becomes the instance stream, not a mesh's vertices
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
			_meshes.DrawInstanced(_cmd, _validate.RequireMesh(call).Id, buffer, offset, (uint)call.Instances.Count, call.Indices);
		}
	}

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
	{
		var baked = Bake(technique);
		_validate.Dispatch(technique, sampled, storage);
		BindPipeline(PipelineBindPoint.Compute, baked.Pipeline);

		if (push.Length > 0)
		{
			// A compute push always rides the scalar block — there is no free-uniform-slot route here.
			_validate.PushFits(technique, push.Length * sizeof(float), routesToUniform: false, 0, VulkanTechnique.FragmentPushSize);
			var block = stackalloc float[16];
			new Span<float>(block, 16).Clear();
			push.CopyTo(new Span<float>(block, push.Length));
			_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.ComputeBit, 0, (uint)VulkanTechnique.FragmentPushSize, block);
		}

		var total = sampled.Length + storage.Length;
		if (total > 0)
		{
			// Sampled inputs use the technique's declared filter (matching the Draw/FullScreen path and DirectX12's
			// static sampler) — not a hardcoded nearest, which silently diverged from DirectX12 for a LinearFilter pass.
			var template = _registry[technique];
			var sampler = template.LinearFilter ? _linearSampler : _nearestSampler;
			// Each descriptor's slot and its type come from the same reflected binding, so they cannot disagree.
			// They could before: the slot was this loop's index while the type was chosen from the runtime handle's
			// kind, and the layout groups every storage image ahead of every storage buffer — so a pass that supplied
			// a Buffer before an Image wrote a StorageBuffer descriptor into a slot the layout declared as a
			// StorageImage. No shipped pass supplies them in that order, which is why nothing had caught it.
			var map = template.Bindings;
			var set = _descriptors.Allocate(baked.DescriptorSetLayout);
			var imageInfos = stackalloc DescriptorImageInfo[total];
			var bufferInfos = stackalloc DescriptorBufferInfo[total];
			var writes = stackalloc WriteDescriptorSet[total];
			int nextImage = 0, nextBuffer = 0;
			for (var i = 0; i < total; i++)
			{
				var write = new WriteDescriptorSet
				{
					SType = StructureType.WriteDescriptorSet,
					DstSet = set,
					DescriptorCount = 1,
				};
				if (i < sampled.Length)
				{
					ref var image = ref _resources.Get(_frameSlot, sampled[i].Id);
					imageInfos[i] = new DescriptorImageInfo { Sampler = sampler, ImageView = image.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
					write.DstBinding = (uint)map.SampledTextures[i].Index;
					write.DescriptorType = DescriptorType.CombinedImageSampler;
					write.PImageInfo = &imageInfos[i];
				}
				else
				{
					var handle = storage[i - sampled.Length];
					if (handle.Kind == ResourceKind.Buffer)
					{
						bufferInfos[i] = new DescriptorBufferInfo { Buffer = _resources.GetBuffer(_frameSlot, handle.Id), Offset = 0, Range = Vk.WholeSize };
						write.DstBinding = (uint)map.StorageBuffers[nextBuffer++].Index;
						write.DescriptorType = DescriptorType.StorageBuffer;
						write.PBufferInfo = &bufferInfos[i];
					}
					else
					{
						ref var image = ref _resources.Get(_frameSlot, handle.Id);
						imageInfos[i] = new DescriptorImageInfo { ImageView = image.View, ImageLayout = ImageLayout.General };
						write.DstBinding = (uint)map.StorageImages[nextImage++].Index;
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
		BindPipeline(PipelineBindPoint.Compute, baked.Pipeline);

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
