using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;

namespace BinaryVibrance.Kaldra.Concepts;

/// <summary>
/// A dense, growable GPU array with lease-based row management, over a growable <see cref="StorageBufferHandle"/>.
/// Hand out rows with <see cref="Lease"/>, write each through its <see cref="BufferLease{T}.Data"/>, and
/// <see cref="Flush"/> once per frame (before <c>Render</c>) to upload the packed contents; a shader reads the buffer
/// indexed by instance/draw (the row's <see cref="BufferLease{T}.Index"/>). Disposing a lease compacts the array by
/// the swap-and-pop trick — the last row fills the freed slot in O(1) and that row's lease is told its new index — so
/// the array stays hole-free and uploads as one contiguous range.
/// <para>
/// Growth is free: the CPU array doubles as needed, and because the backing handle is growable a <see cref="Flush"/>
/// that overflows the current GPU capacity reallocates it (the engine handles frames-in-flight). The buffer keeps a
/// dirty flag, so an unchanged frame uploads nothing.
/// </para>
/// </summary>
/// <typeparam name="T">The row's unmanaged element type (must match the storage buffer's shader element layout).</typeparam>
public sealed class LeasedBuffer<T> where T : unmanaged
{
	private readonly StorageBufferHandle _handle;
	private T[] _data;
	private BufferLease<T>?[] _owners;
	private int _count;
	private bool _dirty;

	/// <summary>
	/// Wrap a growable storage <paramref name="handle"/> (declared via <c>g.Storage&lt;T&gt;(name, count, growable: true)</c>).
	/// <paramref name="initialCapacity"/> sizes the CPU backing arrays — pass the same count the handle was declared
	/// with so the CPU and GPU start in step and growth only happens past that.
	/// </summary>
	public LeasedBuffer(StorageBufferHandle handle, int initialCapacity = 16)
	{
		ArgumentNullException.ThrowIfNull(handle);
		if (!handle.Growable)
			throw new ArgumentException(
				$"Storage '{handle.Name}' is not growable — a LeasedBuffer grows its backing store, so declare it with g.Storage<T>(name, count, growable: true).", nameof(handle));
		var capacity = Math.Max(1, initialCapacity);
		_handle = handle;
		_data = new T[capacity];
		_owners = new BufferLease<T>?[capacity];
	}

	/// <summary>The number of live rows — the instance/element count a draw submits.</summary>
	public int Count => _count;

	/// <summary>The packed rows as a read-only view (length <see cref="Count"/>). Valid until the next mutation
	/// (<see cref="Lease"/>/dispose may reallocate the backing array); don't cache it across one.</summary>
	public ReadOnlySpan<T> AsSpan() => _data.AsSpan(0, _count);

	/// <summary>Reserve a new row at the end and return its lease. Grows the CPU arrays geometrically when full.</summary>
	public BufferLease<T> Lease()
	{
		if (_count == _data.Length)
		{
			var grown = Math.Max(4, _data.Length * 2);
			Array.Resize(ref _data, grown);
			Array.Resize(ref _owners, grown);
		}
		var index = _count++;
		var lease = new BufferLease<T>(this, index);
		_owners[index] = lease;
		_data[index] = default;
		_dirty = true;
		return lease;
	}

	/// <summary>Upload the packed rows to the GPU if anything changed since the last flush. Call once per frame before
	/// recording; a clean frame uploads nothing. A <see cref="Count"/> that has outgrown the handle's declared capacity
	/// reallocates the GPU buffer (the handle is growable).</summary>
	public void Flush(IEngine engine)
	{
		ArgumentNullException.ThrowIfNull(engine);
		if (!_dirty)
			return;
		engine.Write(_handle, _data.AsSpan(0, _count));
		_dirty = false;
	}

	// A lease forwards its Data setter here; a disposed lease forwards index -1, which is ignored.
	internal void Update(int index, in T data)
	{
		if (index < 0)
			return;
		_data[index] = data;
		_dirty = true;
	}

	// Swap-and-pop: move the last row into the freed slot (O(1), keeps the array dense) and tell the moved row's lease
	// its new index. The disposed lease has already cleared its own index.
	internal void Delete(int index)
	{
		if (index < 0 || index >= _count)
			return;
		var last = --_count;
		if (index != last)
		{
			var moved = _owners[last]!;
			_data[index] = _data[last];
			_owners[index] = moved;
			moved.SetIndex(index);
		}
		_owners[last] = null;
		_data[last] = default;
		_dirty = true;
	}
}
