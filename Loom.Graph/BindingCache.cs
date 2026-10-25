namespace Loom.Graph;

/// <summary>
/// The binding objects a backend has built — Vulkan descriptor sets, WebGPU bind groups — keyed by the GPU objects they
/// name (<see cref="BindingKey"/>), so a draw naming the same objects re-binds one rather than building another, and
/// the steady state builds none.
/// <para>
/// <b>Invariant: nothing an entry names may be destroyed while the entry is cached.</b> A key that outlives its object
/// matches whatever object is next given that handle — a WebGPU table index is recycled by the next creation, and a
/// driver may hand a destroyed Vulkan handle to the next object of its kind — and binds the destroyed object in its
/// place. A backend keeps the invariant in one of two ways. Where it destroys a whole generation of objects at a moment
/// it has drained the GPU (a graph swap, a resize, a shader reload) it empties the cache. Where it destroys one object
/// between frames (a released texture or mesh) it calls <see cref="Evict"/> first, while the handle is still its own.
/// </para>
/// </summary>
public sealed class BindingCache<TValue>
{
	private readonly Dictionary<BindingKey, TValue> _entries = [];

	/// <summary>How many binding objects are cached.</summary>
	public int Count => _entries.Count;

	/// <summary>Every cached binding object, for a backend releasing them all before <see cref="Clear"/>.</summary>
	public Dictionary<BindingKey, TValue>.ValueCollection Values => _entries.Values;

	/// <summary>The binding object built for <paramref name="key"/>, if there is one.</summary>
	public bool TryGetValue(in BindingKey key, out TValue value) => _entries.TryGetValue(key, out value!);

	/// <summary>Cache the binding object built for <paramref name="key"/>.</summary>
	public void Add(in BindingKey key, TValue value) => _entries.Add(key, value);

	/// <summary>
	/// Remove every entry that names the <paramref name="kind"/> object <paramref name="handle"/>, appending each removed
	/// binding object to <paramref name="evicted"/>. The caller still owns those: a frame in flight may bind one, so it
	/// destroys them on the same schedule as the object they named. Allocation-free once <paramref name="evicted"/> has
	/// grown to fit.
	/// </summary>
	public void Evict(BoundObject kind, ulong handle, List<TValue> evicted)
	{
		// Removing the current entry does not invalidate a Dictionary enumerator, so this needs no scratch key list.
		foreach (var (key, value) in _entries)
		{
			if (!key.Names(kind, handle))
				continue;
			_entries.Remove(key);
			evicted.Add(value);
		}
	}

	/// <summary>Drop every entry, at a moment the caller has drained the GPU and is destroying what they name.</summary>
	public void Clear() => _entries.Clear();
}
