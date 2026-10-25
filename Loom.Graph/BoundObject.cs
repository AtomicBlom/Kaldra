namespace Loom.Graph;

/// <summary>
/// The kind of GPU object an entry of a <see cref="BindingKey"/> names. Evicting the bindings that name a destroyed
/// object matches on the kind as well as the handle, because handles are only unique within a kind: a backend that
/// numbers its objects per table has a buffer and a view that share every small number.
/// </summary>
public enum BoundObject : byte
{
	/// <summary>An image view: a sampled texture, or a storage image.</summary>
	View = 1,

	/// <summary>A uniform or storage buffer.</summary>
	Buffer = 2,
}
