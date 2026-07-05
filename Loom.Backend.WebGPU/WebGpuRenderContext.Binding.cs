using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Loom.Backend.WebGPU;

[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuRenderContext
{
	// createBindGroup entry kinds (mirror the shim): 0 = buffer, 1 = texture view, 2 = sampler.
	private const int EntryBuffer = 0;
	private const int EntryView = 1;
	private const int EntrySampler = 2;

	/// <summary>
	/// Build (or reuse) the group-0 bind group for a draw/fullscreen and record <c>SetBindGroup</c> with the
	/// per-draw dynamic offsets. Binding order follows the technique's <see cref="WebGpuBindingModel"/>: sampled
	/// textures (each paired with the technique's sampler), then the pass's <c>.Reads</c> storage buffers, then its
	/// uniform buffers (the <c>.Reads</c> constants fill slots 0..K-1, an optional per-draw block fills slot K), then
	/// the lowered push-constant block. Uniform bindings are dynamic-offset: the constants and per-draw/push data
	/// live in stable buffers, so the group is cached by its bound handles and only the offsets change per draw.
	/// </summary>
	private void BindAndSet(WebGpuTechnique baked, ReadOnlySpan<int> sampledViews, ReadOnlySpan<byte> perDrawUniform, ReadOnlySpan<float> push)
	{
		var bindings = baked.Template.Bindings;
		if (!bindings.HasBindings)
			return;

		var transients = _engine.Transients;
		var ring = _engine.UniformRing;

		// Stage the ring-fed blocks (their contents change every draw, even on a bind-group cache hit).
		var perDrawOffset = perDrawUniform.Length > 0 ? ring.Reserve(perDrawUniform, perDrawUniform.Length) : 0;
		var pushOffset = 0;
		if (bindings.PushBinding >= 0)
		{
			Span<byte> pushBytes = stackalloc byte[bindings.PushByteSize];
			pushBytes.Clear();
			var src = MemoryMarshal.AsBytes(push);
			src[..Math.Min(src.Length, pushBytes.Length)].CopyTo(pushBytes);
			pushOffset = ring.Reserve(pushBytes, bindings.PushByteSize);
		}

		Span<int> entries = stackalloc int[bindings.Entries.Count * 5];
		var e = 0;
		Span<int> dynBinding = stackalloc int[MaxUniformBuffers + 1];
		Span<int> dynOffset = stackalloc int[MaxUniformBuffers + 1];
		var dynCount = 0;

		var sampler = sampledViews.Length > 0 ? _engine.Samplers.Get(baked.Template.LinearFilter, baked.Template.Address) : 0;
		for (var i = 0; i < sampledViews.Length; i++)
		{
			AddEntry(entries, ref e, bindings.SampledTextureBindings[i], EntryView, sampledViews[i], 0, 0);
			AddEntry(entries, ref e, bindings.SampledSamplerBindings[i], EntrySampler, sampler, 0, 0);
		}

		for (var j = 0; j < _activeStorageCount && j < bindings.StorageBufferBindings.Length; j++)
			AddEntry(entries, ref e, bindings.StorageBufferBindings[j], EntryBuffer, transients.BufferHandle(_activeStorageIds[j]), 0, 0);

		for (var j = 0; j < bindings.UniformBufferBindings.Length; j++)
		{
			var binding = bindings.UniformBufferBindings[j];
			if (j < _activeConstantsCount)
			{
				// A graph Constants buffer — bound whole (size 0), dynamic offset 0.
				AddEntry(entries, ref e, binding, EntryBuffer, transients.BufferHandle(_activeConstantsIds[j]), 0, 0);
				dynBinding[dynCount] = binding;
				dynOffset[dynCount++] = 0;
			}
			else if (j == _activeConstantsCount && perDrawUniform.Length > 0)
			{
				AddEntry(entries, ref e, binding, EntryBuffer, ring.Buffer, 0, perDrawUniform.Length);
				dynBinding[dynCount] = binding;
				dynOffset[dynCount++] = perDrawOffset;
			}
			else
			{
				// An unfilled uniform slot — bind a defined 16-byte window so the shader reads valid memory.
				AddEntry(entries, ref e, binding, EntryBuffer, ring.Buffer, 0, 16);
				dynBinding[dynCount] = binding;
				dynOffset[dynCount++] = 0;
			}
		}

		if (bindings.PushBinding >= 0)
		{
			AddEntry(entries, ref e, bindings.PushBinding, EntryBuffer, ring.Buffer, 0, bindings.PushByteSize);
			dynBinding[dynCount] = bindings.PushBinding;
			dynOffset[dynCount++] = pushOffset;
		}

		var bindGroup = GetOrCreateBindGroup(baked.Template.BindGroupLayout, entries[..e]);

		// WebGPU wants dynamic offsets in ascending binding order — sort the (binding, offset) pairs together.
		for (var i = 1; i < dynCount; i++)
		{
			int b = dynBinding[i], o = dynOffset[i], k = i - 1;
			while (k >= 0 && dynBinding[k] > b)
			{
				dynBinding[k + 1] = dynBinding[k];
				dynOffset[k + 1] = dynOffset[k];
				k--;
			}
			dynBinding[k + 1] = b;
			dynOffset[k + 1] = o;
		}

		_engine.Writer.SetBindGroup(0, bindGroup, dynOffset[..dynCount]);
	}

	private static void AddEntry(Span<int> entries, ref int e, int binding, int kind, int handle, int offset, int size)
	{
		entries[e++] = binding;
		entries[e++] = kind;
		entries[e++] = handle;
		entries[e++] = offset;
		entries[e++] = size;
	}

	// Reuse or create the bind group for a set of entries. Keyed by the layout plus each entry's (binding, handle):
	// entry offsets are always 0 and sizes are deterministic per technique, so the bound handles fully identify the
	// group — and since the buffers/views/samplers are stable per graph, the steady state creates no bind groups.
	private int GetOrCreateBindGroup(int layout, Span<int> entries)
	{
		var key = new BindGroupKey(layout);
		for (var i = 0; i < entries.Length; i += 5)
		{
			key.Add(entries[i]);     // binding
			key.Add(entries[i + 2]); // resource handle
		}
		if (_bindGroups.TryGetValue(key, out var cached))
			return cached;

		var handle = _engine.Handles.Allocate(WebGpuHandleAllocator.BindGroups);
		WebGpuInterop.CreateBindGroup(handle, layout, entries);
		_bindGroups[key] = handle;
		return handle;
	}

	// Allocation-free bind-group cache key: the layout handle plus the ordered (binding, handle) ints. Full-value
	// equality — a hash-only collapse could alias a group onto the wrong resources after handle recycling.
	private const int MaxBindGroupInts = 64;

	[InlineArray(MaxBindGroupInts)]
	private struct IntBuffer
	{
		private int _element0;
	}

	private struct BindGroupKey(int layout) : IEquatable<BindGroupKey>
	{
		private readonly int _layout = layout;
		private IntBuffer _values;
		private int _count;

		public void Add(int value)
		{
			if (_count >= MaxBindGroupInts)
				throw new WebGpuException($"A bind group's key exceeds {MaxBindGroupInts} ints.");
			_values[_count++] = value;
		}

		public bool Equals(BindGroupKey other)
		{
			if (_layout != other._layout || _count != other._count)
				return false;
			for (var i = 0; i < _count; i++)
				if (_values[i] != other._values[i])
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
				hash.Add(_values[i]);
			return hash.ToHashCode();
		}
	}
}
