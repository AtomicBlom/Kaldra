namespace Loom.Core.Graph;

/// <summary>
/// A handle that can be <c>.Reads</c>'d as a shader storage buffer (a read-only SSBO / <c>StructuredBuffer&lt;T&gt;</c>)
/// — an app-writable structured array (<see cref="StorageBufferHandle"/>). The marker lets <see cref="PassBuilder.Reads(IStorageBufferHandle)"/>
/// reject category errors at compile time, exactly as <see cref="IUniformHandle"/> does for a uniform block.
/// Distinct from <see cref="IStorageHandle"/>, which marks a <em>read-write</em> compute target (<c>.ReadWrites</c>).
/// </summary>
public interface IStorageBufferHandle;
