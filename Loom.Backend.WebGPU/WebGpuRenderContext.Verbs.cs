using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

using Loom.Core.Graph;

namespace Loom.Backend.WebGPU;

[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuRenderContext
{
	public void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs) =>
		FullScreen(technique, default, inputs);

	public void FullScreen(string technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs)
	{
		var baked = Bake(technique);
		var template = baked.Template;
		var bindings = template.Bindings;

		// The push-constant channel and descriptor-bound uniform/storage buffers ride the uniform ring, which lands
		// with the mesh path — a fullscreen post pass that only samples inputs (blit, upscale, debug view) works now.
		if (bindings.PushBinding >= 0 || bindings.UniformBufferBindings.Length > 0 || bindings.StorageBufferBindings.Length > 0)
			throw new WebGpuException($"FullScreen('{technique}'): push/uniform/storage bindings are not yet implemented on the WebGPU backend.");

		var writer = _engine.Writer;
		writer.SetRenderPipeline(baked.Pipeline);
		if (bindings.HasBindings)
			writer.SetBindGroup(0, GetOrCreateSampledBindGroup(baked, inputs), default);
		writer.Draw(3, 1, 0, 0); // three fabricated verts cover the target; no vertex buffer
	}

	// Build (or reuse) the bind group for a sampled-only technique: each sampled input fills its WGSL texture
	// binding, paired with the technique's sampler at the adjacent binding. Keyed by (layout, view handles, sampler)
	// so a stable set of inputs (the common case — transients don't move within a graph) creates the group once.
	private int GetOrCreateSampledBindGroup(WebGpuTechnique baked, ReadOnlySpan<ResourceHandle> inputs)
	{
		var bindings = baked.Template.Bindings;
		if (inputs.Length != bindings.SampledTextureBindings.Length)
			throw new WebGpuException(
				$"A fullscreen technique samples {bindings.SampledTextureBindings.Length} inputs but the pass supplied {inputs.Length}.");

		var sampler = _engine.Samplers.Get(baked.Template.LinearFilter, baked.Template.Address);
		var transients = _engine.Transients;

		var key = new BindGroupKey(baked.Template.BindGroupLayout);
		for (var i = 0; i < inputs.Length; i++)
			key.Add(transients.ViewHandle(inputs[i].Id));
		key.Add(sampler);
		if (_bindGroups.TryGetValue(key, out var cached))
			return cached;

		// createBindGroup entries: (binding, kind, resourceHandle, offset, size); kind 1 = texture view, 2 = sampler.
		Span<int> entries = stackalloc int[inputs.Length * 2 * 5];
		var e = 0;
		for (var i = 0; i < inputs.Length; i++)
		{
			var view = transients.ViewHandle(inputs[i].Id);
			entries[e++] = bindings.SampledTextureBindings[i];
			entries[e++] = 1;
			entries[e++] = view;
			entries[e++] = 0;
			entries[e++] = 0;
			entries[e++] = bindings.SampledSamplerBindings[i];
			entries[e++] = 2;
			entries[e++] = sampler;
			entries[e++] = 0;
			entries[e++] = 0;
		}

		var handle = _engine.Handles.Allocate(WebGpuHandleAllocator.BindGroups);
		WebGpuInterop.CreateBindGroup(handle, baked.Template.BindGroupLayout, entries);
		_bindGroups[key] = handle;
		return handle;
	}

	// Allocation-free bind-group cache key: the layout handle plus the ordered resource handles the group binds
	// (view handles then the sampler). Full-value equality — a hash-only collapse could alias a group onto the
	// wrong textures after handle recycling.
	private const int MaxBindGroupHandles = 32;

	[InlineArray(MaxBindGroupHandles)]
	private struct HandleBuffer
	{
		private int _element0;
	}

	private struct BindGroupKey(int layout) : IEquatable<BindGroupKey>
	{
		private readonly int _layout = layout;
		private HandleBuffer _handles;
		private int _count;

		public void Add(int handle)
		{
			if (_count >= MaxBindGroupHandles)
				throw new WebGpuException($"A bind group binds more than {MaxBindGroupHandles} resources.");
			_handles[_count++] = handle;
		}

		public bool Equals(BindGroupKey other)
		{
			if (_layout != other._layout || _count != other._count)
				return false;
			for (var i = 0; i < _count; i++)
				if (_handles[i] != other._handles[i])
					return false;
			return true;
		}

		public override bool Equals(object? obj) => obj is BindGroupKey other && Equals(other);

		public override int GetHashCode()
		{
			var hash = new HashCode();
			hash.Add(_layout);
			hash.Add(_count);
			for (var i = 0; i < _count; i++)
				hash.Add(_handles[i]);
			return hash.ToHashCode();
		}
	}
}
