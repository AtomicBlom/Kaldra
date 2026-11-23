using System.Numerics;
using System.Runtime.InteropServices;
using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Graph;
using Silk.NET.Vulkan;

namespace BinaryVibrance.Kaldra.Backend.Vulkan;

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
		else if (call.Instances.StaticBuffer is { } stored)
		{
			// Instances uploaded once: the stored buffer itself, from the draw's first instance.
			stored.CheckInstanceStride(template.Instance?.Stride ?? 0, call.Technique);
			var buffer = _meshes.VertexBuffer(stored.Storage.Id);
			_meshes.DrawInstanced(_cmd, _validate.RequireMesh(call).Id, buffer, (ulong)call.Instances.StaticFirst * (ulong)stored.Stride,
				(uint)call.Instances.Count, call.Indices);
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

		// The constant buffers the technique declares take the pass's .Reads Constants, in order, as a draw's do. One
		// that declares more than the pass reads would dispatch with a slot unbound, so it throws instead.
		var template = _registry[technique];
		var uniforms = template.Bindings.UniformBuffers.Length;
		if (uniforms > _activeConstantsCount)
			throw new InvalidOperationException($"Vulkan: compute technique '{technique}' declares {uniforms} constant buffer(s) but its pass .Reads {_activeConstantsCount}; the pass must .Reads each one the technique reads.");
		// The read-only storage buffers it declares take the pass's .Reads Storage, in order, as a draw's do; the
		// read-write ones take the dispatch's own storage buffers, in order. Each throws where it would leave a slot unbound.
		var map = template.Bindings;
		int readOnly = 0, readWrite = 0;
		foreach (var binding in map.StorageBuffers)
			if (binding.Kind == ShaderBindingKind.StorageBufferReadOnly) readOnly++; else readWrite++;
		if (readOnly > _activeStorageCount)
			throw new InvalidOperationException($"Vulkan: compute technique '{technique}' declares {readOnly} read-only storage buffer(s) but its pass .Reads {_activeStorageCount}; the pass must .Reads each one the technique reads.");
		if (readOnly > 0)
			map.CheckPassStorage(technique, _pass!, readOnlyOnly: true);
		map.CheckDispatchStorage(technique, storage);
		var total = sampled.Length + storage.Length + readOnly + uniforms;
		if (total > 0)
		{
			// Sampled inputs use the technique's declared filter (matching the Draw/FullScreen path and DirectX12's
			// static sampler) — not a hardcoded nearest, which silently diverged from DirectX12 for a LinearFilter pass —
			// and a slot the shader reads by comparison takes the technique's comparison sampler, as a draw's does.
			var sampler = template.LinearFilter ? _linearSampler : _nearestSampler;
			// Each descriptor's slot and its type come from the same reflected binding, so they cannot disagree.
			// They could before: the slot was this loop's index while the type was chosen from the runtime handle's
			// kind, and the layout groups every storage image ahead of every storage buffer — so a pass that supplied
			// a Buffer before an Image wrote a StorageBuffer descriptor into a slot the layout declared as a
			// StorageImage. No shipped pass supplies them in that order, which is why nothing had caught it.
			var set = _descriptors.Allocate(baked.DescriptorSetLayout);
			var imageInfos = stackalloc DescriptorImageInfo[total];
			var bufferInfos = stackalloc DescriptorBufferInfo[total];
			var writes = stackalloc WriteDescriptorSet[total];
			int count = 0, nextImage = 0;
			WriteDescriptorSet Write(uint binding, DescriptorType type) =>
				new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DescriptorCount = 1, DstBinding = binding, DescriptorType = type };
			for (var i = 0; i < sampled.Length; i++, count++)
			{
				ref var image = ref _resources.Get(_frameSlot, sampled[i].Id);
				var slotSampler = map.ComparedTextures[i] ? CompareSampler(template.SampleCompare!.Value) : sampler;
				imageInfos[count] = new DescriptorImageInfo { Sampler = slotSampler, ImageView = image.SampledView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
				writes[count] = Write((uint)map.SampledTextures[i].Index, DescriptorType.CombinedImageSampler);
				writes[count].PImageInfo = &imageInfos[count];
			}
			// The dispatch's storage: its images in order into the image slots; its buffers, with the pass's read-only
			// ones, into the buffer slots by each slot's kind
			int nextWritten = 0, nextRead = 0;
			Span<int> writtenBuffers = stackalloc int[storage.Length];
			int writtenCount = 0;
			foreach (var handle in storage)
			{
				if (handle.Kind == ResourceKind.Buffer)
				{
					writtenBuffers[writtenCount++] = handle.Id;
					continue;
				}
				ref var image = ref _resources.Get(_frameSlot, handle.Id);
				imageInfos[count] = new DescriptorImageInfo { ImageView = image.View, ImageLayout = ImageLayout.General };
				writes[count] = Write((uint)map.StorageImages[nextImage++].Index, DescriptorType.StorageImage);
				writes[count].PImageInfo = &imageInfos[count];
				count++;
			}
			foreach (var binding in map.StorageBuffers)
			{
				var buffer = binding.Kind == ShaderBindingKind.StorageBufferReadOnly
					? _resources.GetStorageBuffer(_frameSlot, _activeStorageIds[nextRead++])
					: nextWritten < writtenCount ? _resources.GetBuffer(_frameSlot, writtenBuffers[nextWritten++])
					: throw new InvalidOperationException($"Vulkan: compute technique '{technique}' declares {readWrite} read-write storage buffer(s) but its dispatch supplies {writtenCount}.");
				bufferInfos[count] = new DescriptorBufferInfo { Buffer = buffer, Offset = 0, Range = Vk.WholeSize };
				writes[count] = Write((uint)binding.Index, DescriptorType.StorageBuffer);
				writes[count].PBufferInfo = &bufferInfos[count];
				count++;
			}
			for (var j = 0; j < uniforms; j++, count++)
			{
				// A constant buffer, whole: dynamic like every uniform in a Kaldra layout, at offset 0
				bufferInfos[count] = new DescriptorBufferInfo { Buffer = _resources.GetConstantBuffer(_frameSlot, _activeConstantsIds[j]), Offset = 0, Range = Vk.WholeSize };
				writes[count] = Write((uint)map.UniformBuffers[j].Index, DescriptorType.UniformBufferDynamic);
				writes[count].PBufferInfo = &bufferInfos[count];
			}
			_vk.UpdateDescriptorSets(_ctx.Device, (uint)count, writes, 0, null);
			var offsets = stackalloc uint[Math.Max(uniforms, 1)];
			new Span<uint>(offsets, Math.Max(uniforms, 1)).Clear();
			_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Compute, baked.Layout, 0, 1, in set, (uint)uniforms, offsets);
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
		var inBuffer = _meshes.VertexBuffer(skinnedMesh.Id);
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
