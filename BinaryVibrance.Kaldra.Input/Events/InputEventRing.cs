namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// A fixed-capacity FIFO ring of <see cref="InputEvent"/>s, preallocated so the steady per-frame path never
/// allocates. A host adapter enqueues the frame's raw events during the platform pump; the host drains them into
/// a contiguous buffer once per frame and feeds them to <see cref="InputLayerStack.Resolve"/>.
/// </summary>
/// <remarks>
/// Overflow <b>fails loud</b> — <see cref="Enqueue"/> throws rather than silently dropping an event — because a
/// dropped press/release would strand held state. The capacity is generous and adapters coalesce pointer-move
/// floods before enqueuing, so overflow signals a real backlog, not normal traffic. Single-threaded by design:
/// platform events fire on the pump thread and are drained on the same thread, so no locking is needed.
/// </remarks>
public sealed class InputEventRing
{
	private readonly InputEvent[] _buffer;
	private int _head;
	private int _tail;

	/// <summary>Creates a ring holding up to <paramref name="capacity"/> pending events.</summary>
	public InputEventRing(int capacity = 1024)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
		_buffer = new InputEvent[capacity];
	}

	/// <summary>The maximum number of events the ring can hold at once.</summary>
	public int Capacity => _buffer.Length;

	/// <summary>The number of events currently queued.</summary>
	public int Count { get; private set; }

	/// <summary>True when no events are queued.</summary>
	public bool IsEmpty => Count == 0;

	/// <summary>Appends an event. Throws <see cref="InvalidOperationException"/> if the ring is full.</summary>
	public void Enqueue(in InputEvent e)
	{
		if (Count == _buffer.Length)
			throw new InvalidOperationException($"Input event ring overflowed its capacity of {_buffer.Length}; an event backlog would strand held input state.");

		_buffer[_tail] = e;
		_tail = _tail + 1 == _buffer.Length ? 0 : _tail + 1;
		Count++;
	}

	/// <summary>Removes and returns the oldest event, if any.</summary>
	public bool TryDequeue(out InputEvent e)
	{
		if (Count == 0)
		{
			e = default;
			return false;
		}

		e = _buffer[_head];
		_head = _head + 1 == _buffer.Length ? 0 : _head + 1;
		Count--;
		return true;
	}

	/// <summary>
	/// Copies queued events in FIFO order into <paramref name="destination"/> (up to its length), removing the
	/// copied events from the ring, and returns how many were copied.
	/// </summary>
	public int Drain(Span<InputEvent> destination)
	{
		var n = Math.Min(Count, destination.Length);
		for (var i = 0; i < n; i++)
		{
			destination[i] = _buffer[_head];
			_head = _head + 1 == _buffer.Length ? 0 : _head + 1;
		}

		Count -= n;
		return n;
	}

	/// <summary>Discards all queued events.</summary>
	public void Clear()
	{
		_head = 0;
		_tail = 0;
		Count = 0;
	}
}
