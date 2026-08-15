using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Graph;
using Silk.NET.Vulkan;

namespace Loom.Backend.Vulkan;

internal sealed unsafe partial class VulkanRenderContext
{
	// Bind sampled graph resources (transients) as combined image samplers — the path FullScreen and the instanced
	// light volumes share. The graph put each in ShaderReadOnly before this pass. Every destination slot comes from
	// the technique's reflected bindings, so the k-th supplied input lands on the k-th sampler slot the shader
	// declared whatever number that is. (The slots used to be counted out here as 0..N-1, then the storage buffers
	// and UBOs offset past `inputs.Length` — the count the *pass* supplied, not the count the *shader* declares, so
	// a pass supplying fewer inputs than the shader reflects shifted every following descriptor onto a wrong slot.)
	private void BindSampledInputs(VulkanTechnique baked, in ShaderBindingMap map, ReadOnlySpan<ResourceHandle> inputs, bool linear, ReadOnlySpan<Silk.NET.Vulkan.Buffer> storageBuffers = default, ReadOnlySpan<UniformBinding> ubos = default)
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
				DstBinding = (uint)map.SampledTextures[i].Index,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageInfos[i],
			};
		}
		// Read-only storage buffers (SSBOs), at the slots the shader gave them.
		for (var s = 0; s < storageBuffers.Length; s++)
		{
			bufferInfos[s] = new DescriptorBufferInfo { Buffer = storageBuffers[s], Offset = 0, Range = Vk.WholeSize };
			writes[w++] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)map.StorageBuffers[s].Index,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.StorageBuffer,
				PBufferInfo = &bufferInfos[s],
			};
		}
		// Uniform buffers, likewise at their declared slots.
		for (var u = 0; u < ubos.Length; u++)
		{
			bufferInfos[storageBuffers.Length + u] = ubos[u].Info;
			writes[w++] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)map.UniformBuffers[u].Index,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.UniformBuffer,
				PBufferInfo = &bufferInfos[storageBuffers.Length + u],
			};
		}
		_vk.UpdateDescriptorSets(_ctx.Device, (uint)count, writes, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);
	}

	// Bind a material's loaded textures (VulkanTextureStore views) into the technique's reflected sampler slots, with
	// the technique's address mode + filter. Shared by Draw(mesh, material), textured instancing, and textured
	// particles. The caller checks SampleCount > 0 (or any UBOs). The technique's uniform buffers go to their own
	// declared slots, in slot order (a pass's .Reads constants, then the per-draw block — e.g. a view-projection then
	// the model, or foliage's wind, read in the vertex stage).
	private void BindMaterialTextures(VulkanTechnique baked, VulkanTechniqueTemplate template, Material material, string technique, ReadOnlySpan<UniformBinding> ubos = default)
	{
		_materials.ValidateAgainst(material, technique);
		var textures = _materials.TexturesOf(material.Id);

		var sampler = template.Address switch
		{
			SamplerAddress.Clamp => template.LinearFilter ? _linearSampler : _nearestSampler,
			SamplerAddress.Repeat => template.LinearFilter ? _repeatLinearSampler : _repeatNearestSampler,
			SamplerAddress.MirrorRepeat => template.LinearFilter ? _mirrorLinearSampler : _mirrorNearestSampler,
			SamplerAddress.ClampToBorder => template.LinearFilter ? _borderLinearSampler : _borderNearestSampler,
			SamplerAddress.MirrorClampToEdge => template.LinearFilter ? _mirrorEdgeLinearSampler : _mirrorEdgeNearestSampler,
			_ => throw new VulkanException($"SamplerAddress '{template.Address}' is not mapped to a sampler."),
		};
		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var count = template.SampleCount + ubos.Length;
		var imageInfos = stackalloc DescriptorImageInfo[template.SampleCount];
		var bufferInfos = stackalloc DescriptorBufferInfo[ubos.Length];
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
				DstBinding = (uint)template.Bindings.SampledTextures[i].Index,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageInfos[i],
			};
		}
		// Uniform buffers, at the slots the shader declared, in slot order.
		for (var u = 0; u < ubos.Length; u++)
		{
			bufferInfos[u] = ubos[u].Info;
			writes[template.SampleCount + u] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)template.Bindings.UniformBuffers[u].Index,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.UniformBuffer,
				PBufferInfo = &bufferInfos[u],
			};
		}
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

	// Stage a per-frame uniform block (raw bytes matching the technique's cbuffer) into the uniform ring and
	// resolve it to a bindable descriptor.
	private UniformBinding UploadUniforms(ReadOnlySpan<byte> bytes)
	{
		var (buffer, offset) = _uniformUpload.Upload(_frameSlot, bytes);
		return new UniformBinding(buffer, offset, (ulong)bytes.Length);
	}

	/// <summary>
	/// Fill a draw's uniform slots and return how many were filled. Slot order is the shader's: the slot reflection
	/// identified as the per-material block takes the material's parameter bytes, and the remaining slots take the
	/// pass's <c>.Reads</c> constants in order, then the per-draw block.
	/// <para>
	/// The material block is staged into the same per-frame uniform ring as the per-draw block, so a material needs no
	/// GPU buffer of its own and carries no frames-in-flight hazard — the bytes are copied into this frame's slice
	/// during recording, which is also what makes <c>IEngine.Write(material, …)</c> take effect on the next draw.
	/// </para>
	/// </summary>
	private int FillUniformSlots(Span<UniformBinding> ubos, in DrawCall call, in VulkanTechniqueTemplate template)
	{
		var materialSlot = call.Material is not null ? template.MaterialUniformSlot : null;
		var drawBlock = call.Uniforms.Length > 0;
		// Counting the slots up front (rather than appending) is what guarantees the material's slot is reached even
		// when it sits past the constants — appending would stop short and leave the shader reading an unbound slot.
		var total = Math.Min(_activeConstantsCount + (drawBlock ? 1 : 0) + (materialSlot.HasValue ? 1 : 0), ubos.Length);

		var constant = 0;
		for (var slot = 0; slot < total; slot++)
		{
			if (slot == materialSlot)
				ubos[slot] = UploadUniforms(_materials.ParametersOf(call.Material!.Value.Id));
			else if (constant < _activeConstantsCount)
				ubos[slot] = new UniformBinding(_resources.GetConstantBuffer(_frameSlot, _activeConstantsIds[constant++]), 0, Vk.WholeSize);
			else if (drawBlock)
			{
				ubos[slot] = UploadUniforms(call.Uniforms);
				drawBlock = false;
			}
		}
		return total;
	}
}
