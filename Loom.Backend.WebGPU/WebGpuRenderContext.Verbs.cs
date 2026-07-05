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
		BindAndSet(baked, views, default, push);
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
			var textures = _engine.MaterialTextures(material.Id);
			for (var i = 0; i < sampleCount; i++)
				views[i] = _engine.TextureView(textures[Math.Min(i, textures.Length - 1)]);
		}
		else
		{
			var transients = _engine.Transients;
			for (var i = 0; i < sampleCount && i < call.Sampled.Length; i++)
				views[i] = transients.ViewHandle(call.Sampled[i].Id);
		}

		BindAndSet(baked, views, call.Uniforms, default);
		RecordGeometry(call);
	}

	// Emit the geometry half of a draw: single mesh, skinned override, GPU-resident instances, CPU instances, or a
	// procedural instanced draw whose vertices the shader fabricates from the vertex index.
	private void RecordGeometry(in DrawCall call)
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
