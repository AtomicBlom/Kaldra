using System.Runtime.InteropServices;
using System.Text;

namespace Loom.Backend.Vulkan;

/// <summary>
/// The marshaling seam. A transient bump-style allocator over native memory
/// for building a tree of <c>*CreateInfo</c> structs, <c>pNext</c> chains, arrays, and UTF-8
/// strings just long enough to hand to a <c>vkCreate*</c> call. Because <c>vkCreate*</c> copies
/// everything it needs, the whole graph can be freed the instant the call returns — dispose the
/// arena and the struct soup is gone.
///
/// GC-safe by construction: it allocates unmanaged memory, so nothing the GPU/driver sees is a
/// movable managed reference. Build inside a <c>using</c> scope around the create call.
/// </summary>
public sealed unsafe class VulkanNativeArena : IDisposable
{
	private readonly List<nint> _blocks = [];

	/// <summary>Allocates zero-initialized space for <paramref name="count"/> values of <typeparamref name="T"/>.</summary>
	public T* Alloc<T>(int count = 1) where T : unmanaged
	{
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		// Compute the byte count in native width — sizeof(T) * count in 32-bit int would overflow for a large
		// array (e.g. a bindless descriptor array) and under-allocate, corrupting the heap on write.
		var ptr = NativeMemory.AllocZeroed((nuint)sizeof(T) * (nuint)count);
		_blocks.Add((nint)ptr);
		return (T*)ptr;
	}

	/// <summary>Copies a single value into the arena and returns a stable pointer to it.</summary>
	public T* Write<T>(in T value) where T : unmanaged
	{
		var ptr = Alloc<T>(1);
		*ptr = value;
		return ptr;
	}

	/// <summary>Copies a span of values into the arena and returns a stable pointer to the first.</summary>
	public T* Write<T>(ReadOnlySpan<T> values) where T : unmanaged
	{
		if (values.IsEmpty)
			return null;
		var ptr = Alloc<T>(values.Length);
		for (var i = 0; i < values.Length; i++)
			ptr[i] = values[i];
		return ptr;
	}

	/// <summary>Marshals a string as null-terminated UTF-8 and returns a pointer to it.</summary>
	public byte* String(string value)
	{
		var byteCount = Encoding.UTF8.GetByteCount(value);
		var ptr = Alloc<byte>(byteCount + 1);
		Encoding.UTF8.GetBytes(value, new Span<byte>(ptr, byteCount));
		ptr[byteCount] = 0;
		return ptr;
	}

	public void Dispose()
	{
		foreach (var block in _blocks)
			NativeMemory.Free((void*)block);
		_blocks.Clear();
	}
}
