using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Loom.Graph;

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
	private void BindAndSet(WebGpuTechnique baked, string technique, ReadOnlySpan<int> sampledViews, ReadOnlySpan<byte> perDrawUniform, ReadOnlySpan<float> push, ReadOnlySpan<byte> materialUniform = default)
	{
		var bindings = baked.Template.Bindings;
		if (!bindings.HasBindings)
			return;

		var transients = _engine.Transients;
		var ring = _engine.UniformRing;

		// Stage the ring-fed blocks (their contents change every draw, even on a bind-group cache hit). The material's
		// parameter block rides the same ring as the per-draw block, so a material needs no buffer of its own and
		// IEngine.Write(material, …) takes effect on the next draw without a graph rebuild.
		var perDrawOffset = perDrawUniform.Length > 0 ? ring.Reserve(perDrawUniform, perDrawUniform.Length) : 0;
		var materialSlot = materialUniform.Length > 0 ? bindings.MaterialUniformSlot : null;
		var materialOffset = materialUniform.Length > 0 ? ring.Reserve(materialUniform, materialUniform.Length) : 0;
		var pushOffset = 0;
		if (bindings.PushBinding >= 0)
		{
			// The lowered push block is a fixed-size uniform here, so an over-wide push used to be clipped by the
			// Math.Min below — silently, and only on this backend, since the desktop ones stage the whole span.
			_validate.PushFits(technique, push.Length * sizeof(float), routesToUniform: false, 0, bindings.PushByteSize);
			Span<byte> pushBytes = stackalloc byte[bindings.PushByteSize];
			pushBytes.Clear();
			MemoryMarshal.AsBytes(push).CopyTo(pushBytes);
			pushOffset = ring.Reserve(pushBytes, bindings.PushByteSize);
		}
		else if (push.Length > 0 && bindings.UniformBufferBindings.Length == 0)
		{
			// No push block and no constant buffer at all: the bytes have nowhere to land and used to vanish.
			_validate.PushFits(technique, push.Length * sizeof(float), routesToUniform: false, 0, 0);
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

		// Uniform slots in the shader's order: the slot reflection named as the per-material block takes the material's
		// parameters; the rest take the pass's .Reads constants in order, then the per-draw block.
		var constant = 0;
		var drawBlockPending = perDrawUniform.Length > 0;
		for (var j = 0; j < bindings.UniformBufferBindings.Length; j++)
		{
			var binding = bindings.UniformBufferBindings[j];
			if (j == materialSlot)
			{
				AddEntry(entries, ref e, binding, EntryBuffer, ring.Buffer, 0, materialUniform.Length);
				dynBinding[dynCount] = binding;
				dynOffset[dynCount++] = materialOffset;
			}
			else if (constant < _activeConstantsCount)
			{
				// A graph Constants buffer — bound whole (size 0), dynamic offset 0.
				AddEntry(entries, ref e, binding, EntryBuffer, transients.BufferHandle(_activeConstantsIds[constant++]), 0, 0);
				dynBinding[dynCount] = binding;
				dynOffset[dynCount++] = 0;
			}
			else if (drawBlockPending)
			{
				AddEntry(entries, ref e, binding, EntryBuffer, ring.Buffer, 0, perDrawUniform.Length);
				dynBinding[dynCount] = binding;
				dynOffset[dynCount++] = perDrawOffset;
				drawBlockPending = false;
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

	// Reuse or create the bind group for a set of entries. Keyed by the layout, the sampler and each entry's binding,
	// kind and handle: entry offsets are always 0 and sizes are deterministic per technique, so the bound handles fully
	// identify the group — and since the buffers/views/samplers are stable per graph, the steady state creates no bind
	// groups. The sampler rides the key's header rather than an entry per texture, because every sampled entry of a group
	// pairs with its technique's one sampler; a group that paired two different ones would need them as entries, so it
	// is refused rather than keyed by the first.
	private int GetOrCreateBindGroup(int layout, Span<int> entries)
	{
		var sampler = -1;
		for (var i = 0; i < entries.Length; i += 5)
		{
			if (entries[i + 1] != EntrySampler)
				continue;
			if (sampler >= 0 && entries[i + 2] != sampler)
				throw new WebGpuException("A bind group pairs its textures with two different samplers; its cache key holds one.");
			sampler = entries[i + 2];
		}

		var key = new BindingKey(BindingHandle(layout), sampler >= 0 ? BindingHandle(sampler) : 0);
		for (var i = 0; i < entries.Length; i += 5)
		{
			switch (entries[i + 1])
			{
				case EntryView:
					key.Add(entries[i], BoundObject.View, BindingHandle(entries[i + 2]));
					break;
				case EntryBuffer:
					key.Add(entries[i], BoundObject.Buffer, BindingHandle(entries[i + 2]));
					break;
				case EntrySampler:
					break;
				default:
					throw new WebGpuException($"Bind group entry kind {entries[i + 1]} is not mapped to a cache key.");
			}
		}
		if (_bindGroups.TryGetValue(key, out var cached))
			return (int)cached;

		var handle = _engine.Handles.Allocate(WebGpuHandleAllocator.BindGroups);
		WebGpuInterop.CreateBindGroup(handle, layout, entries);
		_bindGroups.Add(key, BindingHandle(handle));
		return handle;
	}

	/// <summary>
	/// Release every cached bind group that names the <paramref name="kind"/> object <paramref name="handle"/>, which the
	/// app is releasing. Called before the object's handle goes back to the allocator: the next object created of that
	/// kind is given the same number, and a group still keyed by it would bind the destroyed object in its place — the
	/// view of a released UI image drawn where its replacement belongs. Dropping a group the last frame bound is safe,
	/// because that frame's commands were encoded and submitted before this call could be made.
	/// </summary>
	public void EvictBindGroups(BoundObject kind, int handle)
	{
		_bindGroups.Evict(kind, BindingHandle(handle), _evictedGroups);
		foreach (var group in _evictedGroups)
			_engine.Handles.Release(WebGpuHandleAllocator.BindGroups, (int)group);
		_evictedGroups.Clear();
	}

	// A table handle as a cache-key value. Handles are non-negative table indices, so widening through uint keeps every
	// one distinct.
	private static ulong BindingHandle(int handle) => (uint)handle;
}
