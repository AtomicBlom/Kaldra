using System.Runtime.InteropServices;

using Loom.Core.Graph;
using Loom.Core.Materials;
using Silk.NET.Vulkan;

namespace Loom.Vulkan;

internal sealed unsafe partial class VulkanRenderContext
{
	// Bind sampled graph resources (transients) as combined image samplers at bindings 0..N-1 — the path
	// FullScreen and the instanced light volumes share. The graph put each in ShaderReadOnly before this pass.
	// A present uniform buffer is written as the last binding (index inputs.Length), matching the set
	// layout's category order (samplers, then the UBO).
	private void BindSampledInputs(VulkanTechnique baked, ReadOnlySpan<ResourceHandle> inputs, bool linear, ReadOnlySpan<Silk.NET.Vulkan.Buffer> storageBuffers = default, ReadOnlySpan<UniformBinding> ubos = default)
	{
		var sampler = linear ? _linearSampler : _nearestSampler;
		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var count = inputs.Length + storageBuffers.Length + ubos.Length;
		var imageInfos = stackalloc DescriptorImageInfo[inputs.Length];
		var bufferInfos = stackalloc DescriptorBufferInfo[storageBuffers.Length + ubos.Length];
		var writes = stackalloc WriteDescriptorSet[count];
		var w = 0;
		for (var i = 0; i < inputs.Length; i++)
		{
			ref var image = ref _resources.Get(_frameSlot, inputs[i].Id);
			imageInfos[i] = new DescriptorImageInfo
			{
				Sampler = sampler,
				ImageView = image.View,
				ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
			};
			writes[w++] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)i,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageInfos[i],
			};
		}
		// Read-only storage buffers (SSBOs) follow the samplers, as StorageBuffer descriptors.
		for (var s = 0; s < storageBuffers.Length; s++)
		{
			bufferInfos[s] = new DescriptorBufferInfo { Buffer = storageBuffers[s], Offset = 0, Range = Vk.WholeSize };
			writes[w++] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)(inputs.Length + s),
				DescriptorCount = 1,
				DescriptorType = DescriptorType.StorageBuffer,
				PBufferInfo = &bufferInfos[s],
			};
		}
		// Uniform buffers follow the storage buffers.
		for (var u = 0; u < ubos.Length; u++)
		{
			bufferInfos[storageBuffers.Length + u] = ubos[u].Info;
			writes[w++] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)(inputs.Length + storageBuffers.Length + u),
				DescriptorCount = 1,
				DescriptorType = DescriptorType.UniformBuffer,
				PBufferInfo = &bufferInfos[storageBuffers.Length + u],
			};
		}
		_vk.UpdateDescriptorSets(_ctx.Device, (uint)count, writes, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);
	}

	// Bind a material's loaded textures (VulkanTextureStore views) into the technique's reflected sampler slots 0..N-1,
	// with the technique's address mode + filter. Shared by Draw(mesh, material), textured instancing, and
	// textured particles. The caller checks SampleCount > 0 (or a present UBO). A present uniform buffer
	// is written as the last binding (index SampleCount) — e.g. foliage's wind, read in the vertex stage.
	private void BindMaterialTextures(VulkanTechnique baked, VulkanTechniqueTemplate template, Material material, string technique, in UniformBinding ubo = default)
	{
		var textures = _materials.TexturesOf(material.Id);
		if (textures.Length < template.SampleCount)
			throw new VulkanException($"Material supplies {textures.Length} textures but technique '{technique}' samples {template.SampleCount}.");

		var sampler = template.Address switch
		{
			SamplerAddress.Repeat => template.LinearFilter ? _repeatLinearSampler : _repeatNearestSampler,
			SamplerAddress.Clamp => template.LinearFilter ? _linearSampler : _nearestSampler,
			_ => throw new VulkanException($"SamplerAddress '{template.Address}' is not mapped to a sampler."),
		};
		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var count = template.SampleCount + (ubo.Present ? 1 : 0);
		var imageInfos = stackalloc DescriptorImageInfo[template.SampleCount];
		var bufferInfo = ubo.Info;
		var writes = stackalloc WriteDescriptorSet[count];
		for (var i = 0; i < template.SampleCount; i++)
		{
			imageInfos[i] = new DescriptorImageInfo
			{
				Sampler = sampler,
				ImageView = _textures.View(textures[i]),
				ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
			};
			writes[i] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)i,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageInfos[i],
			};
		}
		if (ubo.Present)
			writes[template.SampleCount] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)template.SampleCount,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.UniformBuffer,
				PBufferInfo = &bufferInfo,
			};
		_vk.UpdateDescriptorSets(_ctx.Device, (uint)count, writes, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);
	}

	// A present per-frame uniform buffer to bind as a technique's last descriptor — the resolved
	// DescriptorBufferInfo plus a presence flag (default => absent). Copying Info to a local before taking its
	// address keeps the pointer valid through vkUpdateDescriptorSets.
	private readonly struct UniformBinding
	{
		public readonly DescriptorBufferInfo Info;
		public readonly bool Present;

		public UniformBinding(Silk.NET.Vulkan.Buffer buffer, ulong offset, ulong range)
		{
			Info = new DescriptorBufferInfo { Buffer = buffer, Offset = offset, Range = range };
			Present = true;
		}
	}

	// Stage a per-frame uniform block into the uniform ring and resolve it to a bindable descriptor.
	private UniformBinding UploadUniforms(ReadOnlySpan<float> data)
	{
		var bytes = MemoryMarshal.AsBytes(data);
		var (buffer, offset) = _uniformUpload.Upload(_frameSlot, bytes);
		return new UniformBinding(buffer, offset, (ulong)bytes.Length);
	}
}
