namespace Loom.Concepts;

/// <summary>
/// A handle to one row of a <see cref="LeasedBuffer{T}"/>. Set <see cref="Data"/> to write the row (the buffer marks
/// itself dirty and uploads on the next <see cref="LeasedBuffer{T}.Flush"/>), read <see cref="Index"/> to learn the
/// row's current position in the packed buffer (e.g. to reference it from a per-draw block), and call
/// <see cref="Dispose"/> to release the row. Releasing compacts the buffer by moving its last row into the freed
/// slot, so <em>another</em> lease's <see cref="Index"/> can change — read an index fresh, never cache it across a
/// dispose. A stable identity (a picking id, say) belongs in the row payload, not the index.
/// </summary>
/// <typeparam name="T">The row's unmanaged element type (must match the storage buffer's shader element layout).</typeparam>
public sealed class BufferLease<T> : IDisposable where T : unmanaged
{
	private readonly LeasedBuffer<T> _buffer;

	internal BufferLease(LeasedBuffer<T> buffer, int index)
	{
		_buffer = buffer;
		Index = index;
	}

	/// <summary>This row's current index in the packed buffer — its position in <see cref="LeasedBuffer{T}.AsSpan"/>
	/// and the index a shader reads it by. Stable until another lease is disposed (which may move this row to fill the
	/// gap); <c>-1</c> once this lease is disposed.</summary>
	public int Index { get; private set; }

	/// <summary>The row's value. Setting it records the row for upload on the next flush; getting it returns the last
	/// value set (<c>default</c> until first set). Setting a disposed lease is a no-op.</summary>
	public T Data
	{
		get;
		set
		{
			field = value;
			_buffer.Update(Index, value);
		}
	}

	// The owning buffer calls this when a compaction moves this lease's row into a freed slot.
	internal void SetIndex(int index) => Index = index;

	/// <summary>Release this row. The buffer fills the gap by moving its last row here, giving that row's lease a new
	/// <see cref="Index"/>. Idempotent — disposing twice does nothing.</summary>
	public void Dispose()
	{
		if (Index < 0)
			return;
		var freed = Index;
		Index = -1;
		_buffer.Delete(freed);
	}
}
