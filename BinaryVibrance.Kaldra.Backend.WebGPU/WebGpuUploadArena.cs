namespace BinaryVibrance.Kaldra.Backend.WebGPU;

/// <summary>
/// A per-frame scratch buffer for everything the CPU produces for one frame — parameter/storage uploads,
/// per-draw push-constant slices, instance data, draw-list vertices. Reset at frame start and grown by
/// doubling; each reservation returns a byte offset (referenced by command-stream ops) and a span to fill.
/// The whole used region crosses the interop boundary once, as the second MemoryView of <c>submitFrame</c>.
/// </summary>
internal sealed class WebGpuUploadArena
{
	private byte[] _bytes = new byte[64 * 1024];
	private int _offset;

	/// <summary>Discard the previous frame's contents.</summary>
	public void Reset() => _offset = 0;

	/// <summary>The bytes filled since the last <see cref="Reset"/> — the arena MemoryView for <c>submitFrame</c>.</summary>
	public Span<byte> Used => _bytes.AsSpan(0, _offset);

	/// <summary>Reserve <paramref name="size"/> bytes aligned to <paramref name="alignment"/>; returns the arena
	/// offset and, via <paramref name="span"/>, the region to write into. The span is valid until the next
	/// <see cref="Reset"/> or a growth (a <see cref="Span{T}"/> can't be returned in a tuple — it's a ref struct).</summary>
	public int Reserve(int size, out Span<byte> span, int alignment = 4)
	{
		var start = Align(_offset, alignment);
		EnsureCapacity(start + size);
		_offset = start + size;
		span = _bytes.AsSpan(start, size);
		return start;
	}

	/// <summary>Copy <paramref name="data"/> into a fresh reservation; returns its arena offset.</summary>
	public int Append(ReadOnlySpan<byte> data, int alignment = 4)
	{
		var offset = Reserve(data.Length, out var span, alignment);
		data.CopyTo(span);
		return offset;
	}

	private static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

	private void EnsureCapacity(int required)
	{
		if (required <= _bytes.Length)
			return;
		var grown = new byte[Math.Max(_bytes.Length * 2, required)];
		_bytes.AsSpan(0, _offset).CopyTo(grown);
		_bytes = grown;
	}
}
