namespace Loom.Graph;

/// <summary>
/// The binding objects a backend has built — Vulkan descriptor sets, WebGPU bind groups — keyed by the GPU objects they
/// name (<see cref="BindingKey"/>), so a draw naming the same objects re-binds one rather than building another, and
/// the steady state builds none. Each binding object is held by its 64-bit handle: a Vulkan descriptor set's handle, a
/// WebGPU bind-group table index.
/// <para>
/// <b>Invariant: nothing an entry names may be destroyed while the entry is cached.</b> A key that outlives its object
/// matches whatever object is next given that handle — a WebGPU table index is recycled by the next creation, and a
/// driver may hand a destroyed Vulkan handle to the next object of its kind — and binds the destroyed object in its
/// place. A backend keeps the invariant in one of two ways. Where it destroys a whole generation of objects at a moment
/// it has drained the GPU (a graph swap, a resize, a shader reload) it empties the cache. Where it destroys one object
/// between frames (a released texture or mesh) it calls <see cref="Evict"/> first, while the handle is still its own.
/// </para>
/// <para>
/// Not generic over the binding object's type, deliberately. Mono's WebAssembly AOT compiles a generic class's
/// instantiation over a value type so that its calls into the <see cref="Dictionary{TKey,TValue}"/> behind it trap at
/// run time with "function signature mismatch", which took down the WebGPU backend on every draw. A concrete
/// <c>Dictionary&lt;BindingKey, ulong&gt;</c> reached from non-generic code is a shape every backend's AOT handles.
/// </para>
/// </summary>
public sealed class BindingCache
{
	private readonly Dictionary<BindingKey, ulong> _entries = [];

	/// <summary>How many binding objects are cached.</summary>
	public int Count => _entries.Count;

	/// <summary>Every cached binding object's handle, for a backend releasing them all before <see cref="Clear"/>.</summary>
	public Dictionary<BindingKey, ulong>.ValueCollection Values => _entries.Values;

	/// <summary>The handle of the binding object built for <paramref name="key"/>, if there is one.</summary>
	public bool TryGetValue(in BindingKey key, out ulong binding) => _entries.TryGetValue(key, out binding);

	/// <summary>Cache the handle of the binding object built for <paramref name="key"/>.</summary>
	public void Add(in BindingKey key, ulong binding) => _entries.Add(key, binding);

	/// <summary>
	/// Remove every entry that names the <paramref name="kind"/> object <paramref name="handle"/>, appending each removed
	/// binding object's handle to <paramref name="evicted"/>. The caller still owns those: a frame in flight may bind
	/// one, so it destroys them on the same schedule as the object they named. Allocation-free once
	/// <paramref name="evicted"/> has grown to fit.
	/// </summary>
	public void Evict(BoundObject kind, ulong handle, List<ulong> evicted)
	{
		// Removing the current entry does not invalidate a Dictionary enumerator, so this needs no scratch key list.
		foreach (var (key, binding) in _entries)
		{
			if (!key.Names(kind, handle))
				continue;
			_entries.Remove(key);
			evicted.Add(binding);
		}
	}

	/// <summary>Drop every entry, at a moment the caller has drained the GPU and is destroying what they name.</summary>
	public void Clear() => _entries.Clear();
}
