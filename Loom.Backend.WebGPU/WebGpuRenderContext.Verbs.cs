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
		var transients = _engine.Transients;

		Span<int> views = stackalloc int[inputs.Length];
		for (var i = 0; i < inputs.Length; i++)
			views[i] = transients.ViewHandle(inputs[i].Id);

		_engine.Writer.SetRenderPipeline(baked.Pipeline);
		// The float span fills the technique's per-draw block. A LOOM_PUSH_CONSTANT block (≤16 floats) is filled
		// through the push channel; a larger per-draw constant buffer (e.g. CSM's cascade matrices, register(b2)) is a
		// descriptor-bound uniform and is filled through the per-draw-uniform channel. A technique has one or the
		// other, so passing the same bytes to both lets its binding model bind whichever slot it actually declares.
		BindAndSet(baked, views, MemoryMarshal.AsBytes(push), push);
		_engine.Writer.Draw(3, 1, 0, 0); // three fabricated verts cover the target; no vertex buffer
	}

	public void Draw(in DrawCall call)
	{
		var baked = Bake(call.Technique);
		var template = baked.Template;
		_engine.Writer.SetRenderPipeline(baked.Pipeline);

		// Sampled inputs come from a material's textures or the pass's sampled graph resources (mutually exclusive).
		var sampleCount = template.SampleCount;
		Span<int> views = stackalloc int[sampleCount];
		if (call.Material is { } material)
		{
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
				views[i] = transients.ViewHandle(call.Sampled[i].Id);
		}

		BindAndSet(baked, views, call.Uniforms, default,
			call.Material is { } mat ? _engine.MaterialParameters(mat.Id) : default);
		RecordGeometry(call, template.Instance?.Stride ?? 0);
	}

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
	{
		var baked = BakeCompute(technique);
		var bindings = baked.Template.Bindings;
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
				AddEntry(entries, ref e, bindings.SampledTextureBindings[i], EntryView, transients.ViewHandle(sampled[i].Id), 0, 0);
				AddEntry(entries, ref e, bindings.SampledSamplerBindings[i], EntrySampler, sampler, 0, 0);
			}

			// Storage resources are passed images-first then buffers (the shader's declaration order): a storage
			// image binds its view, a storage buffer binds its buffer.
			var imageCount = bindings.StorageImageBindings.Length;
			for (var k = 0; k < storage.Length; k++)
			{
				if (k < imageCount)
					AddEntry(entries, ref e, bindings.StorageImageBindings[k], EntryView, transients.ViewHandle(storage[k].Id), 0, 0);
				else if (k - imageCount < bindings.StorageBufferBindings.Length)
					AddEntry(entries, ref e, bindings.StorageBufferBindings[k - imageCount], EntryBuffer, transients.BufferHandle(storage[k].Id), 0, 0);
			}

			Span<int> dynOffsets = stackalloc int[1];
			var dynCount = 0;
			if (bindings.PushBinding >= 0)
			{
				Span<byte> pushBytes = stackalloc byte[bindings.PushByteSize];
				pushBytes.Clear();
				var src = MemoryMarshal.AsBytes(push);
				src[..Math.Min(src.Length, pushBytes.Length)].CopyTo(pushBytes);
				dynOffsets[dynCount++] = _engine.UniformRing.Reserve(pushBytes, bindings.PushByteSize);
				AddEntry(entries, ref e, bindings.PushBinding, EntryBuffer, _engine.UniformRing.Buffer, 0, bindings.PushByteSize);
			}

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

		if (call.Instances.IsEmpty)
		{
			if (call.SkinnedVertices is { } sv)
				meshes.DrawSkinned(writer, call.Mesh!.Value.Id, _engine.Transients.BufferHandle(sv.Id));
			else
				meshes.Draw(writer, call.Mesh!.Value.Id);
			return;
		}

		if (call.Instances.GpuBuffer is { } gpuBuf)
		{
			gpuBuf.CheckInstanceStride(instanceStride, call.Technique);
			var gpuBuffer = _engine.Transients.BufferHandle(gpuBuf.Id);
			if (call.Mesh is { } mesh)
			{
				meshes.DrawInstanced(writer, mesh.Id, gpuBuffer, 0, call.Instances.Count);
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

		if (call.Instances.CpuData.IsEmpty)
			return;
		var offset = _engine.InstanceRing.Reserve(call.Instances.CpuData, call.Instances.CpuData.Length);
		meshes.DrawInstanced(writer, call.Mesh!.Value.Id, _engine.InstanceRing.Buffer, offset, call.Instances.Count);
	}
}
