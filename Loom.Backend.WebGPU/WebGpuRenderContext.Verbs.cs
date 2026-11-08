using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Backend.WebGPU;

[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuRenderContext
{
	public void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs) =>
		FullScreen(technique, default, inputs);

	public void FullScreen(string technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs)
	{
		var baked = Bake(technique);
		_validate.FullScreen(technique, inputs);
		var transients = _engine.Transients;

		Span<int> views = stackalloc int[inputs.Length];
		for (var i = 0; i < inputs.Length; i++)
			views[i] = transients.SampledViewHandle(inputs[i].Id);

		_engine.Writer.SetRenderPipeline(baked.Pipeline);
		// The float span fills the technique's per-draw block. A LOOM_PUSH_CONSTANT block (≤16 floats) is filled
		// through the push channel; a larger per-draw constant buffer (e.g. CSM's cascade matrices, register(b2)) is a
		// descriptor-bound uniform and is filled through the per-draw-uniform channel. A technique has one or the
		// other, so passing the same bytes to both lets its binding model bind whichever slot it actually declares.
		BindAndSet(baked, technique, views, MemoryMarshal.AsBytes(push), push);
		_engine.Writer.Draw(3, 1, 0, 0); // three fabricated verts cover the target; no vertex buffer
	}

	public void Draw(in DrawCall call)
	{
		var baked = Bake(call.Technique);
		var template = baked.Template;
		_validate.Draw(call);
		_engine.Writer.SetRenderPipeline(baked.Pipeline);

		// Sampled inputs come from a material's textures or the pass's sampled graph resources (mutually exclusive).
		var sampleCount = template.SampleCount;
		Span<int> views = stackalloc int[sampleCount];
		if (call.Material is { } material)
		{
			// A material draw binds no storage buffers, on every backend: one whose technique reads them throws rather
			// than binding the pass's here alone
			if (template.Bindings.StorageBufferBindings.Length > 0)
				throw new InvalidOperationException($"WebGPU: technique '{call.Technique}' reads a storage buffer and draws with a material; a material draw binds no storage buffers.");
			// The shared contract check, so a material with too few textures is reported here exactly as it is on the
			// desktop backends. Without it this loop clamped to the last texture and silently repeated it.
			_engine.ValidateMaterial(material, call.Technique);
			var textures = _engine.MaterialTextures(material.Id);
			for (var i = 0; i < sampleCount; i++)
				views[i] = _engine.TextureView(textures[i]);
		}
		else
		{
			var transients = _engine.Transients;
			for (var i = 0; i < sampleCount && i < call.Sampled.Length; i++)
				views[i] = transients.SampledViewHandle(call.Sampled[i].Id);
		}

		BindAndSet(baked, call.Technique, views, call.Uniforms, default,
			call.Material is { } mat ? _engine.MaterialParameters(mat.Id) : default);
		RecordGeometry(call, template.Instance?.Stride ?? 0);
	}

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
	{
		var baked = BakeCompute(technique);
		var bindings = baked.Template.Bindings;
		_validate.Dispatch(technique, sampled, storage);
		var writer = _engine.Writer;
		writer.SetComputePipeline(baked.Pipeline);

		if (bindings.HasBindings)
		{
			var transients = _engine.Transients;
			Span<int> entries = stackalloc int[bindings.Entries.Count * 5];
			var e = 0;

			var sampler = sampled.Length > 0 ? _engine.Samplers.Get(baked.Template.LinearFilter, baked.Template.Address) : 0;
			for (var i = 0; i < sampled.Length && i < bindings.SampledTextureBindings.Length; i++)
			{
				// A slot the shader reads by comparison takes a comparison sampler with the technique's function, as a draw's does
				var slotSampler = i < bindings.SampledCompared.Length && bindings.SampledCompared[i]
					? _engine.Samplers.GetComparison(baked.Template.SampleCompare!.Value)
					: sampler;
				AddEntry(entries, ref e, bindings.SampledTextureBindings[i], EntryView, transients.SampledViewHandle(sampled[i].Id), 0, 0);
				AddEntry(entries, ref e, bindings.SampledSamplerBindings[i], EntrySampler, slotSampler, 0, 0);
			}

			// The dispatch's storage, by each handle's kind as Vulkan sorts it: its images in order into the image slots,
			// its buffers in order into the read-write buffer slots. The read-only buffer slots take the pass's .Reads
			// Storage in order, as a draw's do. Each throws where it would leave a slot unbound.
			var imageSlots = bindings.StorageImageBindings;
			int nextImage = 0, writtenCount = 0;
			Span<int> written = stackalloc int[storage.Length];
			foreach (var handle in storage)
			{
				if (handle.Kind == ResourceKind.Buffer)
				{
					written[writtenCount++] = handle.Id;
					continue;
				}
				if (nextImage >= imageSlots.Length)
					throw new InvalidOperationException($"WebGPU: compute technique '{technique}' declares {imageSlots.Length} storage image(s) but its dispatch supplies more.");
				AddEntry(entries, ref e, imageSlots[nextImage++], EntryView, transients.ViewHandle(handle.Id), 0, 0);
			}
			int readOnly = 0, readWrite = 0;
			for (var b = 0; b < bindings.StorageBufferBindings.Length; b++)
			{
				if (b < bindings.StorageBufferReadOnly.Length && bindings.StorageBufferReadOnly[b])
					readOnly++;
				else
					readWrite++;
			}
			if (readOnly > _activeStorageCount)
				throw new InvalidOperationException($"WebGPU: compute technique '{technique}' declares {readOnly} read-only storage buffer(s) but its pass .Reads {_activeStorageCount}; the pass must .Reads each one the technique reads.");
			if (readOnly > 0)
				baked.Template.Reflected.CheckPassStorage(technique, _pass!, readOnlyOnly: true);
			int nextWritten = 0, nextRead = 0;
			for (var b = 0; b < bindings.StorageBufferBindings.Length; b++)
			{
				int buffer;
				if (b < bindings.StorageBufferReadOnly.Length && bindings.StorageBufferReadOnly[b])
					buffer = transients.BufferHandle(_activeStorageIds[nextRead++]);
				else if (nextWritten < writtenCount)
					buffer = transients.BufferHandle(written[nextWritten++]);
				else
					throw new InvalidOperationException($"WebGPU: compute technique '{technique}' declares {readWrite} read-write storage buffer(s) but its dispatch supplies {writtenCount}.");
				AddEntry(entries, ref e, bindings.StorageBufferBindings[b], EntryBuffer, buffer, 0, 0);
			}

			// The constant buffers the technique declares take the pass's .Reads Constants, in order, whole, as a draw's
			// do. One that declares more than the pass reads would bind a group missing an entry, so it throws instead.
			var uniforms = bindings.UniformBufferBindings;
			if (uniforms.Length > _activeConstantsCount)
				throw new InvalidOperationException($"WebGPU: compute technique '{technique}' declares {uniforms.Length} constant buffer(s) but its pass .Reads {_activeConstantsCount}; the pass must .Reads each one the technique reads.");
			Span<int> dynBindings = stackalloc int[uniforms.Length + 1];
			Span<int> dynOffsets = stackalloc int[uniforms.Length + 1];
			var dynCount = 0;
			for (var j = 0; j < uniforms.Length; j++)
			{
				AddEntry(entries, ref e, uniforms[j], EntryBuffer, transients.BufferHandle(_activeConstantsIds[j]), 0, 0);
				dynBindings[dynCount] = uniforms[j];
				dynOffsets[dynCount++] = 0;
			}
			if (bindings.PushBinding >= 0)
			{
				_validate.PushFits(technique, push.Length * sizeof(float), routesToUniform: false, 0, bindings.PushByteSize);
				Span<byte> pushBytes = stackalloc byte[bindings.PushByteSize];
				pushBytes.Clear();
				MemoryMarshal.AsBytes(push).CopyTo(pushBytes);
				dynBindings[dynCount] = bindings.PushBinding;
				dynOffsets[dynCount++] = _engine.UniformRing.Reserve(pushBytes, bindings.PushByteSize);
				AddEntry(entries, ref e, bindings.PushBinding, EntryBuffer, _engine.UniformRing.Buffer, 0, bindings.PushByteSize);
			}

			SortDynamicOffsets(dynBindings[..dynCount], dynOffsets[..dynCount]);
			var bindGroup = GetOrCreateBindGroup(baked.Template.BindGroupLayout, entries[..e]);
			writer.SetBindGroup(0, bindGroup, dynOffsets[..dynCount]);
		}

		writer.Dispatch((int)groups.X, (int)groups.Y, (int)groups.Z);
	}

	// Emit the geometry half of a draw: single mesh, skinned override, GPU-resident instances, CPU instances, or a
	// procedural instanced draw whose vertices the shader fabricates from the vertex index.
	private void RecordGeometry(in DrawCall call, int instanceStride)
	{
		var meshes = _engine.Meshes;
		var writer = _engine.Writer;

		if (call.Instances.IndirectBuffer is { } args)
		{
			// The counts live in the buffer; the CPU supplies only where to read them. The graph ordered the compute
			// that filled it before this pass.
			meshes.DrawIndirect(writer, _validate.RequireMesh(call).Id,
				_engine.Transients.BufferHandle(args.Id), call.Instances.IndirectOffset);
			return;
		}

		if (call.Instances.IsEmpty)
		{
			if (call.SkinnedVertices is { } sv)
				meshes.DrawSkinned(writer, _validate.RequireMesh(call).Id, _engine.Transients.BufferHandle(sv.Id));
			else
				meshes.Draw(writer, _validate.RequireMesh(call).Id, call.Indices);
			return;
		}

		if (call.Instances.GpuBuffer is { } gpuBuf)
		{
			gpuBuf.CheckInstanceStride(instanceStride, call.Technique);
			var gpuBuffer = _engine.Transients.BufferHandle(gpuBuf.Id);
			if (call.Mesh is { } mesh)
			{
				meshes.DrawInstanced(writer, mesh.Id, gpuBuffer, 0, call.Instances.Count, call.Indices);
			}
			else
			{
				// The GPU buffer IS the per-instance vertex stream; vertexCount verts per instance fabricated from
				// the vertex index (the GPU-particle path).
				writer.SetVertexBuffer(0, gpuBuffer, 0);
				writer.Draw(call.VertexCount, call.Instances.Count, 0, 0);
			}
			return;
		}

		if (call.Instances.StaticBuffer is { } stored)
		{
			// Instances uploaded once: the stored buffer itself, from the draw's first instance
			stored.CheckInstanceStride(instanceStride, call.Technique);
			meshes.DrawInstanced(writer, _validate.RequireMesh(call).Id, meshes.BindPoseBuffer(stored.Storage.Id),
				checked((int)((long)call.Instances.StaticFirst * stored.Stride)), call.Instances.Count, call.Indices);
			return;
		}

		if (call.Instances.CpuData.IsEmpty)
			return;
		var offset = _engine.InstanceRing.Reserve(call.Instances.CpuData, call.Instances.CpuData.Length);
		meshes.DrawInstanced(writer, _validate.RequireMesh(call).Id, _engine.InstanceRing.Buffer, offset, call.Instances.Count, call.Indices);
	}
}
