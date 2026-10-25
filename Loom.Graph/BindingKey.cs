using System.Runtime.CompilerServices;

namespace Loom.Graph;

/// <summary>
/// An allocation-free cache key for a GPU binding object — a Vulkan descriptor set, a WebGPU bind group — made of the
/// handles of the objects it names: the layout and the sampler, then the ordered binding, kind, handle and window of
/// every entry. Equality compares every value rather than collapsing to a hash, because two different binding objects
/// colliding would bind one draw's resources to another's.
/// <para>
/// It lives here rather than in a backend because the handles are opaque numbers — a 64-bit Vulkan handle, a WebGPU
/// table index — and the one thing every backend needs beyond equality is the same: <see cref="Names"/>, which finds
/// the keys that name an object being destroyed (see <see cref="BindingCache{TValue}"/>).
/// </para>
/// </summary>
public struct BindingKey : IEquatable<BindingKey>
{
	/// <summary>The most entries one key holds; one more throws.</summary>
	public const int MaxEntries = 16;

	private const int ValuesPerEntry = 3;

	[InlineArray(MaxEntries * ValuesPerEntry)]
	private struct Values
	{
		private ulong _element0;
	}

	private readonly ulong _layout;
	private readonly ulong _sampler;
	private Values _values;
	private int _count;

	/// <param name="layout">The layout the binding object is built against.</param>
	/// <param name="sampler">The sampler every sampled entry pairs with, or 0 when nothing is sampled. Held once rather
	/// than per entry because a binding object samples all its textures through its technique's one sampler.</param>
	public BindingKey(ulong layout, ulong sampler = 0)
	{
		_layout = layout;
		_sampler = sampler;
	}

	/// <summary>
	/// Record one entry: the <paramref name="kind"/> object <paramref name="handle"/> at <paramref name="binding"/>.
	/// <paramref name="window"/> is the byte size a buffer entry exposes when that size is part of the descriptor, so
	/// two windows onto one buffer are two keys; 0 otherwise.
	/// </summary>
	public void Add(int binding, BoundObject kind, ulong handle, ulong window = 0)
	{
		if (_count + ValuesPerEntry > MaxEntries * ValuesPerEntry)
			throw new InvalidOperationException($"A binding cache key holds at most {MaxEntries} entries.");
		_values[_count++] = (uint)binding | ((ulong)kind << 32);
		_values[_count++] = handle;
		_values[_count++] = window;
	}

	/// <summary>Whether any entry names the <paramref name="kind"/> object <paramref name="handle"/>.</summary>
	public readonly bool Names(BoundObject kind, ulong handle)
	{
		for (var i = 0; i < _count; i += ValuesPerEntry)
		{
			if (_values[i] >> 32 == (ulong)kind && _values[i + 1] == handle)
				return true;
		}
		return false;
	}

	public readonly bool Equals(BindingKey other)
	{
		if (_layout != other._layout || _sampler != other._sampler || _count != other._count)
			return false;
		for (var i = 0; i < _count; i++)
		{
			if (_values[i] != other._values[i])
				return false;
		}
		return true;
	}

	public override readonly bool Equals(object? obj) => obj is BindingKey other && Equals(other);

	public override readonly int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(_layout);
		hash.Add(_sampler);
		hash.Add(_count);
		for (var i = 0; i < _count; i++)
			hash.Add(_values[i]);
		return hash.ToHashCode();
	}
}
