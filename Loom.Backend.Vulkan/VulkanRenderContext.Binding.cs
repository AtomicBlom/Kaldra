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
		var count = inputs.Length + storageBuffers.Length + ubos.Length;
		var imageInfos = stackalloc DescriptorImageInfo[inputs.Length];
		var bufferInfos = stackalloc DescriptorBufferInfo[storageBuffers.Length + ubos.Length];
		var writes = stackalloc WriteDescriptorSet[count];
		var key = new BindingKey(baked.DescriptorSetLayout.Handle, sampler.Handle);
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
				DstBinding = (uint)map.SampledTextures[i].Index,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageInfos[i],
			};
			key.Add(map.SampledTextures[i].Index, BoundObject.View, image.View.Handle);
		}
		// Read-only storage buffers (SSBOs), at the slots the shader gave them.
		for (var s = 0; s < storageBuffers.Length; s++)
		{
			bufferInfos[s] = new DescriptorBufferInfo { Buffer = storageBuffers[s], Offset = 0, Range = Vk.WholeSize };
			writes[w++] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstBinding = (uint)map.StorageBuffers[s].Index,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.StorageBuffer,
				PBufferInfo = &bufferInfos[s],
			};
			key.Add(map.StorageBuffers[s].Index, BoundObject.Buffer, storageBuffers[s].Handle);
		}
		// Uniform buffers, likewise at their declared slots — dynamic, so the descriptor names only the buffer and
		// the window size and the byte offset is supplied at bind time.
		for (var u = 0; u < ubos.Length; u++)
		{
			bufferInfos[storageBuffers.Length + u] = ubos[u].Info;
			writes[w++] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstBinding = (uint)map.UniformBuffers[u].Index,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.UniformBufferDynamic,
				PBufferInfo = &bufferInfos[storageBuffers.Length + u],
			};
			key.Add(map.UniformBuffers[u].Index, BoundObject.Buffer, ubos[u].Info.Buffer.Handle, ubos[u].Info.Range);
		}

		var set = GetOrCreateSet(baked.DescriptorSetLayout, in key, writes, count);
		BindSet(PipelineBindPoint.Graphics, baked.Layout, set, ubos);
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
		var count = template.SampleCount + ubos.Length;
		var imageInfos = stackalloc DescriptorImageInfo[template.SampleCount];
		var bufferInfos = stackalloc DescriptorBufferInfo[ubos.Length];
		var writes = stackalloc WriteDescriptorSet[count];
		var key = new BindingKey(baked.DescriptorSetLayout.Handle, sampler.Handle);
		for (var i = 0; i < template.SampleCount; i++)
		{
			var view = _textures.View(textures[i]);
			imageInfos[i] = new DescriptorImageInfo
			{
				Sampler = sampler,
				ImageView = view,
				ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
			};
			writes[i] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstBinding = (uint)template.Bindings.SampledTextures[i].Index,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageInfos[i],
			};
			key.Add(template.Bindings.SampledTextures[i].Index, BoundObject.View, view.Handle);
		}
		// Uniform buffers, at the slots the shader declared, in slot order.
		for (var u = 0; u < ubos.Length; u++)
		{
			bufferInfos[u] = ubos[u].Info;
			writes[template.SampleCount + u] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstBinding = (uint)template.Bindings.UniformBuffers[u].Index,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.UniformBufferDynamic,
				PBufferInfo = &bufferInfos[u],
			};
			key.Add(template.Bindings.UniformBuffers[u].Index, BoundObject.Buffer, ubos[u].Info.Buffer.Handle, ubos[u].Info.Range);
		}

		var set = GetOrCreateSet(baked.DescriptorSetLayout, in key, writes, count);
		BindSet(PipelineBindPoint.Graphics, baked.Layout, set, ubos);
	}

	// Record the bind with this draw's dynamic uniform offsets. Vulkan wants pDynamicOffsets ordered by binding
	// number; uniform buffers are the only dynamic type in a Loom layout and ShaderBindingMap keeps each category
	// ascending by index, so `ubos` order already is binding order — no sort, and none to keep in step.
	private void BindSet(PipelineBindPoint bindPoint, PipelineLayout layout, DescriptorSet set, ReadOnlySpan<UniformBinding> ubos)
	{
		var offsets = stackalloc uint[MaxUniformBuffers];
		for (var i = 0; i < ubos.Length; i++)
			offsets[i] = ubos[i].DynamicOffset;
		_vk.CmdBindDescriptorSets(_cmd, bindPoint, layout, 0, 1, in set, (uint)ubos.Length, offsets);
	}

	/// <summary>
	/// Reuse — or write, once — the descriptor set these writes describe. The set is keyed by the GPU objects it
	/// names, so a hit is guaranteed to describe the same images, sampler and buffers; only the per-draw uniform
	/// offsets, which ride <c>pDynamicOffsets</c> rather than the descriptors, differ between the draws that share
	/// it. That is what removes the allocate-write-bind per draw that made Vulkan slower per draw than DirectX 12,
	/// which has always re-bound a persistent SRV table.
	/// <para>
	/// Keying on the handles rather than on <c>(material, technique)</c> is deliberate: a Vulkan set carries the
	/// technique's uniform buffers alongside the material's images, so the same material drawn in two passes names
	/// different <c>.Reads</c> constants, and drawn in two frame slots names different ring buffers. Handles
	/// subsume material, technique, pass and frame slot in one key.
	/// </para>
	/// <para>
	/// <b>Invariant:</b> nothing a cached set names may be destroyed while the set is cached (see
	/// <see cref="BindingCache"/>). The ring buffers cannot die at all (see
	/// <see cref="VulkanInstanceUploadBuffer"/>). Transient image views, samplers and set layouts die only at graph swap,
	/// resize and shader hot-reload, all of which call <see cref="FlushDescriptorCache"/>. Texture views and mesh
	/// buffers die one at a time when the app releases them, which calls <see cref="EvictBindings"/>. A further place
	/// that destroys an image view or a buffer would rot this cache silently, so it needs one of the two as well.
	/// </para>
	/// </summary>
	private DescriptorSet GetOrCreateSet(DescriptorSetLayout layout, in BindingKey key, WriteDescriptorSet* writes, int count)
	{
		if (_cachedSets.TryGetValue(key, out var cached))
			return new DescriptorSet(cached);

		var set = _descriptors.AllocatePersistent(layout);
		for (var i = 0; i < count; i++)
			writes[i].DstSet = set;
		_vk.UpdateDescriptorSets(_ctx.Device, (uint)count, writes, 0, null);
		_cachedSets.Add(key, set.Handle);
		return set;
	}

	/// <summary>Drop every cached set and free the sets behind them. Called at the drained moments where something a
	/// set names is about to be destroyed: graph swap, present-target resize, and shader hot-reload.</summary>
	public void FlushDescriptorCache()
	{
		_cachedSets.Clear();
		_descriptors.ResetPersistent();
	}

	/// <summary>
	/// Drop every cached set naming the <paramref name="kind"/> object <paramref name="handle"/>, which the app has just
	/// released, and free each one after frame <paramref name="lastReadingFrame"/> — the frame the released object itself
	/// waits for, since a frame in flight may still bind the set. Called before the object is destroyed, while the
	/// handle is still its own: once the driver reuses it, a key still naming it would match the next object to get it.
	/// <para>
	/// Usually there is nothing to drop. A material holds its textures for its whole life, and the draw-list and skinning
	/// paths write per-frame sets, so a released object reaches this cache only through a path that caches what it
	/// binds without pinning it — but such a path is one edit away, and this is what keeps it correct.
	/// </para>
	/// </summary>
	public void EvictBindings(BoundObject kind, ulong handle, ulong lastReadingFrame)
	{
		_cachedSets.Evict(kind, handle, _evictedSets);
		foreach (var set in _evictedSets)
			_descriptors.RetirePersistent(new DescriptorSet(set), lastReadingFrame);
		_evictedSets.Clear();
	}

	// A present per-frame uniform buffer to bind as one of a technique's descriptors: the buffer and the window size
	// as a DescriptorBufferInfo (offset always 0), plus the byte offset within it that rides pDynamicOffsets.
	// Copying Info to a local before taking its address keeps the pointer valid through vkUpdateDescriptorSets.
	private readonly struct UniformBinding
	{
		public readonly DescriptorBufferInfo Info;
		public readonly uint DynamicOffset;

		/// <param name="buffer">The buffer the descriptor names — a graph Constants buffer, or a block of the uniform ring.</param>
		/// <param name="range">The window the shader reads. <see cref="Vk.WholeSize"/> requires a zero dynamic offset,
		/// which is what a whole-buffer Constants binding passes.</param>
		/// <param name="dynamicOffset">Byte offset within <paramref name="buffer"/>, supplied at bind time. Always a
		/// multiple of the ring's 256-byte alignment, which satisfies <c>minUniformBufferOffsetAlignment</c> on any device.</param>
		public UniformBinding(Silk.NET.Vulkan.Buffer buffer, ulong range, uint dynamicOffset)
		{
			Info = new DescriptorBufferInfo { Buffer = buffer, Offset = 0, Range = range };
			DynamicOffset = dynamicOffset;
		}
	}

	// Stage a per-frame uniform block (raw bytes matching the technique's cbuffer) into the uniform ring and
	// resolve it to a bindable descriptor. The descriptor names the block's whole buffer; the offset is dynamic, so
	// two draws staging different bytes of the same size share one descriptor set.
	private UniformBinding UploadUniforms(ReadOnlySpan<byte> bytes)
	{
		var (buffer, offset) = _uniformUpload.Upload(_frameSlot, bytes);
		return new UniformBinding(buffer, (ulong)bytes.Length, (uint)offset);
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
	/// <para>
	/// <em>Every</em> slot the technique declares is filled, not just the supplied ones: with dynamic uniforms the
	/// bind must supply exactly one offset per dynamic descriptor <em>in the layout</em>, so a short fill would be a
	/// validation error rather than merely an unread slot. A slot with nothing to put in it gets a defined 16-byte
	/// window of the ring.
	/// </para>
	/// </summary>
	private int FillUniformSlots(Span<UniformBinding> ubos, in DrawCall call, in VulkanTechniqueTemplate template)
	{
		var materialSlot = call.Material is not null ? template.MaterialUniformSlot : null;
		var drawBlock = call.Uniforms.Length > 0;
		var total = Math.Min(template.UniformBufferCount, ubos.Length);

		var constant = 0;
		for (var slot = 0; slot < total; slot++)
		{
			if (slot == materialSlot)
				ubos[slot] = UploadUniforms(_materials.ParametersOf(call.Material!.Value.Id));
			else if (constant < _activeConstantsCount)
				ubos[slot] = new UniformBinding(_resources.GetConstantBuffer(_frameSlot, _activeConstantsIds[constant++]), Vk.WholeSize, 0);
			else if (drawBlock)
			{
				ubos[slot] = UploadUniforms(call.Uniforms);
				drawBlock = false;
			}
			else
				ubos[slot] = _nullUniform;
		}
		return total;
	}
}
