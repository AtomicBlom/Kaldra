namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// A fixed-capacity ring of pending <see cref="RumbleCommand"/>s. Rumble rides the reverse direction of input:
/// game code enqueues via <see cref="InputSnapshot.Rumble"/> and a host adapter drains to hardware. Preallocated
/// so enqueuing never allocates.
/// </summary>
/// <remarks>
/// Rumble is fire-and-forget output, not input correctness, so a full queue drops the <b>oldest</b> pending
/// command to make room for the newest rather than throwing — the most recent effect is the one that matters.
/// </remarks>
public sealed class RumbleQueue
{
	private readonly RumbleCommand[] _buffer;
	private int _head;
	private int _tail;

	/// <summary>Creates a queue holding up to <paramref name="capacity"/> pending commands.</summary>
	public RumbleQueue(int capacity = 64)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
		_buffer = new RumbleCommand[capacity];
	}

	/// <summary>The number of commands currently queued.</summary>
	public int Count { get; private set; }

	/// <summary>Appends a command, discarding the oldest if the queue is full.</summary>
	public void Enqueue(in RumbleCommand command)
	{
		if (Count == _buffer.Length)
		{
			// Drop the oldest to keep the newest effect.
			_head = _head + 1 == _buffer.Length ? 0 : _head + 1;
			Count--;
		}

		_buffer[_tail] = command;
		_tail = _tail + 1 == _buffer.Length ? 0 : _tail + 1;
		Count++;
	}

	/// <summary>Removes and returns the oldest command, if any.</summary>
	public bool TryDequeue(out RumbleCommand command)
	{
		if (Count == 0)
		{
			command = default;
			return false;
		}

		command = _buffer[_head];
		_head = _head + 1 == _buffer.Length ? 0 : _head + 1;
		Count--;
		return true;
	}

	/// <summary>
	/// Copies queued commands in FIFO order into <paramref name="destination"/> (up to its length), removing the
	/// copied commands, and returns how many were copied.
	/// </summary>
	public int Drain(Span<RumbleCommand> destination)
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

	/// <summary>Discards all queued commands.</summary>
	public void Clear()
	{
		_head = 0;
		_tail = 0;
		Count = 0;
	}
}
