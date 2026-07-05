using System.Runtime.Versioning;

namespace Loom.Backend.WebGPU;

/// <summary>
/// A per-frame ring of dynamic data over one physical GPU buffer, reset each frame. A reservation aligns the
/// cursor, stages the bytes into the frame's upload arena, records a queue-level buffer write from the arena into
/// the ring, and returns the ring offset. Two instances serve the backend: a <c>UNIFORM</c> ring feeding every
/// dynamic-offset uniform binding (per-draw and lowered push-constant blocks, offset rides <c>setBindGroup</c>),
/// and a <c>VERTEX</c> ring feeding CPU-driven per-instance streams (offset rides <c>setVertexBuffer</c>).
/// <para>
/// The buffer handle never changes, so bind groups / vertex bindings that reference it stay valid across draws
/// and frames — only the offset varies, keeping the WASM↔JS boundary at one crossing per frame. The buffer is
/// sized generously up front and throws on overflow rather than reallocating mid-record (a realloc would
/// invalidate the bind groups already recorded this frame). Single-buffered like the rest of the backend.
/// </para>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebGpuBufferRing
{
	private const int Capacity = 4 * 1024 * 1024; // 4 MiB per ring — ample for a frame's uniforms or instances

	private readonly WebGpuUploadArena _arena;
	private readonly WebGpuCommandWriter _writer;
	private readonly int _alignment;
	private readonly int _buffer;
	private int _cursor;

	public WebGpuBufferRing(WebGpuHandleAllocator handles, WebGpuUploadArena arena, WebGpuCommandWriter writer, int usage, int minAlignment, string label)
	{
		_arena = arena;
		_writer = writer;
		_alignment = Math.Max(minAlignment, 4);
		_buffer = handles.Allocate(WebGpuHandleAllocator.Buffers);
		WebGpuInterop.CreateBuffer(_buffer, Capacity, usage | WebGpuStateMapping.BufferCopyDst, label);
	}

	/// <summary>The ring's GPU buffer handle — the buffer a dynamic binding is bound to.</summary>
	public int Buffer => _buffer;

	/// <summary>Reset the write cursor at frame start.</summary>
	public void BeginFrame() => _cursor = 0;

	/// <summary>
	/// Stage <paramref name="data"/> into the ring and return its offset. <paramref name="paddedSize"/> reserves at
	/// least that many bytes (a push block is written padded so the shader always reads defined memory even when a
	/// pass pushes fewer values than the block holds); pass <c>data.Length</c> for an exact reservation.
	/// </summary>
	public int Reserve(ReadOnlySpan<byte> data, int paddedSize)
	{
		var size = Math.Max(data.Length, paddedSize);
		var offset = Align(_cursor, _alignment);
		if (offset + size > Capacity)
			throw new WebGpuException($"Buffer ring overflow: {offset + size} bytes needed, {Capacity} available in a frame.");

		if (data.Length > 0)
		{
			var arenaOffset = _arena.Append(data);
			_writer.WriteBufferFromArena(_buffer, offset, arenaOffset, data.Length);
		}
		_cursor = offset + size;
		return offset;
	}

	private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
